import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as apigateway from 'aws-cdk-lib/aws-apigatewayv2';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as s3 from 'aws-cdk-lib/aws-s3';

/**
 * Main infrastructure stack for WeredaConnect
 * 
 * Optimized for:
 * - Low-traffic scenarios (Ethiopian rural context)
 * - Cost efficiency (on-demand billing)
 * - Cold start <2 seconds (ARM64 + Native AOT)
 * - Intermittent connectivity (offline-first design)
 */
export class WeredaConnectStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    // DynamoDB Tables (On-Demand Billing for low traffic)
    const usersTable = new dynamodb.Table(this, 'UsersTable', {
      tableName: 'wereda-connect-users',
      partitionKey: { name: 'userId', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      removalPolicy: cdk.RemovalPolicy.RETAIN, // Protect production data
      pointInTimeRecovery: true,
    });

    const cropsTable = new dynamodb.Table(this, 'CropsTable', {
      tableName: 'wereda-connect-crops',
      partitionKey: { name: 'cropId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
      pointInTimeRecovery: true,
    });

    const tripsTable = new dynamodb.Table(this, 'TripsTable', {
      tableName: 'wereda-connect-trips',
      partitionKey: { name: 'tripId', type: dynamodb.AttributeType.STRING },
      sortKey: { name: 'createdAt', type: dynamodb.AttributeType.STRING },
      billingMode: dynamodb.BillingMode.PAY_PER_REQUEST,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
      pointInTimeRecovery: true,
    });

    // S3 Bucket for user uploads (crop images, etc.)
    const uploadsBucket = new s3.Bucket(this, 'UploadsBucket', {
      bucketName: `wereda-connect-uploads-${this.account}`,
      encryption: s3.BucketEncryption.S3_MANAGED,
      blockPublicAccess: s3.BlockPublicAccess.BLOCK_ALL,
      removalPolicy: cdk.RemovalPolicy.RETAIN,
      lifecycleRules: [
        {
          // Delete temporary uploads after 7 days
          expiration: cdk.Duration.days(7),
          prefix: 'temp/',
        },
      ],
    });

    // Health Check Lambda (TODO: Uncomment after publishing .NET AOT Lambda)
    // const healthCheckFunction = new lambda.Function(this, 'HealthCheckFunction', {
    //   functionName: 'wereda-connect-health-check',
    //   runtime: lambda.Runtime.PROVIDED_AL2023, // Custom runtime for .NET AOT
    //   handler: 'bootstrap', // .NET AOT uses bootstrap
    //   code: lambda.Code.fromAsset('../server/bin/Release/net10.0/linux-arm64/publish'),
    //   architecture: lambda.Architecture.ARM_64, // ARM64 for cost and performance
    //   memorySize: 512, // Start small, optimize later
    //   timeout: cdk.Duration.seconds(10),
    //   environment: {
    //     USERS_TABLE: usersTable.tableName,
    //     CROPS_TABLE: cropsTable.tableName,
    //     TRIPS_TABLE: tripsTable.tableName,
    //     UPLOADS_BUCKET: uploadsBucket.bucketName,
    //   },
    // });

    // // Grant DynamoDB permissions
    // usersTable.grantReadWriteData(healthCheckFunction);
    // cropsTable.grantReadWriteData(healthCheckFunction);
    // tripsTable.grantReadWriteData(healthCheckFunction);
    // uploadsBucket.grantReadWrite(healthCheckFunction);

    // API Gateway HTTP API (cheaper than REST API)
    const httpApi = new apigateway.CfnApi(this, 'HttpApi', {
      name: 'wereda-connect-api',
      protocolType: 'HTTP',
      description: 'WeredaConnect HTTP API - Optimized for Ethiopian context',
    });

    // Outputs
    new cdk.CfnOutput(this, 'ApiUrl', {
      value: `https://${httpApi.ref}.execute-api.${this.region}.amazonaws.com`,
      description: 'API Gateway URL',
    });

    new cdk.CfnOutput(this, 'UsersTableName', {
      value: usersTable.tableName,
      description: 'DynamoDB Users Table',
    });

    new cdk.CfnOutput(this, 'CropsTableName', {
      value: cropsTable.tableName,
      description: 'DynamoDB Crops Table',
    });

    new cdk.CfnOutput(this, 'TripsTableName', {
      value: tripsTable.tableName,
      description: 'DynamoDB Trips Table',
    });

    new cdk.CfnOutput(this, 'UploadsBucketName', {
      value: uploadsBucket.bucketName,
      description: 'S3 Uploads Bucket',
    });
  }
}

