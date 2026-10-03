namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    private const string ColumnLayoutScalarFields = "SELECT COUNT_BIG(*) AS column_count, "
        + "COALESCE(SUM(CASE WHEN ty.name IN (N'bit', N'varchar', N'nvarchar', N'varbinary') "
        + "THEN CONVERT(bigint,0) ELSE CONVERT(bigint,c.max_length) END),0) AS fixed_bytes, "
        + "COALESCE(SUM(CASE WHEN ty.name=N'bit' THEN 1 ELSE 0 END),0) AS bit_columns, ";

    private const string ColumnLayoutRowFields = "COALESCE(SUM(CASE "
        + "WHEN ty.name IN (N'varchar',N'nvarchar',N'varbinary') THEN 1 ELSE 0 END),0) AS variable_columns, "
        + "COALESCE(SUM(CASE WHEN ty.name IN (N'varchar',N'nvarchar',N'varbinary') "
        + "AND clustered_key.column_id IS NOT NULL AND c.max_length>24 "
        + "THEN CONVERT(bigint,c.max_length-24) ELSE CONVERT(bigint,0) END),0) AS clustered_variable_extra, ";

    private const string ColumnLayoutScalarSource = "COALESCE(SUM(CASE "
        + "WHEN ty.is_user_defined=0 AND ty.is_assembly_type=0 "
        + "AND c.is_computed=0 AND c.is_sparse=0 AND c.is_column_set=0 AND c.is_filestream=0 "
        + "AND c.is_hidden=0 AND c.generated_always_type=0 AND c.is_rowguidcol=0 "
        + "AND ty.name IN (N'bigint',N'binary',N'bit',N'char',N'date',N'datetime',N'datetime2', "
        + "N'datetimeoffset',N'decimal',N'float',N'int',N'money',N'nchar',N'numeric',N'nvarchar', "
        + "N'real',N'smalldatetime',N'smallint',N'smallmoney',N'time',N'timestamp',N'tinyint', "
        + "N'uniqueidentifier',N'varbinary',N'varchar') THEN 0 ELSE 1 END),0) AS unknown_columns "
        + "FROM sys.columns AS c JOIN sys.types AS ty ON ty.user_type_id=c.user_type_id ";

    private const string ColumnLayoutClusteredJoin = "LEFT JOIN "
        + "(SELECT ic.object_id,ic.column_id FROM sys.index_columns AS ic "
        + "JOIN sys.indexes AS i ON i.object_id=ic.object_id AND i.index_id=ic.index_id "
        + "WHERE i.type=1 AND ic.key_ordinal>0) AS clustered_key "
        + "ON clustered_key.object_id=c.object_id AND clustered_key.column_id=c.column_id ";

    private const string ColumnLayoutSchemaQuery = ColumnLayoutScalarFields + ColumnLayoutScalarSource
        + "WHERE c.object_id=t.object_id";

    /// <summary>Describes only row storage that variable-length row overflow cannot release.</summary>
    /// <param name="definition">The already validated authored scalar column.</param>
    /// <param name="layout">The fixed, packed-bit, or variable-offset contribution.</param>
    /// <returns>True when the storage contribution is represented by the supported scalar contract.</returns>
    internal bool TryGetColumnStorageLayout(
        ExpectedColumnDefinition definition,
        out ColumnStorageLayout layout
    )
    {
        if (!TryGetColumnType(definition, out var type))
        {
            layout = default;

            return false;
        }

        var bytes = type.Name switch
        {
            "char" or "nchar" or "binary" => type.Length ?? 8061,
            "tinyint" => 1,
            "smallint" => 2,
            "date" => 3,
            "int" or "real" or "smalldatetime" or "smallmoney" => 4,
            "time" => type.Scale <= 2 ? 3 : type.Scale <= 4 ? 4 : 5,
            "datetime2" => type.Scale <= 2 ? 6 : type.Scale <= 4 ? 7 : 8,
            "datetimeoffset" => type.Scale <= 2 ? 8 : type.Scale <= 4 ? 9 : 10,
            "bigint" or "datetime" or "float" or "money" or "timestamp" => 8,
            "uniqueidentifier" => 16,
            "decimal" or "numeric" => type.Precision <= 9 ? 5 : type.Precision <= 19 ? 9
                : type.Precision <= 28 ? 13 : 17,
            "bit" or "varchar" or "nvarchar" or "varbinary" => 0,
            _ => 8061,
        };

        layout = new ColumnStorageLayout(bytes, type.Name == "bit",
            type.Name is "varchar" or "nvarchar" or "varbinary", type.Length ?? 0);

        return bytes <= 8060;
    }

    /// <summary>Builds a catalog-only admission code for a missing physical column.</summary>
    /// <param name="intent">The scalar column addition.</param>
    /// <returns>NULL for supported capacity, otherwise a stable physical-layout diagnostic.</returns>
    internal string BuildColumnAdditionLayoutFailureExpression(
        EnsureColumnIntent intent
    )
    {
        if (!TryGetColumnStorageLayout(intent.Definition, out var addition))
        {
            return "N'column_layout_unproven'";
        }

        var bit = addition.IsBit ? 1 : 0;
        var bytes = addition.FixedBytes.ToString(CultureInfo.InvariantCulture);
        var tableId = TableId(intent.Table, intent.Schema);
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Definition.Name);

        // WHY: A gap in the documented column-id lineage does not reveal the
        // retained width of dropped storage. Matching columns need no capacity
        // admission, but a new allocation cannot treat that width as reclaimed.

        // WHY: The correlated schema aggregate is invariant across operations.
        // A compile-time fragment and one interpolated result avoid allocating
        // and copying that catalog SQL repeatedly for each column addition.

        return $"""
            CASE WHEN {exists} THEN CONVERT(nvarchar(128), NULL) ELSE (SELECT CASE
            WHEN layout.column_count >= 1024 THEN N'column_limit'
            WHEN layout.unknown_columns <> 0 OR t.max_column_id_used <> layout.column_count
            THEN N'column_layout_unproven'
            WHEN layout.fixed_bytes + {bytes} + (layout.bit_columns + {bit} + 7) / 8
            + 6 + (layout.column_count + 8) / 8 > 8060
            THEN N'column_fixed_row_limit' ELSE NULL END FROM sys.tables AS t
            CROSS APPLY ({ColumnLayoutSchemaQuery}) AS layout WHERE t.object_id = {tableId}) END
            """;
    }

    /// <summary>Builds one compact aggregate over documented scalar catalog metadata.</summary>
    /// <param name="objectId">An internal trusted object-id SQL expression.</param>
    /// <returns>Column count, fixed bytes, packed-bit count, variable offsets, and unknown-facet count.</returns>
    internal static string BuildColumnLayoutCatalogQuery(
        string objectId
    )
    {
        // WHY: Declared variable payload and offsets are not mandatory schema
        // storage: trailing NULL/empty variable fields permit valid near-limit
        // tables. Populated-row safety is a separate delayed admission proof.
        return $"{ColumnLayoutScalarFields}{ColumnLayoutRowFields}{ColumnLayoutScalarSource}"
            + $"{ColumnLayoutClusteredJoin}WHERE c.object_id={objectId}";
    }

    /// <summary>Checks the separate conservative capacity proof for materializing existing rows.</summary>
    /// <param name="intent">The authored column addition.</param>
    /// <returns>A metadata-only predicate requiring an empty-row proof, or null when no row materializes.</returns>
    internal string? BuildColumnAdditionRowCapacityPredicate(
        EnsureColumnIntent intent
    )
    {
        if (!TryGetColumnStorageLayout(intent.Definition, out var addition)
            || !ColumnAdditionMaterializesRows(intent.Definition, addition))
        {
            return null;
        }

        var bits = addition.IsBit ? 1 : 0;
        var variables = addition.IsVariable ? 1 : 0;
        var bytes = addition.FixedBytes.ToString(CultureInfo.InvariantCulture);
        // WHY: Schema admission alone permits near-limit variable columns
        // whose NULL/empty values fit. Backfilling a populated row additionally
        // needs offsets and overflow roots. Variable clustered-key columns
        // cannot overflow, so their declared width replaces the root allowance.
        // Without that conservative upper-bound proof, require no rows.

        return "EXISTS(SELECT 1 FROM (" + BuildColumnLayoutCatalogQuery(TableId(intent.Table, intent.Schema))
            + ") AS layout WHERE "
            + $"layout.variable_columns + {variables} > 0 AND layout.fixed_bytes + {bytes} "
            + $"+ (layout.bit_columns + {bits} + 7) / 8 + 6 + (layout.column_count + 8) / 8 "
            + $"+ 2 + 26 * (layout.variable_columns + {variables}) "
            + "+ layout.clustered_variable_extra > 8060)";
    }

    /// <summary>
    /// Determines whether ADD can extend stored rows rather than append an unmaterialized nullable variable.
    /// </summary>
    /// <param name="definition">The authored scalar definition.</param>
    /// <param name="layout">The scalar storage contribution.</param>
    /// <returns>True when a populated-row allocation proof can be required.</returns>
    internal static bool ColumnAdditionMaterializesRows(
        ExpectedColumnDefinition definition,
        ColumnStorageLayout layout
    ) => !layout.IsVariable || !definition.IsNullable
        || definition.DefaultValue.Kind != SafeMigrationDefaultValueKind.None && !definition.DefaultValue.IsNullLiteral;

    /// <summary>The unavoidable scalar row-storage contribution of an authored column.</summary>
    /// <param name="FixedBytes">Fixed storage in bytes, excluding packed bit storage.</param>
    /// <param name="IsBit">Whether this column participates in the packed bit region.</param>
    /// <param name="IsVariable">Whether this column requires variable-offset metadata.</param>
    /// <param name="MaximumVariableBytes">
    /// Declared variable width, used only for non-overflowable clustered keys.
    /// </param>
    internal readonly record struct ColumnStorageLayout(
        int FixedBytes,
        bool IsBit,
        bool IsVariable,
        int MaximumVariableBytes = 0
    );
}
