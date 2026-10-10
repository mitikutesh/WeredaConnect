using System.Diagnostics;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// ESC-09: the time budget of one cron run, anchored when the handler starts. A sweep takes a
/// <see cref="Slice"/> of it, with its own share of the run and its own Lambda reserve, and asks
/// <see cref="CronRunSlice.MayStartRow"/> before each new row, so it stops at the end of its slice or
/// when the Lambda is about to run out, whichever comes first. Shared by the escrow-cron sweeps: the
/// order-money sweep runs last and keeps 30 seconds; a sweep that runs before the trucker retry keeps
/// that retry's 90 second lock reserve too (ESC-10: 120 seconds).
/// <list type="bullet">
/// <item>The elapsed clock is injectable: tests drive it by hand instead of sleeping.</item>
/// <item>No new row once <c>0 &lt; RemainingTime &lt; reserve</c>. <c>TestLambdaContext</c> reports
/// zero, which turns this guard off, so only the elapsed clock bounds a test run.</item>
/// </list>
/// </summary>
public sealed class CronRunBudget
{
    /// <summary>The reserve of a sweep that runs last: time for the summary and the logs.</summary>
    public static readonly TimeSpan DefaultReserve = TimeSpan.FromSeconds(30);

    private readonly Func<TimeSpan> _elapsed;

    public CronRunBudget(Func<TimeSpan> elapsed)
    {
        _elapsed = elapsed ?? throw new ArgumentNullException(nameof(elapsed));
    }

    /// <summary>A budget whose clock starts now (call it at the start of the handler).</summary>
    public static CronRunBudget StartNew()
    {
        var stopwatch = Stopwatch.StartNew();
        return new CronRunBudget(() => stopwatch.Elapsed);
    }

    /// <summary>Time since the run started.</summary>
    public TimeSpan Elapsed => _elapsed();

    /// <summary>True when the Lambda reports less than <paramref name="reserve"/> left (zero means unknown: false).</summary>
    public static bool IsLambdaTimeLow(ILambdaContext? context, TimeSpan reserve)
    {
        var remaining = context?.RemainingTime ?? TimeSpan.Zero;
        return remaining > TimeSpan.Zero && remaining < reserve;
    }

    /// <summary>
    /// A slice of <paramref name="length"/> starting now. It also stops once the Lambda has less than
    /// <paramref name="reserve"/> left (<see cref="DefaultReserve"/> when not given).
    /// </summary>
    public CronRunSlice Slice(TimeSpan length, TimeSpan? reserve = null)
        => new(this, Elapsed, length, reserve ?? DefaultReserve);
}

/// <summary>One sweep's share of a <see cref="CronRunBudget"/>.</summary>
public sealed class CronRunSlice
{
    private readonly CronRunBudget _budget;
    private readonly TimeSpan _start;
    private readonly TimeSpan _length;

    internal CronRunSlice(CronRunBudget budget, TimeSpan start, TimeSpan length, TimeSpan reserve)
    {
        _budget = budget;
        _start = start;
        _length = length;
        Reserve = reserve;
    }

    /// <summary>The Lambda time this sweep leaves for what runs after it.</summary>
    public TimeSpan Reserve { get; }

    /// <summary>Why the last <see cref="MayStartRow"/> said no: <c>time</c> (slice used up) or <c>lambda</c>.</summary>
    public string? StopReason { get; private set; }

    /// <summary>Whether the sweep may start one more row.</summary>
    public bool MayStartRow(ILambdaContext? context)
    {
        if (CronRunBudget.IsLambdaTimeLow(context, Reserve))
        {
            StopReason = "lambda";
            return false;
        }

        if (_budget.Elapsed - _start >= _length)
        {
            StopReason = "time";
            return false;
        }

        return true;
    }
}
