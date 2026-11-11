namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-130: a party's name as it may be copied onto another row (an order's farmerName, a
/// delivery's party names). An id is never a name: older clients sent the user id when they had
/// no name, and some stored rows still hold one.
/// </summary>
public static class PartyName
{
    /// <summary>
    /// <paramref name="name"/> trimmed, or the empty string when it is blank, is
    /// <paramref name="partyId"/> itself, or has the shape of an id (a UUID, as every user id is).
    /// Clients show their own localized label for an empty name.
    /// </summary>
    public static string OrEmpty(string? name, string? partyId)
    {
        var trimmed = name?.Trim();
        if (string.IsNullOrEmpty(trimmed)
            || string.Equals(trimmed, partyId?.Trim(), StringComparison.Ordinal)
            || Guid.TryParse(trimmed, out _))
        {
            return string.Empty;
        }

        return trimmed;
    }
}
