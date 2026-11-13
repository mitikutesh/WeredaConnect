using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// SIM-56 against real DynamoDB (LocalStack in CI): the profile edit through the <c>/sync</c>
/// <c>user</c> entity and <c>GET /user/profile</c>, on a users table with the deployed indexes
/// (<c>PhoneIndex</c>, <c>RoleWaredaIndex</c>, <c>FaydaIdIndex</c>), so a write that gives an
/// index key a wrong type is refused here as it would be on AWS. The collection's shared users
/// table has no indexes, so this class makes its own. FUP-317: with its own transaction-audit
/// table too, where an accepted edit of the profile text writes one row in the same transaction.
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UserProfileEditDynamoDbTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];
    private readonly TestLambdaContext _ctx = new();
    private bool _created;

    public UserProfileEditDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private string Table => $"users-profile-{_run}";
    private string AuditTable => $"txaudit-profile-{_run}";
    private UserRepository Users => new(Ddb, Table, AuditTable);

    public async Task InitializeAsync()
    {
        if (LocalDynamoDbEnvironment.Skip != null)
        {
            return;
        }

        await Ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = Table,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema = [new KeySchemaElement("userId", KeyType.HASH)],
            AttributeDefinitions =
            [
                new AttributeDefinition("userId", ScalarAttributeType.S),
                new AttributeDefinition("phoneNumber", ScalarAttributeType.S),
                new AttributeDefinition("role", ScalarAttributeType.S),
                new AttributeDefinition("woreda", ScalarAttributeType.S),
                new AttributeDefinition("faydaId", ScalarAttributeType.S)
            ],
            GlobalSecondaryIndexes =
            [
                Index("PhoneIndex", "phoneNumber", null),
                Index("RoleWaredaIndex", "role", "woreda"),
                Index("FaydaIdIndex", "faydaId", null)
            ]
        });
        _created = true;

        // FUP-317: the transaction-audit table as deployed (database-stack.ts).
        await Ddb.CreateTableAsync(new CreateTableRequest
        {
            TableName = AuditTable,
            BillingMode = BillingMode.PAY_PER_REQUEST,
            KeySchema =
            [
                new KeySchemaElement("transactionId", KeyType.HASH),
                new KeySchemaElement("timestamp", KeyType.RANGE)
            ],
            AttributeDefinitions =
            [
                new AttributeDefinition("transactionId", ScalarAttributeType.S),
                new AttributeDefinition("timestamp", ScalarAttributeType.S),
                new AttributeDefinition("entityId", ScalarAttributeType.S)
            ],
            GlobalSecondaryIndexes = [Index("EntityIndex", "entityId", "timestamp")]
        });

        foreach (var table in new[] { Table, AuditTable })
        {
            for (var i = 0; i < 50; i++)
            {
                if ((await Ddb.DescribeTableAsync(table)).Table.TableStatus == TableStatus.ACTIVE)
                {
                    break;
                }

                await Task.Delay(100);
            }
        }
    }

    public async Task DisposeAsync()
    {
        if (_created)
        {
            await Ddb.DeleteTableAsync(Table);
            await Ddb.DeleteTableAsync(AuditTable);
        }
    }

    private static GlobalSecondaryIndex Index(string name, string hash, string? range)
    {
        var schema = new List<KeySchemaElement> { new(hash, KeyType.HASH) };
        if (range != null)
        {
            schema.Add(new KeySchemaElement(range, KeyType.RANGE));
        }

        return new GlobalSecondaryIndex
        {
            IndexName = name,
            KeySchema = schema,
            Projection = new Projection { ProjectionType = ProjectionType.ALL }
        };
    }

    private string Id(string who) => $"user-sim56-{_run}-{who}";

    /// <summary>A registered row as RegisterFunction writes it; without a Fayda ID unless given.</summary>
    private async Task<UserDto> SeedAsync(string who, string role, string? buyerType = null, string? companyName = null,
        string? faydaId = null, string displayName = "Abebe Kebede", string woreda = "Adama")
    {
        var user = new UserDto
        {
            UserId = Id(who),
            PhoneNumber = $"+2519{Math.Abs(Id(who).GetHashCode()) % 100000000:D8}",
            DisplayName = displayName,
            Role = role,
            Woreda = woreda,
            FaydaId = faydaId,
            FaydaVerified = faydaId != null,
            BuyerType = buyerType,
            CompanyName = companyName,
            IsPhoneVerified = true,
            CreatedAt = "2026-11-01T00:00:00.000Z",
            UpdatedAt = "2026-11-01T00:00:00.000Z",
            Version = 1
        };
        (await Users.CreateUserAsync(user)).IsSuccess.Should().BeTrue();
        return user;
    }

    private SyncFunction Sync()
    {
        var meta = Substitute.For<ISyncMetadataRepository>();
        meta.IdempotencyKeyExistsAsync(Arg.Any<string>()).Returns(false);
        return new SyncFunction(meta, Substitute.For<ICropRepository>(), Ddb, "trips", "deliveries", userRepo: Users);
    }

    private static APIGatewayProxyRequest Authorized(string userId, string role) => new()
    {
        RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
        {
            Authorizer = new APIGatewayCustomAuthorizerContext { { "userId", userId }, { "custom:role", role } }
        }
    };

    private async Task<string> EditAsync(string userId, string role, Dictionary<string, object> data)
    {
        var request = Authorized(userId, role);
        request.HttpMethod = "POST";
        request.Path = "/sync";
        request.Body = JsonSerializer.Serialize(new SyncRequest
        {
            SyncBatchId = "b1",
            ClientId = "d1",
            Operations =
            [
                new SyncOperationRequest
                {
                    OperationId = "op-1",
                    IdempotencyKey = $"{userId}:user:{userId}:update:{Guid.NewGuid():N}",
                    EntityType = "user",
                    EntityId = userId,
                    Action = "update",
                    Data = data,
                    Timestamp = "2026-11-12T10:00:00Z"
                }
            ]
        }, Json);

        var response = await Sync().FunctionHandler(request, _ctx);
        response.StatusCode.Should().Be(200);
        return response.Body;
    }

    /// <summary>The user's profile edit rows (FUP-317), oldest first.</summary>
    private async Task<List<Dictionary<string, AttributeValue>>> AuditRowsAsync(string userId)
        => (await Ddb.QueryAsync(new QueryRequest
        {
            TableName = AuditTable,
            KeyConditionExpression = "transactionId = :id",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":id"] = new() { S = $"profile-{userId}" } },
            ConsistentRead = true
        })).Items;

    private static List<ProfileFieldChange> Changes(Dictionary<string, AttributeValue> row)
        => JsonSerializer.Deserialize<List<ProfileFieldChange>>(row["metadata"].S, Json)!;

    private async Task<Dictionary<string, AttributeValue>> RowAsync(string userId)
        => (await Ddb.GetItemAsync(new GetItemRequest
        {
            TableName = Table,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = userId } },
            ConsistentRead = true
        })).Item;

    [LocalDynamoDbFact]
    public async Task FarmerWithoutFaydaId_EditsNameAndWoreda_OnlyThoseChange()
    {
        var seeded = await SeedAsync("farmer", "farmer");

        var body = await EditAsync(seeded.UserId, "farmer", new Dictionary<string, object>
        {
            ["displayName"] = "  Abebe K. ",
            ["woreda"] = "Meki",
            // Never read from the payload.
            ["role"] = "buyer",
            ["phoneNumber"] = "+251911000000",
            ["faydaVerified"] = true,
            ["suspended"] = true
        });

        // Before SIM-56 this write was refused: it set the FaydaIdIndex key to NULL.
        body.Should().Contain("\"status\":\"success\"", ((TestLambdaLogger)_ctx.Logger).Buffer.ToString());
        var row = await RowAsync(seeded.UserId);
        row.ContainsKey("faydaId").Should().BeFalse();
        row["displayName"].S.Should().Be("Abebe K.");
        row["woreda"].S.Should().Be("Meki");
        row["role"].S.Should().Be("farmer");
        row["phoneNumber"].S.Should().Be(seeded.PhoneNumber);
        row["faydaVerified"].BOOL.Should().BeFalse();
        row.ContainsKey("suspended").Should().BeFalse();
        row.ContainsKey("companyName").Should().BeFalse();
        row["version"].N.Should().Be("2");
    }

    [LocalDynamoDbFact]
    public async Task CompanyBuyer_EditsCompanyName_AndTheReadReturnsIt()
    {
        var seeded = await SeedAsync("buyer", "buyer", "company", "Old Traders", faydaId: $"FAYDA-{_run}");

        var body = await EditAsync(seeded.UserId, "buyer", new Dictionary<string, object>
        {
            ["companyName"] = "Meki Grain Traders",
            ["preferredLanguage"] = "om"
        });

        body.Should().Contain("\"status\":\"success\"");
        var row = await RowAsync(seeded.UserId);
        row["companyName"].S.Should().Be("Meki Grain Traders");
        row["buyerType"].S.Should().Be("company");
        row["preferredLanguage"].S.Should().Be("om");
        row["faydaId"].S.Should().Be($"FAYDA-{_run}");

        var read = Authorized(seeded.UserId, "buyer");
        read.HttpMethod = "GET";
        read.Path = "/user/profile";
        var response = await Sync().FunctionHandler(read, _ctx);

        response.StatusCode.Should().Be(200);
        using var doc = JsonDocument.Parse(response.Body);
        var user = doc.RootElement.GetProperty("user");
        user.GetProperty("companyName").GetString().Should().Be("Meki Grain Traders");
        user.GetProperty("version").GetInt32().Should().Be(2);
        // The profile view: never the Fayda ID, the install ids or the phone.
        user.TryGetProperty("faydaId", out _).Should().BeFalse();
        user.TryGetProperty("knownDevices", out _).Should().BeFalse();
        user.TryGetProperty("phoneNumber", out _).Should().BeFalse();
        response.Body.Should().NotContain($"FAYDA-{_run}");
    }

    [LocalDynamoDbFact]
    public async Task RowWithoutWoreda_NameOnlyEdit_IsSaved_AndStaysOutOfTheRoleIndex()
    {
        // FUP-162: farmers and truckers without a woreda exist. FromItem reads the missing woreda as "",
        // and DynamoDB refuses an empty RoleWaredaIndex sort key, so the write removes it instead.
        var userId = Id("no-woreda");
        await Ddb.PutItemAsync(new PutItemRequest
        {
            TableName = Table,
            Item = new Dictionary<string, AttributeValue>
            {
                ["userId"] = new() { S = userId },
                ["phoneNumber"] = new() { S = $"+2519{Math.Abs(userId.GetHashCode()) % 100000000:D8}" },
                ["displayName"] = new() { S = "Abebe" },
                ["role"] = new() { S = "farmer" },
                ["preferredLanguage"] = new() { S = "am" },
                ["isPhoneVerified"] = new() { BOOL = true },
                ["createdAt"] = new() { S = "2026-01-01T00:00:00.000Z" },
                ["updatedAt"] = new() { S = "2026-01-01T00:00:00.000Z" },
                ["version"] = new() { N = "1" }
            }
        });

        var body = await EditAsync(userId, "farmer", new Dictionary<string, object> { ["displayName"] = "Abebe K." });

        body.Should().Contain("\"status\":\"success\"", ((TestLambdaLogger)_ctx.Logger).Buffer.ToString());
        var row = await RowAsync(userId);
        row["displayName"].S.Should().Be("Abebe K.");
        row.ContainsKey("woreda").Should().BeFalse();
        row["version"].N.Should().Be("2");
    }

    [LocalDynamoDbFact]
    public async Task PhoneNumberInTheName_IsRefused_AndNothingIsWritten()
    {
        var seeded = await SeedAsync("trucker", "trucker");

        var body = await EditAsync(seeded.UserId, "trucker", new Dictionary<string, object>
        {
            ["displayName"] = "Call 0911 223 344",
            ["woreda"] = "Ziway",
            ["preferredLanguage"] = "en"
        });

        body.Should().Contain(
            "AUTH_027: Remove the phone number from displayName. Contact opens after payment is held in escrow.");
        var row = await RowAsync(seeded.UserId);
        row["displayName"].S.Should().Be("Abebe Kebede");
        row["woreda"].S.Should().Be("Adama");
        row["version"].N.Should().Be("1");
    }

    // ── FUP-317: the audit row of a profile edit ──────────────────────

    [LocalDynamoDbFact]
    public async Task AnAcceptedEdit_LeavesOneAuditRow_ARefusedNoOpOrLanguageOnlyEditNone()
    {
        var seeded = await SeedAsync("audited", "farmer");

        (await EditAsync(seeded.UserId, "farmer", new Dictionary<string, object>
        {
            ["displayName"] = "Abebe K.",
            ["woreda"] = "Meki"
        })).Should().Contain("\"status\":\"success\"", ((TestLambdaLogger)_ctx.Logger).Buffer.ToString());

        var rows = await AuditRowsAsync(seeded.UserId);
        var row = rows.Should().ContainSingle().Subject;
        row["action"].S.Should().Be("profile_edited");
        row["entityType"].S.Should().Be("user");
        row["entityId"].S.Should().Be(seeded.UserId);
        row["actor"].S.Should().Be(seeded.UserId);
        row["actorRole"].S.Should().Be("farmer");
        Changes(row).Should().Equal(
            new ProfileFieldChange { Field = "displayName", From = "Abebe Kebede", To = "Abebe K." },
            new ProfileFieldChange { Field = "woreda", From = "Adama", To = "Meki" });

        // Refused (a phone number), unchanged, and a language-only change: no row.
        (await EditAsync(seeded.UserId, "farmer", new Dictionary<string, object> { ["displayName"] = "Call 0911 223 344" }))
            .Should().Contain("AUTH_027");
        (await EditAsync(seeded.UserId, "farmer", new Dictionary<string, object> { ["displayName"] = "Abebe K." }))
            .Should().Contain("\"status\":\"success\"");
        (await EditAsync(seeded.UserId, "farmer", new Dictionary<string, object> { ["preferredLanguage"] = "en" }))
            .Should().Contain("\"status\":\"success\"");
        (await AuditRowsAsync(seeded.UserId)).Should().ContainSingle();
        (await RowAsync(seeded.UserId))["preferredLanguage"].S.Should().Be("en");

        // A second edit: a second row.
        (await EditAsync(seeded.UserId, "farmer", new Dictionary<string, object> { ["woreda"] = "Ziway" }))
            .Should().Contain("\"status\":\"success\"");
        rows = await AuditRowsAsync(seeded.UserId);
        rows.Should().HaveCount(2);
        Changes(rows[1]).Should().Equal(new ProfileFieldChange { Field = "woreda", From = "Meki", To = "Ziway" });
    }

    [LocalDynamoDbFact]
    public async Task AVersionRace_WritesNeitherTheUserNorTheRow()
    {
        var seeded = await SeedAsync("raced", "farmer");
        var entry = ProfileEditAudit.Entry(
            seeded, new Dictionary<string, string> { ["displayName"] = "Abebe K." },
            new AuditContext { Actor = seeded.UserId, ActorRole = "farmer" }, DateTime.UtcNow.ToString("o"));

        // Read at version 1, but another write bumped it first.
        (await Users.UpdateUserAsync(seeded with { PreferredLanguage = "om" }, 1)).IsSuccess.Should().BeTrue();
        var result = await Users.UpdateUserWithAuditAsync(seeded with { DisplayName = "Abebe K." }, 1, entry);

        result.IsSuccess.Should().BeFalse();
        result.Error!.Code.Should().Be("AUTH_019");
        (await RowAsync(seeded.UserId))["displayName"].S.Should().Be("Abebe Kebede");
        (await AuditRowsAsync(seeded.UserId)).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task ADeletingAccount_TakesNeitherTheEditNorTheRow()
    {
        var seeded = await SeedAsync("deleting", "farmer");
        await Ddb.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = Table,
            Key = new Dictionary<string, AttributeValue> { ["userId"] = new() { S = seeded.UserId } },
            UpdateExpression = "SET deletionState = :s",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue> { [":s"] = new() { S = "in_progress" } }
        });
        var entry = ProfileEditAudit.Entry(
            seeded, new Dictionary<string, string> { ["displayName"] = "Abebe K." },
            new AuditContext { Actor = seeded.UserId }, DateTime.UtcNow.ToString("o"));

        var result = await Users.UpdateUserWithAuditAsync(seeded with { DisplayName = "Abebe K." }, 1, entry);

        result.Error!.Code.Should().Be("AUTH_019");
        (await AuditRowsAsync(seeded.UserId)).Should().BeEmpty();
    }

    [LocalDynamoDbFact]
    public async Task TheAdminUserDetail_ListsTheNameHistory_NewestFirst()
    {
        var seeded = await SeedAsync("history", "farmer");
        await EditAsync(seeded.UserId, "farmer", new Dictionary<string, object> { ["displayName"] = "Abebe K." });
        await EditAsync(seeded.UserId, "farmer", new Dictionary<string, object> { ["displayName"] = "Abebe Kebede Tola" });

        var response = await new AdminFunction(Ddb, Table, transactionAuditTable: AuditTable).FunctionHandler(
            new APIGatewayProxyRequest
            {
                HttpMethod = "GET",
                Path = $"/admin/users/{seeded.UserId}",
                RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
                {
                    Authorizer = new APIGatewayCustomAuthorizerContext { { "sub", "admin-1" }, { "role", "admin" } }
                }
            }, _ctx);

        response.StatusCode.Should().Be(200, response.Body);
        using var doc = JsonDocument.Parse(response.Body);
        doc.RootElement.GetProperty("profileHistoryMore").GetBoolean().Should().BeFalse();
        var history = doc.RootElement.GetProperty("profileHistory").EnumerateArray().ToList();
        history.Should().HaveCount(2);
        string Change(JsonElement edit, string key) => edit.GetProperty("changes")[0].GetProperty(key).GetString()!;
        Change(history[0], "from").Should().Be("Abebe K.");
        Change(history[0], "to").Should().Be("Abebe Kebede Tola");
        Change(history[1], "from").Should().Be("Abebe Kebede");
        Change(history[1], "to").Should().Be("Abebe K.");
        string.CompareOrdinal(history[0].GetProperty("editedAt").GetString(), history[1].GetProperty("editedAt").GetString())
            .Should().BePositive();
    }
}
