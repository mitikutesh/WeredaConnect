using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-32: the admin reputation endpoints (reputation-api.md section 9): the admin group on every
/// route, the flag on every write, validation, the excuse and uphold decisions with their replay
/// and conflict answers, tag, untag, restrict and lift, the appeals queue, the audit row of every
/// applied action, the recompute of the aggregate and the refresh of the copied owner fields.
/// </summary>
public class AdminReputationTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);
    private const string UsersTable = "users";
    private const string AuditTable = "tx-audit";

    private readonly FakeReputationRepository _repo = new();
    private readonly ManualClock _clock = new(T0);
    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly IReputationCopyWriter _copies = Substitute.For<IReputationCopyWriter>();
    private readonly TestLambdaContext _ctx = new();
    private readonly List<PutItemRequest> _auditRows = new();
    private readonly List<(string User, OwnerReputationFields Fields)> _copied = new();

    public AdminReputationTests()
    {
        _repo.AddUser("farmer-1");
        _repo.AddUser("trucker-1", "trucker");
        _repo.AddUser("buyer-1", "buyer");
        _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => _auditRows.Add(r)), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());
        _dynamoDb.BatchGetItemAsync(Arg.Any<BatchGetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new BatchGetItemResponse
            {
                Responses = new Dictionary<string, List<Dictionary<string, AttributeValue>>>
                {
                    [UsersTable] = new()
                    {
                        new()
                        {
                            ["userId"] = new() { S = "farmer-1" }, ["displayName"] = new() { S = "Abebe K." }, ["role"] = new() { S = "farmer" }
                        },
                        new()
                        {
                            ["userId"] = new() { S = "trucker-1" }, ["displayName"] = new() { S = "Kebede A." }, ["role"] = new() { S = "trucker" }
                        }
                    }
                },
                UnprocessedKeys = new Dictionary<string, KeysAndAttributes>()
            });
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>())
            .Returns(call => $"https://uploads.test/{call.Arg<GetPreSignedUrlRequest>().Key}?sig");
        _copies.RefreshOwnerRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OwnerReputationFields>())
            .Returns(call =>
            {
                _copied.Add((call.ArgAt<string>(0), call.ArgAt<OwnerReputationFields>(2)));
                return 1;
            });
    }

    private ReputationService Service()
        => new(_repo, ReputationPolicySettings.Defaults, Substitute.For<IFraudDetectionService>(), _clock, _ => Task.CompletedTask, _copies);

    private AdminFunction Function(bool penalties = true)
        => new(_dynamoDb, UsersTable, transactionAuditTable: AuditTable)
        {
            ReputationPenaltiesEnabled = penalties,
            ReputationRepo = _repo,
            ReputationSvc = Service(),
            UploadsBucket = "uploads-bucket",
            DeliveryPhotoS3 = _s3,
            Clock = () => _clock.Now
        };

    private async Task Penalty(
        string user, string slot, DateTime at, string code = ReputationCodes.CancelledAfterAccept, string buyer = "buyer-1")
    {
        var before = _clock.Now;
        _clock.Now = at.AddSeconds(4);
        await Service().RecordAsync(new ReputationCandidate
        {
            UserId = user,
            EventId = slot,
            Code = code,
            Party = _repo.Users[user].Role,
            OrderId = slot[(slot.IndexOf('#') + 1)..],
            TripId = code == ReputationCodes.TripCancelledWithBookings ? slot[(slot.IndexOf('#') + 1)..] : null,
            BuyerId = buyer,
            OccurredAt = at,
            RequestWindow = TimeSpan.FromHours(24),
            Reachable = true
        }, timeoutPenaltiesOn: true, new TestLambdaLogger());
        _clock.Now = before;
        _copied.Clear();
    }

    private Task<bool> Appeal(string user, string slot, string text = "The road was closed.", string? photo = null)
        => _repo.TryAppealAsync(user, slot, text, photo, _clock.Now, _repo.Row(user, slot).AppealStatus == "review");

    private static string DecisionPath(string user, string slot) => $"/admin/users/{user}/reputation/events/{Uri.EscapeDataString(slot)}/decision";

    private static string Decision(string decision, string note = "Flood closed the road.", string? until = null)
        => JsonSerializer.Serialize(new Dictionary<string, string?> { ["decision"] = decision, ["note"] = note, ["until"] = until }
            .Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value));

    private static string Action(string action, string? reason = "Repeated complaints from three buyers.", DateTime? until = null)
        => JsonSerializer.Serialize(new Dictionary<string, string?>
        {
            ["action"] = action,
            ["reason"] = reason,
            ["until"] = until is { } u ? ReputationPolicy.Iso(u) : null
        }
            .Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value));

    // ===== authorization and flags =====

    [Theory]
    [InlineData("GET", "/admin/users/farmer-1/reputation")]
    [InlineData("GET", "/admin/reputation/appeals")]
    [InlineData("POST", "/admin/users/farmer-1/reputation")]
    [InlineData("POST", "/admin/users/farmer-1/reputation/events/CANCEL%23o1/decision")]
    public async Task EveryRoute_NeedsTheAdminGroup_NotARoleClaim(string method, string path)
    {
        // A role claim alone passes the Lambda's general admin check; the reputation routes need the group.
        var roleOnly = Request(method, path, "{}", groups: null, role: "admin");
        await Error(roleOnly, 403, "ADMIN_001");

        foreach (var role in new[] { "farmer", "trucker", "buyer" })
        {
            await Error(Request(method, path, "{}", groups: null, role: role), 403, "ADMIN_001");
        }

        _repo.Committed.Should().BeEmpty();
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Writes_AreRefused_WhileThePenaltiesFlagIsOff_AndReadsStillWork()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        var committed = _repo.Committed.Count;

        await Error(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("excuse")), 503, "REPUTATION_UNAVAILABLE", penalties: false);
        await Error(Post("/admin/users/farmer-1/reputation", Action("tag", until: T0.AddDays(10))), 503, "REPUTATION_UNAVAILABLE", penalties: false);
        await Error(Post("/admin/users/farmer-1/reputation", Action("restrict", until: T0.AddDays(10))), 503, "REPUTATION_UNAVAILABLE", penalties: false);
        _repo.Committed.Should().HaveCount(committed);
        _auditRows.Should().BeEmpty();

        var body = await Ok(Get("/admin/users/farmer-1/reputation"), penalties: false);
        body.GetProperty("reputation").GetProperty("activePoints").GetInt32().Should().Be(3);
        body.GetProperty("reputation").GetProperty("penaltiesEnabled").GetBoolean().Should().BeFalse();
        (await Ok(Get("/admin/reputation/appeals"), penalties: false)).GetProperty("appeals").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task EveryRoute_WithoutTheTable_Is503()
    {
        var fn = new AdminFunction(_dynamoDb, UsersTable) { ReputationEventsTable = null, ReputationPenaltiesEnabled = true };

        var response = await fn.FunctionHandler(Get("/admin/users/farmer-1/reputation"), _ctx);

        response.StatusCode.Should().Be(503, response.Body);
    }

    // ===== 9.1: the admin read =====

    [Fact]
    public async Task Read_ShowsEverything_PointsRestrictionsEventsBuyerAndAppeal()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-3), ReputationCodes.LateCancel);
        await Penalty("farmer-1", "CANCEL#o2", T0.AddDays(-2), ReputationCodes.LateCancel, "buyer-2");
        await Penalty("farmer-1", "CANCEL#o3", T0.AddDays(-1), ReputationCodes.LateCancel, "buyer-3");
        await Appeal("farmer-1", "CANCEL#o1", photo: "uploads/appeals/farmer-1/5b0c.webp");

        var body = await Ok(Get("/admin/users/farmer-1/reputation"));

        var reputation = body.GetProperty("reputation");
        reputation.GetProperty("activePoints").GetInt32().Should().Be(12);
        reputation.GetProperty("tier").GetString().Should().Be("restricted");
        reputation.GetProperty("restriction").GetProperty("cause").GetString().Should().Be("points");
        var restrictions = body.GetProperty("restrictions");
        restrictions.GetArrayLength().Should().Be(1);
        restrictions[0].GetProperty("status").GetString().Should().Be("active");
        restrictions[0].GetProperty("until").GetString().Should().NotBeNullOrEmpty();

        var events = body.GetProperty("events").EnumerateArray().ToList();
        events.Select(e => e.GetProperty("code").GetString()).Should()
            .Contain(new[] { "LATE_CANCEL", "RESTRICTION", "TIER_CHANGED" }, "an admin sees the system rows too")
            .And.NotContain("HEAL");
        var appealed = events.Single(e => e.GetProperty("eventId").GetString() == "CANCEL#o1");
        appealed.GetProperty("buyerId").GetString().Should().Be("buyer-1");
        appealed.GetProperty("basePoints").GetInt32().Should().Be(4);
        var appeal = appealed.GetProperty("appeal");
        appeal.GetProperty("status").GetString().Should().Be("pending");
        appeal.GetProperty("text").GetString().Should().Be("The road was closed.");
        appeal.GetProperty("hasPhoto").GetBoolean().Should().BeTrue();
        appeal.GetProperty("appealPhotoUrl").GetString().Should().Be("https://uploads.test/uploads/appeals/farmer-1/5b0c.webp?sig");
        _s3.Received().GetPreSignedURL(Arg.Is<GetPreSignedUrlRequest>(r =>
            r.Verb == HttpVerb.GET && r.BucketName == "uploads-bucket" && r.Expires == T0.AddSeconds(600)));
    }

    [Fact]
    public async Task Read_NeverSignsAPhotoKeyOutsideTheUsersAppealPrefix()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        _repo.Put(_repo.Row("farmer-1", "CANCEL#o1") with { AppealText = "x", AppealPhotoRef = "uploads/photos/other/secret.webp" });

        var body = await Ok(Get("/admin/users/farmer-1/reputation"));

        var appeal = body.GetProperty("events").EnumerateArray()
            .Single(e => e.GetProperty("eventId").GetString() == "CANCEL#o1").GetProperty("appeal");
        appeal.TryGetProperty("appealPhotoUrl", out _).Should().BeFalse();
        _s3.DidNotReceive().GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>());
    }

    [Fact]
    public async Task Read_PagesTheEvents_AndRefusesATokenOfAnotherUser()
    {
        for (var i = 1; i <= 5; i++)
        {
            await Penalty("farmer-1", $"REQUEST#o{i}", T0.AddDays(-i), ReputationCodes.DeclinedRequest, $"buyer-{i}");
        }

        var first = await Ok(Get("/admin/users/farmer-1/reputation", new() { ["limit"] = "2" }));
        first.GetProperty("events").GetArrayLength().Should().Be(2);
        var token = first.GetProperty("nextToken").GetString()!;

        var second = await Ok(Get("/admin/users/farmer-1/reputation", new() { ["limit"] = "2", ["nextToken"] = token }));
        second.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventId").GetString())
            .Should().NotIntersectWith(first.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventId").GetString()));

        await Error(Get("/admin/users/trucker-1/reputation", new() { ["nextToken"] = token }), 400, "REPUTATION_VALIDATION");
        await Error(Get("/admin/users/farmer-1/reputation", new() { ["limit"] = "0" }), 400, "REPUTATION_VALIDATION");
        await Error(Get("/admin/users/farmer-1/reputation", new() { ["limit"] = "101" }), 400, "REPUTATION_VALIDATION");
    }

    [Fact]
    public async Task Read_ABuyerHasNoEvents_AndAMissingOrDeletingUserIsNotFound()
    {
        var buyer = await Ok(Get("/admin/users/buyer-1/reputation"));
        buyer.GetProperty("reputation").GetProperty("role").GetString().Should().Be("buyer");
        buyer.GetProperty("events").GetArrayLength().Should().Be(0);

        await Error(Get("/admin/users/ghost/reputation"), 404, "REPUTATION_NOT_FOUND");
        _repo.Users["farmer-1"].DeletionState = "in_progress";
        await Error(Get("/admin/users/farmer-1/reputation"), 404, "REPUTATION_NOT_FOUND");
    }

    // ===== 9.2: the appeals queue =====

    [Fact]
    public async Task Queue_ListsOpenAppealsOldestFirst_WithTheUserTheTextAndThePhoto()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-3));
        await Penalty("trucker-1", "TRIP_CANCEL#t9", T0.AddDays(-2), ReputationCodes.TripCancelledWithBookings);
        _clock.Now = T0.AddHours(1);
        await Appeal("trucker-1", "TRIP_CANCEL#t9", "The truck broke down.", "uploads/appeals/trucker-1/a1.jpg");
        _clock.Now = T0.AddHours(2);
        await Appeal("farmer-1", "CANCEL#o1");

        var body = await Ok(Get("/admin/reputation/appeals"));

        var appeals = body.GetProperty("appeals").EnumerateArray().ToList();
        appeals.Select(a => a.GetProperty("userId").GetString()).Should().Equal("trucker-1", "farmer-1");
        var first = appeals[0];
        first.GetProperty("displayName").GetString().Should().Be("Kebede A.");
        first.GetProperty("role").GetString().Should().Be("trucker");
        first.GetProperty("eventId").GetString().Should().Be("TRIP_CANCEL#t9");
        first.GetProperty("tripId").GetString().Should().Be("t9");
        first.TryGetProperty("orderId", out _).Should().BeFalse();
        first.GetProperty("appealStatus").GetString().Should().Be("pending");
        first.GetProperty("text").GetString().Should().Be("The truck broke down.");
        first.GetProperty("hasPhoto").GetBoolean().Should().BeTrue();
        first.GetProperty("appealPhotoUrl").GetString().Should().Contain("uploads/appeals/trucker-1/a1.jpg");
        appeals[1].GetProperty("hasPhoto").GetBoolean().Should().BeFalse();
        body.TryGetProperty("nextToken", out _).Should().BeFalse();
        // The queue is one index Query and one batch read of the users: never a Scan.
        await _dynamoDb.DidNotReceive().ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Queue_Pages_AndRefusesATokenOfTheOtherStatus()
    {
        for (var i = 1; i <= 3; i++)
        {
            await Penalty("farmer-1", $"CANCEL#o{i}", T0.AddDays(-i), buyer: $"buyer-{i}");
            _clock.Now = T0.AddMinutes(i);
            await Appeal("farmer-1", $"CANCEL#o{i}");
        }

        var first = await Ok(Get("/admin/reputation/appeals", new() { ["limit"] = "2" }));
        first.GetProperty("appeals").EnumerateArray().Select(a => a.GetProperty("eventId").GetString())
            .Should().Equal("CANCEL#o1", "CANCEL#o2");
        var token = first.GetProperty("nextToken").GetString()!;

        var second = await Ok(Get("/admin/reputation/appeals", new() { ["limit"] = "2", ["nextToken"] = token }));
        second.GetProperty("appeals").EnumerateArray().Select(a => a.GetProperty("eventId").GetString()).Should().Equal("CANCEL#o3");

        await Error(Get("/admin/reputation/appeals", new() { ["status"] = "review", ["nextToken"] = token }), 400, "REPUTATION_VALIDATION");
        await Error(Get("/admin/reputation/appeals", new() { ["status"] = "excused" }), 400, "REPUTATION_VALIDATION");
        await Error(Get("/admin/reputation/appeals", new() { ["nextToken"] = "garbage" }), 400, "REPUTATION_VALIDATION");
    }

    // ===== 9.3: decisions =====

    [Fact]
    public async Task Excuse_RemovesThePoints_ClosesTheAppeal_AuditsAndRefreshesTheCopies()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-3), ReputationCodes.LateCancel);
        await Penalty("farmer-1", "CANCEL#o2", T0.AddDays(-2), ReputationCodes.LateCancel, "buyer-2");
        await Appeal("farmer-1", "CANCEL#o1");
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be("low");

        var body = await Ok(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("excuse")));

        body.GetProperty("alreadyDecided").GetBoolean().Should().BeFalse();
        var ev = body.GetProperty("event");
        ev.GetProperty("status").GetString().Should().Be("excused");
        ev.GetProperty("remainingPoints").GetInt32().Should().Be(0);
        ev.GetProperty("appeal").GetProperty("status").GetString().Should().Be("excused");
        ev.GetProperty("appeal").GetProperty("note").GetString().Should().Be("Flood closed the road.");
        ev.GetProperty("appeal").GetProperty("decidedBy").GetString().Should().Be("admin-1");
        body.GetProperty("reputation").GetProperty("activePoints").GetInt32().Should().Be(4);
        body.GetProperty("reputation").GetProperty("tier").GetString().Should().Be("warning");

        var row = _repo.Row("farmer-1", "CANCEL#o1");
        row.Status.Should().Be(ReputationStatuses.Excused);
        row.AppealStatus.Should().BeNull("the row leaves the appeals index");
        row.AppealedAt.Should().BeNull();
        row.DueDay.Should().BeNull();
        row.AppealDecision.Should().Be("excused");
        row.Version.Should().Be(2);
        // The aggregate is written in the same transaction, with the tier row and the heal marker.
        var write = _repo.Committed[^1];
        write.RowUpdates.Should().ContainSingle().Which.SeenVersion.Should().Be(1);
        write.Map.Aggregate.ActivePoints.Should().Be(4);
        write.NewSystemRows.Should().ContainSingle(r => r.Code == ReputationCodes.TierChanged && r.ToTier == "warning");
        write.HealAt.Should().NotBeNull();
        _copied.Should().ContainSingle().Which.Fields.Tier.Should().Be("warning");

        var audit = _auditRows.Should().ContainSingle().Subject;
        audit.TableName.Should().Be(AuditTable);
        audit.Item["action"].S.Should().Be("reputation_excused");
        audit.Item["entityType"].S.Should().Be("user");
        audit.Item["entityId"].S.Should().Be("farmer-1");
        audit.Item["actor"].S.Should().Be("admin-1");
        audit.Item["fromState"].S.Should().Be("tier=low;points=8");
        audit.Item["toState"].S.Should().Be("tier=warning;points=4");
        audit.Item["metadata"].S.Should().Be("event=CANCEL#o1;note=Flood closed the road.");
        audit.Item.Values.Should().NotContain(v => v.S != null && v.S.Contains("The road was closed."), "the appeal text is never audited");
    }

    [Fact]
    public async Task Excuse_WithoutAnAppeal_Works_AndTheSameDecisionAgainWritesNothing()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));

        await Ok(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("excuse")));
        var committed = _repo.Committed.Count;

        var replay = await Ok(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("excuse", "Another note.")));

        replay.GetProperty("alreadyDecided").GetBoolean().Should().BeTrue();
        replay.GetProperty("event").GetProperty("appeal").GetProperty("note").GetString().Should().Be("Flood closed the road.");
        _repo.Committed.Should().HaveCount(committed);
        _auditRows.Should().ContainSingle("a replay is not audited again");
        // The party cannot appeal an excused event.
        (await Appeal("farmer-1", "CANCEL#o1")).Should().BeFalse();
    }

    [Fact]
    public async Task Excuse_BelowTheLine_LiftsAPointsRestriction()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-3), ReputationCodes.LateCancel);
        await Penalty("farmer-1", "CANCEL#o2", T0.AddDays(-2), ReputationCodes.LateCancel, "buyer-2");
        await Penalty("farmer-1", "CANCEL#o3", T0.AddDays(-1), ReputationCodes.LateCancel, "buyer-3");
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be("restricted");

        var body = await Ok(Post(DecisionPath("farmer-1", "CANCEL#o3"), Decision("excuse")));

        body.GetProperty("reputation").GetProperty("tier").GetString().Should().Be("low");
        body.GetProperty("reputation").TryGetProperty("restriction", out _).Should().BeFalse();
        var restriction = _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.Restriction);
        restriction.Status.Should().Be(ReputationStatuses.Lifted);
        restriction.LiftedBy.Should().Be("admin-1");
        restriction.DueDay.Should().BeNull();
    }

    [Fact]
    public async Task Uphold_ClosesAnOpenAppeal_KeepsThePoints_AndNeverReopens()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        await Appeal("farmer-1", "CANCEL#o1");

        var body = await Ok(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("uphold", "The buyer confirmed the cancel.")));

        body.GetProperty("event").GetProperty("status").GetString().Should().Be("active");
        body.GetProperty("event").GetProperty("appeal").GetProperty("status").GetString().Should().Be("upheld");
        body.GetProperty("event").GetProperty("appeal").GetProperty("canAppeal").GetBoolean().Should().BeFalse();
        body.GetProperty("reputation").GetProperty("activePoints").GetInt32().Should().Be(3);
        _repo.Row("farmer-1", "CANCEL#o1").AppealStatus.Should().BeNull();
        _auditRows.Should().ContainSingle().Which.Item["action"].S.Should().Be("reputation_upheld");
        _copied.Should().BeEmpty("no copied field changed");

        (await Ok(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("uphold")))).GetProperty("alreadyDecided").GetBoolean().Should().BeTrue();
        await Error(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("excuse")), 409, "REPUTATION_EVENT_DECIDED");
        // The party had their say: no second appeal.
        (await Appeal("farmer-1", "CANCEL#o1", "Second try.")).Should().BeFalse();
        _auditRows.Should().ContainSingle();
    }

    [Fact]
    public async Task Uphold_WithNoOpenAppeal_IsRefused_AndAnExcusedEventCannotBeUpheld()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        await Penalty("farmer-1", "CANCEL#o2", T0.AddDays(-1), buyer: "buyer-2");

        await Error(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("uphold")), 409, "REPUTATION_APPEAL_CLOSED");

        await Ok(Post(DecisionPath("farmer-1", "CANCEL#o2"), Decision("excuse")));
        await Error(Post(DecisionPath("farmer-1", "CANCEL#o2"), Decision("uphold")), 409, "REPUTATION_EVENT_DECIDED");
    }

    [Fact]
    public async Task Uphold_OfAnOverdueReview_GivesTheRestrictionItsEnd_AndThePartyMayAppealOnce()
    {
        await Penalty("trucker-1", "OVERDUE#o1", T0.AddDays(-1), ReputationCodes.DeliveryOverdue);
        var review = _repo.Row("trucker-1", "OVERDUE#o1");
        review.AppealStatus.Should().Be("review");
        _repo.RowsOf("trucker-1").Single(r => r.Code == ReputationCodes.Restriction).Until.Should().BeNull();

        var body = await Ok(Post(DecisionPath("trucker-1", "OVERDUE#o1"), Decision("uphold", "No delivery was made.")));

        body.GetProperty("reputation").GetProperty("restriction").GetProperty("until").GetString()
            .Should().Be(ReputationPolicy.Iso(T0.AddDays(14)));
        var restriction = _repo.RowsOf("trucker-1").Single(r => r.Code == ReputationCodes.Restriction);
        restriction.Until.Should().Be(T0.AddDays(14));
        restriction.DueAt.Should().Be(ReputationPolicy.Iso(T0.AddDays(14)), "the daily job ends it");

        // Decided with no word from the party: they may still appeal once, and it is pending again.
        body.GetProperty("event").GetProperty("appeal").GetProperty("canAppeal").GetBoolean().Should().BeTrue();
        (await Appeal("trucker-1", "OVERDUE#o1", "The road was washed out.")).Should().BeTrue();
        _repo.Row("trucker-1", "OVERDUE#o1").AppealStatus.Should().Be("pending");

        var excused = await Ok(Post(DecisionPath("trucker-1", "OVERDUE#o1"), Decision("excuse", "Confirmed with the buyer.")));
        excused.GetProperty("alreadyDecided").GetBoolean().Should().BeFalse();
        excused.GetProperty("reputation").GetProperty("activePoints").GetInt32().Should().Be(0);
        _repo.RowsOf("trucker-1").Single(r => r.Code == ReputationCodes.Restriction).Status.Should().Be(ReputationStatuses.Lifted);
        (await Appeal("trucker-1", "OVERDUE#o1", "Again.")).Should().BeFalse("never a second appeal");
    }

    [Fact]
    public async Task Uphold_OfAnOverdueReview_TakesAnEndFromTheBody_WithinTheLimit()
    {
        await Penalty("trucker-1", "OVERDUE#o1", T0.AddDays(-1), ReputationCodes.DeliveryOverdue);

        await Error(Post(DecisionPath("trucker-1", "OVERDUE#o1"), Decision("uphold", until: ReputationPolicy.Iso(T0.AddDays(366)))),
            400, "REPUTATION_VALIDATION");
        await Error(Post(DecisionPath("trucker-1", "OVERDUE#o1"), Decision("uphold", until: ReputationPolicy.Iso(T0.AddDays(-1)))),
            400, "REPUTATION_VALIDATION");
        _repo.Row("trucker-1", "OVERDUE#o1").AppealStatus.Should().Be("review");

        await Ok(Post(DecisionPath("trucker-1", "OVERDUE#o1"), Decision("uphold", until: ReputationPolicy.Iso(T0.AddDays(30)))));

        _repo.RowsOf("trucker-1").Single(r => r.Code == ReputationCodes.Restriction).Until.Should().Be(T0.AddDays(30));
    }

    [Fact]
    public async Task Excuse_OfAnOverdueReview_LiftsItsRestriction()
    {
        await Penalty("trucker-1", "OVERDUE#o1", T0.AddDays(-1), ReputationCodes.DeliveryOverdue);

        var body = await Ok(Post(DecisionPath("trucker-1", "OVERDUE#o1"), Decision("excuse")));

        body.GetProperty("reputation").GetProperty("tier").GetString().Should().Be("good");
        _repo.RowsOf("trucker-1").Single(r => r.Code == ReputationCodes.Restriction).Status.Should().Be(ReputationStatuses.Lifted);
        _repo.Row("trucker-1", "OVERDUE#o1").AppealStatus.Should().BeNull();
    }

    [Fact]
    public async Task Decision_Validation_AndUnknownTargets()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        var path = DecisionPath("farmer-1", "CANCEL#o1");

        await Error(Post(path, "not json"), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Decision("forgive")), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Decision("excuse", "ok")), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Decision("excuse", new string('x', 501))), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Decision("excuse", until: ReputationPolicy.Iso(T0.AddDays(5)))), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Decision("uphold", until: "next week")), 400, "REPUTATION_VALIDATION");
        await Error(Post(DecisionPath("farmer-1", "CANCEL#missing"), Decision("excuse")), 404, "REPUTATION_EVENT_NOT_FOUND");
        await Error(Post(DecisionPath("ghost", "CANCEL#o1"), Decision("excuse")), 404, "REPUTATION_NOT_FOUND");
        // A system row is not an event an admin decides.
        var tierRow = _repo.RowsOf("farmer-1").First(r => r.Code == ReputationCodes.TierChanged).EventId;
        await Error(Post(DecisionPath("farmer-1", tierRow), Decision("excuse")), 404, "REPUTATION_EVENT_NOT_FOUND");
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Excuse_OfAnExpiredEvent_IsRefused()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-91));

        await Error(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("excuse")), 409, "REPUTATION_APPEAL_CLOSED");
    }

    [Fact]
    public async Task TwoAdmins_DecidingTheSameEvent_ApplyItOnce()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        await Appeal("farmer-1", "CANCEL#o1");
        // The other admin's uphold commits between this request's read and its write.
        _repo.BeforeCommit = async _ =>
        {
            var other = await Service().DecideAsync("farmer-1", "CANCEL#o1", "uphold", "Other admin.", null, "admin-2", new TestLambdaLogger());
            other.Outcome.Should().Be(ReputationAdminOutcome.Applied);
        };

        // This request loses its condition, reads again and finds the event decided the other way.
        await Error(Post(DecisionPath("farmer-1", "CANCEL#o1"), Decision("excuse")), 409, "REPUTATION_EVENT_DECIDED");

        var row = _repo.Row("farmer-1", "CANCEL#o1");
        row.AppealDecision.Should().Be("upheld");
        row.DecidedBy.Should().Be("admin-2");
        row.Status.Should().Be(ReputationStatuses.Active);
        _auditRows.Should().BeEmpty("the losing request applied nothing");
    }

    [Fact]
    public async Task AnUphold_NeverClosesAnAppealWhoseTextArrivedAfterTheRead()
    {
        await Penalty("trucker-1", "OVERDUE#o1", T0.AddDays(-1), ReputationCodes.DeliveryOverdue);
        // The party's text lands on the review row after the admin's read. It moves no version.
        _repo.BeforeCommit = async _ => (await Appeal("trucker-1", "OVERDUE#o1", "The bridge was closed.")).Should().BeTrue();

        var body = await Ok(Post(DecisionPath("trucker-1", "OVERDUE#o1"), Decision("uphold")));

        // The first write lost its condition; the second saw the text.
        _repo.Attempts.Should().BeGreaterThan(2);
        body.GetProperty("event").GetProperty("appeal").GetProperty("text").GetString().Should().Be("The bridge was closed.");
        body.GetProperty("event").GetProperty("appeal").GetProperty("canAppeal").GetBoolean().Should().BeFalse();
    }

    // ===== 9.4: tag, untag, restrict, lift =====

    [Fact]
    public async Task Tag_ShowsLowReputation_WhateverThePoints_AndIsAudited()
    {
        var body = await Ok(Post("/admin/users/farmer-1/reputation", Action("tag", until: T0.AddDays(30))));

        body.GetProperty("alreadyApplied").GetBoolean().Should().BeFalse();
        var reputation = body.GetProperty("reputation");
        reputation.GetProperty("tier").GetString().Should().Be("low");
        reputation.GetProperty("activePoints").GetInt32().Should().Be(0);
        var tag = reputation.GetProperty("override");
        tag.GetProperty("tag").GetString().Should().Be("low");
        tag.GetProperty("until").GetString().Should().Be(ReputationPolicy.Iso(T0.AddDays(30)));
        tag.GetProperty("by").GetString().Should().Be("admin-1");

        var row = _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.AdminTag);
        row.EventId.Should().StartWith("OVERRIDE#");
        row.Reason.Should().Be("Repeated complaints from three buyers.");
        row.DueAt.Should().Be(ReputationPolicy.Iso(T0.AddDays(30)));
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be("low");
        _copied.Should().ContainSingle().Which.Fields.Tier.Should().Be("low");

        var audit = _auditRows.Should().ContainSingle().Subject;
        audit.Item["action"].S.Should().Be("reputation_tagged");
        audit.Item["fromState"].S.Should().Be("tier=good;points=0");
        audit.Item["toState"].S.Should().Be("tier=low;points=0");
        audit.Item["metadata"].S.Should().Be($"reason=Repeated complaints from three buyers.;until={ReputationPolicy.Iso(T0.AddDays(30))}");
    }

    [Fact]
    public async Task TheSameTagAgain_WritesNothing_AndANewOneReplacesTheOld()
    {
        await Ok(Post("/admin/users/farmer-1/reputation", Action("tag", until: T0.AddDays(30))));
        var committed = _repo.Committed.Count;

        var replay = await Ok(Post("/admin/users/farmer-1/reputation", Action("tag", until: T0.AddDays(30))));
        replay.GetProperty("alreadyApplied").GetBoolean().Should().BeTrue();
        _repo.Committed.Should().HaveCount(committed);
        _auditRows.Should().ContainSingle();

        _clock.Now = T0.AddMinutes(1);
        var untag = await Ok(Post("/admin/users/farmer-1/reputation", Action("untag", "Cleared after a call with the farmer.", T0.AddDays(20))));

        untag.GetProperty("reputation").GetProperty("tier").GetString().Should().Be("good");
        untag.GetProperty("reputation").GetProperty("override").GetProperty("tag").GetString().Should().Be("clear");
        _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.AdminTag).Status.Should().Be(ReputationStatuses.Replaced);
        _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.AdminUntag).Status.Should().Be(ReputationStatuses.Active);
        _auditRows.Select(a => a.Item["action"].S).Should().Equal("reputation_tagged", "reputation_untagged");
    }

    [Fact]
    public async Task Untag_HidesTheComputedTag()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-3), ReputationCodes.LateCancel);
        await Penalty("farmer-1", "CANCEL#o2", T0.AddDays(-2), ReputationCodes.LateCancel, "buyer-2");

        var body = await Ok(Post("/admin/users/farmer-1/reputation", Action("untag", until: T0.AddDays(10))));

        body.GetProperty("reputation").GetProperty("activePoints").GetInt32().Should().Be(8);
        body.GetProperty("reputation").GetProperty("tier").GetString().Should().Be("warning");
    }

    [Fact]
    public async Task Restrict_ThenLift_WritesTheRows_AndBothAreAudited()
    {
        var restricted = await Ok(Post("/admin/users/trucker-1/reputation", Action("restrict", until: T0.AddDays(60))));

        restricted.GetProperty("reputation").GetProperty("tier").GetString().Should().Be("restricted");
        var restriction = restricted.GetProperty("reputation").GetProperty("restriction");
        restriction.GetProperty("cause").GetString().Should().Be("admin");
        restriction.GetProperty("until").GetString().Should().Be(ReputationPolicy.Iso(T0.AddDays(60)));
        var row = _repo.RowsOf("trucker-1").Single(r => r.Code == ReputationCodes.Restriction);
        row.By.Should().Be("admin-1");
        row.Reason.Should().Be("Repeated complaints from three buyers.");
        _copied.Should().ContainSingle().Which.Fields.Tier.Should().Be("restricted");

        // The same request again writes nothing; a different one is refused until a lift.
        (await Ok(Post("/admin/users/trucker-1/reputation", Action("restrict", until: T0.AddDays(60)))))
            .GetProperty("alreadyApplied").GetBoolean().Should().BeTrue();
        await Error(Post("/admin/users/trucker-1/reputation", Action("restrict", until: T0.AddDays(90))), 409, "REPUTATION_ALREADY_RESTRICTED");

        _clock.Now = T0.AddHours(1);
        var lifted = await Ok(Post("/admin/users/trucker-1/reputation", Action("lift", "The buyers withdrew their complaints.")));

        lifted.GetProperty("reputation").GetProperty("tier").GetString().Should().Be("good");
        lifted.GetProperty("reputation").TryGetProperty("restriction", out _).Should().BeFalse();
        var after = _repo.RowsOf("trucker-1").Single(r => r.Code == ReputationCodes.Restriction);
        after.Status.Should().Be(ReputationStatuses.Lifted);
        after.LiftedAt.Should().Be(ReputationPolicy.Iso(T0.AddHours(1)));
        after.LiftedBy.Should().Be("admin-1");
        _auditRows.Select(a => a.Item["action"].S).Should().Equal("reputation_restricted", "reputation_lifted");
        _auditRows[1].Item["fromState"].S.Should().Be("tier=restricted;points=0");
        _auditRows[1].Item["toState"].S.Should().Be("tier=good;points=0");

        await Error(Post("/admin/users/trucker-1/reputation", Action("lift")), 409, "REPUTATION_NOT_RESTRICTED");
    }

    [Fact]
    public async Task Lift_EndsAPointsRestrictionToo()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-3), ReputationCodes.LateCancel);
        await Penalty("farmer-1", "CANCEL#o2", T0.AddDays(-2), ReputationCodes.LateCancel, "buyer-2");
        await Penalty("farmer-1", "CANCEL#o3", T0.AddDays(-1), ReputationCodes.LateCancel, "buyer-3");

        var body = await Ok(Post("/admin/users/farmer-1/reputation", Action("lift")));

        body.GetProperty("reputation").GetProperty("tier").GetString().Should().Be("low", "the points stay");
        body.GetProperty("reputation").GetProperty("activePoints").GetInt32().Should().Be(12);
    }

    [Fact]
    public async Task Action_Validation()
    {
        const string path = "/admin/users/farmer-1/reputation";

        await Error(Post(path, "{"), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Action("ban", until: T0.AddDays(1))), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Action("tag", "no", T0.AddDays(1))), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Action("tag", null, T0.AddDays(1))), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Action("tag")), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Action("tag", until: T0)), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Action("tag", until: T0.AddDays(91))), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Action("untag", until: T0.AddDays(91))), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Action("restrict", until: T0.AddDays(366))), 400, "REPUTATION_VALIDATION");
        await Error(Post(path, Action("restrict", until: T0.AddSeconds(-1))), 400, "REPUTATION_VALIDATION");
        await Error(Post("/admin/users/buyer-1/reputation", Action("tag", until: T0.AddDays(1))), 400, "REPUTATION_VALIDATION");
        await Error(Post("/admin/users/ghost/reputation", Action("tag", until: T0.AddDays(1))), 404, "REPUTATION_NOT_FOUND");
        _repo.Users["trucker-1"].DeletionState = "in_progress";
        await Error(Post("/admin/users/trucker-1/reputation", Action("tag", until: T0.AddDays(1))), 404, "REPUTATION_NOT_FOUND");

        _repo.Committed.Should().BeEmpty();
        _auditRows.Should().BeEmpty();
        // The limits themselves are allowed.
        await Ok(Post(path, Action("tag", until: T0.AddDays(90))));
        _clock.Now = T0.AddMilliseconds(1);
        await Ok(Post(path, Action("restrict", until: T0.AddDays(365))));
    }

    [Fact]
    public async Task TwoAdmins_RestrictingAtOnce_OnlyOneRestrictionIsWritten()
    {
        _repo.BeforeCommit = async _ =>
        {
            var other = await Service().ApplyAdminActionAsync(
                "farmer-1", "restrict", "Other admin's reason.", T0.AddDays(7), "admin-2", new TestLambdaLogger());
            other.Outcome.Should().Be(ReputationAdminOutcome.Applied);
        };

        await Error(Post("/admin/users/farmer-1/reputation", Action("restrict", until: T0.AddDays(60))), 409, "REPUTATION_ALREADY_RESTRICTED");

        _repo.RowsOf("farmer-1").Where(r => r.Code == ReputationCodes.Restriction).Should().ContainSingle()
            .Which.By.Should().Be("admin-2");
    }

    [Fact]
    public async Task ACopyThatFails_DoesNotFailTheAction_AndTheHealMarkerStays()
    {
        _copies.RefreshOwnerRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<OwnerReputationFields>())
            .Returns<Task<int>>(_ => throw new AmazonDynamoDBException("throttled"));

        await Ok(Post("/admin/users/farmer-1/reputation", Action("restrict", until: T0.AddDays(60))));

        _repo.RowsOf("farmer-1").Should().Contain(r => r.Code == ReputationCodes.Heal, "the daily job retries the copy");
        ((TestLambdaLogger)_ctx.Logger).Buffer.ToString().Should().Contain("REPUTATION_COPY_FAILED step=admin_restrict");
        _auditRows.Should().ContainSingle();
    }

    // ===== FUP-163: a suspend reaches the listings in the same request =====

    [Theory]
    [InlineData("suspend", true, "restricted")]
    [InlineData("unsuspend", false, "good")]
    public async Task SuspendAndUnsuspend_RefreshTheCopiesAtOnce(string action, bool suspended, string tier)
    {
        _repo.Users["farmer-1"].Suspended = suspended;
        if (!suspended)
        {
            // The stored state of a suspended account, before the unsuspend.
            _repo.Users["farmer-1"].Map = new ReputationMapWrite
            {
                Aggregate = new ReputationAggregate { Tier = "restricted" },
                Version = 1,
                UpdatedAt = T0.AddDays(-1)
            };
        }

        var fn = new AdminFunction(_dynamoDb, UsersTable)
        {
            ReputationPenaltiesEnabled = true,
            ReputationEventsTable = "reputation-events",
            ReputationRepo = _repo,
            ReputationSvc = Service()
        };

        var response = await fn.FunctionHandler(
            Post("/admin/users/farmer-1/action", JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = action, ["reason"] = "fraud" })),
            _ctx);

        response.StatusCode.Should().Be(200, response.Body);
        _copied.Should().ContainSingle().Which.Should().Match<(string User, OwnerReputationFields Fields)>(c =>
            c.User == "farmer-1" && c.Fields.Tier == tier);
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be(tier);
        _repo.RowsOf("farmer-1").Should().ContainSingle(r => r.Code == ReputationCodes.TierChanged && r.ToTier == tier);
    }

    [Fact]
    public async Task Suspend_WithTheFlagOff_TouchesNoReputation()
    {
        _repo.Users["farmer-1"].Suspended = true;
        var fn = new AdminFunction(_dynamoDb, UsersTable)
        {
            ReputationPenaltiesEnabled = false,
            ReputationEventsTable = "reputation-events",
            ReputationRepo = _repo,
            ReputationSvc = Service()
        };

        var response = await fn.FunctionHandler(
            Post("/admin/users/farmer-1/action", JsonSerializer.Serialize(new Dictionary<string, string> { ["action"] = "suspend", ["reason"] = "fraud" })),
            _ctx);

        response.StatusCode.Should().Be(200, response.Body);
        _copied.Should().BeEmpty();
        _repo.Committed.Should().BeEmpty();
    }

    // ===== helpers =====

    private static APIGatewayProxyRequest Get(string path, Dictionary<string, string>? query = null)
        => Request("GET", path, null, query: query);

    private static APIGatewayProxyRequest Post(string path, string body) => Request("POST", path, body);

    private static APIGatewayProxyRequest Request(
        string method, string path, string? body, string? groups = "admin", string? role = null, Dictionary<string, string>? query = null)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" } };
        if (groups != null)
        {
            authorizer["cognito:groups"] = groups;
        }

        if (role != null)
        {
            authorizer["custom:role"] = role;
        }

        return new APIGatewayProxyRequest
        {
            HttpMethod = method,
            Path = path,
            Body = body,
            QueryStringParameters = query,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer }
        };
    }

    private async Task<JsonElement> Ok(APIGatewayProxyRequest request, bool penalties = true)
    {
        var response = await Function(penalties).FunctionHandler(request, _ctx);
        response.StatusCode.Should().Be(200, response.Body);
        return JsonDocument.Parse(response.Body).RootElement.Clone();
    }

    private async Task Error(APIGatewayProxyRequest request, int status, string code, bool penalties = true)
    {
        var response = await Function(penalties).FunctionHandler(request, _ctx);
        response.StatusCode.Should().Be(status, response.Body);
        JsonDocument.Parse(response.Body).RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be(code);
    }
}
