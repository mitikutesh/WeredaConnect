Deploy WeredaConnect to AWS.

The user should provide: $ARGUMENTS (environment name: dev, staging, or prod)

Steps:

1. Parse the environment from arguments. Default to `dev` if not specified. Reject unknown environments.

2. **Pre-flight checks** (run in parallel):
   - Verify AWS credentials: `aws sts get-caller-identity --region me-south-1`
   - Verify Docker is running: `docker info > /dev/null 2>&1`
   - Check current branch and uncommitted changes: `git status --short`
   - If deploying to `prod`, STOP and confirm with the user before proceeding

3. **Run tests** (run in parallel):
   - `cd server && dotnet test --no-restore --verbosity quiet`
   - `cd mobile && flutter analyze --no-fatal-infos`
   - `cd infra && npx cdk synth --quiet --context environment=$ENV > /dev/null`
   - If any test fails, STOP and report the failure

4. **Deploy backend**:
   - Run `./scripts/deploy.sh $ENV` and stream output
   - This handles: Docker AOT build -> CDK deploy -> all stacks

5. **Post-deploy verification** (run in parallel):
   - Health check: `curl -s https://<api-url>/health`
   - Check Lambda cold start logs: `aws logs tail /aws/lambda/wereda-connect-$ENV-health --since 5m --region me-south-1 | head -5`
   - Verify DynamoDB tables exist: `aws dynamodb list-tables --region me-south-1 --query 'TableNames[?contains(@, `wereda-connect-$ENV`)]'`

6. **Report results**:
   - API URL (from CDK outputs)
   - Stack status for each CDK stack
   - Any warnings or errors
   - Remind user about web app deploys (CI/CD triggers on push to main, or manual via `gh workflow run`)

For prod deployments, also remind:
- Verify `DEV_FAYDA_BYPASS` and `TELEBIRR_MOCK` are NOT set (CDK guard should catch this)
- Check WAF rules are attached
- Verify CloudWatch alarms are configured
