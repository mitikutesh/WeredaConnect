import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { WeredaDynamoTable } from '../constructs/dynamodb-construct';
import { EnvironmentConfig } from '../config/environments';

export interface DatabaseStackProps extends cdk.StackProps {
  readonly envConfig: EnvironmentConfig;
}

/**
 * Database stack: DynamoDB tables and S3 buckets.
 * All tables use PAY_PER_REQUEST for low-traffic Ethiopian rural context.
 */
export class DatabaseStack extends cdk.Stack {
  public readonly usersTable: dynamodb.Table;
  public readonly cropsTable: dynamodb.Table;
  public readonly tripsTable: dynamodb.Table;
  public readonly rateLimitsTable: dynamodb.Table;
  public readonly syncMetadataTable: dynamodb.Table;
  public readonly deliveriesTable: dynamodb.Table;
  public readonly paymentsTable: dynamodb.Table;
  public readonly uploadsBucket: s3.Bucket;

  constructor(scope: Construct, id: string, props: DatabaseStackProps) {
    super(scope, id, props);

    const { envConfig } = props;
    const prefix = `wereda-connect-${envConfig.name}`;

    // Users table
    const users = new WeredaDynamoTable(this, 'UsersTable', {
      tableName: `${prefix}-users`,
      partitionKey: { name: 'userId', type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.usersTable = users.table;

    // GSI: look up users by phone number (login, duplicate check)
    this.usersTable.addGlobalSecondaryIndex({
      indexName: 'PhoneIndex',
      partitionKey: { name: 'phoneNumber', type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: query users by role + woreda (matching, admin views)
    this.usersTable.addGlobalSecondaryIndex({
      indexName: 'RoleWaredaIndex',
      partitionKey: { name: 'role', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'woreda', type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Rate limits table (auth brute-force protection)
    const rateLimits = new WeredaDynamoTable(this, 'RateLimitsTable', {
      tableName: `${prefix}-rate-limits`,
      partitionKey: { name: 'pk', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'sk', type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.rateLimitsTable = rateLimits.table;

    // Enable TTL on rate limits table for automatic cleanup
    (this.rateLimitsTable.node.defaultChild as dynamodb.CfnTable).addPropertyOverride(
      'TimeToLiveSpecification',
      { AttributeName: 'expiresAt', Enabled: true },
    );

    // Crops table
    const crops = new WeredaDynamoTable(this, 'CropsTable', {
      tableName: `${prefix}-crops`,
      partitionKey: { name: 'cropId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.cropsTable = crops.table;

    // GSI: Query crops by status and recency (trucker browse experience)
    this.cropsTable.addGlobalSecondaryIndex({
      indexName: 'StatusCreatedAtIndex',
      partitionKey: { name: 'status', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Trips table
    const trips = new WeredaDynamoTable(this, 'TripsTable', {
      tableName: `${prefix}-trips`,
      partitionKey: { name: 'tripId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.tripsTable = trips.table;

    // GSI: Query trips by status and departure date (farmer matching)
    this.tripsTable.addGlobalSecondaryIndex({
      indexName: 'StatusDepartureDateIndex',
      partitionKey: { name: 'status', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'departureDate', type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Sync metadata table (idempotency tracking for offline-first sync)
    const syncMetadata = new WeredaDynamoTable(this, 'SyncMetadataTable', {
      tableName: `${prefix}-sync-metadata`,
      partitionKey: { name: 'idempotencyKey', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'timestamp', type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.syncMetadataTable = syncMetadata.table;

    // Enable TTL for automatic cleanup after 7 days
    (this.syncMetadataTable.node.defaultChild as dynamodb.CfnTable).addPropertyOverride(
      'TimeToLiveSpecification',
      { AttributeName: 'expiresAt', Enabled: true },
    );

    // Deliveries table
    const deliveries = new WeredaDynamoTable(this, 'DeliveriesTable', {
      tableName: `${prefix}-deliveries`,
      partitionKey: { name: 'deliveryId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'tripId', type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.deliveriesTable = deliveries.table;

    // GSI: Look up delivery by listing ID
    this.deliveriesTable.addGlobalSecondaryIndex({
      indexName: 'ListingIndex',
      partitionKey: { name: 'listingId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query deliveries by status (pending, disputed, etc.)
    this.deliveriesTable.addGlobalSecondaryIndex({
      indexName: 'StatusIndex',
      partitionKey: { name: 'status', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'confirmedAt', type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Payments table (Telebirr escrow tracking)
    const payments = new WeredaDynamoTable(this, 'PaymentsTable', {
      tableName: `${prefix}-payments`,
      partitionKey: { name: 'paymentId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'deliveryId', type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.paymentsTable = payments.table;

    // GSI: Query payments to a farmer (earnings history)
    this.paymentsTable.addGlobalSecondaryIndex({
      indexName: 'FarmerIndex',
      partitionKey: { name: 'farmerId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query payments by status (escrowed, disputed, etc.)
    this.paymentsTable.addGlobalSecondaryIndex({
      indexName: 'StatusIndex',
      partitionKey: { name: 'status', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'escrowedAt', type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // S3 uploads bucket
    this.uploadsBucket = new s3.Bucket(this, 'UploadsBucket', {
      bucketName: `${prefix}-uploads-${this.account}`,
      encryption: s3.BucketEncryption.S3_MANAGED,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      removalPolicy: envConfig.isProd ? cdk.RemovalPolicy.RETAIN : cdk.RemovalPolicy.DESTROY,
      autoDeleteObjects: !envConfig.isProd,
      lifecycleRules: [
        {
          expiration: cdk.Duration.days(7),
          prefix: 'temp/',
        },
      ],
    });

    // Outputs
    new cdk.CfnOutput(this, 'UsersTableName', {
      value: this.usersTable.tableName,
      description: 'DynamoDB Users Table',
    });

    new cdk.CfnOutput(this, 'CropsTableName', {
      value: this.cropsTable.tableName,
      description: 'DynamoDB Crops Table',
    });

    new cdk.CfnOutput(this, 'TripsTableName', {
      value: this.tripsTable.tableName,
      description: 'DynamoDB Trips Table',
    });

    new cdk.CfnOutput(this, 'UploadsBucketName', {
      value: this.uploadsBucket.bucketName,
      description: 'S3 Uploads Bucket',
    });

    new cdk.CfnOutput(this, 'RateLimitsTableName', {
      value: this.rateLimitsTable.tableName,
      description: 'DynamoDB Rate Limits Table',
    });

    new cdk.CfnOutput(this, 'SyncMetadataTableName', {
      value: this.syncMetadataTable.tableName,
      description: 'DynamoDB SyncMetadata Table',
    });

    new cdk.CfnOutput(this, 'DeliveriesTableName', {
      value: this.deliveriesTable.tableName,
      description: 'DynamoDB Deliveries Table',
    });

    new cdk.CfnOutput(this, 'PaymentsTableName', {
      value: this.paymentsTable.tableName,
      description: 'DynamoDB Payments Table',
    });
  }
}
