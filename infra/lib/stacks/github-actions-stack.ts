import * as cdk from "aws-cdk-lib";
import { Construct } from "constructs";
import * as iam from "aws-cdk-lib/aws-iam";
import { EnvironmentConfig } from "../config/environments";

export interface GithubActionsStackProps extends cdk.StackProps {
  readonly envConfig: EnvironmentConfig;
  /**
   * GitHub repository in "{owner}/{repo}" format.
   * Used to scope the OIDC trust policy to this repo only.
   */
  readonly githubRepo: string;
  /** S3 bucket name for the marketing website. */
  readonly websiteBucketName: string;
  /** S3 bucket name for the Flutter web app. */
  readonly webappBucketName: string;
  /** S3 bucket name for the admin dashboard. */
  readonly adminBucketName: string;
  /** CloudFront distribution ID for the marketing website. */
  readonly websiteDistributionId: string;
  /** CloudFront distribution ID for the Flutter web app. */
  readonly webappDistributionId: string;
  /** CloudFront distribution ID for the admin dashboard. */
  readonly adminDistributionId: string;
}

/**
 * GitHub Actions OIDC deploy role for WeredaConnect CI/CD.
 *
 * Creates an IAM role that GitHub Actions can assume via OIDC (no long-lived keys).
 * The role is scoped to the specific S3 buckets and CloudFront distributions for
 * this environment.
 *
 * IMPORTANT: If the role `wereda-connect-{env}-github-deploy` was created manually,
 * delete it before deploying this stack, or use `cdk import` to adopt it.
 *
 * Required S3 permissions for `aws s3 sync --delete`:
 *   s3:ListBucket  — on the bucket ARN (not bucket/*)
 *   s3:GetObject, s3:PutObject, s3:DeleteObject — on bucket/*
 */
export class GithubActionsStack extends cdk.Stack {
  public readonly deployRoleArn: string;

