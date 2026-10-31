using System.Text.Json;
using System.Web;
using Amazon;
using Amazon.DynamoDBv2.Model;
using Amazon.Lambda.APIGatewayEvents;
using Amazon.Lambda.TestUtilities;
using Amazon.Runtime;
using Amazon.S3;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using WeredaConnect.Server.Data;
using WeredaConnect.Server.Functions;
using Xunit;

namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// crop-listing-api 1.6.0: POST /crops/{id}/photos (presigned PUTs) and the presigned GETs that
/// GET /crops and GET /crops/{id} add. Signing uses a real S3 client with fake credentials:
/// presigning is offline, so the URLs are the ones production would hand out.
/// </summary>
public class CropFunctionPhotoTests
{
    private const string IncomingBucket = "test-crop-photo-incoming";
    private const string PhotosBucket = "test-crop-photos";
    private const string FarmerId = "0b6f2c1e-9a3d-4e5f-8a7b-1c2d3e4f5a6b";
    private const string OtherFarmerId = "1c7a3d2f-0b4e-4f6a-9b8c-2d3e4f5a6b7c";
    private const string CropId = "2d8b4e3a-1c5f-4a7b-8c9d-3e4f5a6b7c8d";
    private const string Photo1 = "7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11";
    private const string Photo2 = "8e2d1b63-4a1f-4d5c-8b9f-1a4c7d3e0f22";

    private readonly ICropRepository _cropRepo = Substitute.For<ICropRepository>();
    private readonly TestLambdaContext _context = new();
    private readonly IAmazonS3 _s3 = new AmazonS3Client(
        new BasicAWSCredentials("AKIDEXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY"),
        RegionEndpoint.EUNorth1);

    public CropFunctionPhotoTests()
    {
        Environment.SetEnvironmentVariable("PAGINATION_SECRET", "test-pagination-secret-for-unit-tests");
    }

    private CropFunction Function(bool configured = true) => configured
        ? new CropFunction(_cropRepo, _s3, IncomingBucket, PhotosBucket)
        : new CropFunction(_cropRepo);

    private static APIGatewayProxyRequest Request(
        string method, string path, string userId, string role,
        Dictionary<string, string>? pathParams = null, string? body = null) => new()
        {
            HttpMethod = method,
            Path = path,
            PathParameters = pathParams,
            Body = body,
            RequestContext = new APIGatewayProxyRequest.ProxyRequestContext
            {
                Authorizer = new APIGatewayCustomAuthorizerContext
                {
                    { "sub", userId },
                    { "custom:role", role }
                }
            }
        };

    private static APIGatewayProxyRequest UploadRequest(
        string body, string userId = FarmerId, string role = "farmer", string cropId = CropId)
        => Request("POST", $"/crops/{cropId}/photos", userId, role,
            new Dictionary<string, string> { { "id", cropId } }, body);

    private static string PhotosBody(params (string PhotoId, string ContentType, long SizeBytes)[] photos)
        => JsonSerializer.Serialize(new
        {
            photos = photos.Select(p => new { photoId = p.PhotoId, contentType = p.ContentType, sizeBytes = p.SizeBytes })
        });

    private static Dictionary<string, object> Listing(string farmerId = FarmerId, List<object>? photoIds = null)
    {
        var item = new Dictionary<string, object>
        {
            { "cropId", CropId },
            { "farmerId", farmerId },
            { "cropType", "teff" },
            { "woreda", "Adama" },
            { "quantity", "100" },
            { "pricePerKg", "45.5" },
            { "status", "available" },
            { "createdAt", "2026-10-31T00:00:00Z" },
            { "updatedAt", "2026-10-31T00:00:00Z" },
            { "version", "1" }
        };
        if (photoIds != null)
        {
            item["photoIds"] = photoIds;
        }

        return item;
    }

    private static string ErrorCode(string body)
        => JsonDocument.Parse(body).RootElement.GetProperty("error").GetProperty("code").GetString()!;

    // ── POST /crops/{id}/photos ──────────────────────────────────────────────

