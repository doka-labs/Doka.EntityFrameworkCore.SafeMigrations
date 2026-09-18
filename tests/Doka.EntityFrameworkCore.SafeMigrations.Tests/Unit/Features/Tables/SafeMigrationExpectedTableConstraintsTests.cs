namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationExpectedTableConstraintsTests
{
    [Fact]
    public void OperationProjectionTracksIntermediateAndTerminalConstraintStates()
    {
        // Arrange
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "records",
                [
                    new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("code", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true, storeType: "integer"),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        builder.AddPrimaryKeyIfNotExists("pk_records_old", "records", ["id"]);
        builder.DropPrimaryKeyIfExists("pk_records_old", "records");
        builder.AddPrimaryKeyIfNotExists("pk_records", "records", ["id"]);

        builder.AddUniqueConstraintIfNotExists("uq_records_old", "records", ["code"]);
        builder.DropUniqueConstraintIfExists("uq_records_old", "records");
        builder.AddUniqueConstraintIfNotExists("uq_records_code", "records", ["code"]);

        builder.AddCheckConstraintIfNotExists("ck_records_old", "records", "code >= 0");
        builder.DropCheckConstraintIfExists("ck_records_old", "records");
        builder.AddCheckConstraintIfNotExists("ck_records_code", "records", "code > 0");

        builder.AddForeignKeyIfNotExists(
            "fk_records_old",
            "records",
            ["parent_id"],
            "records",
            ["id"]);
        builder.DropForeignKeyIfExists("fk_records_old", "records");
        builder.AddForeignKeyIfNotExists(
            "fk_records_parent",
            "records",
            ["parent_id"],
            "records",
            ["id"],
            onDelete: ReferentialAction.SetNull);

        // Act
        var projected = SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations);
        var constraints = Assert.Single(projected).Value;

        // Assert
        Assert.Equal("pk_records", constraints.PrimaryKey?.Name);
        Assert.Equal("uq_records_code", Assert.Single(constraints.UniqueConstraints).Name);
        Assert.Equal("ck_records_code", Assert.Single(constraints.CheckConstraints).Name);

        var foreignKey = Assert.Single(constraints.ForeignKeys);
        Assert.Equal("fk_records_parent", foreignKey.Name);
        Assert.Equal(ReferentialAction.SetNull, foreignKey.OnDelete);

        Assert.True(constraints.PrimaryKeyMayBeAbsent);
        Assert.Equal(
            ["pk_records_old", "pk_records"],
            constraints.AllowedPrimaryKeys.Select(static value => value.Name));
        Assert.Empty(constraints.RequiredUniqueConstraints);
        Assert.Equal(
            ["uq_records_code", "uq_records_old"],
            constraints.AllowedUniqueConstraints.Select(static value => value.Name));
        Assert.Empty(constraints.RequiredCheckConstraints);
        Assert.Equal(
            ["ck_records_code", "ck_records_old"],
            constraints.AllowedCheckConstraints.Select(static value => value.Name));
        Assert.Empty(constraints.RequiredForeignKeys);
        Assert.Equal(
            ["fk_records_old", "fk_records_parent"],
            constraints.AllowedForeignKeys.Select(static value => value.Name));
    }

    [Fact]
    public void StableConstraintsRemainRequiredWhileStandaloneAddsRemainOptionalAtTableAnalysis()
    {
        // Arrange
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "records",
                [
                    new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("code", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true, storeType: "integer"),
                ],
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_records", "records", ["id"]),
                uniqueConstraints:
                [
                    new ExpectedUniqueConstraintDefinition("uq_records_code", "records", ["code"]),
                ],
                checkConstraints:
                [
                    new ExpectedCheckConstraintDefinition("ck_records_code", "records", "code >= 0"),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddForeignKeyIfNotExists(
            "fk_records_parent",
            "records",
            ["parent_id"],
            "records",
            ["id"],
            onDelete: ReferentialAction.SetNull);

        // Act
        var projected = SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations);
        var constraints = Assert.Single(projected).Value;

        // Assert
        Assert.False(constraints.PrimaryKeyMayBeAbsent);
        Assert.Equal("pk_records", Assert.Single(constraints.AllowedPrimaryKeys).Name);
        Assert.Equal("uq_records_code", Assert.Single(constraints.RequiredUniqueConstraints).Name);
        Assert.Equal("ck_records_code", Assert.Single(constraints.RequiredCheckConstraints).Name);
        Assert.Empty(constraints.RequiredForeignKeys);
        Assert.Equal("fk_records_parent", Assert.Single(constraints.AllowedForeignKeys).Name);
    }

    [Fact]
    public void DropAndReaddOfSameDefinitionPreservesAllowedShapeWithoutTreatingItAsContinuous()
    {
        // Arrange
        var primaryKey = new ExpectedPrimaryKeyDefinition("pk_records", "records", ["id"]);
        var uniqueConstraint = new ExpectedUniqueConstraintDefinition("uq_records_code", "records", ["code"]);
        var checkConstraint = new ExpectedCheckConstraintDefinition("ck_records_code", "records", "code >= 0");
        var foreignKey = new ExpectedForeignKeyDefinition(
            "fk_records_parent",
            "records",
            ["parent_id"],
            "records",
            ["id"]);
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "records",
                [
                    new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("code", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: true, storeType: "integer"),
                ],
                primaryKey: primaryKey,
                uniqueConstraints: [uniqueConstraint],
                checkConstraints: [checkConstraint],
                foreignKeys: [foreignKey]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropPrimaryKeyIfExists(primaryKey.Name, primaryKey.Table);
        builder.AddPrimaryKeyIfNotExists(primaryKey.Name, primaryKey.Table, primaryKey.Columns);
        builder.DropUniqueConstraintIfExists(uniqueConstraint.Name, uniqueConstraint.Table);
        builder.AddUniqueConstraintIfNotExists(
            uniqueConstraint.Name,
            uniqueConstraint.Table,
            uniqueConstraint.Columns);
        builder.DropCheckConstraintIfExists(checkConstraint.Name, checkConstraint.Table);
        builder.AddCheckConstraintIfNotExists(checkConstraint.Name, checkConstraint.Table, checkConstraint.Sql!);
        builder.DropForeignKeyIfExists(foreignKey.Name, foreignKey.Table);
        builder.AddForeignKeyIfNotExists(
            foreignKey.Name,
            foreignKey.Table,
            foreignKey.Columns,
            foreignKey.PrincipalTable,
            foreignKey.PrincipalColumns);

        // Act
        var projected = SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations);
        var constraints = Assert.Single(projected).Value;

        // Assert
        Assert.True(constraints.PrimaryKeyMayBeAbsent);
        Assert.Equal(primaryKey.Name, Assert.Single(constraints.AllowedPrimaryKeys).Name);
        Assert.Empty(constraints.RequiredUniqueConstraints);
        Assert.Equal(uniqueConstraint.Name, Assert.Single(constraints.AllowedUniqueConstraints).Name);
        Assert.Empty(constraints.RequiredCheckConstraints);
        Assert.Equal(checkConstraint.Name, Assert.Single(constraints.AllowedCheckConstraints).Name);
        Assert.Empty(constraints.RequiredForeignKeys);
        Assert.Equal(foreignKey.Name, Assert.Single(constraints.AllowedForeignKeys).Name);
    }

    [Fact]
    public void DroppedTableHasNoTerminalConstraintContract()
    {
        // Arrange
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "records",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer")]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.AddPrimaryKeyIfNotExists("pk_records", "records", ["id"]);
        builder.DropTableIfExists("records");

        // Act
        var projected = SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations);

        // Assert
        Assert.Empty(projected);
    }

    [Fact]
    public void RenamesUpdateOwnedAndReferencingConstraintIdentities()
    {
        // Arrange
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "parents",
                [
                    new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("code", typeof(int), isNullable: false, storeType: "integer"),
                ],
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_parents", "parents", ["id"]),
                uniqueConstraints:
                [
                    new ExpectedUniqueConstraintDefinition("uq_parents_code", "parents", ["code"]),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "children",
                [
                    new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("parent_id", typeof(int), isNullable: false, storeType: "integer"),
                ],
                foreignKeys:
                [
                    new ExpectedForeignKeyDefinition(
                        "fk_children_parents",
                        "children",
                        ["parent_id"],
                        "parents",
                        ["id"]),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameColumnIfExists("id", "parents", "parent_key");
        builder.RenameTableIfExists("parents", "renamed_parents");
        builder.RenameColumnIfExists("parent_id", "children", "owner_id");

        // Act
        var projected = SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations);

        // Assert
        var parent = projected[(null, "renamed_parents")];
        Assert.Equal(["parent_key"], parent.PrimaryKey?.Columns);
        Assert.Equal("renamed_parents", Assert.Single(parent.UniqueConstraints).Table);

        var childForeignKey = Assert.Single(projected[(null, "children")].ForeignKeys);
        Assert.Equal(["owner_id"], childForeignKey.Columns);
        Assert.Equal("renamed_parents", childForeignKey.PrincipalTable);
        Assert.Equal(["parent_key"], childForeignKey.PrincipalColumns);
    }

    [Fact]
    public void ColumnRenameWithOwnedCheckConstraintFailsClosed()
    {
        // Arrange
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "records",
                [new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "integer")],
                checkConstraints:
                [
                    new ExpectedCheckConstraintDefinition("ck_records_value", "records", "value >= 0"),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.RenameColumnIfExists("value", "records", "score");

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("Drop and recreate owned check constraints", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnDropWithPrimaryKeyDependencyFailsClosed()
    {
        // Arrange
        var builder = CreateBuilder(
            new ExpectedTableDefinition(
                "records",
                [new ExpectedColumnDefinition("legacy_id", typeof(int), isNullable: false, storeType: "integer")],
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_records", "records", ["legacy_id"])));
        builder.DropColumnIfExists("legacy_id", "records");

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("primary key 'pk_records'", invalidOperation.Message, StringComparison.Ordinal);
        Assert.Contains("Drop the constraint explicitly", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnDropWithUniqueConstraintDependencyFailsClosed()
    {
        // Arrange
        var builder = CreateBuilder(
            new ExpectedTableDefinition(
                "records",
                [new ExpectedColumnDefinition("legacy_code", typeof(int), isNullable: false, storeType: "integer")],
                uniqueConstraints:
                [
                    new ExpectedUniqueConstraintDefinition("uq_records_code", "records", ["legacy_code"]),
                ]));
        builder.DropColumnIfExists("legacy_code", "records");

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("unique constraint 'uq_records_code'", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnDropWithStructuredCheckConstraintDependencyFailsClosed()
    {
        // Arrange
        var check = ExpectedCheckConstraintDefinition.FromExpression(
            "ck_records_score",
            "records",
            SafeMigrationSql.Binary(
                SafeMigrationSql.Identifier("legacy_score"),
                SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                SafeMigrationSql.Literal(0)));
        var builder = CreateBuilder(
            new ExpectedTableDefinition(
                "records",
                [new ExpectedColumnDefinition("legacy_score", typeof(int), isNullable: false, storeType: "integer")],
                checkConstraints: [check]));
        builder.DropColumnIfExists("legacy_score", "records");

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("check constraint 'ck_records_score'", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnDropWithOpaqueCheckConstraintFailsClosedWhenDependencyIsUnknown()
    {
        // Arrange
        var builder = CreateBuilder(
            new ExpectedTableDefinition(
                "records",
                [
                    new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer"),
                    new ExpectedColumnDefinition("legacy", typeof(int), isNullable: true, storeType: "integer"),
                ],
                checkConstraints:
                [
                    new ExpectedCheckConstraintDefinition("ck_records_id", "records", "id >= 0"),
                ]));
        builder.DropColumnIfExists("legacy", "records");

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("check constraint 'ck_records_id'", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnDropWithLocalForeignKeyDependencyFailsClosed()
    {
        // Arrange
        var builder = CreateBuilder(
            new ExpectedTableDefinition(
                "records",
                [new ExpectedColumnDefinition("legacy_parent_id", typeof(int), true, "integer")],
                foreignKeys:
                [
                    new ExpectedForeignKeyDefinition(
                        "fk_records_parent",
                        "records",
                        ["legacy_parent_id"],
                        "parents",
                        ["id"]),
                ]));
        builder.DropColumnIfExists("legacy_parent_id", "records");

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("foreign key 'fk_records_parent'", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnDropWithReferencingForeignKeyDependencyFailsClosed()
    {
        // Arrange
        var builder = CreateBuilder(
            new ExpectedTableDefinition(
                "parents",
                [new ExpectedColumnDefinition("legacy_id", typeof(int), false, "integer")]));
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "children",
                [new ExpectedColumnDefinition("parent_id", typeof(int), false, "integer")],
                foreignKeys:
                [
                    new ExpectedForeignKeyDefinition(
                        "fk_children_parent",
                        "children",
                        ["parent_id"],
                        "parents",
                        ["legacy_id"]),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropColumnIfExists("legacy_id", "parents");

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations));

        // Assert
        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("referencing foreign key 'fk_children_parent'", invalidOperation.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ColumnDropAfterExplicitDependencyDropsProjectsTerminalContract()
    {
        // Arrange
        var builder = CreateBuilder(
            new ExpectedTableDefinition(
                "records",
                [new ExpectedColumnDefinition("legacy", typeof(int), false, "integer")],
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_records", "records", ["legacy"]),
                uniqueConstraints:
                [
                    new ExpectedUniqueConstraintDefinition("uq_records_legacy", "records", ["legacy"]),
                ],
                checkConstraints:
                [
                    ExpectedCheckConstraintDefinition.FromExpression(
                        "ck_records_legacy",
                        "records",
                        SafeMigrationSql.Binary(
                            SafeMigrationSql.Identifier("legacy"),
                            SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                            SafeMigrationSql.Literal(0))),
                ]));
        builder.DropPrimaryKeyIfExists("pk_records", "records");
        builder.DropUniqueConstraintIfExists("uq_records_legacy", "records");
        builder.DropCheckConstraintIfExists("ck_records_legacy", "records");
        builder.DropColumnIfExists("legacy", "records");

        // Act
        var projected = SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations);
        var constraints = Assert.Single(projected).Value;

        // Assert
        Assert.Null(constraints.PrimaryKey);
        Assert.Empty(constraints.UniqueConstraints);
        Assert.Empty(constraints.CheckConstraints);
        Assert.Empty(constraints.ForeignKeys);
    }

    [Fact]
    public void ColumnDropsAfterExplicitForeignKeyDropProjectBothTables()
    {
        // Arrange
        var builder = CreateBuilder(
            new ExpectedTableDefinition(
                "parents",
                [new ExpectedColumnDefinition("legacy_id", typeof(int), false, "integer")]));
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "children",
                [new ExpectedColumnDefinition("legacy_parent_id", typeof(int), true, "integer")],
                foreignKeys:
                [
                    new ExpectedForeignKeyDefinition(
                        "fk_children_parent",
                        "children",
                        ["legacy_parent_id"],
                        "parents",
                        ["legacy_id"]),
                ]),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropForeignKeyIfExists("fk_children_parent", "children");
        builder.DropColumnIfExists("legacy_parent_id", "children");
        builder.DropColumnIfExists("legacy_id", "parents");

        // Act
        var projected = SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations);

        // Assert
        Assert.Empty(projected[(null, "parents")].ForeignKeys);
        Assert.Empty(projected[(null, "children")].ForeignKeys);
    }

    [Fact]
    public void ColumnDropPreservesUnrelatedStructuredConstraints()
    {
        // Arrange
        var builder = CreateBuilder(
            new ExpectedTableDefinition(
                "records",
                [
                    new ExpectedColumnDefinition("id", typeof(int), false, "integer"),
                    new ExpectedColumnDefinition("legacy", typeof(int), true, "integer"),
                ],
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_records", "records", ["id"]),
                checkConstraints:
                [
                    ExpectedCheckConstraintDefinition.FromExpression(
                        "ck_records_id",
                        "records",
                        SafeMigrationSql.Binary(
                            SafeMigrationSql.Identifier("id"),
                            SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
                            SafeMigrationSql.Literal(0))),
                ]));
        builder.DropColumnIfExists("legacy", "records");

        // Act
        var projected = SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations);
        var constraints = Assert.Single(projected).Value;

        // Assert
        Assert.Equal("pk_records", constraints.PrimaryKey?.Name);
        Assert.Equal("ck_records_id", Assert.Single(constraints.CheckConstraints).Name);
    }

    [Fact]
    public void DropWithDifferentPrimaryKeyNamePreservesTheTerminalContract()
    {
        // Arrange
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            new ExpectedTableDefinition(
                "records",
                [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer")],
                primaryKey: new ExpectedPrimaryKeyDefinition("pk_records", "records", ["id"])),
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);
        builder.DropPrimaryKeyIfExists("pk_other", "records");

        // Act
        var projected = SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations);

        // Assert
        Assert.Equal("pk_records", Assert.Single(projected).Value.PrimaryKey?.Name);
    }

    private static MigrationBuilder CreateBuilder(
        ExpectedTableDefinition definition
    )
    {
        var builder = new MigrationBuilder("Provider");
        builder.EnsureTable(
            definition,
            SafeMigrationTableMode.StrictDefinition,
            SafeMigrationPolicy.ThrowIfDifferent);

        return builder;
    }
}
