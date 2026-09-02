namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Converts supported EF Core migration operations into SafeMigrations calls
/// while preserving EF Core's generated argument contract.
/// </summary>
/// <remarks>
/// The generator first delegates to EF Core and then replaces one validated
/// method shape. Unexpected source shapes fail closed instead of producing
/// ambiguous migration code.
/// </remarks>
internal sealed class SafeMigrationCSharpMigrationOperationGenerator : CSharpMigrationOperationGenerator
{
    private const string LegacyRollbackMessage = "A legacy-convergence migration cannot be rolled back safely "
        + "because SafeMigrations cannot prove which database objects predated the migration.";

    private readonly SafeMigrationScaffoldingConfiguration _configuration;
    private readonly ISafeMigrationCreateIndexScaffoldingProjector? _createIndexProjector;

    /// <summary>Initializes the SafeMigrations operation generator.</summary>
    /// <param name="dependencies">The EF Core C# operation-generator dependencies.</param>
    /// <param name="configuration">The immutable scaffolding configuration.</param>
    /// <param name="createIndexProjectors">The active provider's create-index metadata projectors.</param>
    public SafeMigrationCSharpMigrationOperationGenerator(
        CSharpMigrationOperationGeneratorDependencies dependencies,
        SafeMigrationScaffoldingConfiguration configuration,
        IEnumerable<ISafeMigrationCreateIndexScaffoldingProjector> createIndexProjectors
    ) : base(dependencies)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(createIndexProjectors);

        var projectorSnapshot = createIndexProjectors.ToArray();
        if (projectorSnapshot.Length > 1)
        {
            throw new InvalidOperationException(
                "SafeMigrations requires exactly one create-index metadata projector per active provider.");
        }

