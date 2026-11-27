using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Services;
using Xunit;
using P = WeredaConnect.Server.Services.NotificationParams;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>
/// ESC-28: the reputation-events stream as the fourth notification source (reputation-architecture.md
/// section 9): a penalty with points tells its party <c>penalty_recorded</c>, a tier row
/// <c>tier_changed</c>, nothing else speaks, nobody but the party is told; the push reads in all
/// three languages; and the order-cron's reputation actions behind their flag.
/// </summary>
public class ReputationNotificationTests
{
    private static IReadOnlyList<NotificationEvent> Derive(Dictionary<string, object?> row, string eventName = "INSERT")
        => NotificationRules.Derive(NotificationRules.ReputationTable, eventName, new StreamImage(null),
            NotificationRulesTests.Image(row), new DateTime(2026, 11, 7, 9, 0, 4, DateTimeKind.Utc));

    [Fact]
    public void APenaltyWithPoints_TellsItsParty()
    {
        var events = Derive(new Dictionary<string, object?>
        {
            ["userId"] = "farmer-1",
            ["eventId"] = "CANCEL#order-1",
            ["code"] = "LATE_CANCEL",
            ["points"] = 4,
            ["activePointsAfter"] = 7,
            ["buyerId"] = "buyer-1"
        });

        var e = events.Should().ContainSingle().Subject;
        e.RecipientUserId.Should().Be("farmer-1");
        e.Type.Should().Be(NotificationTypes.PenaltyRecorded);
        e.Entity.Should().Be("reputation");
        e.EntityId.Should().Be("CANCEL#order-1");
        e.OrderId.Should().BeNull("the tap opens My reputation, not the order");
        e.Params.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [P.EventId] = "CANCEL#order-1",
            [P.Code] = "LATE_CANCEL",
            [P.Points] = "4",
            [P.ActivePoints] = "7"
        });
        e.Params.Values.Should().NotContain("buyer-1");
    }

    [Theory]
    [InlineData("DECLINED_REQUEST", 0)]
    [InlineData("ORDER_COMPLETED", 0)]
    [InlineData("CANCELLED_TRANSPORT_FAILED", 0)]
    [InlineData("RESTRICTION", 0)]
    [InlineData("HEAL", 0)]
    public void OtherRows_AreSilent(string code, int points)
        => Derive(new Dictionary<string, object?>
        {
            ["userId"] = "farmer-1",
            ["eventId"] = code + "#x",
            ["code"] = code,
            ["points"] = points
        }).Should().BeEmpty();

    [Fact]
    public void ATierRow_TellsTheParty_WithTheRestrictionEnd()
    {
        var restricted = Derive(new Dictionary<string, object?>
        {
            ["userId"] = "trucker-1",
            ["eventId"] = "TIER#2026-11-07T09:00:00.000Z#CANCEL#order-1",
            ["code"] = "TIER_CHANGED",
            ["fromTier"] = "low",
            ["toTier"] = "restricted",
            ["restrictedUntil"] = "2026-11-21T09:00:00.000Z"
        }).Should().ContainSingle().Subject;

        restricted.Type.Should().Be(NotificationTypes.TierChanged);
        restricted.RecipientUserId.Should().Be("trucker-1");
        restricted.Params[P.RestrictedUntil].Should().Be("2026-11-21T09:00:00.000Z");

        var warning = Derive(new Dictionary<string, object?>
        {
            ["userId"] = "trucker-1",
            ["eventId"] = "TIER#2026-11-07T09:00:00.000Z#daily",
            ["code"] = "TIER_CHANGED",
            ["fromTier"] = "good",
            ["toTier"] = "warning",
            ["restrictedUntil"] = "2026-11-21T09:00:00.000Z"
        }).Single();
        warning.Params.Should().NotContainKey(P.RestrictedUntil);
    }

    [Fact]
    public void AModify_OfAReputationRow_IsSilent()
        => Derive(new Dictionary<string, object?> { ["userId"] = "farmer-1", ["eventId"] = "CANCEL#o", ["code"] = "LATE_CANCEL", ["points"] = 4 },
            eventName: "MODIFY").Should().BeEmpty();

    [Fact]
    public void TheReputationEventsStream_IsARulesSource()
        => OrderNotifyStreamFunction.TableOf("arn:aws:dynamodb:x:1:table/wereda-connect-dev-reputation-events/stream/s")
            .Should().Be(NotificationRules.ReputationTable);

    [Theory]
    [InlineData("en", "Points were added for cancelling close to pickup. Added: 4. Total: 7. See My reputation to learn why or to appeal.")]
    [InlineData("am", null)]
    [InlineData("om", null)]
    public void PenaltyPush_ReadsInEveryLanguage(string language, string? expected)
    {
        var (title, body) = PushText.Render(NotificationTypes.PenaltyRecorded, language,
            new Dictionary<string, string> { [P.Code] = "LATE_CANCEL", [P.Points] = "4", [P.ActivePoints] = "7" });

        title.Should().NotBeNullOrWhiteSpace();
        body.Should().Contain("4").And.Contain("7");
        if (expected != null)
        {
            body.Should().Be(expected);
        }
    }

    [Fact]
    public void TierPush_NamesTheRestrictionEnd_OnlyWhenThereIsOne()
    {
        var (_, restricted) = PushText.Render(NotificationTypes.TierChanged, "en",
            new Dictionary<string, string> { [P.ToTier] = "restricted", [P.RestrictedUntil] = "2026-11-21T09:00:00.000Z" });
        restricted.Should().Be("You cannot take new orders for now. Your current orders continue. You can take new orders again after 21 Nov.");

        var (_, review) = PushText.Render(NotificationTypes.TierChanged, "en", new Dictionary<string, string> { [P.ToTier] = "restricted" });
        review.Should().Be("You cannot take new orders for now. Your current orders continue.");
    }

    // ===== the order-cron actions (8.1, 8.3) =====

    private static APIGatewayProxyRequest Body(string json) => new() { Body = json };

    [Theory]
    [InlineData("reputation_daily")]
    [InlineData("reputation_unpark")]
    [InlineData("reputation_recompute_all")]
    [InlineData("reputation_stamp_owner_copies")]
    public async Task ReputationActions_FlagOff_LogAndReturn(string action)
    {
        var context = new TestLambdaContext();
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>()) { ReputationPenaltiesEnabled = false };

        var response = await cron.FunctionHandler(Body($"{{\"action\":\"{action}\"}}"), context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("\"outcome\":\"disabled\"");
    }

    [Fact]
    public async Task ReputationDaily_FlagOn_Unconfigured_SaysSo()
    {
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>()) { ReputationPenaltiesEnabled = true };

        var response = await cron.FunctionHandler(Body("{\"action\":\"reputation_daily\"}"), new TestLambdaContext());

        response.Body.Should().Contain("\"outcome\":\"unconfigured\"");
    }

    [Fact]
    public async Task ReputationDaily_RunsTheJob_WithACatchUp()
    {
        var repo = new FakeReputationRepository();
        repo.AddUser("farmer-1");
        repo.Put(new Models.ReputationEvent
        {
            UserId = "farmer-1",
            EventId = "HEAL#x",
            Code = "HEAL",
            DueDay = ReputationPolicy.Day(DateTime.UtcNow.AddDays(-20)),
            DueAt = ReputationPolicy.Iso(DateTime.UtcNow.AddDays(-20))
        });
        var service = new ReputationService(repo, ReputationPolicySettings.Defaults, null, null, _ => Task.CompletedTask);
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>())
        {
            ReputationPenaltiesEnabled = true,
            ReputationDaily = new ReputationDailyJob(service, repo)
        };

        var normal = await cron.FunctionHandler(Body("{\"action\":\"reputation_daily\"}"), new TestLambdaContext());
        normal.Body.Should().Contain("\"users\":\"0\"");

        var catchUp = await cron.FunctionHandler(Body("{\"action\":\"reputation_daily\",\"catchUpDays\":30}"), new TestLambdaContext());
        catchUp.Body.Should().Contain("\"users\":\"1\"");
        repo.RowsOf("farmer-1").Should().BeEmpty();
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("\"ten\"")]
    [InlineData("2.5")]
    public async Task ReputationDaily_ABadCatchUp_Is400(string value)
    {
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>()) { ReputationPenaltiesEnabled = true };

        var response = await cron.FunctionHandler(Body($"{{\"action\":\"reputation_daily\",\"catchUpDays\":{value}}}"), new TestLambdaContext());

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ORDER_CRON_VALIDATION");
    }

    [Fact]
    public async Task RatingBackfill_IsNotFlagged()
    {
        var cron = new OrderCronFunction(Substitute.For<IOrderRepository>()) { ReputationPenaltiesEnabled = false };

        var response = await cron.FunctionHandler(Body("{\"action\":\"reputation_backfill_ratings\"}"), new TestLambdaContext());

        // Unconfigured in the test constructor, but not refused by the flag.
        response.Body.Should().Contain("\"outcome\":\"unconfigured\"");
    }
}
