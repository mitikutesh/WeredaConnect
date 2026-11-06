using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// LT-12 (live-trip.md section 10, order-lifecycle.md 2.5.0): the buyer's drop-off landmark on an
/// order. Set at create and changed by the buyer until pickup; a phone number in it is refused like
/// one in listing text. Who reads it is <see cref="OrderDropoffNote.VisibleTo"/>.
/// </summary>
public partial class SyncFunction
{
    /// <summary>
    /// On create, after the field rules: the note is stored trimmed, and left off when empty. One
    /// that holds a phone number is refused with <c>ORDER_CONTACT_IN_TEXT</c> and nothing is
    /// written. Null when the create may go on.
    /// </summary>
    private static SyncOperationResult? ApplyCreateDropoffNote(SyncOperationRequest operation)
    {
        var data = operation.Data!;
        if (!data.TryGetValue(OrderDropoffNote.Field, out var raw))
        {
            return null;
        }

        var note = OrderDropoffNote.Normalize(raw);
        if (note.Length == 0)
        {
            data.Remove(OrderDropoffNote.Field);
            return null;
        }

        if (ContactInText.ContainsPhoneNumber(note))
        {
            return DropoffNoteContactRefusal(operation);
        }

        data[OrderDropoffNote.Field] = note;
        return null;
    }

    /// <summary>
    /// On update, before the other order rules. In order: a party who may not read the note
    /// (<see cref="OrderDropoffNote.VisibleTo"/>, a trucker not committed) is refused with
    /// <c>ORDER_UNAUTHORIZED</c> whatever it sent, before the stored note is looked at, so the
    /// answer never tells whether a note exists or what it says; the field rules
    /// (<c>ORDER_VALIDATION</c>); an unchanged echo of the stored note from a party who reads it is
    /// dropped; a change from anyone but the buyer is <c>ORDER_UNAUTHORIZED</c>; a phone number is
    /// <c>ORDER_CONTACT_IN_TEXT</c>; after pickup <c>ORDER_DROPOFF_NOTE_LOCKED</c>. Both
    /// <c>ORDER_UNAUTHORIZED</c> answers are the same text with the role signal. Every refusal
    /// writes nothing. An empty note clears it. Null when the update may go on, with the trimmed
    /// note in the payload.
    /// </summary>
    private async Task<SyncOperationResult?> CheckDropoffNoteChangeAsync(
        SyncOperationRequest operation,
        Dictionary<string, object> order,
        string? callerId,
        string? orderBuyerId,
        ILambdaContext context)
    {
        var data = operation.Data!;
        var isBuyer = !string.IsNullOrEmpty(callerId) && callerId == orderBuyerId;

        // No equality oracle: a party who may not read the note learns nothing from the answer.
        if (!isBuyer && !OrderDropoffNote.VisibleTo(order, callerId))
        {
            return await DropoffNoteNotBuyerAsync(operation, callerId, context);
        }

        var raw = data[OrderDropoffNote.Field];
        if (OrderDropoffNote.ValidationError(raw) is { } error)
        {
            return OrderValidationError(operation, error);
        }

        var note = OrderDropoffNote.Normalize(raw);
        if (string.Equals(note, OrderDropoffNote.Of(order) ?? string.Empty, StringComparison.Ordinal))
        {
            data.Remove(OrderDropoffNote.Field);
            return null;
        }

        if (!isBuyer)
        {
            return await DropoffNoteNotBuyerAsync(operation, callerId, context);
        }

        if (ContactInText.ContainsPhoneNumber(note))
        {
            return DropoffNoteContactRefusal(operation);
        }

        var windowRefusal = await CheckDropoffNoteWindowAsync(operation, order, context);
        if (windowRefusal != null)
        {
            return windowRefusal;
        }

        data[OrderDropoffNote.Field] = note;
        return null;
    }

    /// <summary>
    /// The edit window: the order is in <see cref="OrderDropoffNote.EditableOrderStatuses"/> and its
    /// active delivery, read strongly consistent, is not picked up (the order follows its delivery
    /// on a best-effort write, so the delivery decides when they disagree). A delivery that cannot
    /// be read refuses for now with an unprefixed, retryable error.
    /// </summary>
    private async Task<SyncOperationResult?> CheckDropoffNoteWindowAsync(
        SyncOperationRequest operation, Dictionary<string, object> order, ILambdaContext context)
    {
        if (!OrderDropoffNote.EditableOrderStatuses.Contains(ItemString(order, "status")))
        {
            return DropoffNoteLocked(operation);
        }

        var activeDeliveryId = ItemString(order, "activeDeliveryId");
        if (activeDeliveryId.Length == 0)
        {
            return null;
        }

        Dictionary<string, AttributeValue>? delivery;
        try
        {
            delivery = await GetDeliveryItemAsync(activeDeliveryId, consistentRead: true);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning(
                $"Drop-off note: delivery {MoneyAlarmLog.LogId(activeDeliveryId)} could not be read: [{ex.GetType().Name}]");
            return TransientOrderError(operation, "The order's delivery could not be read. Please try again later.");
        }

        var deliveryStatus = delivery != null && delivery.TryGetValue("status", out var status) ? status.S : null;
        return deliveryStatus != null && OrderDropoffNote.PickedUpDeliveryStatuses.Contains(deliveryStatus)
            ? DropoffNoteLocked(operation)
            : null;
    }

    /// <summary>A note change from anyone but the buyer: refused, with the role signal.</summary>
    private Task<SyncOperationResult> DropoffNoteNotBuyerAsync(
        SyncOperationRequest operation, string? callerId, ILambdaContext context)
        => RefuseWithSignalAsync(new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = "ORDER_UNAUTHORIZED: Only the buyer can change the drop-off note."
        }, FraudDetectionService.RoleForbiddenSignal, operation, "order", callerId, context);

    private static SyncOperationResult DropoffNoteContactRefusal(SyncOperationRequest operation) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = ContactInTextRefusal(OrderDropoffNote.ContactInTextCode, new List<string> { OrderDropoffNote.Field })
    };

    private static SyncOperationResult DropoffNoteLocked(SyncOperationRequest operation) => new()
    {
        OperationId = operation.OperationId,
        Status = "error",
        Message = OrderDropoffNote.LockedMessage
    };
}
