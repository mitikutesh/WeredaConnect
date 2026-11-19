  export const meta = {
  name: 'wc-full-audit-loop',
  description: 'Audit every feature of WeredaConnect vs docs, adversarially refute each finding, loop until a clean pass finds no new issues',
  phases: [
    { title: 'Audit', detail: 'one auditor per feature/module + dynamic build/test/E2E proofs' },
    { title: 'Refute', detail: 'one adversarial verifier per fresh finding' },
  ],
}

const CTX = `Repository root: '/Users/mitiku.geleta/Documents/Personal /WeredaConnect' (the 'Personal ' directory has a TRAILING SPACE — always double-quote paths in shell commands).
All file paths in your report must be repo-relative (e.g. mobile/lib/features/order/...).
Ground-truth documentation (read what is relevant to your scope BEFORE auditing):
- CLAUDE.md (root) — role visibility rules, platform pairs, core marketplace flow, AOT rules
- .ai/completeness-rules.md — mandatory completeness rules
- specs/contracts/*.md — API contracts (single source of truth for cross-module interfaces)
- server/AOT_COMPLIANCE.md — server AOT rules
Hard rules (violations are CRITICAL):
1. Role visibility: Farmer sees ONLY own crops; Trucker sees ONLY own trips; Buyer sees ALL crops + ALL trips from everyone and creates only purchase intents/orders (never crops/trips).
2. Platform pairs: mobile/web counterpart files (auth_storage, offline_auth_cache, isar_service, sync_executor, sync_queue, cert_pinning, schemas vs core/models/web) must stay in sync; no dart:io or Isar imports reachable from the web build.
3. Offline-first: never assume connectivity; writes go through the sync queue.
4. L10n: UI strings via AppLocalizations with keys present in all 3 .arb files (en, am, om).
5. Server is .NET Native AOT: no reflection-based libs (Newtonsoft.Json, EF Core, Moq, AutoMapper); System.Text.Json source generators only, types registered in server/src/AppJsonContext.cs.
6. Core marketplace flow must never break: farmer crop -> buyer browses ALL -> order -> escrow payment -> trucker trip -> delivery -> farmer payout after confirmation.
Known wire gotchas (real, documented): order status is snake_case on /sync while trip/delivery statuses are camelCase; mobile Web*Repository classes hand-parse camelCase envelopes like {"order":...}/{"orders":[...]}; API auth uses the Cognito ID token (custom:role claim), not the access token; optimistic concurrency must send the PRE-increment version to the server's expectedVersion check.`

const FINDINGS = {
  type: 'object',
  additionalProperties: false,
  required: ['findings'],
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        required: ['file', 'line', 'title', 'detail', 'severity'],
        properties: {
          file: { type: 'string', description: 'repo-relative path' },
          line: { type: 'integer', description: 'exact 1-indexed line the defect anchors to' },
          title: { type: 'string', description: 'short one-line defect statement' },
          detail: { type: 'string', description: 'concrete failure scenario: inputs/state -> wrong behavior, and which doc/contract it violates' },
          severity: { enum: ['critical', 'high', 'medium', 'low'] },
        },
      },
    },
  },
}

const VERDICT = {
  type: 'object',
  additionalProperties: false,
  required: ['refuted', 'reason', 'line'],
  properties: {
    refuted: { type: 'boolean' },
    reason: { type: 'string', description: '1-2 sentences' },
    line: { type: 'integer', description: 'exact confirmed 1-indexed line (when refuted=false); echo the reported line otherwise' },
  },
}

const MOBILE_COMMON = 'Also check: BLoC event/state wiring actually reaches the UI, repository/sync-queue usage for offline writes, l10n keys used exist in all 3 arb files under mobile/lib/shared/l10n/, role gating on screens, and web-platform behavior (Web*Repository parsing, no Isar on web). Tests live in mobile/test/.'

const SERVER_COMMON = 'Also check: DynamoDB marshalling correctness, authorization (role claim checks, ownership checks - IDOR), idempotency, optimistic concurrency (expectedVersion), JSON types registered in AppJsonContext.cs, error responses matching contracts. Tests live in server/tests/.'

