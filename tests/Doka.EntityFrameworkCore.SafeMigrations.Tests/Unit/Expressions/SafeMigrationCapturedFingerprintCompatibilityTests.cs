namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Separates the captured-source upgrade from unchanged legacy fingerprint contracts.</summary>
public sealed class SafeMigrationCapturedFingerprintCompatibilityTests
{
    /// <summary>Contracts outside generated-source capture retain independently recorded legacy fingerprints.</summary>
    /// <param name="contract">The unaffected authoring path.</param>
    /// <param name="expected">The fingerprint recorded from the committed pre-capture implementation.</param>
    [Theory]
    [InlineData("numeric_default", "1563b72abb506330e8f4a09fbc1acb3646eb4f7af1760b7669d5f747bb15eb2b")]
    [InlineData("opaque_computed", "9ae2c22e721ccb68c74641635143a8acdceb819f12842d71ce5d377dd0491344")]
    [InlineData("opaque_check", "688db76cb2a1ee0f6147aa5d3e5d5082644408aa2cd8d5b199c10bac24388d2a")]
    [InlineData("raw_computed", "9cc40fba45696e4d539e7eec35558a2085b3cb3c9a68f7c1463300b4de5e38f6")]
    [InlineData("raw_check", "12a88f2b4b937b81545c4317192d3e094150d679dbbf7851bd6ba02e503feb14")]
    [InlineData("structured_computed", "d6c8d08da807c19201c8ed00e555568896672717d744e766021312e2977ecdf2")]
    [InlineData("structured_check", "1250fd6cfb7089cb7ac8473ac22c0c7e685d4aa432c756cb30fe59c7941779c8")]
    [InlineData("ordinary_column", "b59596e9a1eb7dd6ae8d587bea87df039ff91f2b10b809f5b22bad4603bd16e4")]
    [InlineData("provider_owned", "917b4d9ffbade971b2c3bd18cc7dd9afbc2d4d26242f76756e8338f02920b01a")]
    public void UncapturedContractsRetainLegacyFingerprint(string contract, string expected)
    {
        // Arrange
        var operations = CreateContract(contract);

        // Act
        var actual = SafeMigrationContractFingerprint.Create(operations);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Captured generated definitions require a new approval instead of reusing an old hash.</summary>
    /// <param name="contract">The parsed generated-source authoring path.</param>
    /// <param name="legacy">The fingerprint recorded before generated-source capture.</param>
    [Theory]
    [InlineData("captured_computed", "3a84e6837d4c26437d08d45492db1f6d4da7336a4d64e806c16018a810d74e01")]
    [InlineData("captured_check", "dbb80c391210d9142e921a9eaa919d42ceb6ac786788a7c2d39a885997210570")]
    public void CapturedGeneratedContractsDoNotReuseLegacyFingerprint(string contract, string legacy)
    {
        // Arrange
        var operations = CreateContract(contract);

        // Act
        var actual = SafeMigrationContractFingerprint.Create(operations);

        // Assert
        Assert.NotEqual(legacy, actual);
        Assert.Equal(actual, SafeMigrationContractFingerprint.Create(CreateContract(contract)));
    }

    /// <summary>CHECK identity now retains authored formatting even when the parsed structure agrees.</summary>
    /// <param name="sql">A differently spelled predicate with the same parsed structure.</param>
    [Theory]
    [InlineData("source_value  >  1")]
    [InlineData("(source_value > 1)")]
    public void CapturedCheckFormattingIsNotGeneralSourceEquivalence(string sql)
    {
        // Arrange
        var plain = CaptureCheck("source_value > 1");
        var changed = CaptureCheck(sql);

        // Act
        var equivalentTrees = SafeMigrationSqlExpressionContract.Equivalent(plain.Expression, changed.Expression);
        var equivalentDefinitions = SafeMigrationDefinitionEquivalence.CheckConstraint(plain, changed);

        // Assert
        Assert.True(equivalentTrees);
        Assert.False(equivalentDefinitions);
        Assert.NotEqual(Fingerprint(new EnsureCheckConstraintIntent(plain)),
            Fingerprint(new EnsureCheckConstraintIntent(changed)));
    }

    /// <summary>Computed source formatting was already exact before the parsed-source capture path.</summary>
    /// <param name="sql">A differently spelled computed expression.</param>
    [Theory]
    [InlineData("source_value  +  1")]
    [InlineData("(source_value + 1)")]
    public void ComputedFormattingDoesNotIntroduceANewRepairPermission(string sql)
    {
        // Arrange
        var legacyPlain = RawComputed("source_value + 1");
        var legacyChanged = RawComputed(sql);
        var capturedPlain = CaptureColumn(computedSql: "source_value + 1");
        var capturedChanged = CaptureColumn(computedSql: sql);

        // Act
        var legacyEquivalent = SafeMigrationDefinitionEquivalence.Column(legacyPlain, legacyChanged);
        var capturedEquivalent = SafeMigrationDefinitionEquivalence.Column(capturedPlain, capturedChanged);
        var legacyRepair = SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(legacyPlain, legacyChanged);
        var capturedRepair = SafeMigrationColumnRepairHelper.CanSafelyAlterColumn(capturedPlain, capturedChanged);

        // Assert
        Assert.False(legacyEquivalent);
        Assert.False(capturedEquivalent);
        Assert.False(legacyRepair);
        Assert.False(capturedRepair);
    }

    private static IReadOnlyList<MigrationOperation> CreateContract(string contract)
    {
        if (contract == "provider_owned")
        {
            return [new SqlOperation { Sql = "SELECT 1", }, new AddCheckConstraintOperation
            {
                Name = "ck_items",
                Table = "items",
                Sql = "source_value > 1",
            },];
        }

        SafeMigrationIntent intent = contract switch
        {
            "numeric_default" => new EnsureColumnIntent("items", CaptureColumn(defaultSql: "00001")),
            "opaque_computed" => new EnsureColumnIntent("items", CaptureColumn(computedSql: "source_value; SELECT 1")),
            "opaque_check" => new EnsureCheckConstraintIntent(CaptureCheck("source_value; SELECT 1")),
            "raw_computed" => new EnsureColumnIntent("items", RawComputed("source_value + 1")),
            "raw_check" => new EnsureCheckConstraintIntent(
                new ExpectedCheckConstraintDefinition("ck_items", "items", "source_value > 1")),
            "structured_computed" => new EnsureColumnIntent("items",
                new ExpectedColumnDefinition("computed_value", typeof(long), true,
                    computedExpression: SafeMigrationSql.Literal(1))),
            "structured_check" => new EnsureCheckConstraintIntent(
                ExpectedCheckConstraintDefinition.FromExpression("ck_items", "items", SafeMigrationSql.Literal(true))),
            "ordinary_column" => new EnsureColumnIntent("items", CaptureColumn()),
            "captured_computed" => new EnsureColumnIntent("items", CaptureColumn(computedSql: "source_value + 00001")),
            "captured_check" => new EnsureCheckConstraintIntent(CaptureCheck("source_value > 00001")),
            _ => throw new ArgumentOutOfRangeException(nameof(contract)),
        };

        return [new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent)];
    }

    private static ExpectedColumnDefinition RawComputed(string sql) =>
        new("computed_value", typeof(long), true, computedColumnSql: sql);

    private static ExpectedColumnDefinition CaptureColumn(string? computedSql = null, string? defaultSql = null) =>
        SafeMigrationExpectedDefinitionFactory.From(new AddColumnOperation
        {
            Name = "computed_value",
            Table = "items",
            ClrType = typeof(long),
            IsNullable = true,
            ComputedColumnSql = computedSql,
            DefaultValueSql = defaultSql,
        });

    private static ExpectedCheckConstraintDefinition CaptureCheck(string sql) =>
        SafeMigrationExpectedDefinitionFactory.From(new AddCheckConstraintOperation
        {
            Name = "ck_items",
            Table = "items",
            Sql = sql,
        });

    private static string Fingerprint(SafeMigrationIntent intent) =>
        SafeMigrationContractFingerprint.Create(
            [new SafeMigrationOperation(intent, SafeMigrationPolicy.ThrowIfDifferent)]);
}
