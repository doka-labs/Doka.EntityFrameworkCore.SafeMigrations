namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

public sealed partial class PostgreSqlSafeMigrationIntegrationTests : PostgreSqlIntegrationTestBase
{
    public PostgreSqlSafeMigrationIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    private static string PostgreSqlMaximumIdentifier(
        string prefix
    )
    {
        var remainingBytes = 63 - Encoding.UTF8.GetByteCount(prefix);
        return prefix + new string('x', remainingBytes);
    }

    private static string PostgreSqlIdentifier(
        string value
    ) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private static SafeMigrationSqlExpression SqlColumn(
        string name
    ) => SafeMigrationSql.Identifier(name);

    private static SafeMigrationSqlExpression SqlBinary(
        SafeMigrationSqlExpression left,
        SafeMigrationSqlBinaryOperator @operator,
        SafeMigrationSqlExpression right
    ) => SafeMigrationSql.Binary(left, @operator, right);

    private static SafeMigrationSqlExpression SqlColumnAndColumn(
        string left,
        SafeMigrationSqlBinaryOperator @operator,
        string right
    ) => SqlBinary(SqlColumn(left), @operator, SqlColumn(right));

    private static SafeMigrationSqlExpression SqlColumnAndInt(
        string column,
        SafeMigrationSqlBinaryOperator @operator,
        int value
    ) => SqlBinary(SqlColumn(column), @operator, SafeMigrationSql.Literal(value));

    private static SafeMigrationSqlExpression SqlFunction(
        string name,
        string column
    ) => SafeMigrationSql.Function(name, SqlColumn(column));

    private sealed class UnmappedValue;

    private sealed class SchemaChangingDerivedContext(string connectionString)
        : SafeMigrationDbContext(connectionString)
    {
        protected override void OnModelCreating(
            ModelBuilder modelBuilder
        ) => modelBuilder
            .Entity<DerivedEntity>()
            .ToTable("instance_specific");
    }

    private sealed class DerivedEntity
    {
        public int Id { get; set; }
    }
}
