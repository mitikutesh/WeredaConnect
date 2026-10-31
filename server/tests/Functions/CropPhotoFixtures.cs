namespace WeredaConnect.Server.Tests.Functions;

/// <summary>
/// crop-listing-api 1.6.0 rule 9: every crop create carries 2 to 4 distinct photo ids. Test
/// fixtures that create a listing send these.
/// </summary>
internal static class CropPhotoFixtures
{
    public const string Photo1 = "7d1c0a52-3f0e-4c4b-9a8e-0f3b6c2d9e11";
    public const string Photo2 = "8e2d1b63-4a1f-4d5c-8b9f-1a4c7d3e0f22";
    public const string Photo3 = "9f3e2c74-5b2a-4e6d-9c0a-2b5d8e4f1a33";
    public const string Photo4 = "a04f3d85-6c3b-4f7e-8d1b-3c6e9f5a2b44";
    public const string Photo5 = "b15a4e96-7d4c-4a8f-9e2c-4d7f0a6b3c55";

    /// <summary>A fresh valid photoIds value (two ids), as a client sends it.</summary>
    public static string[] ValidPhotoIds() => new[] { Photo1, Photo2 };
}
