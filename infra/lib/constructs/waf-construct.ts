// ─────────────────────────────────────────────────────────────────────────────
// DRAFT — NOT WIRED INTO ANY STACK (T-SEC-04 / INFRA-01 deferred 2026-06-20).
//
// Why deferred: the public API is an apigatewayv2 HttpApi, which AWS WAF cannot
// attach to directly. This CLOUDFRONT-scoped ACL would protect the website/webapp/
// admin distributions, but mobile/web clients call the API on execute-api.* DIRECTLY
// (not through CloudFront), so it would NOT rate-limit the real /auth/* or /escrow/*
// endpoints. Closing T-SEC-04 properly requires one of:
//   (a) front the HttpApi with CloudFront behind /api/* and point clients at it, or
//   (b) migrate the public API to a REGIONAL apigateway v1 REST API (scope:'REGIONAL').
// Track in TASK.md (T-SEC-WAF / INFRA-01). This construct is kept ready for either path.
// ─────────────────────────────────────────────────────────────────────────────
import { Construct } from 'constructs';
import * as wafv2 from 'aws-cdk-lib/aws-wafv2';

export interface WeredaWebAclProps {
  /** Environment name (dev/staging/prod) — used for metric/ACL naming. */
  readonly envName: string;
  /**
   * WAF scope. Use 'CLOUDFRONT' for CloudFront distributions (the ACL MUST then
   * live in us-east-1) or 'REGIONAL' for ALB/API Gateway REST APIs.
   * NOTE: HTTP APIs (apigatewayv2) cannot have WAF attached directly — protect
   * them via a fronting CloudFront distribution instead.
   */
  readonly scope: 'CLOUDFRONT' | 'REGIONAL';
}

/**
 * INFRA-01/T-SEC-04: Reusable AWS WAF Web ACL with WeredaConnect defaults.
 *
 * Cost-light by design — AWS managed rule groups only (no extra infra):
 *  - AWSManagedRulesCommonRuleSet        (OWASP-ish common attacks) — BLOCK
 *  - AWSManagedRulesKnownBadInputsRuleSet (known-bad request patterns) — BLOCK
 *  - AWSManagedRulesAmazonIpReputationList (malicious IP reputation) — BLOCK
 *  - Rate-based rule, ~100 req / 5 min / IP, prioritising /auth/* and /escrow/* — BLOCK
 *
 * CloudWatch metrics + sampled requests are enabled on every rule and the ACL.
 */
export class WeredaWebAcl extends Construct {
  public readonly webAcl: wafv2.CfnWebACL;
  /** Web ACL ARN — pass to a CloudFront distribution's webAclId or a CfnWebACLAssociation. */
  public readonly webAclArn: string;

  constructor(scope: Construct, id: string, props: WeredaWebAclProps) {
    super(scope, id);

    const namePrefix = `wereda-connect-${props.envName}`;

    // /auth/* and /escrow/* are the highest-value targets (credential stuffing,
    // escrow abuse) — rate-limit them harder and at a higher priority.
    const sensitivePathMatch: wafv2.CfnWebACL.RateBasedStatementProperty = {
      limit: 100,
      aggregateKeyType: 'IP',
      scopeDownStatement: {
        orStatement: {
          statements: [
            {
              byteMatchStatement: {
                searchString: '/auth/',
                fieldToMatch: { uriPath: {} },
                positionalConstraint: 'STARTS_WITH',
                textTransformations: [{ priority: 0, type: 'LOWERCASE' }],
              },
            },
            {
              byteMatchStatement: {
                searchString: '/escrow/',
                fieldToMatch: { uriPath: {} },
                positionalConstraint: 'STARTS_WITH',
                textTransformations: [{ priority: 0, type: 'LOWERCASE' }],
              },
            },
          ],
        },
      },
    };

    const rules: wafv2.CfnWebACL.RuleProperty[] = [
      // Priority 0: per-IP rate limit on sensitive auth/escrow paths.
      {
        name: 'RateLimitSensitivePaths',
        priority: 0,
        action: { block: {} },
        statement: { rateBasedStatement: sensitivePathMatch },
        visibilityConfig: {
          sampledRequestsEnabled: true,
          cloudWatchMetricsEnabled: true,
          metricName: `${namePrefix}-rate-sensitive`,
        },
      },
      // Priority 1: global per-IP rate limit (defence-in-depth across all paths).
      {
        name: 'RateLimitGlobal',
        priority: 1,
        action: { block: {} },
        statement: {
          rateBasedStatement: { limit: 100, aggregateKeyType: 'IP' },
        },
        visibilityConfig: {
          sampledRequestsEnabled: true,
          cloudWatchMetricsEnabled: true,
          metricName: `${namePrefix}-rate-global`,
        },
      },
      // Priority 2: Amazon IP reputation list — block known malicious IPs.
      {
        name: 'AWSManagedRulesAmazonIpReputationList',
        priority: 2,
        overrideAction: { none: {} },
        statement: {
          managedRuleGroupStatement: {
            vendorName: 'AWS',
            name: 'AWSManagedRulesAmazonIpReputationList',
          },
        },
        visibilityConfig: {
          sampledRequestsEnabled: true,
          cloudWatchMetricsEnabled: true,
          metricName: `${namePrefix}-ip-reputation`,
        },
      },
      // Priority 3: known bad inputs.
      {
        name: 'AWSManagedRulesKnownBadInputsRuleSet',
        priority: 3,
        overrideAction: { none: {} },
        statement: {
          managedRuleGroupStatement: {
            vendorName: 'AWS',
            name: 'AWSManagedRulesKnownBadInputsRuleSet',
          },
        },
        visibilityConfig: {
          sampledRequestsEnabled: true,
          cloudWatchMetricsEnabled: true,
          metricName: `${namePrefix}-known-bad-inputs`,
        },
      },
      // Priority 4: common rule set (broad OWASP-style protections).
      {
        name: 'AWSManagedRulesCommonRuleSet',
        priority: 4,
        overrideAction: { none: {} },
        statement: {
          managedRuleGroupStatement: {
            vendorName: 'AWS',
            name: 'AWSManagedRulesCommonRuleSet',
          },
        },
        visibilityConfig: {
          sampledRequestsEnabled: true,
          cloudWatchMetricsEnabled: true,
          metricName: `${namePrefix}-common`,
        },
      },
    ];

    this.webAcl = new wafv2.CfnWebACL(this, 'WebAcl', {
      name: `${namePrefix}-web-acl`,
      scope: props.scope,
      defaultAction: { allow: {} },
      rules,
      visibilityConfig: {
        sampledRequestsEnabled: true,
        cloudWatchMetricsEnabled: true,
        metricName: `${namePrefix}-web-acl`,
      },
    });

    this.webAclArn = this.webAcl.attrArn;
  }
}
