namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Constrains lazy statement-local index certificates without timing-dependent assertions.</summary>
public sealed class PostgreSqlIndexPrerequisiteInterningTests
{
    /// <summary>Repeated index contracts retain all result ordinals but share one native certificate plan.</summary>
    /// <param name="native">Whether the transport submits a native database batch.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedContractsHaveOneLazyStatementCertificate(bool native)
    {
        // Arrange
        using var context = Context();
        var operations = Operations(context, 32, distinct: false);
        await using var connection = new CatalogStatementTestConnection(native, Results);

        // Act
        var states = await ReadPrerequisitesAsync(context, connection, operations);

        // Assert
        Assert.Equal(2, connection.Submissions.Count);
        var submission = connection.Submissions[1];
        Assert.Equal(1, CertificateCount(submission.Sql));
        Assert.InRange(Encoding.UTF8.GetByteCount(submission.Sql), 1, 160 * 1024);
        Assert.Equal(32, RequestOrdinals(submission.Sql).Count);
        Assert.All(states, static state => Assert.Null(state));
        Assert.Equal(71, submission.Timeout);
        Assert.Contains("CASE (", submission.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_indexam_has_property", submission.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("FROM \"_doka_sm_index_input_0\" WHERE", submission.Sql, StringComparison.Ordinal);
    }

    /// <summary>Rollback never leaves an unbound certificate, and prefix accounting includes multibyte input.</summary>
    [Fact]
    public void CertificateRollbackRetainsExactLazyPrefixAndUtf8Accounting()
    {
        // Arrange
        using var context = Context();
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var certificates = new PostgreSqlIndexPrerequisiteCertificates();
        var input = new ExpectedIndexDefinition("first", "members", [new ExpectedIndexKeyDefinition("id")]);
        var extra = new ExpectedIndexDefinition("second", char.ConvertFromUtf32(0x20AC),
            [new ExpectedIndexKeyDefinition("id")]);

        var first = certificates.Intern(input, builder);
        var retainedBytes = certificates.Utf8PrefixBytes;
        var count = certificates.Count;
        _ = certificates.Intern(extra, builder);

        // Act
        certificates.Rollback(count);
        var prefix = certificates.RenderPrefix();
        var reused = certificates.Intern(input, builder);

        // Assert
        Assert.Equal(1, certificates.Count);
        Assert.Equal(first, reused);
        Assert.Equal(retainedBytes, certificates.Utf8PrefixBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(prefix), certificates.Utf8PrefixBytes);
        Assert.DoesNotContain("_doka_sm_index_input_1", prefix, StringComparison.Ordinal);
    }

    /// <summary>Different physical identities and native requirements cannot share a Boolean proof.</summary>
    /// <param name="facet">The independently varied physical or native binding dimension.</param>
    [Theory]
    [InlineData("table")]
    [InlineData("schema")]
    [InlineData("column")]
    [InlineData("method")]
    [InlineData("unique")]
    [InlineData("operator-class")]
    [InlineData("sort-order")]
    [InlineData("null-order")]
    [InlineData("include")]
    [InlineData("nulls-distinct")]
    public void NativeInputDifferencesHaveIndependentCertificates(string facet)
    {
        // Arrange
        using var context = Context();
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var certificates = new PostgreSqlIndexPrerequisiteCertificates();
        var original = new ExpectedIndexDefinition("first", "members", [new ExpectedIndexKeyDefinition("id")]);
        var different = new ExpectedIndexDefinition("second", facet == "table" ? "other" : "members",
            [new ExpectedIndexKeyDefinition(facet == "column" ? "other_id" : "id",
                sortOrder: facet == "sort-order" ? SafeMigrationIndexSortOrder.Descending
                    : SafeMigrationIndexSortOrder.ProviderDefault,
                nullOrder: facet == "null-order" ? SafeMigrationIndexNullOrder.First
                    : SafeMigrationIndexNullOrder.ProviderDefault,
                operatorClass: facet == "operator-class" ? "pg_catalog.int4_ops" : null)],
            schema: facet == "schema" ? "other" : null, unique: facet is "unique" or "nulls-distinct",
            includedColumns: facet == "include" ? ["other"] : [],
            method: facet == "method" ? "hash" : null, nullsDistinct: facet == "nulls-distinct" ? false : null);

        // Act
        _ = builder.BuildIndexInputPrerequisiteState(new EnsureIndexIntent(original), certificates);
        _ = builder.BuildIndexInputPrerequisiteState(new EnsureIndexIntent(different), certificates);

        // Assert
        Assert.Equal(2, certificates.Count);
        Assert.Equal(Encoding.UTF8.GetByteCount(certificates.RenderPrefix()), certificates.Utf8PrefixBytes);
    }

