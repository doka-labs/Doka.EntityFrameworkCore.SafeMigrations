namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Exercises bounded, deduplicated and adversarial PostgreSQL sparse catalog certificates.</summary>
public sealed class PostgreSqlLivePrerequisiteEvidenceTests
{
    /// <summary>Repeated contracts share metadata without treating a backing index as a second constraint.</summary>
    /// <param name="native">Whether the transport supports native batches.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedRequestsUseOneSnapshotAndRetainPhysicalKeyKinds(bool native)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(1, repetitions: 1_000);
        var results = Analyses(operations);
        await using var connection = new CatalogStatementTestConnection(native, Results);
        var columns = new AddedColumnSource();
        var intent = Assert.IsType<EnsureForeignKeyIntent>(operations[1].Intent);

        // Act
        await analyzer.PopulateLivePrerequisiteEvidenceAsync(connection, operations, results,
            71, CancellationToken.None);

        var keys = analyzer.GetLiveForeignKeyCandidateKeys(intent, columns);

        // Assert
        Assert.True(analyzer.HasLiveColumn("live_parent", null, "id"));
        Assert.False(analyzer.HasLiveColumn("child_0", null, "parent_id"));
        Assert.True(analyzer.RequiresLiveForeignKeyQualification(intent));
        Assert.Equal(
            [new SafeMigrationLiveCandidateKey(SafeMigrationDatabaseObjectKind.PrimaryKey, "pk_native", "ix_native"),
                new SafeMigrationLiveCandidateKey(SafeMigrationDatabaseObjectKind.Index, "ux_native")],
            keys);

        var submission = Assert.Single(connection.Submissions);
        Assert.Equal(4, RequestOrdinals(submission.Sql).Count);
        Assert.Equal(71, submission.Timeout);
        Assert.DoesNotContain("SELECT 1 FROM \"child_", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("i.indpred IS NULL", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("i.indimmediate", submission.Sql, StringComparison.Ordinal);
        Assert.Contains("i.indcollation", submission.Sql, StringComparison.Ordinal);
    }

    /// <summary>Metadata request count, SQL/parameter payload and native dispatch size remain bounded.</summary>
    /// <param name="native">Whether the transport supports native batches.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManyOwnersUseBoundedStatementsAndBatches(bool native)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(290);
        var results = Analyses(operations);
        int[] batches = native ? [8, 8, 3] : [];

        await using var connection = new CatalogStatementTestConnection(native, Results);

        // Act
        await analyzer.PopulateLivePrerequisiteEvidenceAsync(connection, operations, results,
            71, CancellationToken.None);

        // Assert
        Assert.Equal(19, connection.Submissions.Count);
        Assert.Equal(batches, connection.BatchCounts);
        Assert.All(connection.Submissions, static submission =>
        {
            Assert.InRange(RequestOrdinals(submission.Sql).Count, 1,
                SafeMigrationCatalogQueryLimits.MaximumOperationsPerStatement);
            Assert.InRange(submission.Parameters.Count, 1, SafeMigrationCatalogQueryLimits.MaximumParameters);
            Assert.InRange(Encoding.UTF8.GetByteCount(submission.Sql), 1,
                SafeMigrationCatalogQueryLimits.MaximumUtf8PayloadBytes);
            Assert.Equal(71, submission.Timeout);
        });
    }

