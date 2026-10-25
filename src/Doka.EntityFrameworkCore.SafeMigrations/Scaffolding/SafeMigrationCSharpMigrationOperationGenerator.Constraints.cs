namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationCSharpMigrationOperationGenerator
{
    /// <inheritdoc />
    protected override void Generate(
        AddPrimaryKeyOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateStandaloneConstraint(operation);
        AppendAddConstraintCall(
            builder,
            "AddPrimaryKeyIfNotExists",
            operation.Name,
            operation.Table,
            operation.Columns,
            operation.Schema);
    }

    /// <inheritdoc />
    protected override void Generate(
        AddUniqueConstraintOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateStandaloneConstraint(operation);
        AppendAddConstraintCall(
            builder,
            "AddUniqueConstraintIfNotExists",
            operation.Name,
            operation.Table,
            operation.Columns,
            operation.Schema);
    }

    /// <inheritdoc />
    protected override void Generate(
        AddCheckConstraintOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateStandaloneConstraint(operation);
        ValidateConstraintIdentity(
            operation.Name,
            operation.Table,
            operation.GetType()
                .Name);
        ValidateCheckConstraint(operation.Name, operation.Sql);

        builder
            .AppendLine(".AddCheckConstraintIfNotExists(")
            .IncrementIndent()
            .Append("name: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Name))
            .AppendLine(",")
            .Append("table: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Table))
            .AppendLine(",")
            .Append("sql: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Sql));

        AppendSchemaAndStrictPolicy(builder, operation.Schema);
    }

    /// <inheritdoc />
    protected override void Generate(
        AddForeignKeyOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateStandaloneConstraint(operation);
        ValidateConstraintIdentity(
            operation.Name,
            operation.Table,
            operation.GetType()
                .Name);
        ValidateColumns(operation.Name, operation.Columns, "dependent");
        if (string.IsNullOrWhiteSpace(operation.PrincipalTable))
        {
            throw new InvalidOperationException(
                $"Foreign key '{SafeMigrationDiagnosticMessages.BoundForDisplay(operation.Name)}' "
                + "does not identify its principal table. "
                + "SafeMigrations stopped instead of generating an incomplete constraint contract.");
        }

        if (operation.PrincipalColumns is not { Length: > 0 } principalColumns)
        {
            throw new InvalidOperationException(
                $"Foreign key '{SafeMigrationDiagnosticMessages.BoundForDisplay(operation.Name)}' "
                + "does not identify its principal columns. "
                + "SafeMigrations stopped instead of generating an incomplete constraint contract.");
        }

        ValidateReferentialAction(operation.Name, operation.OnUpdate, "update");
        ValidateReferentialAction(operation.Name, operation.OnDelete, "delete");
        if (operation.Columns.Length != principalColumns.Length)
        {
            throw new InvalidOperationException(
                $"Foreign key '{SafeMigrationDiagnosticMessages.BoundForDisplay(operation.Name)}' "
                + "has different dependent and principal column counts. "
                + "SafeMigrations stopped before generating source.");
        }

        builder
            .AppendLine(".AddForeignKeyIfNotExists(")
            .IncrementIndent()
            .Append("name: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Name))
            .AppendLine(",")
            .Append("table: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Table))
            .AppendLine(",")
            .Append("columns: ");

        AppendStringArray(builder, operation.Columns);
        builder
            .AppendLine(",")
            .Append("principalTable: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.PrincipalTable))
            .AppendLine(",")
            .Append("principalColumns: ");

        AppendStringArray(builder, principalColumns);
        if (operation.Schema is not null)
        {
            builder
                .AppendLine(",")
                .Append("schema: ")
                .Append(Dependencies.CSharpHelper.Literal(operation.Schema));
        }

        if (operation.PrincipalSchema is not null)
        {
            builder
                .AppendLine(",")
                .Append("principalSchema: ")
                .Append(Dependencies.CSharpHelper.Literal(operation.PrincipalSchema));
        }

        builder
            .AppendLine(",")
            .Append("onUpdate: global::Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.")
            .Append(operation.OnUpdate.ToString())
            .AppendLine(",")
            .Append("onDelete: global::Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.")
            .Append(operation.OnDelete.ToString())
            .AppendLine(",")
            .Append("policy: global::Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationPolicy.ThrowIfDifferent")
            .DecrementIndent()
            .Append(')');
    }

    /// <inheritdoc />
    protected override void Generate(
        DropPrimaryKeyOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateStandaloneConstraint(operation);
        AppendDropConstraintCall(builder, "DropPrimaryKeyIfExists", operation.Name, operation.Table, operation.Schema);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropUniqueConstraintOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateStandaloneConstraint(operation);
        AppendDropConstraintCall(
            builder,
            "DropUniqueConstraintIfExists",
            operation.Name,
            operation.Table,
            operation.Schema);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropCheckConstraintOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateStandaloneConstraint(operation);
        AppendDropConstraintCall(
            builder,
            "DropCheckConstraintIfExists",
            operation.Name,
            operation.Table,
            operation.Schema);
    }

    /// <inheritdoc />
    protected override void Generate(
        DropForeignKeyOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateStandaloneConstraint(operation);
        AppendDropConstraintCall(builder, "DropForeignKeyIfExists", operation.Name, operation.Table, operation.Schema);
    }

    private void AppendAddConstraintCall(
        IndentedStringBuilder builder,
        string method,
        string name,
        string table,
        IReadOnlyList<string> columns,
        string? schema
    )
    {
        ValidateConstraintIdentity(name, table, method);
        ValidateColumns(name, columns, "key");

        builder
            .Append('.')
            .Append(method)
            .AppendLine("(")
            .IncrementIndent()
            .Append("name: ")
            .Append(Dependencies.CSharpHelper.Literal(name))
            .AppendLine(",")
            .Append("table: ")
            .Append(Dependencies.CSharpHelper.Literal(table))
            .AppendLine(",")
            .Append("columns: ");

        AppendStringArray(builder, columns);
        AppendSchemaAndStrictPolicy(builder, schema);
    }

    private void AppendSchemaAndStrictPolicy(
        IndentedStringBuilder builder,
        string? schema
    )
    {
        if (schema is not null)
        {
            builder
                .AppendLine(",")
                .Append("schema: ")
                .Append(Dependencies.CSharpHelper.Literal(schema));
        }

        builder
            .AppendLine(",")
            .Append("policy: global::Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationPolicy.ThrowIfDifferent")
            .DecrementIndent()
            .Append(')');
    }

    private void AppendDropConstraintCall(
        IndentedStringBuilder builder,
        string method,
        string name,
        string table,
        string? schema
    )
    {
        ValidateConstraintIdentity(name, table, method);

        builder
            .Append('.')
            .Append(method)
            .AppendLine("(")
            .IncrementIndent()
            .Append("name: ")
            .Append(Dependencies.CSharpHelper.Literal(name))
            .AppendLine(",")
            .Append("table: ")
            .Append(Dependencies.CSharpHelper.Literal(table));

        if (schema is not null)
        {
            builder
                .AppendLine(",")
                .Append("schema: ")
                .Append(Dependencies.CSharpHelper.Literal(schema));
        }

        builder
            .DecrementIndent()
            .Append(')');
    }

    private static void ValidateCheckConstraints(
        CreateTableOperation operation
    )
    {
        foreach (var constraint in operation.CheckConstraints)
        {
            ValidateCheckConstraint(constraint.Name, constraint.Sql);
        }
    }

    private static void ValidateCheckConstraint(
        string name,
        string sql
    )
    {
        if (SafeMigrationSqlExpressionParser.TryParse(sql, out _, out var failureCode))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Check constraint '{SafeMigrationDiagnosticMessages.BoundForDisplay(name)}' uses SQL "
            + "that SafeMigrations cannot compare structurally "
            + $"('{failureCode}'). Replace the generated operation with an explicit "
            + $"ExpectedCheckConstraintDefinition.FromExpression definition before applying the migration.");
    }

    private static void ValidateStandaloneConstraint(
        MigrationOperation operation
    )
    {
        var annotation = operation
            .GetAnnotations()
            .FirstOrDefault();
        if (annotation is null)
        {
            return;
        }

        // WHY: Standalone constraint definitions have no provider-annotation
        // contract. Silently discarding metadata here could change physical DDL.
        throw new InvalidOperationException(
            "Standalone constraint operation "
            + $"'{SafeMigrationDiagnosticMessages.BoundForDisplay(operation.GetType().Name)}' "
            + "contains unsupported annotation "
            + $"'{SafeMigrationDiagnosticMessages.BoundForDisplay(annotation.Name)}'. "
            + "SafeMigrations stopped instead of generating a lossy constraint contract. "
            + "Use a provider-certified contract that preserves the metadata before applying this constraint.");
    }

    private static void ValidateConstraintIdentity(
        string name,
        string table,
        string operation
    )
    {
        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(table))
        {
            throw new InvalidOperationException(
                $"Standalone constraint operation '{operation}' has an incomplete object identity. "
                + "Set the constraint name and owning table explicitly; "
                + "SafeMigrations stopped before generating source.");
        }
    }

    private static void ValidateColumns(
        string name,
        IReadOnlyList<string> columns,
        string role
    )
    {
        if (columns.Count == 0
            || columns.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException(
                $"Constraint '{SafeMigrationDiagnosticMessages.BoundForDisplay(name)}' "
                + $"has an incomplete {role}-column contract. "
                + "Supply the complete ordered, non-empty column vector before generating source.");
        }
    }

    private static void ValidateReferentialAction(
        string name,
        ReferentialAction action,
        string role
    )
    {
        if (Enum.IsDefined(action))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Foreign key '{SafeMigrationDiagnosticMessages.BoundForDisplay(name)}' "
            + $"has an undefined {role} referential action '{(int)action}'. "
            + "Select a defined ReferentialAction before generating source.");
    }
}
