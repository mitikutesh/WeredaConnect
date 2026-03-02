using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Models;
using WeredaConnect.Server.Services;

namespace WeredaConnect.Server.Data;

/// <summary>
/// DynamoDB user repository using low-level API for AOT compatibility.
/// Manual ToItem/FromItem marshalling — no Document Model or reflection.
/// Optimistic concurrency via version condition expressions.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly IAmazonDynamoDB _dynamoDb;
    private readonly string _tableName;

    public UserRepository(IAmazonDynamoDB dynamoDb, string tableName)
    {
        _dynamoDb = dynamoDb ?? throw new ArgumentNullException(nameof(dynamoDb));
        _tableName = tableName ?? throw new ArgumentNullException(nameof(tableName));
    }

    public async Task<AuthResult<UserDto>> CreateUserAsync(UserDto user)
    {
        var item = ToItem(user);

        var request = new PutItemRequest
        {
            TableName = _tableName,
            Item = item,
            ConditionExpression = "attribute_not_exists(userId)"
        };

        try
        {
            await _dynamoDb.PutItemAsync(request);
            return AuthResult<UserDto>.Success(user);
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<UserDto>.Failure(409, "AUTH_005",
                "A user with this ID already exists.", "userId");
        }
    }

    public async Task<UserDto?> GetUserByIdAsync(string userId)
    {
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = userId } }
            }
        };

        var response = await _dynamoDb.GetItemAsync(request);

        if (response.Item == null || response.Item.Count == 0)
        {
            return null;
        }

        return FromItem(response.Item);
    }

    public async Task<UserDto?> GetUserByPhoneAsync(string phone)
    {
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "PhoneIndex",
            KeyConditionExpression = "phoneNumber = :phone",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":phone", new AttributeValue { S = phone } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);

        if (response.Items == null || response.Items.Count == 0)
        {
            return null;
        }

        return FromItem(response.Items[0]);
    }

    public async Task<AuthResult<UserDto>> UpdateUserAsync(UserDto user, int expectedVersion)
    {
        var now = DateTime.UtcNow.ToString("o");
        var newVersion = expectedVersion + 1;

        var request = new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = user.UserId } }
            },
            UpdateExpression = "SET #phone = :phone, #name = :name, #role = :role, " +
                               "#woreda = :woreda, #lang = :lang, #verified = :verified, " +
                               "#photo = :photo, #fayda = :fayda, #faydaV = :faydaV, #faydaVAt = :faydaVAt, " +
                               "#updated = :updated, #v = :newVersion",
            ConditionExpression = "#v = :expectedVersion",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#phone", "phoneNumber" },
                { "#name", "displayName" },
                { "#role", "role" },
                { "#woreda", "woreda" },
                { "#lang", "preferredLanguage" },
                { "#verified", "isPhoneVerified" },
                { "#photo", "profilePhotoRef" },
                { "#fayda", "faydaId" },
                { "#faydaV", "faydaVerified" },
                { "#faydaVAt", "faydaVerifiedAt" },
                { "#updated", "updatedAt" },
                { "#v", "version" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":phone", new AttributeValue { S = user.PhoneNumber } },
                { ":name", user.DisplayName != null ? new AttributeValue { S = user.DisplayName } : new AttributeValue { NULL = true } },
                { ":role", new AttributeValue { S = user.Role } },
                { ":woreda", new AttributeValue { S = user.Woreda } },
                { ":lang", new AttributeValue { S = user.PreferredLanguage } },
                { ":verified", new AttributeValue { BOOL = user.IsPhoneVerified } },
                { ":photo", user.ProfilePhotoRef != null ? new AttributeValue { S = user.ProfilePhotoRef } : new AttributeValue { NULL = true } },
                { ":fayda", user.FaydaId != null ? new AttributeValue { S = user.FaydaId } : new AttributeValue { NULL = true } },
                { ":faydaV", new AttributeValue { BOOL = user.FaydaVerified } },
                { ":faydaVAt", user.FaydaVerifiedAt != null ? new AttributeValue { S = user.FaydaVerifiedAt } : new AttributeValue { NULL = true } },
                { ":updated", new AttributeValue { S = now } },
                { ":newVersion", new AttributeValue { N = newVersion.ToString() } },
                { ":expectedVersion", new AttributeValue { N = expectedVersion.ToString() } }
            }
        };

        try
        {
            await _dynamoDb.UpdateItemAsync(request);

            var updatedUser = user with
            {
                UpdatedAt = now,
                Version = newVersion
            };

            return AuthResult<UserDto>.Success(updatedUser);
        }
        catch (ConditionalCheckFailedException)
        {
            return AuthResult<UserDto>.Failure(409, "AUTH_019",
                "User was modified by another request. Please retry.", "version");
        }
    }

    public async Task<UserDto?> GetUserByFaydaIdAsync(string faydaId)
    {
        // Use FaydaIdIndex GSI — efficient key-based lookup instead of table scan.
        var request = new QueryRequest
        {
            TableName = _tableName,
            IndexName = "FaydaIdIndex",
            KeyConditionExpression = "faydaId = :fid",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":fid", new AttributeValue { S = faydaId } }
            },
            Limit = 1
        };

        var response = await _dynamoDb.QueryAsync(request);

        if (response.Items == null || response.Items.Count == 0)
        {
            return null;
        }

        return FromItem(response.Items[0]);
    }

    /// <summary>
    /// Marshal a UserDto to a DynamoDB item (Dictionary).
    /// </summary>
    internal static Dictionary<string, AttributeValue> ToItem(UserDto user)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            { "userId", new AttributeValue { S = user.UserId } },
            { "phoneNumber", new AttributeValue { S = user.PhoneNumber } },
            { "role", new AttributeValue { S = user.Role } },
            { "woreda", new AttributeValue { S = user.Woreda } },
            { "preferredLanguage", new AttributeValue { S = user.PreferredLanguage } },
            { "isPhoneVerified", new AttributeValue { BOOL = user.IsPhoneVerified } },
            { "faydaVerified", new AttributeValue { BOOL = user.FaydaVerified } },
            { "createdAt", new AttributeValue { S = user.CreatedAt } },
            { "updatedAt", new AttributeValue { S = user.UpdatedAt } },
            { "version", new AttributeValue { N = user.Version.ToString() } }
        };

        if (user.DisplayName != null)
        {
            item["displayName"] = new AttributeValue { S = user.DisplayName };
        }

        if (user.FaydaId != null)
        {
            item["faydaId"] = new AttributeValue { S = user.FaydaId };
        }

        if (user.FaydaVerifiedAt != null)
        {
            item["faydaVerifiedAt"] = new AttributeValue { S = user.FaydaVerifiedAt };
        }

        if (user.ProfilePhotoRef != null)
        {
            item["profilePhotoRef"] = new AttributeValue { S = user.ProfilePhotoRef };
        }

        return item;
    }

    /// <summary>
    /// Unmarshal a DynamoDB item to a UserDto.
    /// </summary>
    internal static UserDto FromItem(Dictionary<string, AttributeValue> item)
    {
        return new UserDto
        {
            UserId = item["userId"].S,
            PhoneNumber = item["phoneNumber"].S,
            DisplayName = item.TryGetValue("displayName", out var dn) && dn.S != null ? dn.S : null,
            Role = item["role"].S,
            Woreda = item["woreda"].S,
            FaydaId = item.TryGetValue("faydaId", out var fid) && fid.S != null ? fid.S : null,
            FaydaVerified = item.TryGetValue("faydaVerified", out var fv) && fv.BOOL,
            FaydaVerifiedAt = item.TryGetValue("faydaVerifiedAt", out var fva) && fva.S != null ? fva.S : null,
            PreferredLanguage = item.TryGetValue("preferredLanguage", out var lang) ? lang.S : "am",
            IsPhoneVerified = item.TryGetValue("isPhoneVerified", out var verified) && verified.BOOL,
            ProfilePhotoRef = item.TryGetValue("profilePhotoRef", out var photo) && photo.S != null ? photo.S : null,
            CreatedAt = item["createdAt"].S,
            UpdatedAt = item["updatedAt"].S,
            Version = item.TryGetValue("version", out var ver) && ver.N != null ? int.Parse(ver.N) : 1
        };
    }
}
