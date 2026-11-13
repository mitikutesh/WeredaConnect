import * as cdk from "aws-cdk-lib";
import { Construct } from "constructs";
import * as apigateway from "aws-cdk-lib/aws-apigatewayv2";
import { CorsHttpMethod } from "aws-cdk-lib/aws-apigatewayv2";
import * as integrations from "aws-cdk-lib/aws-apigatewayv2-integrations";
import { HttpUserPoolAuthorizer } from "aws-cdk-lib/aws-apigatewayv2-authorizers";
import * as cognito from "aws-cdk-lib/aws-cognito";
import * as dynamodb from "aws-cdk-lib/aws-dynamodb";
import * as ec2 from "aws-cdk-lib/aws-ec2";
import * as events from "aws-cdk-lib/aws-events";
import * as targets from "aws-cdk-lib/aws-events-targets";
import * as iam from "aws-cdk-lib/aws-iam";
import * as s3 from "aws-cdk-lib/aws-s3";
import * as s3n from "aws-cdk-lib/aws-s3-notifications";
import * as secretsmanager from "aws-cdk-lib/aws-secretsmanager";
import * as sqs from "aws-cdk-lib/aws-sqs";
import * as ssm from "aws-cdk-lib/aws-ssm";
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as lambdaEventSources from "aws-cdk-lib/aws-lambda-event-sources";
import * as logs from "aws-cdk-lib/aws-logs";
import * as path from "path";
import { WeredaLambda } from "../constructs/lambda-construct";
import { FaydaMockConstruct } from "../constructs/fayda-mock-construct";
import { EnvironmentConfig } from "../config/environments";
import { webCorsOrigins } from "../config/cors-origins";

/**
 * Public route of the Telebirr payment notification (live gateway only). Exported for the
 * monitoring stack, which reads this route's own metrics (FUP-39).
 */
export const TELEBIRR_WEBHOOK_PATH = "/webhooks/telebirr";

export interface ApiStackProps extends cdk.StackProps {
  readonly envConfig: EnvironmentConfig;
  readonly usersTable: dynamodb.Table;
  readonly cropsTable: dynamodb.Table;
  readonly tripsTable: dynamodb.Table;
  readonly rateLimitsTable: dynamodb.Table;
  readonly syncMetadataTable: dynamodb.Table;
  readonly deliveriesTable: dynamodb.Table;
  readonly paymentsTable: dynamodb.Table;
  readonly escrowsTable: dynamodb.Table;
  readonly escrowAuditTable: dynamodb.Table;
  /** ESC-20: the escrow ledger (escrow-protocol.md 2.2.0, P.3) with its LedgerOpenIndex. */
  readonly escrowLedgerTable: dynamodb.Table;
  readonly transactionAuditTable: dynamodb.Table;
  readonly escrowIdempotencyTable: dynamodb.Table;
  readonly deliveryQrSecretsTable: dynamodb.Table;
  readonly ordersTable: dynamodb.Table;
  readonly fraudSignalsTable: dynamodb.Table;
  readonly intentsTable: dynamodb.Table;
  readonly deviceTokensTable: dynamodb.Table;
  readonly matchEventsTable: dynamodb.Table;
  readonly ratingsTable: dynamodb.Table;
  readonly messagesTable: dynamodb.Table;
  readonly notificationsTable: dynamodb.Table;
  readonly tripLocationsTable: dynamodb.Table;
  readonly listingReportsTable: dynamodb.Table;
  readonly reputationEventsTable: dynamodb.Table;
  readonly uploadsBucket: s3.Bucket;
}

/**
 * The reputation policy defaults (reputation-architecture.md section 5.9), written to
 * /{prefix}/reputation/policy. A string, not JSON.stringify of an object, so `1.0` stays as written.
 * The server keeps the same defaults and falls back to them, key by key, when the parameter is
 * missing or a value is out of range (REPUTATION_POLICY_INVALID).
 */
const REPUTATION_POLICY_JSON =
  '{"declinePoints":1,"expiryPoints":1,"cancelPoints":3,"lateCancelPoints":4,"tripCancelPoints":3,' +
  '"tripLateCancelPoints":4,"noShowPoints":5,"overduePoints":5,"lateCancelHours":24,"pointsLifeDays":90,' +
  '"pairCapDays":30,"dailyCapPoints":6,"expiryMinWindowHours":12,"requestPointsMax":9,"warningAt":3,' +
  '"lowAt":6,"restrictAt":10,"restrictionDays":14,"repeatWindowDays":365,"ratingDeductionPerPoint":0.1,' +
  '"ratingDeductionMax":1.0,"reliabilityMinOrders":3,"earnBackMinOrderEtb":500,"earnBackPer30Days":2,' +
  '"earnBackBuyerRepeatDays":90,"appealWindowDays":7,"tagMaxDays":90,"adminRestrictMaxDays":365,' +
  '"buyerDeclineSignalCount":3,"buyerDeclineSignalDays":30,"eventsReadCap":2000}';

/**
 * Reference to a secret value in its per-environment backend: a Secrets
 * Manager ARN in prod, an SSM parameter name in dev/staging.
 */
interface SecretRef {
  readonly reference: string;
  grantRead(grantee: iam.IGrantable): iam.Grant;
}

/**
 * Per-environment secret backend (cost: SM is ~$0.40/secret/mo, SSM standard
 * tier is free).
 *   prod        → creates a Secrets Manager secret (construct IDs and props
 *                 unchanged from the pre-migration layout — the prod template
 *                 is untouched by this switch).
 *   dev/staging → REFERENCES an SSM SecureString parameter with the same
 *                 path. CloudFormation cannot CREATE SecureStrings, so the
 *                 parameter is seeded out-of-band by
 *                 scripts/seed-ssm-secrets.sh (CI runs it before every
 *                 non-prod deploy). Parameters must use the default aws/ssm
 *                 KMS key: grantRead() emits no kms:Decrypt statement.
 * Either way `reference` lands in the same *_ARN env var; the server routes
 * on the value's shape (AwsParameterResolver.GetSecretOrParameterValue).
 */
function envScopedSecret(
  scope: Construct,
  id: string,
  props: {
    isProd: boolean;
    /** Full path, e.g. `/wereda-connect-dev/pagination-secret`. */
    name: string;
    /** Used only in prod; non-prod descriptions come from the seed script. */
    description: string;
    /** Used only in prod; non-prod values come from the seed script. */
    generateSecretString?: secretsmanager.SecretStringGenerator;
  },
): SecretRef {
  if (props.isProd) {
    const secret = new secretsmanager.Secret(scope, id, {
      secretName: props.name,
      description: props.description,
      generateSecretString: props.generateSecretString,
    });
    return {
      reference: secret.secretArn,
      grantRead: (grantee) => secret.grantRead(grantee),
    };
  }

  const parameter = ssm.StringParameter.fromSecureStringParameterAttributes(
    scope,
    id,
    { parameterName: props.name },
  );
  return {
    reference: parameter.parameterName,
    grantRead: (grantee) => parameter.grantRead(grantee),
  };
}

/**
 * API stack: Cognito User Pool, HTTP API Gateway, Lambda functions, and auth routes.
 * Everything in one stack to avoid cross-stack cyclic references from API routes.
 */
export class ApiStack extends cdk.Stack {
  public readonly httpApiUrl: string;
  public readonly httpApiId: string;
  public readonly userPool: cognito.UserPool;
  public readonly userPoolClient: cognito.UserPoolClient;
  public readonly adminUserPool: cognito.UserPool;

