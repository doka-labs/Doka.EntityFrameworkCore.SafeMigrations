namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationColumnRepairHelperTests
{
    public static TheoryData<SafeMigrationDefaultValue, bool> NonNullDefaultCases => new()
    {
        { SafeMigrationDefaultValue.None, false },
        { SafeMigrationDefaultValue.Literal(null), false },
        { SafeMigrationDefaultValue.Literal(42), true },
        { SafeMigrationDefaultValue.Sql("CURRENT_TIMESTAMP(6)"), false },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(null)), false },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(42)), true },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Identifier("value")), false },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.IsNull(SafeMigrationSql.Identifier("value"))), true },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Current(SafeMigrationSqlCurrentValue.Timestamp)), true },
        {
            SafeMigrationDefaultValue.Sql(SafeMigrationSql.Binary(
                SafeMigrationSql.Literal(1),
                SafeMigrationSqlBinaryOperator.Divide,
                SafeMigrationSql.Literal(0))),
            false
        },
        {
            SafeMigrationDefaultValue.Sql(SafeMigrationSql.Cast(SafeMigrationSql.Literal("invalid"), "signed")),
            false
        },
        {
            SafeMigrationDefaultValue.Sql(SafeMigrationSql.Function(
                "COALESCE",
                SafeMigrationSql.Identifier("value"),
                SafeMigrationSql.Literal("fallback"))),
            true
        },
        {
            SafeMigrationDefaultValue.Sql(SafeMigrationSql.Function(
                "COALESCE",
                SafeMigrationSql.Identifier("value"),
                SafeMigrationSql.Literal(null))),
            false
        },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Opaque("CURRENT_TIMESTAMP(6)")), false },
    };

    [Theory]
    [MemberData(nameof(NonNullDefaultCases))]
    public void HasProvablyNonNullDefault_RequiresStructuralProof(
        SafeMigrationDefaultValue defaultValue,
        bool expected
    )
    {
        // Arrange
        var candidate = defaultValue;

        // Act
        var actual = SafeMigrationColumnRepairHelper.HasProvablyNonNullDefault(candidate);

        // Assert
        Assert.Equal(expected, actual);
    }
}
