/**
 * Environment configuration for WeredaConnect infrastructure.
 *
 * Region: me-south-1 (Bahrain) — closest AWS region to Ethiopia.
 * All environments use the same region to minimize latency for Ethiopian users.
 */

export interface EnvironmentConfig {
  readonly name: string;
  readonly region: string;
  readonly account?: string;
  readonly isProd: boolean;
  readonly tags: Record<string, string>;
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
  },
};
