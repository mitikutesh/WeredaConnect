using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// SIM-56 against real DynamoDB (LocalStack in CI): the profile edit through the <c>/sync</c>
/// <c>user</c> entity and <c>GET /user/profile</c>, on a users table with the deployed indexes
/// (<c>PhoneIndex</c>, <c>RoleWaredaIndex</c>, <c>FaydaIdIndex</c>), so a write that gives an
/// index key a wrong type is refused here as it would be on AWS. The collection's shared users
/// table has no indexes, so this class makes its own.
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
    private UserRepository Users => new(Ddb, Table);

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

        for (var i = 0; i < 50; i++)
        {
            if ((await Ddb.DescribeTableAsync(Table)).Table.TableStatus == TableStatus.ACTIVE)
            {
                return;
            }

            await Task.Delay(100);
        }
    }

    public async Task DisposeAsync()
    {
        if (_created)
        {
            await Ddb.DeleteTableAsync(Table);
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
}
