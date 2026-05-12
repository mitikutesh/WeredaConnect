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

    this.deployRoleArn = deployRole.roleArn;

    new cdk.CfnOutput(this, "DeployRoleArn", {
      value: deployRole.roleArn,
      description:
        "GitHub Actions deploy role ARN — set as AWS_DEPLOY_ROLE_ARN in each GitHub environment",
    });
  }
}
