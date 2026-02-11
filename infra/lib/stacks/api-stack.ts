import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as apigateway from 'aws-cdk-lib/aws-apigatewayv2';
import * as dynamodb from 'aws-cdk-lib/aws-dynamodb';
import * as s3 from 'aws-cdk-lib/aws-s3';
import { EnvironmentConfig } from '../config/environments';

export interface ApiStackProps extends cdk.StackProps {
  readonly envConfig: EnvironmentConfig;
  readonly usersTable: dynamodb.Table;
  readonly cropsTable: dynamodb.Table;
  readonly tripsTable: dynamodb.Table;
  readonly uploadsBucket: s3.Bucket;
}

/**
 * API stack: HTTP API Gateway and Lambda functions.
 * Lambda functions are commented out until .NET AOT publish is configured.
 */
export class ApiStack extends cdk.Stack {
  public readonly httpApiUrl: string;

  constructor(scope: Construct, id: string, props: ApiStackProps) {
    super(scope, id, props);

    const { envConfig } = props;

    // HTTP API Gateway (cheaper than REST API)
    const httpApi = new apigateway.CfnApi(this, 'HttpApi', {
      name: `wereda-connect-${envConfig.name}-api`,
      protocolType: 'HTTP',
      description: 'WeredaConnect HTTP API - Optimized for Ethiopian context',
    });

    this.httpApiUrl = `https://${httpApi.ref}.execute-api.${this.region}.amazonaws.com`;

    // TODO: Add Lambda functions after .NET AOT publish is available
    // const healthCheck = new WeredaLambda(this, 'HealthCheck', {
    //   functionName: `wereda-connect-${envConfig.name}-health`,
    //   description: 'Health check endpoint',
    //   environment: {
    //     USERS_TABLE: props.usersTable.tableName,
    //     CROPS_TABLE: props.cropsTable.tableName,
    //     TRIPS_TABLE: props.tripsTable.tableName,
    //     UPLOADS_BUCKET: props.uploadsBucket.bucketName,
    //   },
    // });
    // props.usersTable.grantReadWriteData(healthCheck.function);
    // props.cropsTable.grantReadWriteData(healthCheck.function);
    // props.tripsTable.grantReadWriteData(healthCheck.function);
    // props.uploadsBucket.grantReadWrite(healthCheck.function);

    // Outputs
    new cdk.CfnOutput(this, 'ApiUrl', {
      value: this.httpApiUrl,
      description: 'API Gateway URL',
    });
  }
}
