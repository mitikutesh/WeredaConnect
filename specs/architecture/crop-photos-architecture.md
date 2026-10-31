# Crop Listing Photos: Architecture

**Status**: Implemented (branch `mobile/crop-listing-photos`), not yet deployed
**Contract**: `specs/contracts/crop-listing-api.md` 1.6.0 (endpoint 4, Photo Renditions, rule 9)
**Last Updated**: 2026-10-31

## 1. What it does

A farmer must add 2 to 4 photos when listing a crop, taken with the camera or chosen from the gallery. Buyers see a small cover photo on every marketplace card and the full photos, swipeable and zoomable, when they open the listing.

Only two renditions of each photo are ever kept: a 320 px thumbnail and a 1024 px "full" image, both WebP with all metadata removed. The original the phone uploads is deleted seconds after upload, so a 10 MB photo never sits in storage.

## 2. Flow

```
 Farmer phone                         API (CropFunction)              S3 incoming bucket        Photo processor Lambda        S3 photos bucket
 ────────────                         ──────────────────              ──────────────────        ──────────────────────        ────────────────
 pick 2-4 photos
 (image_picker shrinks to 1600 px,
  JPEG q85, ~250-600 KB each)
 save files + listing in Isar,
 queue the create (offline OK)
        │
 sync run: preflight ───────────────▶ POST /crops/{id}/photos
                                      farmer only, own listing id,
                                      1-4 photos, type + exact size
        ◀─────────────────────────── presigned PUT URLs (15 min,
                                      Content-Type + Content-Length signed)
 PUT bytes ─────────────────────────────────────────────────────────▶ {farmer}/{crop}/{photo}
                                                                        │ ObjectCreated
                                                                        └──────────────────▶ size cap, header check (≤ 40 MP),
                                                                                             auto-orient, strip EXIF/GPS/ICC,
                                                                                             thumb 320 px q60, full 1024 px q75 ──▶ …/thumb.webp
                                                                                             delete original                        …/full.webp
 POST /sync crop create
 with photoIds ─────────────────────▶ SyncFunction rule 9: 2-4 distinct UUIDs, stored as a list

 Buyer phone
 GET /crops ────────────────────────▶ role gate, then a presigned GET per thumb (6 h)
 list card shows the cover thumb ◀────────────────────────────────────────────────────────────────────────────────────────────── thumb.webp
 open detail: GET /crops/{id} ──────▶ role gate, then thumb + full GETs (full 1 h)
 gallery shows full, thumb as placeholder ◀───────────────────────────────────────────────────────────────────────────────────── full.webp
```

Web works the same without the queue: `WebCropListingRepository.createListing` uploads first and only then posts the create; a failed upload fails the save with its own message.

## 3. Sizes, chosen for Ethiopian networks and phones

| Step | Size | Why |
|------|------|-----|
| Phone pre-shrink before upload | longest edge 1600 px, JPEG q85, 250 to 600 KB | A 12 MP camera JPEG is 4 to 6 MB. Rural 3G uplinks are often 0.3 to 1 Mbps, so four originals would take minutes and cost the farmer real data. image_picker resizes natively on Android, so a 2 GB phone never decodes 12 MP in Dart. 1600 px leaves the server room to make a sharp 1024 px image. |
| Upload cap | 10 MiB per photo, signed into the URL | Stops abuse even from a modified client: S3 refuses a PUT whose length differs from the signed one. |
| Thumb | longest edge 320 px, WebP q60, 8 to 20 KB | The card shows it at 64 dp. On the common 720 x 1600 screens (2x to 3x density) that is 128 to 192 px, and a 4:3 photo at 320 px still has 240 px on its short side for the square crop. A page of 20 cards is about 300 KB of thumbs. |
| Full | longest edge 1024 px, WebP q75 (re-encoded at q60 if over 150 KB), 60 to 150 KB | A 360 to 412 dp wide phone at 2x shows about 720 to 820 px across, so 1024 px is sharp full-width with some pinch zoom left to judge grain quality. 100 KB loads in about 1 second on 1 Mbps 3G. |
| Decode guard | 40 megapixels, read from the header before decoding | A crafted "decompression bomb" header is refused without allocating pixels. |

WebP decodes natively on Android 4+, in Flutter on every platform and in current browsers, and is about 25 to 35 percent smaller than JPEG at the same quality.

## 4. Storage and cost

Two private buckets in the API stack (`ApiStack`): an S3 notification to a Lambda in another stack would create a cross-stack cycle.

| Bucket | Holds | Lifecycle | Versioning |
|--------|-------|-----------|------------|
| Incoming | originals for a few seconds | expire after 1 day, abort multipart after 1 day | off |
| Photos | `{farmerId}/{cropId}/{photoId}/thumb.webp` and `/full.webp` | expire after 365 days (listings expire after 30; a year covers orders and disputes), abort multipart after 1 day | off |

