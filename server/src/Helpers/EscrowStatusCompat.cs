using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-61 (fraud-detection-architecture.md 18.7, escrow-protocol.md 1.31.0): the escrow status a party's
/// app is shown. Apps before build <see cref="FirstBuildParsingAllStatuses"/> know neither <c>held</c> nor
/// <c>dispute_resolved</c>: mobile throws on them and web shows them as <c>initiated</c>. Those apps get the
/// nearest status they parse. Only the response changes: the stored row, the audit and every server
/// decision keep the stored value. Since FUP-108 (escrow-protocol.md 2.32.0) an old app's history filter
/// also lists the rows it is shown under the status it filters by, and the account deletion blockers
/// name the status it is shown.
/// </summary>
public static class EscrowStatusCompat
{
    /// <summary>The stored statuses <see cref="ForOldApp"/> shows as another one.</summary>
    private static readonly string[] MappedForOldApp = [EscrowHoldFields.Status, "dispute_resolved"];

    /// <summary>The first build that sends <see cref="ClientAppBuild.Header"/> and parses every escrow status.</summary>
    public const int FirstBuildParsingAllStatuses = 2;

    /// <summary>True when the caller's app parses every status; a request without the header is an old app.</summary>
    public static bool ShowsStoredStatus(APIGatewayProxyRequest request)
        => ClientAppBuild.Read(request) is { } build && build >= FirstBuildParsingAllStatuses;

    /// <summary><c>held</c> reads <c>escrowed</c> (the money is still in escrow), <c>dispute_resolved</c> reads
    /// <c>disputed</c> (the verdict waits for its settlement); any other value is returned as stored.</summary>
    public static string ForOldApp(string status) => status switch
    {
        EscrowHoldFields.Status => "escrowed",
        "dispute_resolved" => "disputed",
        _ => status
    };

    public static EscrowDto ForCaller(EscrowDto escrow, APIGatewayProxyRequest request)
        => ShowsStoredStatus(request) ? escrow : escrow with { Status = ForOldApp(escrow.Status) };

    public static GetEscrowHistoryResponse ForCaller(GetEscrowHistoryResponse history, APIGatewayProxyRequest request)
        => ShowsStoredStatus(request)
            ? history
            : history with { Escrows = history.Escrows.Select(e => e with { Status = ForOldApp(e.Status) }).ToList() };

    public static InitiateEscrowResponse ForCaller(InitiateEscrowResponse response, APIGatewayProxyRequest request)
        => ShowsStoredStatus(request) ? response : response with { Status = ForOldApp(response.Status) };

    public static DisputeEscrowResponse ForCaller(DisputeEscrowResponse response, APIGatewayProxyRequest request)
        => ShowsStoredStatus(request) ? response : response with { Status = ForOldApp(response.Status) };

    /// <summary>
    /// FUP-108: the stored statuses a <c>GET /escrow/history?status=</c> filter matches for the caller. An old
    /// app filters by the status it is shown, so its filter also matches each stored status shown as that one
    /// (<c>escrowed</c> lists held rows, <c>disputed</c> lists <c>dispute_resolved</c> rows), as the unfiltered
    /// list shows them. Null when the filter is the stored value alone: no filter, build 2 or later, or a
    /// status no other one is shown as.
    /// </summary>
    public static IReadOnlyList<string>? HistoryFilterStatuses(string? status, APIGatewayProxyRequest request)
    {
        if (string.IsNullOrEmpty(status) || ShowsStoredStatus(request))
        {
            return null;
        }

        var shownAs = MappedForOldApp.Where(stored => ForOldApp(stored) == status).ToList();
        return shownAs.Count == 0 ? null : [status, .. shownAs];
    }

    /// <summary>
    /// FUP-108: the blockers of the account deletion <c>409 ACCOUNT_002</c>. An escrow blocker names its
    /// escrow's status, which an old app gets as <see cref="ForOldApp"/> shows it; the other blockers name an
    /// order, delivery or payment status and are returned as they are.
    /// </summary>
    public static List<AccountDeletionBlocker> ForCaller(IEnumerable<AccountDeletionBlocker> blockers, APIGatewayProxyRequest request)
        => ShowsStoredStatus(request)
            ? blockers.ToList()
            : blockers.Select(b => b.Type == AccountDeletionPolicy.BlockerEscrow ? b with { Status = ForOldApp(b.Status) } : b).ToList();
}