  constructor(scope: Construct, id: string, props: GithubActionsStackProps) {
    super(scope, id, props);

    const {
      envConfig,
      githubRepo,
      websiteBucketName,
      webappBucketName,
      adminBucketName,
      websiteDistributionId,
      webappDistributionId,
      adminDistributionId,
    } = props;
    const prefix = `wereda-connect-${envConfig.name}`;

    const bucketNames = [websiteBucketName, webappBucketName, adminBucketName];
    const distributionIds = [
      websiteDistributionId,
      webappDistributionId,
      adminDistributionId,
    ];

    // GitHub Actions OIDC provider is a singleton per account.
    // Import it by its predictable ARN — do not create a duplicate.
    const oidcProvider = iam.OpenIdConnectProvider.fromOpenIdConnectProviderArn(
      this,
      "GithubOidcProvider",
      `arn:aws:iam::${this.account}:oidc-provider/token.actions.githubusercontent.com`,
    );

    // Deploy role — assumed by GitHub Actions via OIDC
    const deployRole = new iam.Role(this, "DeployRole", {
      roleName: `${prefix}-github-deploy`,
      description: `GitHub Actions deploy role for WeredaConnect ${envConfig.name} - S3 + CloudFront`,
      assumedBy: new iam.WebIdentityPrincipal(
        oidcProvider.openIdConnectProviderArn,
        {
          StringEquals: {
            "token.actions.githubusercontent.com:aud": "sts.amazonaws.com",
          },
          StringLike: {
            // Scoped to all branches/tags/PRs in this repo
            "token.actions.githubusercontent.com:sub": `repo:${githubRepo}:*`,
          },
        },
      ),
      maxSessionDuration: cdk.Duration.hours(1),
    });

    // S3 bucket-level: ListBucket is required by `aws s3 sync` to compare
    // local vs remote before uploading. Applied to bucket ARN (not bucket/*).
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        sid: "S3ListBuckets",
        effect: iam.Effect.ALLOW,
        actions: ["s3:ListBucket"],
        resources: bucketNames.map((name) => `arn:aws:s3:::${name}`),
      }),
    );

    // S3 object-level: get/put/delete for sync and cp operations
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        sid: "S3SyncObjects",
        effect: iam.Effect.ALLOW,
        actions: ["s3:GetObject", "s3:PutObject", "s3:DeleteObject"],
        resources: bucketNames.map((name) => `arn:aws:s3:::${name}/*`),
      }),
    );

    // CloudFront: CreateInvalidation scoped to these distributions only
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        sid: "CloudFrontInvalidate",
        effect: iam.Effect.ALLOW,
        actions: ["cloudfront:CreateInvalidation"],
        resources: distributionIds.map(
          (id) =>
            `arn:aws:cloudfront::${this.account}:distribution/${id}`,
        ),
      }),
    );

    // CDK deploys (server Lambdas + infrastructure, via the deploy-server
    // workflow). Rather than granting broad CloudFormation/Lambda/IAM rights to
    // this CI principal, allow it to assume the CDK bootstrap roles — those hold
    // the real deploy permissions. Also allow reading the bootstrap version SSM
    // parameter, which the CLI checks before assuming. Qualifier `hnb659fds` is
    // the CDK bootstrap default.
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        sid: "AssumeCdkBootstrapRoles",
        effect: iam.Effect.ALLOW,
        actions: ["sts:AssumeRole"],
        resources: [`arn:aws:iam::${this.account}:role/cdk-hnb659fds-*`],
      }),
    );
    deployRole.addToPolicy(
      new iam.PolicyStatement({
        sid: "ReadCdkBootstrapVersion",
        effect: iam.Effect.ALLOW,
        actions: ["ssm:GetParameter"],
        resources: [
          `arn:aws:ssm:*:${this.account}:parameter/cdk-bootstrap/hnb659fds/version`,
        ],
      }),
    );

    // Non-prod only: the deploy workflow seeds SSM SecureString secrets
    // (scripts/seed-ssm-secrets.sh) before `cdk deploy`, copying values out of
    // the legacy Secrets Manager secrets on first migration. Scoped to the
    // four secret paths the script touches — NOT parameter/${prefix}/* — so
    // this role cannot rewrite the CDK-managed config parameters (commission
    // rate, dispute window, …). Prod secrets stay in Secrets Manager and are
    // never readable by this role.
    if (!envConfig.isProd) {
      const seededSecretPaths = [
        "pagination-secret",
        "fayda/client-secret",
        "fayda/signing-secret",
        "telebirr/credentials",
      ];
      deployRole.addToPolicy(
        new iam.PolicyStatement({
          sid: "SeedSsmSecrets",
          effect: iam.Effect.ALLOW,
          actions: ["ssm:GetParameter", "ssm:PutParameter"],
          resources: seededSecretPaths.map(
            (p) => `arn:aws:ssm:*:${this.account}:parameter/${prefix}/${p}`,
          ),
        }),
      );
      deployRole.addToPolicy(
        new iam.PolicyStatement({
          sid: "ReadLegacySecretsForMigration",
          effect: iam.Effect.ALLOW,
          actions: ["secretsmanager:GetSecretValue"],
          resources: seededSecretPaths.map(
            (p) =>
              `arn:aws:secretsmanager:*:${this.account}:secret:/${prefix}/${p}-??????`,
          ),
        }),
      );
    }

    // Non-prod only: the deploy workflow seeds the dashboard's test admin
    // (scripts/seed-admin-user.sh, SIM-18) after `cdk deploy`. The admin pool
    // id is only known to the API stack, and this stack deploys FIRST
    // (`--exclusively`, see deploy-server.yml) so a new cross-stack export
    // would break that ordering on its first run; scope to this account's
    // pools in this region instead. Prod admins are created by hand (MFA).
    if (!envConfig.isProd) {
      deployRole.addToPolicy(
        new iam.PolicyStatement({
          sid: "SeedAdminUser",
          effect: iam.Effect.ALLOW,
          actions: [
            "cognito-idp:AdminGetUser",
            "cognito-idp:AdminCreateUser",
            "cognito-idp:AdminSetUserPassword",
            "cognito-idp:AdminAddUserToGroup",
            // Reset the TOTP preference so the e2e enrols a fresh token, and
            // read the groups back when login answers with an MFA challenge.
            "cognito-idp:AdminSetUserMFAPreference",
            "cognito-idp:AdminListGroupsForUser",
          ],
          resources: [
            `arn:aws:cognito-idp:${this.region}:${this.account}:userpool/*`,
          ],
        }),
      );
    }

    this.deployRoleArn = deployRole.roleArn;

    new cdk.CfnOutput(this, "DeployRoleArn", {
      value: deployRole.roleArn,
      description:
        "GitHub Actions deploy role ARN — set as AWS_DEPLOY_ROLE_ARN in each GitHub environment",
    });
  }
}
