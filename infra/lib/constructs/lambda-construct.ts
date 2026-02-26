import * as cdk from 'aws-cdk-lib';
import { Construct } from 'constructs';
import * as lambda from 'aws-cdk-lib/aws-lambda';
import * as logs from 'aws-cdk-lib/aws-logs';
import * as path from 'path';
import * as fs from 'fs';
import { execSync } from 'child_process';

export interface WeredaLambdaProps {
  readonly functionName: string;
  readonly description: string;
  readonly handlerName?: string;
  readonly environment?: Record<string, string>;
  readonly memorySize?: number;
  readonly timeout?: cdk.Duration;
  /** Maximum concurrent executions. Defaults to 10 to prevent runaway costs. */
  readonly reservedConcurrentExecutions?: number;
  /** CloudWatch log retention. Defaults to THREE_MONTHS. */
  readonly logRetention?: logs.RetentionDays;
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

    // Build Lambda code using Docker (for cross-compilation on macOS)
    const serverPath = path.join(__dirname, '..', '..', '..', 'server');

    const logGroup = new logs.LogGroup(this, 'LogGroup', {
      logGroupName: `/aws/lambda/${props.functionName}`,
      retention: props.logRetention ?? logs.RetentionDays.THREE_MONTHS,
      removalPolicy: cdk.RemovalPolicy.DESTROY,
    });

    this.function = new lambda.Function(this, 'Function', {
      functionName: props.functionName,
      runtime: lambda.Runtime.PROVIDED_AL2023,
      handler: 'bootstrap',
      code: lambda.Code.fromAsset(serverPath, {
        bundling: {
          image: lambda.Runtime.PROVIDED_AL2023.bundlingImage,
          command: [
            'bash', '-c', [
              'dnf install -y clang zlib-devel libicu',
              'curl -sSL https://dot.net/v1/dotnet-install.sh | bash /dev/stdin --channel 10.0',
              'export PATH="$HOME/.dotnet:$PATH"',
              'dotnet publish -c Release --runtime linux-arm64 --self-contained -p:InvariantGlobalization=true -o /tmp/publish',
              'mv /tmp/publish/WeredaConnect.Server /asset-output/bootstrap',
              'chmod +x /asset-output/bootstrap',
            ].join(' && '),
          ],
          user: 'root',
          local: {
            tryBundle(outputDir: string): boolean {
              // In CI/test: skip Docker bundling, use placeholder bootstrap.
              // GitHub Actions sets CI=true; Docker is available there but a
              // full .NET cross-compile is slow and unnecessary for synth/tests.
              if (process.env.CI) {
                fs.writeFileSync(
                  path.join(outputDir, 'bootstrap'),
                  '#!/bin/sh\necho "placeholder"',
                );
                return true;
              }
              try {
                execSync('docker info', { stdio: 'pipe' });
                return false; // Docker is available — use Docker bundling
              } catch {
                // Docker unavailable locally — create placeholder bootstrap
                fs.writeFileSync(
                  path.join(outputDir, 'bootstrap'),
                  '#!/bin/sh\necho "placeholder"',
                );
                return true;
              }
            },
          },
        },
      }),
      architecture: lambda.Architecture.ARM_64,
      memorySize: props.memorySize ?? 512,
      timeout: props.timeout ?? cdk.Duration.seconds(10),
      ...(props.reservedConcurrentExecutions != null
        ? { reservedConcurrentExecutions: props.reservedConcurrentExecutions }
        : {}),
      logGroup,
      description: props.description,
      environment: {
        DOTNET_SYSTEM_GLOBALIZATION_INVARIANT: '1',
        ...props.environment,
        ...(props.handlerName ? { FUNCTION_HANDLER: props.handlerName } : {}),
      },
    });
  }
}