    [Fact]
    public async Task Upload_NewListing_SignsOnePutPerPhoto_KeyedByTheCaller()
    {
        _cropRepo.GetByIdAsync(CropId).Returns((Dictionary<string, object>?)null);

        var response = await Function().FunctionHandler(
            UploadRequest(PhotosBody((Photo1, "image/jpeg", 412873), (Photo2.ToUpperInvariant(), "image/png", 2048))),
            _context);

        response.StatusCode.Should().Be(200);
        var root = JsonDocument.Parse(response.Body).RootElement;
        root.GetProperty("expiresInSeconds").GetInt32().Should().Be(900);
        var uploads = root.GetProperty("uploads").EnumerateArray().ToList();
        uploads.Should().HaveCount(2);

        uploads[0].GetProperty("photoId").GetString().Should().Be(Photo1);
        uploads[0].GetProperty("contentType").GetString().Should().Be("image/jpeg");
        uploads[0].GetProperty("sizeBytes").GetInt64().Should().Be(412873);
        var url = new Uri(uploads[0].GetProperty("uploadUrl").GetString()!);
        url.Host.Should().StartWith(IncomingBucket);
        url.AbsolutePath.Should().Be($"/{FarmerId}/{CropId}/{Photo1}");

        // The photo id is lower-cased, as rule 9 stores it.
        uploads[1].GetProperty("photoId").GetString().Should().Be(Photo2);
        new Uri(uploads[1].GetProperty("uploadUrl").GetString()!).AbsolutePath
            .Should().Be($"/{FarmerId}/{CropId}/{Photo2}");
    }

    [Fact]
    public async Task Upload_SignsContentTypeAndContentLength_For15Minutes()
    {
        var response = await Function().FunctionHandler(
            UploadRequest(PhotosBody((Photo1, "image/webp", 4096))), _context);

        response.StatusCode.Should().Be(200);
        var uploadUrl = JsonDocument.Parse(response.Body).RootElement
            .GetProperty("uploads")[0].GetProperty("uploadUrl").GetString()!;
        var query = HttpUtility.ParseQueryString(new Uri(uploadUrl).Query);

        query["X-Amz-SignedHeaders"]!.Split(';').Should().Contain(new[] { "content-length", "content-type", "host" });
        int.Parse(query["X-Amz-Expires"]!).Should().BeInRange(895, 900);
    }

    [Fact]
    public async Task Upload_OwnExistingListing_IsSigned()
    {
        _cropRepo.GetByIdAsync(CropId).Returns(Listing());

        var response = await Function().FunctionHandler(
            UploadRequest(PhotosBody((Photo1, "image/jpeg", 10))), _context);

        response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Upload_AnotherFarmersListing_Is403()
    {
        _cropRepo.GetByIdAsync(CropId).Returns(Listing(OtherFarmerId));

        var response = await Function().FunctionHandler(
            UploadRequest(PhotosBody((Photo1, "image/jpeg", 10))), _context);

        response.StatusCode.Should().Be(403);
        ErrorCode(response.Body).Should().Be("CROP_FORBIDDEN");
        response.Body.Should().NotContain("X-Amz");
    }

    [Theory]
    [InlineData("buyer")]
    [InlineData("trucker")]
    [InlineData("admin")]
    [InlineData("")]
    public async Task Upload_NotAFarmer_Is403(string role)
    {
        var response = await Function().FunctionHandler(
            UploadRequest(PhotosBody((Photo1, "image/jpeg", 10)), role: role), _context);

        response.StatusCode.Should().Be(403);
        ErrorCode(response.Body).Should().Be("CROP_FORBIDDEN");
        await _cropRepo.DidNotReceive().GetByIdAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task Upload_Unauthenticated_Is403()
    {
        var request = UploadRequest(PhotosBody((Photo1, "image/jpeg", 10)));
        request.RequestContext = null;

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(403);
        ErrorCode(response.Body).Should().Be("CROP_FORBIDDEN");
    }

    [Theory]
    [InlineData("crop-001")]
    [InlineData("2d8b4e3a1c5f4a7b8c9d3e4f5a6b7c8d")]
    [InlineData("{2d8b4e3a-1c5f-4a7b-8c9d-3e4f5a6b7c8d}")]
    public async Task Upload_ListingIdNotAUuid_Is400(string cropId)
    {
        var response = await Function().FunctionHandler(
            UploadRequest(PhotosBody((Photo1, "image/jpeg", 10)), cropId: cropId), _context);

        response.StatusCode.Should().Be(400);
        ErrorCode(response.Body).Should().Be("CROP_VALIDATION");
    }

    public static IEnumerable<object[]> BadBodies() => new[]
    {
        new object[] { "" },
        new object[] { "not json" },
        new object[] { "{}" },
        new object[] { "{\"photos\":[]}" },
        new object[] { "{\"photos\":null}" },
        new object[] { "{\"photos\":[null]}" },
        new object[] { PhotosBody(
            (Photo1, "image/jpeg", 1), (Photo2, "image/jpeg", 1),
            ("9f3e2c74-5b2a-4e6d-9c0a-2b5d8e4f1a33", "image/jpeg", 1),
            ("a04f3d85-6c3b-4f7e-8d1b-3c6e9f5a2b44", "image/jpeg", 1),
            ("b15a4e96-7d4c-4a8f-9e2c-4d7f0a6b3c55", "image/jpeg", 1)) },
        new object[] { PhotosBody(("not-a-uuid", "image/jpeg", 1)) },
        new object[] { PhotosBody((Photo1, "image/jpeg", 1), (Photo1.ToUpperInvariant(), "image/jpeg", 1)) },
        new object[] { PhotosBody((Photo1, "image/gif", 1)) },
        new object[] { PhotosBody((Photo1, "IMAGE/JPEG", 1)) },
        new object[] { PhotosBody((Photo1, "image/jpeg", 0)) },
        new object[] { PhotosBody((Photo1, "image/jpeg", 10485761)) },
        new object[] { "{\"photos\":[{\"photoId\":\"" + Photo1 + "\",\"contentType\":\"image/jpeg\"}]}" },
        new object[] { "{\"photos\":[{\"photoId\":\"" + Photo1 + "\",\"contentType\":\"image/jpeg\",\"sizeBytes\":1.5}]}" },
        new object[] { "{\"photos\":[{\"photoId\":\"" + Photo1 + "\",\"contentType\":\"image/jpeg\",\"sizeBytes\":\"10\"}]}" },
    };

    [Theory]
    [MemberData(nameof(BadBodies))]
    public async Task Upload_BadBody_Is400_AndSignsNothing(string body)
    {
        var response = await Function().FunctionHandler(UploadRequest(body), _context);

        response.StatusCode.Should().Be(400);
        ErrorCode(response.Body).Should().Be("CROP_VALIDATION");
        response.Body.Should().NotContain("X-Amz");
    }

    [Fact]
    public async Task Upload_MaxSize_IsAccepted()
    {
        var response = await Function().FunctionHandler(
            UploadRequest(PhotosBody((Photo1, "image/jpeg", 10485760))), _context);

        response.StatusCode.Should().Be(200);
    }

    [Fact]
    public async Task Upload_StorageNotConfigured_Is500()
    {
        var response = await Function(configured: false).FunctionHandler(
            UploadRequest(PhotosBody((Photo1, "image/jpeg", 10))), _context);

        response.StatusCode.Should().Be(500);
        ErrorCode(response.Body).Should().Be("CROP_SERVER_ERROR");
    }

    [Fact]
    public async Task Upload_SigningFails_Is500()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<Amazon.S3.Model.GetPreSignedUrlRequest>())
            .Throws(new AmazonS3Exception("no credentials"));
        var function = new CropFunction(_cropRepo, s3, IncomingBucket, PhotosBucket);

        var response = await function.FunctionHandler(UploadRequest(PhotosBody((Photo1, "image/jpeg", 10))), _context);

        response.StatusCode.Should().Be(500);
        ErrorCode(response.Body).Should().Be("CROP_SERVER_ERROR");
    }