    /// <summary>Malformed member, key or ordinal evidence cannot publish a partial live snapshot.</summary>
    /// <param name="fault">The deliberately violated metadata protocol invariant.</param>
    [Theory]
    [InlineData("ordinal")]
    [InlineData("missing")]
    [InlineData("duplicate-column")]
    [InlineData("duplicate-key")]
    [InlineData("boolean")]
    [InlineData("type")]
    [InlineData("collation")]
    [InlineData("kind")]
    [InlineData("backing")]
    [InlineData("width")]
    public async Task InvalidEvidencePublishesNothing(string fault)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(1);
        var results = Analyses(operations);
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            var table = Results(sql, parameters);
            switch (fault)
            {
                case "ordinal": table.Rows[1][0] = 999; break;
                case "missing": table.Rows.RemoveAt(1); break;
                case "duplicate-column": table.Rows.InsertAt(table.NewRow(), 1); table.Rows[1].ItemArray =
                    table.Rows[0].ItemArray; break;
                case "duplicate-key": table.Rows.Add(table.Rows[^1].ItemArray); break;
                case "boolean": table.Rows[1][1] = 1; break;
                case "type": table.Rows[1][2] = "2950"; break;
                case "collation": table.Rows[1][3] = -1L; break;
                case "kind": table.Rows[^1][4] = "f"; break;
                case "backing": table.Rows[^1][6] = "unexpected_backing"; break;
                case "width": table.Columns.Add("unexpected", typeof(int)); break;
            }

