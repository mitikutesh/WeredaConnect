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
import * as secretsmanager from "aws-cdk-lib/aws-secretsmanager";
import * as ssm from "aws-cdk-lib/aws-ssm";
import * as lambda from "aws-cdk-lib/aws-lambda";
import * as lambdaEventSources from "aws-cdk-lib/aws-lambda-event-sources";
import * as logs from "aws-cdk-lib/aws-logs";
import { WeredaLambda } from "../constructs/lambda-construct";
import { FaydaMockConstruct } from "../constructs/fayda-mock-construct";
import { EnvironmentConfig } from "../config/environments";

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
  readonly uploadsBucket: s3.Bucket;
}

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
      environment: cognitoEnv,
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
        // Confirming a delivery stamps the linked escrow so the auto-release cron
        // can pay the farmer. Without this env var (+ grant below) _escrowRepo is
        // null and the delivery-confirmation -> escrow payout bridge is disabled.
        ESCROWS_TABLE: props.escrowsTable.tableName,
        // Order-funded escrow leg: funding materializes an escrow record, delivery
        // creation links it, reject/cancel schedules its refund — every mutation
        // lands in the immutable escrow-audit ledger. Without this env the leg is
        // disabled (orders still fund; escrow records aren't created here).
        ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
        // FRD-03: on, the farmer's accept is one transaction with a check that the order's escrow
        // is not held (a sweep may hold it between the read and the write). Same value as on
        // escrow-cron; both are turned on together after FRD-06.
        FRAUD_HOLD_ENABLED: "false",
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

    // ─── Crop Function ───────────────────────────────────────────────

    const cropFn = new WeredaLambda(this, "CropFunction", {
      functionName: `${prefix}-crops`,
      description: "GET /crops — browse crop listings (read-only)",
      handlerName: "CropFunction",
      environment: {
        CROPS_TABLE: props.cropsTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    // Crop function needs read-only access to crops table
    props.cropsTable.grantReadData(cropFn.function);
    paginationSecret.grantRead(cropFn.function);

    // ─── Trip Function ────────────────────────────────────────────────

    const tripFn = new WeredaLambda(this, "TripFunction", {
      functionName: `${prefix}-trips`,
      description: "GET /trips — browse trip listings (read-only)",
      handlerName: "TripFunction",
      environment: {
        TRIPS_TABLE: props.tripsTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
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
      description: "GET /match — buyer intent matching (ranked crop listings)",
      handlerName: "MatchFunction",
      environment: {
        CROPS_TABLE: props.cropsTable.tableName,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    // Match function reads crops to score them against the buyer's criteria
    props.cropsTable.grantReadData(matchFn.function);

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
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    props.deviceTokensTable.grantReadWriteData(deviceTokenFn.function);
    deviceTokenFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["sns:CreatePlatformEndpoint"],
        resources: [fcmPlatformAppArn],
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

    // ─── Fayda identity provider: mock (dev) or live, switched by config ─
    // The server code is identical in both modes; only the issuer, client id
    // and client private key it reads from SSM differ. faydaMode 'mock' deploys
    // the mock eSignet provider (tools/fayda-mock) and points these values at it.
    // Switching to live Fayda: specs/features/fayda-mock-server.md.
    // The construct refuses isProd, and ProductionBypassGuard refuses its
    // FAYDA_MOCK marker, so the mock can never sit beside production.
    if (envConfig.faydaMode === "mock" && envConfig.isProd) {
      cdk.Annotations.of(this).addError(
        "faydaMode 'mock' is not allowed in a production environment; use 'live'.",
      );
    }
    const useFaydaMock = envConfig.faydaMode === "mock" && !envConfig.isProd;
    // Web app callback pages (mobile/web/fayda-callback.html). Browsers cannot open
    // the weredaconnect:// deep link, so the web app gets an https callback, but only
    // against the mock: the real eSignet client registers the deep links alone.
    const faydaMockWebRedirectUris = useFaydaMock
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
            ...faydaMockWebRedirectUris,
          ],
        })
      : undefined;

    // ─── Fayda SSM Parameters ──────────────────────────────────────────

    const faydaClientIdParam = new ssm.StringParameter(
      this,
      "FaydaClientId",
      {
        parameterName: `/${prefix}/fayda/client-id`,
        stringValue: faydaMock?.clientId ?? envConfig.faydaClientId ?? "PLACEHOLDER",
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
        // Mock-only web callbacks. FAYDA_MOCK is refused in prod by
        // ProductionBypassGuard, and the server ignores the list without it.
        ...(faydaMock
          ? {
              FAYDA_MOCK: "true",
              FAYDA_MOCK_WEB_REDIRECT_URIS: faydaMockWebRedirectUris.join(","),
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
        "GET /deliveries, POST /deliveries/{id}/photos — delivery verification",
      handlerName: "DeliveryFunction",
      environment: {
        DELIVERIES_TABLE: props.deliveriesTable.tableName,
        TRIPS_TABLE: props.tripsTable.tableName,
        UPLOADS_BUCKET: props.uploadsBucket.bucketName,
        USERS_TABLE: props.usersTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
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
        COMMISSION_RATE_PARAM: commissionRateParam.parameterName,
        DISPUTE_WINDOW_PARAM: disputeWindowParam.parameterName,
        ESCROW_TIMEOUT_PARAM: escrowTimeoutParam.parameterName,
        // FRD-03: off, a sweep that finds a suspended party skips that settlement and logs
        // RISK_SETTLEMENT_SKIPPED; on, it writes the escrow status `held`. Turn on only once every
        // client parses `held` (FRD-06 on web and in both stores, fraud-detection-architecture.md 18.7).
        FRAUD_HOLD_ENABLED: "false",
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
    // Payout destinations and the release-time identity gate read profiles.
    props.usersTable.grantReadData(escrowCronFn.function);
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
        },
        timeout: cdk.Duration.seconds(15),
        memorySize: 512,
        logRetention: logs.RetentionDays.ONE_YEAR,
        reservedConcurrentExecutions: 10,
      });
      props.escrowsTable.grantReadWriteData(telebirrWebhookFn.function);
      props.escrowAuditTable.grantWriteData(telebirrWebhookFn.function);
      props.escrowIdempotencyTable.grantReadWriteData(telebirrWebhookFn.function);
      props.usersTable.grantReadData(telebirrWebhookFn.function);
      props.ordersTable.grantReadWriteData(telebirrWebhookFn.function);
      props.cropsTable.grantReadWriteData(telebirrWebhookFn.function);
      props.transactionAuditTable.grantWriteData(telebirrWebhookFn.function);

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
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    props.ratingsTable.grantReadData(ratingFn.function);
    paginationSecret.grantRead(ratingFn.function);

    // ─── Message Function ────────────────────────────────────────────

    const messageFn = new WeredaLambda(this, "MessageFunction", {
      functionName: `${prefix}-messages`,
      description: "GET /messages — read chat threads + sync-down pull (read-only)",
      handlerName: "MessageFunction",
      environment: {
        MESSAGES_TABLE: props.messagesTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.reference,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    props.messagesTable.grantReadData(messageFn.function);
    paginationSecret.grantRead(messageFn.function);

    // ─── Order Cron Function (auto-cancel expired unpaid orders) ────

    const orderCronFn = new WeredaLambda(this, "OrderCronFunction", {
      functionName: `${prefix}-order-cron`,
      description:
        "Scheduled order cleanup: auto-cancel expired unpaid orders",
      handlerName: "OrderCronFunction",
      environment: {
        ORDERS_TABLE: props.ordersTable.tableName,
        TRANSACTION_AUDIT_TABLE: props.transactionAuditTable.tableName,
        // ESC-45: the payment-failed step skips orders whose verified Telebirr
        // payment is still pending in the webhook inbox.
        ESCROW_IDEMPOTENCY_TABLE: props.escrowIdempotencyTable.tableName,
      },
      timeout: cdk.Duration.minutes(5),
      memorySize: 256,
      reservedConcurrentExecutions: 2,
    });
    props.ordersTable.grantReadWriteData(orderCronFn.function);
    // Auto-cancel writes an order lifecycle event to the audit trail.
    props.transactionAuditTable.grantWriteData(orderCronFn.function);
    // ESC-45: Query on the inbox replay index only, nothing else in that table.
    orderCronFn.function.addToRolePolicy(
      new iam.PolicyStatement({
        actions: ["dynamodb:Query"],
        resources: [
          `${props.escrowIdempotencyTable.tableArn}/index/InboxReplayIndex`,
        ],
      }),
    );

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

    // ─── HTTP API Gateway ────────────────────────────────────────────

    // CORS origins: prod uses exact subdomains, dev allows localhost + wildcard
    const corsOrigins = envConfig.isProd
      ? [
          "https://app.weredaconnect.com",
          "https://admin.weredaconnect.com",
        ]
      : [
          "http://localhost:3000",
          "http://localhost:4321",
          "http://localhost:5173",
          "http://localhost:8080",
          `https://${envConfig.domain.webappSubdomain}.${envConfig.domain.zoneName}`,
          `https://${envConfig.domain.adminSubdomain}.${envConfig.domain.zoneName}`,
          ...(envConfig.extraCorsOrigins ?? []),
        ];

    const httpApi = new apigateway.HttpApi(this, "HttpApi", {
      apiName: `${prefix}-api`,
      description: "WeredaConnect HTTP API — optimized for Ethiopian context",
      corsPreflight: {
        allowOrigins: corsOrigins,
        // X-Device-Id / X-Device-Name ride on login, register and verify-otp
        // (server DeviceIdentity). Without them the browser preflight fails and
        // the web app cannot sign up or sign in.
        allowHeaders: [
          "Content-Type",
          "Authorization",
          "X-Request-Id",
          "X-Device-Id",
          "X-Device-Name",
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

    // ─── Routes ──────────────────────────────────────────────────────

    httpApi.addRoutes({
      path: "/health",
      methods: [apigateway.HttpMethod.GET],
      integration: new integrations.HttpLambdaIntegration(
        "HealthInt",
        healthCheck.function,
      ),
    });

    httpApi.addRoutes({
      path: "/auth/register",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "RegisterInt",
        registerFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/auth/fayda-verify",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "FaydaVerifyInt",
        faydaVerifyFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // The server mints state, nonce and PKCE and returns the authorize URL, so the
    // app carries no Fayda client configuration.
    httpApi.addRoutes({
      path: "/auth/fayda-start",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "FaydaStartInt",
        faydaVerifyFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // Link-later for accounts registered without Fayda (authenticated).
    httpApi.addRoutes({
      path: "/user/fayda-start",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "FaydaUserStartInt",
        faydaVerifyFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/user/fayda-link",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "FaydaUserLinkInt",
        faydaVerifyFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/auth/verify-otp",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "VerifyOtpInt",
        verifyOtpFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/auth/resend-otp",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "ResendOtpInt",
        verifyOtpFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/auth/login",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "LoginInt",
        loginFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/auth/refresh",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "RefreshInt",
        refreshFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/auth/logout",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "LogoutInt",
        logoutFn.function,
        {
          // SIM-60: payload 1.0 like every other route, so the handler sees the
          // JWT claims under authorizer.claims and can bind the token to the caller.
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/auth/forgot-password",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "ForgotPasswordInt",
        passwordResetFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/auth/reset-password",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "ResetPasswordInt",
        passwordResetFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/sync",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "SyncInt",
        syncFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/crops",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "CropsListInt",
        cropFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/crops/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "CropsGetInt",
        cropFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/match",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "MatchInt",
        matchFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/device-tokens",
      methods: [apigateway.HttpMethod.POST, apigateway.HttpMethod.DELETE],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "DeviceTokensInt",
        deviceTokenFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/trips",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "TripsListInt",
        tripFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/trips/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "TripsGetInt",
        tripFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // ─── Order Routes (authenticated) ────────────────────────────────

    httpApi.addRoutes({
      path: "/orders",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "OrdersListInt",
        orderFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/orders/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "OrdersGetInt",
        orderFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // ─── Rating Routes (authenticated) ───────────────────────────────
    // Creates go through /sync; these are read-only.

    httpApi.addRoutes({
      path: "/ratings",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "RatingsListInt",
        ratingFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/ratings/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "RatingsGetInt",
        ratingFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // ─── Message Routes (authenticated) ──────────────────────────────
    // Creates go through /sync; these are read-only (thread load + pull).

    httpApi.addRoutes({
      path: "/messages",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "MessagesListInt",
        messageFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/messages/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "MessagesGetInt",
        messageFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // ─── R4.2 Delivery Routes (authenticated) ────────────────────────

    httpApi.addRoutes({
      path: "/deliveries",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "DeliveriesListInt",
        deliveryFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/deliveries/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "DeliveriesGetInt",
        deliveryFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/deliveries/{id}/photos",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "DeliveriesPhotosInt",
        deliveryFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // ─── R4.3 Escrow Routes (all authenticated) ──────────────────────

    httpApi.addRoutes({
      path: "/escrow/initiate",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "EscrowInitiateInt",
        escrowFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // ESC-01: the buyer opens (or gets back) the live Telebirr checkout for a
    // payment_submitted order. With mock payments it answers ESCROW_031.
    httpApi.addRoutes({
      path: "/escrow/order-checkout",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "EscrowOrderCheckoutInt",
        escrowFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/escrow/release",
      methods: [apigateway.HttpMethod.POST],
      // Admin pool only: settlement is an operator action (or the cron, in-process).
      authorizer: adminJwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "EscrowReleaseInt",
        escrowFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/escrow/dispute",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "EscrowDisputeInt",
        escrowFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/escrow/refund",
      methods: [apigateway.HttpMethod.POST],
      // Admin pool only: settlement is an operator action (or the cron, in-process).
      authorizer: adminJwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "EscrowRefundInt",
        escrowFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/escrow/{id}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "EscrowGetInt",
        escrowFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    httpApi.addRoutes({
      path: "/escrow/history",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "EscrowHistoryInt",
        escrowFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // F-33: fetch the escrow for a delivery. Distinct 3-segment path so it does
    // not collide with /escrow/{id}; server enforces party-or-admin (ESCROW_022).
    httpApi.addRoutes({
      path: "/escrow/by-delivery/{deliveryId}",
      methods: [apigateway.HttpMethod.GET],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "EscrowByDeliveryInt",
        escrowFn.function,
        {
          payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
        },
      ),
    });

    // ─── Telebirr live env wiring + public webhook route (gated) ──────
    // Notify/callback URL and secret access are attached here (after the API
    // exists so we can derive the public URL). The escrow/cron Lambdas make the
    // outbound call; the webhook Lambda receives the callback.
    if (enableTelebirrLive && telebirrSecret) {
      const notifyUrl = `${httpApi.apiEndpoint}/webhooks/telebirr`;
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
        httpApi.addRoutes({
          path: "/webhooks/telebirr",
          methods: [apigateway.HttpMethod.POST],
          // Public: NO authorizer. Telebirr authenticates via its RSA signature,
          // which TelebirrWebhookFunction verifies (+ optional IP allow-list).
          integration: new integrations.HttpLambdaIntegration(
            "TelebirrWebhookInt",
            telebirrWebhookFn.function,
            {
              payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
            },
          ),
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
        PAGINATION_SECRET_ARN: paginationSecret.reference,
      },
      timeout: cdk.Duration.seconds(30),
      memorySize: 512,
      reservedConcurrentExecutions: 10,
    });

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

    // Write access on tables the admin mutates
    props.escrowsTable.grantWriteData(adminFn.function);
    // ESC-03: a dispute verdict moves the order and delivery to it. UpdateItem only:
    // the admin Lambda never puts or deletes rows in these tables.
    props.ordersTable.grant(adminFn.function, "dynamodb:UpdateItem");
    props.deliveriesTable.grant(adminFn.function, "dynamodb:UpdateItem");
    props.escrowAuditTable.grantWriteData(adminFn.function);
    props.fraudSignalsTable.grantReadWriteData(adminFn.function);
    props.usersTable.grantWriteData(adminFn.function);
    // Transaction audit: admin actions are logged here, and the monitor +
    // audit-log read it (read for the timeline/feed, write for admin actions).
    props.transactionAuditTable.grantReadWriteData(adminFn.function);

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

    const adminIntegration = new integrations.HttpLambdaIntegration(
      "AdminInt",
      adminFn.function,
      {
        payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
      },
    );

    // GET routes
    const adminGetPaths = [
      "/admin/dashboard/stats",
      "/admin/escrows",
      "/admin/escrows/{id}",
      "/admin/escrows/{id}/audit",
      "/admin/disputes",
      "/admin/fraud-signals",
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
    ];

    for (const path of adminGetPaths) {
      const integId = `Admin${path
        .replace("/admin/", "")
        .replace(/[/{}-]/g, "_")}GetInt`;
      httpApi.addRoutes({
        path,
        methods: [apigateway.HttpMethod.GET],
        authorizer: adminJwtAuthorizer,
        integration: new integrations.HttpLambdaIntegration(
          integId,
          adminFn.function,
          {
            payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
          },
        ),
      });
    }

    // POST routes
    const adminPostPaths = [
      "/admin/disputes/{id}/resolve",
      "/admin/fraud-signals/{id}/action",
      "/admin/users/{id}/action",
      // ESC-02: record an admin-paid manual payout leg (admin group checked in the Lambda).
      "/admin/payouts/{escrowId}/{ref}/complete",
    ];

    for (const path of adminPostPaths) {
      const integId = `Admin${path
        .replace("/admin/", "")
        .replace(/[/{}-]/g, "_")}PostInt`;
      httpApi.addRoutes({
        path,
        methods: [apigateway.HttpMethod.POST],
        authorizer: adminJwtAuthorizer,
        integration: new integrations.HttpLambdaIntegration(
          integId,
          adminFn.function,
          {
            payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
          },
        ),
      });
    }

    // ─── API Gateway Throttling + Access Logs ────────────────────────
    const defaultStage = httpApi.defaultStage?.node
      .defaultChild as apigateway.CfnStage;
    if (defaultStage) {
      defaultStage.addPropertyOverride("DefaultRouteSettings", {
        ThrottlingBurstLimit: 50,
        ThrottlingRateLimit: 25,
      });

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
 * T-SEC-01/02: CDK Aspect that scans every Lambda in the stack and errors
 * if DEV_FAYDA_BYPASS, TELEBIRR_MOCK, or DEV_OTP_BYPASS are set in a production deployment.
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
