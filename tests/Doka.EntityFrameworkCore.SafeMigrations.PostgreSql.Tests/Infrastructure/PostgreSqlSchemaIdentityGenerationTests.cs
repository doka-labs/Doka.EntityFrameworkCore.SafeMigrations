namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Pins connection-free generation to runtime-certified PostgreSQL schema identity.</summary>
public sealed class PostgreSqlSchemaIdentityGenerationTests
{
    /// <summary>A single authored qualifier cannot establish the connection's current schema.</summary>
    /// <param name="qualifiedTable">Whether the table or its standalone constraint carries the qualifier.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedQualifierTableContract_IsConditionalOnRuntimeIdentity(bool qualifiedTable)
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=localhost;Database=schema_generation;Username=unused;Password=unused");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), false, "integer")],
                qualifiedTable ? "candidate_schema" : null),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.EnsureUniqueConstraint(new ExpectedUniqueConstraintDefinition(
                "uq_identity_target", "identity_target", ["id"], qualifiedTable ? null : "candidate_schema"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Assert
        var sql = commands[0].CommandText;

        Assert.Contains("current_schema() = 'candidate_schema'", sql, StringComparison.Ordinal);
        Assert.Contains("pg_catalog.to_regclass", sql, StringComparison.Ordinal);
        Assert.Contains("schema_search_path_mismatch", sql, StringComparison.Ordinal);
    }

    /// <summary>Native name resolution is quoted without folding a case-sensitive PostgreSQL identifier.</summary>
    [Fact]
    public void UnqualifiedIdentifierGuard_PreservesQuotedIdentifierCase()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=localhost;Database=schema_generation;Username=unused;Password=unused");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("MixedCase");

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>()
            .Generate(builder.Operations, context.Model));

        // Assert
        Assert.Contains("pg_catalog.to_regclass('\"MixedCase\"')", command.CommandText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("pg_catalog.to_regclass('MixedCase')", command.CommandText,
            StringComparison.Ordinal);
    }

    /// <summary>Opaque SQL cannot lend later schema-dependent transitions to an earlier table contract.</summary>
    [Fact]
    public void OpaqueSqlBarrier_SeparatesTableTransitionAliases()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=localhost;Database=schema_generation;Username=unused;Password=unused");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureTable(new ExpectedTableDefinition("identity_target",
                [new ExpectedColumnDefinition("id", typeof(int), false, "integer")]),
            SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        builder.Sql("SET search_path TO candidate_schema;");
        builder.EnsureUniqueConstraint(new ExpectedUniqueConstraintDefinition(
                "uq_identity_target", "identity_target", ["id"], "candidate_schema"),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Assert
        Assert.DoesNotContain("current_schema() = 'candidate_schema'", commands[0].CommandText,
            StringComparison.Ordinal);
        Assert.DoesNotContain("uq_identity_target", commands[0].CommandText, StringComparison.Ordinal);
    }

    /// <summary>A cross-qualifier index dependency is rejected only if the qualifier is actually current.</summary>
    [Fact]
    public void InvalidCurrentSchemaIndexTransition_IsGuardedWithoutRejectingForeignSchemaGeneration()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=localhost;Database=schema_generation;Username=unused;Password=unused");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_identity_target", "identity_target", ["id"], schema: "candidate_schema");
        builder.DropColumnIfExists("id", "identity_target");

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Assert
        Assert.Equal(2, commands.Count);
        Assert.All(commands, command => Assert.Contains(
            "current_schema() IS DISTINCT FROM 'candidate_schema'", command.CommandText, StringComparison.Ordinal));
    }

    /// <summary>A matching index drop discharges the dependency before the aliased column drop.</summary>
    [Fact]
    public void AcceptedCurrentSchemaIndexDrop_DischargesTheAliasedColumnDependency()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(
            "Host=localhost;Database=schema_generation;Username=unused;Password=unused");

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_identity_target", "identity_target", ["id"], schema: "candidate_schema");
        builder.DropIndexIfExists("ix_identity_target", "identity_target");
        builder.DropColumnIfExists("id", "identity_target");

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Assert
        Assert.Equal(3, commands.Count);
        Assert.All(commands, command => Assert.DoesNotContain(
            "current_schema() IS DISTINCT FROM 'candidate_schema'", command.CommandText, StringComparison.Ordinal));
    }
}
