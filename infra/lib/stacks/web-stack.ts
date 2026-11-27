import * as cdk from "aws-cdk-lib";
import { Construct } from "constructs";
import * as s3 from "aws-cdk-lib/aws-s3";
import * as cloudfront from "aws-cdk-lib/aws-cloudfront";
import * as origins from "aws-cdk-lib/aws-cloudfront-origins";
import * as acm from "aws-cdk-lib/aws-certificatemanager";
import { EnvironmentConfig } from "../config/environments";

export interface WebStackProps extends cdk.StackProps {
  readonly envConfig: EnvironmentConfig;
  readonly certificate: acm.ICertificate;
}

/**
 * Web stack: S3 buckets + CloudFront distributions for website, webapp, and admin.
 *
 * Deployed in eu-north-1 (Stockholm) for dev; me-south-1 (Bahrain) for staging/prod. The ACM certificate
 * is created in a separate stack in us-east-1 (hard AWS requirement for CloudFront)
 * and passed here via cross-region references.
 */
export class WebStack extends cdk.Stack {
  public readonly websiteDistributionId: string;
  public readonly webappDistributionId: string;
  public readonly adminDistributionId: string;
  public readonly websiteBucketName: string;
  public readonly webappBucketName: string;
  public readonly adminBucketName: string;