Both block all public access, use S3-managed encryption, refuse plain HTTP, and allow CORS only from the same origins as the HTTP API (the web app PUTs originals and Flutter web fetches images with XHR).

Storage class stays S3 Standard on purpose: renditions are 8 to 150 KB, and Standard-IA, Glacier Instant Retrieval and Intelligent-Tiering bill or monitor small objects as if they were 128 KB and add retrieval fees.

Rough cost at 10,000 listings with 4 photos each (eu-north-1 list prices):

| Item | Estimate |
|------|----------|
| Storage, about 500 KB per listing, 5 GB | about 0.12 USD a month |
| Writes: 4 PUTs of originals + 8 renditions + 4 deletes per listing | about 0.60 USD once for 10,000 listings |
| Processor: 1024 MB, about 1 s per photo, ARM64 | about 0.55 USD once for 10,000 listings |
| Reads: 1,000 buyers browsing daily (thumbs) + 1,000 detail opens | about 20 GB a month of transfer, inside the 100 GB monthly free tier |

Bytes never pass through a Lambda on the way in (presigned PUT straight to S3) or out (presigned GET straight from S3). Clients cache every image by photo id and rendition (`crop-photo:{id}:thumb|full`), never by URL, so a buyer downloads a photo once per device even though each read hands out a new presigned URL.

A CDN (CloudFront with signed URLs) was considered and left out: it adds a key pair to manage and a distribution, and only pays off well beyond the free transfer tier. Reads already go through one place (`CropFunction.SignPhotos`), so switching later touches one method and the clients keep working (they only see URLs).

## 5. Security and privacy

- **Role visibility (CLAUDE.md, critical)**: URLs are signed only after `GET /crops` and `GET /crops/{id}` pass their existing gate (farmer own listings, buyer and admin all, trucker none). There are no public objects, so a farmer cannot fetch another farmer's photos and a trucker cannot browse them.
- **Keys are never caller-chosen**: the upload key uses the caller's own user id, the read keys use the listing's stored `farmerId` and id. `photoIds` are validated as UUIDs on upload, on sync and on read. A farmer can never write into, or point a listing at, another farmer's photos.
- **Least privilege**: CropFunction may only `PutObject` on incoming and read the photos bucket (the presigned URLs carry those rights and no more; an infra test asserts it cannot delete). The processor may read and delete incoming and put to photos.
- **Metadata**: the processor applies the EXIF orientation, then drops EXIF (including GPS), ICC, XMP and IPTC. The farm's location never reaches a buyer.
- **Untrusted images**: only the JPEG, PNG and WebP decoders are registered, the 40 MP header check runs before decoding, the size cap runs before reading, and a bad file is deleted without a retry storm.
- **Account deletion**: `photoIds` is scrubbed with the other listing fields, so no URL is ever signed for those photos again. The objects expire with the bucket lifecycle.

### Dependency: SixLabors.ImageSharp 3.1.12

Pure managed code, so it compiles into the single Native AOT `bootstrap` with no native library to ship (a clean `dotnet publish -p:PublishAot=true` showed no IL trim or AOT warnings). License: Six Labors Split License, free under Apache 2.0 terms for organisations under 1M USD annual revenue.

3.1.12 has six advisories, all fixed in 3.2.0:

| Advisory | Severity | Area | Reachable here |
|----------|----------|------|----------------|
| GHSA-v76p-62qx-wwq2 | High | TIFF fax decoder | No: TIFF is not registered |
| GHSA-j9gm-c75j-xc9q | High | TIFF CCITT T4 encoder | No: we only encode WebP |
| GHSA-jjfr-hcj7-qf5w | High | TIFF CCITT T6 encoder | No: we only encode WebP |
| GHSA-j3p4-wp97-rph4 | High | HistogramEqualization | No: never called |
| GHSA-wmxv-xphr-5c9g | Moderate | BigTIFF IFD loop | No: TIFF is not registered |
| GHSA-gwg2-r3hj-4w44 | Moderate | ICC CLUT allocation | In principle, via an ICC profile in a JPEG/PNG/WebP: one oversized allocation, capped by the Lambda's 1024 MB, on the uploader's own photo |

**Decision (2026-10-31)**: the `vulnerability-scan` job in `.github/workflows/server-tests.yml` fails on any High, so it allow-lists exactly these six ids, with the reasons above. Moving to 3.2.0 removes them but needs a Six Labors license key at build time (a free community key; pass it as a `SixLaborsLicenseKey` secret to the server build, test and deploy workflows and to the Docker bundling command in `infra/lib/constructs/lambda-construct.ts`), then drop the allow-list.

## 6. Offline behaviour (mobile)

