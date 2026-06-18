namespace WeredaConnect.Server.Models;

/// <summary>Request body for POST /device-tokens — register an FCM device token.</summary>
public sealed record RegisterDeviceTokenRequest
{
    /// <summary>The FCM registration token from the device.</summary>
    public string? Token { get; init; }

    /// <summary>Device platform (currently "android").</summary>
    public string? Platform { get; init; }
}

/// <summary>Response for POST /device-tokens.</summary>
public sealed record RegisterDeviceTokenResponse
{
    public required bool Success { get; init; }
}
