namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationCSharpMigrationOperationGenerator
{
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

        if (operation is not EnsureModelManagedDataScaffoldingOperation { Intent: EnsureModelManagedDataIntent intent })
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

        if (operation is not UpdateModelManagedDataScaffoldingOperation { Intent: UpdateModelManagedDataIntent intent })
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

        if (operation is not DeleteModelManagedDataScaffoldingOperation { Intent: DeleteModelManagedDataIntent intent })
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
        builder
            .Append(firstName)
            .Append(": ");

        AppendMatrix(builder, firstValues);

        if (secondValues is not null)
        {
            builder
                .AppendLine(",")
                .Append("newValues: ");

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
            .Append(')');
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
            builder
                .AppendLine(",")
                .AppendLine("foreignKeys:")
                .AppendLine("[")
                .IncrementIndent();
            foreach (var foreignKey in delete.ForeignKeys)
            {
                builder
                    .AppendLine("new ExpectedModelManagedDataForeignKeyDefinition(")
                    .IncrementIndent();

                builder
                    .Append("table: ")
                    .Append(Dependencies.CSharpHelper.Literal(foreignKey.Table))
                    .AppendLine(",");
                builder.Append("columns: ");

                AppendStringArray(builder, foreignKey.Columns);
                builder
                    .AppendLine(",")
                    .Append("principalColumns: ");

                AppendStringArray(builder, foreignKey.PrincipalColumns);
                if (foreignKey.Schema is not null)
                {
                    builder
                        .AppendLine(",")
                        .Append("schema: ")
                        .Append(Dependencies.CSharpHelper.Literal(foreignKey.Schema));
                }

                builder
                    .DecrementIndent()
                    .AppendLine("),");
            }

            builder
                .DecrementIndent()
                .Append("]");
        }
    }

    private void AppendUniqueKeys(
        IndentedStringBuilder builder,
        IReadOnlyList<ExpectedModelManagedDataUniqueKeyDefinition> uniqueKeys
    )
    {
        builder
            .AppendLine(",")
            .AppendLine("uniqueKeys:")
            .AppendLine("[")
            .IncrementIndent();

        foreach (var uniqueKey in uniqueKeys)
        {
            builder.Append("new ExpectedModelManagedDataUniqueKeyDefinition(");
            AppendStringArray(builder, uniqueKey.Columns);
            builder.AppendLine("),");
        }

        builder
            .DecrementIndent()
            .Append("]");
    }

    private void AppendMatrix(
        IndentedStringBuilder builder,
        ModelManagedDataMatrix values
    )
    {
        // EF migration files are emitted under '#nullable disable'. The matrix
        // can still contain null values, but nullable syntax would be rejected
        // by consumers that promote CS8632 to an error.
        builder
            .AppendLine("new object[,]")
            .AppendLine("{")
            .IncrementIndent();

        // WHY: Render matrix rows in authored order without flattening their row boundaries.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var row = 0; row < values.RowCount; row++)
        {
            builder.Append("{ ");
            // WHY: Column ordinals select matrix values and delimiters within each rendered row.
            // ReSharper disable once ForCanBeConvertedToForeach
            // ReSharper disable once LoopCanBeConvertedToQuery
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

        builder
            .DecrementIndent()
            .Append('}');
    }

    private static InvalidOperationException UnpairedDataOperation(
        MigrationOperation operation
    ) => new(
        $"SafeMigrations scaffolding received an unpaired '{operation.GetType().Name}'. "
        + "The migration was not written because model-managed source values could not be proven.");
}
