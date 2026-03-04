import * as cdk from "aws-cdk-lib";
import { Construct } from "constructs";
import * as apigateway from "aws-cdk-lib/aws-apigatewayv2";
import { CorsHttpMethod } from "aws-cdk-lib/aws-apigatewayv2";
import * as integrations from "aws-cdk-lib/aws-apigatewayv2-integrations";
import { HttpUserPoolAuthorizer } from "aws-cdk-lib/aws-apigatewayv2-authorizers";
import * as cognito from "aws-cdk-lib/aws-cognito";
import * as dynamodb from "aws-cdk-lib/aws-dynamodb";
import * as events from "aws-cdk-lib/aws-events";
import * as targets from "aws-cdk-lib/aws-events-targets";
import * as iam from "aws-cdk-lib/aws-iam";
import * as s3 from "aws-cdk-lib/aws-s3";
import * as secretsmanager from "aws-cdk-lib/aws-secretsmanager";
import * as ssm from "aws-cdk-lib/aws-ssm";
import * as logs from "aws-cdk-lib/aws-logs";
import { WeredaLambda } from "../constructs/lambda-construct";
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
  readonly escrowIdempotencyTable: dynamodb.Table;
  readonly ordersTable: dynamodb.Table;
  readonly fraudSignalsTable: dynamodb.Table;
  readonly uploadsBucket: s3.Bucket;
}

/**
 * API stack: Cognito User Pool, HTTP API Gateway, Lambda functions, and auth routes.
 * Everything in one stack to avoid cross-stack cyclic references from API routes.
 */
export class ApiStack extends cdk.Stack {
  public readonly httpApiUrl: string;
  public readonly userPool: cognito.UserPool;
  public readonly userPoolClient: cognito.UserPoolClient;

