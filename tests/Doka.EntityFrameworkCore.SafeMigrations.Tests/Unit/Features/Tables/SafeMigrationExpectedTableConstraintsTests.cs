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
        const string checkSql = "code >= 0";
        var checkConstraint = SafeMigrationExpectedDefinitionFactory.CheckConstraint(
            "ck_records_code", "records", checkSql, schema: null);
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
        builder.AddCheckConstraintIfNotExists(checkConstraint.Name, checkConstraint.Table, checkSql);
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

    /// <summary>Captured CHECK renames preserve native number, type, function, and literal token roles.</summary>
    /// <param name="source">The complete quoted native CHECK expression.</param>
    /// <param name="expected">The exact source after the one certified member rename.</param>
    [Theory]
    [InlineData("[value] + 2147483648 > 0 AND 'value' <> ''", "[score] + 2147483648 > 0 AND 'value' <> ''")]
    [InlineData("`value` + 2147483648 > 0 AND 'value' <> ''", "`score` + 2147483648 > 0 AND 'value' <> ''")]
    [InlineData("\"value\" + 2147483648 > 0 AND 'value' <> ''", "\"score\" + 2147483648 > 0 AND 'value' <> ''")]
    public void CapturedCheckColumnRenamePreservesNativeRoles(string source, string expected)
    {
        // Arrange
        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_records_value", "records", source,
            schema: null);
        var builder = CreateBuilder(new ExpectedTableDefinition("records",
            [new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "integer")],
            checkConstraints: [definition]));

        builder.RenameColumnIfExists("value", "records", "score");

        // Act
        var constraints = Assert.Single(SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations)).Value;

        // Assert
        var check = Assert.Single(constraints.CheckConstraints);
        Assert.Equal(expected, check.CapturedSql);
        Assert.True(SafeMigrationSqlExpressionInspector.ReferencesIdentifier(check.Expression!, "score"));
        Assert.False(SafeMigrationSqlExpressionInspector.ReferencesIdentifier(check.Expression!, "value"));
        Assert.Equal(2, constraints.AllowedCheckConstraints.Count);
        Assert.Empty(constraints.RequiredCheckConstraints);
    }

    /// <summary>Explicit structured CHECKs can rename members without approving opaque source rewriting.</summary>
    [Fact]
    public void StructuredCheckColumnRenamePreservesOtherExpressionRoles()
    {
        // Arrange
        var expression = SafeMigrationSql.Binary(SafeMigrationSql.Cast(SafeMigrationSql.Identifier("value"), "bigint"),
            SafeMigrationSqlBinaryOperator.GreaterThan, SafeMigrationSql.Literal(0));

        var definition = ExpectedCheckConstraintDefinition.FromExpression("ck_records_value", "records", expression);
        var builder = CreateBuilder(new ExpectedTableDefinition("records",
            [new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "integer")],
            checkConstraints: [definition]));

        builder.RenameColumnIfExists("value", "records", "score");

        // Act
        var constraints = Assert.Single(SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations)).Value;

        // Assert
        var check = Assert.Single(constraints.CheckConstraints);
        var binary = Assert.IsType<SafeMigrationSqlBinaryExpression>(check.Expression);
        var cast = Assert.IsType<SafeMigrationSqlCastExpression>(binary.Left);
        Assert.Equal("bigint", cast.StoreType);
        Assert.Equal("score", Assert.Single(Assert.IsType<SafeMigrationSqlIdentifierExpression>(cast.Operand).Parts));
        Assert.Null(check.CapturedSql);
    }

    /// <summary>Owner copies and qualifier normalization never erase a captured CHECK's source contract.</summary>
    [Fact]
    public void CapturedCheckOwnerAndSchemaCopiesPreserveSource()
    {
        // Arrange
        const string source = "CASE WHEN [value] > 0 THEN 2147483648 / 2147483647 ELSE 0 END >= 0";
        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_records_value", "records", source,
            schema: null);
        var builder = CreateBuilder(new ExpectedTableDefinition("records",
            [new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "integer")],
            checkConstraints: [definition]));

        builder.RenameTableIfExists("records", "renamed_records");

        // Act
        var constraints = Assert.Single(SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations,
            static schema => schema ?? "native")).Value;

        // Assert
        var check = Assert.Single(constraints.CheckConstraints);
        Assert.Same(source, check.CapturedSql);
        Assert.Equal("native", check.Schema);
        Assert.Equal("renamed_records", check.Table);
        Assert.True(SafeMigrationSqlExpressionContract.Equivalent(definition.Expression, check.Expression));
    }

    /// <summary>An unrelated member rename retains a captured CHECK's exact native source.</summary>
    [Fact]
    public void UnrelatedColumnRenamePreservesCapturedCheckSource()
    {
        // Arrange
        const string source = "[value] > 2147483648";
        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_records_value", "records", source,
            schema: null);
        var builder = CreateBuilder(new ExpectedTableDefinition("records",
            [new ExpectedColumnDefinition("other", typeof(int), isNullable: false, storeType: "integer")],
            checkConstraints: [definition]));

        builder.RenameColumnIfExists("other", "records", "renamed");

        // Act
        var constraints = Assert.Single(SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations)).Value;

        // Assert
        var check = Assert.Single(constraints.CheckConstraints);
        Assert.Same(source, check.CapturedSql);
        Assert.Single(constraints.AllowedCheckConstraints);
        Assert.Single(constraints.RequiredCheckConstraints);
    }

    /// <summary>Unquoted captured members use only the provider's verified quote and ASCII folding contract.</summary>
    /// <param name="source">The original complete native expression.</param>
    /// <param name="delimiter">The active provider's verified identifier quote.</param>
    /// <param name="fold">Whether unquoted ASCII identifiers fold natively.</param>
    /// <param name="target">The renamed physical column.</param>
    /// <param name="expected">The exact rewritten native expression.</param>
    [Theory]
    [InlineData("value + 2147483648 > 0", '[', false, "new value", "[new value] + 2147483648 > 0")]
    [InlineData("value + 2147483648 > 0", '`', false, "new`value", "`new``value` + 2147483648 > 0")]
    [InlineData("VALUE + 2147483648 > 0", '"', true, "select", "\"select\" + 2147483648 > 0")]
    public void CapturedCheckRenameUsesVerifiedNativeIdentifierRoles(
        string source, char delimiter, bool fold, string target, string expected)
    {
        // Arrange
        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_records_value", "records", source,
            schema: null);

        var builder = CreateBuilder(new ExpectedTableDefinition("records",
            [new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "integer")],
            checkConstraints: [definition]));

        builder.RenameColumnIfExists("value", "records", target);

        // Act
        var constraints = Assert.Single(SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations,
            static schema => schema, StringComparer.Ordinal, delimiter, fold)).Value;

        // Assert
        var check = Assert.Single(constraints.CheckConstraints);
        Assert.Equal(expected, check.CapturedSql);
        Assert.True(SafeMigrationSqlExpressionInspector.ReferencesIdentifier(check.Expression!, target));
    }

    /// <summary>A portable transition catalog cannot invent a native quote for an affected unquoted source.</summary>
    [Fact]
    public void CapturedCheckRenameWithoutNativeQuoteAuthorityFailsClosed()
    {
        // Arrange
        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_records_value", "records",
            "value > 0", schema: null);

        var builder = CreateBuilder(new ExpectedTableDefinition("records",
            [new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "integer")],
            checkConstraints: [definition]));

        builder.RenameColumnIfExists("value", "records", "score");

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>A native case-insensitive CHECK member retains its identity through a physical column rename.</summary>
    [Fact]
    public void CapturedCheckRenameKeepsNativeCaseInsensitiveColumnIdentity()
    {
        // Arrange
        const string source = "[VALUE] + 2147483648 > 0 AND 'VALUE' <> ''";
        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_records_value", "records", source,
            schema: null);

        var builder = CreateBuilder(new ExpectedTableDefinition("records",
            [new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "integer")],
            checkConstraints: [definition]));

        builder.RenameColumnIfExists("value", "records", "score");

        // Act
        var constraints = Assert.Single(SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations,
            static schema => schema, StringComparer.Ordinal, '[', false, StringComparer.OrdinalIgnoreCase)).Value;

        // Assert
        Assert.Equal("[score] + 2147483648 > 0 AND 'VALUE' <> ''",
            Assert.Single(constraints.CheckConstraints).CapturedSql);
    }

    /// <summary>A CHECK dependency cannot disappear when its native member spelling differs only by case.</summary>
    [Fact]
    public void CapturedCheckDropKeepsNativeCaseInsensitiveColumnDependency()
    {
        // Arrange
        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_records_value", "records",
            "[VALUE] >= 0", schema: null);

        var builder = CreateBuilder(new ExpectedTableDefinition("records",
            [new ExpectedColumnDefinition("value", typeof(int), isNullable: false, storeType: "integer")],
            checkConstraints: [definition]));

        builder.DropColumnIfExists("value", "records");

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations,
            static schema => schema, StringComparer.Ordinal, '[', false, StringComparer.OrdinalIgnoreCase));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>The column proof never inherits a table-name comparer or rewrites other native token roles.</summary>
    /// <param name="caseSensitive">Whether the physical columns L and l have independent native identities.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CapturedCheckColumnIdentityIsIndependentFromTableIdentity(bool caseSensitive)
    {
        // Arrange
        const string source = "ABS([l]) + CAST([l] AS bigint) > 0 AND 'L' <> ''";
        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_records_value", "records", source,
            schema: null);

        var columns = caseSensitive
            ? new[]
            {
                new ExpectedColumnDefinition("L", typeof(int), isNullable: false, storeType: "integer"),
                new ExpectedColumnDefinition("l", typeof(int), isNullable: false, storeType: "integer"),
            }
            : [new ExpectedColumnDefinition("L", typeof(int), isNullable: false, storeType: "integer")];

        var builder = CreateBuilder(new ExpectedTableDefinition("records", columns, checkConstraints: [definition]));
        builder.RenameColumnIfExists("L", "records", "score");
        var columnComparer = caseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        // Act
        var constraints = Assert.Single(SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations,
            static schema => schema, StringComparer.OrdinalIgnoreCase, '[', false, columnComparer)).Value;

        // Assert
        var expected = caseSensitive ? source : "ABS([score]) + CAST([score] AS bigint) > 0 AND 'L' <> ''";
        Assert.Equal(expected, Assert.Single(constraints.CheckConstraints).CapturedSql);
    }

    /// <summary>A case-sensitive CHECK on l does not prevent dropping the independent physical member L.</summary>
    [Fact]
    public void CaseSensitiveCheckDropKeepsDistinctColumnMembers()
    {
        // Arrange
        var definition = SafeMigrationExpectedDefinitionFactory.CheckConstraint("ck_records_value", "records",
            "[l] >= 0", schema: null);

        var builder = CreateBuilder(new ExpectedTableDefinition("records",
            [
                new ExpectedColumnDefinition("L", typeof(int), isNullable: false, storeType: "integer"),
                new ExpectedColumnDefinition("l", typeof(int), isNullable: false, storeType: "integer"),
            ], checkConstraints: [definition]));

        builder.DropColumnIfExists("L", "records");

        // Act
        var constraints = Assert.Single(SafeMigrationExpectedTableConstraints.FromOperations(builder.Operations,
            static schema => schema, StringComparer.OrdinalIgnoreCase, '[', false, StringComparer.Ordinal)).Value;

        // Assert
        Assert.Equal("[l] >= 0", Assert.Single(constraints.CheckConstraints).CapturedSql);
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
