using Amazon.DynamoDBv2;
using WeredaConnect.Server.Data;

namespace WeredaConnect.Server.Services;

/// <summary>
/// ESC-20: the ledger's environment. <c>ESCROW_LEDGER_TABLE</c> wires the table into a Lambda (every Lambda
/// that settles, records or reads order escrows gets it); <c>ESCROW_LEDGER_ENABLED</c> (<c>true</c>) makes new
/// order escrows born on the ledger. Turning the flag off sends new escrows back to the 1.x path (spec K3);
/// escrows already on the ledger keep settling there as long as the table is wired.
/// </summary>
internal static class EscrowLedgerConfig
{
    public const string TableVariable = "ESCROW_LEDGER_TABLE";
    public const string FlagVariable = "ESCROW_LEDGER_ENABLED";

    public static bool BirthEnabled => string.Equals(
        Environment.GetEnvironmentVariable(FlagVariable), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The ledger repository, or null when the table, the escrows table or the audit table is not set.</summary>
    public static IEscrowLedgerRepository? FromEnvironment(IAmazonDynamoDB dynamoDb, string? escrowsTable, string? auditTable)
    {
        var ledgerTable = Environment.GetEnvironmentVariable(TableVariable);
        return string.IsNullOrEmpty(ledgerTable) || string.IsNullOrEmpty(escrowsTable) || string.IsNullOrEmpty(auditTable)
            ? null
            : new EscrowLedgerRepository(dynamoDb, ledgerTable, escrowsTable, auditTable);
    }
}
