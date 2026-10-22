namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationCSharpMigrationOperationGenerator
{
    /// <inheritdoc />
    protected override void Generate(
        AddColumnOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        GenerateCapturedColumnOperation(
            operation,
            builder,
            ".AddColumnIfNotExistsFromModel(",
            SafeMigrationPolicy.ThrowIfDifferent);
    }

    /// <inheritdoc />
    protected override void Generate(
        AlterColumnOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        GenerateCapturedColumnOperation(
            operation,
            builder,
            ".AlterColumnIfDifferentFromModel(",
            SafeMigrationPolicy.RepairIfSafe);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropColumnOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateSafeCompatibility(operation);
        AppendDropColumnCall(builder, operation);
    }

    /// <inheritdoc />
    protected override void Generate(
        RenameColumnOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateSafeCompatibility(operation);
        AppendRenameColumnCall(builder, operation);
    }

    private void GenerateCapturedColumnOperation(
        ColumnOperation operation,
        IndentedStringBuilder builder,
        string method,
        SafeMigrationPolicy policy
    )
    {
        var baseline = CreateScratchBuilder(builder);
        switch (operation)
        {
            case AddColumnOperation addColumn:
                base.Generate(addColumn, baseline);
                break;
            case AlterColumnOperation alterColumn:
                base.Generate(alterColumn, baseline);
                break;
            default:
                throw new UnreachableException();
        }

        var source = baseline.ToString();
        var invocationStart = source.IndexOf('.', StringComparison.Ordinal);
        if (invocationStart < 0
            || source
                .AsSpan(0, invocationStart)
                .ContainsAnyExcept(' ', '\t'))
        {
            throw new InvalidOperationException(
                $"The EF Core C# operation generator emitted an unexpected {operation.GetType().Name} shape. "
                + "SafeMigrations stopped instead of generating ambiguous migration code.");
        }

        builder
            .AppendLine(method)
            .IncrementIndent()
            .Append("operationFactory: static operationBuilder => operationBuilder")
            .Append(source[invocationStart..])
            .AppendLine(",")
            .Append("policy: global::Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationPolicy.")
            .Append(policy.ToString())
            .DecrementIndent()
            .Append(')');
    }

    private void AppendDropColumnCall(
        IndentedStringBuilder builder,
        DropColumnOperation operation
    )
    {
        builder
            .AppendLine(".DropColumnIfExists(")
            .IncrementIndent()
            .Append("name: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Name))
            .AppendLine(",")
            .Append("table: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Table));

        AppendOptionalSchemaAndClose(builder, operation.Schema);
    }

    private void AppendRenameColumnCall(
        IndentedStringBuilder builder,
        RenameColumnOperation operation
    )
    {
        builder
            .AppendLine(".RenameColumnIfExists(")
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