- The form keeps the picked photos in memory (decoded at tile size only). On save they go to app-private files `crop_photos/{listingId}/{photoId}.{ext}` and the listing (with local paths) to Isar, and the create is queued. Nothing needs a network.
- The sync executor's preflight (`CropPhotoSyncPreflight`) uploads a crop create's photos just before the create is sent. If that fails (offline, server error, a PUT that did not finish) the create and anything queued behind it for that listing wait for the next sync run, without using a retry. Each photo is marked uploaded in Isar as soon as its PUT succeeds, so a retry sends only the rest. A refusal that cannot change (`CROP_VALIDATION`, `CROP_FORBIDDEN`) or photo files that are gone let the create go and take the server's own answer.
- The farmer's own listing shows its local files offline; a marketplace pull merges the server's copy with those local paths instead of dropping them.
- Buyers see cached thumbs offline. Full images need a network the first time; after that they come from the image cache.

## 7. Failure modes

| What | Result |
|------|--------|
| Photo still processing (1 to 3 s) or refused by the processor | S3 answers 403/404; clients show the crop's sprout placeholder |
| Presigned GET expired (thumb 6 h, full 1 h, or Lambda credentials expiring first) | Cached images still show (cached by photo id); the next listing read hands out fresh URLs |
| Processor put fails | It throws, Lambda retries the event (2 retries); the work is idempotent |
| Processor never runs | The original expires after a day; the listing shows placeholders |
| Old app build (no `photoIds`) | Create refused with `CROP_VALIDATION` (rule 9); accepted while only dev builds exist |

## 8. Code map

| Concern | Files |
|---------|-------|
| Contract | `specs/contracts/crop-listing-api.md` 1.6.0 |
| Upload URLs, read URLs | `server/src/Functions/CropFunction.cs`, `server/src/Helpers/CropPhotoKeys.cs`, `server/src/Models/CropDtos.cs` |
| Sync rule 9 | `server/src/Functions/SyncFunction.cs` (`photoIds` in `AllowedCropFields`, `ValidateCropFields`), `server/src/Data/DynamoDbHelper.cs` (string list to DynamoDB `L`) |
| Processor | `server/src/Functions/CropPhotoProcessorFunction.cs`, `server/src/Services/CropPhotoRenditions.cs`, dispatch in `server/src/Program.cs` |
| Infra | `infra/lib/stacks/api-stack.ts` (buckets, processor, notification, grants, `POST /crops/{id}/photos`), `infra/lib/stacks/monitoring-stack.ts` (processor alarms) |
| Mobile models | `mobile/lib/core/database/schemas/crop_listing.dart` (`CropPhoto` embedded), `mobile/lib/core/models/web/crop_listing.dart` |
| Mobile capture and upload | `mobile/lib/features/crop_listing/domain/crop_photos.dart`, `data/services/crop_photo_picker.dart`, `crop_photo_uploader.dart`, `crop_photo_file_store.dart`, `crop_photo_sync_preflight.dart`, `crop_photo_urls.dart`; preflight hook in `mobile/lib/core/sync/sync_executor.dart` |
| Mobile UI | `presentation/widgets/crop_photo_picker_field.dart` (form), `crop_photo_views.dart` (thumb, gallery, viewer), `crop_photo_local_image*.dart` (io/web pair), `shared/widgets/listing_card.dart` (`leading`) |
| E2E scripts | `scripts/crop_photo_upload.py` used by `e2e_test.py`, `e2e_full_flow.py`, `e2e_three_party.py` |

## 9. Deploy and verify (runbook)

1. Deploy the server and infra together (the API stack creates both buckets, the processor and the route; the server binary carries the new handler). No data migration: listings made before 1.6.0 simply have no photos.
2. Smoke test with a farmer test user (`.ai/test-users.md`): `python3 scripts/e2e_full_flow.py` uploads two photos per listing before each create.
3. Check the processor ran: in CloudWatch logs of `wereda-connect-dev-crop-photo-processor` look for `Crop photo <id> processed: thumb N B, full N B.`; the incoming bucket should be empty a few seconds later.
4. As the buyer test user, `GET /crops` should show `photos[].thumbUrl`; opening one URL returns `image/webp`. `GET /crops/{id}` adds `fullUrl`.
5. On the web app, create a listing with photos from `localhost:8080` or the webapp domain (CORS is limited to the API's origins).

## 10. Follow-ups

- Upgrade ImageSharp to 3.2.0 with a license key and drop the CI allow-list (section 5).
- Photo uploads are not counted in the app's data-usage meter (the S3 client is a separate Dio).
- Match result cards still show the crop tile, not the cover photo (match responses carry no photos).
- Editing photos after create (the wire accepts `photoIds` on update; no screen yet).
- Amharic and Afan Oromo strings for the photo section need a native speaker's review.
- Account deletion could delete the farmer's photo objects outright instead of waiting for the lifecycle.
