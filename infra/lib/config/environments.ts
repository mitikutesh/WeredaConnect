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
   * server's Fayda issuer at its Function URL. 'live': use the real VeriFayda
   * eSignet issuer. Unset keeps the current behaviour. 'mock' must never be
   * used when isProd is true (the stack refuses to synthesize and the construct throws).
   */
  readonly faydaMode?: 'mock' | 'live';
}

export const environments: Record<string, EnvironmentConfig> = {
  dev: {
    name: 'dev',
    region: 'eu-north-1',
    isProd: false,
    // Dev signs in against the mock Fayda eSignet provider (tools/fayda-mock);
    // staging and prod use the real issuer through the seeded SSM parameters.
    faydaMode: 'mock',
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
