using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Data;

/// <summary>
/// FUP-67: the listing-reports table. One row per listing and reporter (conditional put), the status index
/// read as a queue, and a review that only moves an open row. These tests assert the request shapes.
/// </summary>
public class ListingReportRepositoryTests
{
    private const string Table = "test-listing-reports";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly ListingReportRepository _repo;

    public ListingReportRepositoryTests()
    {
        _repo = new ListingReportRepository(_dynamoDb, Table);
    }

    private static ListingReportRow Report() => new(
        "crop-1", "buyer-1", "crop", "farmer-1", "contact_info", "open", "2026-11-07T09:30:15.123Z", 1_778_146_215);

    [Fact]
    public async Task TryCreate_WritesTheRow_ConditionalOnTheReporter()
    {
        PutItemRequest? sent = null;
        _dynamoDb.PutItemAsync(Arg.Do<PutItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new PutItemResponse());

        var created = await _repo.TryCreateAsync(Report());

        created.Should().BeTrue();
        sent!.TableName.Should().Be(Table);
        sent.ConditionExpression.Should().Be("attribute_not_exists(reporterId)");
        sent.Item["listingId"].S.Should().Be("crop-1");
        sent.Item["reporterId"].S.Should().Be("buyer-1");
        sent.Item["listingType"].S.Should().Be("crop");
        sent.Item["ownerId"].S.Should().Be("farmer-1");
        sent.Item["reason"].S.Should().Be("contact_info");
        sent.Item["status"].S.Should().Be("open");
        sent.Item["createdAt"].S.Should().Be("2026-11-07T09:30:15.123Z");
        sent.Item["expiresAt"].N.Should().Be("1778146215");
        sent.Item.Should().HaveCount(8);
    }

    [Fact]
    public async Task TryCreate_AnExistingReport_IsFalse()
    {
        _dynamoDb.PutItemAsync(Arg.Any<PutItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("exists"));

        (await _repo.TryCreateAsync(Report())).Should().BeFalse();
    }

    [Fact]
    public async Task Exists_ReadsTheKeyConsistently()
    {
        GetItemRequest? sent = null;
        _dynamoDb.GetItemAsync(Arg.Do<GetItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse
            {
                Item = new Dictionary<string, AttributeValue> { ["listingId"] = new() { S = "crop-1" } }
            });

        var exists = await _repo.ExistsAsync("crop-1", "buyer-1");

        exists.Should().BeTrue();
        sent!.ConsistentRead.Should().BeTrue();
        sent.Key["listingId"].S.Should().Be("crop-1");
        sent.Key["reporterId"].S.Should().Be("buyer-1");
    }

    [Fact]
    public async Task Exists_NoItem_IsFalse()
    {
        _dynamoDb.GetItemAsync(Arg.Any<GetItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new GetItemResponse());

        (await _repo.ExistsAsync("crop-1", "buyer-1")).Should().BeFalse();
    }

    [Theory]
    [InlineData("open", true)]
    [InlineData("dismissed", false)]
    public async Task ListByStatus_ReadsTheIndex_OpenOldestFirst(string status, bool forward)
    {
        QueryRequest? sent = null;
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new QueryResponse
            {
                Items = new List<Dictionary<string, AttributeValue>>
                {
                    new()
                    {
                        ["listingId"] = new() { S = "crop-1" },
                        ["reporterId"] = new() { S = "buyer-1" },
                        ["listingType"] = new() { S = "crop" },
                        ["ownerId"] = new() { S = "farmer-1" },
                        ["reason"] = new() { S = "scam" },
                        ["status"] = new() { S = status },
                        ["createdAt"] = new() { S = "2026-11-07T09:30:15.123Z" },
                        ["expiresAt"] = new() { N = "1778146215" },
                        ["reviewNote"] = new() { S = "checked" }
                    },
                    // A row without its keys is skipped.
                    new() { ["status"] = new() { S = status } }
                },
                LastEvaluatedKey = new Dictionary<string, AttributeValue> { ["listingId"] = new() { S = "crop-1" } }
            });

