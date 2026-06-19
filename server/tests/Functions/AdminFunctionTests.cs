using System.Text.Json;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// CHAIN-01 regression tests for the admin change_role action: an admin must not be able
/// to assign an arbitrary role string (notably "admin", which EscrowService trusts for
/// fund release). Only the three marketplace roles are assignable.
/// </summary>
public class AdminFunctionTests
{
    private const string UsersTable = "users-test";

    private readonly IAmazonDynamoDB _dynamoDb = Substitute.For<IAmazonDynamoDB>();
    private readonly AdminFunction _function;
    private readonly TestLambdaContext _context = new();

    public AdminFunctionTests()
    {
        _dynamoDb.UpdateItemAsync(Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateItemResponse());
        _function = new AdminFunction(_dynamoDb, UsersTable);
    }

    private static APIGatewayProxyRequest ChangeRoleRequest(string userId, string newRole)
    {
        // IsAdmin() accepts a "role"/"custom:role" authorizer claim equal to "admin".
        var authorizer = new APIGatewayCustomAuthorizerContext
        {
            { "sub", "admin-1" },
            { "role", "admin" },
        };

        return new APIGatewayProxyRequest
        {
            HttpMethod = "POST",
            Path = $"/admin/users/{userId}/action",
            Body = JsonSerializer.Serialize(new { action = "change_role", newRole }),
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext { Authorizer = authorizer },
        };
    }

    [Fact]
    public async Task ChangeRole_ToAdmin_IsRejected400_AndDoesNotWrite()
    {
        var response = await _function.FunctionHandler(
            ChangeRoleRequest("user-1", "admin"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_011");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeRole_ToInvalidString_IsRejected400_AndDoesNotWrite()
    {
        var response = await _function.FunctionHandler(
            ChangeRoleRequest("user-1", "superuser"), _context);

        response.StatusCode.Should().Be(400);
        response.Body.Should().Contain("ADMIN_011");
        await _dynamoDb.DidNotReceive().UpdateItemAsync(
            Arg.Any<UpdateItemRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ChangeRole_ToValidRole_Succeeds_AndStoresLowercaseRole()
    {
        var response = await _function.FunctionHandler(
            ChangeRoleRequest("user-1", "trucker"), _context);

        response.StatusCode.Should().Be(200);
        await _dynamoDb.Received(1).UpdateItemAsync(
            Arg.Is<UpdateItemRequest>(r =>
                r.TableName == UsersTable
                && r.ExpressionAttributeValues[":newRole"].S == "trucker"),
            Arg.Any<CancellationToken>());
    }
}
