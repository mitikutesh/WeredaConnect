namespace WeredaConnect.Server.Data;

/// <summary>
/// Stores the mapping from a user to their SNS platform endpoint ARNs (one per
/// registered device). Table key: PK = userId, SK = endpointArn.
/// </summary>
public interface IDeviceTokenRepository
{
    /// <summary>Upsert a device registration (idempotent on the endpoint ARN).</summary>
    Task PutAsync(string userId, string endpointArn, string platform, string token);

    /// <summary>All SNS endpoint ARNs registered for a user (for fan-out publish).</summary>
    Task<List<string>> GetEndpointArnsByUserAsync(string userId);

    /// <summary>
    /// Remove a device registration. Called when SNS reports an endpoint as
    /// disabled/not-found during publish, so dead endpoints self-heal instead of
    /// being retried (and re-logged) on every future matching crop.
    /// </summary>
    Task DeleteAsync(string userId, string endpointArn);
}