const FEATURES = [
  { key: 'mob-auth', desc: 'Mobile auth feature', paths: ['mobile/lib/features/auth/'], extra: 'Contract: specs/contracts/auth-api.md. ' + MOBILE_COMMON },
  { key: 'mob-buyer', desc: 'Mobile buyer feature (browse ALL crops/trips, purchase intents, matching)', paths: ['mobile/lib/features/buyer/'], extra: 'Contracts: specs/contracts/match-api.md, crop-listing-api.md, trip-listing-api.md. Buyer MUST see ALL crops and ALL trips. ' + MOBILE_COMMON },
  { key: 'mob-conflict', desc: 'Mobile conflict_resolution feature', paths: ['mobile/lib/features/conflict_resolution/'], extra: 'Contract: specs/contracts/sync-protocol.md. ' + MOBILE_COMMON },
  { key: 'mob-crop', desc: 'Mobile crop_listing feature (farmer)', paths: ['mobile/lib/features/crop_listing/'], extra: 'Contract: specs/contracts/crop-listing-api.md. Farmer must see ONLY own crops. ' + MOBILE_COMMON },
  { key: 'mob-delivery', desc: 'Mobile delivery_verification feature (QR handoff, delivery confirmation)', paths: ['mobile/lib/features/delivery_verification/'], extra: 'Contract: specs/contracts/delivery-verification-api.md. ' + MOBILE_COMMON },
  { key: 'mob-escrow', desc: 'Mobile escrow_payment feature (Telebirr/CBE payment, escrow status)', paths: ['mobile/lib/features/escrow_payment/'], extra: 'Contract: specs/contracts/escrow-protocol.md. ' + MOBILE_COMMON },
  { key: 'mob-messaging', desc: 'Mobile messaging feature (context-scoped chat, contact, push)', paths: ['mobile/lib/features/messaging/'], extra: 'Contracts: specs/contracts/messaging.md, push-alerts-api.md. ' + MOBILE_COMMON },
  { key: 'mob-onboarding', desc: 'Mobile onboarding feature', paths: ['mobile/lib/features/onboarding/'], extra: MOBILE_COMMON },
  { key: 'mob-order', desc: 'Mobile order feature', paths: ['mobile/lib/features/order/'], extra: 'Order status is snake_case on the /sync wire (order_status_wire.dart) but camelCase in the OrderStatus enum. Contract: specs/contracts/sync-protocol.md. ' + MOBILE_COMMON },
  { key: 'mob-rating', desc: 'Mobile rating feature', paths: ['mobile/lib/features/rating/'], extra: 'Contract: specs/contracts/rating-api.md. Ratings are immutable, one per order+ratee. ' + MOBILE_COMMON },
  { key: 'mob-trip', desc: 'Mobile trip_listing feature (trucker)', paths: ['mobile/lib/features/trip_listing/'], extra: 'Contract: specs/contracts/trip-listing-api.md. Trucker must see ONLY own trips. ' + MOBILE_COMMON },
  { key: 'mob-core-sync', desc: 'Mobile core sync engine (queue, executor, pull, conflict handling)', paths: ['mobile/lib/core/sync/'], extra: 'Contract: specs/contracts/sync-protocol.md. sync_executor/sync_queue have _web counterparts that must match behavior. ' + MOBILE_COMMON },
  { key: 'mob-core-auth-db', desc: 'Mobile core auth storage + database + models (platform pairs)', paths: ['mobile/lib/core/auth/', 'mobile/lib/core/database/', 'mobile/lib/core/models/'], extra: 'Every platform-paired file must have a behaviorally equivalent web counterpart; Isar schemas vs core/models/web must carry the same fields. ' + MOBILE_COMMON },
  { key: 'mob-core-misc', desc: 'Mobile core network/notifications/routing/config/error/utils', paths: ['mobile/lib/core/network/', 'mobile/lib/core/notifications/', 'mobile/lib/core/routing/', 'mobile/lib/core/config/', 'mobile/lib/core/error/', 'mobile/lib/core/errors/', 'mobile/lib/core/utils/'], extra: 'cert_pinning_io vs cert_pinning_stub pair; routing role gating; FCM wiring. ' + MOBILE_COMMON },
  { key: 'mob-shared-app', desc: 'Mobile shared widgets, l10n, app shell, screens, entrypoints', paths: ['mobile/lib/shared/', 'mobile/lib/app/', 'mobile/lib/screens/', 'mobile/lib/main.dart', 'mobile/lib/main_web.dart'], extra: 'Verify the 3 arb files (en/am/om) have identical key sets and no untranslated placeholders; dashboards/navigation correct per role. ' + MOBILE_COMMON },
  { key: 'srv-auth', desc: 'Server auth functions', paths: ['server/src/Functions/LoginFunction.cs', 'server/src/Functions/LogoutFunction.cs', 'server/src/Functions/RefreshFunction.cs', 'server/src/Functions/RegisterFunction.cs', 'server/src/Functions/PasswordResetFunction.cs', 'server/src/Functions/FaydaVerifyFunction.cs', 'server/src/Services/CognitoAuthService.cs', 'server/src/Services/FaydaOidcService.cs'], extra: 'Contract: specs/contracts/auth-api.md, user-entity.md. ' + SERVER_COMMON },
  { key: 'srv-crop-match', desc: 'Server crop + matching functions', paths: ['server/src/Functions/CropFunction.cs', 'server/src/Functions/CropMatchStreamFunction.cs', 'server/src/Functions/MatchFunction.cs'], extra: 'Contracts: specs/contracts/crop-listing-api.md, match-api.md, match-events.md. Farmer-only visibility on own crops; buyers see all. ' + SERVER_COMMON },
  { key: 'srv-order', desc: 'Server order function + order cron', paths: ['server/src/Functions/OrderFunction.cs', 'server/src/Functions/OrderCronFunction.cs'], extra: 'Order status snake_case on wire. Contract: specs/contracts/sync-protocol.md. ' + SERVER_COMMON },
  { key: 'srv-trip', desc: 'Server trip function', paths: ['server/src/Functions/TripFunction.cs'], extra: 'Contract: specs/contracts/trip-listing-api.md. Trucker-only visibility on own trips; buyers see all. ' + SERVER_COMMON },
  { key: 'srv-delivery', desc: 'Server delivery function (QR handoff, secrets, self-pickup)', paths: ['server/src/Functions/DeliveryFunction.cs'], extra: 'Contract: specs/contracts/delivery-verification-api.md. QR codes single-use, fail-closed, HMAC via delivery-qr-secrets. ' + SERVER_COMMON },
  { key: 'srv-escrow', desc: 'Server escrow + Telebirr gateway + webhook + escrow cron', paths: ['server/src/Functions/EscrowFunction.cs', 'server/src/Functions/EscrowCronFunction.cs', 'server/src/Functions/TelebirrWebhookFunction.cs', 'server/src/Services/EscrowService.cs', 'server/src/Services/MockTelebirrGateway.cs', 'server/src/Services/TelebirrSandboxGateway.cs', 'server/src/Services/TelebirrSignatureService.cs', 'server/src/Services/TelebirrGatewayFactory.cs', 'server/src/Services/TelebirrConfig.cs', 'server/src/Services/FraudDetectionService.cs'], extra: 'Contract: specs/contracts/escrow-protocol.md. Buyer pays -> held -> farmer receives after confirmed delivery; webhook signature verification; audit trail. ' + SERVER_COMMON },
  { key: 'srv-messaging', desc: 'Server messaging + push functions', paths: ['server/src/Functions/MessageFunction.cs', 'server/src/Functions/MessageStreamFunction.cs', 'server/src/Functions/DeviceTokenFunction.cs'], extra: 'Contracts: specs/contracts/messaging.md, push-alerts-api.md. Messages create-only, participants only. ' + SERVER_COMMON },
  { key: 'srv-rating', desc: 'Server rating function', paths: ['server/src/Functions/RatingFunction.cs'], extra: 'Contract: specs/contracts/rating-api.md. Immutable, one per order+ratee. ' + SERVER_COMMON },
  { key: 'srv-sync', desc: 'Server sync function (role-scoped pull queries - CRITICAL)', paths: ['server/src/Functions/SyncFunction.cs'], extra: 'Contract: specs/contracts/sync-protocol.md. This is the main role-visibility enforcement point: farmer pull must never return other farmers\' crops, trucker pull never other truckers\' trips, buyer pull returns ALL crops+trips. ' + SERVER_COMMON },
  { key: 'srv-admin', desc: 'Server admin function (transactions list, timeline, audit feed, analytics)', paths: ['server/src/Functions/AdminFunction.cs'], extra: 'Admin-only authorization required on every route; timeline stitches order/delivery/escrow; append-only transaction-audit. ' + SERVER_COMMON },
  { key: 'srv-crosscut', desc: 'Server cross-cutting: data layer, models, helpers, validation, rate limiting, program wiring, AOT compliance', paths: ['server/src/Data/', 'server/src/Models/', 'server/src/Helpers/', 'server/src/Validation/', 'server/src/RateLimiting/', 'server/src/Program.cs', 'server/src/AppJsonContext.cs'], extra: 'Full AOT sweep per server/AOT_COMPLIANCE.md; every serialized type registered in AppJsonContext.cs; route registration completeness in Program.cs (every Function reachable). ' + SERVER_COMMON },
  { key: 'admin-app', desc: 'Admin web app (React/Vite): pages, components, hooks, lib, i18n', paths: ['admin/src/'], extra: 'Verify pages match server AdminFunction endpoints (paths, response envelopes, types), auth handling uses ID token, i18n completeness, and that dashboards/timelines render the documented data. Tests run with vitest.' },
  { key: 'website', desc: 'Marketing website (Astro): pages, components, i18n, layouts', paths: ['website/src/'], extra: 'Verify all 3 languages present and consistent, no broken internal links or references to nonexistent pages/anchors, claims consistent with the actual product docs (CLAUDE.md flow), and build-breaking issues.' },
  { key: 'infra', desc: 'AWS CDK infrastructure stacks', paths: ['infra/lib/', 'infra/bin/'], extra: 'Verify against .ai/tech_stack.md constraints: ARM64 Lambda 512MB/10s, on-demand DynamoDB, eu-north-1 dev / me-south-1 prod, NEVER NAT gateways or RDS; every DynamoDB table+GSI the server Data layer queries actually exists in the stacks with matching names/keys; env vars the server reads are set on the Lambdas; alarm coverage.' },
  { key: 'sweep-role-visibility', desc: 'CRITICAL cross-cutting sweep: role visibility everywhere', paths: ['server/src/', 'mobile/lib/', 'admin/src/'], extra: 'Trace EVERY data-read path (server queries incl. /sync and per-entity GETs, mobile browse screens and BLoC fetches, web repositories, admin pages): confirm farmers can never see other farmers\' crops, truckers never other truckers\' trips, and buyers see ALL crops and trips. Also confirm buyers cannot create crops/trips via any endpoint. Report each violating query/screen with file+line.' },
  { key: 'sweep-contracts', desc: 'Cross-module contract conformance sweep (server <-> mobile <-> admin)', paths: ['specs/contracts/', 'server/src/Models/', 'mobile/lib/core/models/', 'admin/src/'], extra: 'For each contract in specs/contracts/, diff the documented request/response shapes and enums against the server Models/Functions, mobile models + Web*Repository parsers, and admin types. Report every field-name, casing, enum-value, or envelope-shape mismatch with the exact line in the offending implementation file.' },
]

