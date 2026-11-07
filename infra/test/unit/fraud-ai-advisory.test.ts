import * as cdk from 'aws-cdk-lib';
import { Template, Match } from 'aws-cdk-lib/assertions';
import { DatabaseStack } from '../../lib/stacks/database-stack';
import { ApiStack } from '../../lib/stacks/api-stack';
import { MonitoringStack } from '../../lib/stacks/monitoring-stack';
import { environments, EnvironmentConfig } from '../../lib/config/environments';

// FRD-19: the AI advisory reviewer (fraud spec section 21, specs/decisions/frd-19-ai-advisory-reviewer.md). Kept out
// of stack.test.ts on purpose (sibling branches edit that file).

const devConfig = environments['dev'];
const REVIEWER = 'wereda-connect-dev-fraud-ai-reviewer';
const CRON = 'wereda-connect-dev-fraud-advisory';

type Statement = { Action: string | string[]; Resource: unknown; Effect?: string; Condition?: unknown };

function apiTemplate(envConfig: EnvironmentConfig = devConfig): Template {
  const app = new cdk.App();
  const db = new DatabaseStack(app, 'DbStack', {
    env: { account: '123456789012', region: envConfig.region },
    envConfig,
  });
  const api = new ApiStack(app, 'TestApiStack', {
    env: { account: '123456789012', region: envConfig.region },
    envConfig,
    usersTable: db.usersTable,
    cropsTable: db.cropsTable,
    tripsTable: db.tripsTable,
    rateLimitsTable: db.rateLimitsTable,
    syncMetadataTable: db.syncMetadataTable,
    deliveriesTable: db.deliveriesTable,
    paymentsTable: db.paymentsTable,
    escrowsTable: db.escrowsTable,
    escrowAuditTable: db.escrowAuditTable,
    escrowIdempotencyTable: db.escrowIdempotencyTable,
    deliveryQrSecretsTable: db.deliveryQrSecretsTable,
    ordersTable: db.ordersTable,
    fraudSignalsTable: db.fraudSignalsTable,
    intentsTable: db.intentsTable,
    deviceTokensTable: db.deviceTokensTable,
    matchEventsTable: db.matchEventsTable,
    ratingsTable: db.ratingsTable,
    messagesTable: db.messagesTable,
    notificationsTable: db.notificationsTable,
    tripLocationsTable: db.tripLocationsTable,
    transactionAuditTable: db.transactionAuditTable,
    uploadsBucket: db.uploadsBucket,
  });
  return Template.fromStack(api);
}

function functionOf(template: Template, functionName: string): { id: string; props: Record<string, any> } {
  const fns = template.findResources('AWS::Lambda::Function');
  const id = Object.keys(fns).find((key) => fns[key].Properties?.FunctionName === functionName);
  if (!id) {
    throw new Error(`no Lambda named ${functionName}`);
  }
  return { id, props: fns[id].Properties };
}

function statementsOf(template: Template, functionName: string): Statement[] {
  const fn = functionOf(template, functionName).props;
  const roleId = (fn.Role as { 'Fn::GetAtt': [string, string] })['Fn::GetAtt'][0];
  return Object.values(template.findResources('AWS::IAM::Policy'))
    .filter((p) => JSON.stringify(p.Properties.Roles).includes(`"${roleId}"`))
    .flatMap((p) => p.Properties.PolicyDocument.Statement as Statement[]);
}

const actionsOf = (s: Statement): string[] => (Array.isArray(s.Action) ? s.Action : [s.Action]);

/** Whether a statement's resources mention `hint` (a table's logical id, an ARN fragment). */
const mentions = (s: Statement, hint: string) => JSON.stringify(s.Resource).includes(hint);

