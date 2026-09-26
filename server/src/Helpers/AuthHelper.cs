namespace WeredaConnect.Server.Helpers;

/// <summary>
/// Shared helper for extracting authenticated user identity from API Gateway
/// authorizer context. Handles both HTTP API v2 JWT authorizer (production)
/// and flat dictionary formats (unit tests).
///
/// HTTP API v2 with JWT authorizer and payload format 1.0 nests JWT claims
/// under the "claims" key:
///   authorizer["claims"]["sub"]   → user ID
///   authorizer["claims"]["custom:role"] → user role
///
/// Unit tests may set claims directly on the authorizer dictionary:
///   authorizer["sub"] or authorizer["userId"]
/// </summary>
public static class AuthHelper
{
    /// <summary>
    /// Extract the authenticated user ID from the API Gateway authorizer context.
    /// Returns null if no user ID can be extracted.
    /// </summary>
    public static string? GetUserId(APIGatewayProxyRequest request)
    {
        var authorizer = request.RequestContext?.Authorizer;
        if (authorizer == null || authorizer.Count == 0)
        {
            return null;
        }

        // HTTP API v2 JWT authorizer: claims nested under "claims" key
        if (authorizer.TryGetValue("claims", out var claimsObj) && claimsObj != null)
        {
            var sub = ExtractClaim(claimsObj, "sub");
            if (!string.IsNullOrEmpty(sub))
            {
                return sub;
            }
        }

        // Fallback: direct keys (unit tests or REST API custom authorizer)
        if (authorizer.TryGetValue("sub", out var subVal) && subVal != null)
        {
            var s = subVal.ToString();
            if (!string.IsNullOrEmpty(s))
            {
                return s;
            }
        }

        if (authorizer.TryGetValue("userId", out var userIdVal) && userIdVal != null)
        {
            return userIdVal.ToString();
        }

        return null;
    }

    /// <summary>
    /// Extract a named claim from the authorizer context.
    /// Handles both JWT "claims" nesting and flat dictionaries.
    /// </summary>
    public static string? GetClaim(APIGatewayProxyRequest request, string claimName)
    {
        var authorizer = request.RequestContext?.Authorizer;
        if (authorizer == null || authorizer.Count == 0)
        {
            return null;
        }

        // HTTP API v2 JWT authorizer: claims nested under "claims" key
        if (authorizer.TryGetValue("claims", out var claimsObj) && claimsObj != null)
        {
            var value = ExtractClaim(claimsObj, claimName);
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        // Fallback: direct key
        if (authorizer.TryGetValue(claimName, out var directVal) && directVal != null)
        {
            return directVal.ToString();
        }

        return null;
    }

    /// <summary>
    /// The caller's role claim (<c>role</c>, else Cognito's <c>custom:role</c>),
    /// lower-cased: "farmer", "trucker" or "buyer". Null when the token carries no
    /// role (access tokens do not; only the ID token has custom:role).
    /// </summary>
    public static string? GetRole(APIGatewayProxyRequest request)
    {
        var role = GetClaim(request, "role") ?? GetClaim(request, "custom:role");
        return string.IsNullOrWhiteSpace(role) ? null : role.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// The Cognito auth_time claim (epoch seconds of the last password login),
    /// or null when the token carries none or the value is malformed.
    /// </summary>
    public static long? GetAuthTime(APIGatewayProxyRequest request)
    {
        var raw = GetClaim(request, "auth_time");
        return long.TryParse(raw, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : null;
    }

    /// <summary>
    /// Check if the request has a valid authorizer context (non-empty claims).
    /// </summary>
    public static bool IsAuthenticated(APIGatewayProxyRequest request)
    {
        return request.RequestContext?.Authorizer != null
               && request.RequestContext.Authorizer.Count > 0;
    }

    /// <summary>
    /// Extract the "sub" claim from a raw JWT string. Decodes the payload only —
    /// call this ONLY on tokens whose authenticity is already established
    /// (e.g. tokens Cognito itself just issued to us). Returns null for
    /// anything that isn't a parseable JWT.
    /// </summary>
    public static string? GetSubFromJwt(string? jwt)
    {
        if (string.IsNullOrEmpty(jwt))
        {
            return null;
        }

        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        try
        {
            // Decode JWT payload (base64url): pad to a multiple of 4
            var payload = parts[1];
            switch (payload.Length % 4)
            {
                case 2: payload += "=="; break;
                case 3: payload += "="; break;
            }
            payload = payload.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(payload);
            using var doc = JsonDocument.Parse(bytes);

            if (doc.RootElement.TryGetProperty("sub", out var sub)
                && sub.ValueKind == JsonValueKind.String)
            {
                return sub.GetString();
            }
        }
        catch
        {
            // Malformed payload — treat as "no sub available"
        }

        return null;
    }

    /// <summary>
    /// Extract a claim value from a claims object, which may be a JsonElement
    /// (production deserialization) or a Dictionary (unit tests).
    /// </summary>
    private static string? ExtractClaim(object claimsObj, string claimName)
    {
        // Production: System.Text.Json deserializes nested objects as JsonElement
        if (claimsObj is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Object
                && jsonElement.TryGetProperty(claimName, out var prop))
            {
                return prop.GetString();
            }
        }

        // Unit tests: may pass a Dictionary<string, object> or similar
        if (claimsObj is IDictionary<string, object> dict)
        {
            if (dict.TryGetValue(claimName, out var val))
            {
                return val?.ToString();
            }
        }

        // String representation fallback (shouldn't happen, but defensive)
        if (claimsObj is string s)
        {
            // "claims" value is a raw string — not expected but handle gracefully
            return null;
        }

        return null;
    }
}
