namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Preserves ordered charset-domain proofs while removing per-plan enumeration allocations.</summary>
public sealed class MySqlBackfillCharsetSelectionTests
{
    private const string EveryCharset = "ascii,latin1,utf8,utf8mb3,utf8mb4,ucs2,utf16,utf16le,utf32";
    private const string UnicodeCharsets = "utf8,utf8mb3,utf8mb4,ucs2,utf16,utf16le,utf32";

    /// <summary>Gets serializable engine, literal-domain and expected-charset cases.</summary>
    public static TheoryData<bool, string, string> CharsetCases
    {
        get
        {
            var data = new TheoryData<bool, string, string>();
            foreach (var mariaDb in new[] { false, true })
            {
                data.Add(mariaDb, "ascii", EveryCharset);
                data.Add(mariaDb, "latin1", UnicodeCharsets);
                data.Add(mariaDb, "bmp", UnicodeCharsets);
                data.Add(mariaDb, "supplementary", string.Empty);
                data.Add(mariaDb, "control", EveryCharset);
                data.Add(mariaDb, "too_long", string.Empty);
            }

            return data;
        }
    }

    /// <summary>Retains the old exact allowed-domain set and literal registration order on each profile.</summary>
    /// <param name="mariaDb">Whether the real MariaDB rather than MySQL profile is selected.</param>
    /// <param name="scenario">The independently selectable literal repertoire or length boundary.</param>
    /// <param name="expectedCharsets">The original conservative ordered charset set.</param>
    [Theory]
    [MemberData(nameof(CharsetCases))]
    public void LiteralBackfill_PreservesOrderedCharsetSelection(
        bool mariaDb,
        string scenario,
        string expectedCharsets
    )
    {
        // Arrange
        var literal = Literal(scenario);
        // WHY: Equal widths take the simple alter branch and cannot exercise charset proof construction.
        var source = new ExpectedColumnDefinition("value", typeof(string), true, "varchar(128)", maxLength: 128);
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(64)", maxLength: 64,
            defaultValue: SafeMigrationDefaultValue.Literal(literal));

        using var context = Context(mariaDb);
        var operation = new SafeMigrationOperation(new AlterColumnIntent("backfill_rows", target, source),
            SafeMigrationPolicy.RepairIfSafe);

        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        using var lease = capture.Begin([operation],
            MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes([operation]), includeTransitionEvidence: true);

        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        _ = generator.Generate([operation], context.Model);
        var plan = Assert.Single(lease.Complete());
        var probe = plan.NullabilityDataProbe;
        var invariant = probe is null ? plan.RepairPrecondition : probe.RepairInvariantExpression;
        var rendered = MySqlCatalogSqlTemplate.Render(invariant, plan.ParameterValues,
            value => RenderValue(context, value));

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.NotNull(probe);
        Assert.NotNull(plan.DataProbe);
        var expected = expectedCharsets.Length == 0 ? [] : expectedCharsets.Split(',');

        Assert.All(EveryCharset.Split(','), characterSet => Assert.Equal(expected.Contains(characterSet),
            MySqlSafeMigrationCatalogSqlBuilder.CanRepresentAlterColumnBackfill(source, target,
                "varchar(128)", "varchar(64)", characterSet, strictMode: true,
                supportsQuotedExpressionDefaults: true, supportsTextExpressionControlCharacters: true)));

        if (expected.Length == 0)
        {
            Assert.DoesNotContain("c.CHARACTER_SET_NAME IN (", rendered, StringComparison.Ordinal);
            Assert.Contains("AND (FALSE)", rendered, StringComparison.Ordinal);

            return;
        }

        var stringMapping = context.GetService<IRelationalTypeMappingSource>().FindMapping(typeof(string))
            ?? throw new InvalidOperationException("The native string mapping is absent.");

        var expectedList = string.Join(", ", expected.Select(stringMapping.GenerateSqlLiteral));

