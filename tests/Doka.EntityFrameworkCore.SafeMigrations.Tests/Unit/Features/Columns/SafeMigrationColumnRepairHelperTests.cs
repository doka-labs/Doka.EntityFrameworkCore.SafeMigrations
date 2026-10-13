namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationColumnRepairHelperTests
{
    /// <summary>Preserves unknown/default contracts while refusing one structurally proven NULL replacement.</summary>
    public static TheoryData<SafeMigrationDefaultValue, bool, bool> MissingAddDefaultCases => new()
    {
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(null)), false, false },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(null)), true, true },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(42)), false, true },
        { SafeMigrationDefaultValue.Sql("NULL"), false, true },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Function("NEWID")), false, true },
        { SafeMigrationDefaultValue.Sql(SafeMigrationSql.Cast(SafeMigrationSql.Literal(null), "int")), false, true },
        { SafeMigrationDefaultValue.Literal(null), true, true },
        { SafeMigrationDefaultValue.None, false, false },
    };

    /// <summary>Only the exact required structured SQL NULL default loses the existing-row additive proof.</summary>
    /// <param name="defaultValue">The immutable authored default contract.</param>
    /// <param name="nullable">Whether NULL is valid for the target.</param>
    /// <param name="expected">Whether the existing provider-neutral additive proof remains valid.</param>
    [Theory]
    [MemberData(nameof(MissingAddDefaultCases))]
    public void MissingAddRejectsOnlyKnownRequiredStructuredSqlNull(
        SafeMigrationDefaultValue defaultValue,
        bool nullable,
        bool expected
    )
    {
        // Arrange
        var definition = new ExpectedColumnDefinition("value", typeof(int), nullable, "int",
            defaultValue: defaultValue);

        // Act
        var safe = SafeMigrationColumnRepairHelper.CanSafelyAddMissingColumn(definition);

        // Assert
        Assert.Equal(expected, safe);
    }

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
