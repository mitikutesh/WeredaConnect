import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as cloudwatch from 'aws-cdk-lib/aws-cloudwatch';
import { EnvironmentConfig } from '../config/environments';

export interface MonitoringStackProps extends cdk.StackProps {
  readonly envConfig: EnvironmentConfig;
  readonly apiUrl: string;
}

/**
 * Monitoring stack: CloudWatch dashboards and alarms.
 * Provides observability for debugging intermittent connectivity issues.
 */
export class MonitoringStack extends cdk.Stack {
  constructor(scope: Construct, id: string, props: MonitoringStackProps) {
    super(scope, id, props);

    const { envConfig } = props;

    // CloudWatch Dashboard
    const dashboard = new cloudwatch.Dashboard(this, 'Dashboard', {
      dashboardName: `wereda-connect-${envConfig.name}`,
    });

    // Placeholder widget — will be populated when Lambda functions are deployed
    dashboard.addWidgets(
      new cloudwatch.TextWidget({
        markdown: [
          `# WeredaConnect ${envConfig.name} Dashboard`,
          '',
          `API URL: ${props.apiUrl}`,
          '',
          'Lambda metrics will be added after function deployment.',
        ].join('\n'),
        width: 24,
        height: 4,
      }),
    );

    // Output
    new cdk.CfnOutput(this, 'DashboardUrl', {
      value: `https://${this.region}.console.aws.amazon.com/cloudwatch/home?region=${this.region}#dashboards:name=wereda-connect-${envConfig.name}`,
      description: 'CloudWatch Dashboard URL',
    });
  }
}