            return table;
        });

        // Act
        var exception = await Record.ExceptionAsync(() => analyzer.PopulateLivePrerequisiteEvidenceAsync(
            connection, operations, results, 71, CancellationToken.None));

        // Assert
        Assert.IsType<InvalidOperationException>(exception);
        Assert.False(analyzer.HasLiveColumn("live_parent", null, "id"));
        Assert.Null(analyzer.GetLiveForeignKeyCandidateKeys(
            Assert.IsType<EnsureForeignKeyIntent>(operations[1].Intent), new AddedColumnSource()));
        Assert.Equal(1, connection.ReadersDisposed);
    }

    /// <summary>Cancellation during result enumeration neither publishes evidence nor retains a live reader.</summary>
    [Fact]
    public async Task CancellationPublishesNothingAndDisposesTransport()
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(1);
        var results = Analyses(operations);
        using var cancellation = new CancellationTokenSource();
        await using var connection = new CatalogStatementTestConnection(true, Results)
        {
            BeforeRead = cancellation.Cancel,
        };

        // Act
        var exception = await Record.ExceptionAsync(() => analyzer.PopulateLivePrerequisiteEvidenceAsync(
            connection, operations, results, 71, cancellation.Token));

        // Assert
        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.False(analyzer.HasLiveColumn("live_parent", null, "id"));
        Assert.Equal(1, connection.ReadersDisposed);
        Assert.Equal(1, connection.BatchesDisposed);
    }

    /// <summary>
    /// Callable metadata publishes prior member facts only after its complete protocol has been validated.
    /// </summary>
    /// <param name="fault">The violated second-phase protocol invariant.</param>
    [Theory]
    [InlineData("ordinal")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("argument")]
    [InlineData("cast")]
    [InlineData("variadic")]
    [InlineData("namespace")]
    [InlineData("valid")]
    public async Task CheckCallableEvidencePublishesOnlyCompleteValidatedFacts(string fault)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(1).Append(new SafeMigrationOperation(new EnsureCheckConstraintIntent(
            ExpectedCheckConstraintDefinition.FromExpression("ck_requested", "child_0",
                SafeMigrationSql.Binary(SafeMigrationSql.Identifier("parent_id"),
                    SafeMigrationSqlBinaryOperator.GreaterThan, SafeMigrationSql.Literal(0)))),
            SafeMigrationPolicy.ThrowIfDifferent)).ToArray();

        var results = Analyses(operations);
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            if (!sql.Contains("pg_catalog.pg_operator o", StringComparison.Ordinal))
            {
                return Results(sql, parameters);
            }

            var table = new System.Data.DataTable();
            table.Columns.Add("ordinal", typeof(int));
            for (var field = 1; field < 8; field++)
            {
                table.Columns.Add("field_" + field, typeof(object));
            }

            table.Rows.Add(0, 521L, 16L, "23,23", string.Empty, false, false, 11L);
            switch (fault)
            {
                case "ordinal": table.Rows[0][0] = 99; break;
                case "missing": table.Rows.Clear(); break;
                case "duplicate": table.Rows.Add(table.Rows[0].ItemArray); break;
                case "argument": table.Rows[0][3] = "23,invalid"; break;
                case "cast": table.Rows[0][4] = "25:invalid"; break;
                case "variadic": table.Rows[0][6] = "invalid"; break;
                case "namespace": table.Rows[0][7] = -1L; break;
            }

            return table;
        });

        // Act
        var exception = await Record.ExceptionAsync(() => analyzer.PopulateLivePrerequisiteEvidenceAsync(
            connection, operations, results, 71, CancellationToken.None));

        // Assert
        if (fault == "valid")
        {
            Assert.Null(exception);
            Assert.True(analyzer.HasLiveColumn("live_parent", null, "id"));
            Assert.NotNull(analyzer.GetLiveForeignKeyCandidateKeys(
                Assert.IsType<EnsureForeignKeyIntent>(operations[1].Intent), new AddedColumnSource()));
        }
        else
        {
            Assert.IsType<InvalidOperationException>(exception);
            Assert.False(analyzer.HasLiveColumn("live_parent", null, "id"));
            Assert.Null(analyzer.GetLiveForeignKeyCandidateKeys(
                Assert.IsType<EnsureForeignKeyIntent>(operations[1].Intent), new AddedColumnSource()));
        }

        Assert.Equal(2, connection.ReadersDisposed);
    }

    /// <summary>Unrelated or unsupported operations do not introduce a sparse metadata roundtrip.</summary>
    /// <param name="state">The live state which requires no sparse prerequisite repair.</param>
    [Theory]
    [InlineData(SafeMigrationObservedState.Matching)]
    [InlineData(SafeMigrationObservedState.Missing)]
    [InlineData(SafeMigrationObservedState.Unsupported)]
    public async Task StreamsWithoutSparseMissingPrerequisitesSubmitNothing(SafeMigrationObservedState state)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(1);
        SafeMigrationProviderAnalysis[] results = [Live(SafeMigrationObservedState.Missing),
            Live(state)];

        await using var connection = new CatalogStatementTestConnection(true, Results);

        // Act
        await analyzer.PopulateLivePrerequisiteEvidenceAsync(connection, operations, results,
            71, CancellationToken.None);

        // Assert
        Assert.Empty(connection.Submissions);
        Assert.False(analyzer.HasLiveColumn("live_parent", null, "id"));
    }

    /// <summary>An empty subsequent analysis clears prior facts without opening a database connection.</summary>
    [Fact]
    public async Task EmptyNewInvocationDoesNotRetainOldLiveFacts()
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(1);
        var results = Analyses(operations);
        await using var connection = new CatalogStatementTestConnection(true, Results);
        await analyzer.PopulateLivePrerequisiteEvidenceAsync(connection, operations, results,
            71, CancellationToken.None);

        var initiallyCaptured = analyzer.HasLiveColumn("live_parent", null, "id");

        // Act
        var empty = await analyzer.AnalyzeAsync(context, Array.Empty<SafeMigrationOperation>(), CancellationToken.None);

        // Assert
        Assert.True(initiallyCaptured);
        Assert.Empty(empty);
        Assert.False(analyzer.HasLiveColumn("live_parent", null, "id"));
        Assert.False(analyzer.RequiresLiveForeignKeyQualification(
            Assert.IsType<EnsureForeignKeyIntent>(operations[1].Intent)));
    }

    /// <summary>Only an accepted exact owned FK removal can discharge a captured foreign-key name collision.</summary>
    /// <param name="collisionKind">The independently captured native constraint family.</param>
    /// <param name="sameOwner">Whether the accepted removal belongs to the requesting table.</param>
    /// <param name="executable">Whether the drop was independently classified as executable rather than absent.</param>
    /// <param name="approved">Whether the physical FK name has actually been discharged.</param>
    [Theory]
    [InlineData("f", true, true, true)]
    [InlineData("f", true, false, false)]
    [InlineData("f", false, true, false)]
    [InlineData("c", true, true, false)]
    public async Task ExactAcceptedForeignKeyDropCannotDischargeAnotherNativeKind(
        string collisionKind,
        bool sameOwner,
        bool executable,
        bool approved
    )
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var operations = Operations(1);
        var results = Analyses(operations);
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            var table = Results(sql, parameters);
            foreach (System.Data.DataRow row in table.Rows)
            {
                if (row[4] is string)
                {
                    row[7] = collisionKind;
                }
            }

            return table;
        });

        await analyzer.PopulateLivePrerequisiteEvidenceAsync(connection, operations, results,
            71, CancellationToken.None);

        var drop = new SafeMigrationOperation(new DropForeignKeyIntent("fk_child_0",
            sameOwner ? "child_0" : "other_child"), SafeMigrationPolicy.ThrowIfDifferent);

        var state = executable ? SafeMigrationObservedState.Matching : SafeMigrationObservedState.Missing;
        var analysis = Live(state);
        var decision = SafeMigrationDecisionPlanner.Plan(drop.Intent.Kind, state, drop.Policy);

        // Act
        analyzer.ObserveAcceptedOperation(drop, analysis, analysis, decision);
        var keys = analyzer.GetLiveForeignKeyCandidateKeys(
            Assert.IsType<EnsureForeignKeyIntent>(operations[1].Intent), new AddedColumnSource());

        // Assert
        Assert.Equal(executable ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp, decision.Action);
        Assert.Equal(approved, keys is not null);
    }

    /// <summary>Index metadata validates its complete protocol before publishing any earlier live facts.</summary>
    /// <param name="fault">The deliberately violated index protocol, or the valid deduplicated control.</param>
    [Theory]
    [InlineData("valid")]
    [InlineData("ordinal")]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("boolean")]
    [InlineData("width")]
    [InlineData("cancel")]
    public async Task IndexInputEvidencePublishesOnlyCompleteValidatedFacts(string fault)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var index = new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ix_parent", "live_parent", [new ExpectedIndexKeyDefinition(column: "id")])),
            SafeMigrationPolicy.ThrowIfDifferent);

        var operations = Operations(1).Concat(Enumerable.Repeat(index, 1_000)).ToArray();
        var results = Analyses(operations);
        using var cancellation = new CancellationTokenSource();
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            if (!sql.Contains("WITH RECURSIVE domain_types", StringComparison.Ordinal))
            {
                return Results(sql, parameters);
            }

            var table = new System.Data.DataTable();
            table.Columns.Add("ordinal", typeof(int));
            table.Columns.Add("proof", typeof(object));
            foreach (System.Text.RegularExpressions.Match match in RequestOrdinals(sql))
            {
                table.Rows.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), true);
            }

            switch (fault)
            {
                case "ordinal": table.Rows[0][0] = 99; break;
                case "missing": table.Rows.RemoveAt(0); break;
                case "duplicate": table.Rows.InsertAt(table.NewRow(), 0);
                    table.Rows[0].ItemArray = table.Rows[1].ItemArray; break;
                case "boolean": table.Rows[0][1] = "true"; break;
                case "width": table.Columns.Add("unexpected", typeof(int)); break;
                case "cancel": cancellation.Cancel(); break;
            }

            return table;
        });

        // Act
        var exception = await Record.ExceptionAsync(() => analyzer.PopulateLivePrerequisiteEvidenceAsync(
            connection, operations, results, 71, cancellation.Token));

        // Assert
        if (fault == "valid")
        {
            Assert.Null(exception);
            Assert.True(analyzer.HasLiveColumn("live_parent", null, "id"));
            var submission = Assert.Single(connection.Submissions, static item =>
                item.Sql.Contains("WITH RECURSIVE domain_types", StringComparison.Ordinal));

            Assert.Equal(2, RequestOrdinals(submission.Sql).Count);
            Assert.Equal(71, submission.Timeout);
            Assert.DoesNotContain("pg_indexam_has_property", submission.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("SELECT 1 FROM \"live_parent\"", submission.Sql, StringComparison.Ordinal);
        }
        else
        {
            if (fault == "cancel")
            {
                Assert.IsAssignableFrom<OperationCanceledException>(exception);
            }
            else
            {
                Assert.IsType<InvalidOperationException>(exception);
            }

            Assert.False(analyzer.HasLiveColumn("live_parent", null, "id"));
        }

        Assert.Equal(2, connection.ReadersDisposed);
    }

    /// <summary>A live Matching index retains its native certificate without another metadata transport.</summary>
    [Fact]
    public async Task MatchingIndexesDoNotIntroduceAnInputCatalogRoundtrip()
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        SafeMigrationOperation[] operations = [new(new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ix_parent", "live_parent", [new ExpectedIndexKeyDefinition(column: "id")])),
            SafeMigrationPolicy.ThrowIfDifferent)];

        SafeMigrationProviderAnalysis[] results = [Live(SafeMigrationObservedState.Matching)];
        await using var connection = new CatalogStatementTestConnection(true, Results);

        // Act
        await analyzer.PopulateLivePrerequisiteEvidenceAsync(connection, operations, results,
            71, CancellationToken.None);

        // Assert
        Assert.Empty(connection.Submissions);
    }

    /// <summary>A prior typed drop captures input eligibility without changing normal Matching semantics.</summary>
    /// <param name="kind">The earlier operation which can end the current owner or index lifetime.</param>
    /// <param name="supported">Whether the independently captured native input is eligible.</param>
    [Theory]
    [InlineData("index", true)]
    [InlineData("table", true)]
    [InlineData("column", true)]
    [InlineData("schema", true)]
    [InlineData("index", false)]
    public async Task MatchingIndexReplacementConsumesOnlyCapturedNativeInput(string kind, bool supported)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        SafeMigrationIntent drop = kind switch
        {
            "index" => new DropIndexIntent("ix_parent", "live_parent", "public"),
            "table" => new DropTableIntent("live_parent", "public"),
            "column" => new DropColumnIntent("id", "live_parent", "public"),
            "schema" => new DropSchemaIntent("public"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        SafeMigrationOperation[] operations = [new(drop, SafeMigrationPolicy.ThrowIfDifferent),
            new(new EnsureIndexIntent(new ExpectedIndexDefinition("ix_parent", "live_parent",
                [new ExpectedIndexKeyDefinition(column: "id")], "public")),
                SafeMigrationPolicy.ThrowIfDifferent)];

        SafeMigrationProviderAnalysis[] results = [Live(SafeMigrationObservedState.Matching),
            Live(SafeMigrationObservedState.Matching)];

        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            if (!sql.Contains("WITH RECURSIVE domain_types", StringComparison.Ordinal))
            {
                return Results(sql, parameters);
            }

            var table = new System.Data.DataTable();
            table.Columns.Add("ordinal", typeof(int));
            table.Columns.Add("proof", typeof(bool));
            foreach (System.Text.RegularExpressions.Match match in RequestOrdinals(sql))
            {
                table.Rows.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), supported);
            }

            return table;
        });

        // Act
        await analyzer.PopulateLivePrerequisiteEvidenceAsync(connection, operations, results,
            71, CancellationToken.None);

        // WHY: Core, not metadata capture, owns permission to accept the DROP.
        // Supplying its Missing result exercises only this provider certificate.
        var projected = analyzer.ValidateProjectedOperation(operations[1], Live(SafeMigrationObservedState.Missing),
            new AddedColumnSource());

        // Assert
        Assert.Equal(2, connection.Submissions.Count);
        Assert.Equal(supported ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.Unsupported,
            projected.ObservedState);
    }

    /// <summary>Requires complete outer stream identity before publishing ordinary type declarations.</summary>
    /// <param name="shape">The complete stream control or its deliberately incomplete identity view.</param>
    [Theory]
    [InlineData("complete")]
    [InlineData("short")]
    [InlineData("extra")]
    [InlineData("identity")]
    public async Task ProviderColumnProposalsRequireCompleteOuterStreamIdentity(string shape)
    {
        // Arrange
        using var context = Context();
        var analyzer = Analyzer(context);
        var index = new SafeMigrationOperation(new EnsureIndexIntent(new ExpectedIndexDefinition(
            "ix_child", "child_0", [new ExpectedIndexKeyDefinition(column: "parent_id")])),
            SafeMigrationPolicy.ThrowIfDifferent);

        var tail = new SafeMigrationOperation(new EnsureSchemaIntent("unrelated"),
            SafeMigrationPolicy.ThrowIfDifferent);

        SafeMigrationOperation[] operations = [index, tail];
        SafeMigrationProviderAnalysis[] results = [Live(SafeMigrationObservedState.PrerequisiteMissing),
            Live(SafeMigrationObservedState.Matching)];

        var ordinary = new AddColumnOperation
        {
            Table = "child_0", Name = "parent_id", ClrType = typeof(Guid), ColumnType = "uuid", IsNullable = true,
        };

        MigrationOperation[] stream = shape switch
        {
            "complete" => [ordinary, index, tail],
            "short" => [ordinary, index],
            "extra" => [ordinary, index, tail, tail],
            "identity" => [ordinary, new SafeMigrationOperation(index.Intent, index.Policy), tail],
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        // WHY: This protocol test injects only the already existing private
        // invocation view, not a public bypass or a relaxed projection source.
        var field = typeof(PostgreSqlSafeMigrationProviderAnalyzer).GetField("_analysisOperationStream",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The invocation identity field was not found.");

        field.SetValue(analyzer, stream);
        await using var connection = new CatalogStatementTestConnection(true, (sql, parameters) =>
        {
            if (!sql.Contains("WITH RECURSIVE domain_types", StringComparison.Ordinal))
            {
                return Results(sql, parameters);
            }

            var table = new System.Data.DataTable();
            table.Columns.Add("ordinal", typeof(int));
            table.Columns.Add("proof", typeof(bool));
            foreach (System.Text.RegularExpressions.Match match in RequestOrdinals(sql))
            {
                table.Rows.Add(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), true);
            }

            return table;
        });

        // Act
        await analyzer.PopulateLivePrerequisiteEvidenceAsync(connection, operations, results,
            71, CancellationToken.None);

        var projected = analyzer.ValidateProjectedOperation(index, Live(SafeMigrationObservedState.Missing),
            new AddedColumnSource());

        // Assert
        Assert.Equal(shape == "complete" ? SafeMigrationObservedState.Missing
            : SafeMigrationObservedState.PrerequisiteMissing, projected.ObservedState);
    }

    /// <summary>Creates an offline provider context without connecting to an application database.</summary>
    private static SafeMigrationDbContext Context()
        => new("Host=localhost;Database=live_prerequisite_evidence;Username=unused;Password=unused");

    /// <summary>Obtains the actual registered provider rather than constructing a relaxed reader test double.</summary>
    private static PostgreSqlSafeMigrationProviderAnalyzer Analyzer(DbContext context)
        => Assert.IsType<PostgreSqlSafeMigrationProviderAnalyzer>(context.GetService<ISafeMigrationProviderAnalyzer>());

    /// <summary>Builds repeated or distinct sparse FK requests with exactly one earlier nullable addition.</summary>
    private static SafeMigrationOperation[] Operations(int owners, int repetitions = 1)
    {
        var result = new List<SafeMigrationOperation>();
        for (var owner = 0; owner < owners; owner++)
        {
            var table = "child_" + owner.ToString(CultureInfo.InvariantCulture);
            result.Add(new SafeMigrationOperation(new EnsureColumnIntent(table,
                new ExpectedColumnDefinition("parent_id", typeof(Guid), true, "uuid")),
                SafeMigrationPolicy.ThrowIfDifferent));

            for (var repetition = 0; repetition < repetitions; repetition++)
            {
                result.Add(new SafeMigrationOperation(new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition(
                    "fk_" + table, table, ["parent_id"], "live_parent", ["id"])),
                    SafeMigrationPolicy.ThrowIfDifferent));
            }
        }

        return result.ToArray();
    }

    /// <summary>Separates the missing ADD from FK prerequisite rejection without inventing a table model.</summary>
    private static SafeMigrationProviderAnalysis[] Analyses(IReadOnlyList<SafeMigrationOperation> operations)
        => operations.Select(static operation => Live(operation.Intent is EnsureColumnIntent
            ? SafeMigrationObservedState.Missing : SafeMigrationObservedState.PrerequisiteMissing)).ToArray();

    /// <summary>Creates immutable live classification inputs for the metadata-only phase.</summary>
    private static SafeMigrationProviderAnalysis Live(SafeMigrationObservedState state)
        => new(state, SafeMigrationRepairCapability.None, false, "test");

    /// <summary>Reads only explicit top-level request ordinals, never nested catalog SELECTs.</summary>
    private static System.Text.RegularExpressions.MatchCollection RequestOrdinals(string sql)
        => System.Text.RegularExpressions.Regex.Matches(sql, @"SELECT (\d+),");

    /// <summary>Returns native-shaped typed evidence with a separately named constraint backing index.</summary>
    private static System.Data.DataTable Results(
        string sql,
        IReadOnlyList<System.Data.Common.DbParameter> parameters
    )
    {
        var table = new System.Data.DataTable();
        table.Columns.Add("ordinal", typeof(int));
        for (var field = 1; field < 9; field++)
        {
            table.Columns.Add("field_" + field, typeof(object));
        }

        var ordinals = RequestOrdinals(sql);
        for (var index = 0; index < ordinals.Count; index++)
        {
            var match = ordinals[index];
            var ordinal = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            var end = index + 1 < ordinals.Count ? ordinals[index + 1].Index : sql.Length;
            var selection = sql.AsSpan(match.Index, end - match.Index);
            if (selection.Contains("keys.kind", StringComparison.Ordinal))
            {
                table.Rows.Add(ordinal, true, 2000L, 1000L, "p", "pk_native", "ix_native", DBNull.Value, DBNull.Value);
                table.Rows.Add(ordinal, true, 2000L, 1000L, "i", "ux_native", DBNull.Value, DBNull.Value, DBNull.Value);

                continue;
            }

            var absent = selection.Contains("a.attname =", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.Match(selection.ToString(),
                    @"a\.attname = (@doka_sm_p\d+)") is { Success: true } column
                && parameters.Single(parameter => parameter.ParameterName == column.Groups[1].Value).Value
                    is "parent_id";

            table.Rows.Add(ordinal, !absent, absent ? DBNull.Value : 2950L,
                absent ? DBNull.Value : 0L, DBNull.Value, DBNull.Value, DBNull.Value, DBNull.Value,
                absent ? DBNull.Value : true);
        }

        return table;
    }

    /// <summary>Exposes only the accepted new endpoint; its principal remains entirely live-owned.</summary>
    private sealed class AddedColumnSource : ISafeMigrationProjectedColumnSource
    {
        /// <inheritdoc />
        public bool TryGetProjectedColumn(
            string table,
            string? schema,
            string column,
            [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ExpectedColumnDefinition? definition
        )
        {
            definition = column == "parent_id"
                ? new ExpectedColumnDefinition(column, typeof(Guid), true, "uuid") : null;

            return definition is not null;
        }
    }
}