  constructor(scope: Construct, id: string, props: ApiStackProps) {
    super(scope, id, props);

    const { envConfig } = props;
    const prefix = `wereda-connect-${envConfig.name}`;

    // CORS origins: prod uses exact subdomains, dev allows localhost + wildcard.
    // Shared by the HTTP API and the crop photo buckets (the web app PUTs
    // originals and fetches renditions straight from S3), and with the
    // database stack's uploads bucket (delivery photos, FRD-12).
    const corsOrigins = webCorsOrigins(envConfig);


    // ─── T-SEC-01/02: Hard guard — block dev-only bypasses in production ───
    if (envConfig.isProd) {
      cdk.Aspects.of(this).add(new ProductionBypassGuard());
    }

    // ─── Cognito User Pool ───────────────────────────────────────────
    this.userPool = new cognito.UserPool(this, "UserPool", {
      userPoolName: `${prefix}-users`,
      selfSignUpEnabled: true,
      signInAliases: { phone: true },
      autoVerify: { phone: false },
      mfa: envConfig.isProd ? cognito.Mfa.OPTIONAL : cognito.Mfa.OFF,
      ...(envConfig.isProd ? {
        mfaSecondFactor: {
          sms: true,
          otp: true,
        },
      } : {}),
      accountRecovery: cognito.AccountRecovery.PHONE_ONLY_WITHOUT_MFA,
      standardAttributes: {
        phoneNumber: { required: true, mutable: false },
      },
      customAttributes: {
        role: new cognito.StringAttribute({ mutable: true }),
        woreda: new cognito.StringAttribute({ mutable: true }),
        displayName: new cognito.StringAttribute({ mutable: true }),
        preferredLanguage: new cognito.StringAttribute({ mutable: true }),
      },
      passwordPolicy: envConfig.isProd ? {
        minLength: 12,
        requireLowercase: true,
        requireUppercase: true,
        requireDigits: true,
        requireSymbols: true,
        tempPasswordValidity: cdk.Duration.days(7),
      } : {
        minLength: 8,
        requireLowercase: true,
        requireDigits: true,
        tempPasswordValidity: cdk.Duration.days(7),
      },
      removalPolicy: envConfig.isProd
        ? cdk.RemovalPolicy.RETAIN
        : cdk.RemovalPolicy.DESTROY,
      deletionProtection: true,
    });

    this.userPoolClient = this.userPool.addClient("MobileClient", {
      userPoolClientName: `${prefix}-mobile`,
      // The public client cannot do a password login on its own: every password
      // check goes through the server (ADMIN_USER_PASSWORD_AUTH with IAM), which
      // applies the lockout, suspension and audit rules. Refresh stays client-side.
      authFlows: {
        userSrp: false,
        userPassword: false,
        adminUserPassword: true,
      },
      oAuth: {
        flows: {
          authorizationCodeGrant: true,
          implicitCodeGrant: false,
        },
        callbackUrls: ["myapp://auth/callback"],
        logoutUrls: ["myapp://auth/logout"],
      },
      accessTokenValidity: cdk.Duration.minutes(15),
      refreshTokenValidity: cdk.Duration.days(7),
      idTokenValidity: cdk.Duration.minutes(15),
      preventUserExistenceErrors: true,
      // SEC-VULN-001: Exclude custom:role from client-writable attributes
      // to prevent privilege escalation via Cognito UpdateUserAttributes
      writeAttributes: new cognito.ClientAttributes()
        .withStandardAttributes({ phoneNumber: true })
        .withCustomAttributes("woreda", "displayName", "preferredLanguage"),
    });

    // ─── Admin Cognito User Pool (separate, no self-registration) ─────
    this.adminUserPool = new cognito.UserPool(this, "AdminUserPool", {
      userPoolName: `${prefix}-admins`,
      selfSignUpEnabled: false,
      signInAliases: { phone: true },
      autoVerify: { phone: false },
      // Required in prod, where the admin web app drives the TOTP challenge and
      // setup. SMS stays enabled as a pool-level factor everywhere: the pool
      // carries an SMS configuration (phone sign-in alias), and Cognito can never
      // drop SMS_MFA from such a pool. With MFA REQUIRED, any admin without a
      // TOTP is therefore SMS-challenged (Cognito sends to unverified phones
      // too; MFA_SETUP only appears for users with no phone at all). Dev and
      // staging send no deliverable SMS (SNS sandbox, test numbers), so a
      // REQUIRED pool there locks every admin out, seed and e2e included.
      // Non-prod is OPTIONAL: password login issues tokens, and admins may
      // still enrol TOTP, which is then asked for.
      mfa: envConfig.isProd ? cognito.Mfa.REQUIRED : cognito.Mfa.OPTIONAL,
      mfaSecondFactor: {
        sms: true,
        otp: true,
      },
      // Admin-only recovery: Cognito refuses MFA ON with SMS enabled when phone is
      // the only recovery path (the phone would be both factor and reset channel).
      // The admin app has no forgot-password flow; an admin resets passwords.
      accountRecovery: cognito.AccountRecovery.NONE,
      standardAttributes: {
        phoneNumber: { required: true, mutable: false },
      },
      customAttributes: {
        role: new cognito.StringAttribute({ mutable: true }),
        displayName: new cognito.StringAttribute({ mutable: true }),
      },
      passwordPolicy: envConfig.isProd ? {
        minLength: 12,
        requireLowercase: true,
        requireUppercase: true,
        requireDigits: true,
        requireSymbols: true,
        tempPasswordValidity: cdk.Duration.days(7),
      } : {
        minLength: 8,
        requireLowercase: true,
        requireDigits: true,
        tempPasswordValidity: cdk.Duration.days(7),
      },
      removalPolicy: envConfig.isProd
        ? cdk.RemovalPolicy.RETAIN
        : cdk.RemovalPolicy.DESTROY,
      deletionProtection: true,
    });

    // ─── Admin App Client (on admin pool, longer token validity) ──────
    const adminClient = this.adminUserPool.addClient("AdminClient", {
      userPoolClientName: `${prefix}-admin`,
      authFlows: {
        userSrp: true,
        userPassword: true,
      },
      oAuth: {
        flows: {
          authorizationCodeGrant: true,
          implicitCodeGrant: false,
        },
        callbackUrls: [
          `https://${envConfig.domain.adminSubdomain}.${envConfig.domain.zoneName}/auth/callback`,
        ],
        logoutUrls: [
          `https://${envConfig.domain.adminSubdomain}.${envConfig.domain.zoneName}`,
        ],
      },
      accessTokenValidity: cdk.Duration.minutes(15),
      refreshTokenValidity: cdk.Duration.days(7),
      idTokenValidity: cdk.Duration.minutes(15),
      preventUserExistenceErrors: true,
      writeAttributes: new cognito.ClientAttributes()
        .withStandardAttributes({ phoneNumber: true })
        .withCustomAttributes("displayName"),
    });

    // ─── Admin Cognito Group ──────────────────────────────────────────
    new cognito.CfnUserPoolGroup(this, "AdminGroup", {
      userPoolId: this.adminUserPool.userPoolId,
      groupName: "admin",
      description: "Platform administrators with access to /admin/* endpoints",
    });

    // ─── Shared environment variables ────────────────────────────────
    const cognitoEnv: Record<string, string> = {
      COGNITO_USER_POOL_ID: this.userPool.userPoolId,
      COGNITO_CLIENT_ID: this.userPoolClient.userPoolClientId,
      COGNITO_REGION: this.region,
      USERS_TABLE: props.usersTable.tableName,
      RATE_LIMITS_TABLE: props.rateLimitsTable.tableName,
    };

    // Dev has no SMS delivery (SNS sandbox): the OTP dev bypass (123456) stands in.
    // ProductionBypassGuard refuses DEV_OTP_BYPASS in prod.
    const devAuthEnv: Record<string, string> =
      envConfig.name === "dev" ? { DEV_OTP_BYPASS: "true", SMS_ENABLED: "false" } : {};

    const cognitoOnlyEnv: Record<string, string> = {
      COGNITO_USER_POOL_ID: this.userPool.userPoolId,
      COGNITO_CLIENT_ID: this.userPoolClient.userPoolClientId,
      COGNITO_REGION: this.region,
    };

    // ─── Lambda Functions ────────────────────────────────────────────

    const healthCheck = new WeredaLambda(this, "HealthCheck", {
      functionName: `${prefix}-health`,
      description: "Health check endpoint",
      handlerName: "HealthCheckFunction",
      reservedConcurrentExecutions: 5,
    });
    props.usersTable.grantReadData(healthCheck.function);

    const registerFn = new WeredaLambda(this, "RegisterFunction", {
      functionName: `${prefix}-auth-register`,
      description:
        "POST /auth/register — user registration with Fayda eKYC verification",
      handlerName: "RegisterFunction",
      environment: { ...cognitoEnv, ...devAuthEnv },
      reservedConcurrentExecutions: 20,
    });
    // Read: the unconfirmed-phone replacement path looks the pending profile up.
    props.usersTable.grantReadWriteData(registerFn.function);
    props.rateLimitsTable.grantReadWriteData(registerFn.function);
    this.grantCognito(registerFn.function, [
      "cognito-idp:SignUp",
      "cognito-idp:AdminGetUser",
      "cognito-idp:AdminDeleteUser",
      "cognito-idp:AdminUpdateUserAttributes",
      "cognito-idp:AdminConfirmSignUp",
      "cognito-idp:InitiateAuth",
      "cognito-idp:AdminInitiateAuth",
    ]);
    this.grantSmsPublish(registerFn.function);

    // POST /auth/verify-otp + /auth/resend-otp: SMS proof of phone ownership for
    // registrations that Fayda did not phone-bind.
    const verifyOtpFn = new WeredaLambda(this, "VerifyOtpFunction", {
      functionName: `${prefix}-auth-verify-otp`,
      description:
        "POST /auth/verify-otp, /auth/resend-otp: confirm phone ownership by SMS code",
      handlerName: "VerifyOtpFunction",
      environment: { ...cognitoEnv, ...devAuthEnv },
      reservedConcurrentExecutions: 20,
    });
    props.usersTable.grantReadWriteData(verifyOtpFn.function);
    props.rateLimitsTable.grantReadWriteData(verifyOtpFn.function);
    this.grantCognito(verifyOtpFn.function, [
      "cognito-idp:AdminConfirmSignUp",
      "cognito-idp:AdminUpdateUserAttributes",
      "cognito-idp:InitiateAuth",
      "cognito-idp:AdminInitiateAuth",
    ]);
    this.grantSmsPublish(verifyOtpFn.function);

    const loginFn = new WeredaLambda(this, "LoginFunction", {
      functionName: `${prefix}-auth-login`,
      description: "POST /auth/login — authenticate with phone + password",
      handlerName: "LoginFunction",
      environment: cognitoEnv,
      reservedConcurrentExecutions: 20,
    });
    props.usersTable.grantReadData(loginFn.function);
    props.rateLimitsTable.grantReadWriteData(loginFn.function);
    this.grantCognito(loginFn.function, [
      "cognito-idp:InitiateAuth",
      "cognito-idp:AdminInitiateAuth",
      "cognito-idp:RespondToAuthChallenge",
    ]);

    const refreshFn = new WeredaLambda(this, "RefreshFunction", {
      functionName: `${prefix}-auth-refresh`,
      description: "POST /auth/refresh — refresh access token",
      handlerName: "RefreshFunction",
      // FRD-08: a suspended profile's refusal is a fraud signal (weight 0).
      environment: { ...cognitoEnv, FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName },
      reservedConcurrentExecutions: 20,
    });
    this.grantCognito(refreshFn.function, [
      "cognito-idp:InitiateAuth",
      "cognito-idp:RevokeToken",
      "cognito-idp:AdminInitiateAuth",
    ]);
    // Refresh checks the profile's suspended flag before minting tokens and
    // applies a per-IP cap.
    props.usersTable.grantReadData(refreshFn.function);
    props.rateLimitsTable.grantReadWriteData(refreshFn.function);
    // FRD-08: append only, one conditional PutItem per refused refresh.
    props.fraudSignalsTable.grant(refreshFn.function, "dynamodb:PutItem");

    const logoutFn = new WeredaLambda(this, "LogoutFunction", {
      functionName: `${prefix}-auth-logout`,
      description:
        "POST /auth/logout: revoke the caller's own refresh token (protected)",
      handlerName: "LogoutFunction",
      environment: cognitoOnlyEnv,
      reservedConcurrentExecutions: 20,
    });
    // SIM-60: logout resolves the refresh token's owner with InitiateAuth
    // (REFRESH_TOKEN_AUTH) before revoking it, like refreshFn above.
    this.grantCognito(logoutFn.function, [
      "cognito-idp:InitiateAuth",
      "cognito-idp:RevokeToken",
      "cognito-idp:GlobalSignOut",
      "cognito-idp:AdminUserGlobalSignOut",
    ]);

    const passwordResetFn = new WeredaLambda(this, "PasswordResetFunction", {
      functionName: `${prefix}-auth-password-reset`,
      description:
        "POST /auth/forgot-password, /auth/reset-password — password reset flow",
      handlerName: "PasswordResetFunction",
      environment: cognitoEnv,
      reservedConcurrentExecutions: 10,
    });
    props.rateLimitsTable.grantReadWriteData(passwordResetFn.function);
    this.grantCognito(passwordResetFn.function, [
      "cognito-idp:ForgotPassword",
      "cognito-idp:ConfirmForgotPassword",
    ]);

    // ─── Sync Function ───────────────────────────────────────────────

    // Mock payments: dev/staging run without a real Telebirr gateway, so the
    // sync engine auto-verifies a submitted payment (payment_submitted ->
    // funded) — matching the TELEBIRR_MOCK gate on the escrow/telebirr Lambdas.
    // Prod or an enabled live gateway leaves it unset so the real gateway funds.
    const syncTelebirrMock =
      !envConfig.isProd &&
      !((envConfig.enableTelebirrLive ?? false) ||
        this.node.tryGetContext("enableTelebirrLive") === "true");

    // FRD-03 / FUP-61: one value for sync and escrow-cron, from the environment config (dev on).
    const fraudHoldEnabled = envConfig.fraudHoldEnabled ? "true" : "false";

    // ESC-15: one value for sync, order-cron and the Telebirr webhook (off everywhere until the
    // clients that render awaiting_transport are deployed). FUP-123: escrow and escrow-cron read it
    // too, so the order cancels they make release trip bookings only while bookings exist.
    const commitmentsEnabled = envConfig.commitmentsEnabled ? "true" : "false";

    // ESC-25: the minimum app build for order operations; only /sync takes order commands.
    const minOrderAppBuild = String(envConfig.minOrderAppBuild ?? 0);
    // ESC-20: one value for sync and the Telebirr webhook, the two Lambdas that create order escrows
    // (dev on). The ledger table itself is wired into every escrow Lambda regardless, so an escrow
    // born on the ledger keeps settling there after the flag goes off.
    const escrowLedgerEnabled = envConfig.escrowLedgerEnabled ? "true" : "false";

    // ESC-20: the ledger's own grants. Rows are put (conditional, in a transaction with the escrow and
    // its audit row), updated (status, stamp) and read (GetItem, Query on the table and LedgerOpenIndex);
    // never deleted, batch-written or scanned. TransactWriteItems is authorised per item action.
    const grantEscrowLedger = (fn: lambda.IFunction, actions: string[]) =>
      fn.addToRolePolicy(
        new iam.PolicyStatement({
          actions,
          resources: [props.escrowLedgerTable.tableArn, `${props.escrowLedgerTable.tableArn}/index/*`],
        }),
      );

    // ESC-28: the reputation flags (environments.ts; off everywhere until the rollout of
    // reputation-architecture.md 12.1). Penalties on reputation-stream, order-cron and sync; timeout
    // penalties on the two that derive RESPONSE_EXPIRED (reputation-stream, and order-cron's unpark).
    const reputationPenalties = envConfig.reputationPenalties ? "true" : "false";
    const timeoutPenalties = envConfig.timeoutPenalties ? "true" : "false";
    // ESC-29: buyers get the public tier, the effective rating and the reliability (off everywhere).
    const reputationPublicTags = envConfig.reputationPublicTags ? "true" : "false";
    // ESC-34: the pickup judgement by arrival check-ins (sync takes the check-ins, order-cron judges); off
    // everywhere until its app build is the minimum. It only works with COMMITMENTS_ENABLED.
    const pickupClassification = envConfig.pickupClassification ? "true" : "false";

    // ESC-28: the reputation policy (points, caps, tiers, windows; reputation-architecture.md 5.9),
    // read once per container by every Lambda that recomputes a reputation: reputation-stream,
    // order-cron and sync. Edited in place to tune the policy; the manual order-cron action
    // reputation_recompute_all then applies it to every stored aggregate.
    const reputationPolicyParam = new ssm.StringParameter(this, "ReputationPolicy", {
      parameterName: `/${prefix}/reputation/policy`,
      stringValue: REPUTATION_POLICY_JSON,
      description: "Reputation policy settings, JSON (ESC-28, reputation-architecture.md 5.9)",
    });

    const syncFn = new WeredaLambda(this, "SyncFunction", {
      functionName: `${prefix}-sync`,
      description: "POST /sync — offline-first sync engine batch processor",
      handlerName: "SyncFunction",
      environment: {
        SYNC_METADATA_TABLE: props.syncMetadataTable.tableName,
        CROPS_TABLE: props.cropsTable.tableName,
        USERS_TABLE: props.usersTable.tableName,
        TRIPS_TABLE: props.tripsTable.tableName,
        DELIVERIES_TABLE: props.deliveriesTable.tableName,
        ORDERS_TABLE: props.ordersTable.tableName,
        RATE_LIMITS_TABLE: props.rateLimitsTable.tableName,
        FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName,
        INTENTS_TABLE: props.intentsTable.tableName,
        MATCH_EVENTS_TABLE: props.matchEventsTable.tableName,
        RATINGS_TABLE: props.ratingsTable.tableName,
        MESSAGES_TABLE: props.messagesTable.tableName,
        TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
        DELIVERY_QR_SECRETS_TABLE: props.deliveryQrSecretsTable.tableName,
        // LT-05: location_ping check-ins land here, never on the trip row.
        TRIP_LOCATIONS_TABLE: props.tripLocationsTable.tableName,
        // FUP-67: a buyer's listing_report lands here (admin queue).
        LISTING_REPORTS_TABLE: props.listingReportsTable.tableName,
        // FRD-12: a delivery_photo attach checks the uploaded photo (HEAD) in this bucket.
        UPLOADS_BUCKET: props.uploadsBucket.bucketName,
        // Confirming a delivery stamps the linked escrow so the auto-release cron
        // can pay the farmer. Without this env var (+ grant below) _escrowRepo is
        // null and the delivery-confirmation -> escrow payout bridge is disabled.
        ESCROWS_TABLE: props.escrowsTable.tableName,
        // Order-funded escrow leg: funding materializes an escrow record, delivery
        // creation links it, reject/cancel schedules its refund — every mutation
        // lands in the immutable escrow-audit ledger. Without this env the leg is
        // disabled (orders still fund; escrow records aren't created here).
        ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
        // ESC-20: a mock-funded order's escrow is born on the escrow ledger while the flag is on. The sync
        // Lambda only marks the escrow row; it reads and writes no ledger row, so it has no ledger grant.
        ESCROW_LEDGER_TABLE: props.escrowLedgerTable.tableName,
        ESCROW_LEDGER_ENABLED: escrowLedgerEnabled,
        // FRD-03: on, the farmer's accept is one transaction with a check that the order's escrow
        // is not held (a sweep may hold it between the read and the write). Same value as on
        // escrow-cron; both are turned on together after FRD-06.
        FRAUD_HOLD_ENABLED: fraudHoldEnabled,
        // ESC-15: on, the farmer's accept asks the trucker next and a failed transport parks the
        // order in awaiting_transport; off, the accept lands the order as before.
        COMMITMENTS_ENABLED: commitmentsEnabled,
        // ESC-25: order operations from an app below this build are refused (0: off).
        MIN_ORDER_APP_BUILD: minOrderAppBuild,
        // ESC-34: on, an order accepted under it takes handoff_check_in and is judged at its pickup deadline.
        PICKUP_CLASSIFICATION_ENABLED: pickupClassification,
        // ESC-28 (rating-api 1.2.0 stored aggregate): a rating create is one transaction with the
        // ratee's ratingSum and ratingCount, then a best-effort reputation refresh that reads the
        // ratee's events and writes the reputation map, the heal marker and the copies.
        REPUTATION_EVENTS_TABLE: props.reputationEventsTable.tableName,
        REPUTATION_POLICY_PARAM: reputationPolicyParam.parameterName,
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        // ESC-29: a crop or trip row in a sync answer carries the owner fields only when on.
        REPUTATION_PUBLIC_TAGS_ENABLED: reputationPublicTags,
        ...(syncTelebirrMock ? { TELEBIRR_MOCK: "true" } : {}),
      },
      timeout: cdk.Duration.seconds(30),
      reservedConcurrentExecutions: 10,
    });
    // Sync needs read-write on entity tables and sync metadata
    props.syncMetadataTable.grantReadWriteData(syncFn.function);
    props.intentsTable.grantReadWriteData(syncFn.function);
    // Match events are append-only telemetry: write-only grant.
    props.matchEventsTable.grantWriteData(syncFn.function);
    props.cropsTable.grantReadWriteData(syncFn.function);
    props.usersTable.grantReadData(syncFn.function);
    props.tripsTable.grantReadWriteData(syncFn.function);
    props.deliveriesTable.grantReadWriteData(syncFn.function);
    props.ordersTable.grantReadWriteData(syncFn.function);
    props.rateLimitsTable.grantReadWriteData(syncFn.function);
    props.fraudSignalsTable.grantReadWriteData(syncFn.function);
    // Ratings: sync creates them and reads the order to validate participation.
    props.ratingsTable.grantReadWriteData(syncFn.function);
    // Messages: sync creates them (context participants verified against the
    // crops/orders/trips/deliveries tables, already granted above).
    props.messagesTable.grantReadWriteData(syncFn.function);
    // Transaction audit is append-only: write-only grant. Order/delivery
    // lifecycle transitions are logged here for the admin monitor.
    props.transactionAuditTable.grantWriteData(syncFn.function);
    // QR handoff secrets: sync writes the farmer's nonce and reads it to verify a
    // scanner's echo (and marks it consumed). Never echoed back to any client.
    props.deliveryQrSecretsTable.grantReadWriteData(syncFn.function);
    // Escrows: on delivery confirmation sync looks up the linked escrow by deliveryId
    // and stamps it for the auto-release cron. Read-write (CAS status update) + indexes.
    props.escrowsTable.grantReadWriteData(syncFn.function);
    // Escrow audit is append-only: write-only grant (order-funded escrow leg).
    props.escrowAuditTable.grantWriteData(syncFn.function);
    // ESC-07: a refused delivery dispute is audited once per escrow, delivery and reason a day,
    // so the sync Lambda reads the escrow's trail first (Query only, no Scan or GetItem).
    props.escrowAuditTable.grant(syncFn.function, "dynamodb:Query");
    // LT-05: a location_ping is one conditional PutItem, and a fix first reads its clock hour of
    // the trip's partition (Query) for the rate limit and the repeat check. No GetItem, Scan,
    // update or delete: rows are written once and expire by TTL.
    props.tripLocationsTable.grant(syncFn.function, "dynamodb:PutItem", "dynamodb:Query");
    // FUP-67: a listing_report reads its own key (GetItem, the repeat check) and is one
    // conditional PutItem. No Query, Scan, update or delete: only admins read the queue.
    props.listingReportsTable.grant(syncFn.function, "dynamodb:GetItem", "dynamodb:PutItem");
    // FRD-12: the delivery_photo attach HEADs the photo (s3:GetObject on the photo keys; the
    // bucket-level List lets a missing photo answer 404, not 403). Read only, photos only.
    props.uploadsBucket.grantRead(syncFn.function, "uploads/photos/*");
    // ESC-28 (rating-api 1.2.0 stored aggregate): the rating create transaction ADDs ratingSum and
    // ratingCount on the ratee's users row, and the best-effort refresh writes its reputation map
    // (UpdateItem on the base table; sync keeps its read grant above). On reputation-events, the
    // refresh reads the ratee's partition (base-table Query) and writes the heal marker and any tier
    // row (PutItem, UpdateItem): no index, no delete.
    syncFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:UpdateItem"],
        resources: [props.usersTable.tableArn],
      }),
    );
    syncFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query", "dynamodb:PutItem", "dynamodb:UpdateItem"],
        resources: [props.reputationEventsTable.tableArn],
      }),
    );
    reputationPolicyParam.grantRead(syncFn.function);

    // ─── Pagination Secret (Secrets Manager prod / SSM dev+staging) ───
    // SECURITY: Generated at deploy time (prod) or by the seed script
    // (non-prod), never hardcoded in source.

    const paginationSecret = envScopedSecret(this, "PaginationSecret", {
      isProd: envConfig.isProd,
      name: `/${prefix}/pagination-secret`,
      description:
        "HMAC signing key for pagination tokens — auto-generated at deploy time",
      generateSecretString: {
        excludePunctuation: true,
        passwordLength: 64,
      },
    });

    // ─── Crop photos (crop-listing-api.md 1.6.0) ─────────────────────
    //
    // Devices PUT originals (presigned, type and length signed, 10 MiB cap)
    // into a private incoming bucket. Its ObjectCreated event runs the photo
    // processor, which writes the only two renditions kept (320 px thumb and
    // 1024 px full WebP, metadata stripped) to a private photos bucket and
    // deletes the original. Reads go through short-lived presigned GETs that
    // CropFunction signs after its role gate. Both buckets live in this stack
    // because an S3 notification to a Lambda in another stack is a
    // cross-stack cycle. Design and costs: crop-photos-architecture.md.
    const photoBucketRemoval = envConfig.isProd
      ? { removalPolicy: cdk.RemovalPolicy.RETAIN, autoDeleteObjects: false }
      : { removalPolicy: cdk.RemovalPolicy.DESTROY, autoDeleteObjects: true };

    const cropPhotoIncomingBucket = new s3.Bucket(
      this,
      "CropPhotoIncomingBucket",
      {
        encryption: s3.BucketEncryption.S3_MANAGED,
        blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
        enforceSSL: true,
        ...photoBucketRemoval,
        // Originals live seconds; anything the processor left goes in a day.
        lifecycleRules: [
          {
            expiration: cdk.Duration.days(1),
            abortIncompleteMultipartUploadAfter: cdk.Duration.days(1),
          },
        ],
        cors: [
          {
            allowedMethods: [s3.HttpMethods.PUT],
            allowedOrigins: corsOrigins,
            allowedHeaders: ["Content-Type"],
            maxAge: 3000,
          },
        ],
      },
    );

    const cropPhotosBucket = new s3.Bucket(this, "CropPhotosBucket", {
      encryption: s3.BucketEncryption.S3_MANAGED,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      enforceSSL: true,
      ...photoBucketRemoval,
      // Renditions are 8 to 150 KB: S3 Standard is the cheapest class for
      // them (IA and Glacier bill at least 128 KB per object plus retrieval).
      // Listings expire after 30 days; a year covers orders and disputes.
      lifecycleRules: [
        {
          expiration: cdk.Duration.days(365),
          abortIncompleteMultipartUploadAfter: cdk.Duration.days(1),
        },
      ],
      cors: [
        {
          // Flutter web (CanvasKit) fetches images with XHR, so GETs need CORS.
          allowedMethods: [s3.HttpMethods.GET, s3.HttpMethods.HEAD],
          allowedOrigins: corsOrigins,
          allowedHeaders: ["*"],
          maxAge: 3000,
        },
      ],
    });

    const cropPhotoProcessorFn = new WeredaLambda(
      this,
      "CropPhotoProcessorFunction",
      {
        functionName: `${prefix}-crop-photo-processor`,
        description:
          "S3 ObjectCreated on crop photo originals: 320 px thumb + 1024 px full WebP, original deleted",
        handlerName: "CropPhotoProcessorFunction",
        environment: {
          CROP_PHOTO_INCOMING_BUCKET: cropPhotoIncomingBucket.bucketName,
          CROP_PHOTOS_BUCKET: cropPhotosBucket.bucketName,
        },
        // Decoding a 12 MP photo takes ~50 MB per frame; 1024 MB also buys
        // the CPU that keeps one photo near a second (~0.00002 USD each).
        memorySize: 1024,
        timeout: cdk.Duration.seconds(30),
        reservedConcurrentExecutions: 5,
      },
    );
    cropPhotoIncomingBucket.grantRead(cropPhotoProcessorFn.function);
    cropPhotoIncomingBucket.grantDelete(cropPhotoProcessorFn.function);
    cropPhotosBucket.grantPut(cropPhotoProcessorFn.function);
    cropPhotoIncomingBucket.addEventNotification(
      s3.EventType.OBJECT_CREATED,
      new s3n.LambdaDestination(cropPhotoProcessorFn.function),
    );

    // ─── Crop Function ───────────────────────────────────────────────

    const cropFn = new WeredaLambda(this, "CropFunction", {
      functionName: `${prefix}-crops`,
      description:
        "GET /crops (read-only browse, photo thumbs) and POST /crops/{id}/photos (presigned photo uploads)",
      handlerName: "CropFunction",
      environment: {
        CROPS_TABLE: props.cropsTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
        CROP_PHOTO_INCOMING_BUCKET: cropPhotoIncomingBucket.bucketName,
        CROP_PHOTOS_BUCKET: cropPhotosBucket.bucketName,
        // ESC-29 (reputation-api.md 8.3 and section 7): off, nothing is hidden and no owner field is returned.
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        REPUTATION_PUBLIC_TAGS_ENABLED: reputationPublicTags,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    // Crop function needs read-only access to crops table
    props.cropsTable.grantReadData(cropFn.function);
    paginationSecret.grantRead(cropFn.function);
    // Presigned URLs carry the signer's rights: PUT originals only, GET
    // renditions only. Nothing else on either bucket.
    cropPhotoIncomingBucket.grantPut(cropFn.function);
    cropPhotosBucket.grantRead(cropFn.function);

    // ─── Trip Function ────────────────────────────────────────────────

    const tripFn = new WeredaLambda(this, "TripFunction", {
      functionName: `${prefix}-trips`,
      description: "GET /trips — browse trip listings (read-only)",
      handlerName: "TripFunction",
      environment: {
        TRIPS_TABLE: props.tripsTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
        // FUP-122: the browse reports orderLeadHours (24 on, 0 off) so the apps drop from the
        // match suggestions the trips the sync order create refuses. Same value as on sync.
        COMMITMENTS_ENABLED: commitmentsEnabled,
        // ESC-29 (reputation-api.md 8.3 and section 7): off, nothing is hidden and no owner field is returned.
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        REPUTATION_PUBLIC_TAGS_ENABLED: reputationPublicTags,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    // Trip function needs read-only access to trips table
    props.tripsTable.grantReadData(tripFn.function);
    paginationSecret.grantRead(tripFn.function);

    // ─── Match Function ───────────────────────────────────────────────

    const matchFn = new WeredaLambda(this, "MatchFunction", {
      functionName: `${prefix}-match`,
      description: "GET /match (buyer intent matching, ranked crop listings) and GET /intents (the buyer's own requests)",
      handlerName: "MatchFunction",
      environment: {
        CROPS_TABLE: props.cropsTable.tableName,
        // SIM-56: GET /intents reads the caller's own partition (push-alerts-api.md 1.5.0).
        INTENTS_TABLE: props.intentsTable.tableName,
        // ESC-29 (reputation-api.md 8.3 and section 7): off, nothing is hidden and no owner field is returned.
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        REPUTATION_PUBLIC_TAGS_ENABLED: reputationPublicTags,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    // Match function reads crops to score them against the buyer's criteria
    props.cropsTable.grantReadData(matchFn.function);
    // SIM-56: GET /intents is one Query of the caller's partition, nothing else.
    props.intentsTable.grant(matchFn.function, "dynamodb:Query");

    // ─── Push Alerts: SNS Platform Application (FCM) ───────────────────
    // FCM is the only delivery path to Android; SNS wraps it. CloudFormation
    // has NO resource type for SNS platform applications, so it is created
    // out-of-band (CLI/console) with the FCM v1 service-account credential, and
    // named `${prefix}-fcm` — see .ai/push-notifications-setup.md. Its ARN is
    // therefore deterministic and referenced here.
    const fcmPlatformAppArn =
      `arn:aws:sns:${this.region}:${this.account}:app/GCM/${prefix}-fcm`;
    const fcmEndpointArnPattern =
      `arn:aws:sns:${this.region}:${this.account}:endpoint/GCM/${prefix}-fcm/*`;

    // ─── Device Token Function (POST /device-tokens) ───────────────────
    const deviceTokenFn = new WeredaLambda(this, "DeviceTokenFunction", {
      functionName: `${prefix}-device-tokens`,
      description: "POST /device-tokens — register an FCM device token",
      handlerName: "DeviceTokenFunction",
      environment: {
        DEVICE_TOKENS_TABLE: props.deviceTokensTable.tableName,
        PLATFORM_APP_ARN: fcmPlatformAppArn,
        // ACC-02: a deleting or deleted account registers no device (410 ACCOUNT_004).
        USERS_TABLE: props.usersTable.tableName,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    props.deviceTokensTable.grantReadWriteData(deviceTokenFn.function);
    props.usersTable.grant(deviceTokenFn.function, "dynamodb:GetItem");
    deviceTokenFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["sns:CreatePlatformEndpoint"],
        resources: [fcmPlatformAppArn],
      }),
    );
    // FUP-121: a shared phone's endpoint is read (owner, enabled, token) and rebound to
    // the user who registers it, or disabled when the previous owner's row cannot go.
    deviceTokenFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["sns:GetEndpointAttributes", "sns:SetEndpointAttributes"],
        resources: [fcmEndpointArnPattern],
      }),
    );

    // ─── Crop Match Stream Function (match-on-insert push alerts) ──────
    const matchStreamFn = new WeredaLambda(this, "CropMatchStreamFunction", {
      functionName: `${prefix}-crop-match-stream`,
      description: "Crops DynamoDB Stream → match active intents → push alert",
      handlerName: "CropMatchStreamFunction",
      environment: {
        INTENTS_TABLE: props.intentsTable.tableName,
        DEVICE_TOKENS_TABLE: props.deviceTokensTable.tableName,
      },
      timeout: cdk.Duration.seconds(30),
      memorySize: 256,
      reservedConcurrentExecutions: 5,
    });
    props.intentsTable.grantReadData(matchStreamFn.function);
    props.deviceTokensTable.grantReadData(matchStreamFn.function);
    matchStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["sns:Publish"],
        resources: [fcmEndpointArnPattern],
      }),
    );
    // Consume the crops NEW_IMAGE stream, only for INSERT events (new listings).
    // Price/quantity matching is done in the handler (stream filters can't match
    // numeric ranges).
    matchStreamFn.function.addEventSource(
      new lambdaEventSources.DynamoEventSource(props.cropsTable, {
        startingPosition: lambda.StartingPosition.LATEST,
        batchSize: 10,
        retryAttempts: 3,
        // Partial batch responses: the handler returns the sequence numbers of
        // records that failed (e.g. a transient DynamoDB throttle) so Lambda
        // retries only those instead of checkpointing the whole batch and
        // silently dropping a buyer's push alert.
        reportBatchItemFailures: true,
        filters: [
          lambda.FilterCriteria.filter({
            eventName: lambda.FilterRule.isEqual("INSERT"),
          }),
        ],
      }),
    );

    // ─── Message Stream Function (chat message → recipient push) ───────
    // Messages DynamoDB Stream → look up the recipient's device endpoints →
    // push an FCM notification. Mirrors CropMatchStreamFunction but simpler:
    // the recipientId is read straight off the NEW_IMAGE (no scoring/query).
    const messageStreamFn = new WeredaLambda(this, "MessageStreamFunction", {
      functionName: `${prefix}-message-stream`,
      description: "Messages DynamoDB Stream → push chat notification to recipient",
      handlerName: "MessageStreamFunction",
      environment: {
        DEVICE_TOKENS_TABLE: props.deviceTokensTable.tableName,
      },
      timeout: cdk.Duration.seconds(30),
      memorySize: 256,
      reservedConcurrentExecutions: 5,
    });
    props.deviceTokensTable.grantReadData(messageStreamFn.function);
    messageStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["sns:Publish"],
        resources: [fcmEndpointArnPattern],
      }),
    );
    // Messages are immutable, so only INSERT events matter.
    messageStreamFn.function.addEventSource(
      new lambdaEventSources.DynamoEventSource(props.messagesTable, {
        startingPosition: lambda.StartingPosition.LATEST,
        batchSize: 10,
        retryAttempts: 3,
        reportBatchItemFailures: true,
        filters: [
          lambda.FilterCriteria.filter({
            eventName: lambda.FilterRule.isEqual("INSERT"),
          }),
        ],
      }),
    );

    // ─── Order Notify Stream Function (ESC-21 order notifications) ─────
    // The orders, deliveries and escrows NEW_AND_OLD_IMAGES streams feed one
    // consumer, and since ESC-28 the reputation-events NEW_IMAGE stream too
    // (penalty_recorded, tier_changed; reputation-architecture.md section 9).
    // NotificationRules turns each old/new image pair into events;
    // every event becomes an inbox row (notifications table) and, when the
    // event asks for one, an FCM push in the recipient's preferred language.
    // A record that keeps failing after its retries (and the batch bisection)
    // is dropped from the stream and its metadata lands in this DLQ, which the
    // monitoring stack alarms on (NotifyDlqMessages).
    const notifyDlq = new sqs.Queue(this, "OrderNotifyDlq", {
      queueName: `${prefix}-notify-dlq`,
      retentionPeriod: cdk.Duration.days(14),
      encryption: sqs.QueueEncryption.SQS_MANAGED,
      enforceSSL: true,
    });

    const orderNotifyStreamFn = new WeredaLambda(this, "OrderNotifyStreamFunction", {
      functionName: `${prefix}-order-notify-stream`,
      description:
        "Orders, deliveries, escrows and reputation-events streams: write the notification inbox and push to the recipient (ESC-21, ESC-28)",
      handlerName: "OrderNotifyStreamFunction",
      environment: {
        DEVICE_TOKENS_TABLE: props.deviceTokensTable.tableName,
        // Recipients' preferredLanguage, one BatchGetItem per batch.
        USERS_TABLE: props.usersTable.tableName,
        NOTIFICATIONS_TABLE: props.notificationsTable.tableName,
      },
      timeout: cdk.Duration.seconds(30),
      memorySize: 256,
      reservedConcurrentExecutions: 5,
    });
    // Least privilege: the recipient's endpoints (Query) and the prune of a disabled or missing
    // SNS endpoint (DeleteItem); the recipients' preferredLanguage (one BatchGetItem per batch, no
    // scan of the users table); the conditional inbox PutItem (the inbox is never read here).
    props.deviceTokensTable.grant(orderNotifyStreamFn.function, "dynamodb:Query", "dynamodb:DeleteItem");
    props.usersTable.grant(orderNotifyStreamFn.function, "dynamodb:BatchGetItem");
    props.notificationsTable.grant(orderNotifyStreamFn.function, "dynamodb:PutItem");
    orderNotifyStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["sns:Publish"],
        resources: [fcmEndpointArnPattern],
      }),
    );
    // Shared settings for the four sources. Partial batch responses retry
    // only the failed records; bisecting splits a batch that keeps failing so
    // one bad record cannot hold back the rest of its shard; records older
    // than a day are dropped (to the DLQ) rather than pushed late.
    const notifySourceProps = {
      startingPosition: lambda.StartingPosition.LATEST,
      batchSize: 10,
      retryAttempts: 3,
      bisectBatchOnError: true,
      reportBatchItemFailures: true,
      maxRecordAge: cdk.Duration.days(1),
      onFailure: new lambdaEventSources.SqsDlq(notifyDlq),
    };
    // Orders and escrows are read on MODIFY (status moves), deliveries on
    // INSERT (a delivery created for an order) and on the one MODIFY that
    // stamps nearbyNotifiedAt (LT-07: a check-in put the truck within 5 km
    // of the drop-off, the buyer hears truck_nearby). The filters run before
    // the invoke, so every other event costs no Lambda time.
    const onEvent = (eventName: string) =>
      lambda.FilterCriteria.filter({
        eventName: lambda.FilterRule.isEqual(eventName),
      });
    const nearbyStamped = lambda.FilterCriteria.filter({
      eventName: lambda.FilterRule.isEqual("MODIFY"),
      dynamodb: {
        NewImage: { nearbyNotifiedAt: { S: lambda.FilterRule.exists() } },
        OldImage: { nearbyNotifiedAt: { S: lambda.FilterRule.notExists() } },
      },
    });
    // ESC-28: on the reputation-events stream, only the INSERT of a penalty row or a TIER# row
    // reaches the notifier (the rules skip a penalty with 0 points). Heal markers, completions,
    // restrictions, overrides and every later row update cost no invocation. The notifier reads
    // only the record: no grant on the reputation-events table.
    const reputationNotified = lambda.FilterCriteria.filter({
      eventName: lambda.FilterRule.isEqual("INSERT"),
      dynamodb: {
        NewImage: {
          code: {
            S: lambda.FilterRule.or(
              "DECLINED_REQUEST",
              "RESPONSE_EXPIRED",
              "CANCELLED_AFTER_ACCEPT",
              "LATE_CANCEL",
              "TRIP_CANCELLED_WITH_BOOKINGS",
              "NO_SHOW",
              "DELIVERY_OVERDUE",
              "TIER_CHANGED",
            ),
          },
        },
      },
    });
    for (const [table, filters] of [
      [props.ordersTable, [onEvent("MODIFY")]],
      [props.deliveriesTable, [onEvent("INSERT"), nearbyStamped]],
      [props.escrowsTable, [onEvent("MODIFY")]],
      [props.reputationEventsTable, [reputationNotified]],
    ] as const) {
      orderNotifyStreamFn.function.addEventSource(
        new lambdaEventSources.DynamoEventSource(table, {
          ...notifySourceProps,
          filters: [...filters],
        }),
      );
    }

    // ─── Reputation Stream Function (ESC-28, reputation-architecture.md section 7) ─
    // The second consumer of the orders stream (after order-notify-stream). DynamoDB Streams serve
    // two readers per shard without throttling; a third consumer needs a fan-out first (EventBridge
    // Pipes or Kinesis, section 7.1). Each record that matches a derivation rule becomes a
    // reputation event on the party (ReputationService). A record whose handling throws is parked
    // in the parked queue (only the attributes the derivation reads, well under 2 KB) and reported
    // as processed, so its shard moves on; order-cron's hourly reputation_unpark replays it, and
    // after 5 receives the redrive moves it to the parked DLQ. Only when parking fails too is the
    // record a batch item failure: the stream retries it, then its metadata lands in the
    // reputation DLQ. All three queues are alarmed in the monitoring stack.
    const reputationDlq = new sqs.Queue(this, "ReputationDlq", {
      queueName: `${prefix}-reputation-dlq`,
      retentionPeriod: cdk.Duration.days(14),
      encryption: sqs.QueueEncryption.SQS_MANAGED,
      enforceSSL: true,
    });
    const reputationParkedDlq = new sqs.Queue(this, "ReputationParkedDlq", {
      queueName: `${prefix}-reputation-parked-dlq`,
      retentionPeriod: cdk.Duration.days(14),
      encryption: sqs.QueueEncryption.SQS_MANAGED,
      enforceSSL: true,
    });
    const reputationParkedQueue = new sqs.Queue(this, "ReputationParkedQueue", {
      queueName: `${prefix}-reputation-parked`,
      retentionPeriod: cdk.Duration.days(14),
      encryption: sqs.QueueEncryption.SQS_MANAGED,
      enforceSSL: true,
      // Longer than the 5 minute order-cron run that receives from it, so a message being
      // replayed is never handed out twice at once.
      visibilityTimeout: cdk.Duration.minutes(6),
      deadLetterQueue: { queue: reputationParkedDlq, maxReceiveCount: 5 },
    });

    const reputationStreamFn = new WeredaLambda(this, "ReputationStreamFunction", {
      functionName: `${prefix}-reputation-stream`,
      description:
        "Orders stream: derive reputation events (penalties, completions) and park the records that fail (ESC-28)",
      handlerName: "ReputationStreamFunction",
      environment: {
        REPUTATION_EVENTS_TABLE: props.reputationEventsTable.tableName,
        // The party's row: role, deletionState, suspended and the reputation map (read, then
        // written with a version compare-and-set in the event's transaction).
        USERS_TABLE: props.usersTable.tableName,
        // Reachability of a RESPONSE_EXPIRED party: one key-only Query, Limit 1.
        DEVICE_TOKENS_TABLE: props.deviceTokensTable.tableName,
        FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName,
        REPUTATION_PARKED_QUEUE_URL: reputationParkedQueue.queueUrl,
        REPUTATION_POLICY_PARAM: reputationPolicyParam.parameterName,
        // Off: every record is acknowledged without a write (section 7.4).
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        // Off: RESPONSE_EXPIRED is recorded with 0 points (timeout_penalties_off).
        REPUTATION_TIMEOUT_PENALTIES_ENABLED: timeoutPenalties,
        // ESC-29 (reputation-architecture.md 6.3): the owner fields copied onto the party's active rows.
        CROPS_TABLE: props.cropsTable.tableName,
        TRIPS_TABLE: props.tripsTable.tableName,
        // ESC-33 (section 13): the buyer decline pattern reads the buyer's orders on BuyerIdCreatedAtIndex.
        ORDERS_TABLE: props.ordersTable.tableName,
      },
      timeout: cdk.Duration.seconds(30),
      memorySize: 256,
      // Kept at or above the orders stream's shard count: a shard with no free concurrency waits,
      // and its iterator age climbs towards the 24 hour maxRecordAge (alarmed at 1 hour).
      reservedConcurrentExecutions: 10,
    });
    // Least privilege, base tables only (no index): the party's users row (GetItem, then the
    // conditional UpdateItem of the reputation map); the party's events (Query) and the event,
    // restriction, tier, earn-back and heal writes (PutItem, UpdateItem); the reachability Query on
    // device tokens; the fraud signals Query (FRD hooks). Nothing is deleted.
    reputationStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:GetItem", "dynamodb:UpdateItem"],
        resources: [props.usersTable.tableArn],
      }),
    );
    reputationStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query", "dynamodb:PutItem", "dynamodb:UpdateItem"],
        resources: [props.reputationEventsTable.tableArn],
      }),
    );
    reputationStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [props.deviceTokensTable.tableArn, props.fraudSignalsTable.tableArn],
      }),
    );
    // ESC-33 (reputation-architecture.md section 13): a recorded NO_SHOW or DELIVERY_OVERDUE writes a
    // fraud signal on the party, and a decline or expiry checks the buyer's orders (one index, Query
    // only) and may write BUYER_DECLINE_PATTERN on the buyer. PutItem only: signals are never updated
    // or deleted from here.
    reputationStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:PutItem"],
        resources: [props.fraudSignalsTable.tableArn],
      }),
    );
    reputationStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [`${props.ordersTable.tableArn}/index/BuyerIdCreatedAtIndex`],
      }),
    );
    // ESC-29, the copy refresh: the owner's rows through their owner index (Query), the crop
    // statuses by key (BatchGetItem; the farmer index does not project status) and the conditional
    // UpdateItem of the five owner fields. No Scan, no put, no delete.
    reputationStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [
          `${props.cropsTable.tableArn}/index/FarmerIdCreatedAtIndex`,
          `${props.tripsTable.tableArn}/index/TruckerIdCreatedAtIndex`,
        ],
      }),
    );
    reputationStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:BatchGetItem", "dynamodb:UpdateItem"],
        resources: [props.cropsTable.tableArn],
      }),
    );
    reputationStreamFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:UpdateItem"],
        resources: [props.tripsTable.tableArn],
      }),
    );
    reputationParkedQueue.grantSendMessages(reputationStreamFn.function);
    reputationPolicyParam.grantRead(reputationStreamFn.function);
    // Section 7.1. Starting at LATEST: no points for anything before the deploy. Partial batch
    // responses and bisection keep one bad record from holding its shard; parallelization factor 1
    // keeps the per-shard order. The five filters run before the invoke, each on a MODIFY whose new
    // image carries a value a derivation rule reads; anything else costs no invocation. They also
    // match a later change of a row that keeps those values: the derivation compares the old and
    // new images and ignores it.
    const orderModifiedWith = (field: string, rule: string[] | { [key: string]: boolean }[]) =>
      lambda.FilterCriteria.filter({
        eventName: lambda.FilterRule.isEqual("MODIFY"),
        dynamodb: { NewImage: { [field]: { S: rule } } },
      });
    reputationStreamFn.function.addEventSource(
      new lambdaEventSources.DynamoEventSource(props.ordersTable, {
        startingPosition: lambda.StartingPosition.LATEST,
        batchSize: 25,
        parallelizationFactor: 1,
        retryAttempts: 10,
        bisectBatchOnError: true,
        reportBatchItemFailures: true,
        maxRecordAge: cdk.Duration.hours(24),
        onFailure: new lambdaEventSources.SqsDlq(reputationDlq),
        filters: [
          orderModifiedWith("farmerResponse", lambda.FilterRule.or("declined", "expired")),
          orderModifiedWith("truckerResponse", lambda.FilterRule.or("declined", "expired", "cancelled")),
          orderModifiedWith("transportFailure", lambda.FilterRule.exists()),
          orderModifiedWith("cancelReason", lambda.FilterRule.or("farmer_cancelled", "farmer_no_show")),
          orderModifiedWith("status", lambda.FilterRule.or("settled", "disputed")),
        ],
      }),
    );

    // ─── R4 SSM Parameters ────────────────────────────────────────────

    const commissionRateParam = new ssm.StringParameter(
      this,
      "CommissionRate",
      {
        parameterName: `/${prefix}/escrow/commission-rate`,
        stringValue: "0.05",
        description: "Platform commission rate (default 5%)",
      },
    );

    // SyncFunction needs commission rate for order processing
    syncFn.function.addEnvironment(
      "COMMISSION_RATE_PARAM",
      commissionRateParam.parameterName,
    );
    commissionRateParam.grantRead(syncFn.function);

    const disputeWindowParam = new ssm.StringParameter(this, "DisputeWindow", {
      parameterName: `/${prefix}/escrow/dispute-window-hours`,
      stringValue: "48",
      description:
        "Dispute window duration in hours after delivery confirmation",
    });

    const escrowTimeoutParam = new ssm.StringParameter(this, "EscrowTimeout", {
      parameterName: `/${prefix}/escrow/timeout-days`,
      stringValue: "7",
      description: "Escrow auto-refund timeout in days",
    });

    // ESC-16 (spec 4.5, F9): the order deadline timings, in hours, actually read by the code
    // (server/src/Helpers/OrderTimings.cs). Each Lambda that writes orders reads them once per
    // container, in one GetParameters call, so a change takes effect as containers recycle. A
    // missing or invalid value (outside 1 to 720) keeps the default below and logs
    // ORDER_TIMINGS_DEFAULTED. The defaults are the values the code used before.
    const orderTimingsPath = `/${prefix}/orders/`;
    const orderTimings: Array<[string, string, string]> = [
      ["unpaid-expiry-hours", "24", "T1: an unpaid order is cancelled this long after it was created or entered pending_payment"],
      ["payment-verification-hours", "24", "T2: a submitted payment not verified in this time fails"],
      ["response-window-hours", "24", "T3 and T4: the farmer's and the trucker's time to answer; also the order-create lead before departure"],
      ["departure-margin-hours", "12", "T3 and T4 end at least this long before the trip departs"],
      ["buyer-choice-window-hours", "48", "T5: the buyer's time to decide after the transport failed"],
      ["reminder-lead-hours", "6", "The reminder before T3, T4 and T5 is due this long before the deadline"],
      ["pickup-grace-hours", "48", "T7, trip order: the pickup is overdue this long after the trip departs"],
      ["self-pickup-window-hours", "168", "T7, self-pickup: the pickup is overdue this long after the farmer accepted"],
    ];
    for (const [name, value, description] of orderTimings) {
      new ssm.StringParameter(this, `OrderTiming-${name}`, {
        parameterName: `${orderTimingsPath}${name}`,
        stringValue: value,
        description,
      });
    }
    // GetParameters on the path only: no decryption (String parameters), no other path.
    const orderTimingsRead = new iam.PolicyStatement({
      actions: ["ssm:GetParameters"],
      resources: [
        this.formatArn({
          service: "ssm",
          resource: "parameter",
          resourceName: `${prefix}/orders/*`,
        }),
      ],
    });
    const readsOrderTimings = (fn: lambda.Function) => {
      fn.addEnvironment("ORDER_TIMINGS_PARAM_PATH", orderTimingsPath);
      fn.addToRolePolicy(orderTimingsRead);
    };

    // ─── Fayda identity provider: mock (dev), sandbox (dev) or live, by config ─
    // The server code is identical in every mode; only the issuer, client id and
    // client private key it reads from SSM differ, plus two dev-only env flags on
    // the verify Lambda. faydaMode 'mock' deploys the mock eSignet provider
    // (tools/fayda-mock) and points these values at it. 'sandbox' points them at
    // the real VeriFayda sandbox with its shared test client, whose registered
    // redirect (http://localhost:3000/callback) a local relay forwards to the app
    // (tools/fayda-sandbox-relay). A deploy overrides the config with
    // `-c faydaMode=mock|sandbox|live`. Runbook: specs/features/fayda-mock-server.md.
    // The construct refuses isProd, and ProductionBypassGuard refuses the FAYDA_MOCK
    // and FAYDA_SANDBOX markers, so neither dev provider can sit beside production.
    const isDev = envConfig.name === "dev" && !envConfig.isProd;
    const faydaModeContext = this.node.tryGetContext("faydaMode") as unknown;
    let faydaMode = envConfig.faydaMode;
    if (faydaModeContext !== undefined) {
      if (!isDev) {
        // Staging and prod run the reviewed config only: a one-flag deploy must never
        // be able to put a dev provider there.
        cdk.Annotations.of(this).addError(
          `faydaMode context override is only allowed in dev (environment '${envConfig.name}').`,
        );
      } else if (faydaModeContext === "mock" || faydaModeContext === "live" || faydaModeContext === "sandbox") {
        faydaMode = faydaModeContext;
      } else {
        cdk.Annotations.of(this).addError(
          `faydaMode context '${String(faydaModeContext)}' is not one of mock, sandbox, live.`,
        );
      }
    }
    if (faydaMode === "mock" && envConfig.isProd) {
      cdk.Annotations.of(this).addError(
        "faydaMode 'mock' is not allowed in a production environment; use 'live'.",
      );
    }
    // The shared sandbox test client and its key are public: dev only, never staging or prod.
    if (faydaMode === "sandbox" && !isDev) {
      cdk.Annotations.of(this).addError(
        `faydaMode 'sandbox' is not allowed outside dev (environment '${envConfig.name}'); use 'live'.`,
      );
    }
    if (faydaMode === "live" && !envConfig.faydaClientId) {
      cdk.Annotations.of(this).addError(
        "faydaMode 'live' needs faydaClientId (the client_id from the Fayda partner portal) in environments.ts.",
      );
    }
    const useFaydaMock = faydaMode === "mock" && !envConfig.isProd;
    const useFaydaSandbox = faydaMode === "sandbox" && isDev;
    const faydaSandboxRedirectUri =
      envConfig.faydaSandboxRedirectUri ?? "http://localhost:3000/callback";
    if (useFaydaSandbox && !envConfig.faydaSandboxClientId) {
      cdk.Annotations.of(this).addError(
        "faydaMode 'sandbox' needs faydaSandboxClientId (the shared VeriFayda test client id) in environments.ts.",
      );
    }
    // Web app callback pages (mobile/web/fayda-callback.html). Browsers cannot open
    // the weredaconnect:// deep link, so the web app gets an https callback, but only
    // against a dev provider (mock or sandbox): the real eSignet client registers the
    // deep links alone. In sandbox mode the local relay's target is one of these pages.
    const faydaDevWebRedirectUris = useFaydaMock || useFaydaSandbox
      ? [
          `https://${envConfig.domain.webappSubdomain}.${envConfig.domain.zoneName}`,
          ...(envConfig.extraCorsOrigins ?? []),
          // Local `flutter run -d chrome --web-port 8080`.
          "http://localhost:8080",
        ].map((origin) => `${origin}/fayda-callback.html`)
      : [];
    const faydaMock = useFaydaMock
      ? new FaydaMockConstruct(this, "FaydaMock", {
          prefix,
          removalPolicy: cdk.RemovalPolicy.DESTROY,
          isProd: envConfig.isProd,
          // Registration and link-later callbacks (FaydaStartRequest.IsAllowedRedirect).
          allowedRedirectUris: [
            "weredaconnect://fayda-callback",
            "weredaconnect://fayda-link-callback",
            ...faydaDevWebRedirectUris,
          ],
        })
      : undefined;

    // ─── Fayda SSM Parameters ──────────────────────────────────────────

    const faydaClientIdParam = new ssm.StringParameter(
      this,
      "FaydaClientId",
      {
        parameterName: `/${prefix}/fayda/client-id`,
        stringValue:
          faydaMock?.clientId
          ?? (useFaydaSandbox ? envConfig.faydaSandboxClientId : envConfig.faydaClientId)
          ?? "PLACEHOLDER",
        description: "Fayda OIDC client ID — update after registration",
      },
    );

    const faydaIssuerUrlParam = new ssm.StringParameter(
      this,
      "FaydaIssuerUrl",
      {
        parameterName: `/${prefix}/fayda/issuer-url`,
        // Live VeriFayda eSignet (discovery under /v1/esignet/oidc/.well-known/...).
        stringValue: faydaMock?.issuerUrl ?? envConfig.faydaIssuerUrl ?? "https://esignet.ida.fayda.et",
        description: "Fayda OIDC issuer URL",
      },
    );

    // ─── Fayda Secrets (Secrets Manager prod / SSM dev+staging) ───────

    // The relying party private key for private_key_jwt, the only client
    // authentication Fayda accepts (the sample app's PRIVATE_KEY_BASE64: a base64
    // private JWK). Same parameter, format and env var in mock and live mode:
    // seeded as PLACEHOLDER, then filled by the mock on first use (dev) or with
    // the key registered at the Fayda partner portal (live). Prod keeps it in
    // Secrets Manager, non-prod in an SSM SecureString (envScopedSecret).
    const faydaClientPrivateKey = envScopedSecret(this, "FaydaClientPrivateKey", {
      isProd: envConfig.isProd,
      name: `/${prefix}/fayda/client-private-key`,
      description:
        "Fayda OIDC client private key (base64 JWK, JWK or PEM) for private_key_jwt; replace after partner registration",
      generateSecretString: {
        excludePunctuation: true,
        passwordLength: 64,
      },
    });

    const faydaSigningSecret = envScopedSecret(this, "FaydaSigningSecret", {
      isProd: envConfig.isProd,
      name: `/${prefix}/fayda/signing-secret`,
      description:
        "HMAC signing key for Fayda verification JWTs — auto-generated at deploy time",
      generateSecretString: {
        excludePunctuation: true,
        passwordLength: 64,
      },
    });

    // Grant RegisterFunction access to Fayda signing secret (also the OTP HMAC key)
    registerFn.function.addEnvironment(
      "FAYDA_SIGNING_SECRET_ARN",
      faydaSigningSecret.reference,
    );
    faydaSigningSecret.grantRead(registerFn.function);
    verifyOtpFn.function.addEnvironment(
      "FAYDA_SIGNING_SECRET_ARN",
      faydaSigningSecret.reference,
    );
    faydaSigningSecret.grantRead(verifyOtpFn.function);

    // ─── FaydaVerify Function ────────────────────────────────────────

    const faydaVerifyFn = new WeredaLambda(this, "FaydaVerifyFunction", {
      functionName: `${prefix}-auth-fayda-verify`,
      description:
        "POST /auth/fayda-verify — verify Fayda OIDC authorization code",
      handlerName: "FaydaVerifyFunction",
      environment: {
        USERS_TABLE: props.usersTable.tableName,
        // OIDC state (state, nonce, PKCE verifier) and per-IP counters live here.
        RATE_LIMITS_TABLE: props.rateLimitsTable.tableName,
        FAYDA_CLIENT_ID_PARAM: faydaClientIdParam.parameterName,
        FAYDA_ISSUER_URL_PARAM: faydaIssuerUrlParam.parameterName,
        FAYDA_CLIENT_PRIVATE_KEY_ARN: faydaClientPrivateKey.reference,
        FAYDA_SIGNING_SECRET_ARN: faydaSigningSecret.reference,
        ...(envConfig.name === 'dev' ? { DEV_FAYDA_BYPASS: "true" } : {}),
        // Dev-provider web callbacks. FAYDA_MOCK and FAYDA_SANDBOX are refused in prod
        // by ProductionBypassGuard, and the server ignores the list without one of them.
        ...(faydaMock
          ? {
              FAYDA_MOCK: "true",
              FAYDA_MOCK_WEB_REDIRECT_URIS: faydaDevWebRedirectUris.join(","),
            }
          : {}),
        // Sandbox: the provider is given the shared test client's registered redirect
        // while the app keeps its own (FaydaStartRequest.SandboxProviderRedirect).
        ...(useFaydaSandbox
          ? {
              FAYDA_SANDBOX: "true",
              FAYDA_SANDBOX_REDIRECT_URI: faydaSandboxRedirectUri,
              FAYDA_MOCK_WEB_REDIRECT_URIS: faydaDevWebRedirectUris.join(","),
            }
          : {}),
      },
      reservedConcurrentExecutions: 20,
    });
    // Read for duplicate checks, write for the link-later route.
    props.usersTable.grantReadWriteData(faydaVerifyFn.function);
    props.rateLimitsTable.grantReadWriteData(faydaVerifyFn.function);
    faydaClientIdParam.grantRead(faydaVerifyFn.function);
    faydaIssuerUrlParam.grantRead(faydaVerifyFn.function);
    faydaClientPrivateKey.grantRead(faydaVerifyFn.function);
    faydaSigningSecret.grantRead(faydaVerifyFn.function);

    // ─── R4.2 Delivery Function ───────────────────────────────────────

    const deliveryFn = new WeredaLambda(this, "DeliveryFunction", {
      functionName: `${prefix}-deliveries`,
      description:
        "GET /deliveries, GET /deliveries/{id}/track, POST /deliveries/{id}/photos: delivery verification and live trip",
      handlerName: "DeliveryFunction",
      environment: {
        DELIVERIES_TABLE: props.deliveriesTable.tableName,
        TRIPS_TABLE: props.tripsTable.tableName,
        UPLOADS_BUCKET: props.uploadsBucket.bucketName,
        USERS_TABLE: props.usersTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
        // LT-06: the track reads the trip's check-ins and the listings (pickup points).
        TRIP_LOCATIONS_TABLE: props.tripLocationsTable.tableName,
        CROPS_TABLE: props.cropsTable.tableName,
        // LT-12: the track reads the delivery's order for the buyer's drop-off note.
        ORDERS_TABLE: props.ordersTable.tableName,
      },
      timeout: cdk.Duration.seconds(15),
      memorySize: 512,
      reservedConcurrentExecutions: 10,
    });
    props.deliveriesTable.grantReadData(deliveryFn.function);
    props.tripsTable.grantReadData(deliveryFn.function);
    props.usersTable.grantReadData(deliveryFn.function);
    props.uploadsBucket.grantReadWrite(deliveryFn.function);
    paginationSecret.grantRead(deliveryFn.function);
    // LT-06: the track only reads. A trip's check-ins are one Query; a listing is read by a Query on
    // its id (the crops table is keyed cropId + createdAt). No write on either table.
    props.tripLocationsTable.grant(deliveryFn.function, "dynamodb:Query");
    props.cropsTable.grant(deliveryFn.function, "dynamodb:Query");
    // LT-12: an order is read by a Query on its id (the orders table is keyed orderId + createdAt).
    // Read only: the note is written through /sync.
    props.ordersTable.grant(deliveryFn.function, "dynamodb:Query");

    // ─── Telebirr live gateway (gated) ────────────────────────────────
    // Off by default: dev/staging run MockTelebirrGateway (TELEBIRR_MOCK=true)
    // at zero extra cost. When enabled (env config or `-c enableTelebirrLive=true`)
    // we provision the real path: a credentials secret + a VPC with a t4g.nano
    // NAT INSTANCE giving the escrow Lambdas a STABLE outbound IP for Telebirr's
    // source-IP allow-list. The public webhook route + Lambda are wired below.
    const enableTelebirrLive =
      (envConfig.enableTelebirrLive ?? false) ||
      this.node.tryGetContext("enableTelebirrLive") === "true";

    let telebirrSecret: SecretRef | undefined;
    let telebirrVpc: ec2.Vpc | undefined;
    let telebirrWebhookFn: WeredaLambda | undefined;
    let telebirrWebhookRoutes: apigateway.HttpRoute[] = [];
    // Spread into the escrow Lambdas so they egress via the NAT's stable IP.
    let escrowVpcProps: { vpc?: ec2.IVpc; vpcSubnets?: ec2.SubnetSelection } = {};

    if (enableTelebirrLive) {
      // Merchant credentials JSON. Populate real values OUT-OF-BAND — prod via
      // `aws secretsmanager put-secret-value`, dev/staging via
      // `aws ssm put-parameter --overwrite` (see .ai/telebirr-testing.md).
      // generateSecretString seeds placeholders ONCE at create (prod; the seed
      // script does the same for non-prod), so the Lambdas cold-start before
      // population and later redeploys never clobber the real values. All keys
      // are non-empty so TelebirrConfig.FromJson won't throw; signing/verifying
      // fails cleanly until real creds are set.
      telebirrSecret = envScopedSecret(this, "TelebirrCredentials", {
        isProd: envConfig.isProd,
        name: `/${prefix}/telebirr/credentials`,
        description:
          "Telebirr merchant credentials (JSON). Populate via put-secret-value; do not edit inline.",
        generateSecretString: {
          generateStringKey: "_generated",
          secretStringTemplate: JSON.stringify({
            fabricAppId: "PLACEHOLDER",
            appSecret: "PLACEHOLDER",
            merchantAppId: "PLACEHOLDER",
            merchantCode: "PLACEHOLDER",
            shortCode: "PLACEHOLDER",
            baseUrl:
              "https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway",
            webUrl:
              "https://developerportal.ethiotelebirr.et:38443/payment/web/paygate",
            merchantPrivateKeyPem: "PLACEHOLDER",
            telebirrPublicKeyPem: "PLACEHOLDER",
            returnUrl: "weredaconnect://payment/complete",
            // ESC-45: left as is (editing this prod template can regenerate the
            // secret). Populate the real value as "pss" (.ai/telebirr-testing.md); the
            // webhook accepts a PSS or a PKCS#1 signature from Telebirr either way.
            signPadding: "pkcs1",
            // Optional Ethiopian egress proxy for OUTBOUND Telebirr calls
            // (http[s]://[user:pass@]host:port). Empty = direct. Telebirr's
            // sandbox only accepts Ethiopian source IPs — set this if a NAT EIP
            // is not sufficient. Can also be set via TELEBIRR_HTTP_PROXY env.
            httpProxy: "",
          }),
        },
      });

      // VPC with a single t4g.nano NAT INSTANCE (not a NAT Gateway — see the
      // "Never NAT Gateways" rule in CLAUDE.md). One NAT serves both AZs to keep
      // cost to ~$3-4/mo. Free gateway endpoints keep DynamoDB/S3 traffic off it.
      const natProvider = ec2.NatProvider.instanceV2({
        instanceType: ec2.InstanceType.of(
          ec2.InstanceClass.T4G,
          ec2.InstanceSize.NANO,
        ),
        defaultAllowedTraffic: ec2.NatTrafficDirection.OUTBOUND_ONLY,
      });
      telebirrVpc = new ec2.Vpc(this, "TelebirrVpc", {
        vpcName: `${prefix}-telebirr`,
        maxAzs: 2,
        natGateways: 1,
        natGatewayProvider: natProvider,
        subnetConfiguration: [
          { name: "public", subnetType: ec2.SubnetType.PUBLIC, cidrMask: 24 },
          {
            name: "private",
            subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS,
            cidrMask: 24,
          },
        ],
      });
      // OUTBOUND_ONLY blocks unsolicited inbound from the internet; still let
      // the private-subnet Lambdas reach the NAT instance.
      natProvider.connections.allowFrom(
        ec2.Peer.ipv4(telebirrVpc.vpcCidrBlock),
        ec2.Port.allTraffic(),
        "Escrow Lambdas -> NAT instance",
      );
      telebirrVpc.addGatewayEndpoint("DdbEndpoint", {
        service: ec2.GatewayVpcEndpointAwsService.DYNAMODB,
      });
      telebirrVpc.addGatewayEndpoint("S3Endpoint", {
        service: ec2.GatewayVpcEndpointAwsService.S3,
      });

      // Stable Elastic IP on the NAT instance — the address you register with
      // Telebirr for outbound source-IP allow-listing. Either reuse a
      // pre-allocated EIP (so it can be registered with Telebirr BEFORE the
      // full deploy — `aws ec2 allocate-address --domain vpc`) or let CDK
      // allocate a fresh one and emit its address as an output.
      const presetEipAllocId =
        envConfig.telebirrNatEipAllocationId ??
        (this.node.tryGetContext("telebirrNatEipAllocationId") as
          | string
          | undefined);

      let natEipAllocationId: string;
      let natEipOutputValue: string;
      if (presetEipAllocId) {
        natEipAllocationId = presetEipAllocId;
        natEipOutputValue = `preallocated:${presetEipAllocId}`;
      } else {
        const natEip = new ec2.CfnEIP(this, "TelebirrNatEip", {
          domain: "vpc",
          tags: [{ key: "Name", value: `${prefix}-telebirr-nat` }],
        });
        natEipAllocationId = natEip.attrAllocationId;
        natEipOutputValue = natEip.ref; // the public IPv4 address
      }
      new ec2.CfnEIPAssociation(this, "TelebirrNatEipAssoc", {
        allocationId: natEipAllocationId,
        instanceId: natProvider.gatewayInstances[0].instanceId,
      });
      new cdk.CfnOutput(this, "TelebirrNatEipOut", {
        value: natEipOutputValue,
        description:
          "Outbound IP to register with Telebirr (source-IP allow-list)",
      });

      escrowVpcProps = {
        vpc: telebirrVpc,
        vpcSubnets: { subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
      };
    }

    // ─── R4.3 Escrow Function ─────────────────────────────────────────

    const escrowFn = new WeredaLambda(this, "EscrowFunction", {
      functionName: `${prefix}-escrow`,
      description:
        "POST /escrow/* — Telebirr escrow management (server-authoritative)",
      handlerName: "EscrowFunction",
      environment: {
        ESCROWS_TABLE: props.escrowsTable.tableName,
        ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
        // ESC-20: release and refund settle a ledger escrow through its rows (and call the gateway at once).
        ESCROW_LEDGER_TABLE: props.escrowLedgerTable.tableName,
        ESCROW_IDEMPOTENCY_TABLE: props.escrowIdempotencyTable.tableName,
        DELIVERIES_TABLE: props.deliveriesTable.tableName,
        ORDERS_TABLE: props.ordersTable.tableName,
        USERS_TABLE: props.usersTable.tableName,
        CROPS_TABLE: props.cropsTable.tableName,
        RATE_LIMITS_TABLE: props.rateLimitsTable.tableName,
        FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName,
        // ESC-03: an admin settlement of a dispute verdict audits the order and
        // delivery hops it repairs.
        TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
        // FUP-123: an order the refund settlement cancels gives its trip booking back,
        // only while COMMITMENTS_ENABLED is on (the flag sync uses for bookings).
        TRIPS_TABLE: props.tripsTable.tableName,
        COMMITMENTS_ENABLED: commitmentsEnabled,
        COMMISSION_RATE_PARAM: commissionRateParam.parameterName,
        DISPUTE_WINDOW_PARAM: disputeWindowParam.parameterName,
        ESCROW_TIMEOUT_PARAM: escrowTimeoutParam.parameterName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
        ...(envConfig.isProd || enableTelebirrLive ? {} : { TELEBIRR_MOCK: "true" }),
      },
      timeout: cdk.Duration.seconds(30),
      memorySize: 512,
      logRetention: logs.RetentionDays.ONE_YEAR,
      reservedConcurrentExecutions: 15,
      // In live mode, egress to Telebirr via the NAT instance's stable IP.
      ...escrowVpcProps,
    });
    props.escrowsTable.grantReadWriteData(escrowFn.function);
    props.escrowAuditTable.grantWriteData(escrowFn.function);
    grantEscrowLedger(escrowFn.function, ["dynamodb:GetItem", "dynamodb:Query", "dynamodb:PutItem", "dynamodb:UpdateItem"]);
    props.escrowIdempotencyTable.grantReadWriteData(escrowFn.function);
    props.deliveriesTable.grantReadData(escrowFn.function);
    props.ordersTable.grantReadData(escrowFn.function);
    // A dispute mirrors onto the delivery and order (SIM-12/SIM-47), and so does a
    // dispute verdict before it is settled (ESC-03). UpdateItem only: the escrow
    // Lambda never puts or deletes rows in these tables.
    props.deliveriesTable.grant(escrowFn.function, "dynamodb:UpdateItem");
    props.ordersTable.grant(escrowFn.function, "dynamodb:UpdateItem");
    // Transaction audit is append-only: PutItem, never an update or delete.
    props.transactionAuditTable.grant(escrowFn.function, "dynamodb:PutItem");
    props.usersTable.grantReadData(escrowFn.function);
    props.cropsTable.grantReadData(escrowFn.function);
    // ESC-03: a buyer_wins refund before pickup gives the cancelled order's stock back
    // (conditional UpdateItem on the listing, idempotent per order).
    props.cropsTable.grant(escrowFn.function, "dynamodb:UpdateItem");
    // FUP-123: and its quintals back on the trip: read the trip (Query on the base table) and one
    // conditional UpdateItem. No put, delete, scan or index.
    escrowFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query", "dynamodb:UpdateItem"],
        resources: [props.tripsTable.tableArn],
      }),
    );
    props.rateLimitsTable.grantReadWriteData(escrowFn.function);
    props.fraudSignalsTable.grantReadWriteData(escrowFn.function);
    commissionRateParam.grantRead(escrowFn.function);
    disputeWindowParam.grantRead(escrowFn.function);
    escrowTimeoutParam.grantRead(escrowFn.function);
    paginationSecret.grantRead(escrowFn.function);

    // ─── R4.3 Escrow Cron Function (auto-release, auto-refund, reconciliation) ──

    const escrowCronFn = new WeredaLambda(this, "EscrowCronFunction", {
      functionName: `${prefix}-escrow-cron`,
      description:
        "Scheduled escrow operations: auto-release, auto-refund, reconciliation",
      handlerName: "EscrowCronFunction",
      environment: {
        ESCROWS_TABLE: props.escrowsTable.tableName,
        ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
        // ESC-20: the ledger sweep, the split verdicts and the transport leg retry.
        ESCROW_LEDGER_TABLE: props.escrowLedgerTable.tableName,
        ESCROW_IDEMPOTENCY_TABLE: props.escrowIdempotencyTable.tableName,
        DELIVERIES_TABLE: props.deliveriesTable.tableName,
        // Order-aware auto-refund: an ORDER-funded escrow is only refunded when
        // its order is stale-funded or terminal; live orders get the expiry
        // pushed. Without this env, order-sourced escrows are skipped (fail-safe).
        ORDERS_TABLE: props.ordersTable.tableName,
        // A stale-funded order cancelled by the refund sweep gives its listing
        // stock back (SIM-13). Without this env the cancel and refund still run,
        // stock is simply not restored.
        CROPS_TABLE: props.cropsTable.tableName,
        // Payouts go to the party's registered phone and only to verified
        // identities; the sweep reads profiles for that.
        USERS_TABLE: props.usersTable.tableName,
        // ESC-03: the dispute sweep audits the order and delivery hops it repairs.
        TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
        // FUP-123: an order the refund sweep cancels (or a buyer_wins settlement cancels before
        // pickup) gives its trip booking back, only while COMMITMENTS_ENABLED is on.
        TRIPS_TABLE: props.tripsTable.tableName,
        COMMITMENTS_ENABLED: commitmentsEnabled,
        COMMISSION_RATE_PARAM: commissionRateParam.parameterName,
        DISPUTE_WINDOW_PARAM: disputeWindowParam.parameterName,
        ESCROW_TIMEOUT_PARAM: escrowTimeoutParam.parameterName,
        // FRD-04: the settlement gate reads each party's risk tier (fraud-detection-architecture.md
        // 17.2). Without it only suspended parties are stopped, and every run logs RISK_TABLE_UNCONFIGURED.
        FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName,
        // FRD-03: off, a sweep that finds a suspended party (FRD-04: or a party at risk tier CRITICAL)
        // skips that settlement and logs RISK_SETTLEMENT_SKIPPED; on, it writes the escrow status `held`.
        // On per environment once its clients parse `held` (FRD-06 on web and in both stores); apps
        // older than that read `escrowed` on the escrow routes (FUP-61, fraud-detection-architecture.md 18.7).
        FRAUD_HOLD_ENABLED: fraudHoldEnabled,
        // ESC-33: the payout delay stamped by the sweep's stamp repair reads the payee's reputation tier
        // like the sync stamp does (fraud-detection-architecture.md 17.5). Off: FRD-13's classes.
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        ...(envConfig.isProd || enableTelebirrLive ? {} : { TELEBIRR_MOCK: "true" }),
      },
      timeout: cdk.Duration.minutes(5),
      memorySize: 512,
      logRetention: logs.RetentionDays.ONE_YEAR,
      reservedConcurrentExecutions: 2,
      // In live mode, egress to Telebirr via the NAT instance's stable IP.
      ...escrowVpcProps,
    });
    props.escrowsTable.grantReadWriteData(escrowCronFn.function);
    grantEscrowLedger(escrowCronFn.function, ["dynamodb:GetItem", "dynamodb:Query", "dynamodb:PutItem", "dynamodb:UpdateItem"]);
    // Payout destinations and the release-time identity gate read profiles.
    props.usersTable.grantReadData(escrowCronFn.function);
    // FRD-04: the risk score is one Query of a party's fraud-signals partition. The cron writes no signal.
    props.fraudSignalsTable.grantReadData(escrowCronFn.function);
    // FRD-10: after an auto-release of a clean trade, one conditional PutItem per party (the CLEAN_DELIVERY
    // credit row). Append only: no update of a reviewed row, no delete.
    props.fraudSignalsTable.grant(escrowCronFn.function, "dynamodb:PutItem");
    props.escrowAuditTable.grantWriteData(escrowCronFn.function);
    // ESC-10: a sweep that freezes a disputed trade goes through the ESC-07 delivery dispute path, which
    // reads the escrow's audit trail (Query) before it writes a refusal row.
    props.escrowAuditTable.grantReadData(escrowCronFn.function);
    props.escrowIdempotencyTable.grantReadWriteData(escrowCronFn.function);
    props.deliveriesTable.grantReadData(escrowCronFn.function);
    // ESC-03: settling a dispute verdict first moves its delivery to the verdict
    // (UpdateItem only, never a put or delete).
    props.deliveriesTable.grant(escrowCronFn.function, "dynamodb:UpdateItem");
    props.transactionAuditTable.grant(escrowCronFn.function, "dynamodb:PutItem");
    // Read the order behind an order-funded escrow before refunding; write to
    // cancel a stale-funded order after its escrow refunds.
    props.ordersTable.grantReadWriteData(escrowCronFn.function);
    // Restore the cancelled order's listing stock (conditional update on the listing).
    props.cropsTable.grantReadWriteData(escrowCronFn.function);
    // FUP-123: release the cancelled order's trip booking: read the trip (Query on the base table)
    // and one conditional UpdateItem. No put, delete, scan or index.
    escrowCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query", "dynamodb:UpdateItem"],
        resources: [props.tripsTable.tableArn],
      }),
    );
    commissionRateParam.grantRead(escrowCronFn.function);
    disputeWindowParam.grantRead(escrowCronFn.function);
    escrowTimeoutParam.grantRead(escrowCronFn.function);

    // EventBridge: auto-release + auto-refund every hour
    new events.Rule(this, "EscrowCronHourly", {
      ruleName: `${prefix}-escrow-cron-hourly`,
      description: "Trigger escrow auto-release and auto-refund every hour",
      schedule: events.Schedule.rate(cdk.Duration.hours(1)),
      targets: [
        new targets.LambdaFunction(escrowCronFn.function, {
          // The handler reads `action` from APIGatewayProxyRequest.Body, so the payload MUST be
          // wrapped as `body` (a JSON string). Sending `{action}` at the event root left Body null
          // and the cron returned 400 on every trigger — a silent no-op. (Review C3.)
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "auto_release_and_refund" }),
          }),
        }),
      ],
    });

    // ESC-20: the ledger sweep every 15 minutes (escrow-protocol.md 2.2.0, P.6): calls pending rows,
    // checks sent rows with the gateway before anything else, retries failed rows, reports overdue rows.
    new events.Rule(this, "EscrowCronLedger", {
      ruleName: `${prefix}-escrow-cron-ledger`,
      description: "Trigger the escrow ledger sweep every 15 minutes (ESC-20)",
      schedule: events.Schedule.rate(cdk.Duration.minutes(15)),
      targets: [
        new targets.LambdaFunction(escrowCronFn.function, {
          // Payload wrapped as `body` so it deserializes into APIGatewayProxyRequest.Body (Review C3).
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "ledger_sweep" }),
          }),
        }),
      ],
    });

    // EventBridge: reconciliation every 6 hours
    new events.Rule(this, "EscrowCronReconciliation", {
      ruleName: `${prefix}-escrow-cron-reconciliation`,
      description: "Trigger escrow reconciliation with Telebirr every 6 hours",
      schedule: events.Schedule.rate(cdk.Duration.hours(6)),
      targets: [
        new targets.LambdaFunction(escrowCronFn.function, {
          // Payload wrapped as `body` so it deserializes into APIGatewayProxyRequest.Body (Review C3).
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "reconciliation" }),
          }),
        }),
      ],
    });

    // ─── Telebirr Webhook Function (gated; public, RSA-verified) ───────
    // Receives the async C2B notification and flips escrow initiated→escrowed.
    // NOT placed in the VPC: it makes no outbound Telebirr call (only verifies
    // the signature + writes DynamoDB + reads the secret), so it needs no stable
    // egress IP. Kept out of the VPC to avoid coupling it to the NAT instance.
    if (enableTelebirrLive) {
      telebirrWebhookFn = new WeredaLambda(this, "TelebirrWebhookFunction", {
        functionName: `${prefix}-telebirr-webhook`,
        description:
          "POST /webhooks/telebirr — async C2B payment notification (public, RSA-verified)",
        handlerName: "TelebirrWebhookFunction",
        environment: {
          ESCROWS_TABLE: props.escrowsTable.tableName,
          ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
          // ESC-20: a live-funded order's escrow is born on the ledger while the flag is on, and a duplicate
          // payment gets its ledger refund row.
          ESCROW_LEDGER_TABLE: props.escrowLedgerTable.tableName,
          ESCROW_LEDGER_ENABLED: escrowLedgerEnabled,
          ESCROW_IDEMPOTENCY_TABLE: props.escrowIdempotencyTable.tableName,
          // Payer binding: the paying wallet must be the buyer's registered phone.
          // Recorded as an audit signal by default; enforced only once Telebirr's
          // live payload is confirmed to carry the payer field
          // (`-c telebirrRequirePayerMatch=true`).
          USERS_TABLE: props.usersTable.tableName,
          ...(String(this.node.tryGetContext("telebirrRequirePayerMatch")) === "true"
            ? { TELEBIRR_REQUIRE_PAYER_MATCH: "true" }
            : {}),
          // ESC-01: a paid order checkout funds the order here: status-conditional
          // order write, idempotent listing stock decrement, order escrow, and a
          // transaction-audit row. Without these the order path stays unprocessed.
          ORDERS_TABLE: props.ordersTable.tableName,
          CROPS_TABLE: props.cropsTable.tableName,
          TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
          // FRD-08: a verified payer mismatch is a fraud signal on the buyer.
          FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName,
          // ESC-15: funding a trip order reserves its capacity on the trip and stamps who is
          // asked first; the same flag as on sync, so the two funding paths agree.
          TRIPS_TABLE: props.tripsTable.tableName,
          COMMITMENTS_ENABLED: commitmentsEnabled,
        },
        timeout: cdk.Duration.seconds(15),
        memorySize: 512,
        logRetention: logs.RetentionDays.ONE_YEAR,
        reservedConcurrentExecutions: 10,
      });
      props.escrowsTable.grantReadWriteData(telebirrWebhookFn.function);
      props.escrowAuditTable.grantWriteData(telebirrWebhookFn.function);
      // ESC-20 (P.9): a duplicate payment decided for good gets one conditional ledger row (PutItem in a
      // transaction with its audit row), after a Query of the order's escrow-audit trail for a manual refund.
      grantEscrowLedger(telebirrWebhookFn.function, ["dynamodb:PutItem"]);
      props.escrowAuditTable.grant(telebirrWebhookFn.function, "dynamodb:Query");
      props.escrowIdempotencyTable.grantReadWriteData(telebirrWebhookFn.function);
      props.usersTable.grantReadData(telebirrWebhookFn.function);
      props.ordersTable.grantReadWriteData(telebirrWebhookFn.function);
      props.cropsTable.grantReadWriteData(telebirrWebhookFn.function);
      props.transactionAuditTable.grantWriteData(telebirrWebhookFn.function);
      // ESC-15: read the trip and reserve the funded order's capacity on it (a conditional
      // UpdateItem). The only public route gets the minimum: no put, delete, scan or index.
      telebirrWebhookFn.function.addToRolePolicy(
        new iam.PolicyStatement({
          actions: ["dynamodb:Query", "dynamodb:GetItem", "dynamodb:UpdateItem"],
          resources: [props.tripsTable.tableArn],
        }),
      );
      // FRD-08: append only (the public route reads no score and changes no review).
      props.fraudSignalsTable.grant(telebirrWebhookFn.function, "dynamodb:PutItem");
      // ESC-47: a redelivery reads the order's trail (Query on the base table, no index) before
      // it writes a missing payment_verified row. Without it the read is denied and nothing is written.
      telebirrWebhookFn.function.addToRolePolicy(
        new iam.PolicyStatement({
          actions: ["dynamodb:Query"],
          resources: [props.transactionAuditTable.tableArn],
        }),
      );

      // ESC-45: replay inbox rows left pending (a failure or a cut-off run after the
      // row was written). The same Lambda, invoked directly with the replay action;
      // API Gateway always sets requestContext, so the public route never reaches it.
      // The idempotency grant above covers the InboxReplayIndex query.
      new events.Rule(this, "TelebirrInboxReplay", {
        ruleName: `${prefix}-telebirr-inbox-replay`,
        description: "Replay Telebirr webhook inbox rows left pending (ESC-45)",
        schedule: events.Schedule.rate(cdk.Duration.minutes(5)),
        targets: [
          new targets.LambdaFunction(telebirrWebhookFn.function, {
            // Wrapped as `body` so it lands in APIGatewayProxyRequest.Body (Review C3).
            event: events.RuleTargetInput.fromObject({
              body: JSON.stringify({ action: "webhook_inbox_replay" }),
            }),
          }),
        ],
      });
    }

    // ─── Order Function ──────────────────────────────────────────────

    const orderFn = new WeredaLambda(this, "OrderFunction", {
      functionName: `${prefix}-orders`,
      description: "GET /orders — browse orders (read-only)",
      handlerName: "OrderFunction",
      environment: {
        ORDERS_TABLE: props.ordersTable.tableName,
        // ESC-48: the parties' phone numbers of an order whose contact is open, read at request time.
        USERS_TABLE: props.usersTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    props.ordersTable.grantReadData(orderFn.function);
    props.usersTable.grantReadData(orderFn.function);
    paginationSecret.grantRead(orderFn.function);

    // ─── Rating Function ─────────────────────────────────────────────

    const ratingFn = new WeredaLambda(this, "RatingFunction", {
      functionName: `${prefix}-ratings`,
      description: "GET /ratings — read ratings + aggregate (read-only)",
      handlerName: "RatingFunction",
      environment: {
        RATINGS_TABLE: props.ratingsTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
        // ESC-29 (rating-api.md 1.2.0 read rules): the ratee's row gives their role, the stored
        // rating sum and count and the reputation; the events are read only past nextChangeAt.
        USERS_TABLE: props.usersTable.tableName,
        REPUTATION_EVENTS_TABLE: props.reputationEventsTable.tableName,
        REPUTATION_POLICY_PARAM: reputationPolicyParam.parameterName,
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        REPUTATION_PUBLIC_TAGS_ENABLED: reputationPublicTags,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    props.ratingsTable.grantReadData(ratingFn.function);
    paginationSecret.grantRead(ratingFn.function);
    // ESC-29: one GetItem on the ratee's row and, past nextChangeAt, one Query of their events.
    props.usersTable.grant(ratingFn.function, "dynamodb:GetItem");
    props.reputationEventsTable.grant(ratingFn.function, "dynamodb:Query");
    reputationPolicyParam.grantRead(ratingFn.function);

    // ─── Reputation Function (ESC-29, reputation-api.md sections 2 to 6) ─────────
    // The party's own reputation and events, the public summary for buyers, the appeal photo URL
    // and the appeal. Reads only, except the appeal: one conditional UpdateItem on the caller's
    // own event row, one transaction-audit row and a pre-signed PUT under uploads/appeals/.
    const reputationFn = new WeredaLambda(this, "ReputationFunction", {
      functionName: `${prefix}-reputation`,
      description:
        "GET /reputation/me, /reputation/me/events, /reputation/{userId}; POST appeal photo URL and appeal (ESC-29)",
      handlerName: "ReputationFunction",
      environment: {
        REPUTATION_EVENTS_TABLE: props.reputationEventsTable.tableName,
        USERS_TABLE: props.usersTable.tableName,
        TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
        UPLOADS_BUCKET: props.uploadsBucket.bucketName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
        REPUTATION_POLICY_PARAM: reputationPolicyParam.parameterName,
        // Off: the two POSTs answer 503 REPUTATION_UNAVAILABLE; the reads show the stored state.
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        // Off: the public summary carries only the raw rating average and count.
        REPUTATION_PUBLIC_TAGS_ENABLED: reputationPublicTags,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 5,
    });
    props.usersTable.grant(reputationFn.function, "dynamodb:GetItem");
    props.reputationEventsTable.grant(
      reputationFn.function,
      "dynamodb:Query",
      "dynamodb:GetItem",
      "dynamodb:UpdateItem",
    );
    props.transactionAuditTable.grantWriteData(reputationFn.function);
    // The pre-signed PUT carries the signer's rights, and the appeal checks its photo exists
    // (HeadObject): both only under the appeal prefix.
    props.uploadsBucket.grantPut(reputationFn.function, "uploads/appeals/*");
    props.uploadsBucket.grantRead(reputationFn.function, "uploads/appeals/*");
    paginationSecret.grantRead(reputationFn.function);
    reputationPolicyParam.grantRead(reputationFn.function);

    // ─── Message Function ────────────────────────────────────────────

    const messageFn = new WeredaLambda(this, "MessageFunction", {
      functionName: `${prefix}-messages`,
      description: "GET /messages — read chat threads + sync-down pull (read-only)",
      handlerName: "MessageFunction",
      environment: {
        MESSAGES_TABLE: props.messagesTable.tableName,
        // FUP-124: a trucker reads an order or delivery thread only while committed to the order.
        ORDERS_TABLE: props.ordersTable.tableName,
        DELIVERIES_TABLE: props.deliveriesTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    props.messagesTable.grantReadData(messageFn.function);
    paginationSecret.grantRead(messageFn.function);
    // FUP-124: the order and delivery reads by id are a key Query each; nothing else on those tables.
    props.ordersTable.grant(messageFn.function, "dynamodb:Query");
    props.deliveriesTable.grant(messageFn.function, "dynamodb:Query");

    // ─── Notification Function (ESC-21 inbox read) ───────────────────

    const notificationFn = new WeredaLambda(this, "NotificationFunction", {
      functionName: `${prefix}-notifications`,
      description: "GET /notifications: the caller's order notification inbox (read-only, ESC-21)",
      handlerName: "NotificationFunction",
      environment: {
        NOTIFICATIONS_TABLE: props.notificationsTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    props.notificationsTable.grantReadData(notificationFn.function);
    paginationSecret.grantRead(notificationFn.function);

    // ─── Order Cron Function (auto-cancel expired unpaid orders, commitment deadlines, relationship patterns, reputation) ────

    const orderCronFn = new WeredaLambda(this, "OrderCronFunction", {
      functionName: `${prefix}-order-cron`,
      description:
        "Scheduled order jobs: auto-cancel expired unpaid orders, settle commitment deadlines, flag relationship patterns, run the daily reputation job, replay parked reputation records",
      handlerName: "OrderCronFunction",
      environment: {
        ORDERS_TABLE: props.ordersTable.tableName,
        TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
        // ESC-45: the payment-failed step skips orders whose verified Telebirr
        // payment is still pending in the webhook inbox.
        ESCROW_IDEMPOTENCY_TABLE: props.escrowIdempotencyTable.tableName,
        // ESC-15: the commitment_deadlines walk. A farmer or buyer who never answered is
        // cancelled (refund scheduled on the escrow, stock back on the listing, capacity back
        // on the trip); a trucker who never answered or a cancelled trip fails the transport
        // (the pending delivery of an accepted order is cancelled). Without the escrows,
        // escrow-audit, crops and trips tables the walk logs ORDER_CRON_COMMITMENTS_UNCONFIGURED
        // and settles nothing; USERS_TABLE only feeds the escrow service's profile reads.
        ESCROWS_TABLE: props.escrowsTable.tableName,
        ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
        CROPS_TABLE: props.cropsTable.tableName,
        TRIPS_TABLE: props.tripsTable.tableName,
        DELIVERIES_TABLE: props.deliveriesTable.tableName,
        USERS_TABLE: props.usersTable.tableName,
        // ESC-16: since the deadline index, commitment_deadlines reads only the orders that are
        // due (DeadlineIndex, Query) every 15 minutes; deadline_repair walks the funded,
        // awaiting_transport and accepted partitions daily to stamp rows without a deadline.
        COMMITMENTS_ENABLED: commitmentsEnabled,
        // ESC-34: commitment_deadlines judges a pickup by its arrival check-ins (no-show outcomes, the
        // admin queue); off, such an order gets the P1 marker and its stamp is cleared.
        PICKUP_CLASSIFICATION_ENABLED: pickupClassification,
        // FRD-15: the relationship_patterns scan writes SELF_TRADE, SHARED_DEVICE_TRADE and
        // REPEATED_PAIR signals (fraud-detection-architecture.md 15.2). Without it, or without
        // USERS_TABLE, the action logs FRAUD_RELATIONSHIP_UNCONFIGURED and writes nothing.
        FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName,
        // ESC-28 (reputation-architecture.md sections 8 and 7.5): reputation_daily expires points,
        // ends restrictions and overrides and retries copies; reputation_unpark replays the parked
        // stream records; reputation_backfill_ratings stamps the rating aggregates (ratings table)
        // once per environment; reputation_recompute_all applies a policy change. Device tokens:
        // the reachability read of a replayed RESPONSE_EXPIRED.
        REPUTATION_EVENTS_TABLE: props.reputationEventsTable.tableName,
        REPUTATION_PARKED_QUEUE_URL: reputationParkedQueue.queueUrl,
        REPUTATION_POLICY_PARAM: reputationPolicyParam.parameterName,
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        REPUTATION_TIMEOUT_PENALTIES_ENABLED: timeoutPenalties,
        RATINGS_TABLE: props.ratingsTable.tableName,
        DEVICE_TOKENS_TABLE: props.deviceTokensTable.tableName,
      },
      timeout: cdk.Duration.minutes(5),
      memorySize: 256,
      // Six rules invoke it: auto-cancel and relationship patterns hourly, commitment_deadlines every
      // 15 minutes (ESC-16), deadline_repair daily, and ESC-28's hourly reputation_unpark and daily
      // reputation_daily, so a run of each fits at once.
      reservedConcurrentExecutions: 6,
    });
    // ESC-16: the read grant covers the table's indexes, so the sweep's Query on DeadlineIndex too.
    props.ordersTable.grantReadWriteData(orderCronFn.function);
    // Auto-cancel writes an order lifecycle event to the audit trail.
    props.transactionAuditTable.grantWriteData(orderCronFn.function);
    // ESC-15: the commitment walk's side effects, in the escrow-cron grant style. The escrow is
    // read through its OrderIndex and expired (refund scheduled), every escrow mutation is
    // ledgered, the listing and the trip take conditional updates, the pending delivery is
    // cancelled, and profiles are read by the escrow service.
    props.escrowsTable.grantReadWriteData(orderCronFn.function);
    props.escrowAuditTable.grantWriteData(orderCronFn.function);
    props.cropsTable.grantReadWriteData(orderCronFn.function);
    props.tripsTable.grantReadWriteData(orderCronFn.function);
    props.deliveriesTable.grantReadWriteData(orderCronFn.function);
    props.usersTable.grantReadData(orderCronFn.function);
    // ESC-45: Query on the inbox replay index only, nothing else in that table.
    orderCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [
          `${props.escrowIdempotencyTable.tableArn}/index/InboxReplayIndex`,
        ],
      }),
    );
    // FRD-15: the relationship scan reads orders on StatusCreatedAtIndex and BuyerIdCreatedAtIndex
    // and users rows by key (both covered above). On fraud-signals it needs only the base-table
    // Query of its existence check and the signal writer's conditional PutItem: no index, no update.
    orderCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query", "dynamodb:PutItem"],
        resources: [props.fraudSignalsTable.tableArn],
      }),
    );
    // ESC-28: the reputation actions. On reputation-events: the user's partition (base-table Query),
    // the day partitions of DueIndex (daily job, recompute_all) and the admin queue's
    // AppealStatusIndex (the overdue review and appeal lines); the expiry, end and tier writes and the
    // delete of a processed heal marker. On users: the reputation map and the backfilled rating
    // aggregates (UpdateItem; the read grant above covers RoleWaredaIndex for the backfill). On
    // ratings: the ratee's ratings (RateeIdCreatedAtIndex) and their inAggregate flag. Device tokens:
    // the reachability Query of a replayed record (fraud signals already has its Query above).
    orderCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [
          props.reputationEventsTable.tableArn,
          `${props.reputationEventsTable.tableArn}/index/DueIndex`,
          `${props.reputationEventsTable.tableArn}/index/AppealStatusIndex`,
        ],
      }),
    );
    orderCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:GetItem", "dynamodb:PutItem", "dynamodb:UpdateItem", "dynamodb:DeleteItem"],
        resources: [props.reputationEventsTable.tableArn],
      }),
    );
    orderCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:UpdateItem"],
        resources: [props.usersTable.tableArn],
      }),
    );
    orderCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [`${props.ratingsTable.tableArn}/index/RateeIdCreatedAtIndex`],
      }),
    );
    orderCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:UpdateItem"],
        resources: [props.ratingsTable.tableArn],
      }),
    );
    orderCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [props.deviceTokensTable.tableArn],
      }),
    );
    // reputation_unpark receives up to 100 parked records a run and deletes each replayed one.
    reputationParkedQueue.grantConsumeMessages(orderCronFn.function);
    reputationPolicyParam.grantRead(orderCronFn.function);

    // EventBridge: auto-cancel expired orders every hour
    new events.Rule(this, "OrderCronHourly", {
      ruleName: `${prefix}-order-cron-hourly`,
      description: "Auto-cancel expired unpaid orders every hour",
      schedule: events.Schedule.rate(cdk.Duration.hours(1)),
      targets: [
        new targets.LambdaFunction(orderCronFn.function, {
          // Payload wrapped as `body` so it deserializes into APIGatewayProxyRequest.Body — same
          // silent-no-op bug as the escrow cron (Review C3).
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "auto_cancel_expired" }),
          }),
        }),
      ],
    });

    // EventBridge: ESC-15, settle the farmer, trucker and buyer response windows. ESC-16 (spec
    // 4.5): every 15 minutes, over the orders the DeadlineIndex says are due (a Query, so a quiet
    // run reads almost nothing). A no-op while COMMITMENTS_ENABLED is false (the handler logs and
    // returns).
    new events.Rule(this, "OrderCronCommitments", {
      ruleName: `${prefix}-order-cron-commitments`,
      description: "Settle due order commitment deadlines every 15 minutes (ESC-15, ESC-16)",
      schedule: events.Schedule.rate(cdk.Duration.minutes(15)),
      targets: [
        new targets.LambdaFunction(orderCronFn.function, {
          // Wrapped as `body` so it lands in APIGatewayProxyRequest.Body (Review C3).
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "commitment_deadlines" }),
          }),
        }),
      ],
    });

    // EventBridge: ESC-16, stamp the next deadline on paid orders that lack it or carry a wrong
    // one (orders in flight before the index, or a deadline write that failed). It walks the
    // funded, awaiting_transport and accepted partitions of StatusCreatedAtIndex (Query, no Scan)
    // and decides nothing. Run it by hand once right after the deploy that adds the index.
    new events.Rule(this, "OrderCronDeadlineRepair", {
      ruleName: `${prefix}-order-cron-deadline-repair`,
      description: "Stamp missing or wrong order deadlines once a day (ESC-16)",
      schedule: events.Schedule.rate(cdk.Duration.days(1)),
      targets: [
        new targets.LambdaFunction(orderCronFn.function, {
          // Wrapped as `body` so it lands in APIGatewayProxyRequest.Body (Review C3).
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "deadline_repair" }),
          }),
        }),
      ],
    });

    // EventBridge: FRD-15, flag self-trading, shared devices and repeated buyer and farmer pairs
    // on the orders funded in the last 48 hours (fraud-detection-architecture.md 15.2).
    new events.Rule(this, "OrderCronRelationshipPatterns", {
      ruleName: `${prefix}-order-cron-relationship-patterns`,
      description: "Flag fraud relationship patterns on recently funded orders every hour (FRD-15)",
      schedule: events.Schedule.rate(cdk.Duration.hours(1)),
      targets: [
        new targets.LambdaFunction(orderCronFn.function, {
          // Wrapped as `body` so it lands in APIGatewayProxyRequest.Body (Review C3).
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "relationship_patterns" }),
          }),
        }),
      ],
    });

    // EventBridge: ESC-28, the daily reputation job at 00:00 UTC (03:00 in Ethiopia): expire points
    // and completions past their 90 days, end restrictions and overrides, retry the copies a heal
    // marker asks for, log the overdue reviews and appeals (reputation-architecture.md section 8).
    // A no-op while REPUTATION_PENALTIES_ENABLED is false (the handler logs and returns). The other
    // reputation actions are invoked by hand, with no rule: reputation_backfill_ratings (once per
    // environment, section 12.2), reputation_recompute_all (after a policy change, section 8.3) and
    // reputation_stamp_owner_copies (FUP-160, once per environment before reputationPublicTags, section
    // 12.3; it reads the crop and trip status indexes and updates rows under the grants above).
    new events.Rule(this, "OrderCronReputationDaily", {
      ruleName: `${prefix}-order-cron-reputation-daily`,
      description: "Expire reputation points, end restrictions and retry copies once a day at 03:00 Ethiopia time (ESC-28)",
      schedule: events.Schedule.expression("cron(0 0 * * ? *)"),
      targets: [
        new targets.LambdaFunction(orderCronFn.function, {
          // Wrapped as `body` so it lands in APIGatewayProxyRequest.Body (Review C3).
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "reputation_daily" }),
          }),
        }),
      ],
    });

    // EventBridge: ESC-28, replay the parked reputation stream records every hour (section 7.5).
    new events.Rule(this, "OrderCronReputationUnpark", {
      ruleName: `${prefix}-order-cron-reputation-unpark`,
      description: "Replay parked reputation stream records every hour (ESC-28)",
      schedule: events.Schedule.rate(cdk.Duration.hours(1)),
      targets: [
        new targets.LambdaFunction(orderCronFn.function, {
          // Wrapped as `body` so it lands in APIGatewayProxyRequest.Body (Review C3).
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "reputation_unpark" }),
          }),
        }),
      ],
    });

    // ─── FRD-19: AI advisory reviewer (fraud-detection-architecture.md section 21) ─────
    // Two Lambdas, split so the code that talks to the model holds no data access and the code that holds data
    // access cannot move money (specs/decisions/frd-19-ai-advisory-reviewer.md):
    // - fraud-ai-reviewer (Node.js 24, non-AOT): the only caller of Bedrock. Its role may only invoke the one
    //   configured model (bedrock:InvokeModel); no table, no queue, no other Lambda.
    // - fraud-advisory (the AOT bootstrap, hourly): picks the users the rules have just put at HIGH or CRITICAL,
    //   sends each redacted timeline to the reviewer and writes one capped AI_ADVISORY signal row. Its role reads
    //   fraud signals (table and status index), users rows and the transaction audit, puts fraud-signal rows and
    //   invokes the reviewer: no grant on orders, escrows or any money table.
    // Kill switch: aiReviewEnabled (environments.ts), off unless set (on in dev only), on both Lambdas.
    const aiReviewEnabled = envConfig.aiReviewEnabled ? "true" : "false";
    const aiReviewModelId = envConfig.aiReviewModelId ?? "eu.anthropic.claude-opus-5-5";
    const aiReviewBedrockRegion = envConfig.aiReviewBedrockRegion ?? this.region;
    // FUP-145: the per-run review cap, only when the environment names one (the server defaults to 5 and
    // ignores a value outside 1 to 20, so a bad value fails here instead of silently meaning 5).
    const aiReviewMaxPerRun = envConfig.aiReviewMaxPerRun;
    if (
      aiReviewMaxPerRun !== undefined &&
      (!Number.isInteger(aiReviewMaxPerRun) || aiReviewMaxPerRun < 1 || aiReviewMaxPerRun > 20)
    ) {
      throw new Error(`aiReviewMaxPerRun must be a whole number from 1 to 20, got ${aiReviewMaxPerRun}`);
    }
    const aiReviewerName = `${prefix}-fraud-ai-reviewer`;

    const aiReviewerLogGroup = new logs.LogGroup(this, "FraudAiReviewerLogGroup", {
      logGroupName: `/aws/lambda/${aiReviewerName}`,
      // One line per review (outcome, model, token counts; never the timeline or the advice).
      retention: logs.RetentionDays.ONE_MONTH,
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });
    const aiReviewerFn = new lambda.Function(this, "FraudAiReviewerFunction", {
      functionName: aiReviewerName,
      description: "FRD-19: asks a Claude model on Bedrock for advice on one redacted fraud timeline",
      runtime: lambda.Runtime.NODEJS_24_X,
      architecture: lambda.Architecture.ARM_64,
      handler: "index.handler",
      // No dependencies: the AWS SDK v3 ships with the runtime, so the folder is the bundle.
      code: lambda.Code.fromAsset(path.join(__dirname, "..", "..", "..", "server", "fraud-ai-reviewer"), {
        exclude: ["test", "README.md", "package.json", "node_modules"],
      }),
      memorySize: 256,
      timeout: cdk.Duration.seconds(30),
      // The cron invokes it one user at a time. Skipped in dev like WeredaLambda (account limit 10).
      ...(envConfig.name === "dev" ? {} : { reservedConcurrentExecutions: 2 }),
      logGroup: aiReviewerLogGroup,
      environment: {
        AI_REVIEW_ENABLED: aiReviewEnabled,
        BEDROCK_MODEL_ID: aiReviewModelId,
        BEDROCK_REGION: aiReviewBedrockRegion,
      },
    });
    // bedrock:InvokeModel on the configured model only. A geographic or global inference profile (eu., us.,
    // global. ...) needs the profile in the calling Region and the foundation model in every destination
    // Region, the latter only through that profile; a bare model id needs the foundation model in the Region.
    const aiProfile = /^(global|eu|us|apac|jp|au|ca)\.(.+)$/.exec(aiReviewModelId);
    const foundationModelArn = (region: string, modelId: string) =>
      `arn:${this.partition}:bedrock:${region}::foundation-model/${modelId}`;
    if (aiProfile) {
      const profileArn = `arn:${this.partition}:bedrock:${aiReviewBedrockRegion}:${this.account}:inference-profile/${aiReviewModelId}`;
      aiReviewerFn.addToRolePolicy(
        new iam.PolicyStatement({
          // GetInferenceProfile: required to run inference through a profile (Bedrock inference prerequisites).
          actions: ["bedrock:InvokeModel", "bedrock:GetInferenceProfile"],
          resources: [profileArn],
        }),
      );
      aiReviewerFn.addToRolePolicy(
        new iam.PolicyStatement({
          actions: ["bedrock:InvokeModel"],
          // A global profile's destinations are named without a Region.
          resources: [foundationModelArn("*", aiProfile[2]), foundationModelArn("", aiProfile[2])],
          conditions: { StringEquals: { "bedrock:InferenceProfileArn": profileArn } },
        }),
      );
    } else {
      aiReviewerFn.addToRolePolicy(
        new iam.PolicyStatement({
          actions: ["bedrock:InvokeModel"],
          resources: [foundationModelArn(aiReviewBedrockRegion, aiReviewModelId)],
        }),
      );
    }

    const fraudAdvisoryFn = new WeredaLambda(this, "FraudAdvisoryCronFunction", {
      functionName: `${prefix}-fraud-advisory`,
      description:
        "FRD-19: hourly AI advisory review of the users the fraud rules put at HIGH or CRITICAL (advice only, never money)",
      handlerName: "FraudAdvisoryCronFunction",
      environment: {
        FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName,
        USERS_TABLE: props.usersTable.tableName,
        TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
        FRAUD_AI_REVIEW_ENABLED: aiReviewEnabled,
        FRAUD_AI_REVIEWER_FUNCTION: aiReviewerFn.functionName,
        ...(aiReviewMaxPerRun !== undefined ? { FRAUD_AI_REVIEW_MAX_PER_RUN: String(aiReviewMaxPerRun) } : {}),
      },
      // Reviews start in the first two minutes; one more reviewer call (40 s at most) and the summary fit after.
      timeout: cdk.Duration.minutes(3),
      memorySize: 256,
      // One run at a time: a second concurrent run would only meet the first one's day keys.
      reservedConcurrentExecutions: 1,
      logRetention: logs.RetentionDays.ONE_MONTH,
    });
    fraudAdvisoryFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        // The score and the timeline Query the user's partition, the candidates the status index; the day key is
        // checked with GetItem and the advisory row put once (conditional). No update, no delete.
        actions: ["dynamodb:Query", "dynamodb:GetItem", "dynamodb:PutItem"],
        resources: [props.fraudSignalsTable.tableArn],
      }),
    );
    fraudAdvisoryFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [`${props.fraudSignalsTable.tableArn}/index/StatusCreatedAtIndex`],
      }),
    );
    fraudAdvisoryFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        // Projected to role, createdAt and faydaVerified: never the phone, name or Fayda id.
        actions: ["dynamodb:GetItem"],
        resources: [props.usersTable.tableArn],
      }),
    );
    fraudAdvisoryFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [
          props.transactionAuditTable.tableArn,
          `${props.transactionAuditTable.tableArn}/index/EntityIndex`,
        ],
      }),
    );
    aiReviewerFn.grantInvoke(fraudAdvisoryFn.function);
    // EventBridge invokes the cron asynchronously: no Lambda retry, so a run that timed out after a review
    // never calls Bedrock again for it. The next hourly run reads the lookback anyway.
    fraudAdvisoryFn.function.configureAsyncInvoke({ retryAttempts: 0 });

    new events.Rule(this, "FraudAdvisoryHourly", {
      ruleName: `${prefix}-fraud-advisory-hourly`,
      description: "FRD-19: AI advisory review of new HIGH and CRITICAL fraud users every hour (disabled while off)",
      // Off, the rule does not run at all; the cron's own switch still guards a run invoked by hand.
      enabled: envConfig.aiReviewEnabled === true,
      schedule: events.Schedule.rate(cdk.Duration.hours(1)),
      targets: [
        new targets.LambdaFunction(fraudAdvisoryFn.function, {
          // Wrapped as `body` so it lands in APIGatewayProxyRequest.Body (Review C3); the handler does not read it.
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "ai_review" }),
          }),
        }),
      ],
    });

    // ─── Account Deletion (ACC-02, account-deletion.md) ─────────────
    // One handler, two Lambdas: DELETE /user/account (30 s, behind API Gateway's own 30 s) and
    // the completion sweep (5 minutes, one at a time), which finishes or aborts every deletion
    // started more than an hour ago. Grants are per action: each table gets only the calls the
    // deletion makes on it (reads through the party indexes, conditional updates, and deletes of
    // messages, intents, match events, device tokens, the user's notification inbox rows and the
    // user's reputation events).
    const accountDeletionEnv: Record<string, string> = {
      USERS_TABLE: props.usersTable.tableName,
      CROPS_TABLE: props.cropsTable.tableName,
      TRIPS_TABLE: props.tripsTable.tableName,
      ORDERS_TABLE: props.ordersTable.tableName,
      DELIVERIES_TABLE: props.deliveriesTable.tableName,
      ESCROWS_TABLE: props.escrowsTable.tableName,
      ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
      ESCROW_IDEMPOTENCY_TABLE: props.escrowIdempotencyTable.tableName,
      TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
      RATINGS_TABLE: props.ratingsTable.tableName,
      MESSAGES_TABLE: props.messagesTable.tableName,
      INTENTS_TABLE: props.intentsTable.tableName,
      MATCH_EVENTS_TABLE: props.matchEventsTable.tableName,
      DEVICE_TOKENS_TABLE: props.deviceTokensTable.tableName,
      NOTIFICATIONS_TABLE: props.notificationsTable.tableName,
      REPUTATION_EVENTS_TABLE: props.reputationEventsTable.tableName,
      // ESC-29: the user's appeal photos (uploads/appeals/{userId}/) are deleted with the events,
      // only while reputationPenalties is on: off, no appeal photo can exist and the deletion
      // makes no S3 call, so a live erasure can never stick on this step.
      UPLOADS_BUCKET: props.uploadsBucket.bucketName,
      REPUTATION_PENALTIES_ENABLED: reputationPenalties,
      COGNITO_USER_POOL_ID: this.userPool.userPoolId,
      COGNITO_CLIENT_ID: this.userPoolClient.userPoolClientId,
    };

    const accountDeletionFn = new WeredaLambda(this, "AccountDeletionFunction", {
      functionName: `${prefix}-account-deletion`,
      description: "DELETE /user/account: delete the caller's account (ACC-02)",
      handlerName: "AccountDeletionFunction",
      environment: accountDeletionEnv,
      timeout: cdk.Duration.seconds(30),
      memorySize: 512,
      reservedConcurrentExecutions: 5,
    });

    const accountDeletionSweepFn = new WeredaLambda(this, "AccountDeletionSweep", {
      functionName: `${prefix}-account-deletion-sweep`,
      description: "Finish or abort started account deletions within 24 hours (ACC-02)",
      handlerName: "AccountDeletionFunction",
      environment: accountDeletionEnv,
      timeout: cdk.Duration.minutes(5),
      memorySize: 512,
      reservedConcurrentExecutions: 1,
    });

    for (const fn of [accountDeletionFn.function, accountDeletionSweepFn.function]) {
      // ESC-29: list and delete every version under the appeal prefix, nothing else in the bucket.
      fn.addToRolePolicy(
        new iam.PolicyStatement({
          actions: ["s3:ListBucketVersions"],
          resources: [props.uploadsBucket.bucketArn],
          conditions: { StringLike: { "s3:prefix": ["uploads/appeals/*"] } },
        }),
      );
      fn.addToRolePolicy(
        new iam.PolicyStatement({
          actions: ["s3:DeleteObject", "s3:DeleteObjectVersion"],
          resources: [props.uploadsBucket.arnForObjects("uploads/appeals/*")],
        }),
      );
      props.usersTable.grant(fn, "dynamodb:GetItem", "dynamodb:UpdateItem");
      props.cropsTable.grant(fn, "dynamodb:Query", "dynamodb:GetItem", "dynamodb:UpdateItem");
      props.tripsTable.grant(fn, "dynamodb:Query", "dynamodb:GetItem", "dynamodb:UpdateItem");
      props.ordersTable.grant(fn, "dynamodb:Query", "dynamodb:GetItem", "dynamodb:UpdateItem");
      props.deliveriesTable.grant(fn, "dynamodb:Query", "dynamodb:UpdateItem");
      // Escrows keep their money fields: the only write is the copied name (UpdateItem).
      props.escrowsTable.grant(fn, "dynamodb:Query", "dynamodb:UpdateItem");
      // The payment_review blocker reads the order's payment trail; escrow-audit is never written.
      props.escrowAuditTable.grant(fn, "dynamodb:Query");
      // The checkout slot behind the payment_failed and payment_review rules (GetItem only).
      props.escrowIdempotencyTable.grant(fn, "dynamodb:GetItem");
      // Append-only: the started, deleted and aborted rows and one order_cancelled per order.
      props.transactionAuditTable.grant(fn, "dynamodb:PutItem");
      props.ratingsTable.grant(fn, "dynamodb:Query", "dynamodb:GetItem", "dynamodb:UpdateItem");
      props.messagesTable.grant(fn, "dynamodb:Query", "dynamodb:DeleteItem");
      props.intentsTable.grant(fn, "dynamodb:Query", "dynamodb:DeleteItem");
      props.matchEventsTable.grant(fn, "dynamodb:Query", "dynamodb:DeleteItem");
      props.deviceTokensTable.grant(fn, "dynamodb:Query", "dynamodb:DeleteItem");
      // FUP-120: the user's own inbox rows are deleted; in the other parties' inboxes the user's
      // copied name in a row about a shared order becomes the sentinel (conditional UpdateItem).
      props.notificationsTable.grant(fn, "dynamodb:Query", "dynamodb:UpdateItem", "dynamodb:DeleteItem");
      // ESC-28: the user's reputation partition is deleted (account-deletion.md, per-table row).
      // Base table only: the Query and the deletes are by the user's own key, never an index.
      fn.addToRolePolicy(
        new iam.PolicyStatement({
          actions: ["dynamodb:Query", "dynamodb:DeleteItem"],
          resources: [props.reputationEventsTable.tableArn],
        }),
      );
      // Step 6 revokes every refresh token, step 12 deletes the user (username = sub).
      this.grantCognito(fn, [
        "cognito-idp:AdminUserGlobalSignOut",
        "cognito-idp:AdminDeleteUser",
      ]);
      fn.addToRolePolicy(
        new iam.PolicyStatement({
          actions: ["sns:DeleteEndpoint"],
          resources: [fcmEndpointArnPattern],
        }),
      );
    }
    // The sweep alone scans the users table (filtered, twice a day) and reads the account's
    // audit trail for the reapply action after a backup restore.
    props.usersTable.grant(accountDeletionSweepFn.function, "dynamodb:Scan");
    accountDeletionSweepFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [props.transactionAuditTable.tableArn],
      }),
    );

    // Every 6 hours: a deletion is due one hour after its request, so its first sweep comes at
    // most 7 hours in and three runs try it within the contract's 24 hours.
    new events.Rule(this, "AccountDeletionSweepSchedule", {
      ruleName: `${prefix}-account-deletion-sweep`,
      description: "Finish or abort started account deletions every 6 hours (ACC-02)",
      schedule: events.Schedule.rate(cdk.Duration.hours(6)),
      targets: [
        new targets.LambdaFunction(accountDeletionSweepFn.function, {
          // Wrapped as `body`, like the escrow and order crons (Review C3).
          event: events.RuleTargetInput.fromObject({
            body: JSON.stringify({ action: "sweep" }),
          }),
        }),
      ],
    });

    // ─── HTTP API Gateway ────────────────────────────────────────────

    // CORS origins: see `corsOrigins` at the top of the constructor.

    const httpApi = new apigateway.HttpApi(this, "HttpApi", {
      apiName: `${prefix}-api`,
      description: "WeredaConnect HTTP API — optimized for Ethiopian context",
      corsPreflight: {
        allowOrigins: corsOrigins,
        // X-Device-Id / X-Device-Name ride on login, register and verify-otp
        // (server DeviceIdentity). Without them the browser preflight fails and
        // the web app cannot sign up or sign in. X-App-Build rides on every request
        // (FUP-61, server ClientAppBuild), so it must be here before a web build sends it.
        allowHeaders: [
          "Content-Type",
          "Authorization",
          "X-Request-Id",
          "X-Device-Id",
          "X-Device-Name",
          "X-App-Build",
        ],
        allowMethods: [
          CorsHttpMethod.GET,
          CorsHttpMethod.POST,
          CorsHttpMethod.PUT,
          CorsHttpMethod.PATCH,
          CorsHttpMethod.DELETE,
          CorsHttpMethod.OPTIONS,
        ],
        maxAge: cdk.Duration.hours(1),
        allowCredentials: true,
      },
    });

    this.httpApiUrl = httpApi.apiEndpoint;
    this.httpApiId = httpApi.httpApiId;

    // ─── JWT Authorizer (mobile) ─────────────────────────────────────
    const jwtAuthorizer = new HttpUserPoolAuthorizer(
      "CognitoAuthorizer",
      this.userPool,
      {
        userPoolClients: [this.userPoolClient],
      },
    );

    // ─── JWT Authorizer (admin) ───────────────────────────────────────
    const adminJwtAuthorizer = new HttpUserPoolAuthorizer(
      "AdminCognitoAuthorizer",
      this.adminUserPool,
      {
        userPoolClients: [adminClient],
      },
    );

    // ─── Integrations: one per Lambda (FUP-217) ──────────────────────
    // CloudFormation refuses a stack of more than 500 resources, and an integration plus an invoke
    // permission per route had the live-on API stack at 488 to 496. Every route of a Lambda now shares
    // that Lambda's one integration, and the Lambda has one API Gateway invoke permission instead of
    // one per route (scopePermissionToRoute false), so a route on an existing Lambda costs one
    // resource (stack.test.ts fails above 470). The permission's source ARN is `{this API's id}/*/*/*`:
    // any stage, method and path of this API, no other API; the routes, all defined here, pick the
    // Lambda. CDK creates each shared integration and its permission under the first route that binds
    // it, so their logical ids follow that route: today they keep the ids they had as that route's
    // own. A new route of the same Lambda added before it would move them (a replacement, still deploy
    // safe: CloudFormation creates the new ones before cleanup deletes the old). Payload 1.0 everywhere (the handlers read the 1.0 event) except /health (2.0). The
    // admin integration is with the admin routes.
    const lambdaIntegration = (
      id: string,
      fn: WeredaLambda,
      payloadFormatVersion = apigateway.PayloadFormatVersion.VERSION_1_0,
    ) =>
      new integrations.HttpLambdaIntegration(id, fn.function, {
        payloadFormatVersion,
        scopePermissionToRoute: false,
      });
    const healthInt = lambdaIntegration(
      "HealthInt",
      healthCheck,
      apigateway.PayloadFormatVersion.VERSION_2_0,
    );
    const registerInt = lambdaIntegration("RegisterInt", registerFn);
    const faydaVerifyInt = lambdaIntegration("FaydaVerifyInt", faydaVerifyFn);
    const verifyOtpInt = lambdaIntegration("VerifyOtpInt", verifyOtpFn);
    const loginInt = lambdaIntegration("LoginInt", loginFn);
    const refreshInt = lambdaIntegration("RefreshInt", refreshFn);
    // SIM-60: payload 1.0 like every other route, so the logout handler sees the JWT claims under
    // authorizer.claims and can bind the token to the caller.
    const logoutInt = lambdaIntegration("LogoutInt", logoutFn);
    const passwordResetInt = lambdaIntegration("ForgotPasswordInt", passwordResetFn);
    const syncInt = lambdaIntegration("SyncInt", syncFn);
    const cropInt = lambdaIntegration("CropsListInt", cropFn);
    const matchInt = lambdaIntegration("MatchInt", matchFn);
    const deviceTokenInt = lambdaIntegration("DeviceTokensInt", deviceTokenFn);
    const accountDeletionInt = lambdaIntegration("AccountDeletionInt", accountDeletionFn);
    const tripInt = lambdaIntegration("TripsListInt", tripFn);
    const orderInt = lambdaIntegration("OrdersListInt", orderFn);
    const ratingInt = lambdaIntegration("RatingsListInt", ratingFn);
    const reputationInt = lambdaIntegration("ReputationMeInt", reputationFn);
    const messageInt = lambdaIntegration("MessagesListInt", messageFn);
    const notificationInt = lambdaIntegration("NotificationsListInt", notificationFn);
    const deliveryInt = lambdaIntegration("DeliveriesListInt", deliveryFn);
    const escrowInt = lambdaIntegration("EscrowInitiateInt", escrowFn);

    // ─── Routes ──────────────────────────────────────────────────────

    httpApi.addRoutes({
      path: "/health",
      methods: [apigateway.HttpMethod.GET],
      integration: healthInt,
    });

    httpApi.addRoutes({
      path: "/auth/register",
      methods: [apigateway.HttpMethod.POST],
      integration: registerInt,
    });

    httpApi.addRoutes({
      path: "/auth/fayda-verify",
      methods: [apigateway.HttpMethod.POST],
      integration: faydaVerifyInt,
    });

    // The server mints state, nonce and PKCE and returns the authorize URL, so the
    // app carries no Fayda client configuration.
    httpApi.addRoutes({
      path: "/auth/fayda-start",
      methods: [apigateway.HttpMethod.POST],
      integration: faydaVerifyInt,
    });

    // Link-later for accounts registered without Fayda (authenticated).
    httpApi.addRoutes({
      path: "/user/fayda-start",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: faydaVerifyInt,
    });

    httpApi.addRoutes({
      path: "/user/fayda-link",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: faydaVerifyInt,
    });

    httpApi.addRoutes({
      path: "/auth/verify-otp",
      methods: [apigateway.HttpMethod.POST],
      integration: verifyOtpInt,
    });

    httpApi.addRoutes({
      path: "/auth/resend-otp",
      methods: [apigateway.HttpMethod.POST],
      integration: verifyOtpInt,
    });

    httpApi.addRoutes({
      path: "/auth/login",
      methods: [apigateway.HttpMethod.POST],
      integration: loginInt,
    });

    httpApi.addRoutes({
      path: "/auth/refresh",
      methods: [apigateway.HttpMethod.POST],
      integration: refreshInt,
    });

    httpApi.addRoutes({
      path: "/auth/logout",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: logoutInt,
    });

    httpApi.addRoutes({
      path: "/auth/forgot-password",
      methods: [apigateway.HttpMethod.POST],
      integration: passwordResetInt,
    });

    httpApi.addRoutes({
      path: "/auth/reset-password",
      methods: [apigateway.HttpMethod.POST],
      integration: passwordResetInt,
    });

    httpApi.addRoutes({
      path: "/sync",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: syncInt,
    });

    // SIM-56 (user-entity.md 2.2.0): the caller reads their own profile. The sync Lambda serves it:
    // it owns the profile edit (the /sync user entity) and already reads the users table, so no new
    // grant or integration. After /sync, so the shared integration keeps its logical id.
    httpApi.addRoutes({
      path: "/user/profile",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: syncInt,
    });

    httpApi.addRoutes({
      path: "/crops",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: cropInt,
    });

    httpApi.addRoutes({
      path: "/crops/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: cropInt,
    });

    // crop-listing-api.md 1.6.0: presigned PUT URLs for a listing's photos.
    httpApi.addRoutes({
      path: "/crops/{id}/photos",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: cropInt,
    });

    httpApi.addRoutes({
      path: "/match",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: matchInt,
    });

    // SIM-56 (push-alerts-api.md 1.5.0): the buyer's own requests, read back after a reload or
    // on a new phone. Keyed by the JWT sub, never a parameter. Added after /match so the
    // shared integration keeps the logical id it has under that route.
    httpApi.addRoutes({
      path: "/intents",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: matchInt,
    });

    httpApi.addRoutes({
      path: "/device-tokens",
      methods: [apigateway.HttpMethod.POST, apigateway.HttpMethod.DELETE],
      authorizer: jwtAuthorizer,
      integration: deviceTokenInt,
    });

    // ACC-02: the caller deletes their own account. The browser preflight needs DELETE in the
    // CORS allowMethods above.
    httpApi.addRoutes({
      path: "/user/account",
      methods: [apigateway.HttpMethod.DELETE],
      authorizer: jwtAuthorizer,
      integration: accountDeletionInt,
    });

    httpApi.addRoutes({
      path: "/trips",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: tripInt,
    });

    httpApi.addRoutes({
      path: "/trips/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: tripInt,
    });

    // ─── Order Routes (authenticated) ────────────────────────────────

    httpApi.addRoutes({
      path: "/orders",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: orderInt,
    });

    httpApi.addRoutes({
      path: "/orders/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: orderInt,
    });

    // ─── Rating Routes (authenticated) ───────────────────────────────
    // Creates go through /sync; these are read-only.

    httpApi.addRoutes({
      path: "/ratings",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: ratingInt,
    });

    httpApi.addRoutes({
      path: "/ratings/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: ratingInt,
    });

    // ─── Reputation Routes (authenticated, ESC-29) ───────────────────
    // App user pool only: an admin pool token gets 401 here (admins use the /admin routes, ESC-32).
    // The static /reputation/me wins over /reputation/{userId}.
    const reputationRoutes: [string, apigateway.HttpMethod][] = [
      ["/reputation/me", apigateway.HttpMethod.GET],
      ["/reputation/me/events", apigateway.HttpMethod.GET],
      ["/reputation/{userId}", apigateway.HttpMethod.GET],
      ["/reputation/appeals/photo-url", apigateway.HttpMethod.POST],
      ["/reputation/events/{eventId}/appeal", apigateway.HttpMethod.POST],
    ];
    for (const [path, method] of reputationRoutes) {
      httpApi.addRoutes({
        path,
        methods: [method],
        authorizer: jwtAuthorizer,
        integration: reputationInt,
      });
    }

    // ─── Message Routes (authenticated) ──────────────────────────────
    // Creates go through /sync; these are read-only (thread load + pull).

    httpApi.addRoutes({
      path: "/messages",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: messageInt,
    });

    httpApi.addRoutes({
      path: "/messages/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: messageInt,
    });

    // ─── Notification Routes (authenticated, ESC-21) ─────────────────
    // Read-only: the inbox is written by OrderNotifyStreamFunction. The
    // handler scopes the query to the caller's own userId (JWT sub).

    httpApi.addRoutes({
      path: "/notifications",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: notificationInt,
    });

    // ─── R4.2 Delivery Routes (authenticated) ────────────────────────

    httpApi.addRoutes({
      path: "/deliveries",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: deliveryInt,
    });

    httpApi.addRoutes({
      path: "/deliveries/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: deliveryInt,
    });

    // LT-06: where the truck is, for the delivery's three parties (the handler answers 404 to anyone
    // else). Without this route the request never reaches the Lambda.
    httpApi.addRoutes({
      path: "/deliveries/{id}/track",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: deliveryInt,
    });

    httpApi.addRoutes({
      path: "/deliveries/{id}/photos",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: deliveryInt,
    });

    // ─── R4.3 Escrow Routes (all authenticated) ──────────────────────

    httpApi.addRoutes({
      path: "/escrow/initiate",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: escrowInt,
    });

    // ESC-01: the buyer opens (or gets back) the live Telebirr checkout for a
    // payment_submitted order. With mock payments it answers ESCROW_031.
    httpApi.addRoutes({
      path: "/escrow/order-checkout",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: escrowInt,
    });

    httpApi.addRoutes({
      path: "/escrow/release",
      methods: [apigateway.HttpMethod.POST],
      // Admin pool only: settlement is an operator action (or the cron, in-process).
      authorizer: adminJwtAuthorizer,
      integration: escrowInt,
    });

    httpApi.addRoutes({
      path: "/escrow/dispute",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: escrowInt,
    });

    httpApi.addRoutes({
      path: "/escrow/refund",
      methods: [apigateway.HttpMethod.POST],
      // Admin pool only: settlement is an operator action (or the cron, in-process).
      authorizer: adminJwtAuthorizer,
      integration: escrowInt,
    });

    httpApi.addRoutes({
      path: "/escrow/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: escrowInt,
    });

    httpApi.addRoutes({
      path: "/escrow/history",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: escrowInt,
    });

    // F-33: fetch the escrow for a delivery. Distinct 3-segment path so it does
    // not collide with /escrow/{id}; server enforces party-or-admin (ESCROW_022).
    httpApi.addRoutes({
      path: "/escrow/by-delivery/{deliveryId}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: escrowInt,
    });

    // ─── Telebirr live env wiring + public webhook route (gated) ──────
    // Notify/callback URL and secret access are attached here (after the API
    // exists so we can derive the public URL). The escrow/cron Lambdas make the
    // outbound call; the webhook Lambda receives the callback.
    if (enableTelebirrLive && telebirrSecret) {
      const notifyUrl = `${httpApi.apiEndpoint}${TELEBIRR_WEBHOOK_PATH}`;
      const liveFns = [escrowFn, escrowCronFn, telebirrWebhookFn].filter(
        (f): f is WeredaLambda => f !== undefined,
      );
      for (const fn of liveFns) {
        fn.function.addEnvironment(
          "TELEBIRR_SECRET_ARN",
          telebirrSecret.reference,
        );
        fn.function.addEnvironment("TELEBIRR_NOTIFY_URL", notifyUrl);
        if (envConfig.telebirrAllowedIps) {
          fn.function.addEnvironment(
            "TELEBIRR_ALLOWED_IPS",
            envConfig.telebirrAllowedIps,
          );
        }
        telebirrSecret.grantRead(fn.function);
      }

      if (telebirrWebhookFn) {
        telebirrWebhookRoutes = httpApi.addRoutes({
          path: TELEBIRR_WEBHOOK_PATH,
          methods: [apigateway.HttpMethod.POST],
          // Public: NO authorizer. Telebirr authenticates via its RSA signature,
          // which TelebirrWebhookFunction verifies (+ optional IP allow-list).
          integration: lambdaIntegration("TelebirrWebhookInt", telebirrWebhookFn),
        });
      }
    }

    // ─── Admin Lambda Function ────────────────────────────────────────

    const adminFn = new WeredaLambda(this, "AdminFunction", {
      functionName: `${prefix}-admin`,
      description:
        "Admin endpoints: dashboard stats, escrow management, fraud review, user management",
      handlerName: "AdminFunction",
      environment: {
        ESCROWS_TABLE: props.escrowsTable.tableName,
        ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
        // ESC-20 (P.8): the manual payout queue lists and records ledger rows.
        ESCROW_LEDGER_TABLE: props.escrowLedgerTable.tableName,
        TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
        FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName,
        USERS_TABLE: props.usersTable.tableName,
        COGNITO_USER_POOL_ID: this.userPool.userPoolId,
        COGNITO_CLIENT_ID: this.userPoolClient.userPoolClientId,
        CROPS_TABLE: props.cropsTable.tableName,
        TRIPS_TABLE: props.tripsTable.tableName,
        ORDERS_TABLE: props.ordersTable.tableName,
        DELIVERIES_TABLE: props.deliveriesTable.tableName,
        PAYMENTS_TABLE: props.paymentsTable.tableName,
        // FUP-02: the claim row that makes a manual refund record exclusive.
        ESCROW_IDEMPOTENCY_TABLE: props.escrowIdempotencyTable.tableName,
        // LT-13: the trip's check-ins for the dispute evidence trail.
        TRIP_LOCATIONS_TABLE: props.tripLocationsTable.tableName,
        // FRD-12: the transaction view signs GETs of the delivery's handoff photos.
        UPLOADS_BUCKET: props.uploadsBucket.bucketName,
        // FRD-14: the dispute evidence reads the disputed order's message threads, audited.
        MESSAGES_TABLE: props.messagesTable.tableName,
        // FUP-67: the queue of buyers' listing reports.
        LISTING_REPORTS_TABLE: props.listingReportsTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
        // ESC-29 (reputation-api.md 8.4): a suspend or unsuspend leaves the day's heal marker, so
        // the daily job rewrites the owner fields copied onto the user's listings. Off: no marker.
        // ESC-32 (reputation-api.md section 9): the same table serves the admin reputation routes.
        // The reads work whatever the flag says; the writes answer 503 while it is off.
        REPUTATION_EVENTS_TABLE: props.reputationEventsTable.tableName,
        REPUTATION_PENALTIES_ENABLED: reputationPenalties,
        // ESC-32: the policy (thresholds, tag and restriction limits), read only while the flag is on.
        REPUTATION_POLICY_PARAM: reputationPolicyParam.parameterName,
      },
      timeout: cdk.Duration.seconds(30),
      memorySize: 512,
      reservedConcurrentExecutions: 10,
    });
    // ESC-29: the heal marker is one UpdateItem on the user's reputation events.
    // ESC-32: the admin reputation routes read a user's partition (Query, strongly consistent) and
    // write a decision, a lift or a replaced tag (UpdateItem) and a restriction, tag or tier row
    // (PutItem), all in one transaction with the users map (the users write grant below). The
    // appeals queue is a Query on AppealStatusIndex and on no other index. No Scan, no delete.
    adminFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query", "dynamodb:PutItem", "dynamodb:UpdateItem"],
        resources: [props.reputationEventsTable.tableArn],
      }),
    );
    adminFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [`${props.reputationEventsTable.tableArn}/index/AppealStatusIndex`],
      }),
    );
    // ESC-32 (and TASK FUP-163 for a suspend): the owner fields copied onto the user's active
    // listings are refreshed in the admin request. The rows are found with the read grants below
    // (the owner indexes and the crop BatchGetItem); the copy itself is a conditional UpdateItem.
    props.cropsTable.grant(adminFn.function, "dynamodb:UpdateItem");
    props.tripsTable.grant(adminFn.function, "dynamodb:UpdateItem");
    reputationPolicyParam.grantRead(adminFn.function);

    // Pagination secret access
    paginationSecret.grantRead(adminFn.function);

    // Read access on all tables
    props.escrowsTable.grantReadData(adminFn.function);
    props.escrowAuditTable.grantReadData(adminFn.function);
    props.usersTable.grantReadData(adminFn.function);
    props.cropsTable.grantReadData(adminFn.function);
    props.tripsTable.grantReadData(adminFn.function);
    props.ordersTable.grantReadData(adminFn.function);
    props.deliveriesTable.grantReadData(adminFn.function);
    props.paymentsTable.grantReadData(adminFn.function);
    // LT-13: the delivery trail reads the trip's check-ins (Query by tripId) and nothing else.
    // The delivery itself comes from the deliveries read above.
    props.tripLocationsTable.grant(adminFn.function, "dynamodb:Query");
    // FRD-12: a presigned GET carries its signer's rights, so the admin may read the photo keys.
    props.uploadsBucket.grantRead(adminFn.function, "uploads/photos/*");
    // ESC-32: the same for the appeal photos shown in the reputation panel and the appeals queue
    // (presigned GETs of 10 minutes). Read only, under the appeal prefix only.
    props.uploadsBucket.grantRead(adminFn.function, "uploads/appeals/*");
    // FRD-14: a dispute's message threads, one Query by conversation id each. Query on that index
    // only: no GetItem, no other index (no listing of a user's messages), no write.
    adminFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [
          `${props.messagesTable.tableArn}/index/ConversationIdCreatedAtIndex`,
        ],
      }),
    );

    // FUP-67: the report queue reads the status index and a listing's reports (Query), and a
    // review moves each open report (conditional UpdateItem). No PutItem, Scan or delete.
    props.listingReportsTable.grant(adminFn.function, "dynamodb:Query", "dynamodb:UpdateItem");

    // Write access on tables the admin mutates
    props.escrowsTable.grantWriteData(adminFn.function);
    // ESC-03: a dispute verdict moves the order and delivery to it. UpdateItem only:
    // the admin Lambda never puts or deletes rows in these tables.
    props.ordersTable.grant(adminFn.function, "dynamodb:UpdateItem");
    props.deliveriesTable.grant(adminFn.function, "dynamodb:UpdateItem");
    props.escrowAuditTable.grantWriteData(adminFn.function);
    // ESC-20 (P.8): the manual payout queue reads ledger rows (GetItem, Query on the table and
    // LedgerOpenIndex) and records a paid row (UpdateItem in a transaction with the escrow and its audit
    // row). The admin Lambda never puts or calls a ledger row.
    grantEscrowLedger(adminFn.function, ["dynamodb:GetItem", "dynamodb:Query", "dynamodb:UpdateItem"]);
    // FUP-02: a manual refund record writes its escrow-audit row and a claim row here in one
    // TransactWriteItems. IAM has no separate transaction action: each Put needs PutItem on its
    // table (a conditional Put needs nothing more; ConditionCheckItem is only for ConditionCheck
    // items, which this transaction has none of).
    // FUP-05: a manual payout record writes its reference item (payout-ref#...) the same way, in the
    // record's transaction, and a refused duplicate reads that item (GetItem, strongly consistent) to
    // name where the reference is used. FUP-218: the correction of a recorded reference deletes the old
    // reference item in its transaction (a conditional Delete: DeleteItem), only while the item names the
    // corrected leg. No update, Query or Scan.
    props.escrowIdempotencyTable.grant(adminFn.function, "dynamodb:PutItem", "dynamodb:GetItem", "dynamodb:DeleteItem");
    // FRD-07: the read grant covers the table's indexes too, so Fraud Review and the dashboard
    // count Query the sparse StatusCreatedAtIndex instead of scanning the table.
    props.fraudSignalsTable.grantReadWriteData(adminFn.function);
    props.usersTable.grantWriteData(adminFn.function);
    // Transaction audit: admin actions are logged here, and the monitor +
    // audit-log read it (read for the timeline/feed, write for admin actions).
    props.transactionAuditTable.grantReadWriteData(adminFn.function);

    // ESC-16: every Lambda that writes orders derives the order's next deadline with the same
    // timings, so each reads the order timing parameters (one GetParameters per container). The
    // trips Lambda too: its orderLeadHours (FUP-122) is the response window the order create checks.
    for (const fn of [
      syncFn, deliveryFn, escrowFn, escrowCronFn, telebirrWebhookFn, orderFn, messageFn, orderCronFn, adminFn, tripFn,
    ]) {
      if (fn) {
        readsOrderTimings(fn.function);
      }
    }

    // Admin pool permissions (admin auth)
    adminFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: [
          "cognito-idp:AdminGetUser",
          "cognito-idp:AdminListGroupsForUser",
        ],
        resources: [this.adminUserPool.userPoolArn],
      }),
    );

    // Mobile pool permissions (user management)
    this.grantCognito(adminFn.function, [
      "cognito-idp:AdminGetUser",
      "cognito-idp:AdminDisableUser",
      "cognito-idp:AdminUserGlobalSignOut",
      "cognito-idp:AdminEnableUser",
      "cognito-idp:AdminUpdateUserAttributes",
      "cognito-idp:ListUsers",
      "cognito-idp:AdminListGroupsForUser",
      "cognito-idp:AdminAddUserToGroup",
      "cognito-idp:AdminRemoveUserFromGroup",
    ]);

    // ─── Admin Routes (all authenticated) ─────────────────────────────

    // FUP-217: every admin route shares this one integration and the admin Lambda's one invoke
    // permission (see lambdaIntegration). The id is the first admin route's (GET /admin/dashboard/stats),
    // so that integration keeps its logical id.
    const adminInt = lambdaIntegration("Admindashboard_statsGetInt", adminFn);

    // GET routes
    const adminGetPaths = [
      "/admin/dashboard/stats",
      "/admin/escrows",
      // FRD-07: the held queue (escrows frozen for fraud review), a Query on StatusExpiresIndex.
      // A static segment, so it wins over /admin/escrows/{id}.
      "/admin/escrows/held",
      "/admin/escrows/{id}",
      "/admin/escrows/{id}/audit",
      // ESC-24: the escrow's ledger rows, read only (a Query on the ledger table; the grant is ESC-20's).
      "/admin/escrows/{id}/ledger",
      // ESC-24: stuck orders, from AdminQueueIndex or DeadlineIndex of the orders table (the read grant covers both).
      "/admin/orders/queue",
      "/admin/disputes",
      // FRD-14: a dispute's evidence package (admin group checked in the Lambda; messages only
      // with include=messages, each read audited).
      "/admin/disputes/{id}/evidence",
      "/admin/fraud-signals",
      // FUP-67: buyers' reports of listings, one status at a time.
      "/admin/listing-reports",
      "/admin/users",
      "/admin/users/{id}",
      "/admin/health/overview",
      "/admin/compliance/summary",
      "/admin/analytics/revenue",
      "/admin/analytics/transactions",
      "/admin/transactions",
      "/admin/transactions/{orderId}",
      "/admin/audit",
      "/admin/payouts/manual",
      // FUP-02: captured payments the webhook did not apply (bounded escrow-audit Scan).
      "/admin/payments/unapplied",
      // LT-13: the trip trail of a delivery, dispute evidence (admin group checked in the Lambda).
      "/admin/deliveries/{id}/trail",
      // ESC-32 (reputation-api.md 9.1, 9.2): a user's reputation and the appeals queue (admin
      // group checked in the Lambda).
      "/admin/users/{id}/reputation",
      "/admin/reputation/appeals",
    ];

    for (const path of adminGetPaths) {
      httpApi.addRoutes({
        path,
        methods: [apigateway.HttpMethod.GET],
        authorizer: adminJwtAuthorizer,
        integration: adminInt,
      });
    }

    // POST routes
    const adminPostPaths = [
      "/admin/disputes/{id}/resolve",
      "/admin/fraud-signals/{id}/action",
      // FUP-67: decide a listing's open reports (dismissed or actioned).
      "/admin/listing-reports/{listingId}/review",
      "/admin/users/{id}/action",
      // ESC-02: record an admin-paid manual payout leg ({action} = complete). FUP-05: claim a leg before
      // paying it (claim) and give it back (unclaim); a claim is a conditional UpdateItem on the escrow row
      // or the ledger row (existing grants). FUP-218: correct, the reference of a recorded leg (the
      // escrow-idempotency DeleteItem grant above). One route for them all: with Telebirr live on, this stack
      // then held 497 of CloudFormation's 500 resources (FUP-217 has since freed room). The Lambda
      // answers 404 ADMIN_002 for any other action; admin group checked there.
      "/admin/payouts/{escrowId}/{ref}/{action}",
      // FUP-02: record the manual refund of such a payment (admin group checked in the Lambda).
      // Uses the existing grants: escrow-audit read + write, orders read, transaction-audit write.
      "/admin/payments/{orderId}/refund-manual",
      // ESC-32 (reputation-api.md 9.3, 9.4): excuse or uphold one event; tag, untag, restrict or
      // lift (admin group checked in the Lambda; 503 while reputationPenalties is off).
      "/admin/users/{id}/reputation/events/{eventId}/decision",
      "/admin/users/{id}/reputation",
      // FUP-19: cancel a delivery after pickup ({action} = cancel; any other action is 404 ADMIN_002, admin
      // group checked in the Lambda). One transaction on the existing grants: UpdateItem on deliveries, PutItem
      // on transaction-audit, and ConditionChecks on orders and escrows (ConditionCheckItem is in their read
      // grants).
      "/admin/deliveries/{id}/{action}",
    ];

    for (const path of adminPostPaths) {
      httpApi.addRoutes({
        path,
        methods: [apigateway.HttpMethod.POST],
        authorizer: adminJwtAuthorizer,
        integration: adminInt,
      });
    }

    // ─── API Gateway Throttling + Access Logs ────────────────────────
    const defaultStage = httpApi.defaultStage?.node
      .defaultChild as apigateway.CfnStage;
    if (defaultStage) {
      const stageThrottling = {
        ThrottlingBurstLimit: 50,
        ThrottlingRateLimit: 25,
      };
      defaultStage.addPropertyOverride("DefaultRouteSettings", stageThrottling);

      // FUP-39: route metrics for the Telebirr webhook only, so the API-wide 5xx alarm
      // (monitoring-stack.ts) can leave out its deliberate 500s (ESC-45 D1) and the webhook
      // gets alarms of its own. Detailed metrics are billed as custom metrics, so not for the
      // whole stage. The route's settings repeat the stage throttling so the webhook keeps
      // its limits, and the stage waits for the route: API Gateway refuses route settings
      // for a route key that does not exist yet.
      if (telebirrWebhookRoutes.length > 0) {
        defaultStage.addPropertyOverride("RouteSettings", {
          [`${apigateway.HttpMethod.POST} ${TELEBIRR_WEBHOOK_PATH}`]: {
            ...stageThrottling,
            DetailedMetricsEnabled: true,
          },
        });
        defaultStage.node.addDependency(...telebirrWebhookRoutes);
      }

      // Edge-level audit: one structured JSON line per request (who/when/where
      // + method/path/status/latency), independent of any Lambda. Answers "was
      // this request even received, from where, and did it 4xx/5xx" without
      // reading function logs. Retained in prod (audit), destroyed in dev.
      const accessLogGroup = new logs.LogGroup(this, "ApiAccessLogs", {
        logGroupName: `/aws/apigateway/${prefix}-http-api/access`,
        retention: logs.RetentionDays.ONE_YEAR,
        removalPolicy: envConfig.isProd
          ? cdk.RemovalPolicy.RETAIN
          : cdk.RemovalPolicy.DESTROY,
      });
      // Access logging is wired via the L1 CfnStage (the L2 HttpApi auto-creates
      // the default stage), so grant API Gateway permission to deliver logs
      // explicitly — the L2 log-destination construct would otherwise do this.
      accessLogGroup.grantWrite(new iam.ServicePrincipal("apigateway.amazonaws.com"));
      defaultStage.addPropertyOverride("AccessLogSettings", {
        DestinationArn: accessLogGroup.logGroupArn,
        Format: JSON.stringify({
          requestId: "$context.requestId",
          ip: "$context.identity.sourceIp",
          requestTime: "$context.requestTime",
          httpMethod: "$context.httpMethod",
          routeKey: "$context.routeKey",
          path: "$context.path",
          status: "$context.status",
          protocol: "$context.protocol",
          responseLength: "$context.responseLength",
          integrationLatency: "$context.integrationLatency",
          userAgent: "$context.identity.userAgent",
        }),
      });
    }

    // ─── Outputs ─────────────────────────────────────────────────────
    new cdk.CfnOutput(this, "ApiUrl", {
      value: this.httpApiUrl,
      description: "API Gateway URL",
    });

    new cdk.CfnOutput(this, "UserPoolId", {
      value: this.userPool.userPoolId,
      description: "Cognito User Pool ID",
    });

    new cdk.CfnOutput(this, "UserPoolClientId", {
      value: this.userPoolClient.userPoolClientId,
      description: "Cognito App Client ID",
    });

    new cdk.CfnOutput(this, "AdminUserPoolId", {
      value: this.adminUserPool.userPoolId,
      description: "Cognito Admin User Pool ID",
    });

    new cdk.CfnOutput(this, "AdminClientId", {
      value: adminClient.userPoolClientId,
      description: "Cognito Admin App Client ID",
    });
  }

  /**
   * SMS publishing (OTP codes, security notices). An SMS publish carries no
   * resource ARN, so the allow must be on "*"; the explicit deny on every SNS ARN
   * keeps the role from publishing to topics or push endpoints.
   */
  private grantSmsPublish(fn: cdk.aws_lambda.Function) {
    fn.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["sns:Publish"],
        resources: ["*"],
      }),
    );
    fn.addToRolePolicy(
      new iam.PolicyStatement({
        effect: iam.Effect.DENY,
        actions: ["sns:Publish"],
        resources: ["arn:aws:sns:*:*:*"],
      }),
    );
  }

  /** Grant specific Cognito actions on the User Pool */
  private grantCognito(fn: cdk.aws_lambda.Function, actions: string[]) {
    fn.addToRolePolicy(
      new iam.PolicyStatement({
        actions,
        resources: [this.userPool.userPoolArn],
      }),
    );
  }
}

