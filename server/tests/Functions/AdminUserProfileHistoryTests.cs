using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// FUP-317 (user-entity.md 2.3.0 section 14): <c>GET /admin/users/{id}</c> lists the user's profile edits from their
/// <c>profile-{userId}</c> transaction-audit rows, newest first, at most 50, with whether older ones exist. A failed
/// read leaves the history out and still answers the user.
/// </summary>
public class AdminUserProfileHistoryTests
{
    private const string UsersTable = "users-test";
    private const string TxAuditTable = "transaction-audit-test";

    private readonly IAmazonDynamoDB _ddb = Substitute.For<IAmazonDynamoDB>();
    private readonly TestLambdaContext _context = new();

    public AdminUserProfileHistoryTests()
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new GetItemResponse
            {
                Item = ci.ArgAt<GetItemRequest>(0).Key["userId"].S == "farmer-1" ? UserRow() : new Dictionary<string, AttributeValue>()
            }));
        HistoryRows();
    }

    private static Dictionary<string, AttributeValue> UserRow() => new()
    {
        { "userId", new AttributeValue { S = "farmer-1" } },
        { "phoneNumber", new AttributeValue { S = "+251911000001" } },
        { "displayName", new AttributeValue { S = "Abebe K." } },
        { "role", new AttributeValue { S = "farmer" } },
        { "woreda", new AttributeValue { S = "Meki" } },
        { "isPhoneVerified", new AttributeValue { BOOL = true } },
        { "createdAt", new AttributeValue { S = "2026-01-01T00:00:00Z" } },
        { "updatedAt", new AttributeValue { S = "2026-11-13T00:00:00Z" } },
        { "version", new AttributeValue { N = "3" } }
    };

    /// <summary>A profile_edited row as <see cref="ProfileEditAudit.Entry"/> writes it.</summary>
    private static Dictionary<string, AttributeValue> EditRow(string timestamp, string metadata, string action = "profile_edited") => new()
    {
        { "auditId", new AttributeValue { S = $"txaudit-{timestamp}" } },
        { "transactionId", new AttributeValue { S = "profile-farmer-1" } },
        { "timestamp", new AttributeValue { S = timestamp } },
        { "entityType", new AttributeValue { S = "user" } },
        { "entityId", new AttributeValue { S = "farmer-1" } },
        { "action", new AttributeValue { S = action } },
        { "actor", new AttributeValue { S = "farmer-1" } },
        { "actorRole", new AttributeValue { S = "farmer" } },
        { "metadata", new AttributeValue { S = metadata } }
    };

    private static string Meta(params ProfileFieldChange[] changes)
        => JsonSerializer.Serialize(changes.ToList(), AppJsonContext.Default.ListProfileFieldChange);

    /// <summary>What the transaction-audit Query answers with (DynamoDB returns the page newest first).</summary>
    private void HistoryRows(params Dictionary<string, AttributeValue>[] rows)
        => _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var limit = ci.ArgAt<QueryRequest>(0).Limit;
                return Task.FromResult(new QueryResponse
                {
                    Items = rows.Take(limit).ToList(),
                    LastEvaluatedKey = rows.Length > limit ? rows[limit - 1] : new Dictionary<string, AttributeValue>()
                });
            });

    private Task<APIGatewayProxyResponse> GetAsync(string userId, string transactionAuditTable = TxAuditTable) =>
        new AdminFunction(_ddb, UsersTable, transactionAuditTable: transactionAuditTable).FunctionHandler(new APIGatewayProxyRequest
        {
            HttpMethod = "GET",
            Path = $"/admin/users/{userId}",
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
            }
        }, _context);

    private static JsonElement Json(APIGatewayProxyResponse response) => JsonDocument.Parse(response.Body).RootElement;

    private List<QueryRequest> Queries => _ddb.ReceivedCalls()
        .Select(c => c.GetArguments()[0]).OfType<QueryRequest>().ToList();

    [Fact]
    public async Task UserDetail_ListsTheProfileEdits_NewestFirst_WithTheOldAndNewValues()
    {
        HistoryRows(
            EditRow("2026-11-13T09:00:00.0000000Z", Meta(
                new ProfileFieldChange { Field = "displayName", From = "Abebe", To = "Abebe K." },
                new ProfileFieldChange { Field = "woreda", From = "Adama", To = "Meki" })),
            EditRow("2026-11-01T09:00:00.0000000Z", Meta(
                new ProfileFieldChange { Field = "displayName", To = "Abebe" })));

        var response = await GetAsync("farmer-1");

        response.StatusCode.Should().Be(200, response.Body);
        var query = Queries.Should().ContainSingle().Subject;
        query.TableName.Should().Be(TxAuditTable);
        query.IndexName.Should().BeNull();
        query.KeyConditionExpression.Should().Be("transactionId = :id");
        query.ExpressionAttributeValues[":id"].S.Should().Be("profile-farmer-1");
        query.ScanIndexForward.Should().BeFalse();
        query.Limit.Should().Be(51);

        var body = Json(response);
        body.GetProperty("profileHistoryMore").GetBoolean().Should().BeFalse();
        var history = body.GetProperty("profileHistory").EnumerateArray().ToList();
        history.Should().HaveCount(2);
        history[0].GetProperty("editedAt").GetString().Should().Be("2026-11-13T09:00:00.0000000Z");
        history[0].GetProperty("actor").GetString().Should().Be("farmer-1");
        history[0].GetProperty("actorRole").GetString().Should().Be("farmer");
        var changes = history[0].GetProperty("changes").EnumerateArray().ToList();
        changes.Select(c => c.GetProperty("field").GetString()).Should().Equal("displayName", "woreda");
        changes[0].GetProperty("from").GetString().Should().Be("Abebe");
        changes[0].GetProperty("to").GetString().Should().Be("Abebe K.");
        changes[1].GetProperty("from").GetString().Should().Be("Adama");
        changes[1].GetProperty("to").GetString().Should().Be("Meki");
        // No old value: left out.
        history[1].GetProperty("changes")[0].TryGetProperty("from", out _).Should().BeFalse();
        // The user itself is unchanged.
        body.GetProperty("user").GetProperty("displayName").GetString().Should().Be("Abebe K.");
    }

    [Fact]
    public async Task UserDetail_WithMoreThan50Edits_ListsTheNewest50_AndSaysThereAreMore()
    {
        HistoryRows(Enumerable.Range(0, 60)
            .Select(i => EditRow(
                new DateTime(2026, 11, 13, 0, 0, 0, DateTimeKind.Utc).AddMinutes(-i).ToString("o"),
                Meta(new ProfileFieldChange { Field = "displayName", From = $"Name {i + 1}", To = $"Name {i}" })))
            .ToArray());

        var response = await GetAsync("farmer-1");

        var body = Json(response);
        body.GetProperty("profileHistory").GetArrayLength().Should().Be(50);
        body.GetProperty("profileHistoryMore").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task UserDetail_WithNoEdits_HasAnEmptyHistory()
    {
        var response = await GetAsync("farmer-1");

        var body = Json(response);
        body.GetProperty("profileHistory").GetArrayLength().Should().Be(0);
        body.GetProperty("profileHistoryMore").GetBoolean().Should().BeFalse();
        body.TryGetProperty("profileHistoryHidden", out _).Should().BeFalse();
    }

    [Fact]
    public async Task UserDetail_AnUnreadableRow_IsListedWithoutChanges_AndAnotherAction_IsLeftOut()
    {
        HistoryRows(
            EditRow("2026-11-13T09:00:00.0000000Z", "not json"),
            EditRow("2026-11-12T09:00:00.0000000Z", Meta(new ProfileFieldChange { Field = "woreda", From = "Adama", To = "Meki" }),
                action: "user_suspend"));

        var response = await GetAsync("farmer-1");

        var history = Json(response).GetProperty("profileHistory").EnumerateArray().ToList();
        history.Should().ContainSingle();
        history[0].GetProperty("editedAt").GetString().Should().Be("2026-11-13T09:00:00.0000000Z");
        history[0].GetProperty("changes").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task UserDetail_WithoutTheAuditTable_LeavesTheHistoryOut()
    {
        var response = await GetAsync("farmer-1", transactionAuditTable: "");

        response.StatusCode.Should().Be(200);
        Json(response).TryGetProperty("profileHistory", out _).Should().BeFalse();
        Json(response).TryGetProperty("profileHistoryMore", out _).Should().BeFalse();
        Json(response).TryGetProperty("profileHistoryHidden", out _).Should().BeFalse();
        Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task UserDetail_AFailedHistoryRead_StillAnswersTheUser_WithoutTheHistory()
    {
        _ddb.QueryAsync(Arg.Any<QueryRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ProvisionedThroughputExceededException("throttled"));

        var response = await GetAsync("farmer-1");

        response.StatusCode.Should().Be(200, response.Body);
        Json(response).GetProperty("user").GetProperty("userId").GetString().Should().Be("farmer-1");
        Json(response).TryGetProperty("profileHistory", out _).Should().BeFalse();
        Json(response).TryGetProperty("profileHistoryHidden", out _).Should().BeFalse();
        ((TestLambdaLogger)_context.Logger).Buffer.ToString().Should()
            .Contain("FUP-317: profile history read failed (non-fatal): [ProvisionedThroughputExceededException]");
    }

    [Theory]
    [InlineData("in_progress")]
    [InlineData("completed")]
    public async Task ADeletingOrDeletedAccount_ShowsNoOldNames(string deletionState)
    {
        _ddb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue>(UserRow()) { ["deletionState"] = new AttributeValue { S = deletionState } }
            });
        HistoryRows(EditRow("2026-11-13T09:00:00.0000000Z",
            Meta(new ProfileFieldChange { Field = "displayName", From = "Abebe", To = "Abebe K." })));

        var response = await GetAsync("farmer-1");

        response.StatusCode.Should().Be(200, response.Body);
        Json(response).TryGetProperty("profileHistory", out _).Should().BeFalse();
        Json(response).TryGetProperty("profileHistoryMore", out _).Should().BeFalse();
        // Said, so the admin app tells it apart from a read that failed.
        Json(response).GetProperty("profileHistoryHidden").GetBoolean().Should().BeTrue();
        response.Body.Should().NotContain("\"Abebe\"");
        Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task UnknownUser_Is404_AndReadsNoHistory()
    {
        var response = await GetAsync("nobody");

        response.StatusCode.Should().Be(404);
        Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task TheAuditLog_ListsAProfileEdit_WithoutTheNames_AndKeepsOtherRowsMetadata()
    {
        _ddb.ScanAsync(Arg.Any<ScanRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ScanResponse
            {
                Items =
                [
                    EditRow("2026-11-13T09:00:00.0000000Z",
                        Meta(new ProfileFieldChange { Field = "displayName", From = "Abebe Original", To = "Abebe Renamed" })),
                    EditRow("2026-11-12T09:00:00.0000000Z", "reported by phone", action: "user_suspend")
                ],
                LastEvaluatedKey = new Dictionary<string, AttributeValue>()
            });

        var response = await new AdminFunction(_ddb, UsersTable, transactionAuditTable: TxAuditTable).FunctionHandler(
            new APIGatewayProxyRequest
            {
                HttpMethod = "GET",
                Path = "/admin/audit",
                RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
                }
            }, _context);

        response.StatusCode.Should().Be(200, response.Body);
        var entries = Json(response).GetProperty("entries").EnumerateArray().ToList();
        var edit = entries.Single(e => e.GetProperty("action").GetString() == "profile_edited");
        edit.GetProperty("entityId").GetString().Should().Be("farmer-1");
        edit.TryGetProperty("metadata", out _).Should().BeFalse();
        response.Body.Should().NotContain("Abebe Original").And.NotContain("Abebe Renamed");
        entries.Single(e => e.GetProperty("action").GetString() == "user_suspend")
            .GetProperty("metadata").GetString().Should().Be("reported by phone");
    }

    // ──────────────────────────────────────────────
    // The row itself (ProfileEditAudit)
    // ──────────────────────────────────────────────

    private static UserDto Stored() => new()
    {
        UserId = "buyer-1",
        PhoneNumber = "+251911000003",
        DisplayName = "Almaz",
        Role = "buyer",
        Woreda = "",
        BuyerType = "company",
        CompanyName = "Old Traders",
        IsPhoneVerified = true,
        CreatedAt = "2026-01-01T00:00:00Z",
        UpdatedAt = "2026-01-01T00:00:00Z",
        Version = 2
    };

    [Fact]
    public void Entry_IsKeyedOnTheUsersProfilePartition_WithTheChangedFieldsInOrder()
    {
        var changed = new Dictionary<string, string> { ["companyName"] = "Meki Traders", ["woreda"] = "Meki" };
        var ctx = new AuditContext { Actor = "buyer-1", ActorRole = "buyer", IpAddress = "196.188.0.1", CorrelationId = "c-1" };

        var row = ProfileEditAudit.Entry(Stored(), changed, ctx, "2026-11-13T10:00:00.0000000Z");

        row["transactionId"].Should().Be("profile-buyer-1");
        row["timestamp"].Should().Be("2026-11-13T10:00:00.0000000Z");
        row["entityType"].Should().Be("user");
        row["entityId"].Should().Be("buyer-1");
        row["action"].Should().Be("profile_edited");
        row["actor"].Should().Be("buyer-1");
        row["actorRole"].Should().Be("buyer");
        row["ipAddress"].Should().Be("196.188.0.1");
        row["correlationId"].Should().Be("c-1");
        ((string)row["auditId"]).Should().StartWith("txaudit-profile-buyer-1-profile_edited-");
        row.Should().NotContainKeys("fromState", "toState");
        // The registration's field order, whatever the order sent; a stored empty woreda has no old value.
        row["metadata"].Should().Be(
            "[{\"field\":\"woreda\",\"to\":\"Meki\"},{\"field\":\"companyName\",\"from\":\"Old Traders\",\"to\":\"Meki Traders\"}]");
    }

    [Fact]
    public void FromRow_ReadsTheRowBack()
    {
        var changed = new Dictionary<string, string> { ["displayName"] = "Almaz T." };
        var row = ProfileEditAudit.Entry(Stored(), changed, new AuditContext { Actor = "buyer-1", ActorRole = "buyer" },
            "2026-11-13T10:00:00.0000000Z");

        var dto = ProfileEditAudit.FromRow(row);

        dto.EditedAt.Should().Be("2026-11-13T10:00:00.0000000Z");
        dto.Actor.Should().Be("buyer-1");
        dto.ActorRole.Should().Be("buyer");
        dto.Changes.Should().Equal(new ProfileFieldChange { Field = "displayName", From = "Almaz", To = "Almaz T." });
    }
}
