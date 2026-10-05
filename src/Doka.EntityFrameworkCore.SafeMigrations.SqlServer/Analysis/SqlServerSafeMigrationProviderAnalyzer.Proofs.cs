namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    /// <summary>Evaluates one fresh outer proof through a parameterized scalar statement at its original gate.</summary>
    private static string BuildDelayedScalarProof(
        string expression,
        string variable,
        string storeType,
        string definitions,
        string parameterArguments
    )
    {
        // WHY: The local replay repeatedly compiled the heavy catalog queries inside the procedural
        // dispatcher, while its inner SELECT plans were reused. A separate scalar SELECT permits the
        // same plan reuse for proof SQL, never for proof values. Each invocation writes a private output
        // before its existing gate, so collation/default/name binding and rejection priority stay ordered.
        var scalar = ("SELECT @doka_proof = (" + expression + ");")
            .Replace("'", "''", StringComparison.Ordinal);

        return "DECLARE " + variable + " " + storeType + "; EXEC sys.sp_executesql N'" + scalar
            + "', N'" + definitions + ", @doka_proof " + storeType + " OUTPUT', "
            + "@doka_ordinal = @doka_ordinal" + parameterArguments
            + ", @doka_proof = " + variable + " OUTPUT; ";
    }
}
