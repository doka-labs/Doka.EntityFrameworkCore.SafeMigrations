namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Protects complete target-model constraint projection and unavailable-contract rejection.</summary>
public sealed class SafeMigrationExpectedTableConstraintsTests
{
    /// <summary>Projects primary, unique, check, foreign-key, and index expectations from the target model.</summary>
    [Fact]
    public void TargetModelProjectsEveryConstraintFamily()
    {
        // Arrange
        using var context = new ConstraintContractDbContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        // Act
        var constraints = SafeMigrationExpectedTableConstraints.FromModel(
            model,
            "constraint_contract_rows",
            schema: null);

        // Assert
        Assert.NotNull(constraints);
        Assert.Equal("PK_constraint_contract_rows", constraints.PrimaryKey?.Name);

        var unique = Assert.Single(constraints.UniqueConstraints);
        Assert.Equal("AK_constraint_contract_rows_TenantId_Code", unique.Name);
        Assert.Equal(["TenantId", "Code"], unique.Columns);

        var check = Assert.Single(constraints.CheckConstraints);
        Assert.Equal("CK_constraint_contract_rows_TenantId", check.Name);
        Assert.Null(check.Sql);
        Assert.Equal("`TenantId` >= 0", check.CapturedSql);
        var predicate = Assert.IsType<SafeMigrationSqlBinaryExpression>(check.Expression);
        Assert.Equal(SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, predicate.Operator);
        Assert.Equal(
            "TenantId",
            Assert.Single(
                Assert.IsType<SafeMigrationSqlIdentifierExpression>(predicate.Left)
                    .Parts));

        Assert.Equal(0,
            Assert.IsType<int>(Assert.IsType<SafeMigrationSqlLiteralExpression>(predicate.Right)
                .Value));

        var foreignKey = Assert.Single(constraints.ForeignKeys);
        Assert.Equal("FK_constraint_contract_rows_constraint_contract_rows_ParentId", foreignKey.Name);
        Assert.Equal(["ParentId"], foreignKey.Columns);
        Assert.Equal("constraint_contract_rows", foreignKey.PrincipalTable);
        Assert.Equal(["Id"], foreignKey.PrincipalColumns);
        Assert.Equal(ReferentialAction.NoAction, foreignKey.OnUpdate);
        Assert.Equal(ReferentialAction.NoAction, foreignKey.OnDelete);
    }

    /// <summary>Does not invent table-constraint overrides for an owner outside the supplied target model.</summary>
    [Fact]
    public void TableOutsideTargetModelHasNoConstraintOverride()
    {
        // Arrange
        using var context = new ConstraintContractDbContext();
        var model = context.GetService<IDesignTimeModel>()
            .Model;

        // Act
        var constraints = SafeMigrationExpectedTableConstraints.FromModel(
            model,
            "external_table",
            schema: null);

        // Assert
        Assert.Null(constraints);
    }

    /// <summary>Retains every required constraint family when projecting the runtime model.</summary>
    [Fact]
    public void RuntimeModelProjectsEveryConstraintFamily()
    {
        // Arrange
        using var context = new ConstraintContractDbContext();

        // Act
        var constraints = SafeMigrationExpectedTableConstraints.FromModel(
            context.Model,
            "constraint_contract_rows",
            schema: null,
            [
                new ExpectedCheckConstraintDefinition(
                    "CK_constraint_contract_rows_TenantId",
                    "constraint_contract_rows",
                    "`TenantId` >= 0"),
            ]);

        // Assert
        Assert.NotNull(constraints);
        Assert.Equal("PK_constraint_contract_rows", constraints.PrimaryKey?.Name);
        Assert.Equal("AK_constraint_contract_rows_TenantId_Code",
            Assert.Single(constraints.UniqueConstraints)
                .Name);

        Assert.Equal("CK_constraint_contract_rows_TenantId",
            Assert.Single(constraints.CheckConstraints)
                .Name);

        Assert.Equal(
            "FK_constraint_contract_rows_constraint_contract_rows_ParentId",
            Assert.Single(constraints.ForeignKeys)
                .Name);
    }

    /// <summary>Rejects runtime model projection when operation-owned check definitions are unavailable.</summary>
    [Fact]
    public void RuntimeModelWithoutOperationOwnedCheckConstraintsFailsClosed()
    {
        // Arrange
        using var context = new ConstraintContractDbContext();

        // Act
        var exception = Record.Exception(() => SafeMigrationExpectedTableConstraints.FromModel(
            context.Model,
            "constraint_contract_rows",
            schema: null));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    private sealed class ConstraintContractDbContext : DbContext
    {
        protected override void OnConfiguring(
            DbContextOptionsBuilder optionsBuilder
        ) => optionsBuilder.UseMySql(
            "Server=127.0.0.1;Database=constraint_contract;User ID=root;Password=root;",
            MySqlServerVersion.Parse("11.8.8-mariadb"));

        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        )
        {
            modelBuilder.Entity<ConstraintContractRow>(entity =>
            {
                entity.ToTable(
                    "constraint_contract_rows",
                    table => table.HasCheckConstraint(
                        "CK_constraint_contract_rows_TenantId",
                        "`TenantId` >= 0"));

                entity.HasKey(value => value.Id);
                entity.HasAlternateKey(value => new
                {
                    value.TenantId,
                    value.Code,
                });

                entity
                    .HasOne<ConstraintContractRow>()
                    .WithMany()
                    .HasForeignKey(value => value.ParentId)
                    .OnDelete(DeleteBehavior.NoAction);
            });
        }
    }

    [UsedImplicitly(ImplicitUseTargetFlags.WithMembers)]
    private sealed class ConstraintContractRow
    {
        public int Id { get; set; }

        public int TenantId { get; set; }

        public int Code { get; set; }

        public int? ParentId { get; set; }
    }
}
