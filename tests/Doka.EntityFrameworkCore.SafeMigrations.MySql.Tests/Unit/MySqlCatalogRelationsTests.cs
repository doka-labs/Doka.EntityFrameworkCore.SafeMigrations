namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Checks catalog binding cannot change user expressions or SQL token boundaries.</summary>
public sealed class MySqlCatalogRelationsTests
{
    /// <summary>Checks every supported relation is redirected only in a table-source position.</summary>
    /// <param name="relation">The catalog relation to redirect.</param>
    [Theory]
    [InlineData("INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS")]
    [InlineData("INFORMATION_SCHEMA.TABLE_CONSTRAINTS")]
    [InlineData("INFORMATION_SCHEMA.CHECK_CONSTRAINTS")]
    [InlineData("INFORMATION_SCHEMA.KEY_COLUMN_USAGE")]
    [InlineData("INFORMATION_SCHEMA.CHARACTER_SETS")]
    [InlineData("INFORMATION_SCHEMA.STATISTICS")]
    [InlineData("INFORMATION_SCHEMA.COLLATIONS")]
    [InlineData("INFORMATION_SCHEMA.COLUMNS")]
    [InlineData("INFORMATION_SCHEMA.TABLES")]
    public void RedirectsEveryCatalogTableSource(
        string relation
    )
    {
        // Arrange
        var sql = $"SELECT 1 FROM {relation} a LEFT JOIN {relation} b ON a.k = b.k";

        // Act
        var actual = MySqlCatalogRelations.Resolve(sql, static _ => "`snapshot`");

        // Assert
        Assert.Equal("SELECT 1 FROM `snapshot` a LEFT JOIN `snapshot` b ON a.k = b.k", actual);
    }

    /// <summary>Checks lookalike catalog names in expressions, literals, and identifiers stay unchanged.</summary>
    /// <param name="sql">The statement whose text is not a bindable catalog table source.</param>
    [Theory]
    [InlineData("SELECT INFORMATION_SCHEMA.COLUMNS FROM `items`")]
    [InlineData("SELECT EXTRACT(YEAR FROM INFORMATION_SCHEMA.COLUMNS) FROM `items`")]
    [InlineData("SELECT TRIM('x' FROM INFORMATION_SCHEMA.COLUMNS) FROM `items`")]
    [InlineData("SELECT 1 FROM `items` WHERE `Name` = 'INFORMATION_SCHEMA.COLUMNS'")]
    [InlineData("SELECT 'FROM INFORMATION_SCHEMA.COLUMNS'")]
    [InlineData("SELECT 'JOIN INFORMATION_SCHEMA.COLUMNS'")]
    [InlineData("SELECT 'a'' FROM INFORMATION_SCHEMA.COLUMNS'")]
    [InlineData("SELECT \"a\"\" FROM INFORMATION_SCHEMA.COLUMNS\"")]
    [InlineData("SELECT `a`` FROM INFORMATION_SCHEMA.COLUMNS`")]
    [InlineData("SELECT 1 FROM `INFORMATION_SCHEMA.COLUMNS`")]
    [InlineData("SELECT 1 FROM \"INFORMATION_SCHEMA.COLUMNS\"")]
    [InlineData("SELECT 1 FROM app.INFORMATION_SCHEMA.COLUMNS")]
    [InlineData("SELECT 1 FROM .INFORMATION_SCHEMA.COLUMNS")]
    [InlineData("SELECT 1 FROM XINFORMATION_SCHEMA.COLUMNS")]
    [InlineData("SELECT 1 FROM \u00a0INFORMATION_SCHEMA.COLUMNS")]
    [InlineData("SELECT 1 FROM \u20acINFORMATION_SCHEMA.COLUMNS")]
    [InlineData("SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS_EXTRA")]
    [InlineData("SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS$other")]
    [InlineData("SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS.other")]
    [InlineData("SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS\u20ac")]
    [InlineData("SELECT 1 FROM INFORMATION_SCHEMA.UNKNOWN_VIEW")]
    [InlineData("SELECT 1 -- FROM INFORMATION_SCHEMA.COLUMNS")]
    [InlineData("SELECT 1 # FROM INFORMATION_SCHEMA.COLUMNS")]
    [InlineData("SELECT 1 /* JOIN INFORMATION_SCHEMA.COLUMNS */")]
    public void PreservesNonRelationText(
        string sql
    )
    {
        // Arrange
        var resolutions = 0;

        // Act
        var actual = MySqlCatalogRelations.Resolve(
            sql,
            _ =>
            {
                resolutions++;

                return "`snapshot`";
            });

        // Assert
        Assert.Equal(sql, actual);
        Assert.Equal(0, resolutions);
    }

