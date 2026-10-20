namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlGeneratedDropIntegrationTests
{
    /// <summary>
    /// An exact-owner certificate cannot authorize native recursive removal in another inherited owner.
    /// </summary>
    /// <param name="missing">Whether the requested name is genuinely absent from the owner and its child.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InheritedGeneratedDependencyCannotBorrowExactOwnerAbsence(bool missing)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE generated_parent(source integer, unrelated integer); "
            + "CREATE TABLE generated_child(derived integer GENERATED ALWAYS AS(source+1) STORED) "
            + "INHERITS(generated_parent); INSERT INTO generated_child(source,unrelated) VALUES(2,9);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropColumnIfExists(missing ? "missing" : "source", "generated_parent");

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("generated-drop-inherited-owner"), CancellationToken.None);

        var failure = await Record.ExceptionAsync(() => ExecuteOperationsAsync(context, builder.Operations));
        var childColumns = await ScalarIntAsync(connectionString,
            "SELECT COUNT(*) FROM pg_catalog.pg_attribute WHERE attrelid='generated_child'::regclass "
            + "AND attnum>0 AND NOT attisdropped;");

        // Assert
        _output.WriteLine("preflight={0}; native-failure={1}; surviving-child-columns={2}", report.Status,
            (failure as PostgresException)?.SqlState ?? "none", childColumns);
        Assert.Equal(missing ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectUnsupported,
            Assert.Single(report.Assessments).Action);
        if (missing)
        {
            Assert.Null(failure);
        }
        else
        {
            Assert.Equal("generated_column_drop_unproven", report.Assessments[0].AnalysisCode);
            Assert.Equal("P1002", Assert.IsType<PostgresException>(failure).SqlState);
        }

        Assert.Equal(3, childColumns);
        Assert.Equal("2:9:3", await ScalarStringAsync(connectionString,
            "SELECT source::text || ':' || unrelated::text || ':' || derived::text FROM generated_child;"));
    }
}
