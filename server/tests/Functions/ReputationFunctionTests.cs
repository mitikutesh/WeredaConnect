using System.Text.Json;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// ESC-29: the reputation endpoints (reputation-api.md sections 2 to 6): every row of the
/// visibility tables for the three roles, the read-time recompute, the event list and its paging,
/// the appeal window, idempotency and closed cases, the photo URL, and the flags.
/// </summary>
public class ReputationFunctionTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 9, 0, 0, DateTimeKind.Utc);

    private readonly FakeReputationRepository _repo = new();
    private readonly ManualClock _clock = new(T0);
    private readonly ITransactionAuditRepository _audit = Substitute.For<ITransactionAuditRepository>();
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly TestLambdaContext _ctx = new();
    private readonly List<Dictionary<string, object>> _auditRows = new();

    public ReputationFunctionTests()
    {
        _repo.AddUser("farmer-1");
        _repo.AddUser("farmer-2");
        _repo.AddUser("trucker-1", "trucker");
        _repo.AddUser("buyer-1", "buyer");
        _repo.AddUser("buyer-2", "buyer");
        _audit.AppendAsync(Arg.Do<Dictionary<string, object>>(row => _auditRows.Add(row))).Returns(Task.CompletedTask);
        _s3.GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>()).Returns(call => $"https://uploads.test/{call.Arg<GetPreSignedUrlRequest>().Key}?sig");
        _s3.GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new GetObjectMetadataResponse());
    }

    private ReputationService Service()
        => new(_repo, ReputationPolicySettings.Defaults, Substitute.For<IFraudDetectionService>(), _clock, _ => Task.CompletedTask);

    private ReputationFunction Function(bool penalties = true, bool publicTags = true)
        => new(_repo, Service(), _audit, _s3, "uploads-bucket")
        {
            PenaltiesEnabled = penalties,
            PublicTags = publicTags,
            Clock = () => _clock.Now
        };

    private async Task Penalty(string user, string slot, DateTime at, string code = ReputationCodes.CancelledAfterAccept, string buyer = "buyer-1")
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
    }

    // ===== section 2: GET /reputation/me =====

    [Theory]
    [InlineData("farmer-1", "farmer")]
    [InlineData("trucker-1", "trucker")]
    public async Task Me_AFarmerOrTrucker_ReadsTheirFullReputation(string userId, string role)
    {
        _repo.Users[userId].RatingSum = 48;
        _repo.Users[userId].RatingCount = 10;
        await Penalty(userId, "CANCEL#o1", T0.AddDays(-2), ReputationCodes.LateCancel);

        var body = await Ok(Get("/reputation/me", userId, role));

        var r = body.GetProperty("reputation");
        r.GetProperty("userId").GetString().Should().Be(userId);
        r.GetProperty("role").GetString().Should().Be(role);
        r.GetProperty("tier").GetString().Should().Be("warning", "the own view shows the internal tier");
        r.GetProperty("activePoints").GetInt32().Should().Be(4);
        r.GetProperty("thresholds").GetProperty("low").GetInt32().Should().Be(6);
        r.GetProperty("thresholds").GetProperty("restricted").GetInt32().Should().Be(10);
        r.GetProperty("ratingAverage").GetDecimal().Should().Be(4.8m);
        r.GetProperty("ratingCount").GetInt32().Should().Be(10);
        r.GetProperty("ratingDeduction").GetDecimal().Should().Be(0.4m);
        r.GetProperty("effectiveRating").GetDecimal().Should().Be(4.4m);
        r.GetProperty("faultCancels90d").GetInt32().Should().Be(1);
        r.GetProperty("nextPointExpiryAt").GetString().Should().Be(ReputationPolicy.Iso(T0.AddDays(88)));
        r.GetProperty("penaltiesEnabled").GetBoolean().Should().BeTrue();
        r.GetProperty("asOf").GetString().Should().Be(ReputationPolicy.Iso(T0));
        r.TryGetProperty("restriction", out _).Should().BeFalse();
        r.TryGetProperty("reliabilityPct", out _).Should().BeFalse("below 3 orders");
    }

    [Fact]
    public async Task Me_ABuyer_IsForbidden()
        => await Error(Get("/reputation/me", "buyer-1", "buyer"), 403, "REPUTATION_FORBIDDEN");

    [Fact]
    public async Task Me_NoRoleClaim_IsForbidden()
        => await Error(Get("/reputation/me", "farmer-1", null), 403, "REPUTATION_FORBIDDEN");

    [Fact]
    public async Task Me_NoRow_OrADeletingAccount_IsNotFound()
    {
        await Error(Get("/reputation/me", "ghost", "farmer"), 404, "REPUTATION_NOT_FOUND");
        _repo.Users["farmer-1"].DeletionState = "in_progress";
        await Error(Get("/reputation/me", "farmer-1", "farmer"), 404, "REPUTATION_NOT_FOUND");
    }

    [Fact]
    public async Task Me_WithNoEvents_IsGood_AndWithTheFlagOff_SaysSo()
    {
        var r = (await Ok(Get("/reputation/me", "farmer-1", "farmer"), penalties: false)).GetProperty("reputation");

        r.GetProperty("tier").GetString().Should().Be("good");
        r.GetProperty("activePoints").GetInt32().Should().Be(0);
        r.GetProperty("penaltiesEnabled").GetBoolean().Should().BeFalse();
        r.TryGetProperty("nextPointExpiryAt", out _).Should().BeFalse();
        r.TryGetProperty("ratingAverage", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Me_WhileRestricted_ShowsTheRestriction_AndNeverAnEndedOne()
    {
        foreach (var n in Enumerable.Range(1, 4))
        {
            await Penalty("farmer-1", $"CANCEL#o{n}", T0.AddHours(-n));
        }

        var r = (await Ok(Get("/reputation/me", "farmer-1", "farmer"))).GetProperty("reputation");
        r.GetProperty("tier").GetString().Should().Be("restricted");
        r.GetProperty("restriction").GetProperty("cause").GetString().Should().Be("points");
        r.GetProperty("restriction").TryGetProperty("until", out _).Should().BeTrue();

        // 15 days on the daily job has not run: the read recomputes from the events.
        _clock.Now = T0.AddDays(15);
        var later = (await Ok(Get("/reputation/me", "farmer-1", "farmer"))).GetProperty("reputation");
        later.GetProperty("tier").GetString().Should().Be("low");
        later.TryGetProperty("restriction", out _).Should().BeFalse();
        _repo.Users["farmer-1"].Map!.Aggregate.Tier.Should().Be("restricted", "a read writes nothing");

        // After the 90 days no point is shown either.
        _clock.Now = T0.AddDays(91);
        var expired = (await Ok(Get("/reputation/me", "farmer-1", "farmer"))).GetProperty("reputation");
        expired.GetProperty("activePoints").GetInt32().Should().Be(0);
        expired.GetProperty("tier").GetString().Should().Be("good");
    }

    // ===== section 3: GET /reputation/{userId} =====

    [Theory]
    [InlineData("farmer-1", "farmer")]
    [InlineData("trucker-1", "trucker")]
    public async Task Public_ABuyer_ReadsTheSummaryOfAFarmerOrTrucker_NeverThePoints(string target, string targetRole)
    {
        _repo.Users[target].RatingSum = 48;
        _repo.Users[target].RatingCount = 10;
        await Penalty(target, "CANCEL#o1", T0.AddDays(-2), ReputationCodes.LateCancel);
        await Penalty(target, "CANCEL#o2", T0.AddDays(-1));

        var raw = await OkText(Get($"/reputation/{target}", "buyer-1", "buyer"));

        var r = JsonDocument.Parse(raw).RootElement.GetProperty("reputation");
        r.GetProperty("role").GetString().Should().Be(targetRole);
        r.GetProperty("tier").GetString().Should().Be("low");
        r.GetProperty("effectiveRating").GetDecimal().Should().Be(4.1m);
        r.GetProperty("ratingAverage").GetDecimal().Should().Be(4.8m);
        r.GetProperty("completedOrders90d").GetInt32().Should().Be(0);
        r.GetProperty("totalOrders90d").GetInt32().Should().Be(2);
        foreach (var hidden in new[] { "activePoints", "thresholds", "restriction", "override", "nextPointExpiryAt", "faultCancels90d", "noShows90d", "ratingDeduction", "penaltiesEnabled" })
        {
            raw.Should().NotContain($"\"{hidden}\"");
        }
    }

    [Fact]
    public async Task Public_ARestrictedParty_ReadsAsLow_WithNoDates()
    {
        foreach (var n in Enumerable.Range(1, 4))
        {
            await Penalty("farmer-1", $"CANCEL#o{n}", T0.AddHours(-n));
        }

        var raw = await OkText(Get("/reputation/farmer-1", "buyer-1", "buyer"));

        JsonDocument.Parse(raw).RootElement.GetProperty("reputation").GetProperty("tier").GetString().Should().Be("low");
        raw.Should().NotContain("restricted").And.NotContain("until");
    }

    [Fact]
    public async Task Public_TagsOff_OnlyTheRawRatingStays()
    {
        _repo.Users["farmer-1"].RatingSum = 9;
        _repo.Users["farmer-1"].RatingCount = 2;
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-2));

        var r = (await Ok(Get("/reputation/farmer-1", "buyer-1", "buyer"), publicTags: false)).GetProperty("reputation");

        r.GetProperty("ratingAverage").GetDecimal().Should().Be(4.5m);
        r.GetProperty("ratingCount").GetInt32().Should().Be(2);
        foreach (var hidden in new[] { "tier", "effectiveRating", "reliabilityPct", "completedOrders90d", "totalOrders90d" })
        {
            r.TryGetProperty(hidden, out _).Should().BeFalse(hidden);
        }
    }

    [Theory]
    [InlineData("buyer-2")]
    [InlineData("ghost")]
    public async Task Public_ABuyer_AskingAboutABuyerOrAMissingRow_IsNotFound(string target)
        => await Error(Get($"/reputation/{target}", "buyer-1", "buyer"), 404, "REPUTATION_NOT_FOUND");

    [Fact]
    public async Task Public_ABuyer_AskingAboutADeletingAccount_IsNotFound()
    {
        _repo.Users["farmer-1"].DeletionState = "completed";
        await Error(Get("/reputation/farmer-1", "buyer-1", "buyer"), 404, "REPUTATION_NOT_FOUND");
    }

    [Theory]
    [InlineData("farmer-1", "farmer")]
    [InlineData("trucker-1", "trucker")]
    public async Task Public_AFarmerOrTrucker_ReadsHowBuyersSeeThem(string userId, string role)
    {
        var r = (await Ok(Get($"/reputation/{userId}", userId, role))).GetProperty("reputation");

        r.GetProperty("tier").GetString().Should().Be("good");
        r.TryGetProperty("activePoints", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("farmer-1", "farmer", "farmer-2")]
    [InlineData("farmer-1", "farmer", "trucker-1")]
    [InlineData("trucker-1", "trucker", "farmer-1")]
    [InlineData("farmer-1", "farmer", "buyer-1")]
    [InlineData("farmer-1", "farmer", "ghost")]
    [InlineData("admin-1", "admin", "farmer-1")]
    public async Task Public_AFarmerOrTrucker_NeverReadsAnyoneElse_AndLearnsNothingAboutTheId(string caller, string role, string target)
    {
        await Error(Get($"/reputation/{target}", caller, role), 403, "REPUTATION_FORBIDDEN");
    }

    [Fact]
    public async Task Unauthenticated_Is401()
    {
        var response = await Function().FunctionHandler(new APIGatewayProxyRequest { HttpMethod = "GET", Path = "/reputation/me" }, _ctx);
        response.StatusCode.Should().Be(401);
    }

    // ===== section 4: GET /reputation/me/events =====

    [Fact]
    public async Task Events_NewestFirst_WithoutSystemRows_BuyerIdsOrPlainCompletions()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-3), ReputationCodes.LateCancel);
        await Penalty("farmer-1", "REQUEST#o2", T0.AddDays(-2), ReputationCodes.DeclinedRequest, "buyer-2");
        await Penalty("farmer-1", "COMPLETED#o3", T0.AddDays(-1), ReputationCodes.OrderCompleted);

        var raw = await OkText(Get("/reputation/me/events", "farmer-1", "farmer"));

        var events = JsonDocument.Parse(raw).RootElement.GetProperty("events").EnumerateArray().ToList();
        events.Select(e => e.GetProperty("eventId").GetString()).Should().Equal("REQUEST#o2", "CANCEL#o1");
        raw.Should().NotContain("buyer-").And.NotContain("basePoints").And.NotContain("TIER#").And.NotContain("HEAL#");
        var cancel = events[1];
        cancel.GetProperty("code").GetString().Should().Be("LATE_CANCEL");
        cancel.GetProperty("points").GetInt32().Should().Be(4);
        cancel.GetProperty("remainingPoints").GetInt32().Should().Be(4);
        cancel.GetProperty("status").GetString().Should().Be("active");
        cancel.GetProperty("orderId").GetString().Should().Be("o1");
        cancel.GetProperty("appeal").GetProperty("status").GetString().Should().Be("none");
        cancel.GetProperty("appeal").GetProperty("canAppeal").GetBoolean().Should().BeTrue();
        cancel.GetProperty("appeal").GetProperty("deadlineAt").GetString().Should().Be(ReputationPolicy.Iso(T0.AddDays(-3).AddSeconds(4).AddDays(7)));
    }

    [Fact]
    public async Task Events_AZeroPointPenalty_SaysWhy_AndCannotBeAppealed()
    {
        await Penalty("farmer-1", "REQUEST#o1", T0.AddDays(-2), ReputationCodes.DeclinedRequest);
        await Penalty("farmer-1", "REQUEST#o2", T0.AddDays(-1), ReputationCodes.DeclinedRequest);

        var events = (await Ok(Get("/reputation/me/events", "farmer-1", "farmer"))).GetProperty("events").EnumerateArray().ToList();

        var capped = events.Single(e => e.GetProperty("points").GetInt32() == 0);
        capped.GetProperty("zeroReason").GetString().Should().Be("pair_cap");
        capped.GetProperty("appeal").GetProperty("canAppeal").GetBoolean().Should().BeFalse();
        capped.GetProperty("appeal").TryGetProperty("deadlineAt", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Events_AnExpiredPoint_ReadsExpired_BeforeTheDailyJobWritesIt()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-100));

        var e = (await Ok(Get("/reputation/me/events", "farmer-1", "farmer"))).GetProperty("events")[0];

        e.GetProperty("status").GetString().Should().Be("expired");
        e.GetProperty("remainingPoints").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Events_ATripCancel_CarriesTheTripId()
    {
        await Penalty("trucker-1", "TRIP_CANCEL#trip-9", T0.AddDays(-1), ReputationCodes.TripCancelledWithBookings);

        var e = (await Ok(Get("/reputation/me/events", "trucker-1", "trucker"))).GetProperty("events")[0];

        e.GetProperty("tripId").GetString().Should().Be("trip-9");
        e.TryGetProperty("orderId", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Events_PageWithASignedToken_ThatOnlyItsOwnerCanUse()
    {
        foreach (var n in Enumerable.Range(1, 5))
        {
            await Penalty("farmer-1", $"REQUEST#o{n}", T0.AddDays(-n), ReputationCodes.DeclinedRequest, $"buyer-{n}");
        }

        var first = await Ok(Get("/reputation/me/events", "farmer-1", "farmer", query: new() { { "limit", "2" } }));
        first.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventId").GetString()).Should().Equal("REQUEST#o1", "REQUEST#o2");
        var token = first.GetProperty("nextToken").GetString()!;

        var second = await Ok(Get("/reputation/me/events", "farmer-1", "farmer", query: new() { { "limit", "2" }, { "nextToken", token } }));
        second.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("eventId").GetString()).Should().Equal("REQUEST#o3", "REQUEST#o4");

        var last = await Ok(Get("/reputation/me/events", "farmer-1", "farmer",
            query: new() { { "limit", "2" }, { "nextToken", second.GetProperty("nextToken").GetString()! } }));
        last.GetProperty("events").GetArrayLength().Should().Be(1);
        last.TryGetProperty("nextToken", out _).Should().BeFalse();

        await Error(Get("/reputation/me/events", "farmer-2", "farmer", query: new() { { "nextToken", token } }), 400, "REPUTATION_VALIDATION");
        await Error(Get("/reputation/me/events", "farmer-1", "farmer", query: new() { { "nextToken", token + "x" } }), 400, "REPUTATION_VALIDATION");
    }

    [Theory]
    [InlineData("0")]
    [InlineData("101")]
    [InlineData("abc")]
    public async Task Events_ABadLimit_IsRefused(string limit)
        => await Error(Get("/reputation/me/events", "farmer-1", "farmer", query: new() { { "limit", limit } }), 400, "REPUTATION_VALIDATION");

    [Fact]
    public async Task Events_ABuyer_IsForbidden()
        => await Error(Get("/reputation/me/events", "buyer-1", "buyer"), 403, "REPUTATION_FORBIDDEN");

    // ===== section 5: POST /reputation/appeals/photo-url =====

    [Fact]
    public async Task PhotoUrl_SignsAPutUnderTheCallersOwnPrefix()
    {
        GetPreSignedUrlRequest? signed = null;
        _s3.GetPreSignedURL(Arg.Do<GetPreSignedUrlRequest>(r => signed = r)).Returns("https://uploads.test/put");

        var body = await Ok(Post("/reputation/appeals/photo-url", "farmer-1", "farmer", """{"sizeBytes":148213,"contentType":"image/webp"}"""));

        var photoRef = body.GetProperty("photoRef").GetString()!;
        photoRef.Should().StartWith("uploads/appeals/farmer-1/").And.EndWith(".webp");
        ReputationFunction.IsOwnAppealPhoto(photoRef, "farmer-1").Should().BeTrue();
        body.GetProperty("expiresInSeconds").GetInt32().Should().Be(900);
        signed!.BucketName.Should().Be("uploads-bucket");
        signed.Verb.Should().Be(HttpVerb.PUT);
        signed.ContentType.Should().Be("image/webp");
        signed.Headers.ContentLength.Should().Be(148213);
        signed.Expires.Should().Be(T0.AddSeconds(900));
    }

    [Theory]
    [InlineData("""{"sizeBytes":0}""")]
    [InlineData("""{"sizeBytes":204801}""")]
    [InlineData("""{"contentType":"image/webp"}""")]
    [InlineData("""{"sizeBytes":100,"contentType":"image/gif"}""")]
    [InlineData("not json")]
    public async Task PhotoUrl_BadInput_IsRefused(string json)
        => await Error(Post("/reputation/appeals/photo-url", "farmer-1", "farmer", json), 400, "REPUTATION_VALIDATION");

    [Fact]
    public async Task PhotoUrl_Refusals_ABuyer_TheFlag_ASuspendedOrDeletingCaller()
    {
        const string json = """{"sizeBytes":100}""";
        await Error(Post("/reputation/appeals/photo-url", "buyer-1", "buyer", json), 403, "REPUTATION_FORBIDDEN");
        await Error(Post("/reputation/appeals/photo-url", "farmer-1", "farmer", json), 503, "REPUTATION_UNAVAILABLE", penalties: false);

        _repo.Users["farmer-1"].Suspended = true;
        await Error(Post("/reputation/appeals/photo-url", "farmer-1", "farmer", json), 403, "AUTH_025");

        _repo.Users["farmer-1"].DeletionState = "in_progress";
        await Error(Post("/reputation/appeals/photo-url", "farmer-1", "farmer", json), 410, "ACCOUNT_004");
        _s3.DidNotReceive().GetPreSignedURL(Arg.Any<GetPreSignedUrlRequest>());
    }

    // ===== section 6: POST /reputation/events/{eventId}/appeal =====

    private const string AppealPath = "/reputation/events/CANCEL%23o1/appeal";
    private const string Photo = "uploads/appeals/farmer-1/5b0c9a7e-2d41-4c6b-8f0e-1d2c3b4a5e6f.webp";

    private static string AppealJson(string text = "The road was closed by the flood.", string? photoRef = null)
        => JsonSerializer.Serialize(new Dictionary<string, string?> { { "text", text }, { "photoRef", photoRef } });

    [Theory]
    [InlineData("farmer-1", "farmer")]
    [InlineData("trucker-1", "trucker")]
    public async Task Appeal_StoresTheTextAndPhoto_OpensThePendingAppeal_AndAuditsWithoutTheText(string userId, string role)
    {
        await Penalty(userId, "CANCEL#o1", T0.AddDays(-1), ReputationCodes.LateCancel);
        var photo = Photo.Replace("farmer-1", userId);

        var body = await Ok(Post(AppealPath, userId, role, AppealJson("  The road was closed by the flood.  ", photo)));

        body.GetProperty("alreadyAppealed").GetBoolean().Should().BeFalse();
        body.GetProperty("appeal").GetProperty("status").GetString().Should().Be("pending");
        body.GetProperty("appeal").GetProperty("appealedAt").GetString().Should().Be(ReputationPolicy.Iso(T0));
        body.GetProperty("appeal").GetProperty("hasPhoto").GetBoolean().Should().BeTrue();
        body.GetProperty("event").GetProperty("appeal").GetProperty("canAppeal").GetBoolean().Should().BeFalse();
        body.GetProperty("event").GetProperty("points").GetInt32().Should().Be(4, "an appeal changes no point");

        var row = _repo.Row(userId, "CANCEL#o1");
        row.AppealText.Should().Be("The road was closed by the flood.");
        row.AppealPhotoRef.Should().Be(photo);
        row.AppealStatus.Should().Be("pending");
        row.AppealedAt.Should().Be(ReputationPolicy.Iso(T0));
        row.Status.Should().Be("active");

        var audit = _auditRows.Should().ContainSingle().Subject;
        audit["action"].Should().Be("reputation_appealed");
        audit["entityType"].Should().Be("user");
        audit["entityId"].Should().Be(userId);
        string.Join("|", audit.Values.Select(v => v?.ToString())).Should().Contain("CANCEL#o1").And.NotContain("flood");
    }

    [Fact]
    public async Task Appeal_TheSameRequestAgain_Is200AlreadyAppealed_AndADifferentTextIsRefused()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        await Ok(Post(AppealPath, "farmer-1", "farmer", AppealJson()));

        var again = await Ok(Post(AppealPath, "farmer-1", "farmer", AppealJson()));
        again.GetProperty("alreadyAppealed").GetBoolean().Should().BeTrue();
        _auditRows.Should().ContainSingle("the replay writes nothing");

        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson("Another story.")), 409, "REPUTATION_APPEAL_EXISTS");
        _repo.Row("farmer-1", "CANCEL#o1").AppealText.Should().Be("The road was closed by the flood.");
    }

    [Fact]
    public async Task Appeal_Window_IsSevenDaysFromRecording_ByTheServerClock()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-7));

        // Recorded 4 seconds after it occurred: still inside at exactly 7 days after the event.
        _clock.Now = T0.AddSeconds(4);
        var probe = (await Ok(Get("/reputation/me/events", "farmer-1", "farmer"))).GetProperty("events")[0];
        probe.GetProperty("appeal").GetProperty("canAppeal").GetBoolean().Should().BeTrue();

        _clock.Now = T0.AddSeconds(5);
        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson()), 409, "REPUTATION_APPEAL_CLOSED");
        _repo.Row("farmer-1", "CANCEL#o1").AppealText.Should().BeNull();
    }

    [Fact]
    public async Task Appeal_ClosedCases_ZeroPoints_Excused_Expired_NoFault()
    {
        await Penalty("farmer-1", "REQUEST#a", T0.AddDays(-2), ReputationCodes.DeclinedRequest);
        await Penalty("farmer-1", "REQUEST#b", T0.AddDays(-1), ReputationCodes.DeclinedRequest);
        var capped = _repo.RowsOf("farmer-1").Single(r => r.Code == ReputationCodes.DeclinedRequest && r.Points == 0).EventId;
        await Error(Post($"/reputation/events/{Uri.EscapeDataString(capped)}/appeal", "farmer-1", "farmer", AppealJson()), 409, "REPUTATION_APPEAL_CLOSED");

        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        _repo.Put(_repo.Row("farmer-1", "CANCEL#o1") with { Status = ReputationStatuses.Excused });
        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson()), 409, "REPUTATION_APPEAL_CLOSED");

        _repo.Put(_repo.Row("farmer-1", "CANCEL#o1") with { Status = ReputationStatuses.Active, PointsExpireAt = T0.AddDays(-1) });
        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson()), 409, "REPUTATION_APPEAL_CLOSED");

        await Penalty("farmer-1", "CANCEL#nf", T0.AddDays(-1), ReputationCodes.CancelledTransportFailed);
        await Error(Post("/reputation/events/CANCEL%23nf/appeal", "farmer-1", "farmer", AppealJson()), 409, "REPUTATION_APPEAL_CLOSED");
    }

    [Fact]
    public async Task Appeal_AnotherUsersEvent_ASystemRow_OrAnUnknownId_IsNotFound()
    {
        await Penalty("farmer-2", "CANCEL#o1", T0.AddDays(-1));
        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson()), 404, "REPUTATION_EVENT_NOT_FOUND");
        _repo.Row("farmer-2", "CANCEL#o1").AppealText.Should().BeNull();

        var tierRow = _repo.RowsOf("farmer-2").First(r => r.Code == ReputationCodes.TierChanged).EventId;
        await Error(Post($"/reputation/events/{Uri.EscapeDataString(tierRow)}/appeal", "farmer-2", "farmer", AppealJson()), 404, "REPUTATION_EVENT_NOT_FOUND");
    }

    [Fact]
    public async Task Appeal_ADeliveryOverdueReview_KeepsItsReviewStatusAndQueuePlace()
    {
        await Penalty("trucker-1", "OVERDUE#o1", T0.AddDays(-1), ReputationCodes.DeliveryOverdue);
        var queuedAt = _repo.Row("trucker-1", "OVERDUE#o1").AppealedAt;

        var body = await Ok(Post("/reputation/events/OVERDUE%23o1/appeal", "trucker-1", "trucker", AppealJson()));

        body.GetProperty("appeal").GetProperty("status").GetString().Should().Be("review");
        var row = _repo.Row("trucker-1", "OVERDUE#o1");
        row.AppealStatus.Should().Be("review");
        row.AppealedAt.Should().Be(queuedAt);
        row.AppealText.Should().NotBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Appeal_EmptyText_IsRefused(string text)
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson(text)), 400, "REPUTATION_VALIDATION");
    }

    [Fact]
    public async Task Appeal_TextOver500Characters_IsRefused_And500IsTaken()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson(new string('a', 501))), 400, "REPUTATION_VALIDATION");
        await Ok(Post(AppealPath, "farmer-1", "farmer", AppealJson(new string('a', 500))));
    }

    [Theory]
    [InlineData("uploads/appeals/farmer-2/5b0c9a7e.webp")]
    [InlineData("uploads/deliveries/del-1/pickup.webp")]
    [InlineData("uploads/appeals/farmer-1/../farmer-2/x.webp")]
    [InlineData("uploads/appeals/farmer-1/")]
    public async Task Appeal_APhotoOutsideTheCallersPrefix_IsRefused_BeforeAnyRead(string photoRef)
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));

        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson(photoRef: photoRef)), 400, "REPUTATION_VALIDATION");

        await _s3.DidNotReceive().GetObjectMetadataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        _repo.Row("farmer-1", "CANCEL#o1").AppealText.Should().BeNull();
    }

    [Fact]
    public async Task Appeal_APhotoThatWasNeverUploaded_IsRefused()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        _s3.GetObjectMetadataAsync("uploads-bucket", Photo, Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonS3Exception("missing") { StatusCode = System.Net.HttpStatusCode.NotFound });

        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson(photoRef: Photo)), 400, "REPUTATION_VALIDATION");
        _repo.Row("farmer-1", "CANCEL#o1").AppealText.Should().BeNull();
    }

    [Fact]
    public async Task Appeal_LosingARaceToTheSameAppeal_AnswersAlreadyAppealed()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        _repo.BeforeAppeal = () => _repo.Put(_repo.Row("farmer-1", "CANCEL#o1") with
        {
            AppealText = "The road was closed by the flood.", AppealStatus = "pending", AppealedAt = ReputationPolicy.Iso(T0)
        });

        var body = await Ok(Post(AppealPath, "farmer-1", "farmer", AppealJson()));

        body.GetProperty("alreadyAppealed").GetBoolean().Should().BeTrue();
        _auditRows.Should().BeEmpty();
    }

    [Fact]
    public async Task Appeal_Refusals_ABuyer_TheFlag_ASuspendedCaller()
    {
        await Penalty("farmer-1", "CANCEL#o1", T0.AddDays(-1));
        await Error(Post(AppealPath, "buyer-1", "buyer", AppealJson()), 403, "REPUTATION_FORBIDDEN");
        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson()), 503, "REPUTATION_UNAVAILABLE", penalties: false);
        _repo.Users["farmer-1"].Suspended = true;
        await Error(Post(AppealPath, "farmer-1", "farmer", AppealJson()), 403, "AUTH_025");
        _repo.Row("farmer-1", "CANCEL#o1").AppealText.Should().BeNull();
    }

    [Fact]
    public async Task AFailedRead_Is500_WithTheReputationCode()
    {
        _repo.ReadFailure = new InvalidOperationException("down");
        await Error(Get("/reputation/me", "farmer-1", "farmer"), 500, "REPUTATION_SERVER_ERROR");
    }

    // ===== helpers =====

    private static APIGatewayProxyRequest Get(string path, string userId, string? role, Dictionary<string, string>? query = null)
        => Request("GET", path, userId, role, null, query);

    private static APIGatewayProxyRequest Post(string path, string userId, string? role, string body)
        => Request("POST", path, userId, role, body, null);

    private static APIGatewayProxyRequest Request(
        string method, string path, string userId, string? role, string? body, Dictionary<string, string>? query)
    {
        var authorizer = new APIGatewayCustomAuthorizerContext { { "sub", userId } };
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

    private async Task<string> OkText(APIGatewayProxyRequest request, bool penalties = true, bool publicTags = true)
    {
        var response = await Function(penalties, publicTags).FunctionHandler(request, _ctx);
        response.StatusCode.Should().Be(200, response.Body);
        return response.Body;
    }

    private async Task<JsonElement> Ok(APIGatewayProxyRequest request, bool penalties = true, bool publicTags = true)
        => JsonDocument.Parse(await OkText(request, penalties, publicTags)).RootElement.Clone();

    private async Task Error(APIGatewayProxyRequest request, int status, string code, bool penalties = true)
    {
        var response = await Function(penalties).FunctionHandler(request, _ctx);
        response.StatusCode.Should().Be(status, response.Body);
        JsonDocument.Parse(response.Body).RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be(code);
    }
}