    /// <summary>Checks ordinary comments and quoted tokens do not hide a later table source.</summary>
    /// <param name="prefix">The complete token or comment before a bindable source.</param>
    [Theory]
    [InlineData("SELECT 'a'' FROM INFORMATION_SCHEMA.COLUMNS' ")]
    [InlineData("SELECT \"a\"\" FROM INFORMATION_SCHEMA.COLUMNS\" ")]
    [InlineData("SELECT `a`` FROM INFORMATION_SCHEMA.COLUMNS` ")]
    [InlineData("SELECT 1 /* FROM INFORMATION_SCHEMA.COLUMNS */ ")]
    [InlineData("SELECT 1 -- FROM INFORMATION_SCHEMA.COLUMNS\n")]
    [InlineData("SELECT 1 --\tFROM INFORMATION_SCHEMA.COLUMNS\r\n")]
    [InlineData("SELECT 1 # FROM INFORMATION_SCHEMA.COLUMNS\n")]
    [InlineData("SELECT 1--2 ")]
    public void ResumesAfterUnambiguousTriviaAndQuotedTokens(
        string prefix
    )
    {
        // Arrange
        var sql = prefix + "FROM INFORMATION_SCHEMA.COLUMNS c";

        // Act
        var actual = MySqlCatalogRelations.Resolve(sql, static _ => "`snapshot`");

        // Assert
        Assert.Equal(prefix + "FROM `snapshot` c", actual);
    }

    /// <summary>Checks comments may separate the table-source keyword and relation.</summary>
    /// <param name="trivia">The whitespace or comment between tokens.</param>
    [Theory]
    [InlineData(" ")]
    [InlineData("\n\t")]
    [InlineData("/* ordinary comment */")]
    [InlineData("-- comment\n")]
    [InlineData("# comment\n")]
    public void PreservesTableSourceContextAcrossTrivia(
        string trivia
    )
    {
        // Arrange
        var sql = "SELECT 1 FROM" + trivia + "INFORMATION_SCHEMA.COLUMNS c";

        // Act
        var actual = MySqlCatalogRelations.Resolve(sql, static _ => "`snapshot`");

        // Assert
        Assert.Equal("SELECT 1 FROM" + trivia + "`snapshot` c", actual);
    }

    /// <summary>Checks server-dependent lexical syntax leaves the remaining expression live.</summary>
    /// <param name="ambiguous">A fragment whose interpretation needs server version or SQL mode.</param>
    [Theory]
    [InlineData("/*! FROM INFORMATION_SCHEMA.COLUMNS */")]
    [InlineData("/*!100000 FROM INFORMATION_SCHEMA.COLUMNS */")]
    [InlineData("/*M! FROM INFORMATION_SCHEMA.COLUMNS */")]
    [InlineData("/*M!100000 FROM INFORMATION_SCHEMA.COLUMNS */")]
    [InlineData("/* outer /* inner */ FROM INFORMATION_SCHEMA.COLUMNS */")]
    [InlineData("'a\\' FROM INFORMATION_SCHEMA.COLUMNS'")]
    [InlineData("\"a\\\" FROM INFORMATION_SCHEMA.COLUMNS\"")]
    public void AmbiguousSyntaxLeavesTheRemainderVerbatim(
        string ambiguous
    )
    {
        // Arrange
        var suffix = " WHERE " + ambiguous + " AND EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES)";
        var sql = "SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS" + suffix;

        // Act
        var actual = MySqlCatalogRelations.Resolve(sql, static _ => "`snapshot`");

        // Assert
        Assert.Equal("SELECT 1 FROM `snapshot`" + suffix, actual);
    }