describe('FRD-19 AI advisory reviewer (ApiStack)', () => {
  const template = apiTemplate();

  test('the reviewer is a Node.js 24 Lambda on ARM64 with no bundle step, 30 s, off by default', () => {
    const fn = functionOf(template, REVIEWER).props;
    expect(fn.Runtime).toBe('nodejs24.x');
    expect(fn.Architectures).toEqual(['arm64']);
    expect(fn.Handler).toBe('index.handler');
    expect(fn.Timeout).toBe(30);
    expect(fn.MemorySize).toBe(256);
    expect(fn.ReservedConcurrentExecutions).toBeUndefined(); // dev: the account limit is 10
    expect(fn.Environment.Variables).toEqual({
      AI_REVIEW_ENABLED: 'false',
      BEDROCK_MODEL_ID: 'eu.anthropic.claude-opus-5-5',
      BEDROCK_REGION: 'eu-north-1',
    });
  });

  test('the reviewer keeps its logs a month', () => {
    template.hasResourceProperties('AWS::Logs::LogGroup', {
      LogGroupName: `/aws/lambda/${REVIEWER}`,
      RetentionInDays: 30,
    });
  });

  test('the reviewer may only invoke the configured model, through its EU profile', () => {
    const statements = statementsOf(template, REVIEWER);
    const actions = statements.flatMap(actionsOf);
    expect(actions.every((a) => a.startsWith('bedrock:'))).toBe(true);
    expect([...new Set(actions)].sort()).toEqual(['bedrock:GetInferenceProfile', 'bedrock:InvokeModel']);

    const profile = statements.find((s) => mentions(s, 'inference-profile/eu.anthropic.claude-opus-5-5'));
    expect(profile).toBeDefined();
    expect(JSON.stringify(profile!.Resource)).toContain(':bedrock:eu-north-1:');

    const models = statements.find((s) => mentions(s, 'foundation-model/anthropic.claude-opus-5-5'));
    expect(models).toBeDefined();
    expect(actionsOf(models!)).toEqual(['bedrock:InvokeModel']);
    expect(JSON.stringify(models!.Condition)).toContain('bedrock:InferenceProfileArn');
    expect(JSON.stringify(models!.Condition)).toContain('inference-profile/eu.anthropic.claude-opus-5-5');
  });

  test('the cron is the AOT bootstrap with the kill switch off, the reviewer named, 3 minutes, one at a time', () => {
    const fn = functionOf(template, CRON).props;
    expect(fn.Runtime).toBe('provided.al2023');
    expect(fn.Timeout).toBe(180);
    expect(fn.Environment.Variables).toEqual(expect.objectContaining({
      FUNCTION_HANDLER: 'FraudAdvisoryCronFunction',
      FRAUD_AI_REVIEW_ENABLED: 'false',
      FRAUD_AI_REVIEWER_FUNCTION: expect.anything(),
      FRAUD_SIGNALS_TABLE: expect.anything(),
      USERS_TABLE: expect.anything(),
      TRANSACTION_AUDIT_TABLE: expect.anything(),
    }));
    expect(JSON.stringify(fn.Environment.Variables.FRAUD_AI_REVIEWER_FUNCTION)).toContain('FraudAiReviewerFunction');
  });

  test('the cron reads fraud signals, users rows and the audit, puts fraud-signal rows and invokes the reviewer: nothing else', () => {
    const statements = statementsOf(template, CRON);
    const dynamo = statements.filter((s) => actionsOf(s).some((a) => a.startsWith('dynamodb:')));
    const byResource = dynamo.map((s) => ({ actions: actionsOf(s).sort(), resource: JSON.stringify(s.Resource) }));

    // No grant names a money or order table, and no update or delete exists at all.
    for (const { resource } of byResource) {
      expect(resource).toMatch(/FraudSignals|UsersTable|TransactionAudit/);
      expect(resource).not.toMatch(/Escrow|Orders|Deliveries|Crops|Trips|Payments/);
    }
    const allDynamo = byResource.flatMap((r) => r.actions);
    expect([...new Set(allDynamo)].sort()).toEqual(['dynamodb:GetItem', 'dynamodb:PutItem', 'dynamodb:Query']);

    const putOn = byResource.filter((r) => r.actions.includes('dynamodb:PutItem')).map((r) => r.resource);
    expect(putOn).toHaveLength(1);
    expect(putOn[0]).toContain('FraudSignals');
    expect(putOn[0]).not.toContain('/index/');

    expect(byResource.some((r) => r.resource.includes('/index/StatusCreatedAtIndex') && r.actions.join() === 'dynamodb:Query')).toBe(true);
    expect(byResource.some((r) => r.resource.includes('UsersTable') && r.actions.join() === 'dynamodb:GetItem')).toBe(true);
    expect(byResource.some((r) => r.resource.includes('/index/EntityIndex') && r.actions.join() === 'dynamodb:Query')).toBe(true);

    const invoke = statements.filter((s) => actionsOf(s).some((a) => a.startsWith('lambda:')));
    expect(invoke).toHaveLength(1);
    expect(JSON.stringify(invoke[0].Resource)).toContain('FraudAiReviewerFunction');
    expect(statements.flatMap(actionsOf).some((a) => a.startsWith('bedrock:'))).toBe(false);
  });

  test('an hourly rule runs the cron', () => {
    template.hasResourceProperties('AWS::Events::Rule', {
      Name: 'wereda-connect-dev-fraud-advisory-hourly',
      ScheduleExpression: 'rate(1 hour)',
      Targets: Match.arrayWith([
        Match.objectLike({
          Arn: { 'Fn::GetAtt': [Match.stringLikeRegexp('^FraudAdvisoryCronFunction'), 'Arn'] },
          Input: JSON.stringify({ body: JSON.stringify({ action: 'ai_review' }) }),
        }),
      ]),
    });
  });

  test('aiReviewEnabled turns both switches on', () => {
    const on = apiTemplate({ ...devConfig, aiReviewEnabled: true });
    expect(functionOf(on, REVIEWER).props.Environment.Variables.AI_REVIEW_ENABLED).toBe('true');
    expect(functionOf(on, CRON).props.Environment.Variables.FRAUD_AI_REVIEW_ENABLED).toBe('true');
  });

  test('every environment ships with the reviewer off', () => {
    for (const name of ['dev', 'staging', 'prod']) {
      expect([name, environments[name].aiReviewEnabled ?? false]).toEqual([name, false]);
    }
  });

  test('staging and prod (me-south-1) would call the EU profile from eu-central-1', () => {
    for (const name of ['staging', 'prod']) {
      expect([name, environments[name].aiReviewBedrockRegion]).toEqual([name, 'eu-central-1']);
      expect([name, environments[name].aiReviewModelId]).toEqual([name, 'eu.anthropic.claude-opus-5-5']);
    }
    const routed = apiTemplate({ ...devConfig, aiReviewBedrockRegion: 'eu-central-1' });
    expect(functionOf(routed, REVIEWER).props.Environment.Variables.BEDROCK_REGION).toBe('eu-central-1');
    const profile = statementsOf(routed, REVIEWER).find((s) => mentions(s, 'inference-profile/'));
    expect(JSON.stringify(profile!.Resource)).toContain(':bedrock:eu-central-1:');
  });

  test('a bare model id is granted on the foundation model in the Bedrock Region, without a profile', () => {
    const bare = apiTemplate({ ...devConfig, aiReviewModelId: 'anthropic.claude-haiku-5-5' });
    const statements = statementsOf(bare, REVIEWER);
    expect(statements).toHaveLength(1);
    expect(actionsOf(statements[0])).toEqual(['bedrock:InvokeModel']);
    expect(JSON.stringify(statements[0].Resource)).toContain(':bedrock:eu-north-1::foundation-model/anthropic.claude-haiku-5-5');
    expect(statements[0].Condition).toBeUndefined();
  });
});

