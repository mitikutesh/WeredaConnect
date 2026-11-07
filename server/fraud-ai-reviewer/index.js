'use strict';

// FRD-19: Lambda entry point of the AI reviewer (fraud-detection-architecture.md section 21). Invoked
// synchronously by the advisory cron only (its role is the one principal allowed to invoke it). The AWS SDK for
// JavaScript v3 ships with the Node.js Lambda runtime, so this function has no dependencies to bundle.

const { review } = require('./src/review');

let client = null;

/** InvokeModel on bedrock-runtime in BEDROCK_REGION (default: the Lambda's own Region). */
async function invokeBedrock(modelId, body) {
  const { BedrockRuntimeClient, InvokeModelCommand } = require('@aws-sdk/client-bedrock-runtime');
  if (client === null) {
    client = new BedrockRuntimeClient({
      region: process.env.BEDROCK_REGION || process.env.AWS_REGION,
      // A throttled call is retried once; the cron's next hourly run is the real retry.
      maxAttempts: 2,
    });
  }

  const response = await client.send(new InvokeModelCommand({
    modelId,
    contentType: 'application/json',
    accept: 'application/json',
    body: JSON.stringify(body),
  }));
  return JSON.parse(new TextDecoder().decode(response.body));
}

exports.handler = async (event) => review(event, {
  env: process.env,
  invoke: invokeBedrock,
  log: (line) => console.log(line),
});
