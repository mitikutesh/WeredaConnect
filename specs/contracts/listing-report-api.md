# Listing Report Contract (report a listing, admin queue)

## Version
1.0.0

## Parties
- **Mobile / Web** (client): the buyer's "Report this listing" button and sheet on the crop listing and trip detail screens. Mobile queues the report in the sync queue; web sends it straight to `/sync`. Mobile and web are a platform pair and ship together.
- **Backend** (server): the `listing_report` sync entity (`SyncFunction.ListingReport.cs`) and the admin queue routes (`AdminFunction.ListingReports.cs`).
- **Admin**: the Listing Reports page (`/listing-reports`).
- **Infrastructure**: the `listing-reports` table, its grants and the two admin routes.

## Why

Audio notes (`audioNoteRef`) and photos cannot be screened for phone numbers (FUP-49, `crop-listing-api.md`), and no check catches a scam. Buyers, who see every listing, report what they find; admins decide.

## Rules

1. **Buyers only.** Buyers see every crop listing and every trip. A farmer sees only their own crops and a trucker only their own trips (CLAUDE.md role rules), so neither has another party's listing to report. The server refuses any other role; the clients show the button to buyers only.
2. **Never your own listing.** Refused on the server; never shown by the clients.
3. **The reporter stays hidden.** Only admins read reports. Nothing is written to the listing, the owner is not notified, and no client route returns a report. The server log line carries no ids.
4. **Reason codes, no free text.** A report holds pseudonymous ids and a reason code only, so it carries no personal data to erase or screen.
5. **One report per buyer and listing.** A later repeat answers `duplicate` and costs no quota (two reports of the same listing sent at the same moment may spend one quota each; the loser still answers `duplicate`).
6. **Daily limit.** At most 10 reports per buyer per UTC day (rate-limits table, key `LISTING_REPORT#{buyerId}#{yyyy-MM-dd}`); more are refused.
7. **Offline first.** On mobile a report made offline waits in the sync queue and is sent when the phone is online.

---

## 1. The `listing_report` sync entity (sync-protocol.md 1.44.0)

Create only. Sent in a normal `/sync` batch.

```json
{
  "operationId": "uuid-v4",
  "idempotencyKey": "{buyerId}:listing_report:crop#{cropId}:create:1762507815123",
  "entityType": "listing_report",
  "entityId": "crop#{cropId}",
  "action": "create",
  "data": {
    "listingType": "crop",
    "listingId": "{cropId}",
    "reason": "contact_info"
  },
  "timestamp": "2026-11-07T09:30:15Z"
}
```

| Field | Rule |
|-------|------|
| `entityId` | `{listingType}#{listingId}` |
| `listingType` | `crop` (a crop listing, `cropId`) or `trip` (`tripId`) |
| `listingId` | Required, at most 128 characters, no `#` |
| `reason` | `contact_info` (a phone number or other contact in the listing, its photos or its voice note), `misleading` (wrong price, quantity, crop, route or photos), `scam`, `offensive`, `other` |
| `version` | Accepted and ignored |

Any other field is refused (`Unknown fields: ...`), free text included.

### Answers

| Case | Status | Message |
|------|--------|---------|
| Stored | `success`, `serverVersion: 1` | none |
| The buyer already reported this listing (read first, and the conditional put) | `duplicate` | `This listing is already reported.` |
| The caller is not a buyer | `error` | `SYNC_VALIDATION: Only buyers can report a listing.` (a `ROLE_FORBIDDEN` fraud signal when the token carries a role, FRD-08) |
| Not `create` | `error` | `SYNC_VALIDATION: Unsupported action '...'. Listing reports are create only.` |
| Payload invalid | `error` | `SYNC_VALIDATION: ` and the problems found |
| No such crop listing or trip | `error` | `SYNC_VALIDATION: The reported listing does not exist.` A listing that exists in any status (a deleted crop included) can be reported. |
| The caller owns the listing | `error` | `SYNC_VALIDATION: You cannot report your own listing.` |
| Over the daily limit | `error` | `SYNC_VALIDATION: report limit reached. A buyer can send at most 10 reports a day.` Nothing stored. |
| The server has no reports table (`LISTING_REPORTS_TABLE` unset) | `error` | `Listing reports are not enabled on this server yet.` (unprefixed: clients retry) |