describe('FRD-19 AI advisory reviewer (MonitoringStack)', () => {
  const app = new cdk.App();
  const stack = new MonitoringStack(app, 'FraudAdvisoryMonitoring', {
    env: { account: '123456789012', region: devConfig.region },
    envConfig: devConfig,
    apiUrl: `https://test.execute-api.${devConfig.region}.amazonaws.com`,
    apiId: 'testapi123',
  });
  const template = Template.fromStack(stack);

  test('one alarm counts the errors of both Lambdas and notifies the alarm topic', () => {
    const alarms = template.findResources('AWS::CloudWatch::Alarm', {
      Properties: { AlarmName: 'wereda-connect-dev-fraud-advisory-errors' },
    });
    const alarm = Object.values(alarms)[0];
    expect(alarm).toBeDefined();
    const props = alarm.Properties;
    expect(props.Threshold).toBe(1);
    expect(props.ComparisonOperator).toBe('GreaterThanOrEqualToThreshold');
    expect(props.TreatMissingData).toBe('notBreaching');
    expect(JSON.stringify(props.AlarmActions)).toMatch(/AlarmTopic/);
    const metrics = JSON.stringify(props.Metrics);
    expect(metrics).toContain('wereda-connect-dev-fraud-advisory"');
    expect(metrics).toContain('wereda-connect-dev-fraud-ai-reviewer');
    expect(metrics).toContain('cron + reviewer');
  });

  test('the cron log group feeds the FRD-16 signal metrics (its advisory rows count like any signal row)', () => {
    const filters = Object.values(template.findResources('AWS::Logs::MetricFilter'))
      .filter((f) => f.Properties.LogGroupName === '/aws/lambda/wereda-connect-dev-fraud-advisory')
      .map((f) => f.Properties.MetricTransformations[0].MetricName)
      .sort();
    expect(filters).toEqual(expect.arrayContaining(['FraudSignalsByType', 'FraudSignalsRecorded']));
  });
});
