import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as apigateway from 'aws-cdk-lib/aws-apigatewayv2';
import * as integrations from 'aws-cdk-lib/aws-apigatewayv2-integrations';
import * as cognito from 'aws-cdk-lib/aws-cognito';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { WeredaLambda } from '../constructs/lambda-construct';
import { EnvironmentConfig } from '../config/environments';

export interface ApiStackProps extends cdk.StackProps {
  readonly envConfig: EnvironmentConfig;
  readonly usersTable: dynamodb.Table;
  readonly cropsTable: dynamodb.Table;
  readonly tripsTable: dynamodb.Table;
  readonly rateLimitsTable: dynamodb.Table;
  readonly syncMetadataTable: dynamodb.Table;
  readonly deliveriesTable: dynamodb.Table;
  readonly paymentsTable: dynamodb.Table;
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
    this.userPool = new cognito.UserPool(this, 'UserPool', {
      userPoolName: `${prefix}-users`,
      selfSignUpEnabled: true,
      signInAliases: { phone: true },
      autoVerify: { phone: true },
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
      removalPolicy: envConfig.isProd ? cdk.RemovalPolicy.RETAIN : cdk.RemovalPolicy.DESTROY,
    });

    this.userPoolClient = this.userPool.addClient('MobileClient', {
      userPoolClientName: `${prefix}-mobile`,
      authFlows: {
        userSrp: true,
        userPassword: true,
      },
      accessTokenValidity: cdk.Duration.minutes(15),
      refreshTokenValidity: cdk.Duration.days(30),
      idTokenValidity: cdk.Duration.minutes(15),
      preventUserExistenceErrors: true,
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

    const healthCheck = new WeredaLambda(this, 'HealthCheck', {
      functionName: `${prefix}-health`,
      description: 'Health check endpoint',
      handlerName: 'HealthCheckFunction',
    });
    props.usersTable.grantReadData(healthCheck.function);

    const registerFn = new WeredaLambda(this, 'RegisterFunction', {
      functionName: `${prefix}-auth-register`,
      description: 'POST /auth/register — user registration with phone OTP',
      handlerName: 'RegisterFunction',
      environment: cognitoEnv,
    });
    props.usersTable.grantWriteData(registerFn.function);
    props.rateLimitsTable.grantReadWriteData(registerFn.function);
    this.grantCognito(registerFn.function, ['cognito-idp:SignUp', 'cognito-idp:AdminGetUser']);

    const verifyOtpFn = new WeredaLambda(this, 'VerifyOtpFunction', {
      functionName: `${prefix}-auth-verify-otp`,
      description: 'POST /auth/verify-otp — confirm phone number with OTP',
      handlerName: 'VerifyOtpFunction',
      environment: cognitoEnv,
    });
    props.usersTable.grantReadWriteData(verifyOtpFn.function);
    props.rateLimitsTable.grantReadWriteData(verifyOtpFn.function);
    this.grantCognito(verifyOtpFn.function, [
      'cognito-idp:ConfirmSignUp',
      'cognito-idp:AdminConfirmSignUp',
      'cognito-idp:ResendConfirmationCode',
    ]);

    const loginFn = new WeredaLambda(this, 'LoginFunction', {
      functionName: `${prefix}-auth-login`,
      description: 'POST /auth/login — authenticate with phone + password',
      handlerName: 'LoginFunction',
      environment: cognitoEnv,
    });
    props.usersTable.grantReadData(loginFn.function);
    props.rateLimitsTable.grantReadWriteData(loginFn.function);
    this.grantCognito(loginFn.function, [
      'cognito-idp:InitiateAuth',
      'cognito-idp:AdminInitiateAuth',
      'cognito-idp:RespondToAuthChallenge',
    ]);

    const refreshFn = new WeredaLambda(this, 'RefreshFunction', {
      functionName: `${prefix}-auth-refresh`,
      description: 'POST /auth/refresh — refresh access token',
      handlerName: 'RefreshFunction',
      environment: cognitoOnlyEnv,
    });
    this.grantCognito(refreshFn.function, [
      'cognito-idp:InitiateAuth',
      'cognito-idp:AdminInitiateAuth',
    ]);

    const logoutFn = new WeredaLambda(this, 'LogoutFunction', {
      functionName: `${prefix}-auth-logout`,
      description: 'POST /auth/logout — global sign-out (protected)',
      handlerName: 'LogoutFunction',
      environment: cognitoOnlyEnv,
    });
    this.grantCognito(logoutFn.function, [
      'cognito-idp:GlobalSignOut',
      'cognito-idp:AdminUserGlobalSignOut',
    ]);

    // ─── Sync Function ───────────────────────────────────────────────

    const syncFn = new WeredaLambda(this, 'SyncFunction', {
      functionName: `${prefix}-sync`,
      description: 'POST /sync — offline-first sync engine batch processor',
      handlerName: 'SyncFunction',
      environment: {
        SYNC_METADATA_TABLE: props.syncMetadataTable.tableName,
        CROPS_TABLE: props.cropsTable.tableName,
        USERS_TABLE: props.usersTable.tableName,
      },
      timeout: cdk.Duration.seconds(30),
    });
    // Sync needs read-write on entity tables and sync metadata
    props.syncMetadataTable.grantReadWriteData(syncFn.function);
    props.cropsTable.grantReadWriteData(syncFn.function);
    props.usersTable.grantReadData(syncFn.function);

    // ─── Crop Function ───────────────────────────────────────────────

    const cropFn = new WeredaLambda(this, 'CropFunction', {
      functionName: `${prefix}-crops`,
      description: 'GET /crops — browse crop listings (read-only)',
      handlerName: 'CropFunction',
      environment: {
        CROPS_TABLE: props.cropsTable.tableName,
      },
      timeout: cdk.Duration.seconds(10),
      memorySize: 256,
    });
    // Crop function needs read-only access to crops table
    props.cropsTable.grantReadData(cropFn.function);

    // ─── HTTP API Gateway ────────────────────────────────────────────

    const httpApi = new apigateway.HttpApi(this, 'HttpApi', {
      apiName: `${prefix}-api`,
      description: 'WeredaConnect HTTP API — optimized for Ethiopian context',
      corsPreflight: {
        allowOrigins: ['*'],
        allowMethods: [apigateway.CorsHttpMethod.POST, apigateway.CorsHttpMethod.GET],
        allowHeaders: ['Content-Type', 'Authorization'],
      },
    });

    this.httpApiUrl = httpApi.apiEndpoint;

    // ─── Routes ──────────────────────────────────────────────────────

    httpApi.addRoutes({
      path: '/health',
      methods: [apigateway.HttpMethod.GET],
      integration: new integrations.HttpLambdaIntegration('HealthInt', healthCheck.function),
    });

    httpApi.addRoutes({
      path: '/auth/register',
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration('RegisterInt', registerFn.function),
    });

    httpApi.addRoutes({
      path: '/auth/verify-otp',
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration('VerifyOtpInt', verifyOtpFn.function),
    });

    httpApi.addRoutes({
      path: '/auth/login',
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration('LoginInt', loginFn.function),
    });

    httpApi.addRoutes({
      path: '/auth/refresh',
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration('RefreshInt', refreshFn.function),
    });

    httpApi.addRoutes({
      path: '/auth/logout',
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration('LogoutInt', logoutFn.function),
    });

    httpApi.addRoutes({
      path: '/sync',
      methods: [apigateway.HttpMethod.POST],
      integration: new integrations.HttpLambdaIntegration('SyncInt', syncFn.function, {
        payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
      }),
    });

    httpApi.addRoutes({
      path: '/crops',
      methods: [apigateway.HttpMethod.GET],
      integration: new integrations.HttpLambdaIntegration('CropsListInt', cropFn.function, {
        payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
      }),
    });

    httpApi.addRoutes({
      path: '/crops/{id}',
      methods: [apigateway.HttpMethod.GET],
      integration: new integrations.HttpLambdaIntegration('CropsGetInt', cropFn.function, {
        payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0,
      }),
    });

    // ─── API Gateway Throttling ──────────────────────────────────────
    const defaultStage = httpApi.defaultStage?.node.defaultChild as apigateway.CfnStage;
    if (defaultStage) {
      defaultStage.addPropertyOverride('DefaultRouteSettings', {
        ThrottlingBurstLimit: 200,
        ThrottlingRateLimit: 100,
      });
    }

    // ─── Outputs ─────────────────────────────────────────────────────
    new cdk.CfnOutput(this, 'ApiUrl', {
      value: this.httpApiUrl,
      description: 'API Gateway URL',
    });

    new cdk.CfnOutput(this, 'UserPoolId', {
      value: this.userPool.userPoolId,
      description: 'Cognito User Pool ID',
    });

    new cdk.CfnOutput(this, 'UserPoolClientId', {
      value: this.userPoolClient.userPoolClientId,
      description: 'Cognito App Client ID',
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
