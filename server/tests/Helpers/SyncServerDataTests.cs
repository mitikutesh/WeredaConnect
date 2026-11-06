using System.Reflection;
using System.Text.Json;
using Amazon.DynamoDBv2.Model;
using FluentAssertions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// SIM-25: a /sync row leaves the server with the JSON types GET gives the same row: numbers as
/// numbers and a trip's matchedListings as an array (sync-protocol.md 1.37.0).
/// </summary>
public class SyncServerDataTests
{
    [Fact]
    public void TypedRow_NumericFieldsBecomeNumbers_AndOtherStringsStayStrings()
    {
        var row = DynamoDbHelper.FromItem(new Dictionary<string, AttributeValue>
        {
            ["cropId"] = new() { S = "crop-001" },
            ["version"] = new() { N = "2" },
            ["pricePerKg"] = new() { N = "18" },
            ["quantity"] = new() { N = "12.5" },
            ["latitude"] = new() { N = "9.03" },
            // A string that reads as a number keeps its type: only the named fields are numbers.
            ["woreda"] = new() { S = "105" },
            ["farmerPhone"] = new() { S = "0912345678" },
            ["isFlagged"] = new() { BOOL = true },
        });

        var typed = SyncServerData.TypedRow(row);

        typed["version"].Should().Be(2L);
        typed["pricePerKg"].Should().Be(18L);
        typed["quantity"].Should().Be(12.5m);
        typed["latitude"].Should().Be(9.03m);
        typed["woreda"].Should().Be("105");
        typed["farmerPhone"].Should().Be("0912345678");
        typed["cropId"].Should().Be("crop-001");
        typed["isFlagged"].Should().Be(true);
    }

    [Fact]
    public void TypedRow_DoesNotChangeTheRowItWasGiven()
    {
        var row = new Dictionary<string, object> { ["version"] = "3" };

        SyncServerData.TypedRow(row);

        row["version"].Should().Be("3");
    }

    [Fact]
    public void TypedRow_NestedPointsAreTypedToo()
    {
        var row = DynamoDbHelper.FromItem(new Dictionary<string, AttributeValue>
        {
            ["gpsPickup"] = new()
            {
                M = new Dictionary<string, AttributeValue>
                {
                    ["latitude"] = new() { N = "9.0301" },
                    ["longitude"] = new() { N = "38.7469" },
                }
            },
        });

        var pickup = (Dictionary<string, object>)SyncServerData.TypedRow(row)["gpsPickup"];

        pickup["latitude"].Should().Be(9.0301m);
        pickup["longitude"].Should().Be(38.7469m);
    }

    [Theory]
    [InlineData("[]", new string[0])]
    [InlineData("", new string[0])]
    [InlineData("[\"listing-1\",\"listing-2\"]", new[] { "listing-1", "listing-2" })]
    [InlineData("not json", new string[0])]
    [InlineData("[1,2]", new string[0])]
    public void TypedRow_MatchedListingsTextBecomesTheListGetReturns(string stored, string[] expected)
    {
        var row = new Dictionary<string, object> { ["matchedListings"] = stored };

        var listings = SyncServerData.TypedRow(row)["matchedListings"];

        listings.Should().BeOfType<List<object>>().Which.Should().Equal(expected);
    }

    [Fact]
    public void TypedRow_AStoredListIsKept()
    {
        var row = DynamoDbHelper.FromItem(new Dictionary<string, AttributeValue>
        {
            ["photoIds"] = new() { L = new List<AttributeValue> { new() { S = "a" }, new() { S = "b" } } },
        });

        SyncServerData.TypedRow(row)["photoIds"].Should().BeOfType<List<object>>()
            .Which.Should().Equal("a", "b");
    }

    [Theory]
    [InlineData("")]
    [InlineData("n/a")]
    public void TypedRow_ANumericFieldThatIsNotANumberIsLeftAsItIs(string stored)
    {
        var row = new Dictionary<string, object> { ["transportCost"] = stored };

        SyncServerData.TypedRow(row)["transportCost"].Should().Be(stored);
    }

    [Fact]
    public void TypedRow_KeepsAnUnroundedFloatExactly()
    {
        var row = new Dictionary<string, object> { ["commissionAmount"] = "0.9500000000000001" };

        SyncServerData.TypedRow(row)["commissionAmount"].Should().Be(0.9500000000000001m);
    }

    [Fact]
    public void Typed_ResultWithoutARowIsReturnedAsItIs()
    {
        var result = new SyncOperationResult { OperationId = "op-1", Status = "duplicate" };

        SyncServerData.Typed(result).Should().BeSameAs(result);
    }

    [Fact]
    public void SyncResponse_WithATypedRow_SerializesNumbersAndArraysUnderTheSourceGenContext()
    {
        var row = DynamoDbHelper.FromItem(new Dictionary<string, AttributeValue>
        {
            ["tripId"] = new() { S = "trip-001" },
            ["version"] = new() { N = "2" },
            ["capacity"] = new() { N = "50" },
            ["pricePerQuintal"] = new() { N = "90.5" },
            ["matchedListings"] = new() { S = "[\"listing-1\"]" },
        });
        var response = new SyncResponse
        {
            SyncBatchId = "batch-001",
            Results = new List<SyncOperationResult>
            {
                SyncServerData.Typed(new SyncOperationResult
                {
                    OperationId = "op-001", Status = "conflict", ServerVersion = 2, ServerData = row
                })
            }
        };

        var json = JsonSerializer.Serialize(response, AppJsonContext.Default.SyncResponse);

        json.Should().Contain("\"version\":2,")
            .And.Contain("\"capacity\":50,")
            .And.Contain("\"pricePerQuintal\":90.5,")
            .And.Contain("\"matchedListings\":[\"listing-1\"]");
    }

    /// <summary>
    /// Drift guard: every number of the GET read models is a numeric field here, so a field added
    /// to a DTO is typed on /sync too.
    /// </summary>
    [Theory]
    [InlineData(typeof(CropDto))]
    [InlineData(typeof(TripDto))]
    [InlineData(typeof(OrderDto))]
    [InlineData(typeof(DeliveryDto))]
    [InlineData(typeof(LatLngDto))]
    [InlineData(typeof(RatingDto))]
    [InlineData(typeof(MessageDto))]
    [InlineData(typeof(UserDto))]
    public void NumericFields_CoverEveryNumberOfTheReadModels(Type dto)
    {
        var numeric = new[] { typeof(int), typeof(long), typeof(decimal), typeof(double), typeof(float) };
        var names = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => numeric.Contains(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType))
            .Select(p => JsonNamingPolicy.CamelCase.ConvertName(p.Name));

        SyncServerData.NumericFields.Should().Contain(names);
    }
}
