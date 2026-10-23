using WeredaConnect.Server.Data;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// FUP-61 (fraud-detection-architecture.md 18.7, escrow-protocol.md 1.31.0): the escrow status a party's
/// app is shown. Apps before build <see cref="FirstBuildParsingAllStatuses"/> know neither <c>held</c> nor
/// <c>dispute_resolved</c>: mobile throws on them and web shows them as <c>initiated</c>. Those apps get the
/// nearest status they parse. Only the response changes: the stored row, the history filter, the audit
/// and every server decision keep the stored value.
/// </summary>
public static class EscrowStatusCompat
{
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
}
