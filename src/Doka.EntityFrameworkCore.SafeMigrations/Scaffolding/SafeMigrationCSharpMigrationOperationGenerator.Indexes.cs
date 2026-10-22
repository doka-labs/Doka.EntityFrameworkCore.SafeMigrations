namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationCSharpMigrationOperationGenerator
{
    /// <inheritdoc />
    protected override void Generate(
        RenameIndexOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateSafeCompatibility(operation);
        AppendRenameIndexCall(builder, operation);
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

        ValidateSafeCompatibility(operation);
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

        ValidateSafeCompatibility(projection.Operation);

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

        if (projection.IncludedColumns is not null)
        {
            if (projection.PrefixLengths is not null)
            {
                throw new InvalidOperationException(
                    "A provider cannot project both index prefixes and included columns into one generated operation.");
            }

            source = AppendIndexIncludedColumns(source, projection.IncludedColumns);
        }

        var replacement = operation.Columns.Length == 1
            ? projection.IncludedColumns is not null
                ? ".CreateIndexWithIncludesIfNotExistsFromModel("
                : projection.PrefixLengths is null
                    ? ".CreateIndexIfNotExistsFromModel("
                    : ".CreateIndexWithPrefixesIfNotExistsFromModel("
            : projection.IncludedColumns is not null
                ? ".CreateCompositeIndexWithIncludesIfNotExistsFromModel("
                : projection.PrefixLengths is null
                    ? ".CreateCompositeIndexIfNotExistsFromModel("
                    : ".CreateCompositeIndexWithPrefixesIfNotExistsFromModel(";

        AppendReplaced(builder, source, ".CreateIndex(", replacement);
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

    private string AppendIndexIncludedColumns(
        string source,
        IReadOnlyList<string> includedColumns
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
        var columns = string.Join(", ", includedColumns.Select(column => Dependencies.CSharpHelper.Literal(column)));

        return source.Insert(
            closeParenthesis,
            string.Concat(",", newline, argumentIndent, "includedColumns: [", columns, "]"));
    }

    private void AppendRenameIndexCall(
        IndentedStringBuilder builder,
        RenameIndexOperation operation
    )
    {
        builder
            .AppendLine(".RenameIndexIfExists(")
            .IncrementIndent()
            .Append("name: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Name))
            .AppendLine(",")
            .Append("table: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Table))
            .AppendLine(",")
            .Append("newName: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.NewName));

        AppendOptionalSchemaAndClose(builder, operation.Schema);
    }
}