const DYNAMICS = [
  { key: 'dyn-server-tests', desc: 'Build and test the .NET server.', cmd: 'cd "/Users/mitiku.geleta/Documents/Personal /WeredaConnect/server" && dotnet build 2>&1 | tail -30 && dotnet test 2>&1 | tail -40', notes: 'Use a 600000ms Bash timeout. If build/tests fail, read the failing test/source files to anchor each failure to a file+line.' },
  { key: 'dyn-mobile-analyze-test', desc: 'Run flutter analyze and the full mobile test suite.', cmd: 'cd "/Users/mitiku.geleta/Documents/Personal /WeredaConnect/mobile" && flutter analyze 2>&1 | tail -40 && flutter test 2>&1 | tail -40', notes: 'Use a 600000ms Bash timeout. Report analyzer errors/warnings (not infos) and test failures, each anchored to file+line.' },
  { key: 'dyn-mobile-web-build', desc: 'Build the mobile app for web to catch platform-pair breakage.', cmd: 'cd "/Users/mitiku.geleta/Documents/Personal /WeredaConnect/mobile" && flutter build web --no-tree-shake-icons 2>&1 | tail -30', notes: 'Use a 600000ms Bash timeout. Any compile error is a finding anchored to the offending file+line.' },
  { key: 'dyn-admin-build', desc: 'Build and test the admin app.', cmd: 'cd "/Users/mitiku.geleta/Documents/Personal /WeredaConnect/admin" && npm run build 2>&1 | tail -30 && npx vitest run 2>&1 | tail -40', notes: 'Use a 600000ms Bash timeout. TypeScript/build errors and test failures are findings with file+line.' },
  { key: 'dyn-website-build', desc: 'Build the Astro website.', cmd: 'cd "/Users/mitiku.geleta/Documents/Personal /WeredaConnect/website" && npm run build 2>&1 | tail -30', notes: 'Build errors are findings with file+line.' },
  { key: 'dyn-infra-synth', desc: 'Synthesize the CDK app.', cmd: 'cd "/Users/mitiku.geleta/Documents/Personal /WeredaConnect/infra" && npx cdk synth --quiet 2>&1 | tail -30', notes: 'Synth errors are findings with file+line in infra/lib.' },
  { key: 'dyn-e2e-api', desc: 'Exercise the deployed dev API end-to-end per the documented marketplace flow.', cmd: 'curl against https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com — read .ai/test-users.md and .ai/deployment.md first; login all 3 test users (POST /auth/login, password TestPass1dev; use the ID token for authenticated calls)', notes: 'Verify per docs: all 3 roles can log in; buyer browse/pull returns ALL crops and trips; farmer pull returns only own crops; trucker pull returns only own trips; then probe the core flow as far as practical (you MAY create minimal test entities named with prefix E2E-AUDIT). IMPORTANT: the deployed dev env may lag the source on main — before reporting a failure as a finding, check whether current source already fixes it; only report defects that exist in source, anchored to the server source file+line. Deploy-lag-only failures are NOT findings.' },
]

