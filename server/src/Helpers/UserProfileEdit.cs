using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Validation;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// SIM-56 (sync-protocol.md 1.57.0, user-entity.md 2.2.0): the profile text a user changes through
/// the <c>/sync</c> <c>user</c> entity: <c>displayName</c>, <c>woreda</c> and a company buyer's
/// <c>companyName</c>. Only these keys are read from the payload, so role, phone, Fayda identity,
/// verification, reputation, suspension and the deletion attributes can never change here.
/// Values are trimmed. A value equal to the stored one is an unchanged echo: it is neither checked
/// nor written (stored values are not re-validated, user-entity.md 1.2.0). The rules are the
/// registration's (auth-api.md 2.12.0), checked on the fields that change.
/// </summary>
internal static class UserProfileEdit
{
    public const string DisplayName = "displayName";
    public const string Woreda = "woreda";
    public const string CompanyName = "companyName";

    /// <summary>The editable text fields, in the order a refusal names them (the registration's).</summary>
    public static readonly string[] TextFields = [DisplayName, Woreda, CompanyName];

    /// <summary>The registration's limit for each of the three fields.</summary>
    public const int MaxLength = 100;

    /// <summary>
    /// The text fields <paramref name="data"/> sends, trimmed. A missing key or a JSON null is not
    /// sent; a value that is not a string adds an error to <paramref name="errors"/>.
    /// </summary>
    public static Dictionary<string, string> ReadText(Dictionary<string, object> data, List<string> errors)
    {
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in TextFields)
        {
            if (!data.TryGetValue(field, out var value))
            {
                continue;
            }

            switch (value)
            {
                case null:
                case JsonElement { ValueKind: JsonValueKind.Null }:
                    break;
                case string s:
                    sent[field] = s.Trim();
                    break;
                case JsonElement { ValueKind: JsonValueKind.String } je:
                    sent[field] = (je.GetString() ?? "").Trim();
                    break;
                default:
                    errors.Add($"{field} must be a string.");
                    break;
            }
        }

        return sent;
    }

    /// <summary>The stored value of <paramref name="field"/> on <paramref name="user"/>.</summary>
    public static string? StoredValue(UserDto user, string field) => field switch
    {
        DisplayName => user.DisplayName,
        Woreda => user.Woreda,
        CompanyName => user.CompanyName,
        _ => null
    };

    /// <summary>
    /// The sent fields whose value differs from the stored one, both trimmed and compared ordinal.
    /// </summary>
    public static Dictionary<string, string> Changed(Dictionary<string, string> sent, UserDto stored)
    {
        var changed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in TextFields)
        {
            if (sent.TryGetValue(field, out var value)
                && !string.Equals(value, StoredValue(stored, field)?.Trim(), StringComparison.Ordinal))
            {
                changed[field] = value;
            }
        }

        return changed;
    }

    /// <summary>A buyer registered as a company (F-32): the only account with a company name.</summary>
    public static bool IsCompanyBuyer(UserDto user)
        => string.Equals(user.Role, "buyer", StringComparison.OrdinalIgnoreCase)
           && string.Equals(user.BuyerType, "company", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The field rules for the changed fields, in <see cref="TextFields"/> order: none may be empty
    /// (a listing, trip or order takes the name from the profile, and the woreda is an index key),
    /// none longer than <see cref="MaxLength"/>, the name not the deleted-account name, and the
    /// company name only on a company buyer. The phone screen runs after these.
    /// </summary>
    public static List<string> Validate(Dictionary<string, string> changed, UserDto stored)
    {
        var errors = new List<string>();
        foreach (var field in TextFields)
        {
            if (!changed.TryGetValue(field, out var value))
            {
                continue;
            }

            if (field == CompanyName && !IsCompanyBuyer(stored))
            {
                errors.Add("companyName can only be changed by a company buyer.");
                continue;
            }

            if (value.Length == 0)
            {
                errors.Add($"{field} must not be empty.");
            }
            else if (value.Length > MaxLength)
            {
                errors.Add($"{field} must not exceed {MaxLength} characters.");
            }
            else if (field == DisplayName && AccountDeletionPolicy.IsSentinelName(value))
            {
                errors.Add("This display name is not available.");
            }
        }

        return errors;
    }

    /// <summary>
    /// FUP-52: the changed fields that hold an Ethiopian mobile number (the listing text detector),
    /// in <see cref="TextFields"/> order.
    /// </summary>
    public static List<string> FieldsWithPhoneNumber(Dictionary<string, string> changed)
        => TextFields.Where(f => changed.TryGetValue(f, out var v) && ContactInText.ContainsPhoneNumber(v)).ToList();

    /// <summary>The stored row with the changed fields applied.</summary>
    public static UserDto Apply(UserDto stored, Dictionary<string, string> changed) => stored with
    {
        DisplayName = changed.TryGetValue(DisplayName, out var name) ? name : stored.DisplayName,
        Woreda = changed.TryGetValue(Woreda, out var woreda) ? woreda : stored.Woreda,
        CompanyName = changed.TryGetValue(CompanyName, out var company) ? company : stored.CompanyName
    };
}
