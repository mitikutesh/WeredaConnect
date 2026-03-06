namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Admin authorization helper. Checks JWT claims for admin role
/// via API Gateway authorizer context and JWT token parsing.
/// </summary>
public static class AdminAuthHelper
{
    private const string AdminRole = "admin";

    /// <summary>
    /// Check if the authenticated user has admin role.
    /// HTTP API v2 JWT authorizer does NOT forward array claims like cognito:groups,
    /// so we also parse the JWT from the Authorization header as a fallback.
    /// </summary>
    public static bool IsAdmin(APIGatewayProxyRequest request)
    {
        if (!AuthHelper.IsAuthenticated(request))
        {
            return false;
        }

        // 1. Check Cognito groups claim from authorizer context (comma-separated list)
        var groups = AuthHelper.GetClaim(request, "cognito:groups") ?? "";
        if (groups.Split(',').Any(g => string.Equals(g.Trim(), AdminRole, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // 2. Check custom role claims from authorizer context
        var role = AuthHelper.GetClaim(request, "role")
                   ?? AuthHelper.GetClaim(request, "custom:role");
        if (string.Equals(role, AdminRole, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // 3. Fallback: parse cognito:groups from the JWT Authorization header directly.
        //    HTTP API v2 JWT authorizer only forwards string claims, not arrays,
        //    so cognito:groups is silently dropped from the authorizer context.
        return HasAdminGroupInJwt(request);
    }

    /// <summary>
    /// Parse the JWT from the Authorization header and check cognito:groups.
    /// Only decodes the payload (base64); signature is already verified by API Gateway.
    /// </summary>
    private static bool HasAdminGroupInJwt(APIGatewayProxyRequest request)
    {
        var authHeader = request.Headers
            ?.FirstOrDefault(h => string.Equals(h.Key, "authorization", StringComparison.OrdinalIgnoreCase))
            .Value;

        if (string.IsNullOrEmpty(authHeader))
            return false;

        var token = authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? authHeader.Substring(7)
            : authHeader;

        var parts = token.Split('.');
        if (parts.Length != 3)
            return false;

        try
        {
            // Decode JWT payload (base64url)
            var payload = parts[1];
            // Pad to multiple of 4
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            payload = payload.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);

            if (doc.RootElement.TryGetProperty("cognito:groups", out var groupsElement)
                && groupsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in groupsElement.EnumerateArray())
                {
                    if (string.Equals(item.GetString(), AdminRole, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }
        catch
        {
            // Malformed JWT — already validated by API Gateway, so this shouldn't happen
        }

        return false;
    }

    /// <summary>
    /// Return a 403 Forbidden response for non-admin users.
    /// </summary>
    public static APIGatewayProxyResponse Forbidden()
    {
        return LambdaResponse.Error(403, "ADMIN_001", "Admin access required.");
    }
}
