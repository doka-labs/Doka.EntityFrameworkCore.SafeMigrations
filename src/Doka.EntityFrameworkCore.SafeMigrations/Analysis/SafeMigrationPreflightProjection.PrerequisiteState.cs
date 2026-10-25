namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationPreflightProjection
{
    private readonly record struct ProjectedColumnState(
        ExpectedColumnDefinition? Definition,
        bool IsMissing,
        bool PreservesLiveValueDomain = false,
        int ValueMutationOriginOrdinal = -1
    );

    private sealed class ProjectedPrerequisites(
        bool newlyCreated,
        long dataMutationVersion,
        ISafeMigrationProviderObjectIdentityNormalizer? objectIdentityNormalizer,
        bool sharesUniqueIndexIdentity
    )
    {
        private readonly ISafeMigrationProviderObjectIdentityNormalizer? _objectIdentityNormalizer =
            objectIdentityNormalizer;

        public ProjectedDefinitionSet<ExpectedCheckConstraintDefinition> CheckConstraints { get; } = new(
            SafeMigrationSemanticDefinitionComparers.CheckConstraint,
            objectIdentityNormalizer);

        public Dictionary<string, ProjectedColumn> Columns { get; } = new(
            new IdentifierComparer(objectIdentityNormalizer, objectIdentityNormalizer?.ColumnIdentifierComparer));

        public long DataMutationVersion { get; } = dataMutationVersion;

        public long ModelManagedDataMutationVersion { get; set; } = dataMutationVersion;

        public long? EmptyTableProofVersion { get; set; }

        /// <summary>Gets whether a certified rename retained accepted CHECK shapes through unknown structure.</summary>
        public bool CheckDefinitionsFollowKnownRename { get; set; }

        /// <summary>Gets whether current states exhaust generated members, not physical layout or rows.</summary>
        public bool GeneratedColumnsComplete { get; set; }

        /// <summary>Gets whether only guarded index or constraint removals precede the native snapshot.</summary>
        public bool NativeGeneratedSnapshotPreservedByIndexOrConstraintRemoval { get; set; }

        public ProjectedDefinitionSet<ExpectedForeignKeyDefinition> ForeignKeys { get; } = new(
            SafeMigrationSemanticDefinitionComparers.ForeignKey,
            objectIdentityNormalizer,
            objectIdentityNormalizer?.ForeignKeyIdentifierComparer);

        public ProjectedDefinitionSet<ExpectedIndexDefinition> Indexes { get; } = new(
            SafeMigrationSemanticDefinitionComparers.IndexForColumns(
                objectIdentityNormalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal),
            objectIdentityNormalizer,
            objectIdentityNormalizer?.IndexIdentifierComparer);

        public bool NewlyCreated { get; } = newlyCreated;

        public ExpectedPrimaryKeyDefinition? PrimaryKey { get; private set; }

        /// <summary>Gets the separately proven physical PK name, or null when only semantic shape is known.</summary>
        public string? PrimaryKeyPhysicalName { get; private set; }

        public bool PrimaryKeyWasDropped { get; private set; }

        /// <summary>Gets whether accepted Apply established the current PK's physical lifetime.</summary>
        public bool PrimaryKeyCreatedByProjection { get; private set; }

        public ExpectedPrimaryKeyDefinition? RemovedPrimaryKey { get; private set; }

        public ProjectedDefinitionSet<ExpectedUniqueConstraintDefinition> UniqueConstraints { get; } = new(
            SafeMigrationSemanticDefinitionComparers.UniqueForColumns(
                objectIdentityNormalizer?.ColumnIdentifierComparer ?? StringComparer.Ordinal),
            objectIdentityNormalizer,
            sharesUniqueIndexIdentity ? objectIdentityNormalizer?.IndexIdentifierComparer : null);

        /// <summary>Accepts key shape with separate authored-alias and physical-identity proof.</summary>
        /// <param name="definition">The accepted key shape.</param>
        /// <param name="physicalName">The separately proven physical name, when available.</param>
        /// <param name="createdByProjection">Whether an accepted Apply created this exact physical lifetime.</param>
        public void AcceptPrimaryKey(
            ExpectedPrimaryKeyDefinition definition,
            string? physicalName,
            bool createdByProjection = false
        )
        {
            PrimaryKeyCreatedByProjection = createdByProjection
                || (PrimaryKeyCreatedByProjection
                    && PrimaryKey is not null
                    && SameColumns(PrimaryKey.Columns, definition.Columns, _objectIdentityNormalizer)
                    && StringComparer.Ordinal.Equals(PrimaryKeyPhysicalName, physicalName));

            PrimaryKey = definition;
            PrimaryKeyPhysicalName = physicalName;
            PrimaryKeyWasDropped = false;
            RemovedPrimaryKey = null;
        }

        public void DropPrimaryKey()
        {
            RemovedPrimaryKey = PrimaryKey;
            PrimaryKey = null;
            PrimaryKeyPhysicalName = null;
            PrimaryKeyWasDropped = true;
            PrimaryKeyCreatedByProjection = false;
        }

        /// <summary>Rebinds accepted key owners while preserving physical names and semantic aliases.</summary>
        public void RenameKeys(
            string table,
            string? schema
        )
        {
            PrimaryKey = PrimaryKey is null
                ? null
                : new ExpectedPrimaryKeyDefinition(PrimaryKey.Name, table, PrimaryKey.Columns, schema);

            RemovedPrimaryKey = RemovedPrimaryKey is null
                ? null
                : new ExpectedPrimaryKeyDefinition(RemovedPrimaryKey.Name, table, RemovedPrimaryKey.Columns, schema);

            UniqueConstraints.Rebind(value => new ExpectedUniqueConstraintDefinition(
                value.Name,
                table,
                value.Columns,
                schema));

            Indexes.Rebind(value => new ExpectedIndexDefinition(
                value.Name,
                table,
                value.Keys,
                schema,
                value.Unique,
                value.Filter,
                value.IncludedColumns,
                value.Method,
                value.NullsDistinct,
                value.StructuredFilter));
        }

        public bool HasCandidateKey(
            string table,
            string? schema,
            IReadOnlyList<string> columns
        )
        {
            if (PrimaryKey is not null
                && IdentifierEquals(_objectIdentityNormalizer, PrimaryKey.Table, table)
                && StringComparer.Ordinal.Equals(
                    NormalizeSchema(_objectIdentityNormalizer, PrimaryKey.Schema),
                    NormalizeSchema(_objectIdentityNormalizer, schema))
                && SameColumns(PrimaryKey.Columns, columns, _objectIdentityNormalizer))
            {
                return true;
            }

            return UniqueConstraints.Any(constraint =>
                IdentifierEquals(_objectIdentityNormalizer, constraint.Table, table)
                && StringComparer.Ordinal.Equals(
                    NormalizeSchema(_objectIdentityNormalizer, constraint.Schema),
                    NormalizeSchema(_objectIdentityNormalizer, schema))
                && SameColumns(constraint.Columns, columns, _objectIdentityNormalizer));
        }
    }

    // WHY: These four row-safety flags have value semantics. Inline dictionary storage
    // avoids allocating one short-lived prerequisite object for every accepted column.
    private readonly record struct ProjectedColumn(
        bool IsNullable,
        bool PreservesNullForExistingRows,
        bool IsComputed,
        bool AddedToExistingTable
    )
    {
        public static ProjectedColumn From(
            ExpectedColumnDefinition definition,
            bool addedToExistingTable
        ) => new(
            definition.IsNullable,
            SafeMigrationPreflightProjection.PreservesNullForExistingRows(definition.DefaultValue),
            definition.ComputedColumnSql is not null || definition.ComputedExpression is not null,
            addedToExistingTable);

        public static ProjectedColumn From(
            ColumnOperation operation,
            bool addedToExistingTable
        ) => new(
            operation.IsNullable,
            operation.DefaultValue is null && operation.DefaultValueSql is null,
            operation.ComputedColumnSql is not null,
            addedToExistingTable);

        public static ProjectedColumn Unknown { get; } = new(
            IsNullable: false,
            PreservesNullForExistingRows: false,
            IsComputed: true,
            AddedToExistingTable: false);
    }
}
