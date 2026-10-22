namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private CollationContract BuildCollationContract(
        string table,
        SafeMigrationCollationIdentifier? expectedCollation,
        bool mariaDbJsonAlias
    )
    {
        if (mariaDbJsonAlias)
        {
            // Doka materializes MariaDB's JSON alias as LONGTEXT with the
            // binary JSON collation. Compare the provider-owned physical
            // representation, not the table default inherited by ordinary text.
            return new CollationContract(
                "LOWER(c.COLLATION_NAME) = 'utf8mb4_bin'",
                CollationExpectationKind.Expression,
                "'utf8mb4_bin'");
        }

        if (expectedCollation is not null)
        {
            var expectedExpression = Literal(expectedCollation.Name);

            return new CollationContract(
                $"c.COLLATION_NAME <=> {expectedExpression}",
                CollationExpectationKind.Expression,
                expectedExpression);
        }

        // WHY: The expected table-default expression is only needed for
        // diagnostics. Retaining the table name avoids materializing a second
        // catalog subquery for every comparison-only operation.
        return new CollationContract(
            "(c.COLLATION_NAME IS NULL OR c.COLLATION_NAME <=> "
            + "(SELECT t.TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES t "
            + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(table)}))",
            CollationExpectationKind.InheritedTableDefault,
            table);
    }

    private string BuildCollationExpectedExpression(
        CollationContract contract
    ) => contract.ExpectationKind switch
    {
        CollationExpectationKind.Expression => contract.ExpectationSource,
        CollationExpectationKind.InheritedTableDefault =>
            "COALESCE((SELECT t.TABLE_COLLATION FROM INFORMATION_SCHEMA.TABLES t "
            + $"WHERE t.TABLE_SCHEMA = DATABASE() AND t.TABLE_NAME = {Literal(contract.ExpectationSource)}), 'none')",
        _ => throw new InvalidOperationException(
            $"Unsupported collation expectation kind '{contract.ExpectationKind}'."),
    };

    private enum CollationExpectationKind : byte
    {
        Expression = 1,
        InheritedTableDefault = 2,
    }

    private readonly record struct CollationContract(
        string MatchExpression,
        CollationExpectationKind ExpectationKind,
        string ExpectationSource
    );
}
