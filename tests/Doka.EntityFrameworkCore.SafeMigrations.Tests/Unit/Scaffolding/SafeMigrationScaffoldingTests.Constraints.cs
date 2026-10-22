namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Generates a structurally comparable CHECK as part of safe table creation.</summary>
    [Fact]
    public void CreateTableGenerationAcceptsStructurallyComparableCheckConstraints()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = CreateTable();
        operation.CheckConstraints.Add(
            new AddCheckConstraintOperation
            {
                Name = "ck_users_id",
                Table = "users",
                Sql = "`id` >= 0",
            });

        // Act
        generator.Generate("migrationBuilder", [operation], builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains(".CreateTableIfNotExists(", source, StringComparison.Ordinal);
        Assert.Contains("table.CheckConstraint(\"ck_users_id\", \"`id` >= 0\")", source, StringComparison.Ordinal);
    }

    /// <summary>Rejects an opaque table CHECK before publishing a partial CREATE.</summary>
    [Fact]
    public void CreateTableGenerationRejectsOpaqueCheckConstraintBeforeEmittingSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = CreateTable();
        operation.CheckConstraints.Add(
            new AddCheckConstraintOperation
            {
                Name = "ck_users_reference",
                Table = "users",
                Sql = "reference LIKE 'USR-%'",
            });

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        Assert.DoesNotContain("CreateTable", builder.ToString(), StringComparison.Ordinal);
        Assert.Contains("ck_users_reference", exception.Message, StringComparison.Ordinal);
        Assert.Contains("trailing_token", exception.Message, StringComparison.Ordinal);
        Assert.Contains("FromExpression", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Preserves composite principal-column ordering in generated foreign-key source.</summary>
    [Fact]
    public void CompositeForeignKeyGenerationUsesAnalyzerCompatiblePrincipalColumns()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = CreateTable();
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "tenant_id",
                Table = "users",
                ClrType = typeof(int),
                ColumnType = "integer",
                IsNullable = false,
            });
        operation.Columns.Add(
            new AddColumnOperation
            {
                Name = "role_id",
                Table = "users",
                ClrType = typeof(int),
                ColumnType = "integer",
                IsNullable = false,
            });
        operation.ForeignKeys.Add(
            new AddForeignKeyOperation
            {
                Name = "fk_users_roles",
                Table = "users",
                Columns = ["tenant_id", "role_id"],
                PrincipalTable = "roles",
                PrincipalColumns = ["tenant_id", "id"],
            });

        // Act
        generator.Generate("migrationBuilder", [operation], builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains("principalColumns: [\"tenant_id\", \"id\"]", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new[]", source, StringComparison.Ordinal);
    }

    /// <summary>Preserves primary, unique, CHECK and foreign-key contracts in safe source.</summary>
    [Fact]
    public void StandaloneConstraintGenerationUsesSafeCompleteContracts()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        MigrationOperation[] operations =
        [
            new AddPrimaryKeyOperation
            {
                Name = "pk_users",
                Table = "users",
                Columns = ["tenant_id", "id"],
                Schema = "identity",
            },
            new AddUniqueConstraintOperation
            {
                Name = "uq_users_email",
                Table = "users",
                Columns = ["email"],
            },
            new AddCheckConstraintOperation
            {
                Name = "ck_users_id",
                Table = "users",
                Sql = "`id` >= 0",
            },
            new AddForeignKeyOperation
            {
                Name = "fk_users_roles",
                Table = "users",
                Columns = ["tenant_id", "role_id"],
                PrincipalTable = "roles",
                PrincipalColumns = ["tenant_id", "id"],
                Schema = "identity",
                PrincipalSchema = "authorization",
                OnUpdate = ReferentialAction.Cascade,
                OnDelete = ReferentialAction.SetNull,
            },
        ];

        // Act
        generator.Generate("migrationBuilder", operations, builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains("migrationBuilder.AddPrimaryKeyIfNotExists(", source, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.AddUniqueConstraintIfNotExists(", source, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.AddCheckConstraintIfNotExists(", source, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.AddForeignKeyIfNotExists(", source, StringComparison.Ordinal);
        Assert.Contains("columns: [\"tenant_id\", \"role_id\"]", source, StringComparison.Ordinal);
        Assert.Contains("principalColumns: [\"tenant_id\", \"id\"]", source, StringComparison.Ordinal);
        Assert.Contains("schema: \"identity\"", source, StringComparison.Ordinal);
        Assert.Contains("principalSchema: \"authorization\"", source, StringComparison.Ordinal);
        Assert.Contains("ReferentialAction.Cascade", source, StringComparison.Ordinal);
        Assert.Contains("ReferentialAction.SetNull", source, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(source, "SafeMigrationPolicy.ThrowIfDifferent"));
        Assert.DoesNotContain("migrationBuilder.AddPrimaryKey(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.AddUniqueConstraint(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.AddCheckConstraint(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.AddForeignKey(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("new[]", source, StringComparison.Ordinal);
    }

    /// <summary>Emits safe standalone constraint DROPs with original schema identities.</summary>
    [Fact]
    public void StandaloneConstraintDropGenerationUsesSafeEntryPoints()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        MigrationOperation[] operations =
        [
            new DropPrimaryKeyOperation { Name = "pk_users", Table = "users", Schema = "identity" },
            new DropUniqueConstraintOperation { Name = "uq_users_email", Table = "users", Schema = "identity" },
            new DropCheckConstraintOperation { Name = "ck_users_id", Table = "users", Schema = "identity" },
            new DropForeignKeyOperation { Name = "fk_users_roles", Table = "users", Schema = "identity" },
        ];

        // Act
        generator.Generate("migrationBuilder", operations, builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains("migrationBuilder.DropPrimaryKeyIfExists(", source, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.DropUniqueConstraintIfExists(", source, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.DropCheckConstraintIfExists(", source, StringComparison.Ordinal);
        Assert.Contains("migrationBuilder.DropForeignKeyIfExists(", source, StringComparison.Ordinal);
        Assert.Equal(4, CountOccurrences(source, "schema: \"identity\""));
        Assert.DoesNotContain("migrationBuilder.DropPrimaryKey(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.DropUniqueConstraint(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.DropCheckConstraint(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("migrationBuilder.DropForeignKey(", source, StringComparison.Ordinal);
    }

    /// <summary>Keeps ordinary foreign-key generation unchanged when disabled.</summary>
    [Fact]
    public void DisabledScaffoldingPreservesStandaloneForeignKeyGeneration()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict, isEnabled: false);
        var builder = new IndentedStringBuilder();
        var operation = new AddForeignKeyOperation
        {
            Name = "fk_users_roles",
            Table = "users",
            Columns = ["role_id"],
            PrincipalTable = "roles",
            PrincipalColumns = ["id"],
        };

        // Act
        generator.Generate("migrationBuilder", [operation], builder);
        var source = builder.ToString();

        // Assert
        Assert.Contains("migrationBuilder.AddForeignKey(", source, StringComparison.Ordinal);
        Assert.DoesNotContain("AddForeignKeyIfNotExists", source, StringComparison.Ordinal);
    }

    /// <summary>Rejects unsupported standalone CHECK syntax before publishing source.</summary>
    [Fact]
    public void StandaloneCheckConstraintRejectsOpaqueSqlBeforeEmittingSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = new AddCheckConstraintOperation
        {
            Name = "ck_users_reference",
            Table = "users",
            Sql = "reference LIKE 'USR-%'",
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        Assert.Empty(builder.ToString());
        Assert.Contains("ck_users_reference", exception.Message, StringComparison.Ordinal);
        Assert.Contains("trailing_token", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects provider constraint annotations that would be lost during capture.</summary>
    [Fact]
    public void StandaloneConstraintRejectsProviderAnnotationsBeforeEmittingSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = new AddForeignKeyOperation
        {
            Name = "fk_users_roles",
            Table = "users",
            Columns = ["role_id"],
            PrincipalTable = "roles",
            PrincipalColumns = ["id"],
        };
        operation["Provider:PhysicalOption"] = "value";

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        Assert.Empty(builder.ToString());
        Assert.Contains("Provider:PhysicalOption", exception.Message, StringComparison.Ordinal);
        Assert.Contains("lossy constraint contract", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects an implicit principal key rather than guessing its columns.</summary>
    [Fact]
    public void StandaloneForeignKeyRejectsImplicitPrincipalColumnsBeforeEmittingSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = new AddForeignKeyOperation
        {
            Name = "fk_users_roles",
            Table = "users",
            Columns = ["role_id"],
            PrincipalTable = "roles",
            PrincipalColumns = null,
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        Assert.Empty(builder.ToString());
        Assert.Contains("does not identify its principal columns", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects a standalone constraint with no declared columns.</summary>
    [Fact]
    public void StandaloneConstraintRejectsAnEmptyColumnContractBeforeEmittingSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = new AddUniqueConstraintOperation
        {
            Name = "uq_users_email",
            Table = "users",
            Columns = [],
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        Assert.Empty(builder.ToString());
        Assert.Contains("incomplete key-column contract", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects undefined foreign-key actions before publishing source.</summary>
    [Fact]
    public void StandaloneForeignKeyRejectsAnUndefinedReferentialActionBeforeEmittingSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = new AddForeignKeyOperation
        {
            Name = "fk_users_roles",
            Table = "users",
            Columns = ["role_id"],
            PrincipalTable = "roles",
            PrincipalColumns = ["id"],
            OnDelete = (ReferentialAction)int.MaxValue,
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        Assert.Empty(builder.ToString());
        Assert.Contains("undefined delete referential action", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects mismatched foreign-key column counts before publishing source.</summary>
    [Fact]
    public void StandaloneForeignKeyRejectsMismatchedColumnCountsBeforeEmittingSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var builder = new IndentedStringBuilder();
        var operation = new AddForeignKeyOperation
        {
            Name = "fk_users_roles",
            Table = "users",
            Columns = ["tenant_id", "role_id"],
            PrincipalTable = "roles",
            PrincipalColumns = ["id"],
        };

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            generator.Generate("migrationBuilder", [operation], builder));

        // Assert
        Assert.Empty(builder.ToString());
        Assert.Contains("different dependent and principal column counts", exception.Message, StringComparison.Ordinal);
    }
}