function auditPrompt(f, knownLines) {
  return CTX + '\n\nYOUR AUDIT SCOPE: ' + f.key + ' — ' + f.desc + '\nFiles in scope: ' + f.paths.join(', ') + '\n' + (f.extra || '') +
    '\n\nAudit EVERY file in your scope against the documentation and hard rules. Hunt for: broken flows vs the contracts, role-visibility violations, platform-pair drift, missing/wrong l10n keys, optimistic-concurrency/version bugs, wire-format mismatches, swallowed errors, dead buttons/unreachable states, AOT violations, missing auth/ownership checks, and untested critical paths.' +
    (knownLines ? '\n\nAlready-known findings — do NOT re-report these (look for NEW issues only):\n' + knownLines : '') +
    '\n\nReport ONLY concrete, verifiable defects. For each: repo-relative file path, EXACT 1-indexed line (open the file and verify the line number before reporting), short title, detail with the concrete failure scenario and which doc it violates, severity. No style nits, no speculation, no TODO-comment inventory. If your scope is clean, return an empty findings array.'
}

function dynPrompt(f, knownLines) {
  return CTX + '\n\nYOUR TASK: dynamic verification — ' + f.key + '. ' + f.desc + '\nRun: ' + f.cmd + '\n' + f.notes +
    (knownLines ? '\n\nAlready-known findings — do NOT re-report these:\n' + knownLines : '') +
    '\n\nReport each REAL failure as a finding anchored to the exact file+line the error points at (open the file to verify). Passing runs with warnings that do not violate the hard rules are NOT findings. If everything passes, return an empty findings array.'
}

