namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Admin authorization helper. Checks JWT claims for admin role
/// via API Gateway authorizer context (same pattern as AuthHelper).
/// </summary>
public static class AdminAuthHelper
{
    private const string AdminRole = "admin";

    /// <summary>
    /// Check if the authenticated user has admin role.
    /// Checks both Cognito groups claim (cognito:groups) and custom role claims.
    /// </summary>
    public static bool IsAdmin(APIGatewayProxyRequest request)
    {
        if (!AuthHelper.IsAuthenticated(request))
        {
            return false;
        }

        // Check Cognito groups claim (comma-separated list)
        var groups = AuthHelper.GetClaim(request, "cognito:groups") ?? "";
        if (groups.Split(',').Any(g => string.Equals(g.Trim(), AdminRole, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // Fallback: check custom role claims
        var role = AuthHelper.GetClaim(request, "role")
                   ?? AuthHelper.GetClaim(request, "custom:role");

        return string.Equals(role, AdminRole, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Return a 403 Forbidden response for non-admin users.
    /// </summary>
    public static APIGatewayProxyResponse Forbidden()
    {
        return LambdaResponse.Error(403, "ADMIN_001", "Admin access required.");
    }
}
