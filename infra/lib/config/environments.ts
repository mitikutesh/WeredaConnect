/**
 * Environment configuration for WeredaConnect infrastructure.
 *
 * Region: eu-north-1 (Stockholm) for dev; me-south-1 (Bahrain) for staging/prod.
 * Dev uses Stockholm for lower cost during development (working from Finland).
 */

export interface DomainConfig {
  readonly zoneName: string;
  readonly websiteSubdomain: string; // e.g. "www" → www.weredaconnect.com
  readonly webappSubdomain: string; // e.g. "app" → app.weredaconnect.com
  readonly adminSubdomain: string; // e.g. "admin" → admin.weredaconnect.com
}

export interface EnvironmentConfig {
  readonly name: string;
  readonly region: string;
  readonly account?: string;
  readonly isProd: boolean;
  readonly tags: Record<string, string>;
  readonly domain: DomainConfig;
  /** Extra CORS origins beyond the standard domain-derived ones (e.g. temp CloudFront URLs). */
  readonly extraCorsOrigins?: readonly string[];
  /**
   * When false, the Monitoring stack (CloudWatch dashboard, alarms, log metric filters, alarm SNS
   * topic; about $15 a month, counted in the MonitoringStack header) is not synthesized. Default
   * true. Dev is off to save cost; staging and prod must stay on (a test pins it). Override per
   * deploy with `-c monitoringEnabled=true|false`. Turning it off does not delete a stack that is
   * already deployed: run `npx cdk destroy WeredaConnect-<env>-Monitoring -c environment=<env>
   * -c monitoringEnabled=true` once.
   */
  readonly monitoringEnabled?: boolean;
  /**
   * When true, provision the real Telebirr gateway path instead of the mock:
   * a Telebirr credentials secret, a VPC + t4g.nano NAT instance (for a stable
   * outbound IP that Telebirr can source-IP allow-list), the public
   * POST /webhooks/telebirr route + Lambda, and switch the escrow/cron Lambdas
   * off MockTelebirrGateway. Default false — dev/staging stay on the mock at
   * zero extra cost. Can also be enabled per-deploy with `-c enableTelebirrLive=true`.
   */
  readonly enableTelebirrLive?: boolean;
  /**
   * Comma-separated Telebirr source IPs allowed to POST the webhook. Optional;
   * when empty the webhook's IP allow-list is skipped (RSA signature
   * verification is still enforced). Set once Telebirr provides their
   * notification IPs. Only used when enableTelebirrLive is true.
   */
  readonly telebirrAllowedIps?: string;
  /**
   * Allocation ID (eipalloc-…) of a pre-existing Elastic IP to bind to the NAT
   * instance instead of letting CDK create a fresh one. Use this to get a stable
   * IP you can register with Telebirr BEFORE the full stack is deployed:
   * `aws ec2 allocate-address --domain vpc` → give the IP to Telebirr → set this
   * to the returned AllocationId. If unset, CDK allocates a new EIP on deploy
   * (its address is emitted as the TelebirrNatEipOut output). Only used when
   * enableTelebirrLive is true. Can also be passed as `-c telebirrNatEipAllocationId=…`.
   */
  readonly telebirrNatEipAllocationId?: string;
  /**
   * Which Fayda (national ID) OIDC provider the server uses.
   * 'mock': deploy the FaydaMockConstruct (tools/fayda-mock) and point the
   * server's Fayda issuer at its Function URL. 'sandbox': the real VeriFayda
   * sandbox (esignet.ida.fayda.et) with its shared test client; the test client's
   * only redirect is http://localhost:3000/callback, which the developer's local
   * relay (tools/fayda-sandbox-relay) forwards to the app. 'live': the real
   * VeriFayda eSignet issuer with our own client. Unset keeps the current
   * behaviour. A deploy can override this with `-c faydaMode=mock|sandbox|live`.
   * 'mock' and 'sandbox' must never be used when isProd is true (the stack refuses
   * to synthesize and the mock construct throws).
   */
  readonly faydaMode?: 'mock' | 'live' | 'sandbox';
  /**
   * Live Fayda only: the client_id the Fayda partner portal issued. Not a secret,
   * so it lives here and survives deploys (the stack writes it to
   * /{prefix}/fayda/client-id). The matching private key is the seeded secret
   * /{prefix}/fayda/client-private-key. Runbook: specs/features/fayda-mock-server.md.
   */
  readonly faydaClientId?: string;
  /** Live Fayda only: issuer override. Defaults to https://esignet.ida.fayda.et. */
  readonly faydaIssuerUrl?: string;
  /**
   * Sandbox mode only: the client_id of the shared VeriFayda sandbox test client (the
   * integration guide's "Test Client 1"). Public, like every client_id. Its private
   * key (a base64 JWK, published with it) is pasted into /{prefix}/fayda/client-private-key
   * by the operator, never committed. Runbook: specs/features/fayda-mock-server.md.
   */
  readonly faydaSandboxClientId?: string;
  /** Sandbox mode only: the test client's registered redirect. Defaults to http://localhost:3000/callback. */
  readonly faydaSandboxRedirectUri?: string;
  /**
   * FRAUD_HOLD_ENABLED on the sync and escrow-cron Lambdas (fraud-detection-architecture.md 18.7).
   * On, a settlement sweep writes the escrow status `held` for a suspended, fraud-confirmed or
   * CRITICAL party instead of skipping the escrow. Default false. Turn it on for an environment only
   * once its web app and store builds parse `held`; older apps are answered `escrowed` (FUP-61).
   */
  readonly fraudHoldEnabled?: boolean;
  /**
   * COMMITMENTS_ENABLED on the sync, order-cron and telebirr-webhook Lambdas (ESC-15): sequential
   * farmer-then-trucker commitments and the awaiting_transport status; escrow and escrow-cron read it
   * to release the trip booking of an order they cancel (FUP-123), and the trips browse to report
   * the order lead to the apps (FUP-122). Default false. Turn it on for
   * an environment only once its web app and store builds render awaiting_transport and send the
   * farmer and trucker commands (app build 3, the ESC-15 client PR), and only with `minOrderAppBuild` at 3
   * or more (ESC-25, a test pins it). Dev is on; staging and prod stay off until the owner turns them on
   * after the ESC-25 gate is deployed there.
   */
  readonly commitmentsEnabled?: boolean;
  /**
   * MIN_ORDER_APP_BUILD on the sync Lambda (ESC-25, sync-protocol.md 1.41.0): `/sync` refuses every order
   * operation from an app whose `X-App-Build` is below it (`APP_UPDATE_REQUIRED`; no header:
   * `ORDER_VALIDATION`). Default 0, which turns the gate off. 3 is the first build that renders the
   * commitments; it does not depend on `commitmentsEnabled`, so it goes on before the flag does.
   */
  readonly minOrderAppBuild?: number;
  /**
   * REPUTATION_PENALTIES_ENABLED on the reputation-stream, order-cron and sync Lambdas (ESC-28,
   * reputation-architecture.md section 7.4). On, the reputation stream records events from the orders
   * stream and the daily job runs (reputation-api.md section 11); off, the stream acknowledges every record
   * without a write and the daily job logs and returns. Default false. Turn it on only where
   * `commitmentsEnabled` is on, after ESC-29 to ESC-33 ship (reputation-architecture.md 12.1 step 5).
   */
  readonly reputationPenalties?: boolean;
  /**
   * REPUTATION_TIMEOUT_PENALTIES_ENABLED on the reputation-stream and order-cron Lambdas (ESC-28): on,
   * RESPONSE_EXPIRED carries its point; off, it is recorded with 0 points (`timeout_penalties_off`).
   * Default false. Turned on last, after Android push delivery is confirmed on real devices
   * (reputation-architecture.md 12.1 step 7).
   */
  readonly timeoutPenalties?: boolean;
  /**
   * REPUTATION_PUBLIC_TAGS_ENABLED on the Lambdas that return a farmer's or trucker's reputation to
   * other users (ESC-29, reputation-api.md section 11): crops, trips, match, ratings, reputation and
   * sync. On, buyers get the public tier, the effective rating and the reliability; off, those fields
   * are left out of every answer. Default false. Turned on after `reputationPenalties` and the client
   * screens of ESC-31 (reputation-architecture.md 12.1).
   */
  readonly reputationPublicTags?: boolean;
  /**
   * PICKUP_CLASSIFICATION_ENABLED on the sync and order-cron Lambdas (ESC-34, delivery-verification-api.md
   * 2.6.0). On (and only with `commitmentsEnabled`), an order that becomes accepted is judged at its pickup
   * deadline by the arrival check-ins ("I have arrived", "Crops are ready"): a trucker no-show goes to
   * awaiting_transport, a farmer or self-pickup buyer no-show is cancelled with a full refund, both present
   * without a scan goes to the admin queue. Off, nothing changes. Default false everywhere. Turn it on only
   * after the app build that sends the check-ins is the minimum (`minOrderAppBuild`), and with
   * `reputationPenalties` for the NO_SHOW points (spec K3).
   */
  readonly pickupClassification?: boolean;
  /**
   * ESC-20: ESCROW_LEDGER_ENABLED on the sync and telebirr-webhook Lambdas (escrow-protocol.md 2.2.0, P.12).
   * On, a new order escrow is born on the escrow ledger (settlementModel ledger: per-leg write-ahead rows
   * instead of the whole-escrow settlement lock) and a duplicate payment gets its ledger refund row. Off, new
   * escrows take the 1.x path again; escrows already on the ledger keep settling there (the table stays wired
   * in every escrow Lambda). Default false; dev is on, staging and prod off until the ledger has run in dev.
   */
  readonly escrowLedgerEnabled?: boolean;
  /**
   * FRD-19 kill switch: FRAUD_AI_REVIEW_ENABLED on the fraud-advisory cron and AI_REVIEW_ENABLED on the
   * fraud-ai-reviewer Lambda (fraud-detection-architecture.md section 21). On, an hourly run sends the redacted
   * audit timeline of each user the rules have just put at HIGH or CRITICAL to a Claude model on Amazon Bedrock
   * and writes its advice as one capped AI_ADVISORY signal row. Default false in every environment: turning it on
   * is the owner's decision (specs/decisions/frd-19-ai-advisory-reviewer.md), after model access is granted in
   * the Bedrock console.
   */
  readonly aiReviewEnabled?: boolean;
  /**
   * FRD-19: the Bedrock model or inference profile id the reviewer invokes, e.g. `eu.anthropic.claude-opus-5-5`.
   * The reviewer's IAM grant is scoped to it. Default `eu.anthropic.claude-opus-5-5`.
   */
  readonly aiReviewModelId?: string;
  /**
   * FRD-19: the Region of the Bedrock endpoint the reviewer calls. Default: the stack's Region. me-south-1 has
   * only the Global profile for current Claude models, so staging and prod name eu-central-1 to keep the
   * timeline in EU Regions (the decision record's option 3; still off until the owner decides).
   */
  readonly aiReviewBedrockRegion?: string;
  /**
   * FRD-19 (FUP-145): the most reviews one hourly run may send to the model, FRAUD_AI_REVIEW_MAX_PER_RUN on the
   * fraud-advisory cron. A whole number from 1 to 20 (the server's cap); synth fails on anything else. Unset, the
   * variable is not set and the server keeps its default of 5. With one review per user and UTC day it bounds the
   * spend: at most 24 times this many reviews a day.
   */
  readonly aiReviewMaxPerRun?: number;
}