        _configuration = configuration;
        _createIndexProjector = projectorSnapshot.SingleOrDefault();
    }

    /// <inheritdoc />
    public override void Generate(
        string builderName,
        IReadOnlyList<MigrationOperation> operations,
        IndentedStringBuilder builder
    )
    {
        if (operations.Count == 1
            && operations[0] is SafeMigrationLegacyRollbackOperation)
        {
            builder
                .Append("throw new global::System.NotSupportedException(")
                .Append(Dependencies.CSharpHelper.Literal(LegacyRollbackMessage))
                .Append(");");

            return;
        }

        base.Generate(builderName, operations, builder);
    }

    /// <inheritdoc />
    protected override void Generate(
        CreateTableOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        var replacement = _configuration.Mode switch
        {
            SafeMigrationScaffoldingMode.Strict => ".CreateTableIfNotExists(",
            SafeMigrationScaffoldingMode.LegacyConvergence => ".ConvergeTableFromModel(",
            _ => throw new InvalidOperationException("The SafeMigrations scaffolding mode is invalid."),
        };

        ValidateCheckConstraints(operation);

        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);

        var source = ReplaceCompositePrincipalColumnArrays(operation, baseline.ToString());
        if (_configuration.Mode == SafeMigrationScaffoldingMode.LegacyConvergence)
        {
            source = AppendLegacyConvergencePolicy(source);
        }

        AppendReplaced(builder, source, ".CreateTable(", replacement);
    }

    /// <inheritdoc />
    protected override void Generate(
        InsertDataOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        if (operation is not EnsureModelManagedDataScaffoldingOperation safeOperation
            || safeOperation.Intent is not EnsureModelManagedDataIntent intent)
        {
            throw UnpairedDataOperation(operation);
        }

        AppendModelManagedCall(builder, "EnsureModelManagedDataFromModel", intent, intent.Values, null);
    }

    /// <inheritdoc />
    protected override void Generate(
        UpdateDataOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        if (operation is not UpdateModelManagedDataScaffoldingOperation safeOperation
            || safeOperation.Intent is not UpdateModelManagedDataIntent intent)
        {
            throw UnpairedDataOperation(operation);
        }

        AppendModelManagedCall(builder, "UpdateModelManagedDataFromModel", intent, intent.OldValues, intent.NewValues);
    }

    /// <inheritdoc />
    protected override void Generate(
        DeleteDataOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        if (operation is not DeleteModelManagedDataScaffoldingOperation safeOperation
            || safeOperation.Intent is not DeleteModelManagedDataIntent intent)
        {
            throw UnpairedDataOperation(operation);
        }

        AppendModelManagedCall(builder, "DeleteModelManagedDataFromModel", intent, intent.OldValues, null);
    }

    private void AppendModelManagedCall(
        IndentedStringBuilder builder,
        string method,
        ModelManagedDataIntent intent,
        ModelManagedDataMatrix firstValues,
        ModelManagedDataMatrix? secondValues
    )
    {
        builder
            .Append(".")
            .Append(method)
            .AppendLine("(")
            .IncrementIndent()
            .Append("table: ")
            .Append(Dependencies.CSharpHelper.Literal(intent.Table))
            .AppendLine(",")
            .Append("keyColumns: ");

        AppendStringArray(builder, intent.KeyColumns);
        builder
            .AppendLine(",")
            .Append("keyColumnTypes: ");

        AppendStringArray(builder, intent.KeyColumnTypes);

        if (intent is not EnsureModelManagedDataIntent)
        {
            builder
                .AppendLine(",")
                .Append("keyValues: ");

            AppendMatrix(builder, intent.KeyValues);
        }

        builder
            .AppendLine(",")
            .Append("columns: ");

        AppendStringArray(builder, intent.Columns);
        builder
            .AppendLine(",")
            .Append("columnTypes: ");

        AppendStringArray(builder, intent.ColumnTypes);
        builder.AppendLine(",");

        var firstName = intent is EnsureModelManagedDataIntent ? "values" : "oldValues";
        builder.Append(firstName).Append(": ");
        AppendMatrix(builder, firstValues);

        if (secondValues is not null)
        {
            builder.AppendLine(",").Append("newValues: ");
            AppendMatrix(builder, secondValues);
        }

        if (intent.Schema is not null)
        {
            builder
                .AppendLine(",")
                .Append("schema: ")
                .Append(Dependencies.CSharpHelper.Literal(intent.Schema));
        }

        AppendModelMetadata(builder, intent);

        builder
            .DecrementIndent()
            .Append(");");
    }

    private void AppendModelMetadata(
        IndentedStringBuilder builder,
        ModelManagedDataIntent intent
    )
    {
        if (intent is EnsureModelManagedDataIntent { UniqueKeys.Count: > 0 } ensure)
        {
            AppendUniqueKeys(builder, ensure.UniqueKeys);
        }
        else if (intent is UpdateModelManagedDataIntent { UniqueKeys.Count: > 0 } update)
        {
            AppendUniqueKeys(builder, update.UniqueKeys);
        }
        else if (intent is DeleteModelManagedDataIntent { ForeignKeys.Count: > 0 } delete)
        {
            builder.AppendLine(",").AppendLine("foreignKeys:").AppendLine("[").IncrementIndent();
            foreach (var foreignKey in delete.ForeignKeys)
            {
                builder.AppendLine("new ExpectedModelManagedDataForeignKeyDefinition(").IncrementIndent();
                builder.Append("table: ").Append(Dependencies.CSharpHelper.Literal(foreignKey.Table)).AppendLine(",");
                builder.Append("columns: ");
                AppendStringArray(builder, foreignKey.Columns);
                builder.AppendLine(",").Append("principalColumns: ");
                AppendStringArray(builder, foreignKey.PrincipalColumns);
                if (foreignKey.Schema is not null)
                {
                    builder.AppendLine(",").Append("schema: ")
                        .Append(Dependencies.CSharpHelper.Literal(foreignKey.Schema));
                }

                builder.DecrementIndent().AppendLine("),");
            }

            builder.DecrementIndent().Append("]");
        }
    }

    private void AppendUniqueKeys(
        IndentedStringBuilder builder,
        IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> uniqueKeys
    )
    {
        builder.AppendLine(",").AppendLine("uniqueKeys:").AppendLine("[").IncrementIndent();
        foreach (var uniqueKey in uniqueKeys)
        {
            builder.Append("new ExpectedModelManagedDataUniqueKeyDefinition(");
            AppendStringArray(builder, uniqueKey.Columns);
            builder.AppendLine("),");
        }

        builder.DecrementIndent().Append("]");
    }

    private void AppendStringArray(
        IndentedStringBuilder builder,
        IReadOnlyList<string> values
    )
    {
        builder.Append('[');
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Dependencies.CSharpHelper.Literal(values[index]));
        }

        builder.Append(']');
    }

    private void AppendMatrix(
        IndentedStringBuilder builder,
        ModelManagedDataMatrix values
    )
    {
        // EF migration files are emitted under '#nullable disable'. The matrix
        // can still contain null values, but nullable syntax would be rejected
        // by consumers that promote CS8632 to an error.
        builder.AppendLine("new object[,]").AppendLine("{").IncrementIndent();
        for (var row = 0; row < values.RowCount; row++)
        {
            builder.Append("{ ");
            for (var column = 0; column < values.ColumnCount; column++)
            {
                if (column > 0)
                {
                    builder.Append(", ");
                }

                var value = values.GetUnsafeValue(row, column);
                builder.Append(value is null ? "null" : Dependencies.CSharpHelper.UnknownLiteral(value));
            }

            builder.AppendLine(" },");
        }

        builder.DecrementIndent().Append('}');
    }

    private static InvalidOperationException UnpairedDataOperation(
        MigrationOperation operation
    ) => new(
        $"SafeMigrations scaffolding received an unpaired '{operation.GetType().Name}'. "
        + "The migration was not written because model-managed source values could not be proven.");

    private string AppendLegacyConvergencePolicy(
        string source
    )
    {
        const string method = ".CreateTable(";

        var methodIndex = source.IndexOf(method, StringComparison.Ordinal);
        if (methodIndex < 0
            || source.IndexOf(method, methodIndex + method.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected CreateTable shape. "
                + "SafeMigrations stopped instead of generating an ambiguous policy argument.");
        }

        var openParenthesis = methodIndex + method.Length - 1;
        var closeParenthesis = FindMatchingParenthesis(source, openParenthesis);
        var argumentIndent = FindArgumentIndent(source, openParenthesis, closeParenthesis);
        var newline = SafeMigrationGeneratedSource.GetConsistentNewLine(source);
        var policy = _configuration.LegacyConvergencePolicy switch
        {
            SafeMigrationPolicy.ThrowIfDifferent => nameof(SafeMigrationPolicy.ThrowIfDifferent),
            SafeMigrationPolicy.RepairIfSafe => nameof(SafeMigrationPolicy.RepairIfSafe),
            _ => throw new InvalidOperationException("The legacy-convergence policy is invalid."),
        };

        // Freeze the policy into generated source. Runtime option changes must
        // never reinterpret a migration that was already reviewed.
        return source.Insert(
            closeParenthesis,
            string.Concat(
                ",",
                newline,
                argumentIndent,
                "policy: global::Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationPolicy.",
                policy));
    }

    private static int FindMatchingParenthesis(
        string source,
        int openParenthesis
    )
    {
        var depth = 1;
        var inString = false;
        var inCharacter = false;
        var verbatimString = false;
        var escaped = false;

        for (var index = openParenthesis + 1; index < source.Length; index++)
        {
            var current = source[index];
            if (inString)
            {
                if (verbatimString)
                {
                    if (current != '"')
                    {
                        continue;
                    }

                    if (index + 1 < source.Length && source[index + 1] == '"')
                    {
                        index++;
                        continue;
                    }

                    inString = false;
                    continue;
                }

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inCharacter)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '\'')
                {
                    inCharacter = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                verbatimString = index > 0 && source[index - 1] == '@';
                continue;
            }

            if (current == '\'')
            {
                inCharacter = true;
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')' && --depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            "The EF Core C# operation generator emitted an unterminated CreateTable call. "
            + "SafeMigrations stopped instead of generating an ambiguous policy argument.");
    }

    private static string FindArgumentIndent(
        string source,
        int openParenthesis,
        int closeParenthesis
    )
    {
        var index = openParenthesis + 1;
        if (index >= closeParenthesis)
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected single-line CreateTable call.");
        }

        if (source[index] == '\r'
            && index + 1 < closeParenthesis
            && source[index + 1] == '\n')
        {
            index += 2;
        }
        else if (source[index] == '\n')
        {
            index++;
        }
        else
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected single-line CreateTable call.");
        }

        var indentStart = index;
        while (index < closeParenthesis && source[index] is ' ' or '\t')
        {
            index++;
        }

        if (index == indentStart || index >= closeParenthesis)
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected CreateTable argument layout.");
        }

        return source[indentStart..index];
    }

    private static void ValidateCheckConstraints(
        CreateTableOperation operation
    )
    {
        foreach (var constraint in operation.CheckConstraints)
        {
            if (SafeMigrationSqlExpressionParser.TryParse(
                    constraint.Sql,
                    out _,
                    out var failureCode))
            {
                continue;
            }

            throw new InvalidOperationException(
                $"Check constraint '{constraint.Name}' uses SQL that SafeMigrations cannot compare structurally "
                + $"('{failureCode}'). Replace the generated table operation with an explicit "
                + $"ExpectedCheckConstraintDefinition.FromExpression definition before applying the migration.");
        }
    }

    /// <inheritdoc />
    protected override void Generate(
        DropTableOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        GenerateWithReplacement(operation, builder, ".DropTable(", ".DropTableIfExists(");
    }

    /// <inheritdoc />
    protected override void Generate(
        DropIndexOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        GenerateWithReplacement(operation, builder, ".DropIndex(", ".DropIndexIfExists(");
    }

    /// <inheritdoc />
    protected override void Generate(
        CreateIndexOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        var projection = _createIndexProjector?.Project(operation)
            ?? new SafeMigrationCreateIndexScaffoldingProjection(operation, PrefixLengths: null);

        var baseline = CreateScratchBuilder(builder);
        base.Generate(projection.Operation, baseline);

        // EF migration bodies remain reviewable source and are analyzed by the
        // consuming project. Collection expressions preserve EF's values while
        // avoiding CA1861 on constant array arguments.
        var source = baseline.ToString();
        if (operation.Columns.Length > 1)
        {
            source = ReplaceArrayLiteral(source, Dependencies.CSharpHelper.Literal(operation.Columns), 1);
        }

        if (operation.IsDescending is not null)
        {
            source = ReplaceArrayLiteral(source, Dependencies.CSharpHelper.Literal(operation.IsDescending), 1);
        }

        if (projection.PrefixLengths is not null)
        {
            source = AppendIndexPrefixLengths(source, projection.PrefixLengths);
        }

        AppendReplaced(
            builder,
            source,
            ".CreateIndex(",
            operation.Columns.Length == 1
                ? projection.PrefixLengths is null
                    ? ".CreateIndexIfNotExistsFromModel("
                    : ".CreateIndexWithPrefixesIfNotExistsFromModel("
                : projection.PrefixLengths is null
                    ? ".CreateCompositeIndexIfNotExistsFromModel("
                    : ".CreateCompositeIndexWithPrefixesIfNotExistsFromModel(");
    }

    private static string AppendIndexPrefixLengths(
        string source,
        IReadOnlyList<int> prefixLengths
    )
    {
        const string method = ".CreateIndex(";

        var methodIndex = source.IndexOf(method, StringComparison.Ordinal);
        if (methodIndex < 0
            || source.IndexOf(method, methodIndex + method.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected CreateIndex shape. "
                + "SafeMigrations stopped instead of projecting ambiguous index metadata.");
        }

        var openParenthesis = methodIndex + method.Length - 1;
        var closeParenthesis = FindMatchingParenthesis(source, openParenthesis);
        var argumentIndent = FindArgumentIndent(source, openParenthesis, closeParenthesis);
        var newline = SafeMigrationGeneratedSource.GetConsistentNewLine(source);
        var prefixValues = string.Join(
            ", ",
            prefixLengths.Select(static value => value.ToString(CultureInfo.InvariantCulture)));

        return source.Insert(
            closeParenthesis,
            string.Concat(",", newline, argumentIndent, "prefixLengths: [", prefixValues, "]"));
    }

    private void GenerateWithReplacement(
        DropTableOperation operation,
        IndentedStringBuilder builder,
        string expected,
        string replacement
    )
    {
        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);
        AppendReplaced(builder, baseline.ToString(), expected, replacement);
    }

    private void GenerateWithReplacement(
        DropIndexOperation operation,
        IndentedStringBuilder builder,
        string expected,
        string replacement
    )
    {
        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);
        AppendReplaced(builder, baseline.ToString(), expected, replacement);
    }

    private static void AppendReplaced(
        IndentedStringBuilder builder,
        string baseline,
        string expected,
        string replacement
    )
    {
        var methodIndex = baseline.IndexOf(expected, StringComparison.Ordinal);
        if (methodIndex < 0
            || baseline.AsSpan(0, methodIndex).ContainsAnyExcept(' ', '\t')
            || baseline.IndexOf(expected, methodIndex + expected.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"The EF Core C# operation generator emitted an unexpected '{expected}' shape. "
                + "SafeMigrations stopped instead of generating ambiguous migration code.");
        }

        builder
            .Append(replacement)
            .Append(baseline[(methodIndex + expected.Length)..]);
    }

    private string ReplaceCompositePrincipalColumnArrays(
        CreateTableOperation operation,
        string source
    )
    {
        var literals = operation.ForeignKeys
            .Where(static foreignKey => foreignKey.PrincipalColumns?.Length > 1)
            .Select(foreignKey => Dependencies.CSharpHelper.Literal(foreignKey.PrincipalColumns!))
            .GroupBy(static literal => literal, StringComparer.Ordinal);

        foreach (var group in literals)
        {
            source = ReplaceArrayLiteral(source, group.Key, group.Count());
        }

        return source;
    }

    private static string ReplaceArrayLiteral(
        string source,
        string literal,
        int expectedOccurrences
    )
    {
        const string prefix = "new[] { ";
        const string suffix = " }";

        if (!literal.StartsWith(prefix, StringComparison.Ordinal)
            || !literal.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The EF Core C# helper emitted an unexpected one-dimensional array literal shape.");
        }

        var occurrenceCount = 0;
        var searchIndex = 0;
        while ((searchIndex = source.IndexOf(literal, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            occurrenceCount++;
            searchIndex += literal.Length;
        }

        if (occurrenceCount != expectedOccurrences)
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected array-literal shape. "
                + "SafeMigrations stopped instead of generating analyzer-incompatible migration code.");
        }

        var collectionExpression = string.Concat(
            "[",
            literal.AsSpan(prefix.Length, literal.Length - prefix.Length - suffix.Length),
            "]");

        return source.Replace(literal, collectionExpression, StringComparison.Ordinal);
    }

    private static IndentedStringBuilder CreateScratchBuilder(
        IndentedStringBuilder source
    )
    {
        var scratch = new IndentedStringBuilder();
        for (var index = 0; index < source.IndentCount; index++)
        {
            scratch.IncrementIndent();
        }

        return scratch;
    }
}
