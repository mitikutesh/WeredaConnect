import * as cdk from "aws-cdk-lib";
import { Construct } from "constructs";
import * as dynamodb from "aws-cdk-lib/aws-dynamodb";
import * as s3 from "aws-cdk-lib/aws-s3";
import { WeredaDynamoTable } from "../constructs/dynamodb-construct";
import { EnvironmentConfig } from "../config/environments";

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
  public readonly escrowsTable: dynamodb.Table;
  public readonly escrowAuditTable: dynamodb.Table;
  public readonly escrowIdempotencyTable: dynamodb.Table;
  public readonly ordersTable: dynamodb.Table;
  public readonly fraudSignalsTable: dynamodb.Table;
  public readonly uploadsBucket: s3.Bucket;
  public readonly accessLogsBucket: s3.Bucket;

  constructor(scope: Construct, id: string, props: DatabaseStackProps) {
    super(scope, id, props);

    const { envConfig } = props;
    const prefix = `wereda-connect-${envConfig.name}`;

    // Users table
    const users = new WeredaDynamoTable(this, "UsersTable", {
      tableName: `${prefix}-users`,
      partitionKey: { name: "userId", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.usersTable = users.table;

    // GSI: look up users by phone number (login, duplicate check)
    this.usersTable.addGlobalSecondaryIndex({
      indexName: "PhoneIndex",
      partitionKey: {
        name: "phoneNumber",
        type: dynamodb.AttributeType.STRING,
      },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: query users by role + woreda (matching, admin views)
    this.usersTable.addGlobalSecondaryIndex({
      indexName: "RoleWaredaIndex",
      partitionKey: { name: "role", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "woreda", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: look up users by Fayda ID (duplicate check during registration)
    this.usersTable.addGlobalSecondaryIndex({
      indexName: "FaydaIdIndex",
      partitionKey: {
        name: "faydaId",
        type: dynamodb.AttributeType.STRING,
      },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Rate limits table (auth brute-force protection)
    const rateLimits = new WeredaDynamoTable(this, "RateLimitsTable", {
      tableName: `${prefix}-rate-limits`,
      partitionKey: { name: "pk", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "sk", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.rateLimitsTable = rateLimits.table;

    // Enable TTL on rate limits table for automatic cleanup
    (
      this.rateLimitsTable.node.defaultChild as dynamodb.CfnTable
    ).addPropertyOverride("TimeToLiveSpecification", {
      AttributeName: "expiresAt",
      Enabled: true,
    });

    // Crops table
    const crops = new WeredaDynamoTable(this, "CropsTable", {
      tableName: `${prefix}-crops`,
      partitionKey: { name: "cropId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.cropsTable = crops.table;

    // GSI: Query crops by status and recency (trucker browse experience)
    this.cropsTable.addGlobalSecondaryIndex({
      indexName: "StatusCreatedAtIndex",
      partitionKey: { name: "status", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // F-14: GSI for duplicate listing detection (fraud rule 1)
    // Uses INCLUDE projection — server does GSI query + BatchGetItem for full items
    this.cropsTable.addGlobalSecondaryIndex({
      indexName: "FarmerIdCreatedAtIndex",
      partitionKey: { name: "farmerId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.INCLUDE,
      nonKeyAttributes: ["cropType", "pricePerKg", "deletedAt"],
    });

    // F-14: GSI for price anomaly detection (fraud rule 2)
    // Uses INCLUDE projection — server does GSI query + BatchGetItem for full items
    this.cropsTable.addGlobalSecondaryIndex({
      indexName: "CropTypeCreatedAtIndex",
      partitionKey: { name: "cropType", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.INCLUDE,
      nonKeyAttributes: ["pricePerKg", "deletedAt"],
    });

    // Trips table
    const trips = new WeredaDynamoTable(this, "TripsTable", {
      tableName: `${prefix}-trips`,
      partitionKey: { name: "tripId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.tripsTable = trips.table;

    // GSI: Query trips by status and departure date (farmer matching)
    this.tripsTable.addGlobalSecondaryIndex({
      indexName: "StatusDepartureDateIndex",
      partitionKey: { name: "status", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "departureDate", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query trips by trucker ID (trucker's "My Trips" view)
    this.tripsTable.addGlobalSecondaryIndex({
      indexName: "TruckerIdCreatedAtIndex",
      partitionKey: { name: "truckerId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Sync metadata table (idempotency tracking for offline-first sync)
    const syncMetadata = new WeredaDynamoTable(this, "SyncMetadataTable", {
      tableName: `${prefix}-sync-metadata`,
      partitionKey: {
        name: "idempotencyKey",
        type: dynamodb.AttributeType.STRING,
      },
      sortKey: { name: "timestamp", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.syncMetadataTable = syncMetadata.table;

    // Enable TTL for automatic cleanup after 7 days
    (
      this.syncMetadataTable.node.defaultChild as dynamodb.CfnTable
    ).addPropertyOverride("TimeToLiveSpecification", {
      AttributeName: "expiresAt",
      Enabled: true,
    });

    // Deliveries table
    const deliveries = new WeredaDynamoTable(this, "DeliveriesTable", {
      tableName: `${prefix}-deliveries`,
      partitionKey: { name: "deliveryId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "tripId", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.deliveriesTable = deliveries.table;

    // GSI: Look up delivery by listing ID
    this.deliveriesTable.addGlobalSecondaryIndex({
      indexName: "ListingIndex",
      partitionKey: { name: "listingId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query deliveries by status (pending, disputed, etc.)
    this.deliveriesTable.addGlobalSecondaryIndex({
      indexName: "StatusIndex",
      partitionKey: { name: "status", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "confirmedAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // R4.2: GSI: Query deliveries by status + createdAt (per delivery-verification-api.md)
    this.deliveriesTable.addGlobalSecondaryIndex({
      indexName: "StatusCreatedAtIndex",
      partitionKey: { name: "status", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // R4.2: GSI: Query all deliveries for a specific trip
    this.deliveriesTable.addGlobalSecondaryIndex({
      indexName: "TripIdIndex",
      partitionKey: { name: "tripId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // R4.2: GSI: Query deliveries by participant (farmer, trucker, or buyer)
    this.deliveriesTable.addGlobalSecondaryIndex({
      indexName: "ParticipantIndex",
      partitionKey: {
        name: "participantId",
        type: dynamodb.AttributeType.STRING,
      },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Payments table (Telebirr escrow tracking)
    const payments = new WeredaDynamoTable(this, "PaymentsTable", {
      tableName: `${prefix}-payments`,
      partitionKey: { name: "paymentId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "deliveryId", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.paymentsTable = payments.table;

    // GSI: Query payments to a farmer (earnings history)
    this.paymentsTable.addGlobalSecondaryIndex({
      indexName: "FarmerIndex",
      partitionKey: { name: "farmerId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query payments by status (escrowed, disputed, etc.)
    this.paymentsTable.addGlobalSecondaryIndex({
      indexName: "StatusIndex",
      partitionKey: { name: "status", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "escrowedAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // ─── R4.3 Escrow Tables (per escrow-protocol.md) ──────────────

    // Escrows table (server-authoritative, not sync-based)
    const escrows = new WeredaDynamoTable(this, "EscrowsTable", {
      tableName: `${prefix}-escrows`,
      partitionKey: { name: "escrowId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.escrowsTable = escrows.table;

    // GSI: Look up escrow by delivery (1:1 relationship)
    this.escrowsTable.addGlobalSecondaryIndex({
      indexName: "DeliveryIndex",
      partitionKey: { name: "deliveryId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Buyer's payment history
    this.escrowsTable.addGlobalSecondaryIndex({
      indexName: "BuyerStatusIndex",
      partitionKey: { name: "buyerId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Farmer's payment history
    this.escrowsTable.addGlobalSecondaryIndex({
      indexName: "FarmerStatusIndex",
      partitionKey: { name: "farmerId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Find escrowed items nearing expiry (for auto-refund/auto-release cron)
    this.escrowsTable.addGlobalSecondaryIndex({
      indexName: "StatusExpiresIndex",
      partitionKey: { name: "status", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "expiresAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // Escrow audit table (immutable append-only, no updates or deletes)
    const escrowAudit = new WeredaDynamoTable(this, "EscrowAuditTable", {
      tableName: `${prefix}-escrow-audit`,
      partitionKey: { name: "escrowId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "timestamp", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.escrowAuditTable = escrowAudit.table;

    // Escrow idempotency table (prevents double-charges on flaky 3G networks)
    const escrowIdempotency = new WeredaDynamoTable(
      this,
      "EscrowIdempotencyTable",
      {
        tableName: `${prefix}-escrow-idempotency`,
        partitionKey: {
          name: "idempotencyKey",
          type: dynamodb.AttributeType.STRING,
        },
        isProd: envConfig.isProd,
      },
    );
    this.escrowIdempotencyTable = escrowIdempotency.table;

    // Enable TTL for automatic cleanup after 30 days (longer than sync 7-day TTL)
    (
      this.escrowIdempotencyTable.node.defaultChild as dynamodb.CfnTable
    ).addPropertyOverride("TimeToLiveSpecification", {
      AttributeName: "expiresAt",
      Enabled: true,
    });

    // F-14: Fraud signals table for risk scoring (Rule 5)
    const fraudSignals = new WeredaDynamoTable(this, "FraudSignalsTable", {
      tableName: `${prefix}-fraud-signals`,
      partitionKey: { name: "userId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "signalKey", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.fraudSignalsTable = fraudSignals.table;

    // Enable TTL for 30-day auto-cleanup of fraud signals
    (
      this.fraudSignalsTable.node.defaultChild as dynamodb.CfnTable
    ).addPropertyOverride("TimeToLiveSpecification", {
      AttributeName: "expiresAt",
      Enabled: true,
    });

    // ─── Orders Table (buyer-initiated orders) ─────────────────────
    const orders = new WeredaDynamoTable(this, "OrdersTable", {
      tableName: `${prefix}-orders`,
      partitionKey: { name: "orderId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.ordersTable = orders.table;

    // GSI: Query orders by buyer
    this.ordersTable.addGlobalSecondaryIndex({
      indexName: "BuyerIdCreatedAtIndex",
      partitionKey: { name: "buyerId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query orders by farmer
    this.ordersTable.addGlobalSecondaryIndex({
      indexName: "FarmerIdCreatedAtIndex",
      partitionKey: { name: "farmerId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query orders by status
    this.ordersTable.addGlobalSecondaryIndex({
      indexName: "StatusCreatedAtIndex",
      partitionKey: { name: "status", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query orders by listing
    this.ordersTable.addGlobalSecondaryIndex({
      indexName: "ListingIdIndex",
      partitionKey: { name: "listingId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query orders by trip
    this.ordersTable.addGlobalSecondaryIndex({
      indexName: "TripIdIndex",
      partitionKey: { name: "tripId", type: dynamodb.AttributeType.STRING },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // GSI: Query orders by trucker (trucker's order view)
    this.ordersTable.addGlobalSecondaryIndex({
      indexName: "TruckerIdCreatedAtIndex",
      partitionKey: {
        name: "truckerId",
        type: dynamodb.AttributeType.STRING,
      },
      sortKey: { name: "createdAt", type: dynamodb.AttributeType.STRING },
      projectionType: dynamodb.ProjectionType.ALL,
    });

    // S3 access logs bucket (F-18: audit logging for compliance)
    this.accessLogsBucket = new s3.Bucket(this, "AccessLogsBucket", {
      bucketName: `${prefix}-access-logs-${this.account}`,
      encryption: s3.BucketEncryption.S3_MANAGED,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      removalPolicy: envConfig.isProd
        ? cdk.RemovalPolicy.RETAIN
        : cdk.RemovalPolicy.DESTROY,
      autoDeleteObjects: !envConfig.isProd,
      lifecycleRules: [
        {
          expiration: cdk.Duration.days(365),
          prefix: "uploads/",
        },
      ],
      objectOwnership: s3.ObjectOwnership.BUCKET_OWNER_PREFERRED,
    });

    // S3 uploads bucket (F-18: versioning + access logging enabled)
    this.uploadsBucket = new s3.Bucket(this, "UploadsBucket", {
      bucketName: `${prefix}-uploads-${this.account}`,
      encryption: s3.BucketEncryption.S3_MANAGED,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      removalPolicy: envConfig.isProd
        ? cdk.RemovalPolicy.RETAIN
        : cdk.RemovalPolicy.DESTROY,
      autoDeleteObjects: !envConfig.isProd,
      versioned: true,
      serverAccessLogsBucket: this.accessLogsBucket,
      serverAccessLogsPrefix: "uploads/",
      lifecycleRules: [
        {
          expiration: cdk.Duration.days(7),
          prefix: "temp/",
        },
        {
          // Move delivery photos to Glacier after 1 year (per escrow-protocol.md)
          transitions: [
            {
              storageClass: s3.StorageClass.GLACIER,
              transitionAfter: cdk.Duration.days(365),
            },
          ],
          prefix: "photos/",
        },
      ],
    });

    // Outputs
    new cdk.CfnOutput(this, "UsersTableName", {
      value: this.usersTable.tableName,
      description: "DynamoDB Users Table",
    });

    new cdk.CfnOutput(this, "CropsTableName", {
      value: this.cropsTable.tableName,
      description: "DynamoDB Crops Table",
    });

    new cdk.CfnOutput(this, "TripsTableName", {
      value: this.tripsTable.tableName,
      description: "DynamoDB Trips Table",
    });

    new cdk.CfnOutput(this, "UploadsBucketName", {
      value: this.uploadsBucket.bucketName,
      description: "S3 Uploads Bucket",
    });

    new cdk.CfnOutput(this, "AccessLogsBucketName", {
      value: this.accessLogsBucket.bucketName,
      description: "S3 Access Logs Bucket (F-18 audit logging)",
    });

    new cdk.CfnOutput(this, "RateLimitsTableName", {
      value: this.rateLimitsTable.tableName,
      description: "DynamoDB Rate Limits Table",
    });

    new cdk.CfnOutput(this, "SyncMetadataTableName", {
      value: this.syncMetadataTable.tableName,
      description: "DynamoDB SyncMetadata Table",
    });

    new cdk.CfnOutput(this, "DeliveriesTableName", {
      value: this.deliveriesTable.tableName,
      description: "DynamoDB Deliveries Table",
    });

    new cdk.CfnOutput(this, "PaymentsTableName", {
      value: this.paymentsTable.tableName,
      description: "DynamoDB Payments Table",
    });

    new cdk.CfnOutput(this, "EscrowsTableName", {
      value: this.escrowsTable.tableName,
      description: "DynamoDB Escrows Table",
    });

    new cdk.CfnOutput(this, "EscrowAuditTableName", {
      value: this.escrowAuditTable.tableName,
      description: "DynamoDB Escrow Audit Table",
    });

    new cdk.CfnOutput(this, "EscrowIdempotencyTableName", {
      value: this.escrowIdempotencyTable.tableName,
      description: "DynamoDB Escrow Idempotency Table",
    });

    new cdk.CfnOutput(this, "FraudSignalsTableName", {
      value: this.fraudSignalsTable.tableName,
      description: "DynamoDB Fraud Signals Table (F-14)",
    });

    new cdk.CfnOutput(this, "OrdersTableName", {
      value: this.ordersTable.tableName,
      description: "DynamoDB Orders Table",
    });
  }
}
