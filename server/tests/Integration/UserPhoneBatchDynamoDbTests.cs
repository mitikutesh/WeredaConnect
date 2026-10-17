using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Integration;

/// <summary>
/// ESC-48 against real DynamoDB (LocalStack in CI): the order read resolves the parties' phone
/// numbers with one batched read of the users table (BatchGetItem, 100 keys per request).
/// </summary>
[Collection(LocalDynamoDbCollection.Name)]
[Trait("Category", "Integration")]
public sealed class UserPhoneBatchDynamoDbTests
{
    private readonly LocalDynamoDbFixture _db;
    private readonly string _run = Guid.NewGuid().ToString("N")[..8];

    public UserPhoneBatchDynamoDbTests(LocalDynamoDbFixture db)
    {
        _db = db;
    }

    private IAmazonDynamoDB Ddb => _db.Client;
    private UserRepository Users => new(Ddb, _db.Users);

    private string UserId(int i) => $"user-esc48-{_run}-{i}";

    private static string Phone(int i) => $"+2519{i:D8}";

    private async Task SeedAsync(int i, bool withPhone = true)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["userId"] = new() { S = UserId(i) },
            ["displayName"] = new() { S = $"User {i}" },
            ["role"] = new() { S = "buyer" }
        };
        if (withPhone)
        {
            item["phoneNumber"] = new AttributeValue { S = Phone(i) };
        }

        await Ddb.PutItemAsync(new PutItemRequest { TableName = _db.Users, Item = item });
    }

    [LocalDynamoDbFact]
    public async Task GetPhoneNumbers_ReadsEveryUserAcrossBatchesOf100()
    {
        for (var i = 0; i < 105; i++)
        {
            await SeedAsync(i);
        }

        var ids = Enumerable.Range(0, 105).Select(UserId).ToList();
        var phones = await Users.GetPhoneNumbersAsync(ids);

        phones.Should().HaveCount(105);
        phones[UserId(0)].Should().Be(Phone(0));
        phones[UserId(104)].Should().Be(Phone(104));
    }

    [LocalDynamoDbFact]
    public async Task GetPhoneNumbers_LeavesOutUnknownUsersAndUsersWithoutAPhone()
    {
        await SeedAsync(1);
        await SeedAsync(2, withPhone: false);

        var phones = await Users.GetPhoneNumbersAsync(new[] { UserId(1), UserId(2), UserId(3), UserId(1), "" });

        phones.Should().ContainSingle();
        phones[UserId(1)].Should().Be(Phone(1));
    }

    [LocalDynamoDbFact]
    public async Task GetPhoneNumbers_NoIds_ReadsNothing()
    {
        var phones = await Users.GetPhoneNumbersAsync(Array.Empty<string>());

        phones.Should().BeEmpty();
    }
}
