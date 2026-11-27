using System.Runtime.CompilerServices;
using Amazon;

namespace WeredaConnect.Server;

/// <summary>
/// Process-wide AWS SDK settings, applied when this assembly loads (before any client is
/// created, in the Lambda host and in tests alike).
/// </summary>
internal static class AwsSdkDefaults
{
    /// <summary>
    /// AWS SDK v4 leaves request and response collections null by default. The repositories
    /// were written against v3, where a missing list or map is an empty one (for example a
    /// GetItem miss returns an empty <c>Item</c>), so keep that behaviour.
    /// </summary>
    [ModuleInitializer]
    internal static void Apply() => AWSConfigs.InitializeCollections = true;
}
