using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using NSubstitute;
using WeredaConnect.Server.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Services;

/// <summary>ESC-29: a deleted account's appeal photos, every version, under its own prefix only.</summary>
public class AppealPhotoStoreTests
{
    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();

    [Fact]
    public async Task DeletesEveryVersionUnderTheUsersPrefix_AcrossPages()
    {
        var requests = new List<ListVersionsRequest>();
        _s3.ListVersionsAsync(Arg.Do<ListVersionsRequest>(requests.Add), Arg.Any<CancellationToken>()).Returns(
            new ListVersionsResponse
            {
                Versions = new List<S3ObjectVersion>
                {
                    new() { Key = "uploads/appeals/farmer-1/a.webp", VersionId = "v1" },
                    new() { Key = "uploads/appeals/farmer-1/a.webp", VersionId = "v2", IsDeleteMarker = true }
                },
                IsTruncated = true,
                NextKeyMarker = "uploads/appeals/farmer-1/a.webp",
                NextVersionIdMarker = "v2"
            },
            new ListVersionsResponse
            {
                Versions = new List<S3ObjectVersion> { new() { Key = "uploads/appeals/farmer-1/b.jpg", VersionId = "v3" } },
                IsTruncated = false
            });
        var deletes = new List<DeleteObjectsRequest>();
        _s3.DeleteObjectsAsync(Arg.Do<DeleteObjectsRequest>(deletes.Add), Arg.Any<CancellationToken>()).Returns(new DeleteObjectsResponse());

        var deleted = await new AppealPhotoStore(_s3, "uploads").DeleteAllAsync("farmer-1");

        deleted.Should().Be(3);
        requests.Should().OnlyContain(r => r.BucketName == "uploads" && r.Prefix == "uploads/appeals/farmer-1/");
        requests[1].KeyMarker.Should().Be("uploads/appeals/farmer-1/a.webp");
        requests[1].VersionIdMarker.Should().Be("v2");
        deletes.SelectMany(d => d.Objects).Select(o => o.VersionId).Should().Equal("v1", "v2", "v3");
    }

    [Fact]
    public async Task NothingStored_DeletesNothing()
    {
        _s3.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>()).Returns(new ListVersionsResponse());

        (await new AppealPhotoStore(_s3, "uploads").DeleteAllAsync("farmer-1")).Should().Be(0);
        await _s3.DidNotReceive().DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AVersionThatCannotBeDeleted_Throws_SoTheJobRunsAgain()
    {
        _s3.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>()).Returns(new ListVersionsResponse
        {
            Versions = new List<S3ObjectVersion> { new() { Key = "uploads/appeals/farmer-1/a.webp", VersionId = "v1" } }
        });
        _s3.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>()).Returns(new DeleteObjectsResponse
        {
            DeleteErrors = new List<DeleteError> { new() { Key = "uploads/appeals/farmer-1/a.webp", Code = "AccessDenied" } }
        });

        var act = () => new AppealPhotoStore(_s3, "uploads").DeleteAllAsync("farmer-1");

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(false, "uploads")]
    [InlineData(true, null)]
    [InlineData(true, "")]
    public void TheDeletionStep_DoesNotExist_WithTheFlagOff_OrWithoutABucket_AndBuildsNoClient(bool flag, string? bucket)
    {
        var built = 0;

        var step = AppealPhotoStore.DeletionStep(flag, bucket, () =>
        {
            built++;
            return _s3;
        });

        step.Should().BeNull();
        built.Should().Be(0);
        _s3.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task TheDeletionStep_WithTheFlagOn_DeletesTheUsersPhotos()
    {
        _s3.ListVersionsAsync(Arg.Any<ListVersionsRequest>(), Arg.Any<CancellationToken>()).Returns(new ListVersionsResponse
        {
            Versions = new List<S3ObjectVersion> { new() { Key = "uploads/appeals/farmer-1/a.webp", VersionId = "v1" } }
        });
        _s3.DeleteObjectsAsync(Arg.Any<DeleteObjectsRequest>(), Arg.Any<CancellationToken>()).Returns(new DeleteObjectsResponse());

        var step = AppealPhotoStore.DeletionStep(true, "uploads", () => _s3);

        step.Should().NotBeNull();
        (await step!("farmer-1")).Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("farmer-1/../farmer-2")]
    public async Task AnIdThatIsNoSinglePathSegment_ListsNothing(string userId)
    {
        (await new AppealPhotoStore(_s3, "uploads").DeleteAllAsync(userId)).Should().Be(0);
        _s3.ReceivedCalls().Should().BeEmpty();
    }
}
