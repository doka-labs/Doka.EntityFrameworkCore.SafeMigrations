namespace Doka.EntityFrameworkCore.SafeMigrations;

internal enum SafeMigrationOperationCompatibilityKind : byte
{
    Safe = 1,
    CertifiedProvider = 2,
    Unsupported = 3,
}

internal readonly record struct SafeMigrationOperationCompatibilityResult(
    MigrationOperation Operation,
    SafeMigrationOperationCompatibilityKind Kind
);

/// <summary>
/// Classifies standard EF operations for scaffolding: an operation is either
/// expressible as a closed SafeMigrations contract or it is not.
/// </summary>
/// <remarks>
/// This is an authoring-time classifier. It never rewrites a published
/// migration or changes the runtime dispatch of its ordinary EF operations.
/// </remarks>
internal static class SafeMigrationOperationCompatibility
{
    /// <summary>Classifies and normalizes one migration operation.</summary>
    /// <param name="operation">The migration operation.</param>
    /// <param name="providerAdapter">The active provider adapter, or null.</param>
    /// <returns>The closed compatibility result.</returns>
    public static SafeMigrationOperationCompatibilityResult Normalize(
        MigrationOperation operation,
        ISafeMigrationProviderOperationAdapter? providerAdapter
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        if (operation is SafeMigrationOperation)
        {
            return new SafeMigrationOperationCompatibilityResult(
                operation,
                SafeMigrationOperationCompatibilityKind.Safe);
        }

        if (operation is SafeMigrationDesignTimeServicesRequiredOperation)
        {
            return new SafeMigrationOperationCompatibilityResult(
                operation,
                SafeMigrationOperationCompatibilityKind.CertifiedProvider);
        }

        if (providerAdapter?.TryNormalize(operation, out var providerOperation) == true)
        {
            return new SafeMigrationOperationCompatibilityResult(
                providerOperation,
                SafeMigrationOperationCompatibilityKind.Safe);
        }

        if (TryNormalizeCoreOperation(operation, out var safeOperation))
        {
            return new SafeMigrationOperationCompatibilityResult(
                safeOperation,
                SafeMigrationOperationCompatibilityKind.Safe);
        }

        if (providerAdapter?.IsCertifiedPassthrough(operation) == true)
        {
            return new SafeMigrationOperationCompatibilityResult(
                operation,
                SafeMigrationOperationCompatibilityKind.CertifiedProvider);
        }

        return new SafeMigrationOperationCompatibilityResult(
            operation,
            SafeMigrationOperationCompatibilityKind.Unsupported);
    }

    /// <summary>
    /// Creates the fail-closed exception for an operation without a complete
    /// contract.
    /// </summary>
    /// <param name="operation">The unsupported migration operation.</param>
    /// <returns>The exception to throw before publishing migration source.</returns>
    public static NotSupportedException Unsupported(
        MigrationOperation operation
    )
    {
        ArgumentNullException.ThrowIfNull(operation);

        return new NotSupportedException(
            $"Migration operation '{operation.GetType().FullName}' has no complete SafeMigrations contract. "
            + "SafeMigrations stopped scaffolding before publishing unsupported migration source.");
    }