    /// <summary>Checks parameter rendering neither rewrites values nor resets an open SQL token.</summary>
    /// <param name="prefix">Text preceding a parameter marker.</param>
    /// <param name="suffix">Text completing that token and introducing a real source.</param>
    /// <param name="prepared">Whether the value has already been rendered.</param>
    [Theory]
    [InlineData("SELECT '", " FROM INFORMATION_SCHEMA.COLUMNS' FROM INFORMATION_SCHEMA.TABLES", false)]
    [InlineData("SELECT '", " FROM INFORMATION_SCHEMA.COLUMNS' FROM INFORMATION_SCHEMA.TABLES", true)]
    [InlineData("SELECT \"", " FROM INFORMATION_SCHEMA.COLUMNS\" FROM INFORMATION_SCHEMA.TABLES", false)]
    [InlineData("SELECT \"", " FROM INFORMATION_SCHEMA.COLUMNS\" FROM INFORMATION_SCHEMA.TABLES", true)]
    [InlineData("SELECT `", " FROM INFORMATION_SCHEMA.COLUMNS` FROM INFORMATION_SCHEMA.TABLES", false)]
    [InlineData("SELECT `", " FROM INFORMATION_SCHEMA.COLUMNS` FROM INFORMATION_SCHEMA.TABLES", true)]
    [InlineData("SELECT 1 /*", " FROM INFORMATION_SCHEMA.COLUMNS */ FROM INFORMATION_SCHEMA.TABLES", false)]
    [InlineData("SELECT 1 /*", " FROM INFORMATION_SCHEMA.COLUMNS */ FROM INFORMATION_SCHEMA.TABLES", true)]
    [InlineData("SELECT 1 -- ", " FROM INFORMATION_SCHEMA.COLUMNS\nFROM INFORMATION_SCHEMA.TABLES", false)]
    [InlineData("SELECT 1 -- ", " FROM INFORMATION_SCHEMA.COLUMNS\nFROM INFORMATION_SCHEMA.TABLES", true)]
    [InlineData("SELECT 1 #", " FROM INFORMATION_SCHEMA.COLUMNS\nFROM INFORMATION_SCHEMA.TABLES", false)]
    [InlineData("SELECT 1 #", " FROM INFORMATION_SCHEMA.COLUMNS\nFROM INFORMATION_SCHEMA.TABLES", true)]
    public void RetainsLexicalStateAcrossValueMarkers(
        string prefix,
        string suffix,
        bool prepared
    )
    {
        // Arrange
        const string value = "FROM INFORMATION_SCHEMA.COLUMNS";
        var template = prefix + MySqlCatalogSqlTemplate.Marker(0) + suffix;
        var expected = prefix + value + suffix.Replace("INFORMATION_SCHEMA.TABLES", "`snapshot`", StringComparison.Ordinal);

        // Act
        var actual = prepared
            ? MySqlCatalogSqlTemplate.RenderPrepared(template, [value], static _ => "`snapshot`")
            : MySqlCatalogSqlTemplate.Render(
                template,
                [new MySqlCatalogParameterValue(value, StoreType: null)],
                static parameter => (string)parameter.Value!,
                static _ => "`snapshot`");

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Checks inline CHECK text and rendered literal values are preserved by both render paths.</summary>
    /// <param name="prepared">Whether the value has already been rendered.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesInlineCheckAndParameterLiterals(
        bool prepared
    )
    {
        // Arrange
        const string value = "'FROM INFORMATION_SCHEMA.TABLES'";
        var template = "SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE `Name` = 'INFORMATION_SCHEMA.COLUMNS' AND x = "
            + MySqlCatalogSqlTemplate.Marker(0);

        // Act
        var actual = prepared
            ? MySqlCatalogSqlTemplate.RenderPrepared(template, [value], static _ => "`snapshot`")
            : MySqlCatalogSqlTemplate.Render(
                template,
                [new MySqlCatalogParameterValue(value, StoreType: null)],
                static parameter => (string)parameter.Value!,
                static _ => "`snapshot`");

        // Assert
        Assert.Equal(
            "SELECT 1 FROM `snapshot` WHERE `Name` = 'INFORMATION_SCHEMA.COLUMNS' AND x = " + value,
            actual);
    }

    /// <summary>Checks nested SELECT sources remain bindable without rewriting function arguments.</summary>
    [Fact]
    public void TracksNestedQueryAndFunctionScopes()
    {
        // Arrange
        const string sql = "SELECT (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS), "
            + "EXTRACT(YEAR FROM INFORMATION_SCHEMA.COLUMNS) FROM INFORMATION_SCHEMA.TABLES";

        // Act
        var actual = MySqlCatalogRelations.Resolve(sql, static _ => "`snapshot`");

        // Assert
        Assert.Equal(
            "SELECT (SELECT 1 FROM `snapshot`), "
            + "EXTRACT(YEAR FROM INFORMATION_SCHEMA.COLUMNS) FROM `snapshot`",
            actual);
    }

    /// <summary>Checks an adjacent value marker cannot become part of a redirected identifier.</summary>
    /// <param name="prepared">Whether the value has already been rendered.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesCatalogIdentifierCompletedByAValue(
        bool prepared
    )
    {
        // Arrange
        const string sql = "SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS";
        var template = sql + MySqlCatalogSqlTemplate.Marker(0);

        // Act
        var actual = prepared
            ? MySqlCatalogSqlTemplate.RenderPrepared(template, ["_SUFFIX"], static _ => "`snapshot`")
            : MySqlCatalogSqlTemplate.Render(
                template,
                [new MySqlCatalogParameterValue("_SUFFIX", StoreType: null)],
                static parameter => (string)parameter.Value!,
                static _ => "`snapshot`");

        // Assert
        Assert.Equal(sql + "_SUFFIX", actual);
    }

    /// <summary>Checks unsupported query nesting conservatively retains the original live source.</summary>
    [Fact]
    public void ExcessiveQueryNestingRemainsVerbatim()
    {
        // Arrange
        var sql = new string('(', 64) + "SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS" + new string(')', 64);

        // Act
        var actual = MySqlCatalogRelations.Resolve(sql, static _ => "`snapshot`");

        // Assert
        Assert.Equal(sql, actual);
    }

    /// <summary>Checks the unbound fast path returns the original statement without allocating a copy.</summary>
    [Fact]
    public void UnboundStatementRetainsReferenceIdentity()
    {
        // Arrange
        const string sql = "SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS WHERE x = 'INFORMATION_SCHEMA.TABLES'";

        // Act
        var actual = MySqlCatalogSqlTemplate.RenderPrepared(sql, [], relations: null);

        // Assert
        Assert.Same(sql, actual);
    }
}