Every refusal is `SYNC_VALIDATION`, which every client already treats as permanent (`sync-protocol.md` Retry Logic): the mobile queue marks it rejected, reverts nothing (there is no local row) and tells the buyer: `errorReportLimitReached` for the limit, `errorReportNotSent` otherwise (en, am, om). Web shows the same texts in the sheet.

### Storage

Table `{prefix}-listing-reports`, PK `listingId`, SK `reporterId`.

| Attribute | Value |
|-----------|-------|
| `listingId`, `reporterId` | Keys |
| `listingType` | `crop` or `trip` |
| `ownerId` | The listing's `farmerId` or `truckerId` at report time |
| `reason` | The reason code |
| `status` | `open`, then `dismissed` or `actioned` |
| `createdAt` | ISO 8601 UTC with milliseconds |
| `reviewedAt`, `reviewedBy`, `reviewNote` | Set by the admin review |
| `expiresAt` | TTL, epoch seconds, `createdAt` + 180 days, reviewed or not |

GSI `StatusCreatedAtIndex` (PK `status`, SK `createdAt`, projection ALL): the admin queue.

The sync Lambda holds `GetItem` and `PutItem` on the table only; the admin Lambda `Query` (table and index) and `UpdateItem` only.

---

## 2. Admin queue

Both routes sit on the admin JWT authorizer; the Lambda checks the admin role.

### GET /admin/listing-reports

Query: `status` (`open` by default, `dismissed`, `actioned`), `limit` (1 to 100, default 50), `nextToken`.

One page of the status partition of `StatusCreatedAtIndex`: `open` oldest first (a queue), the decided statuses newest first. For each distinct listing on the page, what it holds now (one read of the crops or trips table by its id; a failed read shows the listing as not found).

```json
{
  "reports": [
    {
      "listingId": "crop-1",
      "listingType": "crop",
      "ownerId": "farmer-1",
      "reporterId": "buyer-1",
      "reason": "contact_info",
      "status": "open",
      "createdAt": "2026-11-07T09:30:15.123Z"
    }
  ],
  "listings": [
    {
      "listingId": "crop-1",
      "listingType": "crop",
      "found": true,
      "status": "available",
      "ownerName": "Abebe",
      "cropType": "teff",
      "description": "White teff",
      "audioNoteRef": "audio/farmer-1/crop-1.m4a"
    }
  ],
  "nextToken": "..."
}
```

A trip entry carries `fromWoreda`, `toWoreda` and `departureDate` instead of the crop fields. `reviewedAt`, `reviewedBy` and `reviewNote` appear on decided reports.

Errors: `400 ADMIN_005` (unknown `status`, bad `nextToken`), `503 ADMIN_004` (no reports table).

### POST /admin/listing-reports/{listingId}/review

Body: `{"decision": "dismissed" | "actioned", "note": "optional, at most 500 characters"}`.

Decides every `open` report of the listing (conditional on `status = open`, so a report another admin decided meanwhile is left as it is). `actioned` means the admin acted, for example suspended the owner on the user page; this route records the decision only and changes neither the listing nor the owner.

```json
{ "listingId": "crop-1", "decision": "actioned", "reviewed": 2, "reviewedAt": "2026-11-07T12:00:00.000Z" }
```

`reviewed` is 0 when nothing was open. When it is above 0, one transaction-audit row: `transactionId = listing-report-{listingId}`, `entityType = listing_report`, `action = listing_report_dismissed | listing_report_actioned`, from `open` to the decision, metadata `reports={n}` and the note. An `ADMIN LISTING_REPORT_REVIEW` audit log line in every case.

Errors: `400 ADMIN_009` (unknown decision), `400 ADMIN_005` (bad JSON, bad listing id, note too long), `503 ADMIN_004` (no reports table).

---

## Invariants

1. Only a buyer's report is stored; never a report of the caller's own listing.
2. At most one report per buyer and listing.
3. No client route returns a report or a reporter id; the owner is never notified.
4. A report holds no free text from the buyer.
5. A review changes only reports that are still `open`.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-11-07 | First version (FUP-67): the `listing_report` sync entity (buyers only, reason codes, one per buyer and listing, 10 a day, every refusal `SYNC_VALIDATION`), the `listing-reports` table (180-day TTL, `StatusCreatedAtIndex`), `GET /admin/listing-reports` and `POST /admin/listing-reports/{listingId}/review`, the mobile and web report sheet, the admin Listing Reports page |