/**
 * T-SEC-01/02: CDK Aspect that scans every Lambda in the stack and errors if any
 * dev-only flag (DEV_FAYDA_BYPASS, TELEBIRR_MOCK, DEV_OTP_BYPASS, FAYDA_MOCK,
 * FAYDA_SANDBOX and its redirect) is set in a production deployment.
 * Attach via `cdk.Aspects.of(stack).add(new ProductionBypassGuard())`.
 */
class ProductionBypassGuard implements cdk.IAspect {
  private static readonly FORBIDDEN_ENV_VARS = [
    "DEV_FAYDA_BYPASS",
    "TELEBIRR_MOCK",
    "DEV_OTP_BYPASS",
    // Set by the mock Fayda eSignet Lambda (tools/fayda-mock); a fake identity
    // provider must never be deployable beside production.
    "FAYDA_MOCK",
    // The real VeriFayda sandbox with its shared, publicly known test client: dev only.
    "FAYDA_SANDBOX",
    "FAYDA_SANDBOX_REDIRECT_URI",
  ];

  visit(node: Construct): void {
    if (node instanceof lambda.Function) {
      const cfnFn = node.node.defaultChild as lambda.CfnFunction | undefined;
      if (!cfnFn) return;

      // cfnFn.environment is a CfnFunction.EnvironmentProperty whose env vars
      // live under `.variables` (and the whole thing is a Lazy token at synth
      // time). Resolve the token, then inspect the nested `variables` map —
      // checking `flag in env` on the outer object never matches (its only
      // key is `variables`), which silently defeated this guard.
      const resolved = cdk.Stack.of(node).resolve(cfnFn.environment) as
        | { variables?: Record<string, unknown> }
        | undefined;
      const variables = resolved?.variables ?? {};
      if (typeof variables === "object" && variables !== null) {
        for (const flag of ProductionBypassGuard.FORBIDDEN_ENV_VARS) {
          if (flag in variables) {
            cdk.Annotations.of(node).addError(
              `SECURITY: Lambda "${node.node.path}" has ${flag} set. ` +
              `This flag must NEVER be deployed to production.`,
            );
          }
        }
      }
    }
  }
}
