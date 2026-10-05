namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer.Tests;

/// <summary>
/// Exercises provider service composition without requiring a live container.
/// </summary>
public sealed class SqlServerServiceCompositionTests
{
    private const string ConnectionString =
        "Server=127.0.0.1,1433;Database=composition;"
        + "User ID=sa;Password=unused;TrustServerCertificate=True";

    /// <summary>
    /// Installs the safe SQL generator and runner when SQL Server is configured.
    /// </summary>
    [Fact]
    public void Registration_InstallsSafeGeneratorAndRunner()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);

        // Act
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var runner = context.GetService<ISafeMigrationRunner>();

        // Assert
        Assert.IsType<SqlServerSafeMigrationsSqlGenerator>(generator);
        Assert.NotNull(runner);
    }

    /// <summary>
    /// Leaves ordinary EF Core operations on the provider's normal SQL path.
    /// </summary>
    [Fact]
    public void OrdinaryCreateTable_RemainsAnOrdinarySqlServerCommand()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateTable(
            "ordinary_table",
            table => new { Id = table.Column<int>(type: "int", nullable: false) });

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(builder.Operations, context.Model);

        // Assert
        var command = Assert.Single(commands);
        Assert.Contains("CREATE TABLE", command.CommandText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ordinary_table", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>
    /// Confirms that an unregistered context keeps EF Core's ordinary generator.
    /// </summary>
    [Fact]
    public void UnregisteredContext_DoesNotInstallSafeGenerator()
    {
        // Arrange
        using var context = new SafeMigrationDbContext(ConnectionString, registerSafeMigrations: false);

        // Act
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Assert
        Assert.IsNotType<SqlServerSafeMigrationsSqlGenerator>(generator);
    }

    /// <summary>
    /// Honors pre-cancellation before opening a database connection.
    /// </summary>
    [Fact]
    public async Task PreCancelledAnalysis_DoesNotAttemptDatabaseAccess()
    {
        // Arrange
        await using var context = new SafeMigrationDbContext(ConnectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropTableIfExists("absent_table");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // Act
        var exception = await Record.ExceptionAsync(() =>
            context.GetService<ISafeMigrationRunner>().AnalyzeAsync(
                context,
                builder.Operations,
                new SafeMigrationRunOptions("sqlserver-cancelled"),
                cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
    }
}
