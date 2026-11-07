using System.Globalization;
using Amazon.SimpleSystemsManagement;
using Amazon.SimpleSystemsManagement.Model;

namespace WeredaConnect.Server.Helpers;

/// <summary>
/// ESC-16 (spec 4.5, F9): the order deadline timings. Each default is the value the code used before;
/// each can be overridden by an SSM String parameter (a number of hours) under the path in
/// <see cref="PathEnv"/> (<c>/{prefix}/orders/</c>). The values are read once per container, on first
/// use, in one <c>GetParameters</c> call, so a changed parameter takes effect as containers recycle.
/// A parameter that is missing, unreadable or outside 1 to 720 hours keeps its default and logs
/// <c>ORDER_TIMINGS_DEFAULTED</c>; an SSM failure keeps every default. T2 never goes below the close of
/// the last order checkout (<see cref="PaymentVerificationFloor"/>, 22h10m): a lower value is raised to
/// it and logs <c>ORDER_TIMINGS_CLAMPED</c>. Without the env var (tests, and Lambdas that compute no
/// deadline) nothing is read and the defaults apply. The read is bounded (2 seconds, one retry), so a
/// degraded SSM costs a cold request at most a few seconds, never the Lambda timeout.
/// </summary>
public sealed record OrderTimings
{
    /// <summary>Env var naming the SSM path of the parameters, with its trailing slash.</summary>
    public const string PathEnv = "ORDER_TIMINGS_PARAM_PATH";

    /// <summary>The token of a value that kept its default, so a wrong parameter is found in the logs.</summary>
    public const string DefaultedToken = "ORDER_TIMINGS_DEFAULTED";

    /// <summary>The token of a value raised to its floor (<see cref="PaymentVerificationFloor"/>).</summary>
    public const string ClampedToken = "ORDER_TIMINGS_CLAMPED";

    /// <summary>
    /// T2's floor: an order checkout opens new attempts until 20 hours after <c>paymentSubmittedAt</c> and
    /// the last one stays payable 2 hours and 10 minutes more (<c>EscrowService.LastCheckoutClosesAfter</c>).
    /// The payment-failed step must not fail an order whose checkout can still take the buyer's money.
    /// </summary>
    public static TimeSpan PaymentVerificationFloor => Services.EscrowService.LastCheckoutClosesAfter;

    /// <summary>Parameter names under the path (hours each).</summary>
    public static class Names
    {
        /// <summary>T1: an order not paid this long after it was created (or entered <c>pending_payment</c>) is cancelled.</summary>
        public const string UnpaidExpiry = "unpaid-expiry-hours";

        /// <summary>T2: a submitted payment not verified this long after <c>paymentSubmittedAt</c> fails (never below <see cref="PaymentVerificationFloor"/>).</summary>
        public const string PaymentVerification = "payment-verification-hours";

        /// <summary>T3 and T4: the farmer's and the trucker's window to answer; also the order-create lead before departure.</summary>
        public const string ResponseWindow = "response-window-hours";

        /// <summary>T3 and T4 end at least this long before the trip departs.</summary>
        public const string DepartureMargin = "departure-margin-hours";

        /// <summary>T5: the buyer's window to decide once the transport failed.</summary>
        public const string BuyerChoiceWindow = "buyer-choice-window-hours";

        /// <summary>The reminder of T3, T4 and T5 is due this long before the deadline.</summary>
        public const string ReminderLead = "reminder-lead-hours";

        /// <summary>T7, trip order: the pickup is overdue this long after the trip departs.</summary>
        public const string PickupGrace = "pickup-grace-hours";

        /// <summary>T7, self-pickup order: the pickup is overdue this long after the farmer accepted.</summary>
        public const string SelfPickupWindow = "self-pickup-window-hours";

        public static readonly string[] All =
        {
            UnpaidExpiry, PaymentVerification, ResponseWindow, DepartureMargin,
            BuyerChoiceWindow, ReminderLead, PickupGrace, SelfPickupWindow
        };
    }

    private const double MinHours = 1;
    private const double MaxHours = 720;

