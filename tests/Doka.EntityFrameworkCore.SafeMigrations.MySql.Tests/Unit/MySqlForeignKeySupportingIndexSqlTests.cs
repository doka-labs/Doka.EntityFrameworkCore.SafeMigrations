namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies catalog-only child-index collision qualification without restricting native parent keys.</summary>
public sealed class MySqlForeignKeySupportingIndexSqlTests
{
    /// <summary>An occupied automatic-index name is rejected only after exact and semantic FK matching.</summary>
    /// <param name="mariaDb">Whether MariaDB or MySQL catalog semantics apply.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeCollisionGuardPreservesMatchingPrecedence(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(
            new ExpectedForeignKeyDefinition("fk_owner", "child_items", ["owner"], "parent_items", ["id"])),
            SafeMigrationPolicy.ThrowIfDifferent);

        using var lease = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plans = lease.Complete();

        // Assert
        var plan = Assert.Single(plans);
        var state = plan.StateExpression;
        var semanticMatching = state.LastIndexOf("THEN 'matching'", StringComparison.Ordinal);
        var collision = state.IndexOf("automatic_index_name.INDEX_NAME", StringComparison.Ordinal);
        var rowProbe = state.IndexOf("LEFT JOIN `parent_items`", StringComparison.Ordinal);

        Assert.True(semanticMatching >= 0);
        Assert.True(collision > semanticMatching);
        Assert.True(rowProbe > collision);
        Assert.Contains("AND NOT EXISTS (SELECT supporting_index.INDEX_NAME", state, StringComparison.Ordinal);
        Assert.Contains("supporting_index.INDEX_TYPE = 'BTREE'", state, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP INDEX", state, StringComparison.Ordinal);
        Assert.DoesNotContain("supporting_index.NON_UNIQUE = 0", state, StringComparison.Ordinal);
    }

    /// <summary>Qualifies automatic-index suppression with MySQL ASC prefixes or MariaDB's unrestricted sort.</summary>
    /// <param name="mariaDb">Whether native MariaDB direction independence applies.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RuntimeCollisionGuardUsesNativePrefixDirection(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "fk_owner", "child_items", ["first_owner", "second_owner"], "parent_items", ["first_id", "second_id"])),
            SafeMigrationPolicy.ThrowIfDifferent);

        using var lease = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plans = lease.Complete();

        // Assert
        var plan = Assert.Single(plans);

        Assert.Equal(!mariaDb, plan.StateExpression.Contains("supporting_index.COLLATION = 'A'",
            StringComparison.Ordinal));
        Assert.DoesNotContain("SUM(CASE WHEN supporting_index.COLLATION = 'D'", plan.StateExpression,
            StringComparison.Ordinal);
    }

    /// <summary>Catalog facts cannot borrow case-distinct physical owners through metadata collation.</summary>
    [Fact]
    public void SupportingIndexFactsBindExactPhysicalOwners()
    {
        // Arrange
        const string table = "@bound_child";
        const string name = "@bound_fk";

        // Act
        var coverage = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyChildCoveringIndexQuery(
            table, ["@column"], isMariaDb: false);
        var occupation = MySqlSafeMigrationCatalogSqlBuilder.IsSupportingIndexNameOccupied(table, name);

        // Assert
        Assert.Contains("CAST(supporting_index.TABLE_NAME AS BINARY) = CAST(@bound_child AS BINARY)", coverage,
            StringComparison.Ordinal);
        Assert.Contains("CAST(supporting_index.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", coverage,
            StringComparison.Ordinal);
        Assert.Contains("CAST(automatic_index_name.TABLE_NAME AS BINARY) = CAST(@bound_child AS BINARY)", occupation,
            StringComparison.Ordinal);
        Assert.Contains("CAST(automatic_index_name.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", occupation,
            StringComparison.Ordinal);
    }

    /// <summary>Native columns and index aliases fold case without adopting metadata accent equivalence.</summary>
    /// <param name="mariaDb">Whether MariaDB's accent-insensitive metadata weights apply.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SupportingIndexIdentifiersPreserveNativeCaseWithoutAccentFolding(bool mariaDb)
    {
        // Arrange
        const string table = "@child";
        const string name = "@index";
        string[] columns = ["@column"];

        // Act
        var coverage = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyChildCoveringIndexQuery(
            table, columns, mariaDb);
        var occupation = MySqlSafeMigrationCatalogSqlBuilder.IsSupportingIndexNameOccupied(table, name);

        // Assert
        Assert.Contains("CAST(LOWER(supporting_index.COLUMN_NAME) AS BINARY) = CAST(LOWER(@column) AS BINARY)",
            coverage, StringComparison.Ordinal);
        Assert.Contains("GROUP BY supporting_index.INDEX_NAME, CAST(supporting_index.INDEX_NAME AS BINARY)",
            coverage, StringComparison.Ordinal);
        Assert.Contains("CAST(LOWER(automatic_index_name.INDEX_NAME) AS BINARY) = CAST(LOWER(@index) AS BINARY)",
            occupation, StringComparison.Ordinal);
        Assert.DoesNotContain(" COLLATE ", coverage, StringComparison.Ordinal);
        Assert.DoesNotContain(" COLLATE ", occupation, StringComparison.Ordinal);
    }

    /// <summary>Missing native identifier operands cannot broaden a bounded catalog comparison.</summary>
    /// <param name="shape">The absent catalog or expected identifier-expression token.</param>
    [Theory]
    [InlineData("null_catalog")]
    [InlineData("empty_catalog")]
    [InlineData("null_expected")]
    [InlineData("empty_expected")]
    public void NativeIdentifierMatcherRejectsIncompleteOperands(string shape)
    {
        // Arrange
        var catalog = shape switch
        {
            "null_catalog" => null,
            "empty_catalog" => string.Empty,
            _ => "s.COLUMN_NAME",
        };

        var expected = shape switch
        {
            "null_expected" => null,
            "empty_expected" => string.Empty,
            _ => "@column",
        };

        // Act
        var exception = Record.Exception(() =>
            MySqlSafeMigrationCatalogSqlBuilder.BuildNativeCaseInsensitiveIdentifierMatches(catalog!, expected!));

        // Assert
        Assert.IsAssignableFrom<ArgumentException>(exception);
    }

    /// <summary>Canonical native array-cast wrappers qualify an integrity risk without application-row reads.</summary>
    [Fact]
    public void MultiValuedRiskUsesCanonicalNativeWrapperAndCompletePrefix()
    {
        // Arrange
        string[] columns = ["@first", "@second"];

        // Act
        var sql = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyMultiValuedIndexRisk(
            "@child", columns, isMariaDb: false);

        // Assert
        Assert.StartsWith("EXISTS (SELECT supporting_index.INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS ", sql,
            StringComparison.Ordinal);
        Assert.Contains("CAST(supporting_index.TABLE_NAME AS BINARY) = CAST(@child AS BINARY)", sql,
            StringComparison.Ordinal);
        Assert.Contains("supporting_index.SEQ_IN_INDEX = 1 AND supporting_index.COLUMN_NAME = @first ", sql,
            StringComparison.Ordinal);
        Assert.Contains("supporting_index.SEQ_IN_INDEX = 2 AND supporting_index.COLUMN_NAME = @second ", sql,
            StringComparison.Ordinal);
        Assert.Contains("supporting_index.SUB_PART IS NULL", sql, StringComparison.Ordinal);
        Assert.Contains("supporting_index.EXPRESSION IS NOT NULL", sql, StringComparison.Ordinal);
        Assert.Contains("CAST(LEFT(supporting_index.EXPRESSION, 5) AS BINARY) = CAST('cast(' AS BINARY)", sql,
            StringComparison.Ordinal);
        Assert.Contains("CAST(RIGHT(supporting_index.EXPRESSION, 7) AS BINARY) = CAST(' array)' AS BINARY)", sql,
            StringComparison.Ordinal);
        Assert.EndsWith("THEN 1 ELSE 0 END) > 0)", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("COLLATION =", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("automatic_index_name", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REGEXP", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(" LIKE ", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM `", sql, StringComparison.Ordinal);
    }

    /// <summary>MariaDB keeps its expression-free catalog shape without inheriting MySQL's MVI guard.</summary>
    [Fact]
    public void MariaDbMultiValuedRiskDoesNotReferenceUnavailableExpressionCatalog()
    {
        // Arrange
        string[] columns = ["@column"];

        // Act
        var sql = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyMultiValuedIndexRisk(
            "@child", columns, isMariaDb: true);

        // Assert
        Assert.Equal("FALSE", sql);
    }

    /// <summary>Captures unsafe index names as independent physical lifetimes rather than one risk bit.</summary>
    /// <param name="mariaDb">Whether the native engine lacks MySQL's array-cast expression field.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiValuedInventoryRetainsExactUnsafeNames(bool mariaDb)
    {
        // Arrange
        string[] columns = ["@column"];

        // Act
        var query = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyMultiValuedIndexQuery(
            "@child", columns, mariaDb);

        // Assert
        if (mariaDb)
        {
            Assert.Equal("SELECT NULL AS INDEX_NAME WHERE FALSE", query);
        }
        else
        {
            Assert.StartsWith("SELECT supporting_index.INDEX_NAME", query, StringComparison.Ordinal);
            Assert.Contains("GROUP BY supporting_index.INDEX_NAME, "
                + "CAST(supporting_index.INDEX_NAME AS BINARY) HAVING COUNT(*) >= 1", query,
                StringComparison.Ordinal);
            Assert.Contains("supporting_index.EXPRESSION IS NOT NULL", query, StringComparison.Ordinal);
            Assert.DoesNotContain("COLLATION =", query, StringComparison.Ordinal);
            Assert.DoesNotContain("GROUP_CONCAT", query, StringComparison.Ordinal);
        }

    }

    /// <summary>Reuses native case rules without relying on broad metadata collation equality.</summary>
    [Fact]
    public void IndexEligibilityReusesNativeCaseModeOwnerContract()
    {
        // Arrange
        const string table = "@child";
        string[] columns = ["@column"];

        // Act
        var coverage = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyChildCoveringIndexQuery(
            table, columns, isMariaDb: false);
        var occupation = MySqlSafeMigrationCatalogSqlBuilder.IsSupportingIndexNameOccupied(table, "@fk");
        var unsafeQuery = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyMultiValuedIndexQuery(
            table, columns, isMariaDb: false);

        // Assert
        Assert.All(new[] { coverage, occupation, unsafeQuery }, sql =>
        {
            Assert.Contains("CASE WHEN @@lower_case_table_names = 0 THEN", sql, StringComparison.Ordinal);
            Assert.Contains("WHEN @@lower_case_table_names IN (1, 2) THEN", sql, StringComparison.Ordinal);
            Assert.Contains("CAST(LOWER(@child) AS BINARY) ELSE FALSE END)", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(" COLLATE ", sql, StringComparison.Ordinal);
        });
    }

    /// <summary>Native integrity qualification precedes matching and constrains the final FK postcondition.</summary>
    /// <param name="mariaDb">Whether MySQL's native array-index risk exists.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiValuedIntegrityGuardPrecedesMatchingAndSurvivesPostcondition(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "fk_owner", "child_items", ["owner"], "parent_items", ["id"])), SafeMigrationPolicy.ThrowIfDifferent);

        using var lease = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plans = lease.Complete();

        // Assert
        var plan = Assert.Single(plans);
        var state = plan.StateExpression;

        Assert.Equal("foreign_key_multi_valued_supporting_index", plan.UnsupportedCode);
        Assert.True(state.IndexOf("THEN 'unsupported'", StringComparison.Ordinal)
            < state.IndexOf("THEN 'matching'", StringComparison.Ordinal));
        Assert.Equal(!mariaDb, state.Contains("supporting_index.EXPRESSION IS NOT NULL", StringComparison.Ordinal));
        Assert.Equal(!mariaDb, plan.Postcondition.Contains("supporting_index.EXPRESSION IS NOT NULL",
            StringComparison.Ordinal));
        Assert.Contains(mariaDb ? "NOT (FALSE)" : "NOT (EXISTS (SELECT supporting_index.INDEX_NAME",
            plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains(" AND ", plan.Postcondition, StringComparison.Ordinal);
        Assert.Contains(") OR (NOT (", plan.Postcondition, StringComparison.Ordinal);
    }

    /// <summary>A live FK dependency prevents removal of its unsafe native MVI backing index.</summary>
    /// <param name="mariaDb">Whether MySQL's native array-index dependency exists.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultiValuedDropDependencyGuardPrecedesMatching(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var operation = new SafeMigrationOperation(new DropIndexIntent("unsafe_array_key", "child_items"),
            SafeMigrationPolicy.ThrowIfDifferent);

        using var lease = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plans = lease.Complete();

        // Assert
        var plan = Assert.Single(plans);
        var state = plan.StateExpression;

        Assert.Equal("foreign_key_multi_valued_supporting_index", plan.UnsupportedCode);
        Assert.Contains("THEN 'unsupported'", state, StringComparison.Ordinal);
        Assert.True(state.IndexOf("THEN 'unsupported'", StringComparison.Ordinal)
            < state.IndexOf("'matching'", StringComparison.Ordinal));
        Assert.Equal(!mariaDb, state.Contains("dependent_fk.REFERENCED_TABLE_NAME IS NOT NULL",
            StringComparison.Ordinal));
        Assert.Equal(!mariaDb, state.Contains("array_part.EXPRESSION IS NOT NULL", StringComparison.Ordinal));
        Assert.DoesNotContain("DROP FOREIGN KEY", state, StringComparison.Ordinal);
    }

    /// <summary>Only a complete actual FK prefix binds removal of a specific native unsafe index.</summary>
    [Fact]
    public void MultiValuedDropInventoryRequiresEveryActualForeignKeyPart()
    {
        // Arrange
        const string table = "@child";
        const string index = "@unsafe_index";

        // Act
        var query = MySqlSafeMigrationCatalogSqlBuilder.BuildMultiValuedIndexBoundForeignKeysQuery(
            table, index, isMariaDb: false);

        // Assert
        Assert.StartsWith("SELECT DISTINCT dependent_fk.CONSTRAINT_NAME "
            + "FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE ", query,
            StringComparison.Ordinal);
        Assert.Contains("LEFT JOIN INFORMATION_SCHEMA.STATISTICS bound_index ON ", query, StringComparison.Ordinal);
        Assert.Contains("bound_index.SEQ_IN_INDEX = dependent_fk.ORDINAL_POSITION", query, StringComparison.Ordinal);
        Assert.Contains("bound_index.COLUMN_NAME = dependent_fk.COLUMN_NAME", query, StringComparison.Ordinal);
        Assert.Contains("bound_index.SUB_PART IS NULL", query, StringComparison.Ordinal);
        Assert.Contains("bound_index.INDEX_NAME = @unsafe_index", query, StringComparison.Ordinal);
        Assert.Contains("bound_index.INDEX_TYPE = 'BTREE'", query, StringComparison.Ordinal);
        Assert.Contains("dependent_fk.REFERENCED_TABLE_NAME IS NOT NULL", query, StringComparison.Ordinal);
        Assert.Contains("GROUP BY dependent_fk.CONSTRAINT_NAME, CAST(dependent_fk.CONSTRAINT_NAME AS BINARY), "
            + "unsafe_index.INDEX_NAME, CAST(unsafe_index.INDEX_NAME AS BINARY) "
            + "HAVING COUNT(*) = COUNT(bound_index.SEQ_IN_INDEX)",
            query, StringComparison.Ordinal);
        Assert.Contains("CAST(LEFT(array_part.EXPRESSION, 5) AS BINARY) = CAST('cast(' AS BINARY)", query,
            StringComparison.Ordinal);
        Assert.Contains("CAST(RIGHT(array_part.EXPRESSION, 7) AS BINARY) = CAST(' array)' AS BINARY)", query,
            StringComparison.Ordinal);
        Assert.Contains("array_part.TABLE_SCHEMA = DATABASE() AND array_part.TABLE_NAME = @child", query,
            StringComparison.Ordinal);
        Assert.DoesNotContain("array_part.TABLE_SCHEMA = dependent_fk.TABLE_SCHEMA", query, StringComparison.Ordinal);
        Assert.DoesNotContain("COLLATION =", query, StringComparison.Ordinal);
        Assert.DoesNotContain("NON_UNIQUE", query, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP_CONCAT", query, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM `", query, StringComparison.Ordinal);
    }

    /// <summary>An ordinary target is protected only by a complete compatible native MVI sibling prefix.</summary>
    [Fact]
    public void OrdinaryDropRiskPairsItsActualForeignKeyWithCompatibleMultiValuedIndexes()
    {
        // Arrange
        const string table = "@child";
        const string target = "@ordinary_index";

        // Act
        var query = MySqlSafeMigrationCatalogSqlBuilder.BuildMultiValuedIndexBoundForeignKeysQuery(
            table, target, isMariaDb: false);

        // Assert
        Assert.Contains("JOIN (SELECT array_part.INDEX_NAME", query, StringComparison.Ordinal);
        Assert.Contains("unsafe_index ON TRUE", query, StringComparison.Ordinal);
        Assert.Contains("bound_index.INDEX_NAME = @ordinary_index", query, StringComparison.Ordinal);
        Assert.Contains("unsafe_prefix.INDEX_NAME = unsafe_index.INDEX_NAME", query, StringComparison.Ordinal);
        Assert.Contains("AND COUNT(*) = COUNT(unsafe_prefix.SEQ_IN_INDEX)", query, StringComparison.Ordinal);
        Assert.DoesNotContain("array_part.INDEX_NAME = @ordinary_index", query, StringComparison.Ordinal);
    }

    /// <summary>An unrelated, reversed or partial MVI prefix cannot prohibit dropping an ordinary index.</summary>
    [Fact]
    public void OrdinaryDropRiskRequiresBothCompleteUnprefixedOrderedForeignKeyMatches()
    {
        // Arrange
        const string table = "@child";
        const string target = "@ordinary_index";

        // Act
        var query = MySqlSafeMigrationCatalogSqlBuilder.BuildMultiValuedIndexBoundForeignKeysQuery(
            table, target, isMariaDb: false);

        // Assert
        Assert.Contains("unsafe_prefix.SEQ_IN_INDEX = dependent_fk.ORDINAL_POSITION", query, StringComparison.Ordinal);
        Assert.Contains("unsafe_prefix.COLUMN_NAME = dependent_fk.COLUMN_NAME", query, StringComparison.Ordinal);
        Assert.Contains("unsafe_prefix.SUB_PART IS NULL", query, StringComparison.Ordinal);
        Assert.Contains("unsafe_prefix.INDEX_TYPE = 'BTREE'", query, StringComparison.Ordinal);
        Assert.Contains("HAVING COUNT(*) = COUNT(bound_index.SEQ_IN_INDEX) "
            + "AND COUNT(*) = COUNT(unsafe_prefix.SEQ_IN_INDEX)", query, StringComparison.Ordinal);
        Assert.Contains("CAST(unsafe_prefix.TABLE_NAME AS BINARY)", query, StringComparison.Ordinal);
        Assert.Contains("CAST(unsafe_prefix.TABLE_SCHEMA AS BINARY)", query, StringComparison.Ordinal);
    }

    /// <summary>Several compatible native MVI aliases retain exactly one captured dependency per physical FK.</summary>
    [Fact]
    public void OrdinaryDropRiskDoesNotDuplicateForeignKeyDependencyAliases()
    {
        // Arrange
        const string table = "@child";
        const string target = "@ordinary_index";

        // Act
        var query = MySqlSafeMigrationCatalogSqlBuilder.BuildMultiValuedIndexBoundForeignKeysQuery(
            table, target, isMariaDb: false);

        // Assert
        Assert.StartsWith("SELECT DISTINCT dependent_fk.CONSTRAINT_NAME", query, StringComparison.Ordinal);
        Assert.Contains("GROUP BY dependent_fk.CONSTRAINT_NAME, CAST(dependent_fk.CONSTRAINT_NAME AS BINARY), "
            + "unsafe_index.INDEX_NAME, CAST(unsafe_index.INDEX_NAME AS BINARY)", query,
            StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP_CONCAT", query, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM `", query, StringComparison.Ordinal);
    }

    /// <summary>Actual FK and index facts retain native owner case modes rather than catalog aliases.</summary>
    [Fact]
    public void MultiValuedDropInventoryBindsEveryNativePhysicalOwner()
    {
        // Arrange
        const string table = "@child";
        string[] aliases = ["dependent_fk", "bound_index", "array_part", "unsafe_prefix"];

        // Act
        var query = MySqlSafeMigrationCatalogSqlBuilder.BuildMultiValuedIndexBoundForeignKeysQuery(
            table, "@index", isMariaDb: false);

        // Assert
        Assert.All(aliases, alias =>
        {
            Assert.Contains("CAST(" + alias + ".TABLE_NAME AS BINARY)", query, StringComparison.Ordinal);
            Assert.Contains("CAST(" + alias + ".TABLE_SCHEMA AS BINARY)", query, StringComparison.Ordinal);
        });

        Assert.Contains("CASE WHEN @@lower_case_table_names = 0 THEN", query, StringComparison.Ordinal);
        Assert.Contains("WHEN @@lower_case_table_names IN (1, 2) THEN", query, StringComparison.Ordinal);
        Assert.Contains("CAST(LOWER(@child) AS BINARY) ELSE FALSE END)", query, StringComparison.Ordinal);
        Assert.DoesNotContain(" COLLATE ", query, StringComparison.Ordinal);
    }

    /// <summary>MariaDB does not acquire an unsupported MySQL expression dependency during index removal.</summary>
    [Fact]
    public void MariaDbMultiValuedDropInventoryRemainsExpressionFree()
    {
        // Arrange
        const string table = "@child";
        const string index = "@index";

        // Act
        var query = MySqlSafeMigrationCatalogSqlBuilder.BuildMultiValuedIndexBoundForeignKeysQuery(
            table, index, isMariaDb: true);

        // Assert
        Assert.Equal("SELECT NULL AS CONSTRAINT_NAME WHERE FALSE", query);
    }

    /// <summary>Incomplete rendered ownership cannot broaden the unsafe dependency namespace.</summary>
    /// <param name="shape">The absent table or index token.</param>
    /// <param name="mariaDb">Whether validation precedes the expression-free MariaDB shortcut.</param>
    [Theory]
    [InlineData("empty_table", false)]
    [InlineData("null_table", false)]
    [InlineData("empty_index", false)]
    [InlineData("null_index", false)]
    [InlineData("empty_table", true)]
    [InlineData("null_table", true)]
    [InlineData("empty_index", true)]
    [InlineData("null_index", true)]
    public void MultiValuedDropInventoryRejectsIncompleteOwnership(string shape, bool mariaDb)
    {
        // Arrange
        var table = shape switch
        {
            "empty_table" => string.Empty,
            "null_table" => null,
            _ => "@child",
        };

        var index = shape switch
        {
            "empty_index" => string.Empty,
            "null_index" => null,
            _ => "@index",
        };

        // Act
        var exception = Record.Exception(() =>
            MySqlSafeMigrationCatalogSqlBuilder.BuildMultiValuedIndexBoundForeignKeysQuery(table!, index!, mariaDb));

        // Assert
        Assert.IsAssignableFrom<ArgumentException>(exception);
    }

    /// <summary>Requires the complete ordered unprefixed FK prefix while permitting trailing index parts.</summary>
    /// <param name="columnCount">The authored FK width, including its qualified upper boundary.</param>
    /// <param name="mariaDb">Whether either native prefix direction applies.</param>
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(16, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(16, true)]
    public void CoveringQueryKeepsCompleteOrderedLeftPrefix(int columnCount, bool mariaDb)
    {
        // Arrange
        var columns = Enumerable.Range(0, columnCount)
            .Select(static ordinal => "@column_" + ordinal.ToString(CultureInfo.InvariantCulture))
            .ToArray();

        // Act
        var sql = MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyChildCoveringIndexQuery(
            "@child", columns, mariaDb);

        // Assert
        Assert.StartsWith("SELECT supporting_index.INDEX_NAME FROM INFORMATION_SCHEMA.STATISTICS ", sql,
            StringComparison.Ordinal);
        Assert.Contains("CAST(supporting_index.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", sql,
            StringComparison.Ordinal);
        Assert.Contains("CAST(supporting_index.TABLE_NAME AS BINARY) = CAST(@child AS BINARY)", sql,
            StringComparison.Ordinal);
        Assert.Contains("supporting_index.INDEX_TYPE = 'BTREE'", sql, StringComparison.Ordinal);
        Assert.Contains("GROUP BY supporting_index.INDEX_NAME, CAST(supporting_index.INDEX_NAME AS BINARY) "
            + "HAVING COUNT(*) >= "
            + columnCount.ToString(CultureInfo.InvariantCulture), sql, StringComparison.Ordinal);
        Assert.All(Enumerable.Range(0, columnCount), ordinal => Assert.Contains(
            "supporting_index.SEQ_IN_INDEX = " + (ordinal + 1).ToString(CultureInfo.InvariantCulture)
            + " AND supporting_index.COLUMN_NAME = " + columns[ordinal]
            + " AND " + MySqlSafeMigrationCatalogSqlBuilder.BuildNativeCaseInsensitiveIdentifierMatches(
                "supporting_index.COLUMN_NAME", columns[ordinal])
            + (mariaDb ? string.Empty : " AND supporting_index.COLLATION = 'A'")
            + " AND supporting_index.SUB_PART IS NULL THEN 1 ELSE 0 END) = 1", sql, StringComparison.Ordinal));

        Assert.DoesNotContain("NON_UNIQUE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP_CONCAT", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IS_VISIBLE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("IGNORED", sql, StringComparison.Ordinal);
        Assert.Equal(!mariaDb, sql.Contains("COLLATION", StringComparison.Ordinal));
        Assert.DoesNotContain("SUM(CASE WHEN supporting_index.COLLATION = 'D'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM `", sql, StringComparison.Ordinal);
        Assert.DoesNotContain(";", sql, StringComparison.Ordinal);
    }

    /// <summary>Occupation is table-local even when the foreign-key symbol is database-scoped.</summary>
    [Fact]
    public void OccupationQueryDoesNotMergeForeignOwnerOrIndexShape()
    {
        // Arrange
        const string table = "@bound_child";
        const string symbol = "@bound_symbol";

        // Act
        var sql = MySqlSafeMigrationCatalogSqlBuilder.IsSupportingIndexNameOccupied(table, symbol);

        // Assert
        Assert.Equal("EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS automatic_index_name "
            + "WHERE automatic_index_name.TABLE_SCHEMA = DATABASE() AND automatic_index_name.TABLE_NAME = "
            + table + " AND "
            + MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
                "automatic_index_name.TABLE_SCHEMA", "DATABASE()") + " AND "
            + MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
                "automatic_index_name.TABLE_NAME", table) + " "
            + "AND automatic_index_name.INDEX_NAME = " + symbol + " AND "
            + MySqlSafeMigrationCatalogSqlBuilder.BuildNativeCaseInsensitiveIdentifierMatches(
                "automatic_index_name.INDEX_NAME", symbol) + ")", sql);
        Assert.DoesNotContain("INDEX_TYPE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("NON_UNIQUE", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("REFERENTIAL_CONSTRAINTS", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("DROP", sql, StringComparison.Ordinal);
    }

    /// <summary>Qualified endpoints retain the identity guard before current-database index qualification.</summary>
    /// <param name="mariaDb">Whether MariaDB or MySQL schema qualification applies.</param>
    /// <param name="principalSchema">The explicit referenced database qualifier.</param>
    [Theory]
    [InlineData(false, "application")]
    [InlineData(false, "foreign_database")]
    [InlineData(true, "application")]
    [InlineData(true, "foreign_database")]
    public void QualifiedEndpointsRetainDatabaseIdentityGuard(bool mariaDb, string principalSchema)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var operation = new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
            "fk_owner", "child_items", ["owner"], "parent_items", ["id"],
            schema: "application", principalSchema: principalSchema)), SafeMigrationPolicy.ThrowIfDifferent);

        using var lease = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plans = lease.Complete();

        // Assert
        var plan = Assert.Single(plans);

        Assert.NotNull(plan.CurrentDatabaseQualificationExpression);
        Assert.Equal("database_qualifier_mismatch", plan.CurrentDatabaseQualificationFailureCode);
        Assert.Contains("DATABASE()", plan.CurrentDatabaseQualificationExpression, StringComparison.Ordinal);
        Assert.Contains("CAST(automatic_index_name.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)",
            plan.StateExpression,
            StringComparison.Ordinal);
        Assert.Contains("CAST(supporting_index.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)",
            plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains(plan.ParameterValues, value => Equals(value.Value, "application"));
        Assert.Contains(plan.ParameterValues, value => Equals(value.Value, principalSchema));
    }

    /// <summary>Incomplete supporting-key requests cannot become broad catalog certificates.</summary>
    /// <param name="shape">The invalid table, column collection or rendered column token.</param>
    [Theory]
    [InlineData("empty_table")]
    [InlineData("null_table")]
    [InlineData("empty_columns")]
    [InlineData("null_columns")]
    [InlineData("empty_column")]
    [InlineData("null_column")]
    public void CoveringQueryRejectsIncompleteInputs(string shape)
    {
        // Arrange
        var table = shape switch
        {
            "empty_table" => string.Empty,
            "null_table" => null,
            _ => "@child",
        };

        IReadOnlyList<string>? columns = shape switch
        {
            "empty_columns" => [],
            "null_columns" => null,
            "empty_column" => [string.Empty],
            "null_column" => [null!],
            _ => ["@column"],
        };

        // Act
        var exception = Record.Exception(() =>
            MySqlSafeMigrationCatalogSqlBuilder.BuildForeignKeyChildCoveringIndexQuery(
                table!, columns!, isMariaDb: false));

        // Assert
        Assert.IsAssignableFrom<ArgumentException>(exception);
    }

    /// <summary>Missing name or ownership tokens never qualify an unrelated automatic-index namespace.</summary>
    /// <param name="shape">The absent table or FK-symbol token.</param>
    [Theory]
    [InlineData("empty_table")]
    [InlineData("null_table")]
    [InlineData("empty_symbol")]
    [InlineData("null_symbol")]
    public void OccupationQueryRejectsIncompleteOwnership(string shape)
    {
        // Arrange
        var table = shape switch
        {
            "empty_table" => string.Empty,
            "null_table" => null,
            _ => "@child",
        };

        var symbol = shape switch
        {
            "empty_symbol" => string.Empty,
            "null_symbol" => null,
            _ => "@symbol",
        };

        // Act
        var exception = Record.Exception(() =>
            MySqlSafeMigrationCatalogSqlBuilder.IsSupportingIndexNameOccupied(table!, symbol!));

        // Assert
        Assert.IsAssignableFrom<ArgumentException>(exception);
    }

    /// <summary>Creates a provider context without opening a connection.</summary>
    /// <param name="mariaDb">Whether MariaDB feature metadata applies.</param>
    /// <returns>The disposable offline SQL-generation context.</returns>
    private static DbContext CreateContext(bool mariaDb)
    {
        var version = mariaDb
            ? MySqlServerVersion.MariaDb(new Version(11, 8, 8))
            : MySqlServerVersion.MySql(new Version(8, 4, 11));

        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
                version)
            .UseMySqlSafeMigrations()
            .Options;

        return new DbContext(options);
    }
}
