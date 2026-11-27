using System.Runtime.CompilerServices;
using Amazon;
using Amazon.DynamoDBv2.Model;
using WeredaConnect.Server.Data;
using Xunit;

namespace WeredaConnect.Server.Tests.Helpers;

/// <summary>
/// AWS SDK v4 leaves collections null unless <see cref="AWSConfigs.InitializeCollections"/> is
/// set. The server's module initializer sets it so the v3-era repositories keep reading a
/// missing list or map as an empty one.
/// </summary>
public class AwsSdkDefaultsTests
{
    [Fact]
    public void LoadingTheServer_StartsSdkCollectionsEmpty_AsInV3()
    {
        RuntimeHelpers.RunModuleConstructor(typeof(OrderRepository).Module.ModuleHandle);

        Assert.True(AWSConfigs.InitializeCollections);
        Assert.NotNull(new GetItemResponse().Item);
        Assert.Empty(new GetItemResponse().Item);
        Assert.NotNull(new QueryResponse().Items);
        Assert.NotNull(new QueryResponse().LastEvaluatedKey);
        Assert.Empty(new QueryResponse().LastEvaluatedKey);
    }
}
