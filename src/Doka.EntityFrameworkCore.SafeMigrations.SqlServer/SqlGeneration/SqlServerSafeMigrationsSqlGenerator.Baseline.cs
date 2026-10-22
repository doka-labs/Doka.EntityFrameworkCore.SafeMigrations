namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

public sealed partial class SqlServerSafeMigrationsSqlGenerator
{
    private IReadOnlyList<MigrationCommand> RenderBaseline(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan,
        IModel? model,
        MigrationsSqlGenerationOptions options,
        int ordinal
    )
    {
        if (plan.IsStaticallyUnsupported)
        {
            return [];
        }

        if (operation.Intent is ModelManagedDataIntent data)
        {
            return _baselineGenerator.Generate(
                [
                    new SqlOperation
                    {
                        Sql = _catalogSqlBuilder.BuildModelManagedDataMutationSql(
                            data,
                            scriptIdentityScope: (options & MigrationsSqlGenerationOptions.Script) != 0,
                            scopeInUseMessage: SafeMigrationDiagnosticMessages.FormatRuntime(
                                operation,
                                "doka_sm_identity_scope_in_use",
                                ordinal)),
                    },
                ],
                model,
                options);
        }

        if (IsUnchangedTableRename(operation.Intent))
        {
            // WHY: A repeated identity still requires the runtime object-kind
            // and policy guards, but sp_rename and schema transfer have no
            // work to perform. The guarded command remains non-empty.
            return [];
        }

        var baselineOperation = SafeMigrationStandardOperationFactory.Create(
            operation.Intent,
            _expressionRenderer.Render,
            static collation => collation.Schema is null ? collation.Name : null);

        var generationModel = model;

        if (operation.Intent is RenameTableIntent rename
            && baselineOperation is RenameTableOperation renameTable)
        {
            // WHY: EF interprets an omitted NewSchema as a transfer to the
            // principal's default schema. A safe rename without a requested
            // transfer must instead preserve its explicitly qualified source.
            renameTable.NewSchema = rename.NewSchema ?? rename.Schema;
        }

        if (operation.Intent is EnsureIndexIntent { Definition.IncludedColumns.Count: > 0 } index
            && baselineOperation is CreateIndexOperation createIndex)
        {
            // WHY: Core's provider-neutral factory cannot carry included columns.
            // SQL Server's EF generator consumes this provider annotation.
            createIndex["SqlServer:Include"] = index.Definition.IncludedColumns.ToArray();
        }

        if (operation.Intent is AlterColumnIntent
            && baselineOperation is AlterColumnOperation alteration)
        {
            // WHY: A safe ALTER owns a complete captured column contract.
            // EF's single-operation generation otherwise derives extra index
            // drops/recreates from the target model, outside the ordered safe
            // operations and their proofs. Dependencies must be explicit.
            generationModel = null;
            if (plan is { RepairCapability: SafeMigrationRepairCapability.Safe, MayRequireNullabilityDataProof: true }
                && !alteration.IsNullable
                && (alteration.DefaultValue is not null || alteration.DefaultValueSql is not null))
            {
                // WHY: Runtime already rejects any NULL row before ALTER.
                // EF would still emit a zero-row default UPDATE, which fires
                // DML triggers. This renderer-only hint suppresses that write;
                // the original immutable source/null proof remains unchanged.
                alteration.OldColumn.IsNullable = false;
            }
        }

        return _baselineGenerator.Generate([baselineOperation], generationModel, options);
    }

    /// <summary>Identifies the exact safe rename whose guarded contract needs no physical DDL.</summary>
    private static bool IsUnchangedTableRename(
        SafeMigrationIntent intent
    ) => intent is RenameTableIntent rename
        && (rename.NewName ?? rename.Name) == rename.Name
        && (rename.NewSchema ?? rename.Schema ?? "dbo") == (rename.Schema ?? "dbo");

