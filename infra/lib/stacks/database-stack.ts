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

    // Crops table
    const crops = new WeredaDynamoTable(this, 'CropsTable', {
      tableName: `${prefix}-crops`,
      partitionKey: { name: 'cropId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.cropsTable = crops.table;

    // Trips table
    const trips = new WeredaDynamoTable(this, 'TripsTable', {
      tableName: `${prefix}-trips`,
      partitionKey: { name: 'tripId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
      isProd: envConfig.isProd,
    });
    this.tripsTable = trips.table;

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
  }
}
