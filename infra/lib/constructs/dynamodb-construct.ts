import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';

export interface DynamoDbTableProps {
  readonly tableName: string;
  readonly partitionKey: dynamodb.Attribute;
  readonly sortKey?: dynamodb.Attribute;
  readonly isProd: boolean;
  /** Optional DynamoDB stream view type (e.g. NEW_IMAGE for change consumers). */
  readonly stream?: dynamodb.StreamViewType;
}

/**
 * Reusable DynamoDB table construct with WeredaConnect defaults:
 * - PAY_PER_REQUEST billing (cost-efficient for low traffic)
 * - Point-in-time recovery enabled
 * - RETAIN removal policy in production, DESTROY in dev
 */
export class WeredaDynamoTable extends Construct {
  public readonly table: dynamodb.Table;

  constructor(scope: Construct, id: string, props: DynamoDbTableProps) {
    super(scope, id);

    this.table = new dynamodb.Table(this, 'Table', {
      tableName: props.tableName,
      partitionKey: props.partitionKey,
      sortKey: props.sortKey,
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      removalPolicy: props.isProd ? cdk.RemovalPolicy.RETAIN : cdk.RemovalPolicy.DESTROY,
      pointInTimeRecoverySpecification: { pointInTimeRecoveryEnabled: true },
      stream: props.stream,
    });
  }
}
