namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlGeneratedDropIntegrationTests
{
    /// <summary>Current accepted operands, not initial source absence, qualify a finite generated addition.</summary>
    /// <param name="sourceType">The independently authored source's builtin storage type.</param>
    /// <param name="expression">The captured expression whose result type must remain certified.</param>
    /// <param name="expected">The required final classification.</param>
    [Theory]
    [InlineData("integer", "source + 1", SafeMigrationAction.Apply)]
    [InlineData("bigint", "source + 1", SafeMigrationAction.RejectUnsupported)]
    [InlineData("uuid", "source + 1", SafeMigrationAction.RejectUnsupported)]
    [InlineData("integer", "source", SafeMigrationAction.Apply)]
    public async Task SparseGeneratedAdditionUsesCurrentBuiltinOperandProof(
        string sourceType, string expression, SafeMigrationAction expected)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE sparse_generated (unrelated integer); "
            + "INSERT INTO sparse_generated VALUES(9);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (sourceType == "uuid")
        {
            builder.AddColumnIfNotExists<Guid>("source", "sparse_generated", type: sourceType, nullable: true);
        }
        else if (sourceType == "bigint")
        {
            builder.AddColumnIfNotExists<long>("source", "sparse_generated", type: sourceType, nullable: true);
        }
        else
        {
            builder.AddColumnIfNotExists<int>("source", "sparse_generated", type: sourceType, nullable: true);
        }

        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "derived", "sparse_generated", type: "integer", nullable: true,
            computedColumnSql: expression, stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-sparse-operand-binding"), CancellationToken.None);

        if (report.Status == SafeMigrationReportStatus.Ready)
        {
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.True(report.Assessments[1].Action == expected,
            report.Assessments[1].Action + ":" + report.Assessments[1].AnalysisCode);
        Assert.Equal(expected == SafeMigrationAction.Apply ? 3 : 1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='sparse_generated'::regclass "
            + "AND attnum>0 AND NOT attisdropped;"));
    }

    /// <summary>
    /// An opaque prefix cannot borrow typed source evidence for a newly captured arithmetic expression.
    /// </summary>
    [Fact]
    public async Task OpaqueSourcePrefixCannotQualifySparseGeneratedArithmetic()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE sparse_generated (unrelated integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("source", "sparse_generated", type: "integer", nullable: true);
        builder.Sql("ALTER TABLE sparse_generated ALTER COLUMN source TYPE bigint;");
        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<int>(
            "derived", "sparse_generated", type: "integer", nullable: true,
            computedColumnSql: "source+1", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-sparse-opaque-binding"), CancellationToken.None);

        // Assert
        Assert.NotEqual(SafeMigrationAction.Apply, report.Assessments[^1].Action);
        Assert.Equal(1, await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='sparse_generated'::regclass "
            + "AND attnum>0 AND NOT attisdropped;"));
    }

    /// <summary>
    /// Finite comparisons reuse builtin source identities without confusing them with Boolean target storage.
    /// </summary>
    /// <param name="wide">Whether the accepted source uses the wider builtin integer representation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SparseGeneratedComparisonQualifiesCurrentIntegerSource(bool wide)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE sparse_generated (unrelated integer);");
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (wide)
        {
            builder.AddColumnIfNotExists<long>("source", "sparse_generated", type: "bigint", nullable: true);
        }
        else
        {
            builder.AddColumnIfNotExists<int>("source", "sparse_generated", type: "integer", nullable: true);
        }

        builder.AddColumnIfNotExistsFromModel(operationBuilder => operationBuilder.AddColumn<bool>(
            "derived", "sparse_generated", type: "boolean", nullable: true,
            computedColumnSql: "source > 0", stored: true));

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-sparse-comparison-binding"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static assessment => Assert.Equal(SafeMigrationAction.Apply, assessment.Action));
    }
}