    private static bool TryNormalizeCoreOperation(
        MigrationOperation operation,
        [NotNullWhen(true)] out SafeMigrationOperation? safeOperation
    )
    {
        safeOperation = operation switch
        {
            EnsureSchemaOperation value when HasNoAnnotations(value) =>
                Create(new EnsureSchemaIntent(value.Name), SafeMigrationPolicy.ThrowIfDifferent),
            DropSchemaOperation value when HasNoAnnotations(value) =>
                Create(new DropSchemaIntent(value.Name), SafeMigrationPolicy.ThrowIfDifferent),
            CreateTableOperation value when HasCompleteGenericTableContract(value) =>
                Create(
                    new EnsureTableIntent(
                        SafeMigrationExpectedDefinitionFactory.From(value),
                        SafeMigrationTableMode.StrictDefinition),
                    SafeMigrationPolicy.ThrowIfDifferent),
            DropTableOperation value when HasNoAnnotations(value) =>
                Create(new DropTableIntent(value.Name, value.Schema), SafeMigrationPolicy.ThrowIfDifferent),
            RenameTableOperation value when HasNoAnnotations(value) =>
                Create(
                    new RenameTableIntent(value.Name, value.NewName, value.Schema, value.NewSchema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            AddColumnOperation value =>
                Create(
                    new EnsureColumnIntent(
                        value.Table,
                        SafeMigrationExpectedDefinitionFactory.From(value),
                        value.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            AlterColumnOperation value =>
                Create(
                    new AlterColumnIntent(
                        value.Table,
                        SafeMigrationExpectedDefinitionFactory.From(value),
                        SafeMigrationExpectedDefinitionFactory.From(value.OldColumn, value.Name),
                        value.Schema),
                    // WHY: This design-time classification must agree with the
                    // policy emitted by the generated alter-column source.
                    SafeMigrationPolicy.RepairIfSafe),
            DropColumnOperation value when HasNoAnnotations(value) =>
                Create(
                    new DropColumnIntent(value.Name, value.Table, value.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            RenameColumnOperation value when HasNoAnnotations(value) =>
                Create(
                    new RenameColumnIntent(value.Name, value.Table, value.NewName, value.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            CreateIndexOperation value when HasNoAnnotations(value) =>
                Create(
                    new EnsureIndexIntent(SafeMigrationExpectedDefinitionFactory.From(value)),
                    SafeMigrationPolicy.ThrowIfDifferent),
            DropIndexOperation value when value.Table is not null && HasNoAnnotations(value) =>
                Create(
                    new DropIndexIntent(value.Name, value.Table, value.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            RenameIndexOperation value when value.Table is not null && HasNoAnnotations(value) =>
                Create(
                    new RenameIndexIntent(value.Name, value.Table, value.NewName, value.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            AddPrimaryKeyOperation value when HasNoAnnotations(value) =>
                Create(
                    new EnsurePrimaryKeyIntent(SafeMigrationExpectedDefinitionFactory.From(value)),
                    SafeMigrationPolicy.ThrowIfDifferent),
            DropPrimaryKeyOperation value when HasNoAnnotations(value) =>
                Create(
                    new DropPrimaryKeyIntent(value.Name, value.Table, value.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            AddUniqueConstraintOperation value when HasNoAnnotations(value) =>
                Create(
                    new EnsureUniqueConstraintIntent(SafeMigrationExpectedDefinitionFactory.From(value)),
                    SafeMigrationPolicy.ThrowIfDifferent),
            DropUniqueConstraintOperation value when HasNoAnnotations(value) =>
                Create(
                    new DropUniqueConstraintIntent(value.Name, value.Table, value.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            AddCheckConstraintOperation value when HasNoAnnotations(value) =>
                Create(
                    new EnsureCheckConstraintIntent(SafeMigrationExpectedDefinitionFactory.From(value)),
                    SafeMigrationPolicy.ThrowIfDifferent),
            DropCheckConstraintOperation value when HasNoAnnotations(value) =>
                Create(
                    new DropCheckConstraintIntent(value.Name, value.Table, value.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            AddForeignKeyOperation value when HasNoAnnotations(value) =>
                Create(
                    new EnsureForeignKeyIntent(SafeMigrationExpectedDefinitionFactory.From(value)),
                    SafeMigrationPolicy.ThrowIfDifferent),
            DropForeignKeyOperation value when HasNoAnnotations(value) =>
                Create(
                    new DropForeignKeyIntent(value.Name, value.Table, value.Schema),
                    SafeMigrationPolicy.ThrowIfDifferent),
            _ => null,
        };

        return safeOperation is not null;
    }

    private static SafeMigrationOperation Create(
        SafeMigrationIntent intent,
        SafeMigrationPolicy policy
    ) => new(intent, policy);

    private static bool HasCompleteGenericTableContract(
        CreateTableOperation operation
    ) => HasNoAnnotations(operation)
        && (operation.PrimaryKey is null || HasNoAnnotations(operation.PrimaryKey))
        && operation.UniqueConstraints.All(HasNoAnnotations)
        && operation.CheckConstraints.All(HasNoAnnotations)
        && operation.ForeignKeys.All(HasNoAnnotations);

    private static bool HasNoAnnotations(
        MigrationOperation operation
    ) => !operation.GetAnnotations().Any();
}
