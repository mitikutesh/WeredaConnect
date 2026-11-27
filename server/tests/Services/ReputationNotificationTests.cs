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

    // ===== FUP-195: an admin's decision on an event (the row's MODIFY, new image only) =====

    /// <summary>The event row as an admin's decision leaves it: <c>decidedVersion</c> equal to <c>version</c>, no open appeal.</summary>
    private static Dictionary<string, object?> Decided(string decision = "excused", Action<Dictionary<string, object?>>? change = null)
    {
        var row = new Dictionary<string, object?>
        {
            ["userId"] = "farmer-1",
            ["eventId"] = "CANCEL#order-1",
            ["code"] = "LATE_CANCEL",
            ["party"] = "farmer",
            ["status"] = decision == "excused" ? "excused" : "active",
            ["points"] = 4,
            ["buyerId"] = "buyer-1",
            ["orderId"] = "order-1",
            ["appealText"] = "The road was closed.",
            ["appealTextAt"] = "2026-11-06T08:00:00.000Z",
            ["appealDecision"] = decision,
            ["decidedAt"] = "2026-11-07T09:00:00.000Z",
            ["decidedBy"] = "admin-1",
            ["decisionNote"] = "Flood closed the road; confirmed with the buyer.",
            ["version"] = 2,
            ["decidedVersion"] = 2
        };
        change?.Invoke(row);
        return row;
    }

    [Theory]
    [InlineData("excused")]
    [InlineData("upheld")]
    public void ADecision_TellsItsParty_TheDecisionAndTheNote(string decision)
    {
        var e = Derive(Decided(decision), eventName: "MODIFY").Should().ContainSingle().Subject;

        e.RecipientUserId.Should().Be("farmer-1");
        e.Type.Should().Be(NotificationTypes.AppealDecided);
        e.Entity.Should().Be("reputation");
        e.EntityId.Should().Be("CANCEL#order-1");
        e.OrderId.Should().BeNull("the tap opens My reputation, not the order");
        e.Push.Should().BeTrue();
        e.Params.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            [P.EventId] = "CANCEL#order-1",
            [P.Code] = "LATE_CANCEL",
            [P.Decision] = decision,
            [P.Note] = "Flood closed the road; confirmed with the buyer."
        });
        // Never the buyer the row names, the admin, or the party's own appeal text.
        e.Params.Values.Should().NotContain(v => v.Contains("buyer-1") || v.Contains("admin-1") || v.Contains("road was closed"));
    }

    [Fact]
    public void ATrucker_IsToldToo()
        => Derive(Decided("upheld", r =>
            {
                r["userId"] = "trucker-1";
                r["party"] = "trucker";
                r["code"] = "DELIVERY_OVERDUE";
                r["eventId"] = "OVERDUE#order-1";
            }), eventName: "MODIFY")
            .Should().ContainSingle().Which.RecipientUserId.Should().Be("trucker-1");

    public static IEnumerable<object[]> SilentWrites() => new[]
    {
        // A later write to a decided row: an earn-back or the daily job's expiry moved the version on.
        new object[] { "a later write", (Action<Dictionary<string, object?>>)(r => r["version"] = 3) },
        // Decided before FUP-195: no decidedVersion, so no later write of it ever speaks.
        new object[] { "decided before FUP-195", (Action<Dictionary<string, object?>>)(r => r["decidedVersion"] = null) },
        // The party appealed an upheld event again: an open appeal, nothing decided yet.
        new object[] { "appealed again", (Action<Dictionary<string, object?>>)(r => r["appealStatus"] = "pending") },
        new object[] { "no decision", (Action<Dictionary<string, object?>>)(r => r["appealDecision"] = null) },
        new object[] { "an unknown decision", (Action<Dictionary<string, object?>>)(r => r["appealDecision"] = "withdrawn") },
        new object[] { "no decision time", (Action<Dictionary<string, object?>>)(r => r["decidedAt"] = null) },
        new object[] { "a system row", (Action<Dictionary<string, object?>>)(r => r["code"] = "RESTRICTION") },
        // Only a farmer or a trucker has a reputation: a buyer is never told.
        new object[] { "a buyer's row", (Action<Dictionary<string, object?>>)(r => r["party"] = "buyer") },
        new object[] { "no user", (Action<Dictionary<string, object?>>)(r => r["userId"] = null) }
    };

    [Theory]
    [MemberData(nameof(SilentWrites))]
    public void EveryOtherModify_IsSilent(string why, Action<Dictionary<string, object?>> change)
        => Derive(Decided("upheld", change), eventName: "MODIFY").Should().BeEmpty(why);

    [Fact]
    public void ADecidedRow_SpeaksOnlyOnItsModify()
        => Derive(Decided(), eventName: "REMOVE").Should().BeEmpty();

    [Theory]
    [InlineData("Called 0911 22 33 44 and +251-911-223-344, no answer.", "Called … and …, no answer.")]
    [InlineData("Office line (011) 551-2345.", "Office line (….")]
    [InlineData("Confirmed on 2026-11-07 with order ord-7f3a.", "Confirmed on 2026-11-07 with order ord-7f3a.")]
    [InlineData("Flood\u202E closed {the} [road]\n  again.", "Flood closed the road again.")]
    [InlineData("   ", "")]
    public void TheNote_KeepsItsWords_ButNoPhoneNumberOrControlCharacter(string raw, string stored)
        => NotificationRules.DecisionNote(raw).Should().Be(stored);

    [Fact]
    public void ANoteWithNothingLeft_LeavesNoNoteParam()
    {
        var e = Derive(Decided(change: r => r["decisionNote"] = "\u200B"), eventName: "MODIFY").Single();

        e.Params.Should().NotContainKey(P.Note);
    }

    [Theory]
    [InlineData("en", "excused", "Penalty points removed", "Our team removed the points for cancelling close to pickup. Open My reputation to read their note.")]
    [InlineData("en", "upheld", "The penalty points stay", "Our team reviewed the points for cancelling close to pickup and kept them. Open My reputation to read their note.")]
    [InlineData("en", "", "Your record was reviewed", "Our team reviewed your record for cancelling close to pickup. Open My reputation to read their note.")]
    [InlineData("am", "excused", null, null)]
    [InlineData("am", "upheld", null, null)]
    [InlineData("om", "excused", null, null)]
    [InlineData("om", "upheld", null, null)]
    public void DecisionPush_SaysTheDecision_ButNeverTheNote(string language, string decision, string? title, string? body)
    {
        var p = new Dictionary<string, string>
        {
            [P.Code] = "LATE_CANCEL",
            [P.Decision] = decision,
            [P.Note] = "Flood closed the road; confirmed with the buyer."
        };

        var rendered = PushText.Render(NotificationTypes.AppealDecided, language, p);

        rendered.Body.Should().NotContain("Flood");
        if (title != null)
        {
            rendered.Should().Be((title, body!));
        }
        else
        {
            // Its own words per decision, not the neutral ones.
            rendered.Title.Should().NotBe(PushText.Render(NotificationTypes.AppealDecided, language, new Dictionary<string, string>()).Title);
            rendered.Body.Should().NotEndWith("…");
        }
    }

    [Fact]
    public void TheDecisionTexts_AreTheTwoDecisions()
        => PushText.DecisionKeys.Should().BeEquivalentTo(ReputationService.DecisionExcused, ReputationService.DecisionUpheld);

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
