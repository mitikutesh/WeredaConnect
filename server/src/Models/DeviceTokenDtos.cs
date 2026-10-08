namespace WeredaConnect.Server.Models;

/// <summary>
/// Request body for POST /device-tokens — register an FCM device token.
///
/// Token/Platform are nullable because System.Text.Json deserialization can
/// always produce a missing field; the "Token is required" invariant is
/// enforced at the handler boundary (DeviceTokenFunction returns 400
/// DEVICE_TOKEN_VALIDATION when absent), which yields a cleaner client error
/// than a deserialization throw. Any new consumer must re-check Token != null.
/// </summary>
public sealed record RegisterDeviceTokenRequest
{
    /// <summary>The FCM registration token from the device.</summary>
    public string? Token { get; init; }

    /// <summary>
    /// Device platform. Optional, defaults to "android"; the only allowed value today
    /// (case-insensitive, trimmed). Anything else is a 400 DEVICE_TOKEN_VALIDATION.
    /// </summary>
    public string? Platform { get; init; }
}

/// <summary>Response for POST /device-tokens.</summary>
public sealed record RegisterDeviceTokenResponse
{
    public required bool Success { get; init; }
}
