namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-75, FUP-241 (sync-protocol.md 1.53.0): <c>REPUTATION_RESTRICTED</c>, <c>TRIP_HAS_BOOKINGS</c>
/// and <c>CROP_HAS_ACTIVE_ORDERS</c> are known to clients only since FUP-01 (#155, sync-protocol.md
/// 1.17.0). An app that sends no readable <c>X-App-Build</c> (build 1 or older,
/// <see cref="ClientAppBuild.Read"/>) may predate that client and would resend an unknown code on every
/// reconnect, so it gets the same sentence under a code every client treats as permanent:
/// <c>ORDER_VALIDATION</c> on an order, <c>SYNC_VALIDATION</c> on anything else (<c>TRIP_VALIDATION</c>
/// and <c>CROP_VALIDATION</c> stay retryable on every client).
/// </summary>
public static class OldClientRefusal
{
    /// <summary>
    /// <paramref name="message"/> as the caller of build <paramref name="clientBuild"/> (null: no
    /// readable <c>X-App-Build</c>) gets it. A message under <paramref name="code"/> to such a caller
    /// keeps only its sentence, what follows the last <c>": "</c>: the code and any reason tokens before
    /// the sentence (<c>until=...</c>, <c>counterparty</c>) are dropped, so a sentence sent under one of
    /// these codes must not contain <c>": "</c>. Every other message, and every message to a caller that
    /// sends the header, is returned unchanged.
    /// </summary>
    public static string ForClient(string message, int? clientBuild, string code, string entityType)
    {
        if (clientBuild != null || !message.StartsWith(code + ":", StringComparison.Ordinal))
        {
            return message;
        }

        var sentence = message[(message.LastIndexOf(": ", StringComparison.Ordinal) + 2)..];
        return (entityType == "order" ? "ORDER_VALIDATION: " : "SYNC_VALIDATION: ") + sentence;
    }
}
