namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Converts supported EF Core migration operations into SafeMigrations calls
/// while preserving EF Core's generated argument contract.
/// </summary>
/// <remarks>
/// Table and index operations delegate to EF Core before one validated method
/// shape is replaced. Standalone constraints render from their complete
/// validated operation values. Unexpected or lossy shapes fail closed.
/// </remarks>
internal sealed partial class SafeMigrationCSharpMigrationOperationGenerator : CSharpMigrationOperationGenerator
{
    private const string LegacyRollbackMessage = "A legacy-convergence migration cannot be rolled back safely "
        + "because SafeMigrations cannot prove which database objects predated the migration. "
        + "Use an explicitly reviewed forward recovery migration or a verified backup recovery procedure "
        + "instead of the generated Down operation.";

    private readonly SafeMigrationScaffoldingConfiguration _configuration;
    private readonly ISafeMigrationCreateIndexScaffoldingProjector? _createIndexProjector;
    private readonly ISafeMigrationProviderOperationAdapter? _providerOperationAdapter;

    /// <summary>Initializes the SafeMigrations operation generator.</summary>
    /// <param name="dependencies">The EF Core C# operation-generator dependencies.</param>
    /// <param name="configuration">The immutable scaffolding configuration.</param>
    /// <param name="createIndexProjectors">The active provider's create-index metadata projectors.</param>
    /// <param name="providerOperationAdapters">The active provider's standard-operation adapters.</param>
    public SafeMigrationCSharpMigrationOperationGenerator(
        CSharpMigrationOperationGeneratorDependencies dependencies,
        SafeMigrationScaffoldingConfiguration configuration,
        IEnumerable<ISafeMigrationCreateIndexScaffoldingProjector> createIndexProjectors,
        IEnumerable<ISafeMigrationProviderOperationAdapter> providerOperationAdapters
    ) : base(dependencies)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(createIndexProjectors);
        ArgumentNullException.ThrowIfNull(providerOperationAdapters);

        var projectorSnapshot = createIndexProjectors.ToArray();

        if (projectorSnapshot.Length > 1)
        {
            throw new InvalidOperationException(
                "SafeMigrations requires exactly one create-index metadata projector per active provider. "
                + "Remove duplicate provider or custom projector registrations from the design-time service provider.");
        }

        _configuration = configuration;
        _createIndexProjector = projectorSnapshot.SingleOrDefault();

        var adapterSnapshot = providerOperationAdapters.ToArray();

        if (adapterSnapshot.Length > 1)
        {
            throw new InvalidOperationException(
                "SafeMigrations requires at most one provider operation adapter per active provider. "
                + "Remove duplicate provider or custom adapter registrations from the design-time service provider.");
        }

