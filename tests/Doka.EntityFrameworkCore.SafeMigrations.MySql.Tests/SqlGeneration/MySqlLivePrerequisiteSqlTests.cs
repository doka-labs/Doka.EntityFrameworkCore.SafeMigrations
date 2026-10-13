namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Guards native storage equivalence and bounded catalog-only sparse prerequisite certificates.</summary>
public sealed class MySqlLivePrerequisiteSqlTests
{
    private static readonly string[] s_quotedParameterValues =
        ["parent'quoted", "child'quoted", "fk'quoted", "id'one", "id'two", "new", "old"];

    /// <summary>
    /// Matching create declarations retain the sparse-capture fast path; real namespace mutations do not.
    /// </summary>
    /// <param name="kind">The authored index or constraint namespace operation.</param>
    /// <param name="state">Its independently observed live state.</param>
    /// <param name="expected">Whether ordered namespace evidence may need a bounded catalog capture.</param>
    [Theory]
    [InlineData("index", SafeMigrationObservedState.Matching, false)]
    [InlineData("primary", SafeMigrationObservedState.Matching, false)]
    [InlineData("unique", SafeMigrationObservedState.Matching, false)]
    [InlineData("index", SafeMigrationObservedState.Missing, true)]
    [InlineData("primary", SafeMigrationObservedState.PrerequisiteMissing, true)]
    [InlineData("unique", SafeMigrationObservedState.Unsupported, false)]
    [InlineData("drop_fk", SafeMigrationObservedState.Matching, true)]
    [InlineData("drop_fk", SafeMigrationObservedState.Missing, false)]
    [InlineData("drop_primary", SafeMigrationObservedState.Matching, true)]
    [InlineData("drop_primary", SafeMigrationObservedState.Missing, false)]
    [InlineData("drop_unique", SafeMigrationObservedState.Matching, true)]
    [InlineData("drop_unique", SafeMigrationObservedState.Unsupported, false)]
    public void NamespaceCaptureSkipsMatchingCreateDeclarations(
        string kind,
        SafeMigrationObservedState state,
        bool expected
    )
    {
        // Arrange
        var builder = new MigrationBuilder("Doka.EntityFrameworkCore.MySql");
        switch (kind)
        {
            case "index":
                builder.CreateIndexIfNotExistsFromModel("ix", "items", "id");
                break;
            case "primary":
                builder.AddPrimaryKeyIfNotExists("PRIMARY", "items", ["id"]);
                break;
            case "unique":
                builder.AddUniqueConstraintIfNotExists("uq", "items", ["id"]);
                break;
            case "drop_fk":
                builder.DropForeignKeyIfExists("fk", "items");
                break;
            case "drop_primary":
                builder.DropPrimaryKeyIfExists("PRIMARY", "items");
                break;
            default:
                builder.DropUniqueConstraintIfExists("uq", "items");
                break;
        }

        var intent = ((SafeMigrationOperation)builder.Operations[0]).Intent;
        var analysis = new SafeMigrationProviderAnalysis(state, SafeMigrationRepairCapability.None, false, "probe");

        // Act
        var actual = MySqlSafeMigrationProviderAnalyzer.MayMutateLiveNamespace(intent, analysis);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>Only integer widths and SQL keyword spelling are cosmetic; native member literals are not.</summary>
    /// <param name="first">The first native or projected physical storage contract.</param>
    /// <param name="second">The second physical storage contract.</param>
    /// <param name="expected">Whether they represent the same FK storage domain.</param>
    [Theory]
    [InlineData("int", "int(11)", true)]
    [InlineData("int unsigned", "int(10) unsigned", true)]
    [InlineData("bigint", "bigint(20)", true)]
    [InlineData("tinyint", "tinyint(1)", true)]
    [InlineData("int", "int unsigned", false)]
    [InlineData("int", "bigint", false)]
    [InlineData("varchar(36)", "VARCHAR(36)", true)]
    [InlineData("varchar(36)", "varchar(64)", false)]
    [InlineData("decimal(8,2)", "decimal(9,2)", false)]
    [InlineData("enum('A','B')", "enum('a','B')", false)]
    [InlineData("set('A','B')", "set('B','A')", false)]
    [InlineData("enum('A','B')", "enum('A','B')", true)]
    [InlineData("ENUM('A','b')", "enum('A','b')", true)]
    [InlineData(" SET ( 'A' , 'b' ) ", "set('A','b')", true)]
    [InlineData("ENUM('A','b')", "enum('a','b')", false)]
    [InlineData("ENUM('A','b')", "enum('b','A')", false)]
    [InlineData("ENUM ('A','b')", "enum ('a','b')", false)]
    [InlineData("ENUM('A\\n','b')", "enum('A\\n','b')", false)]
    public void LiveForeignKeyStorageEquivalencePreservesNativeDomains(string first, string second, bool expected)
    {
        // Arrange
        // Act
        var actual = MySqlSafeMigrationProviderAnalyzer.ForeignKeyStoreTypesMatch(first, second);

        // Assert
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Candidate transport binds authored identities and preserves exact order without lossy aliases.
    /// </summary>
    /// <param name="databaseScopedNames">Whether duplicate symbols are rejected across table owners.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveForeignKeyCandidateQueryIsBoundedParameterizedAndCatalogOnly(bool databaseScopedNames)
    {
        // Arrange
        await using var connection = new MySqlConnection();
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: 17);
        var command = batch.CreateCommand();
        var definition = new ExpectedForeignKeyDefinition("fk'quoted", "child'quoted", ["new", "old"],
            "parent'quoted", ["id'one", "id'two"]);

        // Act
        var valueBytes = MySqlSafeMigrationCatalogSqlBuilder.BuildLiveForeignKeyCandidateProbe(
            command, definition, databaseScopedNames);

        // Assert
        Assert.Equal(7, command.Parameters.Count);
        Assert.DoesNotContain("parent'quoted", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("fk'quoted", command.CommandText, StringComparison.Ordinal);
        Assert.Contains("s.SEQ_IN_INDEX = 1 AND s.COLUMN_NAME = @live_3", command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("s.SEQ_IN_INDEX = 2 AND s.COLUMN_NAME = @live_4", command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("s.SUB_PART IS NULL", command.CommandText, StringComparison.Ordinal);
        Assert.Contains(MySqlSafeMigrationCatalogSqlBuilder.BuildNativeCaseInsensitiveIdentifierMatches(
            "s.COLUMN_NAME", "@live_3"), command.CommandText, StringComparison.Ordinal);

        Assert.Contains("GROUP BY s.INDEX_NAME, CAST(s.INDEX_NAME AS BINARY)", command.CommandText,
            StringComparison.Ordinal);
        Assert.Contains("HAVING COUNT(*) = 2", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("GROUP_CONCAT", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM `", command.CommandText, StringComparison.Ordinal);
        Assert.Equal(!databaseScopedNames,
            command.CommandText.Contains("AND rc.CONSTRAINT_NAME = @live_2 "
                + "AND " + MySqlSafeMigrationCatalogSqlBuilder.BuildNativeCaseInsensitiveIdentifierMatches(
                    "rc.CONSTRAINT_NAME", "@live_2") + " "
                + "AND " + MySqlSafeMigrationCatalogSqlBuilder.BuildRequiredColumnAddOwnerMatches(
                    "rc.TABLE_NAME", "@live_1") + ") "
                + "AS names_free", StringComparison.Ordinal));

        Assert.Equal(s_quotedParameterValues,
            command.Parameters.Cast<DbParameter>().Select(static parameter => (string)parameter.Value!).ToArray());

        Assert.True(valueBytes >= 7 * 32);
        Assert.False(SafeMigrationCatalogQueryLimits.Exceeded(command.Parameters.Count,
            Encoding.UTF8.GetByteCount(command.CommandText) + valueBytes,
            SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes));
    }

    /// <summary>All actual candidate aliases survive streaming, independently of the sixteen-column FK limit.</summary>
    [Fact]
    public async Task LiveForeignKeyCandidateReaderPreservesCompleteAliasInventory()
    {
        // Arrange
        using var table = CandidateFactsTable();
        for (var index = 0; index < 32; index++)
        {
            table.Rows.Add(1, 0, 0, 0, "uq_" + index.ToString(CultureInfo.InvariantCulture), "unique_constraint");
        }

        await using var reader = table.CreateDataReader();

        // Act
        var result = await MySqlSafeMigrationProviderAnalyzer.ReadLiveCandidateKeyProofAsync(
            reader, CancellationToken.None);

        // Assert
        Assert.True(result.NamesFree);
        Assert.False(result.OwnedCollision);
        Assert.False(result.LocalIndexNameOccupied);
        Assert.False(result.MultiValuedIndexRisk);
        Assert.Empty(result.CoveringIndexNames);
        Assert.Equal(32, result.Keys.Length);
        Assert.Equal(32, result.Keys.Select(static key => key.PhysicalName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(result.Keys, static key =>
        {
            Assert.Equal(SafeMigrationDatabaseObjectKind.UniqueConstraint, key.Kind);
            Assert.Equal(key.PhysicalName, key.BackingIndexName);
        });
    }

    /// <summary>Principal and dependent alias namespaces remain separate without truncating local evidence.</summary>
    [Fact]
    public async Task LiveForeignKeyCandidateReaderPreservesSupportingAliasesIndependently()
    {
        // Arrange
        using var table = CandidateFactsTable();
        table.Rows.Add(1, 0, 1, 0, "same_name", "unique_constraint");
        for (var index = 0; index < 32; index++)
        {
            table.Rows.Add(1, 0, 1, 0, index == 0 ? "same_name" : "ix_" + index.ToString(CultureInfo.InvariantCulture),
                "local_covering_index");
        }

        await using var reader = table.CreateDataReader();

        // Act
        var result = await MySqlSafeMigrationProviderAnalyzer.ReadLiveCandidateKeyProofAsync(reader,
            CancellationToken.None);

        // Assert
        Assert.True(result.LocalIndexNameOccupied);
        Assert.Single(result.Keys);
        Assert.Equal(32, result.CoveringIndexNames.Length);
        Assert.Contains("same_name", result.CoveringIndexNames);
    }

    /// <summary>Malformed transport must fail closed rather than omit or merge a native candidate.</summary>
    /// <param name="shape">The contradictory, truncated or duplicated result form.</param>
    [Theory]
    [InlineData("empty")]
    [InlineData("duplicate")]
    [InlineData("sentinel_first")]
    [InlineData("sentinel_last")]
    [InlineData("contradiction")]
    [InlineData("unknown_kind")]
    [InlineData("empty_name")]
    [InlineData("wrong_primary")]
    [InlineData("invalid_boolean")]
    [InlineData("changing_index_flag")]
    [InlineData("invalid_mvi_boolean")]
    [InlineData("changing_mvi_flag")]
    [InlineData("missing_mvi_alias")]
    [InlineData("unowned_mvi_alias")]
    [InlineData("duplicate_mvi_alias")]
    [InlineData("fractional_boolean")]
    [InlineData("text_boolean")]
    [InlineData("negative_boolean")]
    [InlineData("wide_boolean")]
    [InlineData("duplicate_covering")]
    [InlineData("null_flag")]
    [InlineData("truncated")]
    public async Task LiveForeignKeyCandidateReaderRejectsMalformedFacts(string shape)
    {
        // Arrange
        using var table = CandidateFactsTable();
        switch (shape)
        {
            case "empty":
                break;
            case "duplicate":
                table.Rows.Add(1, 0, 0, 0, "uq_candidate", "unique_constraint");
                table.Rows.Add(1, 0, 0, 0, "UQ_CANDIDATE", "unique_constraint");
                break;
            case "sentinel_first":
                table.Rows.Add(1, 0, 0, 0, DBNull.Value, DBNull.Value);
                table.Rows.Add(1, 0, 0, 0, "uq_candidate", "unique_constraint");
                break;
            case "sentinel_last":
                table.Rows.Add(1, 0, 0, 0, "uq_candidate", "unique_constraint");
                table.Rows.Add(1, 0, 0, 0, DBNull.Value, DBNull.Value);
                break;
            case "contradiction":
                table.Rows.Add(1, 1, 0, 0, DBNull.Value, DBNull.Value);
                break;
            case "unknown_kind":
                table.Rows.Add(1, 0, 0, 0, "uq_candidate", "unproven");
                break;
            case "empty_name":
                table.Rows.Add(1, 0, 0, 0, string.Empty, "unique_constraint");
                break;
            case "wrong_primary":
                table.Rows.Add(1, 0, 0, 0, "not_primary", "primary_key");
                break;
            case "invalid_boolean":
                table.Rows.Add(1, 0, 2, 0, DBNull.Value, DBNull.Value);
                break;
            case "changing_index_flag":
                table.Rows.Add(1, 0, 0, 0, "uq_candidate", "unique_constraint");
                table.Rows.Add(1, 0, 1, 0, "ix_child", "local_covering_index");
                break;
            case "invalid_mvi_boolean":
                table.Rows.Add(1, 0, 0, 2, DBNull.Value, DBNull.Value);
                break;
            case "changing_mvi_flag":
                table.Rows.Add(1, 0, 0, 0, "uq_candidate", "unique_constraint");
                table.Rows.Add(1, 0, 0, 1, "ix_child", "local_covering_index");
                break;
            case "missing_mvi_alias":
                table.Rows.Add(1, 0, 0, 1, "uq_parent", "unique_constraint");
                break;
            case "unowned_mvi_alias":
                table.Rows.Add(1, 0, 0, 0, "ix_multi", "local_multi_valued_index");
                break;
            case "duplicate_mvi_alias":
                table.Rows.Add(1, 0, 0, 1, "ix_multi", "local_multi_valued_index");
                table.Rows.Add(1, 0, 0, 1, "IX_MULTI", "local_multi_valued_index");
                break;
            case "fractional_boolean":
                table.Columns[3].DataType = typeof(object);
                table.Rows.Add(1, 0, 0, 0.5m, DBNull.Value, DBNull.Value);
                break;
            case "text_boolean":
                table.Columns[3].DataType = typeof(object);
                table.Rows.Add(1, 0, 0, "1", DBNull.Value, DBNull.Value);
                break;
            case "negative_boolean":
                table.Rows.Add(1, 0, 0, -1, DBNull.Value, DBNull.Value);
                break;
            case "wide_boolean":
                table.Columns[3].DataType = typeof(object);
                table.Rows.Add(1, 0, 0, ulong.MaxValue, DBNull.Value, DBNull.Value);
                break;
            case "duplicate_covering":
                table.Rows.Add(1, 0, 1, 0, "ix_child", "local_covering_index");
                table.Rows.Add(1, 0, 1, 0, "IX_CHILD", "local_covering_index");
                break;
            case "null_flag":
                table.Rows.Add(DBNull.Value, 0, 0, 0, DBNull.Value, DBNull.Value);
                break;
            case "truncated":
                table.Columns.RemoveAt(5);
                break;
            default:
                throw new UnreachableException();
        }

        await using var reader = table.CreateDataReader();

        // Act
        var exception = await Record.ExceptionAsync(async () =>
            await MySqlSafeMigrationProviderAnalyzer.ReadLiveCandidateKeyProofAsync(reader, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
    }

    /// <summary>Constructs the exact owned candidate result shape without contacting an engine.</summary>
    /// <returns>A disposable table suitable for positive and adversarial transport probes.</returns>
    private static DataTable CandidateFactsTable()
    {
        var result = new DataTable { Locale = CultureInfo.InvariantCulture };
        result.Columns.Add("names_free", typeof(int));
        result.Columns.Add("owned_collision", typeof(int));
        result.Columns.Add("local_index_name_occupied", typeof(int));
        result.Columns.Add("multi_valued_index_risk", typeof(int));
        result.Columns.Add("name", typeof(string));
        result.Columns.Add("kind", typeof(string));

        return result;
    }

    /// <summary>A large valid inventory splits before the packet ceiling without discarding the next ordinal.</summary>
    [Fact]
    public async Task LiveColumnInventoryShrinksWindowToQualifiedPayload()
    {
        // Arrange
        await using var connection = new MySqlConnection();
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: 17);
        var command = batch.CreateCommand();
        var requests = Enumerable.Range(0, 32).Select(static index =>
            ((string?)null, "t_" + index.ToString(CultureInfo.InvariantCulture) + new string('\u20AC', 58),
                "c_" + index.ToString(CultureInfo.InvariantCulture) + new string('\u20AC', 58))).ToArray();

        const int maximumPayload = 4096;

        // Act
        var result = MySqlSafeMigrationProviderAnalyzer.BuildLivePrerequisiteColumnsProbe(
            command, requests, offset: 0, maximumPayload);

        // Assert
        Assert.InRange(result.OperationCount, 1, requests.Length - 1);
        Assert.Equal(result.OperationCount * 2, command.Parameters.Count);
        Assert.Equal(0, result.FirstOrdinal);
        Assert.True(Encoding.UTF8.GetByteCount(command.CommandText) + result.ParameterPayloadBytes <= maximumPayload);
        Assert.DoesNotContain("SELECT " + result.OperationCount.ToString(CultureInfo.InvariantCulture)
            + " AS ordinal", command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>An individually oversized request is rejected explicitly rather than submitted as empty SQL.</summary>
    [Fact]
    public async Task LiveColumnInventoryRejectsIndividuallyOversizedRequest()
    {
        // Arrange
        await using var connection = new MySqlConnection();
        await using var batch = new SafeMigrationCatalogBatch(connection, commandTimeout: 17);
        var command = batch.CreateCommand();
        (string? Schema, string Table, string Name)[] requests = [(null, "live_table", "live_column")];

        // Act
        var exception = Record.Exception(() => MySqlSafeMigrationProviderAnalyzer.BuildLivePrerequisiteColumnsProbe(
            command, requests, offset: 0, maximumPayloadBytes: 128));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("exceeds a bounded query limit", exception.Message, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(command.CommandText));
    }
}