function refutePrompt(x) {
  return CTX + '\n\nYou are an adversarial verifier. An auditor reported this finding — your job is to try to REFUTE it against the actual code and docs.\n\nFinding (severity ' + x.severity + ', area ' + x.area + '):\nFile: ' + x.file + '\nLine: ' + x.line + '\nTitle: ' + x.title + '\nDetail: ' + x.detail +
    '\n\nOpen the file at that line and the surrounding code; check the counterpart platform file if it is a paired file, the relevant contract in specs/contracts/, callers, and tests. Decide:\n- refuted=true if the behavior is intended per docs, already handled elsewhere (guard, caller, server side), unreachable, stylistic, a deploy-lag artifact, or you cannot construct a concrete failure scenario. DEFAULT TO refuted=true WHEN UNCERTAIN.\n- refuted=false ONLY if you can articulate the concrete failure (inputs/state -> wrong behavior) and the documentation supports that it is wrong. Then set line to the exact 1-indexed line the defect anchors to (correct the auditor if needed).'
}

const seen = new Set()
const seenList = []
const confirmed = []
const keyOf = (x) => x.file + '::' + x.title.toLowerCase().replace(/[^a-z0-9 ]/g, '').slice(0, 60)

let round = 0
let clean = false
let sessionLimited = false
const MAX_ROUNDS = 4