        _providerOperationAdapter = adapterSnapshot.SingleOrDefault();
    }

    /// <inheritdoc />
    public override void Generate(
        string builderName,
        IReadOnlyList<MigrationOperation> operations,
        IndentedStringBuilder builder
    )
    {
        if (operations is [SafeMigrationLegacyRollbackOperation])
        {
            builder
                .Append("throw new global::System.NotSupportedException(")
                .Append(Dependencies.CSharpHelper.Literal(LegacyRollbackMessage))
                .Append(");");

            return;
        }

        if (!_configuration.IsEnabled)
        {
            base.Generate(builderName, operations, builder);

            return;
        }

        // WHY: EF renders an operation stream incrementally. A later unsupported
        // operation must not leave an apparently usable prefix in the generated
        // migration source, so publish the stream only after every operation has
        // passed its SafeMigrations contract and rendered successfully.
        var validatedSource = CreateScratchBuilder(builder);

        base.Generate(builderName, operations, validatedSource);

        // WHY: The outer builder supplies the first-line indentation itself.
        // The scratch builder already rendered that indentation for validation;
        // retaining it would double-indent the first generated operation.
        builder.Append(
            validatedSource
                .ToString()
                .TrimStart(' ', '\t'));
    }

    /// <inheritdoc />
    protected override void Generate(
        MigrationOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        throw SafeMigrationOperationCompatibility.Unsupported(operation);
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

        ValidateSafeCompatibility(operation);
        var replacement = _configuration.Mode switch
        {
            SafeMigrationScaffoldingMode.Strict => ".CreateTableIfNotExists(",
            SafeMigrationScaffoldingMode.LegacyConvergence => ".ConvergeTableFromModel(",
            _ => throw new InvalidOperationException(
                "The SafeMigrations scaffolding mode is invalid. Select Strict or LegacyConvergence "
                + "through UseScaffoldingMode before generating migrations."),
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
        EnsureSchemaOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateSafeCompatibility(operation);
        GenerateWithReplacement(operation, builder, ".EnsureSchema(", ".EnsureSchemaExists(");
    }

    /// <inheritdoc />
    protected override void Generate(
        DropSchemaOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateSafeCompatibility(operation);
        GenerateWithReplacement(operation, builder, ".DropSchema(", ".DropSchemaIfExists(");
    }

    /// <inheritdoc />
    protected override void Generate(
        RenameTableOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        ValidateSafeCompatibility(operation);
        AppendRenameTableCall(builder, operation);
    }

    /// <inheritdoc />
    protected override void Generate(
        AlterDatabaseOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            base.Generate(operation, builder);
            return;
        }

        var compatibility = SafeMigrationOperationCompatibility.Normalize(operation, _providerOperationAdapter);
        if (compatibility.Kind != SafeMigrationOperationCompatibilityKind.CertifiedProvider)
        {
            throw SafeMigrationOperationCompatibility.Unsupported(operation);
        }

        base.Generate(operation, builder);
    }

    /// <inheritdoc />
    protected override void Generate(
        AlterTableOperation operation,
        IndentedStringBuilder builder
    ) => GenerateUnsupported(operation, builder);

    /// <inheritdoc />
    protected override void Generate(
        CreateSequenceOperation operation,
        IndentedStringBuilder builder
    ) => GenerateUnsupported(operation, builder);

    /// <inheritdoc />
    protected override void Generate(
        AlterSequenceOperation operation,
        IndentedStringBuilder builder
    ) => GenerateUnsupported(operation, builder);

    /// <inheritdoc />
    protected override void Generate(
        DropSequenceOperation operation,
        IndentedStringBuilder builder
    ) => GenerateUnsupported(operation, builder);

    /// <inheritdoc />
    protected override void Generate(
        RenameSequenceOperation operation,
        IndentedStringBuilder builder
    ) => GenerateUnsupported(operation, builder);

    /// <inheritdoc />
    protected override void Generate(
        RestartSequenceOperation operation,
        IndentedStringBuilder builder
    ) => GenerateUnsupported(operation, builder);

    /// <inheritdoc />
    protected override void Generate(
        SqlOperation operation,
        IndentedStringBuilder builder
    ) => GenerateUnsupported(operation, builder);

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

        ValidateSafeCompatibility(operation);
        GenerateWithReplacement(operation, builder, ".DropTable(", ".DropTableIfExists(");
    }

    private void ValidateSafeCompatibility(
        MigrationOperation operation
    )
    {
        var compatibility = SafeMigrationOperationCompatibility.Normalize(operation, _providerOperationAdapter);
        if (compatibility.Kind != SafeMigrationOperationCompatibilityKind.Safe)
        {
            throw SafeMigrationOperationCompatibility.Unsupported(operation);
        }
    }

    private void GenerateUnsupported(
        MigrationOperation operation,
        IndentedStringBuilder builder
    )
    {
        if (!_configuration.IsEnabled)
        {
            switch (operation)
            {
                case AlterTableOperation value:
                    base.Generate(value, builder);
                    return;
                case CreateSequenceOperation value:
                    base.Generate(value, builder);
                    return;
                case AlterSequenceOperation value:
                    base.Generate(value, builder);
                    return;
                case DropSequenceOperation value:
                    base.Generate(value, builder);
                    return;
                case RenameSequenceOperation value:
                    base.Generate(value, builder);
                    return;
                case RestartSequenceOperation value:
                    base.Generate(value, builder);
                    return;
                case SqlOperation value:
                    base.Generate(value, builder);
                    return;
                default:
                    throw new UnreachableException();
            }
        }

        throw SafeMigrationOperationCompatibility.Unsupported(operation);
    }

    private void AppendRenameTableCall(
        IndentedStringBuilder builder,
        RenameTableOperation operation
    )
    {
        builder
            .AppendLine(".RenameTableIfExists(")
            .IncrementIndent()
            .Append("name: ")
            .Append(Dependencies.CSharpHelper.Literal(operation.Name));

        if (operation.NewName is not null)
        {
            builder
                .AppendLine(",")
                .Append("newName: ")
                .Append(Dependencies.CSharpHelper.Literal(operation.NewName));
        }

        if (operation.Schema is not null)
        {
            builder
                .AppendLine(",")
                .Append("schema: ")
                .Append(Dependencies.CSharpHelper.Literal(operation.Schema));
        }

        if (operation.NewSchema is not null)
        {
            builder
                .AppendLine(",")
                .Append("newSchema: ")
                .Append(Dependencies.CSharpHelper.Literal(operation.NewSchema));
        }

        builder
            .DecrementIndent()
            .Append(')');
    }
}
