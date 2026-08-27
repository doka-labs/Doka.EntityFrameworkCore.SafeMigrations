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

    /// <summary>Initializes the SafeMigrations operation generator.</summary>
    /// <param name="dependencies">The EF Core C# operation-generator dependencies.</param>
    /// <param name="configuration">The immutable scaffolding configuration.</param>
    public SafeMigrationCSharpMigrationOperationGenerator(
        CSharpMigrationOperationGeneratorDependencies dependencies,
        SafeMigrationScaffoldingConfiguration configuration
    ) : base(dependencies)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _configuration = configuration;
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

        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);

        var source = ReplaceCompositePrincipalColumnArrays(operation, baseline.ToString());
        AppendReplaced(builder, source, ".CreateTable(", replacement);
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
        CreateIndexOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);

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

        AppendReplaced(
            builder,
            source,
            ".CreateIndex(",
            operation.Columns.Length == 1
                ? ".CreateIndexIfNotExistsFromModel("
                : ".CreateCompositeIndexIfNotExistsFromModel(");
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