export const environments: Record<string, EnvironmentConfig> = {
  dev: {
    name: 'dev',
    region: 'eu-north-1',
    isProd: false,
    // No dashboard or alarms in dev, to save the CloudWatch cost (owner's decision, 2026-11-11).
    // `-c monitoringEnabled=true` brings them back for one deploy, e.g. for the synthetic alarm scripts.
    monitoringEnabled: false,
    // Dev signs in against the mock Fayda eSignet provider (tools/fayda-mock).
    // To use real Fayda: faydaMode 'live' + faydaClientId, then seed the client
    // private key (specs/features/fayda-mock-server.md, "Switching to live Fayda").
    faydaMode: 'mock',
    // `-c faydaMode=sandbox` points dev at the real VeriFayda sandbox with this shared
    // test client (specs/features/fayda-mock-server.md, "Sandbox mode").
    faydaSandboxClientId: 'aQ7xN2kLm9_Vp4RzT8cY-hW3sJ6FdE0uXbKqP1',
    // FUP-61: dev holds the escrows of flagged parties; staging and prod stay off.
    fraudHoldEnabled: true,
    // ESC-15: the farmer, then the trucker, must accept a paid order; staging and prod stay off
    // until the owner turns them on after the ESC-25 gate is deployed there.
    commitmentsEnabled: true,
    // ESC-25: apps before build 3 cannot act on orders.
    minOrderAppBuild: 3,
    // ESC-28: reputation penalties and timeout penalties stay off until the rollout (reputation-architecture.md 12.1).
    reputationPenalties: false,
    timeoutPenalties: false,
    reputationPublicTags: false,
    // ESC-34: the pickup judgement by arrival check-ins stays off until its app build is the minimum.
    pickupClassification: false,
    // ESC-20: new order escrows settle through the escrow ledger; staging and prod stay off.
    escrowLedgerEnabled: true,
    // FRD-19: the AI advisory reviewer is on in dev only (owner's decision, 2026-11-11), on Haiku 5.5; the EU
    // profile keeps the redacted timelines in EU Regions. Staging and prod stay off.
    aiReviewEnabled: true,
    aiReviewModelId: 'eu.anthropic.claude-haiku-5-5',
    tags: {
      Environment: 'development',
      Project: 'WeredaConnect',
      ManagedBy: 'CDK',
    },
    domain: {
      zoneName: 'weredaconnect.com',
      websiteSubdomain: 'dev',
      webappSubdomain: 'dev-app',
      adminSubdomain: 'dev-admin',
    },
    // Temporary CloudFront distribution for dev webapp before custom domain is wired.
    // Remove once dev-app.weredaconnect.com is fully configured.
    extraCorsOrigins: ['https://d32f0lzad1ens5.cloudfront.net'],
  },
  staging: {
    name: 'staging',
    region: 'me-south-1',
    isProd: false,
    // ESC-25: apps before build 3 cannot act on orders. The commitments (ESC-15) stay off until the
    // owner turns them on here, after this gate is deployed.
    minOrderAppBuild: 3,
    // ESC-28: reputation penalties and timeout penalties stay off until the rollout (reputation-architecture.md 12.1).
    reputationPenalties: false,
    timeoutPenalties: false,
    reputationPublicTags: false,
    // ESC-34: the pickup judgement by arrival check-ins stays off until its app build is the minimum.
    pickupClassification: false,
    // FRD-19: off. me-south-1 has no EU or ME geo profile for current Claude models; if turned on, the reviewer
    // calls the EU profile from eu-central-1 (decision record, option 3).
    aiReviewEnabled: false,
    aiReviewModelId: 'eu.anthropic.claude-opus-5-5',
    aiReviewBedrockRegion: 'eu-central-1',
    tags: {
      Environment: 'staging',
      Project: 'WeredaConnect',
      ManagedBy: 'CDK',
    },
    domain: {
      zoneName: 'weredaconnect.com',
      websiteSubdomain: 'staging',
      webappSubdomain: 'staging-app',
      adminSubdomain: 'staging-admin',
    },
  },
  prod: {
    name: 'prod',
    region: 'me-south-1',
    isProd: true,
    // ESC-25: apps before build 3 cannot act on orders. The commitments (ESC-15) stay off until the
    // owner turns them on here, after this gate is deployed.
    minOrderAppBuild: 3,
    // ESC-28: reputation penalties and timeout penalties stay off until the rollout (reputation-architecture.md 12.1).
    reputationPenalties: false,
    timeoutPenalties: false,
    reputationPublicTags: false,
    // ESC-34: the pickup judgement by arrival check-ins stays off until its app build is the minimum.
    pickupClassification: false,
    // FRD-19: off. me-south-1 has no EU or ME geo profile for current Claude models; if turned on, the reviewer
    // calls the EU profile from eu-central-1 (decision record, option 3).
    aiReviewEnabled: false,
    aiReviewModelId: 'eu.anthropic.claude-opus-5-5',
    aiReviewBedrockRegion: 'eu-central-1',
    tags: {
      Environment: 'production',
      Project: 'WeredaConnect',
      ManagedBy: 'CDK',
    },
    domain: {
      zoneName: 'weredaconnect.com',
      websiteSubdomain: 'www',
      webappSubdomain: 'app',
      adminSubdomain: 'admin',
    },
  },
};