  constructor(scope: Construct, id: string, props: WebStackProps) {
    super(scope, id, props);

    const { envConfig, certificate } = props;
    const prefix = `wereda-connect-${envConfig.name}`;
    // S3 bucket names are globally unique. Dev was previously deployed in me-south-1;
    // adding the region prevents name collisions when redeploying dev in eu-north-1.
    // Staging/prod keep their existing names (already deployed, must not change).
    const bucketPrefix =
      envConfig.name === "dev" ? `${prefix}-${envConfig.region}` : prefix;
    const { domain } = envConfig;

    // ─── CloudFront Function: rewrite /path/ → /path/index.html ───
    const urlRewriteFunction = new cloudfront.Function(
      this,
      "UrlRewriteFunction",
      {
        functionName: `${prefix}-url-rewrite`,
        code: cloudfront.FunctionCode.fromInline(`
function handler(event) {
  var request = event.request;
  var uri = request.uri;
  if (uri.endsWith('/')) {
    request.uri += 'index.html';
  } else if (!uri.includes('.')) {
    request.uri += '/index.html';
  }
  return request;
}
`),
      },
    );

    // ─── Security Headers Policy ──────────────────────────────────
    // Regional S3 endpoints only: virtual-hosted (bucket.s3.<region>) and
    // path style (s3.<region>), never every bucket on s3.amazonaws.com.
    const s3Sources = `https://*.s3.${envConfig.region}.amazonaws.com https://s3.${envConfig.region}.amazonaws.com`;
    const securityHeaders = new cloudfront.ResponseHeadersPolicy(
      this,
      "SecurityHeaders",
      {
        responseHeadersPolicyName: `${prefix}-security-headers`,
        securityHeadersBehavior: {
          contentSecurityPolicy: {
            // connect-src is region-scoped: dev=eu-north-1, staging/prod=me-south-1.
            // Hardcoding eu-north-1 here would block the API + Cognito on me-south-1.
            // Crop photos (crop-listing-api 1.6.0) need two more kinds of source:
            //   blob:  image_picker on web hands back a blob: URL and
            //          XFile.readAsBytes() fetches it, which connect-src governs.
            //   S3     the presigned PUT of an original and the presigned GETs of
            //          thumb/full renditions go straight to the regional bucket
            //          (virtual-hosted style). CanvasKit loads images with XHR, so
            //          the GETs count as connect-src; img-src covers any image
            //          drawn through an <img> element instead.
            contentSecurityPolicy:
              `default-src 'self'; script-src 'self' 'unsafe-inline' 'wasm-unsafe-eval' https://www.gstatic.com; style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; img-src 'self' data: blob: ${s3Sources}; font-src 'self' https://fonts.gstatic.com; connect-src 'self' blob: https://*.weredaconnect.com https://*.execute-api.${envConfig.region}.amazonaws.com https://www.gstatic.com https://fonts.gstatic.com https://fonts.googleapis.com https://cognito-idp.${envConfig.region}.amazonaws.com ${s3Sources}; worker-src 'self' blob:; frame-ancestors 'none'`,
            override: true,
          },
          strictTransportSecurity: {
            accessControlMaxAge: cdk.Duration.days(365),
            includeSubdomains: true,
            preload: true,
            override: true,
          },
          frameOptions: {
            frameOption: cloudfront.HeadersFrameOption.DENY,
            override: true,
          },
          contentTypeOptions: {
            override: true,
          },
          referrerPolicy: {
            referrerPolicy:
              cloudfront.HeadersReferrerPolicy.STRICT_ORIGIN_WHEN_CROSS_ORIGIN,
            override: true,
          },
        },
      },
    );

    // ─── Website (marketing site — Astro SSG) ─────────────────────
    const websiteBucket = this.createBucket(`${bucketPrefix}-website`, "Website");
    const websiteDomain = `${domain.websiteSubdomain}.${domain.zoneName}`;
    const websiteDistribution = this.createDistribution(
      "WebsiteDistribution",
      websiteBucket,
      certificate,
      securityHeaders,
      [websiteDomain],
      {
        errorResponses: [
          {
            httpStatus: 404,
            responsePagePath: "/404.html",
            responseHttpStatus: 404,
            ttl: cdk.Duration.minutes(5),
          },
        ],
        cachePolicy: cloudfront.CachePolicy.CACHING_OPTIMIZED,
        viewerRequestFunction: urlRewriteFunction,
      },
    );

    // ─── T-SEC-05: Block version.json from public access ─────────
    const blockVersionJson = new cloudfront.Function(
      this,
      "BlockVersionJson",
      {
        functionName: `${prefix}-block-version-json`,
        code: cloudfront.FunctionCode.fromInline(`
function handler(event) {
  return {
    statusCode: 404,
    statusDescription: 'Not Found',
    body: { encoding: 'text', data: 'Not Found' },
  };
}
`),
      },
    );

    // ─── Webapp (Flutter web — SPA) ───────────────────────────────
    const webappBucket = this.createBucket(`${bucketPrefix}-webapp`, "Webapp");
    const webappDomain = `${domain.webappSubdomain}.${domain.zoneName}`;
    const webappDistribution = this.createDistribution(
      "WebappDistribution",
      webappBucket,
      certificate,
      securityHeaders,
      [webappDomain],
      {
        errorResponses: [
          {
            httpStatus: 403,
            responsePagePath: "/index.html",
            responseHttpStatus: 200,
            ttl: cdk.Duration.seconds(0),
          },
        ],
        disableIndexCache: true,
      },
    );

    // Block /version.json on webapp distribution
    webappDistribution.addBehavior(
      "/version.json",
      origins.S3BucketOrigin.withOriginAccessControl(webappBucket),
      {
        viewerProtocolPolicy: cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
        functionAssociations: [
          {
            function: blockVersionJson,
            eventType: cloudfront.FunctionEventType.VIEWER_REQUEST,
          },
        ],
      },
    );

    // ─── Admin (React Vite — SPA) ─────────────────────────────────
    const adminBucket = this.createBucket(`${bucketPrefix}-admin`, "Admin");
    const adminDomain = `${domain.adminSubdomain}.${domain.zoneName}`;
    const adminDistribution = this.createDistribution(
      "AdminDistribution",
      adminBucket,
      certificate,
      securityHeaders,
      [adminDomain],
      {
        errorResponses: [
          {
            httpStatus: 403,
            responsePagePath: "/index.html",
            responseHttpStatus: 200,
            ttl: cdk.Duration.seconds(0),
          },
        ],
        disableIndexCache: true,
      },
    );

    // ─── Store IDs for CI/CD ──────────────────────────────────────
    this.websiteDistributionId = websiteDistribution.distributionId;
    this.webappDistributionId = webappDistribution.distributionId;
    this.adminDistributionId = adminDistribution.distributionId;
    this.websiteBucketName = websiteBucket.bucketName;
    this.webappBucketName = webappBucket.bucketName;
    this.adminBucketName = adminBucket.bucketName;

    // ─── Outputs ──────────────────────────────────────────────────
    new cdk.CfnOutput(this, "WebsiteUrl", {
      value: `https://${websiteDomain}`,
      description: "Marketing website URL",
    });

    new cdk.CfnOutput(this, "WebappUrl", {
      value: `https://${webappDomain}`,
      description: "Web application URL",
    });

    new cdk.CfnOutput(this, "AdminUrl", {
      value: `https://${adminDomain}`,
      description: "Admin dashboard URL",
    });

    new cdk.CfnOutput(this, "WebsiteBucketName", {
      value: websiteBucket.bucketName,
      description: "Website S3 bucket name",
    });

    new cdk.CfnOutput(this, "WebappBucketName", {
      value: webappBucket.bucketName,
      description: "Webapp S3 bucket name",
    });

    new cdk.CfnOutput(this, "AdminBucketName", {
      value: adminBucket.bucketName,
      description: "Admin S3 bucket name",
    });

    new cdk.CfnOutput(this, "WebsiteDistributionIdOutput", {
      value: websiteDistribution.distributionId,
      description: "Website CloudFront distribution ID",
    });

    new cdk.CfnOutput(this, "WebappDistributionIdOutput", {
      value: webappDistribution.distributionId,
      description: "Webapp CloudFront distribution ID",
    });

    new cdk.CfnOutput(this, "AdminDistributionIdOutput", {
      value: adminDistribution.distributionId,
      description: "Admin CloudFront distribution ID",
    });
  }