    public TimeSpan UnpaidExpiry { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan PaymentVerification { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan ResponseWindow { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan DepartureMargin { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan BuyerChoiceWindow { get; init; } = TimeSpan.FromHours(48);
    public TimeSpan ReminderLead { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan PickupGrace { get; init; } = TimeSpan.FromHours(48);
    public TimeSpan SelfPickupWindow { get; init; } = TimeSpan.FromDays(7);

    /// <summary>The values the code used before ESC-16.</summary>
    public static OrderTimings Defaults { get; } = new();

    private static readonly Lazy<OrderTimings> LazyCurrent =
        new(LoadFromEnvironment, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>This container's timings: read from SSM on first use, then cached.</summary>
    public static OrderTimings Current => LazyCurrent.Value;

    /// <summary>A whole number of hours as text, for messages ("24").</summary>
    public static string HoursText(TimeSpan span)
        => span.TotalHours.ToString("0.##", CultureInfo.InvariantCulture);

    private static OrderTimings LoadFromEnvironment()
    {
        var path = Environment.GetEnvironmentVariable(PathEnv);
        return string.IsNullOrWhiteSpace(path)
            ? Defaults
            : Load(path, FetchFromSsm, Console.WriteLine);
    }

    /// <summary>
    /// Read the parameters under <paramref name="path"/> through <paramref name="fetch"/> (full names in,
    /// values by full name out) and apply them over the defaults. Never throws.
    /// </summary>
    internal static OrderTimings Load(
        string path, Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string>> fetch, Action<string> log)
    {
        var prefix = path.EndsWith('/') ? path : path + "/";
        IReadOnlyDictionary<string, string> values;
        try
        {
            values = fetch(Names.All.Select(name => prefix + name).ToList());
        }
        catch (Exception ex)
        {
            log($"{DefaultedToken} path={prefix} reason=[{ex.GetType().Name}]: every order timing keeps its default.");
            return Defaults;
        }

        var timings = new OrderTimings
        {
            UnpaidExpiry = Read(Names.UnpaidExpiry, Defaults.UnpaidExpiry),
            PaymentVerification = AtLeast(
                Names.PaymentVerification, Read(Names.PaymentVerification, Defaults.PaymentVerification), PaymentVerificationFloor),
            ResponseWindow = Read(Names.ResponseWindow, Defaults.ResponseWindow),
            DepartureMargin = Read(Names.DepartureMargin, Defaults.DepartureMargin),
            BuyerChoiceWindow = Read(Names.BuyerChoiceWindow, Defaults.BuyerChoiceWindow),
            ReminderLead = Read(Names.ReminderLead, Defaults.ReminderLead),
            PickupGrace = Read(Names.PickupGrace, Defaults.PickupGrace),
            SelfPickupWindow = Read(Names.SelfPickupWindow, Defaults.SelfPickupWindow)
        };
        log($"Order timings from {prefix}: {timings.Describe()}");
        return timings;

        TimeSpan Read(string name, TimeSpan fallback)
        {
            if (!values.TryGetValue(prefix + name, out var raw))
            {
                log($"{DefaultedToken} name={prefix}{name} reason=missing default={HoursText(fallback)}h");
                return fallback;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)
                && hours is >= MinHours and <= MaxHours)
            {
                return TimeSpan.FromHours(hours);
            }

            log($"{DefaultedToken} name={prefix}{name} reason=invalid default={HoursText(fallback)}h");
            return fallback;
        }

        TimeSpan AtLeast(string name, TimeSpan value, TimeSpan floor)
        {
            if (value >= floor)
            {
                return value;
            }

            log($"{ClampedToken} name={prefix}{name} value={HoursText(value)}h floor={HoursText(floor)}h");
            return floor;
        }
    }

    /// <summary>The values in hours, one line, for the load log.</summary>
    public string Describe() => string.Join(", ",
        $"{Names.UnpaidExpiry}={HoursText(UnpaidExpiry)}",
        $"{Names.PaymentVerification}={HoursText(PaymentVerification)}",
        $"{Names.ResponseWindow}={HoursText(ResponseWindow)}",
        $"{Names.DepartureMargin}={HoursText(DepartureMargin)}",
        $"{Names.BuyerChoiceWindow}={HoursText(BuyerChoiceWindow)}",
        $"{Names.ReminderLead}={HoursText(ReminderLead)}",
        $"{Names.PickupGrace}={HoursText(PickupGrace)}",
        $"{Names.SelfPickupWindow}={HoursText(SelfPickupWindow)}");

    private static IReadOnlyDictionary<string, string> FetchFromSsm(IReadOnlyList<string> names)
    {
        // GetParameters takes at most 10 names; Names.All has 8. String parameters: no decryption.
        // Bounded: 2 seconds a try, one retry, so the first order write of a container waits at most a
        // few seconds for a degraded SSM (the API Lambdas time out at 10) and then keeps the defaults.
        using var client = new AmazonSimpleSystemsManagementClient(
            new AmazonSimpleSystemsManagementConfig { Timeout = TimeSpan.FromSeconds(2), MaxErrorRetry = 1 });
        var response = client.GetParametersAsync(new GetParametersRequest { Names = names.ToList() })
            .GetAwaiter().GetResult();
        return (response.Parameters ?? new List<Parameter>())
            .Where(p => p.Name != null && p.Value != null)
            .ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
    }
}