    [Fact]
    public async Task Upload_GetWithPhotosPath_IsNotAnUpload()
    {
        // GET /crops/{id}/photos is not a route: the GET by id handler answers.
        _cropRepo.GetByIdAsync(CropId).Returns(Listing());
        var request = Request("GET", $"/crops/{CropId}", FarmerId, "farmer",
            new Dictionary<string, string> { { "id", CropId } });

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().NotContain("uploads");
    }

    // ── Reads: presigned GETs after the role gate ────────────────────────────

    private void ListReturns(params Dictionary<string, object>[] items)
        => _cropRepo.QueryCropsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<int>(), Arg.Any<Dictionary<string, AttributeValue>?>())
            .Returns((items.ToList(), null));

    [Fact]
    public async Task List_GivesThumbUrlsOnly_InOrder_For6Hours()
    {
        ListReturns(Listing(photoIds: new List<object> { Photo2, Photo1 }));

        var response = await Function().FunctionHandler(Request("GET", "/crops", "buyer-1", "buyer"), _context);

        response.StatusCode.Should().Be(200);
        var photos = JsonDocument.Parse(response.Body).RootElement
            .GetProperty("crops")[0].GetProperty("photos").EnumerateArray().ToList();
        photos.Select(p => p.GetProperty("photoId").GetString()).Should().Equal(Photo2, Photo1);
        photos.Should().AllSatisfy(p => p.TryGetProperty("fullUrl", out _).Should().BeFalse());

        var thumb = new Uri(photos[0].GetProperty("thumbUrl").GetString()!);
        thumb.Host.Should().StartWith(PhotosBucket);
        thumb.AbsolutePath.Should().Be($"/{FarmerId}/{CropId}/{Photo2}/thumb.webp");
        var query = HttpUtility.ParseQueryString(thumb.Query);
        int.Parse(query["X-Amz-Expires"]!).Should().BeInRange(6 * 3600 - 5, 6 * 3600);
    }

    [Fact]
    public async Task Detail_AddsFullUrls_For1Hour()
    {
        _cropRepo.GetByIdAsync(CropId).Returns(Listing(photoIds: new List<object> { Photo1, Photo2 }));
        var request = Request("GET", $"/crops/{CropId}", "buyer-1", "buyer",
            new Dictionary<string, string> { { "id", CropId } });

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200);
        var photos = JsonDocument.Parse(response.Body).RootElement
            .GetProperty("crop").GetProperty("photos").EnumerateArray().ToList();
        photos.Should().HaveCount(2);
        var full = new Uri(photos[1].GetProperty("fullUrl").GetString()!);
        full.AbsolutePath.Should().Be($"/{FarmerId}/{CropId}/{Photo2}/full.webp");
        int.Parse(HttpUtility.ParseQueryString(full.Query)["X-Amz-Expires"]!).Should().BeInRange(3600 - 5, 3600);
        new Uri(photos[1].GetProperty("thumbUrl").GetString()!).AbsolutePath
            .Should().Be($"/{FarmerId}/{CropId}/{Photo2}/thumb.webp");
    }

    [Fact]
    public async Task Reads_KeysComeFromTheListingsOwnFarmer_NotTheCaller()
    {
        // The owning farmer reads their own listing: keys are the item's farmerId either way.
        ListReturns(Listing(photoIds: new List<object> { Photo1, Photo2 }));

        var response = await Function().FunctionHandler(Request("GET", "/crops", FarmerId, "farmer"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain($"/{FarmerId}/{CropId}/{Photo1}/thumb.webp");
    }

    [Fact]
    public async Task Reads_ListingWithoutPhotos_OmitsPhotos()
    {
        ListReturns(Listing());

        var response = await Function().FunctionHandler(Request("GET", "/crops", "buyer-1", "buyer"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().NotContain("\"photos\"");
    }

    [Fact]
    public async Task Reads_StorageNotConfigured_OmitsPhotos_AndStillAnswers()
    {
        ListReturns(Listing(photoIds: new List<object> { Photo1, Photo2 }));

        var response = await Function(configured: false)
            .FunctionHandler(Request("GET", "/crops", "buyer-1", "buyer"), _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().NotContain("\"photos\"");
    }

    [Fact]
    public async Task Reads_SigningFails_OmitsPhotos_AndStillAnswers()
    {
        var s3 = Substitute.For<IAmazonS3>();
        s3.GetPreSignedURL(Arg.Any<Amazon.S3.Model.GetPreSignedUrlRequest>())
            .Throws(new AmazonS3Exception("no credentials"));
        _cropRepo.GetByIdAsync(CropId).Returns(Listing(photoIds: new List<object> { Photo1, Photo2 }));
        var request = Request("GET", $"/crops/{CropId}", "buyer-1", "buyer",
            new Dictionary<string, string> { { "id", CropId } });

        var response = await new CropFunction(_cropRepo, s3, IncomingBucket, PhotosBucket)
            .FunctionHandler(request, _context);

        response.StatusCode.Should().Be(200);
        response.Body.Should().NotContain("\"photos\"");
    }

    [Fact]
    public async Task Reads_SkipStoredIdsThatAreNotUuids()
    {
        ListReturns(Listing(photoIds: new List<object> { Photo1, "../other-farmer/x", Photo2 }));

        var response = await Function().FunctionHandler(Request("GET", "/crops", "buyer-1", "buyer"), _context);

        var photos = JsonDocument.Parse(response.Body).RootElement
            .GetProperty("crops")[0].GetProperty("photos").EnumerateArray()
            .Select(p => p.GetProperty("photoId").GetString()).ToList();
        photos.Should().Equal(Photo1, Photo2);
        response.Body.Should().NotContain("other-farmer");
    }

    [Fact]
    public async Task Reads_Trucker_StillForbidden_AndSignsNothing()
    {
        ListReturns(Listing(photoIds: new List<object> { Photo1, Photo2 }));

        var response = await Function().FunctionHandler(Request("GET", "/crops", "trucker-1", "trucker"), _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().NotContain("X-Amz");
    }

    [Fact]
    public async Task Detail_AnotherFarmer_StillForbidden_AndSignsNothing()
    {
        _cropRepo.GetByIdAsync(CropId).Returns(Listing(photoIds: new List<object> { Photo1, Photo2 }));
        var request = Request("GET", $"/crops/{CropId}", OtherFarmerId, "farmer",
            new Dictionary<string, string> { { "id", CropId } });

        var response = await Function().FunctionHandler(request, _context);

        response.StatusCode.Should().Be(403);
        response.Body.Should().NotContain("X-Amz");
    }
}
