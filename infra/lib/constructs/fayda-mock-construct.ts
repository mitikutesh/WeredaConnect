import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as iam from 'aws-cdk-lib/aws-iam';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as path from 'path';

export interface FaydaMockConstructProps {
  /** Resource name prefix, e.g. "wereda-dev". */
  readonly prefix: string;
  /** Removal policy for the codes table and log group. */
  readonly removalPolicy: cdk.RemovalPolicy;
  /** Exact redirect URIs the mock accepts. Defaults to the mobile deep link. */
  readonly allowedRedirectUris?: readonly string[];
  /** Fixed issuer URL (e.g. a custom domain). Defaults to the Function URL. */
  readonly issuerUrlOverride?: string;
  /**
   * Pass the environment's isProd. The construct throws when true, because a
   * mock identity provider must never exist in production.
   */
  readonly isProd?: boolean;
}

/**
 * Mock VeriFayda eSignet OIDC provider for dev (tools/fayda-mock), served by a
 * public Lambda Function URL. The Function URL root is the OIDC issuer, and the
 * endpoints use the live Fayda paths (/v1/esignet/...).
 *
 * Two keys live in SSM SecureStrings that CDK never creates, so no private key
 * appears in a CloudFormation template; the Lambda generates each on first use:
 * - `/${prefix}/fayda-mock/signing-key`: the mock's own ID token / userinfo key.
 * - `/${prefix}/fayda/client-private-key`: the WeredaConnect relying party key,
 *   the same parameter and base64 JWK format live Fayda uses. The seed script
 *   writes PLACEHOLDER there; the mock replaces it with a generated key, verifies
 *   private_key_jwt assertions with its public half, and the server signs with it.
 *   A usable key already there (such as a real Fayda key) is kept.
 */
export class FaydaMockConstruct extends Construct {
  /** OIDC issuer: the Function URL without a trailing slash. */
  public readonly issuerUrl: string;
  public readonly clientId: string;
  /** SSM SecureString with the relying party private key (base64 JWK); the mock fills a PLACEHOLDER. */
  public readonly clientPrivateKeyParameterName: string;
  public readonly function: lambda.Function;
  public readonly functionUrl: lambda.FunctionUrl;
  public readonly codesTable: dynamodb.Table;
  public readonly signingKeyParameterName: string;

  constructor(scope: Construct, id: string, props: FaydaMockConstructProps) {
    super(scope, id);

    if (props.isProd) {
      throw new Error('FaydaMockConstruct must never be deployed to a production environment.');
    }

    const { prefix } = props;
    this.clientId = `weredaconnect-${prefix}`;
    this.clientPrivateKeyParameterName = `/${prefix}/fayda/client-private-key`;
    this.signingKeyParameterName = `/${prefix}/fayda-mock/signing-key`;
    const functionName = `${prefix}-fayda-mock`;

    this.codesTable = new dynamodb.Table(this, 'CodesTable', {
      tableName: `${prefix}-fayda-mock-codes`,
      partitionKey: { name: 'code', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      timeToLiveAttribute: 'expiresAt',
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });

    const logGroup = new logs.LogGroup(this, 'LogGroup', {
      logGroupName: `/aws/lambda/${functionName}`,
      retention: logs.RetentionDays.ONE_WEEK,
      removalPolicy: props.removalPolicy,
    });

    const environment: Record<string, string> = {
      CLIENT_ID: this.clientId,
      CLIENT_KEY_PARAM: this.clientPrivateKeyParameterName,
      CODES_TABLE: this.codesTable.tableName,
      SIGNING_KEY_PARAM: this.signingKeyParameterName,
      ALLOWED_REDIRECT_URIS: (props.allowedRedirectUris ?? ['weredaconnect://fayda-callback']).join(','),
      REQUIRE_PKCE: 'true',
      // Marker for ProductionBypassGuard-style checks.
      FAYDA_MOCK: 'true',
    };
    if (props.issuerUrlOverride) {
      environment.ISSUER_URL_OVERRIDE = props.issuerUrlOverride.replace(/\/+$/, '');
    }

    this.function = new lambda.Function(this, 'Function', {
      functionName,
      description: 'Mock Fayda eSignet OIDC provider (dev only)',
      // Node 20 on Lambda was deprecated 2026-04-30; 24 is the current LTS runtime.
      runtime: lambda.Runtime.NODEJS_24_X,
      architecture: lambda.Architecture.ARM_64,
      handler: 'index.handler',
      code: lambda.Code.fromAsset(path.join(__dirname, '..', '..', '..', 'tools', 'fayda-mock'), {
        exclude: ['test', 'node_modules', 'README.md', 'server-local.js', 'package-lock.json'],
      }),
      memorySize: 256,
      // Public Function URL without auth: cap concurrency so a scan cannot run up the bill.
      // Skipped in dev like WeredaLambda: the account limit is 10 total, so any
      // reservation fails the deploy, and that limit already caps the mock.
      ...(this.node.tryGetContext('environment') ?? 'dev') === 'dev'
        ? {}
        : { reservedConcurrentExecutions: 5 },
      timeout: cdk.Duration.seconds(10),
      logGroup,
      environment,
    });

    this.codesTable.grantReadWriteData(this.function);
    const stack = cdk.Stack.of(this);
    this.function.addToRolePolicy(new iam.PolicyStatement({
      actions: ['ssm:GetParameter', 'ssm:PutParameter'],
      // The name's leading slash doubles as the ARN separator after "parameter".
      resources: [this.signingKeyParameterName, this.clientPrivateKeyParameterName].map(
        (name) => `arn:${stack.partition}:ssm:${stack.region}:${stack.account}:parameter${name}`,
      ),
    }));

    this.functionUrl = this.function.addFunctionUrl({
      authType: lambda.FunctionUrlAuthType.NONE,
      cors: {
        allowedOrigins: ['*'],
        allowedMethods: [lambda.HttpMethod.GET, lambda.HttpMethod.POST],
        allowedHeaders: ['content-type', 'authorization'],
      },
    });

    // Function URLs end in "/". Split "https://host/" on "/" and keep the host.
    this.issuerUrl = props.issuerUrlOverride
      ? props.issuerUrlOverride.replace(/\/+$/, '')
      : cdk.Fn.join('', ['https://', cdk.Fn.select(2, cdk.Fn.split('/', this.functionUrl.url))]);
  }
}
