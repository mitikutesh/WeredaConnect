using Amazon.DynamoDBv2.Model;
using NSubstitute;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-10: rows a stubbed paged sweep served. An unstubbed <c>QueryCronPageAsync</c> returns no rows, so
/// every <c>DidNotReceive</c> after it would pass with nothing read; a negative test asserts
/// <see cref="AssertServed"/> as its positive control.
/// </summary>
internal sealed class SweepStub
{
    public int Served { get; set; }

    public void AssertServed() => Assert.True(Served > 0, "The sweep never read the stubbed rows.");
}

/// <summary>
/// ESC-10: stubs for the paged sweeps of <c>EscrowCronFunction</c> (<see cref="IEscrowRepository.QueryCronPageAsync"/>).
/// Each matches the sweep's query shape, so a sweep that asks with other filters reads nothing.
/// </summary>
internal static class CronSweepStubs
{
    public static (List<Dictionary<string, object>> Items, Dictionary<string, AttributeValue>? LastEvaluatedKey) Page(
        IEnumerable<Dictionary<string, object>> rows, Dictionary<string, AttributeValue>? lastKey = null)
        => (rows.ToList(), lastKey);

    /// <summary>Every paged sweep reads an empty partition (tests override the ones they exercise).</summary>
    public static void StubEmptySweeps(this IEscrowRepository repo)
        => repo.QueryCronPageAsync(Arg.Any<CronSweepQuery>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(Page([]));

    /// <summary>One page of rows for every sweep of <paramref name="status"/> (the release and the refund sweep both read <c>escrowed</c>).</summary>
    public static SweepStub StubSweep(this IEscrowRepository repo, string status, params Dictionary<string, object>[] rows)
        => repo.Stub(q => q.Status == status, rows);

    /// <summary>One page of rows for the refund sweep only: <c>escrowed</c>, expired, unconfirmed, unlocked.</summary>
    public static SweepStub StubRefundSweep(this IEscrowRepository repo, params Dictionary<string, object>[] rows)
        => repo.Stub(q => q.Status == "escrowed" && q.ExpiresAtBefore != null && q.DeliveryConfirmed == false && q.ExcludeLocked, rows);

    /// <summary>One page of rows for the release sweep only: <c>escrowed</c>, confirmed, unlocked, no expiry bound.</summary>
    public static SweepStub StubReleaseSweep(this IEscrowRepository repo, params Dictionary<string, object>[] rows)
        => repo.Stub(q => q.Status == "escrowed" && q.ExpiresAtBefore == null && q.DeliveryConfirmed == true && q.ExcludeLocked, rows);

    private static SweepStub Stub(this IEscrowRepository repo, Func<CronSweepQuery, bool> matches, Dictionary<string, object>[] rows)
    {
        var stub = new SweepStub();
        repo.QueryCronPageAsync(Arg.Is<CronSweepQuery>(q => matches(q)), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns(_ =>
            {
                stub.Served += rows.Length;
                return Page(rows);
            });
        return stub;
    }
}