        var (rows, lastKey) = await _repo.ListByStatusAsync(status, 25, null);

        sent!.IndexName.Should().Be("StatusCreatedAtIndex");
        sent.ScanIndexForward.Should().Be(forward);
        sent.Limit.Should().Be(25);
        sent.ExpressionAttributeValues[":s"].S.Should().Be(status);
        var row = rows.Should().ContainSingle().Subject;
        row.Should().Be(new ListingReportRow(
            "crop-1", "buyer-1", "crop", "farmer-1", "scam", status, "2026-11-07T09:30:15.123Z", 1_778_146_215,
            ReviewNote: "checked"));
        lastKey.Should().NotBeNull();
    }

    [Fact]
    public async Task ListByListing_ReadsEveryPage_Consistently()
    {
        var calls = new List<QueryRequest>();
        _dynamoDb.QueryAsync(Arg.Do<QueryRequest>(r => calls.Add(r)), Arg.Any<CancellationToken>())
            .Returns(
                new QueryResponse
                {
                    Items = new List<Dictionary<string, AttributeValue>> { Item("buyer-1") },
                    LastEvaluatedKey = new Dictionary<string, AttributeValue> { ["reporterId"] = new() { S = "buyer-1" } }
                },
                new QueryResponse { Items = new List<Dictionary<string, AttributeValue>> { Item("buyer-2") } });

        var rows = await _repo.ListByListingAsync("crop-1");

        rows.Select(r => r.ReporterId).Should().Equal("buyer-1", "buyer-2");
        calls.Should().HaveCount(2);
        calls.Should().OnlyContain(c => c.ConsistentRead == true && c.IndexName == null);
        calls[1].ExclusiveStartKey["reporterId"].S.Should().Be("buyer-1");
    }

    [Fact]
    public async Task TryReview_MovesOnlyAnOpenRow_WithTheNote()
    {
        UpdateItemRequest? sent = null;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        var moved = await _repo.TryReviewAsync("crop-1", "buyer-1", "actioned", "admin-1", "2026-11-07T12:00:00.000Z", "Suspended");

        moved.Should().BeTrue();
        sent!.ConditionExpression.Should().Be("#s = :open");
        sent.ExpressionAttributeValues[":open"].S.Should().Be("open");
        sent.ExpressionAttributeValues[":decision"].S.Should().Be("actioned");
        sent.ExpressionAttributeValues[":rb"].S.Should().Be("admin-1");
        sent.ExpressionAttributeValues[":rn"].S.Should().Be("Suspended");
        sent.UpdateExpression.Should().Contain("#rn = :rn");
    }

    [Fact]
    public async Task TryReview_WithoutANote_SetsNone()
    {
        UpdateItemRequest? sent = null;
        _dynamoDb.UpdateItemAsync(Arg.Do<UpdateItemRequest>(r => sent = r), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());

        await _repo.TryReviewAsync("crop-1", "buyer-1", "dismissed", "admin-1", "2026-11-07T12:00:00.000Z", null);

        sent!.UpdateExpression.Should().Be("SET #s = :decision, #ra = :ra, #rb = :rb");
        sent.ExpressionAttributeValues.Should().NotContainKey(":rn");
    }

    [Fact]
    public async Task TryReview_AlreadyDecided_IsFalse()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ConditionalCheckFailedException("decided"));

        (await _repo.TryReviewAsync("crop-1", "buyer-1", "dismissed", "admin-1", "t", null)).Should().BeFalse();
    }

    private static Dictionary<string, AttributeValue> Item(string reporterId) => new()
    {
        ["listingId"] = new() { S = "crop-1" },
        ["reporterId"] = new() { S = reporterId },
        ["status"] = new() { S = "open" }
    };
}
