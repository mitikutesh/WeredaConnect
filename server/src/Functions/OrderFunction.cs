using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Helpers;
using WeredaConnect.Server.Models;

namespace WeredaConnect.Server.Functions;

/// <summary>
/// GET /orders and GET /orders/{id} Lambda handler.
/// Provides read-only access to orders.
/// Create/update operations are handled through /sync endpoint.
///
/// Endpoints:
/// - GET /orders - Query orders with optional filters (buyerId, farmerId, truckerId, status, listingId)
/// - GET /orders/{id} - Get a single order by ID
///
/// Error codes:
/// - 400: ORDER_VALIDATION - Invalid query parameters
/// - 404: ORDER_NOT_FOUND - Order does not exist
/// - 500: ORDER_SERVER_ERROR - Unexpected server failure
///
/// ESC-48: every order carries <c>contactOpen</c>, and a party of an order whose contact is open
/// also gets the parties' phone numbers, read from the users table at request time (orders store
/// none). A failed phone read leaves the numbers out; it never fails the order read.
/// </summary>
public class OrderFunction
{
    private readonly IOrderRepository _orderRepo;
    private readonly IUserRepository? _userRepo;

    private static readonly IReadOnlyDictionary<string, string> NoPhones = new Dictionary<string, string>();

    /// <summary>
    /// Default constructor for Lambda runtime.
    /// Reads table names from environment variables.
    /// </summary>
    public OrderFunction()
    {
        var ordersTable = Environment.GetEnvironmentVariable("ORDERS_TABLE")
                          ?? throw new InvalidOperationException("ORDERS_TABLE is not set.");

        var dynamoDb = new AmazonDynamoDBClient();
        _orderRepo = new OrderRepository(dynamoDb, ordersTable);

        // Without the users table no phone is ever returned (fail closed), and orders still load.
        var usersTable = Environment.GetEnvironmentVariable("USERS_TABLE");
        _userRepo = string.IsNullOrEmpty(usersTable) ? null : new UserRepository(dynamoDb, usersTable);
    }

    /// <summary>
    /// Constructor for testing with mocked dependencies.
    /// </summary>
    public OrderFunction(IOrderRepository orderRepo, IUserRepository? userRepo = null)
    {
        _orderRepo = orderRepo ?? throw new ArgumentNullException(nameof(orderRepo));
        _userRepo = userRepo;
    }

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        context.Logger.LogInformation($"OrderFunction invoked: {request.HttpMethod} {request.Path}");