while (!clean && !sessionLimited && round < MAX_ROUNDS) {
  round++
  const pool = round === 1 ? FEATURES.concat(DYNAMICS) : FEATURES
  const knownLines = seenList.length ? seenList.map((k) => '- ' + k.file + ':' + k.line + ' — ' + k.title).join('\n') : ''
  log('Round ' + round + ': fanning out ' + pool.length + ' auditors (' + seenList.length + ' known findings so far)')
  let freshCount = 0
  const failedAudits = []

  const roundResults = await pipeline(
    pool,
    (f) => agent(f.cmd && f.notes ? dynPrompt(f, knownLines) : auditPrompt(f, knownLines), { label: 'audit:' + f.key + ' r' + round, phase: 'Audit', schema: FINDINGS }),
    (res, f) => {
      if (!res || !res.findings) { failedAudits.push(f.key); return [] }
      const fresh = res.findings.filter((x) => x && x.file && !seen.has(keyOf(x)))
      fresh.forEach((x) => { seen.add(keyOf(x)); seenList.push(x); x.area = f.key })
      freshCount += fresh.length
      if (!fresh.length) return []
      log(f.key + ' r' + round + ': ' + fresh.length + ' fresh findings -> refuting')
      return parallel(fresh.map((x) => () =>
        agent(refutePrompt(x), { label: 'refute:' + x.file.split('/').pop() + ':' + x.line, phase: 'Refute', schema: VERDICT })
          .then((v) => ({ x, v }))
      ))
    }
  )

  let flat = roundResults.filter(Boolean).flat().filter(Boolean)
  // Refuter died (session limit / API error): retry once so findings are never silently dropped
  const unverified = flat.filter((p) => !p.v).map((p) => p.x)
  if (unverified.length) {
    log('Retrying ' + unverified.length + ' refutes that returned no verdict')
    const retried = await parallel(unverified.map((x) => () =>
      agent(refutePrompt(x), { label: 'refute:' + x.file.split('/').pop() + ':' + x.line, phase: 'Refute', schema: VERDICT })
        .then((v) => ({ x, v }))
    ))
    flat = flat.filter((p) => p.v).concat(retried.filter(Boolean))
  }
  const stillUnverified = flat.filter((p) => !p.v)

  const conf = flat
    .filter((p) => p.v && p.v.refuted === false)
    .map((p) => ({ area: p.x.area, file: p.x.file, line: p.v.line || p.x.line, title: p.x.title, detail: p.x.detail, severity: p.x.severity, verifierNote: p.v.reason }))
  confirmed.push(...conf)
  log('Round ' + round + ' done: ' + freshCount + ' fresh, ' + conf.length + ' confirmed this round, ' + confirmed.length + ' confirmed total, ' + failedAudits.length + ' auditor failures')

  // A pass only counts if EVERY auditor completed and EVERY finding got a verdict
  if (failedAudits.length || stillUnverified.length) {
    sessionLimited = true
    log('ROUND INCOMPLETE — ' + failedAudits.length + ' auditors failed (' + failedAudits.join(', ') + '), ' + stillUnverified.length + ' findings unverified. NOT a valid pass; resume this run once the session limit resets.')
    break
  }
  if (freshCount === 0) clean = true
}

if (!clean && !sessionLimited) log('WARNING: hit MAX_ROUNDS=' + MAX_ROUNDS + ' without a clean pass — more rounds needed after fixes')
const order = { critical: 0, high: 1, medium: 2, low: 3 }
confirmed.sort((a, b) => (order[a.severity] ?? 9) - (order[b.severity] ?? 9))
return { cleanPass: clean, sessionLimited, rounds: round, totalReported: seenList.length, totalConfirmed: confirmed.length, confirmed }
