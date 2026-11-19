namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Verifies semantic inventory candidates preserve complete endpoint and key shapes without binding unrelated owners.
/// </summary>
public sealed class SafeMigrationSemanticCandidateFactoryTests
{
    /// <summary>
    /// Rebinds unexpected physical names while preserving complete FK endpoints, index directions and semantic
    /// deduplication.
    /// </summary>
    [Fact]
    public void Create_ProjectsEverySupportedNamedObjectWithoutChangingItsShape()
    {
        // Arrange
        var operations = new MigrationOperation[]
        {
            Operation(
                new EnsurePrimaryKeyIntent(
                    new ExpectedPrimaryKeyDefinition("pk_expected", "children", ["id", "tenant_id"]))),
            Operation(
                new EnsureUniqueConstraintIntent(
                    new ExpectedUniqueConstraintDefinition("uq_expected", "children", ["code", "tenant_id"]))),
            Operation(
                new EnsureCheckConstraintIntent(
                    ExpectedCheckConstraintDefinition.FromExpression(
                        "ck_expected",
                        "children",
                        SafeMigrationSql.Binary(
                            SafeMigrationSql.Identifier("value"),
                            SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                            SafeMigrationSql.Literal(0))))),
            Operation(
                new EnsureForeignKeyIntent(
                    new ExpectedForeignKeyDefinition(
                        "fk_expected",
                        "children",
                        ["parent_id", "tenant_id"],
                        "parents",
                        ["id", "tenant_id"],
                        onDelete: ReferentialAction.Cascade))),
            Operation(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_expected_duplicate",
                        "children",
                        [
                            new ExpectedIndexKeyDefinition(
                                column: "code",
                                sortOrder: SafeMigrationIndexSortOrder.Descending),
                            new ExpectedIndexKeyDefinition(column: "tenant_id"),
                        ],
                        unique: true))),
            Operation(
                new EnsureIndexIntent(
                    new ExpectedIndexDefinition(
                        "ix_expected",
                        "children",
                        [
                            new ExpectedIndexKeyDefinition(
                                column: "code",
                                sortOrder: SafeMigrationIndexSortOrder.Descending),
                            new ExpectedIndexKeyDefinition(column: "tenant_id"),
                        ],
                        unique: true))),
        };

        var unexpected = new[]
        {
            Unexpected(SafeMigrationDatabaseObjectKind.PrimaryKey, "pk_legacy"),
            Unexpected(SafeMigrationDatabaseObjectKind.UniqueConstraint, "uq_legacy"),
            Unexpected(SafeMigrationDatabaseObjectKind.CheckConstraint, "ck_legacy"),
            Unexpected(SafeMigrationDatabaseObjectKind.ForeignKey, "fk_legacy"),
            Unexpected(SafeMigrationDatabaseObjectKind.Index, "ix_legacy"),
        };

        // Act
        var candidates = SafeMigrationSemanticCandidateFactory
            .Create(
                operations,
                unexpected,
                projectUniqueIndexesAsUniqueConstraints: true)
            .ToArray();

        // Assert
        Assert.Equal(6, candidates.Length);
        Assert.Equal(
            ["pk_legacy", "uq_legacy", "uq_legacy", "ck_legacy", "fk_legacy", "ix_legacy"],
            candidates.Select(static value => value.Operation.Intent.ObjectName));

        var foreignKey = Assert.IsType<EnsureForeignKeyIntent>(candidates[4].Operation.Intent)
            .Definition;

        Assert.Equal(["parent_id", "tenant_id"], foreignKey.Columns);
        Assert.Equal(["id", "tenant_id"], foreignKey.PrincipalColumns);
        Assert.Equal(ReferentialAction.Cascade, foreignKey.OnDelete);

        var index = Assert.IsType<EnsureIndexIntent>(candidates[5].Operation.Intent)
            .Definition;

        Assert.True(index.Unique);
        Assert.Equal(SafeMigrationIndexSortOrder.Descending, index.Keys[0].SortOrder);

        Assert.IsType<EnsureIndexIntent>(candidates[2].Operation.Intent);
    }

    /// <summary>
    /// Produces no semantic candidate for another owner, a column object or an index lacking an owner.
    /// </summary>
    [Fact]
    public void Create_IgnoresDifferentTablesAndUnsupportedObjectKinds()
    {
        // Arrange
        var operation = Operation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_expected",
                    "expected_table",
                    [new ExpectedIndexKeyDefinition(column: "code")])));

        // Act
        var candidates = SafeMigrationSemanticCandidateFactory
            .Create(
                [operation],
                [
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Index,
                        schema: null,
                        "other_table",
                        "ix_other",
                        "unexpected_index"),
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Column,
                        schema: null,
                        "expected_table",
                        "code",
                        "unexpected_column"),
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Index,
                        schema: null,
                        table: null,
                        "ix_without_table",
                        "unexpected_index"),
                ])
            .ToArray();

        // Assert
        Assert.Empty(candidates);
    }

    /// <summary>
    /// Binds an unqualified expected index only to the independently declared provider default schema.
    /// </summary>
    [Fact]
    public void Create_BindsUnqualifiedContractsToTheProviderDefaultSchema()
    {
        // Arrange
        var operation = Operation(
            new EnsureIndexIntent(
                new ExpectedIndexDefinition(
                    "ix_expected",
                    "records",
                    [new ExpectedIndexKeyDefinition(column: "code")])));

        // Act
        var candidates = SafeMigrationSemanticCandidateFactory
            .Create(
                [operation],
                [
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Index,
                        "tenant",
                        "records",
                        "ix_tenant",
                        "unexpected_index"),
                    new SafeMigrationUnexpectedObject(
                        SafeMigrationDatabaseObjectKind.Index,
                        "public",
                        "records",
                        "ix_public",
                        "unexpected_index"),
                ],
                defaultSchema: "public")
            .ToArray();

        // Assert
        var candidate = Assert.Single(candidates);

        Assert.Equal(1, candidate.UnexpectedObjectIndex);
        Assert.Equal("ix_public", candidate.Operation.Intent.ObjectName);
    }

    private static SafeMigrationOperation Operation(
        SafeMigrationIntent intent
    ) => new(intent, SafeMigrationPolicy.ThrowIfDifferent);

    private static SafeMigrationUnexpectedObject Unexpected(
        SafeMigrationDatabaseObjectKind kind,
        string name
    ) => new(kind, schema: null, "children", name, $"unexpected_{kind}");
}
