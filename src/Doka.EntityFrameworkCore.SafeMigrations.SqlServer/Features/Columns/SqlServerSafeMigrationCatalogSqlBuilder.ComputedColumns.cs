namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationCatalogSqlBuilder
{
    /// <summary>Identifies private exact DROP completeness carried by the existing classifier reason field.</summary>
    internal const string EmptyGeneratedDropSetCode = "generated_column_drop_empty_set_proof";

    private string? GetUnsupportedComputedColumnFeature(ExpectedColumnDefinition definition)
    {
        var expression = SqlServerComputedColumnContract.Expression(definition);
        if (expression is null)
        {
            return "opaque_sql_expression";
        }

        if (!TryGetColumnType(definition, out var type))
        {
            return "column_type_mapping";
        }

        if (definition.Collation is not null && !type.IsCharacter)
        {
            return "column_collation_unproven";
        }

        if (!SqlServerComputedColumnContract.IsDeterministic(expression)
            || _expressionRenderer.GetUnsupportedFeature(expression) is not null)
        {
            return "computed_expression_unproven";
        }

        if ((definition.CapturedComputedSql ?? definition.ComputedColumnSql) is { } source
            && (!SqlServerSourceExpressionRoles.IsNativeScalar(expression)
                || !SafeMigrationSqlExpressionParser.IsCapturedSourceDialectCompatible(source, '[')))
        {
            // WHY: Captured SQL retains native scalar/predicate and quote roles;
            // it must not inherit conversions supplied only by the AST renderer.
            return "computed_expression_unproven";
        }

        if (!definition.IsNullable && !SqlServerComputedColumnContract.IsNonNull(expression))
        {
            // WHY: EF emits neither NULL nor NOT NULL on computed columns, even
            // for PERSISTED. A non-null model therefore needs a semantic proof,
            // not an optimistic interpretation of the model's CLR value type.
            return "computed_column_nullability_unproven";
        }

        return definition.IsRowVersion ? "computed_column_storage_unproven" : null;
    }

    private SqlServerSafeMigrationRuntimePlan BuildEnsureComputedColumn(EnsureColumnIntent intent)
    {
        var table = TableExists(intent.Table, intent.Schema);
        var exists = ColumnExists(intent.Table, intent.Schema, intent.Definition.Name);
        var matching = ColumnMatches(intent.Table, intent.Schema, intent.Definition);
        var support = ComputedVariable(intent.Definition, "support");
        var preamble = BuildComputedColumnPreamble(intent.Table, intent.Schema, [intent.Definition]);
        var rowCapacity = BuildColumnAdditionRowCapacityPredicate(intent);
        var capacityFailure = rowCapacity is null ? string.Empty
            : $"WHEN NOT {exists} AND ({rowCapacity}) AND EXISTS(SELECT TOP (1) 1 FROM "
                + QualifiedTable(intent.Table, intent.Schema) + ") THEN N'data_blocked' ";

        var canRead = $"COALESCE(HAS_PERMS_BY_NAME({Literal(QualifiedTable(intent.Table, intent.Schema))}, "
            + "N'OBJECT',N'SELECT'),0)=1";

        return Plan($"CASE WHEN NOT {table} THEN N'prerequisite_missing' "
            + $"WHEN {support} <> 1 THEN N'unsupported' " + capacityFailure
            + $"WHEN NOT {exists} THEN N'missing' "
            + $"WHEN {matching} THEN N'matching' ELSE N'different' END", Bit(matching)) with
        {
            RequiresDelayedBinding = true,
            CatalogPreambleSql = preamble,
            PostconditionPreambleSql = preamble,
            RequiresLiveDataProof = rowCapacity is not null,
            StateEvaluationGuardExpression = rowCapacity is null ? "1" : Bit(canRead),
            StateEvaluationGuardFailureExpression = rowCapacity is null ? null : "N'unsupported'",
            ClassificationCodeExpression = $"CASE WHEN {support} = -1 "
                + "THEN N'computed_column_operand_identity_unproven' "
                + $"WHEN {support} <> 1 "
                + "THEN N'computed_column_native_type_unproven' ELSE NULL END",
            ColumnLayoutFailureExpression = intent.Definition.IsStored == true
                ? BuildColumnAdditionLayoutFailureExpression(intent)
                : "CASE WHEN (SELECT COUNT(*) FROM sys.columns WHERE object_id = "
                    + TableId(intent.Table, intent.Schema) + ") >= 1024 AND NOT " + exists
                    + " THEN N'column_limit' ELSE NULL END",
        };
    }

    /// <summary>Uses SQL Server's own expression compiler instead of stripping significant SQL tokens.</summary>
    private string BuildComputedColumnPreamble(
        string table,
        string? schema,
        IReadOnlyList<ExpectedColumnDefinition> columns,
        ExpectedTableDefinition? authoredTable = null,
        bool captureBindings = false
    )
    {
        var result = new StringBuilder();
        foreach (var column in columns)
        {
            if (!SqlServerComputedColumnContract.IsComputed(column))
            {
                continue;
            }

            var expression = SqlServerComputedColumnContract.Expression(column)
                ?? throw new UnreachableException();

            var referenced = new HashSet<string>(StringComparer.Ordinal);
            SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression, referenced);
            var names = referenced.Order(StringComparer.Ordinal).ToArray();
            var definitionVariable = ComputedVariable(column, "definition");
            var supportVariable = ComputedVariable(column, "support");
            var nullableVariable = ComputedVariable(column, "nullable");
            var indexableVariable = ComputedVariable(column, "indexable");
            var includeableVariable = ComputedVariable(column, "includeable");
            var collationVariable = ComputedVariable(column, "collation");
            var bindingsVariable = captureBindings ? ComputedVariable(column, "bindings") : null;
            var temporary = "#doka_sm_computed_" + ContractFingerprint("computed_probe", schema, table,
                column.Name, ComputedSql(column, expression)).AsSpan(0, 24).ToString();

            result.Append("DECLARE ").Append(definitionVariable).Append(" nvarchar(max) = NULL, ")
                .Append(supportVariable).Append(" int = 0, ").Append(nullableVariable).Append(" bit = NULL;\n");

            result.Append("DECLARE ").Append(indexableVariable).Append(" int = 0;\n");
            result.Append("DECLARE ").Append(includeableVariable).Append(" int = 0;\n");
            result.Append("DECLARE ").Append(collationVariable).Append(" nvarchar(128) = NULL;\n");
            if (captureBindings)
            {
                result.Append("DECLARE ").Append(bindingsVariable).Append(" nvarchar(max)=N'';\n");
            }

            var probe = new StringBuilder("SET NOCOUNT ON; IF OBJECT_ID(N'tempdb..")
                .Append(temporary).Append("') IS NOT NULL THROW 51004, N'Computed probe scope occupied', 1; ");

            if (names.Length != 0)
            {
                AppendLiveComputedOperands(probe, table, schema, names, temporary, column.Name, authoredTable,
                    captureBindings);
            }
            else
            {
                // WHY: SQL Server requires an ordinary field beside computed
                // members. Resolve its private name under tempdb's identifier
                // collation, which can differ from the user's database collation.
                var chooseOperand = "SELECT @name=CASE WHEN @target COLLATE CATALOG_DEFAULT="
                    + "N'__doka_operand' THEN N'__doka_operand_other' ELSE N'__doka_operand' END;";

                probe.Append("DECLARE @auxiliary sysname; EXEC tempdb.sys.sp_executesql ")
                    .Append(RenderStringLiteral(chooseOperand))
                    .Append(",N'@target sysname,@name sysname OUTPUT',@target=")
                    .Append(RenderStringLiteral(column.Name)).Append(",@name=@auxiliary OUTPUT; ")
                    .Append("DECLARE @compiled nvarchar(max)=N'CREATE TABLE ").Append(Delimited(temporary))
                    .Append(" ('+QUOTENAME(@auxiliary)+N' int NULL); '; ");
            }

            var generated = new StringBuilder("ALTER TABLE ").Append(Delimited(temporary)).Append(" ADD ");
            AppendComputedProbeDefinition(generated, column, expression);
            generated.Append("; ");

            generated.Append("SELECT @definition = cc.definition, @nullable = c.is_nullable, ")
                .Append("@collation=c.collation_name, @support = CASE WHEN ")
                .Append(ComputedProbeTypePredicate(column))
                .Append(column.IsNullable || SqlServerComputedColumnContract.IsNonNull(expression)
                    ? string.Empty : " AND c.is_nullable = 0")
                .Append(" THEN 1 ELSE 0 END FROM tempdb.sys.computed_columns cc ")
                .Append("JOIN tempdb.sys.columns c ON c.object_id=cc.object_id AND c.column_id=cc.column_id ")
                .Append("JOIN tempdb.sys.types ty ON ty.user_type_id=c.user_type_id ")
                .Append("WHERE cc.object_id=OBJECT_ID(N'tempdb..").Append(temporary)
                .Append("') AND cc.name=").Append(RenderStringLiteral(column.Name)).Append(';');

            var indexProperties = "SELECT @indexable=CASE WHEN "
                + "COLUMNPROPERTY(cc.object_id,cc.name,N'IsDeterministic')=1 "
                + "AND (COLUMNPROPERTY(cc.object_id,cc.name,N'IsPrecise')=1 OR cc.is_persisted=1) "
                + "AND COLUMNPROPERTY(cc.object_id,cc.name,N'IsIndexable')=1 THEN 1 ELSE 0 END, "
                + "@includeable=CASE WHEN COLUMNPROPERTY(cc.object_id,cc.name,N'IsDeterministic')=1 "
                + "THEN 1 ELSE 0 END "
                + "FROM sys.computed_columns cc WHERE cc.object_id=OBJECT_ID(N'tempdb.." + temporary
                + "') AND cc.name=" + RenderStringLiteral(column.Name) + ";";

            // WHY: COLUMNPROPERTY resolves object ids in its current database.
            // The compiler-owned table lives in tempdb, so read those properties
            // through a tempdb child scope while the owner scope is still alive.
            generated.Append(" EXEC tempdb.sys.sp_executesql ").Append(RenderStringLiteral(indexProperties))
                .Append(",N'@indexable int OUTPUT,@includeable int OUTPUT',")
                .Append("@indexable=@indexable OUTPUT,@includeable=@includeable OUTPUT;");

            // WHY: A temporary table created in a child EXEC disappears when
            // that child returns. CREATE, ADD and catalog acquisition therefore
            // execute together in that same child, with explicit output bindings.
            probe.Append("SET @compiled+=").Append(RenderStringLiteral(generated.ToString()))
                .Append("; EXEC sys.sp_executesql @compiled, ")
                .Append("N'@definition nvarchar(max) OUTPUT, @nullable bit OUTPUT, ")
                .Append("@support int OUTPUT, @indexable int OUTPUT, @includeable int OUTPUT, ")
                .Append("@collation nvarchar(128) OUTPUT', ")
                .Append("@definition=@definition OUTPUT,@nullable=@nullable OUTPUT,@support=@support OUTPUT,")
                .Append("@indexable=@indexable OUTPUT,@includeable=@includeable OUTPUT,@collation=@collation OUTPUT;");

            result.Append("BEGIN TRY EXEC sys.sp_executesql ").Append(Literal(probe.ToString()))
                .Append(", N'@definition nvarchar(max) OUTPUT, @nullable bit OUTPUT, ")
                .Append("@support int OUTPUT, @indexable int OUTPUT, @includeable int OUTPUT, ")
                .Append("@collation nvarchar(128) OUTPUT")
                .Append(captureBindings ? ", @bindings nvarchar(max) OUTPUT" : string.Empty).Append("', ")
                .Append("@definition=").Append(definitionVariable).Append(" OUTPUT, @nullable=")
                .Append(nullableVariable).Append(" OUTPUT, @support=").Append(supportVariable)
                .Append(" OUTPUT, @indexable=").Append(indexableVariable)
                .Append(" OUTPUT, @includeable=").Append(includeableVariable)
                .Append(" OUTPUT, @collation=").Append(collationVariable)
                .Append(" OUTPUT");
            if (captureBindings)
            {
                result.Append(", @bindings=").Append(bindingsVariable).Append(" OUTPUT");
            }

            result.Append("; END TRY BEGIN CATCH SET ").Append(supportVariable).Append("=0; END CATCH;\n");
        }

        return result.ToString();
    }

    private void AppendComputedProbeDefinition(
        StringBuilder destination,
        ExpectedColumnDefinition column,
        SafeMigrationSqlExpression expression
    )
    {
        destination.Append(Delimited(column.Name)).Append(" AS ").Append(ComputedSql(column, expression));
        if (column.Collation is not null)
        {
            destination.Append(" COLLATE ").Append(Delimited(column.Collation.Name));
        }

        if (column.IsStored == true)
        {
            destination.Append(" PERSISTED");
        }
    }

    /// <summary>Captures native inferred facets and SET eligibility for an authored index member.</summary>
    internal string BuildProjectedComputedKeyProofSql(
        string table,
        string? schema,
        ExpectedColumnDefinition definition,
        ExpectedTableDefinition? authoredTable,
        int ordinal
    )
    {
        var preamble = BuildComputedColumnPreamble(table, schema, [definition], authoredTable, captureBindings: true);
        var select = "SELECT " + ordinal.ToString(CultureInfo.InvariantCulture) + ", CASE WHEN "
            + ComputedVariable(definition, "support") + "=1 THEN CONVERT(int,"
            + ComputedVariable(definition, "nullable") + ")+CASE WHEN "
            + ComputedVariable(definition, "indexable") + "=1 AND (" + ComputedIndexSessionPredicate
            + ") THEN 1 WHEN " + ComputedVariable(definition, "includeable") + "=1 AND ("
            + ComputedIndexSessionPredicate + ") THEN 5 ELSE 3 END ELSE 0 END, "
            + ComputedVariable(definition, "bindings") + ";";

        return "EXEC sys.sp_executesql " + Literal(preamble + select) + ";";
    }

    private string ComputedProbeTypePredicate(ExpectedColumnDefinition definition)
    {
        if (!TryGetColumnType(definition, out var type))
        {
            return "1 = 0";
        }

        return "ty.name = " + RenderStringLiteral(type.Name)
            + (type.Length is { } length ? " AND c.max_length = " + length.ToString(CultureInfo.InvariantCulture)
                : string.Empty)
            + (type.Precision is { } precision
                ? " AND c.precision = " + precision.ToString(CultureInfo.InvariantCulture) : string.Empty)
            + (type.Scale is { } scale ? " AND c.scale = " + scale.ToString(CultureInfo.InvariantCulture)
                : string.Empty);
    }

    private static string ComputedVariable(ExpectedColumnDefinition definition, string facet)
        => "@doka_computed_" + facet + "_"
            + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(definition.Name)))
                .AsSpan(0, 24).ToString();

    private string ComputedSql(ExpectedColumnDefinition definition, SafeMigrationSqlExpression expression)
        // WHY: A completely parsed authored literal can still have different native
        // typing from its CLR-rendered form (for example a large numeric constant).
        // Compile exactly the same text as Core's baseline, not a rewritten spelling.
        => definition.CapturedComputedSql ?? definition.ComputedColumnSql ?? _expressionRenderer.Render(expression);

    private static string[] ComputedPrerequisiteColumns(ExpectedColumnDefinition definition)
    {
        var expression = SqlServerComputedColumnContract.Expression(definition);
        if (expression is null)
        {
            return [];
        }

        var columns = new HashSet<string>(StringComparer.Ordinal);
        SafeMigrationSqlExpressionInspector.CollectIdentifiers(expression, columns);

        return columns.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Recreates only referenced built-in operand metadata, without selecting application rows.</summary>
    private void AppendLiveComputedOperands(
        StringBuilder probe,
        string table,
        string? schema,
        string[] names,
        string temporary,
        string target,
        ExpectedTableDefinition? authoredTable,
        bool captureBindings
    )
    {
        // WHY: SELECT INTO can transfer identity and adds user-table SELECT
        // requirements. Exact catalog-only built-in metadata needs neither, and
        // rejects computed/UDT operands that SQL Server cannot reference safely.
        probe.Append("DECLARE @operands nvarchar(max), @identities nvarchar(max), @operand_count int; ")
            .Append("WITH resolved AS (SELECT expected.ordinal, ROW_NUMBER() OVER(PARTITION BY ")
            .Append("COALESCE(authored.name,c.name) COLLATE CATALOG_DEFAULT ORDER BY expected.ordinal) AS duplicate, ")
            .Append("COALESCE(authored.name,c.name) AS physical_name, authored.ordinal AS authored_ordinal, ")
            .Append("CASE WHEN authored.definition IS NOT NULL THEN authored.definition ELSE ")
            .Append("QUOTENAME(c.name)+N' '+QUOTENAME(ty.name)+CASE ")
            .Append("WHEN ty.name IN (N'varchar',N'char',N'varbinary',N'binary') ")
            .Append("THEN N'('+CASE WHEN c.max_length=-1 THEN N'max' ")
            .Append("ELSE CONVERT(nvarchar(10),c.max_length) END+N')' ")
            .Append("WHEN ty.name IN (N'nvarchar',N'nchar') THEN N'('+CASE WHEN c.max_length=-1 THEN N'max' ")
            .Append("ELSE CONVERT(nvarchar(10),c.max_length/2) END+N')' ")
            .Append("WHEN ty.name IN (N'decimal',N'numeric') THEN N'('+CONVERT(nvarchar(10),c.precision)")
            .Append("+N','+CONVERT(nvarchar(10),c.scale)+N')' ")
            .Append("WHEN ty.name IN (N'time',N'datetime2',N'datetimeoffset') ")
            .Append("THEN N'('+CONVERT(nvarchar(10),c.scale)+N')' ")
            .Append("WHEN ty.name=N'float' THEN N'('+CONVERT(nvarchar(10),c.precision)+N')' ELSE N'' END ")
            .Append("+CASE WHEN c.collation_name IS NULL THEN N'' ELSE N' COLLATE '+QUOTENAME(c.collation_name) END ")
            .Append("+CASE WHEN c.is_nullable=1 THEN N' NULL' ELSE N' NOT NULL' END END AS definition ")
            .Append("FROM (VALUES ");

        for (var ordinal = 0; ordinal < names.Length; ordinal++)
        {
            if (ordinal != 0)
            {
                probe.Append(", ");
            }

            var name = names[ordinal];
            probe.Append('(').Append(ordinal.ToString(CultureInfo.InvariantCulture)).Append(',')
                .Append(RenderStringLiteral(name)).Append(')');
        }

        probe.Append(") expected(ordinal,name) LEFT JOIN ");
        if (authoredTable is { Columns.Count: > 0 })
        {
            probe.Append("(VALUES ");
            for (var ordinal = 0; ordinal < authoredTable.Columns.Count; ordinal++)
            {
                if (ordinal != 0)
                {
                    probe.Append(',');
                }

                var authored = authoredTable.Columns[ordinal];
                probe.Append('(').Append(ordinal.ToString(CultureInfo.InvariantCulture)).Append(',')
                    .Append(RenderStringLiteral(authored.Name)).Append(",CONVERT(nvarchar(max),")
                    .Append(RenderStringLiteral(RenderComputedOperand(authored))).Append("))");
            }

            probe.Append(") authored(ordinal,name,definition)");
        }
        else
        {
            probe.Append("(SELECT CONVERT(int,NULL) AS ordinal, CONVERT(nvarchar(128),NULL) AS name, ")
                .Append("CONVERT(nvarchar(max),NULL) AS definition WHERE 1=0) authored");
        }

        // WHY: Identifier aliases are resolved by the target database, not by
        // CLR casing rules. Count every resolved reference, but clone one field
        // per native identity; CS databases retain genuinely distinct operands.
        probe.Append(" ON authored.name COLLATE CATALOG_DEFAULT=expected.name COLLATE CATALOG_DEFAULT ")
            .Append("LEFT JOIN sys.columns c ON c.object_id=OBJECT_ID(")
            .Append(RenderStringLiteral(QualifiedTable(table, schema)))
            .Append(",N'U') AND c.name=expected.name COLLATE CATALOG_DEFAULT ")
            .Append("LEFT JOIN sys.types ty ON ty.user_type_id=c.user_type_id ")
            .Append("WHERE authored.definition IS NOT NULL OR (c.is_computed=0 AND c.is_hidden=0 ")
            .Append("AND c.encryption_type IS NULL ")
            .Append("AND ty.is_user_defined=0 AND ty.is_assembly_type=0 ")
            .Append("AND ty.name IN (N'bigint',N'binary',N'bit',N'char',N'date',N'datetime', ")
            .Append("N'datetime2',N'datetimeoffset',N'decimal',N'float',N'int',N'money',N'nchar',N'numeric', ")
            .Append("N'nvarchar',N'real',N'smalldatetime',N'smallint',N'smallmoney',N'time',N'tinyint', ")
            .Append("N'uniqueidentifier',N'varbinary',N'varchar'))) ")
            .Append("SELECT @operand_count=COUNT(*), @operands=STRING_AGG(CONVERT(nvarchar(max), ")
            .Append("CASE WHEN duplicate=1 THEN definition END),N',') ")
            .Append("WITHIN GROUP(ORDER BY ordinal), @identities=STRING_AGG(CONVERT(nvarchar(max), ")
            .Append("CASE WHEN duplicate=1 THEN N'('+QUOTENAME(physical_name,N'''' )+N')' END),N',') ")
            .Append("WITHIN GROUP(ORDER BY ordinal)");
        if (captureBindings)
        {
            // WHY: The native resolver owns identifier collation. Carry its
            // exact source identities and immutable authored ordinals back to
            // projection; CLR case folding cannot reproduce that binding.
            probe.Append(", @bindings=STRING_AGG(CONVERT(nvarchar(max), ")
                .Append("CONVERT(nvarchar(12),COALESCE(authored_ordinal,-1))+N':'")
                .Append("+CONVERT(nvarchar(12),DATALENGTH(physical_name)/2)+N':'+physical_name),N'') ")
                .Append("WITHIN GROUP(ORDER BY ordinal)");
        }

        probe.Append(" FROM resolved; IF @operand_count<>").Append(names.Length.ToString(CultureInfo.InvariantCulture))
            .Append(" THROW 51004,N'Computed operand metadata unavailable',1; ")
            .Append("DECLARE @identity_conflict bit, @identity_sql nvarchar(max)=")
            .Append("N'SELECT @conflict=CASE WHEN COUNT(*)<>COUNT(DISTINCT name COLLATE CATALOG_DEFAULT) ")
            .Append("THEN 1 ELSE 0 END FROM(VALUES '+@identities+N',('")
            .Append("+QUOTENAME(").Append(RenderStringLiteral(target)).Append(",N'''')+N')) AS names(name);'; ")
            .Append("EXEC tempdb.sys.sp_executesql @identity_sql,N'@conflict bit OUTPUT',")
            .Append("@conflict=@identity_conflict OUTPUT; ")
            .Append("IF @identity_conflict=1 BEGIN SET @support=-1; RETURN; END; ")
            .Append("DECLARE @compiled nvarchar(max)=N'CREATE TABLE ").Append(Delimited(temporary))
            .Append(" ('+@operands+N'); '; ");
    }

    private string RenderComputedOperand(ExpectedColumnDefinition definition)
    {
        var mapping = FindColumnTypeMapping(definition);
        if (mapping is null || SqlServerComputedColumnContract.IsComputed(definition) || definition.IsRowVersion)
        {
            // WHY: A rejected prior authored column cannot become a fictitious
            // built-in operand in a later computed-index projection proof.
            return string.Empty;
        }

        var type = definition.StoreType ?? mapping.StoreType;
        var collation = TryGetColumnType(definition, out var parsed) && parsed.IsCharacter
            ? " COLLATE " + (definition.Collation is null ? "DATABASE_DEFAULT" : Delimited(definition.Collation.Name))
            : string.Empty;

        return Delimited(definition.Name) + " " + type + collation + (definition.IsNullable ? " NULL" : " NOT NULL");
    }
}
