using System.Net;
using System.Text;
using Amazon.Lambda.TestUtilities;
using Amazon.S3;
using Amazon.S3.Model;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Functions;
using WeredaConnect.Server.Tests.Services;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// crop-listing-api 1.6.0, Photo Renditions: the S3-triggered processor writes the thumb and the
/// full WebP to the photos bucket and deletes the original; it deletes and never retries what it
/// refuses, and throws (so Lambda retries) only when a write fails.
/// </summary>
public class CropPhotoProcessorFunctionTests
{
    private const string Incoming = "incoming-bucket";
    private const string Photos = "photos-bucket";
    private const string Farmer = "11111111-2222-4333-8444-555555555555";
    private const string Crop = "66666666-7777-4888-9999-aaaaaaaaaaaa";
    private const string Photo = "7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11";
    private static readonly string Key = $"{Farmer}/{Crop}/{Photo}";

    private readonly IAmazonS3 _s3 = Substitute.For<IAmazonS3>();
    private readonly TestLambdaContext _ctx = new();
    private readonly List<PutObjectRequest> _puts = new();
    private readonly CropPhotoProcessorFunction _function;

    public CropPhotoProcessorFunctionTests()
    {
        _function = new CropPhotoProcessorFunction(_s3, Incoming, Photos);
        _s3.PutObjectAsync(Arg.Do<PutObjectRequest>(_puts.Add), Arg.Any<CancellationToken>())
            .Returns(new PutObjectResponse());
        _s3.DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>())
            .Returns(new DeleteObjectResponse());
    }

    private void StoreOriginal(byte[] bytes)
    {
        _s3.GetObjectAsync(
                Arg.Is<GetObjectRequest>(r => r.BucketName == Incoming && r.Key == Key),
                Arg.Any<CancellationToken>())
            .Returns(_ => new GetObjectResponse
            {
                ResponseStream = new MemoryStream(bytes),
                ContentLength = bytes.Length,
            });
    }

    private void VerifyOriginalDeleted() =>
        _s3.Received(1).DeleteObjectAsync(
            Arg.Is<DeleteObjectRequest>(r => r.BucketName == Incoming && r.Key == Key),
            Arg.Any<CancellationToken>());

    [Fact]
    public async Task Process_ValidPhoto_WritesThumbAndFullThenDeletesTheOriginal()
    {
        StoreOriginal(CropPhotoRenditionsTests.Jpeg(1600, 1200));

        var outcome = await _function.ProcessObjectAsync(Incoming, Key, 1000, _ctx);

        outcome.Should().Be(CropPhotoOutcome.Processed);
        _puts.Select(p => p.Key).Should().BeEquivalentTo(
            $"{Key}/thumb.webp", $"{Key}/full.webp");
        _puts.Should().OnlyContain(p =>
            p.BucketName == Photos
            && p.ContentType == "image/webp"
            && p.Headers.CacheControl == "private, max-age=31536000, immutable");
        VerifyOriginalDeleted();
    }

    [Theory]
    [InlineData("not-a-uuid/66666666-7777-4888-9999-aaaaaaaaaaaa/7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11")]
    [InlineData("11111111-2222-4333-8444-555555555555/66666666-7777-4888-9999-aaaaaaaaaaaa")]
    [InlineData("11111111-2222-4333-8444-555555555555/../7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11")]
    public async Task Process_BadKey_IsDeletedWithoutReadingIt(string key)
    {
        var outcome = await _function.ProcessObjectAsync(Incoming, key, 1000, _ctx);

        outcome.Should().Be(CropPhotoOutcome.Refused);
        await _s3.DidNotReceive().GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>());
        await _s3.Received(1).DeleteObjectAsync(
            Arg.Is<DeleteObjectRequest>(r => r.Key == key), Arg.Any<CancellationToken>());
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Process_EventSizeOver10MiB_IsDeletedWithoutReadingIt()
    {
        var outcome = await _function.ProcessObjectAsync(Incoming, Key, 10L * 1024 * 1024 + 1, _ctx);

        outcome.Should().Be(CropPhotoOutcome.Refused);
        await _s3.DidNotReceive().GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>());
        VerifyOriginalDeleted();
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Process_NotAnImage_IsDeletedAndNothingWritten()
    {
        StoreOriginal(Encoding.UTF8.GetBytes("this is not a photo"));

        var outcome = await _function.ProcessObjectAsync(Incoming, Key, 19, _ctx);

        outcome.Should().Be(CropPhotoOutcome.Refused);
        VerifyOriginalDeleted();
        _puts.Should().BeEmpty();
    }

    [Fact]
    public async Task Process_OriginalAlreadyGone_IsARetryWithNothingToDo()
    {
        _s3.GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonS3Exception("gone") { StatusCode = HttpStatusCode.NotFound });

        var outcome = await _function.ProcessObjectAsync(Incoming, Key, 1000, _ctx);

        outcome.Should().Be(CropPhotoOutcome.AlreadyGone);
        _puts.Should().BeEmpty();
        await _s3.DidNotReceive().DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_OtherBucket_IsLeftAlone()
    {
        var outcome = await _function.ProcessObjectAsync("someone-elses-bucket", Key, 1000, _ctx);

        outcome.Should().Be(CropPhotoOutcome.Ignored);
        await _s3.DidNotReceive().GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>());
        await _s3.DidNotReceive().DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Process_PutFails_ThrowsForARetryAndKeepsTheOriginal()
    {
        StoreOriginal(CropPhotoRenditionsTests.Jpeg(800, 600));
        _s3.PutObjectAsync(Arg.Any<PutObjectRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new AmazonS3Exception("slow down") { StatusCode = HttpStatusCode.ServiceUnavailable });

        var act = () => _function.ProcessObjectAsync(Incoming, Key, 1000, _ctx);

        await act.Should().ThrowAsync<AmazonS3Exception>();
        await _s3.DidNotReceive().DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_S3Event_DecodesTheKeyAndProcessesEveryRecord()
    {
        StoreOriginal(CropPhotoRenditionsTests.Jpeg(640, 480));
        // Event keys are URL-encoded ('/' as %2F here).
        var encoded = Uri.EscapeDataString(Key);
        string Record(string key, int size) =>
            "{\"s3\":{\"bucket\":{\"name\":\"" + Incoming + "\"},\"object\":{\"key\":\""
            + key + "\",\"size\":" + size + "}}}";
        var json = "{\"Records\":[" + Record(encoded, 1000) + "," + Record("bad-key", 10) + "]}";

        await _function.HandleAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), _ctx);

        _puts.Should().HaveCount(2);
        VerifyOriginalDeleted();
        await _s3.Received(1).DeleteObjectAsync(
            Arg.Is<DeleteObjectRequest>(r => r.Key == "bad-key"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TestEvent_DoesNothing()
    {
        const string json = """{"Service":"Amazon S3","Event":"s3:TestEvent","Bucket":"incoming-bucket"}""";

        await _function.HandleAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), _ctx);

        await _s3.DidNotReceive().GetObjectAsync(Arg.Any<GetObjectRequest>(), Arg.Any<CancellationToken>());
        await _s3.DidNotReceive().DeleteObjectAsync(Arg.Any<DeleteObjectRequest>(), Arg.Any<CancellationToken>());
    }
}
