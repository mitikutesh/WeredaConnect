using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// FUP-140 (delivery-verification-api.md 2.14.0): the by-hand action <c>backfill_delivery_photo_copies</c>, run once per
/// environment after the deploy that ships the kept copies and before the <c>uploads/photos/</c> expiry rule goes on
/// there (<c>.ai/deployment.md</c> 3e).
/// </summary>
public partial class OrderCronFunction
{
    /// <summary>FUP-140: each run's share of the 5 minute Lambda, as the reputation actions (the rest for the summary).</summary>
    internal static readonly TimeSpan PhotoCopyBackfillRunTime = TimeSpan.FromMinutes(4);

    internal const string PhotoCopyBackfillUnconfiguredToken = "DELIVERY_PHOTO_BACKFILL_UNCONFIGURED";

    /// <summary>FUP-140: wired when <c>DELIVERIES_TABLE</c> and <c>UPLOADS_BUCKET</c> are set; tests set it.</summary>
    internal DeliveryPhotoCopyBackfill? PhotoCopyBackfill { get; init; }

    /// <summary>
    /// FUP-140: one run of the photo copy backfill. Not flagged; a rerun copies nothing twice (a row with its copy
    /// recorded is filtered out). <paramref name="scanFrom"/> resumes the Scan where a stopped run left it.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandlePhotoCopyBackfillAsync(string? scanFrom, ILambdaContext context)
    {
        if (scanFrom != null && DeliveryPhotoCopyBackfill.KeyOf(scanFrom) == null)
        {
            return LambdaResponse.Error(400, "ORDER_CRON_VALIDATION",
                $"scanFrom must be the scanCursor of an earlier {DeliveryPhotoCopyBackfill.Action} run.");
        }

        if (PhotoCopyBackfill == null)
        {
            context.Logger.LogError($"{PhotoCopyBackfillUnconfiguredToken}: DELIVERIES_TABLE or UPLOADS_BUCKET is not set; nothing was done.");
            return JsonResponse(new Dictionary<string, string>
            {
                ["action"] = DeliveryPhotoCopyBackfill.Action,
                ["outcome"] = "unconfigured"
            });
        }

        return JsonResponse(await PhotoCopyBackfill.RunAsync(
            scanFrom, CronRunBudget.StartNew().Slice(PhotoCopyBackfillRunTime), context));
    }
}
