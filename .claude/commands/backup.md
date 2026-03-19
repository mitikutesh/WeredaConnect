Create, verify, or restore DynamoDB backups. Check Point-in-Time Recovery (PITR) status.

The user may provide: $ARGUMENTS ("check", "create", "list", "restore <table> <datetime>", or specific table name)

Prerequisites: AWS CLI configured with access to `me-south-1`.

Steps:

1. **Parse action**:
   - "check" or no argument: verify PITR is enabled on all tables
   - "create": create on-demand backups for all tables
   - "list": list existing backups
   - "restore <table> <datetime>": restore a specific table

2. **Check PITR status** (always run first):
   ```bash
   # List all WeredaConnect tables and their PITR status
   for table in $(aws dynamodb list-tables --region me-south-1 \
     --query 'TableNames[?contains(@, `wereda-connect`)]' --output text); do
     pitr=$(aws dynamodb describe-continuous-backups \
       --table-name "$table" --region me-south-1 \
       --query 'ContinuousBackupsDescription.PointInTimeRecoveryDescription.PointInTimeRecoveryStatus' \
       --output text 2>/dev/null)
     echo "$table: PITR=$pitr"
   done
   ```

   Expected tables (12):
   - users, crops, trips, orders, deliveries, escrows
   - escrow-audit, escrow-idempotency, payments
   - fraud-signals, rate-limits, sync-metadata

   Flag any table where PITR is DISABLED.

3. **Create on-demand backups** (if "create"):
   ```bash
   TIMESTAMP=$(date -u +%Y%m%d-%H%M%S)
   for table in $(aws dynamodb list-tables --region me-south-1 \
     --query 'TableNames[?contains(@, `wereda-connect`)]' --output text); do
     echo "Backing up $table..."
     aws dynamodb create-backup \
       --table-name "$table" \
       --backup-name "${table}-${TIMESTAMP}" \
       --region me-south-1 \
       --query 'BackupDetails.{Name:BackupName,Status:BackupStatus,Size:BackupSizeBytes}' \
       --output table
   done
   ```

4. **List existing backups** (if "list"):
   ```bash
   aws dynamodb list-backups --region me-south-1 \
     --query 'BackupSummaries[?contains(TableName, `wereda-connect`)].{Table:TableName,Name:BackupName,Created:BackupCreationDateTime,Status:BackupStatus,Size:BackupSizeBytes}' \
     --output table
   ```

5. **Restore from backup** (if "restore"):

   **IMPORTANT**: Restoring creates a NEW table. It does NOT overwrite the existing table.

   ```bash
   # Restore to point in time
   aws dynamodb restore-table-to-point-in-time \
     --source-table-name <table> \
     --target-table-name <table>-restored-$(date +%Y%m%d) \
     --restore-date-time <ISO8601-datetime> \
     --region me-south-1
   ```

   After restore:
   - Verify restored table has expected item count
   - If replacing the original, rename tables (manual process — confirm with user first)

6. **Report results**:

   ```
   ## Backup Report

   ### PITR Status
   | Table | PITR | Last Restorable |
   |-------|------|-----------------|
   | wereda-connect-dev-users | ENABLED/DISABLED | [datetime] |
   ...

   ### On-Demand Backups
   - [count] backups exist
   - Latest: [name] created [datetime]

   ### Action Items
   1. [if PITR disabled: "Enable PITR on [table] — add to CDK stack or run AWS CLI"]
   2. [if no recent backups: "Create on-demand backup — data is at risk"]
   ```

7. If PITR is disabled on any table, offer to enable it:
   ```bash
   aws dynamodb update-continuous-backups \
     --table-name <table> \
     --point-in-time-recovery-specification PointInTimeRecoveryEnabled=true \
     --region me-south-1
   ```
   Also suggest adding PITR to the CDK DynamoDB construct so it's always enabled.