    private IReadOnlyList<MigrationCommand> RenderRepairBaseline(
        SafeMigrationOperation operation,
        SqlServerSafeMigrationRuntimePlan plan,
        IModel? model,
        MigrationsSqlGenerationOptions options
    )
    {
        if (operation.Policy != SafeMigrationPolicy.RepairIfSafe
            || plan.RepairCapability != SafeMigrationRepairCapability.Safe
            || operation.Intent is not EnsureColumnIntent column)
        {
            return [];
        }

        var repairOperation = SafeMigrationStandardOperationFactory.CreateRepair(
            column,
            _expressionRenderer.Render,
            static collation => collation.Schema is null ? collation.Name : null);

        return _baselineGenerator.Generate([repairOperation], model, options);
    }

    private static bool UsesDefaultSchema(
        SafeMigrationIntent intent
    ) => intent switch
    {
        EnsureSchemaIntent or DropSchemaIntent => false,
        EnsureTableIntent value =>
            value.Definition.Schema is null
            || value.Definition.ForeignKeys.Any(static foreignKey => foreignKey.PrincipalSchema is null),
        DropTableIntent value => value.Schema is null,
        RenameTableIntent value => value.Schema is null,
        EnsureColumnIntent value => value.Schema is null,
        DropColumnIntent value => value.Schema is null,
        RenameColumnIntent value => value.Schema is null,
        AlterColumnIntent value => value.Schema is null,
        EnsureIndexIntent value => value.Definition.Schema is null,
        DropIndexIntent value => value.Schema is null,
        RenameIndexIntent value => value.Schema is null,
        EnsurePrimaryKeyIntent value => value.Definition.Schema is null,
        DropPrimaryKeyIntent value => value.Schema is null,
        EnsureUniqueConstraintIntent value => value.Definition.Schema is null,
        DropUniqueConstraintIntent value => value.Schema is null,
        EnsureCheckConstraintIntent value => value.Definition.Schema is null,
        DropCheckConstraintIntent value => value.Schema is null,
        EnsureForeignKeyIntent value => value.Definition.Schema is null || value.Definition.PrincipalSchema is null,
        DropForeignKeyIntent value => value.Schema is null,
        ModelManagedDataIntent value => value.Schema is null,
        _ => throw new UnreachableException(),
    };

    private static void ValidateTemporalOperationBoundary(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var hasOrdinaryOperation = false;
        var hasSafeBoundaryAfterOrdinary = false;
        var hasOrdinaryOperationsAcrossBoundary = false;
        var hasTemporalMetadata = false;

        foreach (var operation in operations)
        {
            if (operation is SafeMigrationOperation)
            {
                hasSafeBoundaryAfterOrdinary |= hasOrdinaryOperation;

                continue;
            }

            if (operation is SafeMigrationDesignTimeServicesRequiredOperation)
            {
                continue;
            }

            hasOrdinaryOperationsAcrossBoundary |= hasSafeBoundaryAfterOrdinary;
            hasOrdinaryOperation = true;
            hasTemporalMetadata |= HasTemporalMetadata(operation);
        }

        if (!hasOrdinaryOperationsAcrossBoundary
            || !hasTemporalMetadata)
        {
            return;
        }

        // WHY: EF Core's SQL Server generator derives temporal-table state
        // from its whole operation list before rewriting column operations.
        // Separate baseline calls cannot preserve that state across a Safe
        // operation inserted between two ordinary EF operation segments.
        throw new NotSupportedException(
            "SQL Server temporal operations cannot cross a SafeMigrations operation boundary.");
    }

    private static bool HasTemporalMetadata(
        MigrationOperation operation
    )
    {
        if (HasTemporalAnnotation(operation.GetAnnotations()))
        {
            return true;
        }

        return operation switch
        {
            AlterTableOperation value => HasTemporalAnnotation(value.OldTable.GetAnnotations()),
            AlterColumnOperation value => HasTemporalAnnotation(value.OldColumn.GetAnnotations()),
            CreateTableOperation value => value.Columns.Any(static column =>
                HasTemporalAnnotation(column.GetAnnotations())),
            _ => false,
        };
    }

    private static bool HasTemporalAnnotation(
        IEnumerable<IAnnotation> annotations
    ) => annotations.Any(static annotation => annotation.Name is "SqlServer:IsTemporal"
        || annotation.Name.StartsWith("SqlServer:Temporal", StringComparison.Ordinal));
}