        return request.HttpMethod switch
        {
            "GET" when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetOrderByIdAsync(request, context),
            "GET" => await HandleGetOrdersAsync(request, context),
            _ => LambdaResponse.Error(405, "ORDER_VALIDATION", "Method not allowed.")
        };
    }

    /// <summary>
    /// GET /orders - Query orders with optional filters.
    /// Query parameters: buyerId, farmerId, truckerId, status, listingId, limit, nextToken.
    /// Without a party filter the list is scoped to the caller by their role claim.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetOrdersAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        var queryParams = request.QueryStringParameters ?? new Dictionary<string, string>();

        queryParams.TryGetValue("buyerId", out var buyerId);
        queryParams.TryGetValue("farmerId", out var farmerId);
        queryParams.TryGetValue("truckerId", out var truckerId);
        queryParams.TryGetValue("status", out var status);
        queryParams.TryGetValue("listingId", out var listingId);

        // Parse limit (default 20, max 50)
        var limit = 20;
        if (queryParams.TryGetValue("limit", out var limitStr))
        {
            if (!int.TryParse(limitStr, out limit) || limit < 1 || limit > 50)
            {
                return LambdaResponse.Error(400, "ORDER_VALIDATION",
                    "Invalid limit parameter. Must be between 1 and 50.");
            }
        }

        // Parse nextToken
        Dictionary<string, AttributeValue>? exclusiveStartKey = null;
        if (queryParams.TryGetValue("nextToken", out var nextToken) && !string.IsNullOrEmpty(nextToken))
        {
            exclusiveStartKey = PaginationTokenHelper.DecodeNextToken(nextToken);
            if (exclusiveStartKey == null)
            {
                return LambdaResponse.Error(400, "ORDER_VALIDATION", "Invalid or tampered nextToken parameter.");
            }
        }

        // Authorization: user must be authenticated
        var authenticatedUserId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        // Security: users can only query orders they participate in
        if (!string.IsNullOrEmpty(buyerId) && buyerId != authenticatedUserId)
        {
            return LambdaResponse.Error(403, "ORDER_FORBIDDEN",
                "You can only query your own orders.");
        }

        if (!string.IsNullOrEmpty(farmerId) && farmerId != authenticatedUserId)
        {
            return LambdaResponse.Error(403, "ORDER_FORBIDDEN",
                "You can only query your own orders.");
        }

        if (!string.IsNullOrEmpty(truckerId) && truckerId != authenticatedUserId)
        {
            return LambdaResponse.Error(403, "ORDER_FORBIDDEN",
                "You can only query your own orders.");
        }

        // Default: no party filter given, so scope by the caller's role claim
        // (SIM-16). This used to assume buyer, so a farmer or trucker reading
        // GET /orders without farmerId=/truckerId= silently got [].
        if (string.IsNullOrEmpty(buyerId) && string.IsNullOrEmpty(farmerId) && string.IsNullOrEmpty(truckerId))
        {
            switch (AuthHelper.GetRole(request))
            {
                case "farmer":
                    farmerId = authenticatedUserId;
                    break;
                case "trucker":
                    truckerId = authenticatedUserId;
                    break;
                default:
                    buyerId = authenticatedUserId;
                    break;
            }
        }

        try
        {
            var queryResult = await _orderRepo.QueryOrdersAsync(
                buyerId, farmerId, truckerId, status, listingId, limit, exclusiveStartKey);

            var phones = await ReadPartyPhonesAsync(queryResult.Items, authenticatedUserId, context);
            var orders = queryResult.Items
                .Select(i => MapToOrderDto(i, IsParty(i, authenticatedUserId) ? phones : null))
                .ToList();

            string? nextTokenResponse = null;
            if (queryResult.LastEvaluatedKey != null)
            {
                nextTokenResponse = PaginationTokenHelper.EncodeNextToken(queryResult.LastEvaluatedKey);
            }

            var response = new GetOrdersResponse
            {
                Orders = orders,
                NextToken = nextTokenResponse
            };

            return LambdaResponse.Ok(response, AppJsonContext.Default.GetOrdersResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error querying orders: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ORDER_SERVER_ERROR", "Failed to query orders.");
        }
    }

    /// <summary>
    /// GET /orders/{id} - Get a single order by ID.
    /// </summary>
    private async Task<APIGatewayProxyResponse> HandleGetOrderByIdAsync(
        APIGatewayProxyRequest request,
        ILambdaContext context)
    {
        if (!request.PathParameters.TryGetValue("id", out var orderId) || string.IsNullOrEmpty(orderId))
        {
            return LambdaResponse.Error(400, "ORDER_VALIDATION", "Missing order ID in path.");
        }

        // Authorization
        var authenticatedUserId = AuthHelper.GetUserId(request);
        if (string.IsNullOrEmpty(authenticatedUserId))
        {
            return LambdaResponse.Error(401, "UNAUTHORIZED", "Authentication required.");
        }

        try
        {
            var order = await _orderRepo.GetByIdAsync(orderId);

            if (order == null)
            {
                return LambdaResponse.Error(404, "ORDER_NOT_FOUND", "Order not found.");
            }

            // Authorization: only participants of this order can view it
            var orderBuyerId = order.TryGetValue("buyerId", out var bid) ? bid?.ToString() : null;
            var orderFarmerId = order.TryGetValue("farmerId", out var fid) ? fid?.ToString() : null;
            var orderTruckerId = order.TryGetValue("truckerId", out var tid) ? tid?.ToString() : null;

            if (authenticatedUserId != orderBuyerId && authenticatedUserId != orderFarmerId
                && authenticatedUserId != orderTruckerId)
            {
                return LambdaResponse.Error(403, "ORDER_FORBIDDEN",
                    "You are not authorized to view this order.");
            }

            var phones = await ReadPartyPhonesAsync(new[] { order }, authenticatedUserId, context);
            var orderDto = MapToOrderDto(order, phones);
            var response = new GetOrderResponse { Order = orderDto };

            return LambdaResponse.Ok(response, AppJsonContext.Default.GetOrderResponse);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Error getting order {orderId}: [{ex.GetType().Name}]");
            return LambdaResponse.Error(500, "ORDER_SERVER_ERROR", "Failed to retrieve order.");
        }
    }

    private static bool IsParty(Dictionary<string, object> order, string userId)
        => GetStringOrNull(order, "buyerId") == userId
           || GetStringOrNull(order, "farmerId") == userId
           || GetStringOrNull(order, "truckerId") == userId;

    /// <summary>
    /// ESC-48: the phone numbers of the parties of the caller's orders whose contact is open, in one
    /// batched read. Empty when there is none, when the users table is not configured, or when the
    /// read fails (logged without any number).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> ReadPartyPhonesAsync(
        IEnumerable<Dictionary<string, object>> orders, string callerId, ILambdaContext context)
    {
        if (_userRepo == null)
        {
            return NoPhones;
        }

        var userIds = orders
            .Where(o => ContactReveal.IsOpen(o) && IsParty(o, callerId))
            .SelectMany(o => new[]
            {
                GetStringOrNull(o, "buyerId"), GetStringOrNull(o, "farmerId"), GetStringOrNull(o, "truckerId")
            })
            .OfType<string>()
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (userIds.Count == 0)
        {
            return NoPhones;
        }

        try
        {
            return await _userRepo.GetPhoneNumbersAsync(userIds);
        }
        catch (Exception ex)
        {
            context.Logger.LogWarning($"Order contact phones could not be read: [{ex.GetType().Name}]");
            return NoPhones;
        }
    }

    /// <summary>
    /// Map a DynamoDB item dictionary to an OrderDto, without phone numbers (the admin transaction view).
    /// </summary>
    internal static OrderDto MapToOrderDto(Dictionary<string, object> item) => MapToOrderDto(item, null);

    /// <summary>
    /// Map a DynamoDB item dictionary to an OrderDto. <paramref name="phones"/> (user id to phone) fills
    /// the parties' numbers only when the order's contact is open; pass null for a caller who is not a party.
    /// </summary>
    internal static OrderDto MapToOrderDto(Dictionary<string, object> item, IReadOnlyDictionary<string, string>? phones)
    {
        var contactOpen = ContactReveal.IsOpen(item);
        string? PhoneOf(string idKey)
            => contactOpen && phones != null
               && GetStringOrNull(item, idKey) is { Length: > 0 } id
               && phones.TryGetValue(id, out var phone)
                ? phone
                : null;

        return new OrderDto
        {
            OrderId = GetString(item, "orderId"),
            ListingId = GetString(item, "listingId"),
            BuyerId = GetString(item, "buyerId"),
            FarmerId = GetString(item, "farmerId"),
            BuyerName = GetString(item, "buyerName"),
            FarmerName = GetString(item, "farmerName"),
            CropType = GetString(item, "cropType"),
            Quantity = GetDouble(item, "quantity"),
            PricePerUnit = GetDouble(item, "pricePerUnit"),
            CropCost = GetDouble(item, "cropCost"),
            TripId = GetStringOrNull(item, "tripId"),
            TruckerId = GetStringOrNull(item, "truckerId"),
            TruckerName = GetStringOrNull(item, "truckerName"),
            TransportCost = GetDoubleOrNull(item, "transportCost"),
            CommissionRate = GetDouble(item, "commissionRate"),
            CommissionAmount = GetDouble(item, "commissionAmount"),
            OrderTotal = GetDouble(item, "orderTotal"),
            PaymentMethod = GetStringOrNull(item, "paymentMethod"),
            CbeTransactionId = GetStringOrNull(item, "cbeTransactionId"),
            PaymentVerifiedAt = GetStringOrNull(item, "paymentVerifiedAt"),
            Status = GetString(item, "status"),
            CreatedAt = GetString(item, "createdAt"),
            UpdatedAt = GetString(item, "updatedAt"),
            Version = GetInt(item, "version"),
            RespondedAt = GetStringOrNull(item, "respondedAt"),
            RejectionReason = GetStringOrNull(item, "rejectionReason"),
            CancelledAt = GetStringOrNull(item, "cancelledAt"),
            CancelReason = GetStringOrNull(item, "cancelReason"),
            PickupDeadlineAt = GetStringOrNull(item, PickupDeadline.Field),
            AdminQueueReason = GetStringOrNull(item, AdminQueueFields.Reason),
            AdminQueuedAt = GetStringOrNull(item, AdminQueueFields.QueuedAt),
            ContactOpen = contactOpen,
            BuyerPhone = PhoneOf("buyerId"),
            FarmerPhone = PhoneOf("farmerId"),
            TruckerPhone = PhoneOf("truckerId")
        };
    }

    private static string GetString(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() ?? "" : "";

    private static string? GetStringOrNull(Dictionary<string, object> item, string key)
        => item.TryGetValue(key, out var value) ? value.ToString() : null;

    private static int GetInt(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0;
        }

        return value switch
        {
            int i => i,
            string s => int.TryParse(s, out var result) ? result : 0,
            _ => 0
        };
    }

    private static double GetDouble(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return 0.0;
        }

        return value switch
        {
            double d => d,
            float f => f,
            decimal dec => (double)dec,
            string s => double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : 0.0,
            _ => 0.0
        };
    }

    private static double? GetDoubleOrNull(Dictionary<string, object> item, string key)
    {
        if (!item.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            double d => d,
            float f => f,
            decimal dec => (double)dec,
            string s => double.TryParse(s, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var result) ? result : null,
            _ => null
        };
    }
}