  /**
   * Create an S3 bucket with BlockPublicAccess, S3-managed encryption.
   */
  private createBucket(bucketName: string, label: string): s3.Bucket {
    return new s3.Bucket(this, `${label}Bucket`, {
      bucketName,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      encryption: s3.BucketEncryption.S3_MANAGED,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
      autoDeleteObjects: false,
    });
  }

  /**
   * Create a CloudFront distribution with OAC for S3 origin.
   */
  private createDistribution(
    id: string,
    bucket: s3.Bucket,
    certificate: acm.ICertificate,
    responseHeadersPolicy: cloudfront.ResponseHeadersPolicy,
    domainNames: string[],
    options: {
      errorResponses: cloudfront.ErrorResponse[];
      cachePolicy?: cloudfront.ICachePolicy;
      disableIndexCache?: boolean;
      viewerRequestFunction?: cloudfront.IFunction;
    },
  ): cloudfront.Distribution {
    const s3Origin = origins.S3BucketOrigin.withOriginAccessControl(bucket);

    const functionAssociations = options.viewerRequestFunction
      ? [
          {
            function: options.viewerRequestFunction,
            eventType: cloudfront.FunctionEventType.VIEWER_REQUEST,
          },
        ]
      : undefined;

    const distribution = new cloudfront.Distribution(this, id, {
      defaultBehavior: {
        origin: s3Origin,
        viewerProtocolPolicy: cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
        cachePolicy:
          options.cachePolicy ?? cloudfront.CachePolicy.CACHING_OPTIMIZED,
        responseHeadersPolicy,
        functionAssociations,
      },
      domainNames,
      certificate,
      defaultRootObject: "index.html",
      errorResponses: options.errorResponses,
      minimumProtocolVersion: cloudfront.SecurityPolicyProtocol.TLS_V1_2_2021,
      httpVersion: cloudfront.HttpVersion.HTTP2_AND_3,
    });

    // For SPAs: disable caching on index.html so deployments take effect immediately
    if (options.disableIndexCache) {
      distribution.addBehavior("/index.html", s3Origin, {
        viewerProtocolPolicy: cloudfront.ViewerProtocolPolicy.REDIRECT_TO_HTTPS,
        cachePolicy: cloudfront.CachePolicy.CACHING_DISABLED,
        responseHeadersPolicy,
      });
    }

    return distribution;
  }
}
