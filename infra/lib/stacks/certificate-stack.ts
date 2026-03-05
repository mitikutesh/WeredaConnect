import * as cdk from "aws-cdk-lib";
import { Construct } from "constructs";
import * as acm from "aws-cdk-lib/aws-certificatemanager";
import { EnvironmentConfig } from "../config/environments";

export interface CertificateStackProps extends cdk.StackProps {
  readonly envConfig: EnvironmentConfig;
}

/**
 * ACM certificate stack — MUST be deployed in us-east-1.
 *
 * CloudFront requires ACM certificates in us-east-1. This is a hard AWS
 * constraint. The certificate is created here and passed to the WebStack
 * (deployed in me-south-1) via cross-region references.
 */
export class CertificateStack extends cdk.Stack {
  public readonly certificate: acm.ICertificate;

  constructor(scope: Construct, id: string, props: CertificateStackProps) {
    super(scope, id, props);

    const { domain } = props.envConfig;

    this.certificate = new acm.Certificate(this, "WebCertificate", {
      domainName: domain.zoneName,
      subjectAlternativeNames: [`*.${domain.zoneName}`],
      validation: acm.CertificateValidation.fromDns(),
    });

    new cdk.CfnOutput(this, "CertificateArn", {
      value: this.certificate.certificateArn,
      description: "ACM certificate ARN for CloudFront (us-east-1)",
    });
  }
}
