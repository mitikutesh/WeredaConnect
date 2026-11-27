"""Crop listing photos for the e2e scripts (crop-listing-api.md 1.6.0).

Every crop create must carry 2 to 4 `photoIds` (rule 9). The device uploads
each photo first: `POST /crops/{id}/photos` hands out presigned PUT URLs with
the Content-Type and Content-Length signed in, then the bytes go straight to
S3. The S3-triggered processor makes the thumb and full renditions a moment
later; a create does not wait for them.

Usage:
    from crop_photo_upload import upload_crop_photos
    photo_ids = upload_crop_photos(base_url, token, crop_id)  # raises on failure
    data["photoIds"] = photo_ids
"""
import base64
import json
import ssl
import uuid
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen

# A valid 16x16 JPEG (777 bytes): small enough to send from any network.
TINY_JPEG = base64.b64decode(
    "/9j/4AAQSkZJRgABAQAASABIAAD/4QBMRXhpZgAATU0AKgAAAAgAAYdpAAQAAAABAAAAGgAAAAAA"
    "A6ABAAMAAAABAAEAAKACAAQAAAABAAAAEKADAAQAAAABAAAAEAAAAAD/7QA4UGhvdG9zaG9wIDMu"
    "MAA4QklNBAQAAAAAAAA4QklNBCUAAAAAABDUHYzZjwCyBOmACZjs+EJ+/8AAEQgAEAAQAwEiAAIR"
    "AQMRAf/EAB8AAAEFAQEBAQEBAAAAAAAAAAABAgMEBQYHCAkKC//EALUQAAIBAwMCBAMFBQQEAAAB"
    "fQECAwAEEQUSITFBBhNRYQcicRQygZGhCCNCscEVUtHwJDNicoIJChYXGBkaJSYnKCkqNDU2Nzg5"
    "OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6g4SFhoeIiYqSk5SVlpeYmZqio6Slpqeo"
    "qaqys7S1tre4ubrCw8TFxsfIycrS09TV1tfY2drh4uPk5ebn6Onq8fLz9PX29/j5+v/EAB8BAAMB"
    "AQEBAQEBAQEAAAAAAAABAgMEBQYHCAkKC//EALURAAIBAgQEAwQHBQQEAAECdwABAgMRBAUhMQYS"
    "QVEHYXETIjKBCBRCkaGxwQkjM1LwFWJy0QoWJDThJfEXGBkaJicoKSo1Njc4OTpDREVGR0hJSlNU"
    "VVZXWFlaY2RlZmdoaWpzdHV2d3h5eoKDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5"
    "usLDxMXGx8jJytLT1NXW19jZ2uLj5OXm5+jp6vLz9PX29/j5+v/bAEMAAgICAgICAwICAwUDAwMF"
    "BgUFBQUGCAYGBgYGCAoICAgICAgKCgoKCgoKCgwMDAwMDA4ODg4ODw8PDw8PDw8PD//bAEMBAgIC"
    "BAQEBwQEBxALCQsQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQ"
    "EBAQEP/dAAQAAf/aAAwDAQACEQMRAD8A7iiiiv8AQA+XP//Z"
)


class CropPhotoUploadError(RuntimeError):
    """The upload URLs or a PUT failed; the listing must not be created."""


def upload_crop_photos(base_url: str, token: str, crop_id: str, count: int = 2) -> list:
    """Upload `count` photos for listing `crop_id` and return their ids."""
    photo_ids = [str(uuid.uuid4()) for _ in range(count)]
    body = json.dumps({
        "photos": [
            {"photoId": pid, "contentType": "image/jpeg", "sizeBytes": len(TINY_JPEG)}
            for pid in photo_ids
        ]
    }).encode()
    ctx = ssl.create_default_context()
    req = Request(f"{base_url}/crops/{crop_id}/photos", data=body, method="POST")
    req.add_header("Content-Type", "application/json")
    req.add_header("Authorization", f"Bearer {token}")
    try:
        with urlopen(req, timeout=30, context=ctx) as r:
            uploads = json.loads(r.read().decode()).get("uploads", [])
    except HTTPError as e:
        detail = e.read().decode() if e.fp else ""
        raise CropPhotoUploadError(f"upload URLs refused ({e.code}): {detail[:200]}") from e
    except URLError as e:
        raise CropPhotoUploadError(f"upload URLs unreachable: {e}") from e

    urls = {u.get("photoId"): u.get("uploadUrl") for u in uploads}
    for pid in photo_ids:
        url = urls.get(pid)
        if not url:
            raise CropPhotoUploadError(f"no upload URL for photo {pid}")
        # No Authorization header: the signature is in the URL. Both headers
        # below are signed, so they must match what was announced.
        put = Request(url, data=TINY_JPEG, method="PUT")
        put.add_header("Content-Type", "image/jpeg")
        put.add_header("Content-Length", str(len(TINY_JPEG)))
        try:
            with urlopen(put, timeout=60, context=ctx) as r:
                if r.status not in (200, 204):
                    raise CropPhotoUploadError(f"photo PUT answered {r.status}")
        except HTTPError as e:
            raise CropPhotoUploadError(f"photo PUT refused ({e.code})") from e
        except URLError as e:
            raise CropPhotoUploadError(f"photo PUT unreachable: {e}") from e
    return photo_ids
