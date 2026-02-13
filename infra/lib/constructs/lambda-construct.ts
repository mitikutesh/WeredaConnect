import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as path from 'path';

export interface WeredaLambdaProps {
  readonly functionName: string;
  readonly description: string;
  readonly handlerName?: string;
  readonly environment?: Record<string, string>;
  readonly memorySize?: number;
  readonly timeout?: cdk.Duration;
}

/**
 * Reusable Lambda construct for .NET 10 Native AOT functions:
 * - ARM64 architecture (Graviton2 — better price/performance)
 * - Custom runtime (provided.al2023) for AOT binary
 * - 512MB default memory, 10s timeout
 */
export class WeredaLambda extends Construct {
  public readonly function: lambda.Function;

  constructor(scope: Construct, id: string, props: WeredaLambdaProps) {
    super(scope, id);

    this.function = new lambda.Function(this, 'Function', {
      functionName: props.functionName,
      runtime: lambda.Runtime.PROVIDED_AL2023,
      handler: 'bootstrap',
      code: lambda.Code.fromAsset(
        path.join(__dirname, '..', '..', '..', 'server', 'bin', 'Release', 'net10.0', 'linux-arm64', 'publish'),
      ),
      architecture: lambda.Architecture.ARM_64,
      memorySize: props.memorySize ?? 512,
      timeout: props.timeout ?? cdk.Duration.seconds(10),
      description: props.description,
      environment: {
        ...props.environment,
        ...(props.handlerName ? { FUNCTION_HANDLER: props.handlerName } : {}),
      },
    });
  }
}