        Assert.Contains($"c.CHARACTER_SET_NAME IN ({expectedList})", rendered, StringComparison.Ordinal);
    }

    /// <summary>Cannot authorize a backfill with unknown charset identity or permissive conversion mode.</summary>
    /// <param name="characterSet">The known or unknown native character-set identity.</param>
    /// <param name="strict">Whether conversion errors are guaranteed to reject.</param>
    [Theory]
    [InlineData(null, true)]
    [InlineData("unknown_charset", true)]
    [InlineData("utf8mb4", false)]
    public void LiteralBackfill_RequiresKnownDomainAndStrictMode(
        string? characterSet,
        bool strict
    )
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(string), true, "varchar(64)");
        var target = new ExpectedColumnDefinition("value", typeof(string), false, "varchar(64)",
            defaultValue: SafeMigrationDefaultValue.Literal("value"));

        // Act
        var represented = MySqlSafeMigrationCatalogSqlBuilder.CanRepresentAlterColumnBackfill(source, target,
            "varchar(64)", "varchar(64)", characterSet, strict,
            supportsQuotedExpressionDefaults: true, supportsTextExpressionControlCharacters: true);

        // Assert
        Assert.False(represented);
    }

    /// <summary>Boolean backfill remains independent of charset enumeration and exact about old storage.</summary>
    /// <param name="sourceType">The declared physical source type.</param>
    /// <param name="expected">Whether its exact domain permits the boolean transition.</param>
    [Theory]
    [InlineData("bit(1)", true)]
    [InlineData("tinyint(1)", false)]
    public void BooleanBackfill_RetainsExactSourceDomain(
        string sourceType,
        bool expected
    )
    {
        // Arrange
        var source = new ExpectedColumnDefinition("value", typeof(bool), true, sourceType);
        var target = new ExpectedColumnDefinition("value", typeof(bool), false, "tinyint(1)",
            defaultValue: SafeMigrationDefaultValue.Literal(true));

        // Act
        var represented = MySqlSafeMigrationCatalogSqlBuilder.CanRepresentAlterColumnBackfill(source, target,
            sourceType, "tinyint(1)", characterSet: null, strictMode: true,
            supportsQuotedExpressionDefaults: true, supportsTextExpressionControlCharacters: true);

        // Assert
        Assert.Equal(expected, represented);
    }

    /// <summary>Creates literal values after xUnit has independently discovered each stable case identifier.</summary>
    /// <param name="scenario">The serialized repertoire or length-boundary case.</param>
    /// <returns>The exact source literal used by both the old domain oracle and generated SQL.</returns>
    private static string Literal(
        string scenario
    ) => scenario switch
    {
        "ascii" => "replacement",
        "latin1" => "caf\u00e9",
        "bmp" => "\u20ac",
        "supplementary" => "\U0001F600",
        "control" => "a\0b\nc\\d",
        "too_long" => new string('x', 65),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
    };

    /// <summary>Uses real pinned provider profiles without opening a connection.</summary>
    /// <param name="mariaDb">Whether MariaDB rather than MySQL is selected.</param>
    /// <returns>The offline context providing real native mappings and handler capture.</returns>
    private static SafeMigrationDbContext Context(
        bool mariaDb
    ) => new("Server=127.0.0.1;Port=1;Database=unused;User ID=unused;Allow User Variables=true",
        mariaDb ? MySqlServerVersion.MariaDb(new Version(11, 8, 8)) : MySqlServerVersion.MySql(new Version(8, 4, 11)));

    /// <summary>Renders exact typed parameter values without reordering their captured ordinals.</summary>
    /// <param name="context">The native mapping owner.</param>
    /// <param name="value">The captured table, column, charset or typed default value.</param>
    /// <returns>The native SQL literal for that exact captured slot.</returns>
    private static string RenderValue(
        DbContext context,
        MySqlCatalogParameterValue value
    )
    {
        var mappings = context.GetService<IRelationalTypeMappingSource>();
        var mapping = value.StoreType is { } storeType
            ? MySqlCatalogTypeMapping.Resolve(mappings, value.Value, storeType)
            : mappings.FindMapping(typeof(string))
                ?? throw new InvalidOperationException("The native identifier mapping is absent.");

        return mapping.GenerateSqlLiteral(value.Value);
    }
}