    /// <summary>Every independent native AM flag remains identical to its requested definition facet.</summary>
    /// <param name="facet">The independently supplied flag dimension.</param>
    /// <param name="expected">The exact source-pinned native flag mask.</param>
    [Theory]
    [InlineData("default", 0)]
    [InlineData("unique", 2)]
    [InlineData("multiple", 4)]
    [InlineData("include", 8)]
    [InlineData("nulls-not-distinct", 18)]
    [InlineData("ascending", 1)]
    [InlineData("descending", 1)]
    [InlineData("nulls-first", 1)]
    [InlineData("nulls-last", 1)]
    public void NativeFlagMatrixRemainsExact(string facet, int expected)
    {
        // Arrange
        var key = new ExpectedIndexKeyDefinition("id", sortOrder: facet switch
        {
            "ascending" => SafeMigrationIndexSortOrder.Ascending,
            "descending" => SafeMigrationIndexSortOrder.Descending,
            _ => SafeMigrationIndexSortOrder.ProviderDefault,
        }, nullOrder: facet switch
        {
            "nulls-first" => SafeMigrationIndexNullOrder.First,
            "nulls-last" => SafeMigrationIndexNullOrder.Last,
            _ => SafeMigrationIndexNullOrder.ProviderDefault,
        });

        var input = new ExpectedIndexDefinition("index", "members",
            facet == "multiple" ? [key, new ExpectedIndexKeyDefinition("other")] : [key],
            unique: facet is "unique" or "nulls-not-distinct", includedColumns: facet == "include" ? ["other"] : [],
            nullsDistinct: facet == "nulls-not-distinct" ? false : null);

        // Act
        var actual = PostgreSqlSafeMigrationCatalogSqlBuilder.IndexRequiredFlags(input);

        // Assert
        Assert.Equal(expected, (int)actual);
    }

    /// <summary>Later unqualified owners reserve aliases once even across native statement boundaries.</summary>
    [Fact]
    public void AliasPaletteExcludesAllActualUnqualifiedOwners()
    {
        // Arrange
        using var context = Context();
        var migration = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < 65; ordinal++)
        {
            migration.CreateIndexIfNotExists("index_" + ordinal, "_doka_sm_index_input_" + ordinal, ["id"]);
        }

        // Act
        var names = PostgreSqlIndexPrerequisiteCertificates.ReserveNames(
            migration.Operations.Cast<SafeMigrationOperation>().ToArray());

