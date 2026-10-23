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

    public Task<UserDto?> GetUserByIdAsync(string userId) => GetUserByIdAsync(userId, consistentRead: false);

    public async Task<UserDto?> GetUserByIdAsync(string userId, bool consistentRead)
    {
        var request = new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = userId } }
            },
            ConsistentRead = consistentRead
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
            UpdateExpression = "SET #phone = :phone, #name = :name, #fullName = :fullName, #role = :role, " +
                               "#woreda = :woreda, #lang = :lang, #verified = :verified, " +
                               "#photo = :photo, #fayda = :fayda, #faydaV = :faydaV, #faydaVAt = :faydaVAt, #faydaAuthAt = :faydaAuthAt, " +
                               "#devices = :devices, #updated = :updated, #v = :newVersion",
            // ACC-02: never on a deleting or deleted account, so no profile write can put the
            // personal data back on a tombstone.
            ConditionExpression = "#v = :expectedVersion AND attribute_not_exists(#deletionState)",
            ExpressionAttributeNames = new Dictionary<string, string>
            {
                { "#deletionState", "deletionState" },
                { "#phone", "phoneNumber" },
                { "#name", "displayName" },
                { "#fullName", "fullName" },
                { "#role", "role" },
                { "#woreda", "woreda" },
                { "#lang", "preferredLanguage" },
                { "#verified", "isPhoneVerified" },
                { "#photo", "profilePhotoRef" },
                { "#fayda", "faydaId" },
                { "#faydaV", "faydaVerified" },
                { "#faydaVAt", "faydaVerifiedAt" },
                { "#faydaAuthAt", "faydaAuthenticatedAt" },
                { "#devices", "knownDevices" },
                { "#updated", "updatedAt" },
                { "#v", "version" }
            },
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                { ":phone", new AttributeValue { S = user.PhoneNumber } },
                { ":name", user.DisplayName != null ? new AttributeValue { S = user.DisplayName } : new AttributeValue { NULL = true } },
                { ":fullName", user.FullName != null ? new AttributeValue { S = user.FullName } : new AttributeValue { NULL = true } },
                { ":role", new AttributeValue { S = user.Role } },
                { ":woreda", new AttributeValue { S = user.Woreda } },
                { ":lang", new AttributeValue { S = user.PreferredLanguage } },
                { ":verified", new AttributeValue { BOOL = user.IsPhoneVerified } },
                { ":photo", user.ProfilePhotoRef != null ? new AttributeValue { S = user.ProfilePhotoRef } : new AttributeValue { NULL = true } },
                { ":fayda", user.FaydaId != null ? new AttributeValue { S = user.FaydaId } : new AttributeValue { NULL = true } },
                { ":faydaV", new AttributeValue { BOOL = user.FaydaVerified } },
                { ":faydaVAt", user.FaydaVerifiedAt != null ? new AttributeValue { S = user.FaydaVerifiedAt } : new AttributeValue { NULL = true } },
                { ":faydaAuthAt", user.FaydaAuthenticatedAt != null ? new AttributeValue { S = user.FaydaAuthenticatedAt } : new AttributeValue { NULL = true } },
                { ":devices", user.KnownDevices.Count > 0 ? new AttributeValue { SS = [.. user.KnownDevices] } : new AttributeValue { NULL = true } },
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

    public Task DeleteUserAsync(string userId)
        => _dynamoDb.DeleteItemAsync(new DeleteItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue>
            {
                { "userId", new AttributeValue { S = userId } }
            }
        });

    /// <summary>Rounds of BatchGetItem for the keys DynamoDB left unprocessed (throttling, size cap).</summary>
    private const int MaxPhoneBatchAttempts = 3;

    public async Task<IReadOnlyDictionary<string, string>> GetPhoneNumbersAsync(IReadOnlyCollection<string> userIds)
    {
        var phones = new Dictionary<string, string>(StringComparer.Ordinal);
        var distinct = userIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();

        // BatchGetItem takes at most 100 keys per request.
        foreach (var batch in distinct.Chunk(100))
        {
            var requestItems = new Dictionary<string, KeysAndAttributes>
            {
                [_tableName] = new KeysAndAttributes
                {
                    Keys = batch.Select(id => new Dictionary<string, AttributeValue>
                    {
                        { "userId", new AttributeValue { S = id } }
                    }).ToList(),
                    ProjectionExpression = "userId, phoneNumber"
                }
            };

            // The low-level client does not retry UnprocessedKeys by itself.
            for (var attempt = 0; requestItems.Count > 0 && attempt < MaxPhoneBatchAttempts; attempt++)
            {
                if (attempt > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50 << attempt));
                }

                var response = await _dynamoDb.BatchGetItemAsync(new BatchGetItemRequest { RequestItems = requestItems });
                if (response.Responses != null && response.Responses.TryGetValue(_tableName, out var items))
                {
                    foreach (var item in items)
                    {
                        var id = item.TryGetValue("userId", out var uid) ? uid.S : null;
                        var phone = item.TryGetValue("phoneNumber", out var pn) ? pn.S : null;
                        if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(phone))
                        {
                            phones[id] = phone;
                        }
                    }
                }

                requestItems = response.UnprocessedKeys is { Count: > 0 }
                    ? response.UnprocessedKeys
                    : new Dictionary<string, KeysAndAttributes>();
            }
        }

        return phones;
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

        if (user.FullName != null)
        {
            item["fullName"] = new AttributeValue { S = user.FullName };
        }

        if (user.FaydaId != null)
        {
            item["faydaId"] = new AttributeValue { S = user.FaydaId };
        }

        if (user.KnownDevices.Count > 0)
        {
            item["knownDevices"] = new AttributeValue { SS = [.. user.KnownDevices] };
        }

        if (user.FaydaVerifiedAt != null)
        {
            item["faydaVerifiedAt"] = new AttributeValue { S = user.FaydaVerifiedAt };
        }

        if (user.FaydaAuthenticatedAt != null)
        {
            item["faydaAuthenticatedAt"] = new AttributeValue { S = user.FaydaAuthenticatedAt };
        }

        if (user.ProfilePhotoRef != null)
        {
            item["profilePhotoRef"] = new AttributeValue { S = user.ProfilePhotoRef };
        }

        if (user.BuyerType != null)
        {
            item["buyerType"] = new AttributeValue { S = user.BuyerType };
        }

        if (user.CompanyName != null)
        {
            item["companyName"] = new AttributeValue { S = user.CompanyName };
        }

        return item;
    }

    /// <summary>
    /// Unmarshal a DynamoDB item to a UserDto. A deleted account's tombstone (ACC-02) has no
    /// phone and no woreda: both map to an empty string.
    /// </summary>
    internal static UserDto FromItem(Dictionary<string, AttributeValue> item)
    {
        return new UserDto
        {
            UserId = item["userId"].S,
            PhoneNumber = item.TryGetValue("phoneNumber", out var pn) && pn.S != null ? pn.S : "",
            DisplayName = item.TryGetValue("displayName", out var dn) && dn.S != null ? dn.S : null,
            FullName = item.TryGetValue("fullName", out var fn) && fn.S != null ? fn.S : null,
            Role = item["role"].S,
            Woreda = item.TryGetValue("woreda", out var wd) && wd.S != null ? wd.S : "",
            FaydaId = item.TryGetValue("faydaId", out var fid) && fid.S != null ? fid.S : null,
            FaydaVerified = item.TryGetValue("faydaVerified", out var fv) && fv.BOOL,
            FaydaVerifiedAt = item.TryGetValue("faydaVerifiedAt", out var fva) && fva.S != null ? fva.S : null,
            FaydaAuthenticatedAt = item.TryGetValue("faydaAuthenticatedAt", out var faa) && faa.S != null ? faa.S : null,
            PreferredLanguage = item.TryGetValue("preferredLanguage", out var lang) ? lang.S : "am",
            IsPhoneVerified = item.TryGetValue("isPhoneVerified", out var verified) && verified.BOOL,
            Suspended = item.TryGetValue("suspended", out var susp) && susp.BOOL,
            KnownDevices = item.TryGetValue("knownDevices", out var kd) && kd.SS is { Count: > 0 } ? [.. kd.SS] : [],
            ProfilePhotoRef = item.TryGetValue("profilePhotoRef", out var photo) && photo.S != null ? photo.S : null,
            BuyerType = item.TryGetValue("buyerType", out var bt) && bt.S != null ? bt.S : null,
            CompanyName = item.TryGetValue("companyName", out var cn) && cn.S != null ? cn.S : null,
            DeletionState = item.TryGetValue("deletionState", out var ds) && ds.S != null ? ds.S : null,
            DeletionRequestedAt = item.TryGetValue("deletionRequestedAt", out var dra) && dra.S != null ? dra.S : null,
            DeletedAt = item.TryGetValue("deletedAt", out var da) && da.S != null ? da.S : null,
            CreatedAt = item["createdAt"].S,
            UpdatedAt = item["updatedAt"].S,
            Version = item.TryGetValue("version", out var ver) && ver.N != null ? int.Parse(ver.N) : 1
        };
    }
}