  constructor(scope: Construct, id: string, props: ApiStackProps) {
    super(scope, id, props);

    const { envConfig } = props;
    const prefix = `wereda-connect-${envConfig.name}`;

    // ─── Cognito User Pool ───────────────────────────────────────────
    this.userPool = new cognito.UserPool(this, "UserPool", {
      userPoolName: `${prefix}-users`,
      selfSignUpEnabled: true,
      signInAliases: { phone: true },
      autoVerify: { phone: false },
      mfa: cognito.Mfa.OFF,
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
      passwordPolicy: {
        minLength: 8,
        requireLowercase: true,
        requireUppercase: false,
        requireDigits: true,
        requireSymbols: false,
        tempPasswordValidity: cdk.Duration.days(7),
      },
      removalPolicy: envConfig.isProd
        ? cdk.RemovalPolicy.RETAIN
        : cdk.RemovalPolicy.DESTROY,
    });

    this.userPoolClient = this.userPool.addClient("MobileClient", {
      userPoolClientName: `${prefix}-mobile`,
      authFlows: {
        userSrp: true,
        userPassword: true,
      },
      accessTokenValidity: cdk.Duration.minutes(15),
      refreshTokenValidity: cdk.Duration.days(30),
      idTokenValidity: cdk.Duration.minutes(15),
      preventUserExistenceErrors: true,
      // SEC-VULN-001: Exclude custom:role from client-writable attributes
      // to prevent privilege escalation via Cognito UpdateUserAttributes
      writeAttributes: new cognito.ClientAttributes()
        .withStandardAttributes({ phoneNumber: true })
        .withCustomAttributes("woreda", "displayName", "preferredLanguage"),
    });

    // ─── Shared environment variables ────────────────────────────────
    const cognitoEnv: Record<string, string> = {
      COGNITO_USER_POOL_ID: this.userPool.userPoolId,
      COGNITO_CLIENT_ID: this.userPoolClient.userPoolClientId,
      COGNITO_REGION: this.region,
      USERS_TABLE: props.usersTable.tableName,
      RATE_LIMITS_TABLE: props.rateLimitsTable.tableName,
    };

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
      environment: cognitoEnv,
      reservedConcurrentExecutions: 20,
    });
    props.usersTable.grantWriteData(registerFn.function);
    props.rateLimitsTable.grantReadWriteData(registerFn.function);
    this.grantCognito(registerFn.function, [
      "cognito-idp:SignUp",
      "cognito-idp:AdminGetUser",
      "cognito-idp:AdminDeleteUser",
      "cognito-idp:AdminUpdateUserAttributes",
      "cognito-idp:AdminConfirmSignUp",
      "cognito-idp:InitiateAuth",
    ]);

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
      environment: cognitoOnlyEnv,
      reservedConcurrentExecutions: 20,
    });
    this.grantCognito(refreshFn.function, [
      "cognito-idp:InitiateAuth",
      "cognito-idp:AdminInitiateAuth",
    ]);

    const logoutFn = new WeredaLambda(this, "LogoutFunction", {
      functionName: `${prefix}-auth-logout`,
      description: "POST /auth/logout — global sign-out (protected)",
      handlerName: "LogoutFunction",
      environment: cognitoOnlyEnv,
      reservedConcurrentExecutions: 20,
    });
    this.grantCognito(logoutFn.function, [
      "cognito-idp:RevokeToken",
      "cognito-idp:GlobalSignOut",
      "cognito-idp:AdminUserGlobalSignOut",
    ]);

    // ─── Sync Function ───────────────────────────────────────────────

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
      },
      timeout: cdk.Duration.seconds(30),
      reservedConcurrentExecutions: 10,
    });
    // Sync needs read-write on entity tables and sync metadata
    props.syncMetadataTable.grantReadWriteData(syncFn.function);
    props.cropsTable.grantReadWriteData(syncFn.function);
    props.usersTable.grantReadData(syncFn.function);
    props.tripsTable.grantReadWriteData(syncFn.function);
    props.deliveriesTable.grantReadWriteData(syncFn.function);
    props.ordersTable.grantReadWriteData(syncFn.function);
    props.rateLimitsTable.grantReadWriteData(syncFn.function);
    props.fraudSignalsTable.grantReadWriteData(syncFn.function);

    // ─── Pagination Secret (Secrets Manager) ──────────────────────────
    // SECURITY: Generated at deploy time, never hardcoded in source.

    const paginationSecret = new secretsmanager.Secret(
      this,
      "PaginationSecret",
      {
        secretName: `/${prefix}/pagination-secret`,
        description:
          "HMAC signing key for pagination tokens — auto-generated at deploy time",
        generateSecretString: {
          excludePunctuation: true,
          passwordLength: 64,
        },
      },
    );

    // ─── Crop Function ───────────────────────────────────────────────

    const cropFn = new WeredaLambda(this, "CropFunction", {
      functionName: `${prefix}-crops`,
      description: "GET /crops — browse crop listings (read-only)",
      handlerName: "CropFunction",
      environment: {
        CROPS_TABLE: props.cropsTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.secretArn,
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
        PAGINATION_SECRET_ARN: paginationSecret.secretArn,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    // Trip function needs read-only access to trips table
    props.tripsTable.grantReadData(tripFn.function);
    paginationSecret.grantRead(tripFn.function);

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

    // ─── Fayda SSM Parameters ──────────────────────────────────────────

    const faydaClientIdParam = new ssm.StringParameter(
      this,
      "FaydaClientId",
      {
        parameterName: `/${prefix}/fayda/client-id`,
        stringValue: "PLACEHOLDER",
        description: "Fayda OIDC client ID — update after registration",
      },
    );

    const faydaIssuerUrlParam = new ssm.StringParameter(
      this,
      "FaydaIssuerUrl",
      {
        parameterName: `/${prefix}/fayda/issuer-url`,
        stringValue: "https://fayda.et",
        description: "Fayda OIDC issuer URL",
      },
    );

    // ─── Fayda Secrets (Secrets Manager) ─────────────────────────────

    const faydaClientSecret = new secretsmanager.Secret(
      this,
      "FaydaClientSecret",
      {
        secretName: `/${prefix}/fayda/client-secret`,
        description:
          "Fayda OIDC client secret — update after registration",
        generateSecretString: {
          excludePunctuation: true,
          passwordLength: 64,
        },
      },
    );

    const faydaSigningSecret = new secretsmanager.Secret(
      this,
      "FaydaSigningSecret",
      {
        secretName: `/${prefix}/fayda/signing-secret`,
        description:
          "HMAC signing key for Fayda verification JWTs — auto-generated at deploy time",
        generateSecretString: {
          excludePunctuation: true,
          passwordLength: 64,
        },
      },
    );

    // Grant RegisterFunction access to Fayda signing secret
    registerFn.function.addEnvironment(
      "FAYDA_SIGNING_SECRET_ARN",
      faydaSigningSecret.secretArn,
    );
    faydaSigningSecret.grantRead(registerFn.function);

    // ─── FaydaVerify Function ────────────────────────────────────────

    const faydaVerifyFn = new WeredaLambda(this, "FaydaVerifyFunction", {
      functionName: `${prefix}-auth-fayda-verify`,
      description:
        "POST /auth/fayda-verify — verify Fayda OIDC authorization code",
      handlerName: "FaydaVerifyFunction",
      environment: {
        USERS_TABLE: props.usersTable.tableName,
        FAYDA_CLIENT_ID_PARAM: faydaClientIdParam.parameterName,
        FAYDA_ISSUER_URL_PARAM: faydaIssuerUrlParam.parameterName,
        FAYDA_CLIENT_SECRET_ARN: faydaClientSecret.secretArn,
        FAYDA_SIGNING_SECRET_ARN: faydaSigningSecret.secretArn,
        ...(envConfig.name === 'dev' ? { DEV_FAYDA_BYPASS: "true" } : {}),
      },
      reservedConcurrentExecutions: 20,
    });
    props.usersTable.grantReadData(faydaVerifyFn.function);
    faydaClientIdParam.grantRead(faydaVerifyFn.function);
    faydaIssuerUrlParam.grantRead(faydaVerifyFn.function);
    faydaClientSecret.grantRead(faydaVerifyFn.function);
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
        PAGINATION_SECRET_ARN: paginationSecret.secretArn,
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
        COMMISSION_RATE_PARAM: commissionRateParam.parameterName,
        DISPUTE_WINDOW_PARAM: disputeWindowParam.parameterName,
        ESCROW_TIMEOUT_PARAM: escrowTimeoutParam.parameterName,
        PAGINATION_SECRET_ARN: paginationSecret.secretArn,
        ...(envConfig.isProd ? {} : { TELEBIRR_MOCK: "true" }),
      },
      timeout: cdk.Duration.seconds(30),
      memorySize: 512,
      logRetention: logs.RetentionDays.ONE_YEAR,
      reservedConcurrentExecutions: 15,
    });
    props.escrowsTable.grantReadWriteData(escrowFn.function);
    props.escrowAuditTable.grantWriteData(escrowFn.function);
    props.escrowIdempotencyTable.grantReadWriteData(escrowFn.function);
    props.deliveriesTable.grantReadData(escrowFn.function);
    props.ordersTable.grantReadData(escrowFn.function);
    props.usersTable.grantReadData(escrowFn.function);
    props.cropsTable.grantReadData(escrowFn.function);
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
        COMMISSION_RATE_PARAM: commissionRateParam.parameterName,
        DISPUTE_WINDOW_PARAM: disputeWindowParam.parameterName,
        ESCROW_TIMEOUT_PARAM: escrowTimeoutParam.parameterName,
        ...(envConfig.isProd ? {} : { TELEBIRR_MOCK: "true" }),
      },
      timeout: cdk.Duration.minutes(5),
      memorySize: 512,
      logRetention: logs.RetentionDays.ONE_YEAR,
      reservedConcurrentExecutions: 2,
    });
    props.escrowsTable.grantReadWriteData(escrowCronFn.function);
    props.escrowAuditTable.grantWriteData(escrowCronFn.function);
    props.escrowIdempotencyTable.grantReadWriteData(escrowCronFn.function);
    props.deliveriesTable.grantReadData(escrowCronFn.function);
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
          event: events.RuleTargetInput.fromObject({
            action: "auto_release_and_refund",
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
          event: events.RuleTargetInput.fromObject({
            action: "reconciliation",
          }),
        }),
      ],
    });

    // ─── Order Function ──────────────────────────────────────────────

    const orderFn = new WeredaLambda(this, "OrderFunction", {
      functionName: `${prefix}-orders`,
      description: "GET /orders — browse orders (read-only)",
      handlerName: "OrderFunction",
      environment: {
        ORDERS_TABLE: props.ordersTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.secretArn,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
      reservedConcurrentExecutions: 10,
    });
    props.ordersTable.grantReadData(orderFn.function);
    paginationSecret.grantRead(orderFn.function);

    // ─── Order Cron Function (auto-cancel expired unpaid orders) ────

    const orderCronFn = new WeredaLambda(this, "OrderCronFunction", {
      functionName: `${prefix}-order-cron`,
      description:
        "Scheduled order cleanup: auto-cancel expired unpaid orders",
      handlerName: "OrderCronFunction",
      environment: {
        ORDERS_TABLE: props.ordersTable.tableName,
      },
      timeout: cdk.Duration.minutes(5),
      memorySize: 256,
      reservedConcurrentExecutions: 2,
    });
    props.ordersTable.grantReadWriteData(orderCronFn.function);

    // EventBridge: auto-cancel expired orders every hour
    new events.Rule(this, "OrderCronHourly", {
      ruleName: `${prefix}-order-cron-hourly`,
      description: "Auto-cancel expired unpaid orders every hour",
      schedule: events.Schedule.rate(cdk.Duration.hours(1)),
      targets: [
        new targets.LambdaFunction(orderCronFn.function, {
          event: events.RuleTargetInput.fromObject({
            action: "auto_cancel_expired",
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
        ];

    const httpApi = new apigateway.HttpApi(this, "HttpApi", {
      apiName: `${prefix}-api`,
      description: "WeredaConnect HTTP API — optimized for Ethiopian context",
      corsPreflight: {
        allowOrigins: corsOrigins,
        allowHeaders: ["Content-Type", "Authorization", "X-Request-Id"],
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

    // ─── JWT Authorizer ──────────────────────────────────────────────
    const jwtAuthorizer = new HttpUserPoolAuthorizer(
      "CognitoAuthorizer",
      this.userPool,
      {
        userPoolClients: [this.userPoolClient],
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
      ),
    });

    httpApi.addRoutes({
      path: "/auth/fayda-verify",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "FaydaVerifyInt",
        faydaVerifyFn.function,
      ),
    });

    httpApi.addRoutes({
      path: "/auth/login",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "LoginInt",
        loginFn.function,
      ),
    });

    httpApi.addRoutes({
      path: "/auth/refresh",
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration(
        "RefreshInt",
        refreshFn.function,
      ),
    });

    httpApi.addRoutes({
      path: "/auth/logout",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
      integration: new integrations.HttpLambdaIntegration(
        "LogoutInt",
        logoutFn.function,
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

    httpApi.addRoutes({
      path: "/escrow/release",
      methods: [apigateway.HttpMethod.POST],
      authorizer: jwtAuthorizer,
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
      authorizer: jwtAuthorizer,
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

    // ─── Admin Cognito Group ──────────────────────────────────────────
    new cognito.CfnUserPoolGroup(this, "AdminGroup", {
      userPoolId: this.userPool.userPoolId,
      groupName: "admin",
      description: "Platform administrators with access to /admin/* endpoints",
    });

    // ─── Admin App Client (longer token validity) ─────────────────────
    const adminClient = this.userPool.addClient("AdminClient", {
      userPoolClientName: `${prefix}-admin`,
      authFlows: {
        userSrp: true,
        userPassword: true,
      },
      accessTokenValidity: cdk.Duration.minutes(60),
      refreshTokenValidity: cdk.Duration.days(7),
      idTokenValidity: cdk.Duration.minutes(60),
      preventUserExistenceErrors: true,
      writeAttributes: new cognito.ClientAttributes()
        .withStandardAttributes({ phoneNumber: true })
        .withCustomAttributes("woreda", "displayName", "preferredLanguage"),
    });

    // ─── Admin Lambda Function ────────────────────────────────────────

    const adminFn = new WeredaLambda(this, "AdminFunction", {
      functionName: `${prefix}-admin`,
      description:
        "Admin endpoints: dashboard stats, escrow management, fraud review, user management",
      handlerName: "AdminFunction",
      environment: {
        ESCROWS_TABLE: props.escrowsTable.tableName,
        ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
        FRAUD_SIGNALS_TABLE: props.fraudSignalsTable.tableName,
        USERS_TABLE: props.usersTable.tableName,
        CROPS_TABLE: props.cropsTable.tableName,
        TRIPS_TABLE: props.tripsTable.tableName,
        ORDERS_TABLE: props.ordersTable.tableName,
        DELIVERIES_TABLE: props.deliveriesTable.tableName,
        PAYMENTS_TABLE: props.paymentsTable.tableName,
        PAGINATION_SECRET_ARN: paginationSecret.secretArn,
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
    props.escrowAuditTable.grantWriteData(adminFn.function);
    props.fraudSignalsTable.grantReadWriteData(adminFn.function);
    props.usersTable.grantWriteData(adminFn.function);

    // Cognito admin permissions for user management
    this.grantCognito(adminFn.function, [
      "cognito-idp:AdminGetUser",
      "cognito-idp:AdminDisableUser",
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
    ];

    for (const path of adminGetPaths) {
      const integId = `Admin${path
        .replace("/admin/", "")
        .replace(/[/{}-]/g, "_")}GetInt`;
      httpApi.addRoutes({
        path,
        methods: [apigateway.HttpMethod.GET],
        authorizer: jwtAuthorizer,
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
    ];

    for (const path of adminPostPaths) {
      const integId = `Admin${path
        .replace("/admin/", "")
        .replace(/[/{}-]/g, "_")}PostInt`;
      httpApi.addRoutes({
        path,
        methods: [apigateway.HttpMethod.POST],
        authorizer: jwtAuthorizer,
        integration: new integrations.HttpLambdaIntegration(
          integId,
          adminFn.function,
          {
            payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
          },
        ),
      });
    }

    // ─── API Gateway Throttling ──────────────────────────────────────
    const defaultStage = httpApi.defaultStage?.node
      .defaultChild as apigateway.CfnStage;
    if (defaultStage) {
      defaultStage.addPropertyOverride("DefaultRouteSettings", {
        ThrottlingBurstLimit: 50,
        ThrottlingRateLimit: 25,
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

    new cdk.CfnOutput(this, "AdminClientId", {
      value: adminClient.userPoolClientId,
      description: "Cognito Admin App Client ID",
    });
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