        // Assert
        Assert.Equal(SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement, names.Length);
        Assert.Equal("\"_doka_sm_index_input_65\"", names[0]);
        Assert.Equal("\"_doka_sm_index_input_96\"", names[^1]);
        Assert.Equal(names.Length, names.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>Discarding the only certificate removes every WITH byte and makes its alias reusable.</summary>
    [Fact]
    public void EmptyRollbackRemovesTheEntireCertificatePrefix()
    {
        // Arrange
        using var context = Context();
        var builder = new PostgreSqlSafeMigrationCatalogSqlBuilder(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var input = new ExpectedIndexDefinition("first", "members", [new ExpectedIndexKeyDefinition("id")]);
        var certificates = new PostgreSqlIndexPrerequisiteCertificates();
        _ = certificates.Intern(input, builder);

        // Act
        certificates.Rollback(0);

        // Assert
        Assert.Empty(certificates.RenderPrefix());
        Assert.Equal(0, certificates.Utf8PrefixBytes);
        Assert.Equal(0, certificates.Count);
    }

    /// <summary>Different native contracts and statement lifetimes never borrow another certificate.</summary>
    /// <param name="native">Whether the transport submits a native database batch.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctContractsKeepExistingStatementAndDispatchBounds(bool native)
    {
        // Arrange
        using var context = Context();
        var operations = Operations(context, 290, distinct: true);
        int[] expectedBatches = native ? [8, 2, 8, 2] : [];

        await using var connection = new CatalogStatementTestConnection(native, Results);

        // Act
        var states = await ReadPrerequisitesAsync(context, connection, operations);

        // Assert
        Assert.Equal(20, connection.Submissions.Count);
        Assert.Equal(expectedBatches, connection.BatchCounts);
        Assert.All(states, static state => Assert.Null(state));
        Assert.All(connection.Submissions.Skip(10), static submission =>
        {
            var count = RequestOrdinals(submission.Sql).Count;

            Assert.InRange(count, 1, SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
            Assert.Equal(1, CertificateCount(submission.Sql));
            Assert.InRange(Encoding.UTF8.GetByteCount(submission.Sql), 1,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
            Assert.InRange(submission.Parameters.Count, 1, SafeMigrationCatalogQueryLimits.MaximumParameters);
            Assert.Equal(71, submission.Timeout);
        });
    }

    /// <summary>Occupied names keep ordinary classification without an extra metadata-input dispatch.</summary>
    /// <param name="native">Whether the transport supports native database batches.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OccupiedNamesNeverDispatchIndexInputProofs(bool native)
    {
        // Arrange
        using var context = Context();
        var operations = Operations(context, 32, distinct: true);
        await using var connection = new CatalogStatementTestConnection(native, (sql, parameters) =>
        {
            var result = Results(sql, parameters);
            foreach (DataRow row in result.Rows)
            {
                row[1] = DBNull.Value;
            }

            return result;
        });

        // Act
        var states = await ReadPrerequisitesAsync(context, connection, operations);

        // Assert
        var submission = Assert.Single(connection.Submissions);
        Assert.Equal(0, CertificateCount(submission.Sql));
        Assert.InRange(Encoding.UTF8.GetByteCount(submission.Sql), 1, 64 * 1024);
        Assert.All(states, static state => Assert.Null(state));
    }

    /// <summary>Malformed or cancelled second-stage metadata cannot return any complete prerequisite proof.</summary>
    /// <param name="fault">The deliberately violated metadata protocol or cancellation boundary.</param>
    [Theory]
    [InlineData("ordinal")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("code")]
    [InlineData("width")]
    [InlineData("cancel")]
    public async Task InvalidInputFenceCannotReturnPartialEvidence(string fault)
    {
        // Arrange
        using var context = Context();
        var operations = Operations(context, 2, distinct: true);
        using var cancellation = new CancellationTokenSource();
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            var result = Results(sql, parameters);
            if (!sql.StartsWith("WITH ", StringComparison.Ordinal))
            {
                return result;
            }

            switch (fault)
            {
                case "ordinal": result.Rows[0][0] = 99; break;
                case "missing": result.Rows.RemoveAt(0); break;
                case "duplicate": result.Rows.Add(result.Rows[0].ItemArray); break;
                case "code": result.Rows[0][1] = "matching"; break;
                case "width": result.Columns.Add("extra", typeof(int)); break;
                case "cancel": cancellation.Cancel(); break;
            }

            return result;
        });

        // Act
        var exception = await Record.ExceptionAsync(() =>
            ReadPrerequisitesAsync(context, connection, operations, cancellation.Token));

        // Assert
        if (fault == "cancel")
        {
            Assert.IsAssignableFrom<OperationCanceledException>(exception);
        }
        else
        {
            Assert.IsType<InvalidOperationException>(exception);
        }

        Assert.Equal(2, connection.Submissions.Count);
    }

    /// <summary>Captures actual prerequisite packets without constructing or modifying native index state.</summary>
    internal static async Task<SafeMigrationObservedState?[]> ReadPrerequisitesAsync(SafeMigrationDbContext context,
        DbConnection connection, IReadOnlyList<SafeMigrationOperation> operations,
        CancellationToken cancellationToken = default)
    {
        var analyzer = new PostgreSqlSafeMigrationProviderAnalyzer(context.GetService<IRelationalTypeMappingSource>(),
            context.GetService<ISqlGenerationHelper>());

        var states = new SafeMigrationObservedState?[operations.Count];
        var method = typeof(PostgreSqlSafeMigrationProviderAnalyzer).GetMethod("FindPrerequisiteStatesAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        await (Task)method.Invoke(analyzer, [connection, operations, states, 71, cancellationToken])!;

        return states;
    }

    /// <summary>Builds a fixed physical two-key contract, optionally varying its exact native index identity.</summary>
    internal static SafeMigrationOperation[] Operations(SafeMigrationDbContext context, int count, bool distinct)
    {
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        for (var ordinal = 0; ordinal < count; ordinal++)
        {
            builder.CreateIndexIfNotExists(distinct ? "ix_prerequisite_" + ordinal : "ix_prerequisite",
                "_doka_sm_index_input_0", ["id", "parent_id"]);
        }

        return builder.Operations.Cast<SafeMigrationOperation>().ToArray();
    }

    /// <summary>Returns only the expected ordered null short-circuit states.</summary>
    internal static DataTable Results(string sql, IReadOnlyList<DbParameter> parameters)
    {
        var table = new DataTable();
        table.Columns.Add("ordinal", typeof(int));
        table.Columns.Add("state", typeof(string));
        foreach (var ordinal in RequestOrdinals(sql))
        {
            table.Rows.Add(ordinal, sql.Contains("'index_input_required'", StringComparison.Ordinal)
                ? "index_input_required" : DBNull.Value);
        }

        return table;
    }

    private static SafeMigrationDbContext Context()
        => new("Host=127.0.0.1;Port=1;Database=index_prerequisite;Username=test;Password=test",
            registerSafeMigrations: false);

    private static int CertificateCount(string sql)
        => System.Text.RegularExpressions.Regex.Count(sql, "AS MATERIALIZED \\(SELECT");

    private static List<int> RequestOrdinals(string sql)
        => System.Text.RegularExpressions.Regex.Matches(sql, @"SELECT (\d+),")
            .Select(static match => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)).ToList();
}
