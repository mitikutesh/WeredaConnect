/**
 * Crop listing photos for API-level e2e setup (crop-listing-api.md 1.6.0).
 *
 * Every crop create must carry 2 to 4 `photoIds` (rule 9), and the listing id
 * must be a UUID (`POST /crops/{id}/photos` refuses anything else). The
 * photos go up first: the endpoint hands out presigned PUT URLs with the
 * Content-Type and Content-Length signed in, then the bytes go straight to S3.
 */
import { API_URL } from "./auth";

/** A valid 16x16 JPEG (777 bytes). */
const TINY_JPEG = Buffer.from(
  "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAAA6ABAAMAAAABAAEAAKACAAQAAAABAAAAEKADAAQAAAABAAAAEAAAAAD/7QA4UGhvdG9zaG9wIDMuMAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgAEAAQAwEiAAIRAQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAABfQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeoqaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMBAQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYSQVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNUVVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMAAgICAgICAwICAwUDAwMFBgUFBQUGCAYGBgYGCAoICAgICAgKCgoKCgoKCgwMDAwMDA4ODg4ODw8PDw8PDw8PD//bAEMBAgICBAQEBwQEBxALCQsQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEP/dAAQAAf/aAAwDAQACEQMRAD8A7iiiiv8AQA+XP//Z",
  "base64",
);

/**
 * Upload `count` photos for listing `listingId` (a UUID) as the farmer whose
 * ID token is `token`, and return their ids for the create's `photoIds`.
 * Throws when the URLs or a PUT fail.
 */
export async function uploadCropPhotos(
  token: string,
  listingId: string,
  count = 2,
): Promise<string[]> {
  const photoIds = Array.from({ length: count }, () => crypto.randomUUID());
  const res = await fetch(`${API_URL}/crops/${listingId}/photos`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({
      photos: photoIds.map((photoId) => ({
        photoId,
        contentType: "image/jpeg",
        sizeBytes: TINY_JPEG.length,
      })),
    }),
  });
  if (!res.ok) {
    throw new Error(`POST /crops/${listingId}/photos failed: ${res.status} ${await res.text()}`);
  }
  const { uploads } = (await res.json()) as {
    uploads: Array<{ photoId: string; uploadUrl: string }>;
  };
  for (const photoId of photoIds) {
    const url = uploads.find((u) => u.photoId === photoId)?.uploadUrl;
    if (!url) throw new Error(`no upload URL for photo ${photoId}`);
    // No Authorization header: the signature is in the URL. Node's fetch sets
    // Content-Length from the body, which is the signed length.
    const put = await fetch(url, {
      method: "PUT",
      headers: { "Content-Type": "image/jpeg" },
      body: TINY_JPEG,
    });
    if (!put.ok) throw new Error(`photo PUT failed: ${put.status}`);
  }
  return photoIds;
}
