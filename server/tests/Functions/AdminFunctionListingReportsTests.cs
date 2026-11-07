using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-67: the admin queue of listing reports (listing-report-api.md 1.0.0 section 2). The list reads one
/// status at a time with what each reported listing holds now; the review decides every open report of a
/// listing once and writes the audit row.
/// </summary>
public class AdminFunctionListingReportsTests
{
    private const string UsersTable = "users-test";
    private const string CropsTable = "crops-test";
    private const string TripsTable = "trips-test";
    private const string TxAuditTable = "transaction-audit-test";
    private const string CropId = "crop-1";
    private const string TripId = "trip-1";

    private static readonly DateTime Now = new(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly IListingReportRepository _reports = Substitute.For<IListingReportRepository>();
    private readonly TestLambdaContext _context = new();

    public AdminFunctionListingReportsTests()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse { Items = new List<Dictionary<string, AttributeValue>>() });
        _ddb.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == CropsTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["cropId"] = S(CropId),
                        ["status"] = S("available"),
                        ["farmerName"] = S("Abebe"),
                        ["cropType"] = S("teff"),
                        ["description"] = S("White teff"),
                        ["audioNoteRef"] = S("audio/farmer-1/crop-1.m4a")
                    }
                }
            });
        _ddb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>()).Returns(new PutItemResponse());
        _reports.ListByStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((new List<ListingReportRow>(), (Dictionary<string, AttributeValue>?)null));
        _reports.TryReviewAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(true);
    }

    // ===== GET /admin/listing-reports =====

    [Fact]
    public async Task List_ReadsTheOpenQueue_WithEachListingOnce()
    {
        _reports.ListByStatusAsync("open", 50, null).Returns((new List<ListingReportRow>
        {
            Row(CropId, "buyer-1", "crop", "contact_info"),
            Row(CropId, "buyer-2", "crop", "scam"),
            Row(TripId, "buyer-1", "trip", "misleading")
        }, (Dictionary<string, AttributeValue>?)null));

        var response = await Build().FunctionHandler(Request("GET", "/admin/listing-reports"), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var body = JsonDocument.Parse(response.Body).RootElement;
        body.GetProperty("reports").GetArrayLength().Should().Be(3);
        var first = body.GetProperty("reports")[0];
        first.GetProperty("listingId").GetString().Should().Be(CropId);
        first.GetProperty("reporterId").GetString().Should().Be("buyer-1");
        first.GetProperty("ownerId").GetString().Should().Be("owner-crop-1");
        first.GetProperty("reason").GetString().Should().Be("contact_info");
        first.GetProperty("status").GetString().Should().Be("open");

        var listings = body.GetProperty("listings").EnumerateArray().ToList();
        listings.Should().HaveCount(2);
        var crop = listings.Single(l => l.GetProperty("listingType").GetString() == "crop");
        crop.GetProperty("found").GetBoolean().Should().BeTrue();
        crop.GetProperty("cropType").GetString().Should().Be("teff");
        crop.GetProperty("description").GetString().Should().Be("White teff");
        crop.GetProperty("audioNoteRef").GetString().Should().Be("audio/farmer-1/crop-1.m4a");
        crop.GetProperty("ownerName").GetString().Should().Be("Abebe");
        // The trips table answers no row: the trip is shown as gone.
        listings.Single(l => l.GetProperty("listingType").GetString() == "trip")
            .GetProperty("found").GetBoolean().Should().BeFalse();
        body.TryGetProperty("nextToken", out _).Should().BeFalse();
        await _ddb.Received(1).QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == CropsTable), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_ReadsATrip_WithItsRoute()
    {
        _ddb.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == TripsTable), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["tripId"] = S(TripId),
                        ["status"] = S("open"),
                        ["truckerName"] = S("Kebede"),
                        ["fromWoreda"] = S("Adama"),
                        ["toWoreda"] = S("Addis Ababa"),
                        ["departureDate"] = S("2026-11-09")
                    }
                }
            });
        _reports.ListByStatusAsync("open", 50, null).Returns((new List<ListingReportRow>
        {
            Row(TripId, "buyer-1", "trip", "misleading")
        }, (Dictionary<string, AttributeValue>?)null));

        var response = await Build().FunctionHandler(Request("GET", "/admin/listing-reports"), _context);

        var trip = JsonDocument.Parse(response.Body).RootElement.GetProperty("listings")[0];
        trip.GetProperty("found").GetBoolean().Should().BeTrue();
        trip.GetProperty("fromWoreda").GetString().Should().Be("Adama");
        trip.GetProperty("toWoreda").GetString().Should().Be("Addis Ababa");
        trip.GetProperty("departureDate").GetString().Should().Be("2026-11-09");
        trip.GetProperty("ownerName").GetString().Should().Be("Kebede");
    }

    [Fact]
    public async Task List_AFailedListingRead_ShowsTheListingAsNotFound()
    {
        _ddb.QueryAsync(Arg.Is<QueryRequest>(q => q.TableName == CropsTable), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonDynamoDBException("throttled"));
        _reports.ListByStatusAsync("open", 50, null).Returns((new List<ListingReportRow>
        {
            Row(CropId, "buyer-1", "crop", "scam")
        }, (Dictionary<string, AttributeValue>?)null));

        var response = await Build().FunctionHandler(Request("GET", "/admin/listing-reports"), _context);

        response.StatusCode.Should().Be(200);
        JsonDocument.Parse(response.Body).RootElement.GetProperty("listings")[0]
            .GetProperty("found").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData("dismissed")]
    [InlineData("actioned")]
    public async Task List_ADecidedStatus_IsRead(string status)
    {
        var response = await Build().FunctionHandler(
            Request("GET", "/admin/listing-reports", query: new() { ["status"] = status, ["limit"] = "20" }), _context);

        response.StatusCode.Should().Be(200);
        await _reports.Received(1).ListByStatusAsync(status, 20, null);
    }

    [Fact]
    public async Task List_AnUnknownStatus_Is400()
    {
        var response = await Build().FunctionHandler(
            Request("GET", "/admin/listing-reports", query: new() { ["status"] = "pending" }), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_005");
    }

    [Fact]
    public async Task List_ATamperedToken_Is400()
    {
        var response = await Build().FunctionHandler(
            Request("GET", "/admin/listing-reports", query: new() { ["nextToken"] = "not-a-token" }), _context);

        response.StatusCode.Should().Be(400);
    }

    [Fact]
    public async Task List_AFullPage_CarriesATokenThatResumesIt()
    {
        var lastKey = new Dictionary<string, AttributeValue>
        {
            ["listingId"] = S(CropId),
            ["reporterId"] = S("buyer-9"),
            ["status"] = S("open"),
            ["createdAt"] = S("2026-11-07T09:00:00.000Z")
        };
        _reports.ListByStatusAsync("open", 50, null)
            .Returns((new List<ListingReportRow> { Row(CropId, "buyer-9", "crop", "scam") },
                (Dictionary<string, AttributeValue>?)lastKey));

        var first = await Build().FunctionHandler(Request("GET", "/admin/listing-reports"), _context);
        var token = JsonDocument.Parse(first.Body).RootElement.GetProperty("nextToken").GetString();
        token.Should().NotBeNullOrEmpty();

        await Build().FunctionHandler(
            Request("GET", "/admin/listing-reports", query: new() { ["nextToken"] = token! }), _context);

        await _reports.Received(1).ListByStatusAsync("open", 50,
            Arg.Is<Dictionary<string, AttributeValue>?>(k => k != null && k["reporterId"].S == "buyer-9"));
    }

    [Fact]
    public async Task WithoutTheTable_BothRoutesAre503()
    {
        var function = new AdminFunction(_ddb, UsersTable, "", "", "", "", TxAuditTable, tripsTable: TripsTable, cropsTable: CropsTable);

        var list = await function.FunctionHandler(Request("GET", "/admin/listing-reports"), _context);
        var review = await function.FunctionHandler(
            Request("POST", $"/admin/listing-reports/{CropId}/review", Body("dismissed")), _context);

        list.StatusCode.Should().Be(503);
        review.StatusCode.Should().Be(503);
        list.Body.Should().Contain("ADMIN_004");
    }

    [Fact]
    public async Task ANonAdmin_IsForbidden()
    {
        var request = Request("GET", "/admin/listing-reports");
        request.RequestContext.Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "buyer-1" }, { "custom:role", "buyer" } };

        var response = await Build().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(403);
        await _reports.DidNotReceive().ListByStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>());
    }

    // ===== POST /admin/listing-reports/{listingId}/review =====

    [Fact]
    public async Task Review_DecidesEveryOpenReportOfTheListing_AndAuditsIt()
    {
        _reports.ListByListingAsync(CropId).Returns(new List<ListingReportRow>
        {
            Row(CropId, "buyer-1", "crop", "scam"),
            Row(CropId, "buyer-2", "crop", "contact_info"),
            Row(CropId, "buyer-3", "crop", "other") with { Status = "dismissed" }
        });

        var response = await Build().FunctionHandler(
            Request("POST", $"/admin/listing-reports/{CropId}/review", Body("actioned", "Owner suspended")), _context);

        response.StatusCode.Should().Be(200, response.Body);
        var body = JsonDocument.Parse(response.Body).RootElement;
        body.GetProperty("listingId").GetString().Should().Be(CropId);
        body.GetProperty("decision").GetString().Should().Be("actioned");
        body.GetProperty("reviewed").GetInt32().Should().Be(2);
        body.GetProperty("reviewedAt").GetString().Should().Be("2026-11-07T12:00:00.000Z");
        await _reports.Received(1).TryReviewAsync(CropId, "buyer-1", "actioned", "admin-1", "2026-11-07T12:00:00.000Z", "Owner suspended");
        await _reports.Received(1).TryReviewAsync(CropId, "buyer-2", "actioned", "admin-1", "2026-11-07T12:00:00.000Z", "Owner suspended");
        await _reports.DidNotReceive().TryReviewAsync(CropId, "buyer-3", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _ddb.Received(1).PutItemAsync(Arg.Is<PutItemRequest>(r =>
            r.TableName == TxAuditTable
            && r.Item["action"].S == "listing_report_actioned"
            && r.Item["entityId"].S == CropId
            && r.Item["transactionId"].S == $"listing-report-{CropId}"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Review_AReportDecidedMeanwhile_IsNotCounted()
    {
        _reports.ListByListingAsync(CropId).Returns(new List<ListingReportRow>
        {
            Row(CropId, "buyer-1", "crop", "scam"),
            Row(CropId, "buyer-2", "crop", "scam")
        });
        _reports.TryReviewAsync(CropId, "buyer-2", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>())
            .Returns(false);

        var response = await Build().FunctionHandler(
            Request("POST", $"/admin/listing-reports/{CropId}/review", Body("dismissed")), _context);

        JsonDocument.Parse(response.Body).RootElement.GetProperty("reviewed").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task Review_NothingOpen_AnswersZero_AndWritesNoAuditRow()
    {
        _reports.ListByListingAsync(CropId).Returns(new List<ListingReportRow>());

        var response = await Build().FunctionHandler(
            Request("POST", $"/admin/listing-reports/{CropId}/review", Body("dismissed")), _context);

        response.StatusCode.Should().Be(200);
        JsonDocument.Parse(response.Body).RootElement.GetProperty("reviewed").GetInt32().Should().Be(0);
        await _ddb.DidNotReceive().PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("open")]
    [InlineData("approve")]
    [InlineData(null)]
    public async Task Review_AnUnknownDecision_Is400(string? decision)
    {
        var response = await Build().FunctionHandler(
            Request("POST", $"/admin/listing-reports/{CropId}/review", Body(decision)), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_009");
        await _reports.DidNotReceive().ListByListingAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Review_ANoteOverTheLimit_Is400()
    {
        var response = await Build().FunctionHandler(
            Request("POST", $"/admin/listing-reports/{CropId}/review", Body("dismissed", new string('x', 501))), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_005");
    }

    [Fact]
    public async Task Review_InvalidJson_Is400()
    {
        var response = await Build().FunctionHandler(
            Request("POST", $"/admin/listing-reports/{CropId}/review", "{not json"), _context);

        response.StatusCode.Should().Be(400);
    }

    // ===== Helpers =====

    private AdminFunction Build() =>
        new(_ddb, UsersTable, "", "", "", "", TxAuditTable, tripsTable: TripsTable, cropsTable: CropsTable)
        {
            ListingReportRepo = _reports,
            Clock = () => Now
        };

    private static ListingReportRow Row(string listingId, string reporterId, string listingType, string reason) =>
        new(listingId, reporterId, listingType, $"owner-{listingId}", reason, ListingReportRules.StatusOpen,
            "2026-11-07T08:00:00.000Z", 1_800_000_000);

    private static string Body(string? decision, string? note = null) =>
        JsonSerializer.Serialize(new { decision, note });

    private static AttributeValue S(string value) => new() { S = value };

    private static APIGatewayProxyRequest Request(
        string method, string path, string? body = null, Dictionary<string, string>? query = null) =>
        new()
        {
            HttpMethod = method,
            Path = path,
            Body = body,
            QueryStringParameters = query,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "cognito:groups", "admin" } }
            }
        };
}
