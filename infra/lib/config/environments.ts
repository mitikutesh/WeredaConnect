/**
 * Environment configuration for WeredaConnect infrastructure.
 *
 * Region: me-south-1 (Bahrain) — closest AWS region to Ethiopia.
 * All environments use the same region to minimize latency for Ethiopian users.
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
}

export const environments: Record<string, EnvironmentConfig> = {
  dev: {
    name: 'dev',
    region: 'me-south-1',
    isProd: false,
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
