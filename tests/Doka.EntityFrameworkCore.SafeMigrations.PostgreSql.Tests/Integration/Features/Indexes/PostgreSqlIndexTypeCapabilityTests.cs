namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Constrains native index inputs across live, sparse and newly authored column lifetimes.</summary>
public sealed class PostgreSqlIndexTypeCapabilityTests : PostgreSqlIntegrationTestBase
{
    /// <summary>Uses the same isolated native fixture as the provider's other integration contracts.</summary>
    /// <param name="fixture">The owned PostgreSQL fixture, never an application database.</param>
    public PostgreSqlIndexTypeCapabilityTests(PostgreSqlContainerFixture fixture) : base(fixture) { }

    /// <summary>Safe index eligibility neither ignores earlier EF declarations nor borrows from future ones.</summary>
    /// <param name="shape">The earlier accepted native declaration or deliberately unproven control.</param>
    [Theory]
    [InlineData("add")]
    [InlineData("alter")]
    [InlineData("json")]
    [InlineData("future")]
    [InlineData("unmapped")]
    public async Task OrdinaryDeclarationsRetainCurrentNativeIndexBinding(string shape)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE native_members (id integer, label varchar(8)); "
            + "INSERT INTO native_members VALUES (1, 'value');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var member = shape == "alter" ? "label" : "added";
        if (shape == "future")
        {
            builder.CreateIndexIfNotExists("ix_native_members", "native_members", [member]);
        }

        if (shape == "alter")
        {
            builder.AlterColumn<string>("label", "native_members", type: "varchar(32)", maxLength: 32,
                nullable: true, oldClrType: typeof(string), oldType: "varchar(8)", oldMaxLength: 8,
                oldNullable: true);
        }
        else if (shape == "json")
        {
            builder.AddColumn<string>(member, "native_members", type: "json", nullable: true);
        }
        else if (shape == "unmapped")
        {
            builder.AddColumn<Guid>(member, "native_members", type: "integer", nullable: true);
        }
        else
        {
            builder.AddColumn<int>(member, "native_members", type: "integer", nullable: true);
        }

        if (shape != "future")
        {
            builder.CreateIndexIfNotExists("ix_native_members", "native_members", [member]);
        }

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("ordinary-current-index-binding"), CancellationToken.None);

        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;
        if (shape is "add" or "alter")
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("ordinary-current-index-binding-final"), CancellationToken.None);

            // WHY: An ordinary EF ADD is deliberately not made idempotent by
            // this fix. Replay exercises the safe index's independent native
            // certificate, not a rewritten or repeated ordinary migration.
            replay = await runner.AnalyzeAsync(context, builder.Operations.OfType<SafeMigrationOperation>().ToArray(),
                new SafeMigrationRunOptions("ordinary-current-index-binding-replay"), CancellationToken.None);
        }

        // Assert
        var assessment = Assert.Single(report.Assessments, static item =>
            item.OperationKind == SafeMigrationOperationKind.EnsureIndex);

        if (shape is "add" or "alter")
        {
            Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, report.Status);
            Assert.Equal(SafeMigrationAction.Apply, assessment.Action);
            Assert.Equal(SafeMigrationReportStatus.ReadyWithProviderOperations, postflight!.Status);
            Assert.Equal(SafeMigrationAction.NoOp, Assert.Single(replay!.Assessments).Action);
        }
        else
        {
            Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
            Assert.Equal(shape == "json" ? SafeMigrationAction.RejectUnsupported
                : SafeMigrationAction.RejectPrerequisiteMissing, assessment.Action);
        }
    }

    /// <summary>Default btree eligibility comes from the native type/opclass contract, not column presence.</summary>
    /// <param name="path">The live, sparse or fully authored prerequisite path.</param>
    /// <param name="jsonb">Whether the native input has a default btree operator class.</param>
    [Theory]
    [InlineData("live", false)]
    [InlineData("sparse", false)]
    [InlineData("authored", false)]
    [InlineData("live", true)]
    [InlineData("sparse", true)]
    [InlineData("authored", true)]
    public async Task DefaultBtreeRequiresNativeInputOperatorClass(string path, bool jsonb)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var type = jsonb ? "jsonb" : "json";
        if (path != "authored")
        {
            await ExecuteSqlAsync(connectionString, $"CREATE TABLE native_members (old_value {type} NOT NULL"
                + (path == "live" ? ", added integer NULL" : string.Empty) + ");");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (path == "authored")
        {
            builder.EnsureTable(new ExpectedTableDefinition("native_members",
                [new ExpectedColumnDefinition("old_value", typeof(string), isNullable: false, storeType: type),
                    new ExpectedColumnDefinition("added", typeof(int), isNullable: true, storeType: "integer")]),
                SafeMigrationTableMode.StrictDefinition, SafeMigrationPolicy.ThrowIfDifferent);
        }
        else if (path == "sparse")
        {
            builder.AddColumnIfNotExists<int>("added", "native_members", type: "integer", nullable: true);
        }

        builder.CreateIndexIfNotExists("ix_native_members", "native_members", ["old_value", "added"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-default-btree-input"), CancellationToken.None);

        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;
        PostgresException? nativeError = null;
        if (jsonb)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-default-btree-input-final"), CancellationToken.None);

            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-default-btree-input-replay"), CancellationToken.None);
        }
        else
        {
            // WHY: The native control runs only on an owned disposable empty
            // fixture. Analysis must reject before delegating this known
            // incompatible operator-class input to native CREATE INDEX.
            if (path == "authored")
            {
                await ExecuteSqlAsync(connectionString,
                    $"CREATE TABLE native_members (old_value {type} NOT NULL, added integer NULL);");
            }
            else if (path == "sparse")
            {
                await ExecuteSqlAsync(connectionString, "ALTER TABLE native_members ADD COLUMN added integer NULL;");
            }

            nativeError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(connectionString,
                "CREATE INDEX ix_native_members ON native_members (old_value, added);"));
        }

        // Assert
        Assert.Equal(jsonb ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        if (jsonb)
        {
            Assert.All(report.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight!.Status);
            Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
            Assert.Equal(SafeMigrationReportStatus.Ready, replay!.Status);
            Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        }
        else
        {
            Assert.Equal("42704", nativeError!.SqlState);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[^1].Action);
        }
    }

    /// <summary>Native default selection retains binary, domain and polymorphic input semantics.</summary>
    /// <param name="shape">The native physical type family whose actual catalog identity is required.</param>
    [Theory]
    [InlineData("varchar")]
    [InlineData("domain")]
    [InlineData("array")]
    [InlineData("enum")]
    [InlineData("composite")]
    [InlineData("range")]
    [InlineData("multirange")]
    [InlineData("json-array-empty")]
    public async Task DefaultBindingsRetainNativeDomainBinaryAndPolymorphicSelection(string shape)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var setup = shape switch
        {
            "domain" => "CREATE DOMAIN label_domain AS varchar(32); ",
            "enum" => "CREATE TYPE native_mood AS ENUM ('low', 'high'); ",
            "composite" => "CREATE TYPE native_pair AS (number integer, label text); ",
            _ => string.Empty,
        };

        var type = shape switch
        {
            "varchar" => "varchar(32)", "domain" => "label_domain", "array" => "integer[]",
            "enum" => "native_mood", "composite" => "native_pair", "range" => "int4range",
            "multirange" => "int4multirange", "json-array-empty" => "json[]",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var values = shape switch
        {
            "varchar" or "domain" => "('a'), ('b')", "array" => "('{1}'), ('{2}')",
            "enum" => "('low'), ('high')", "composite" => "(ROW(1, 'a')), (ROW(2, 'b'))",
            "range" => "('[1,2)'), ('[3,4)')", "multirange" => "('{[1,2)}'), ('{[3,4)}')",
            "json-array-empty" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        // WHY: JSON[] has a valid native array opclass, but JSON elements have
        // no comparison support. Empty creation proves only native selection,
        // not arbitrary row/comparator execution; other shapes retain real rows.
        await ExecuteSqlAsync(connectionString, setup + $"CREATE TABLE native_members (old_value {type} NOT NULL); "
            + (values is null ? string.Empty : "INSERT INTO native_members VALUES " + values + ";"));

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "native_members", type: "integer", nullable: true);
        builder.CreateIndexIfNotExists("ix_native_members", "native_members", ["old_value", "added"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-default-input-shapes"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-default-input-shapes-final"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-default-input-shapes-replay"), CancellationToken.None);

        var rows = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM native_members;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
        Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        Assert.Equal(values is null ? 0 : 2, rows);
    }

    /// <summary>Explicit opclasses use directional binary compatibility and exact namespace/method identity.</summary>
    /// <param name="compatible">Whether VARCHAR is binary-coercible to the explicit native opclass input.</param>
    /// <param name="qualified">Whether the class uses the explicit pg_catalog namespace.</param>
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task ExplicitOperatorClassRequiresDirectionalNativeCompatibility(bool compatible, bool qualified)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE native_members (value varchar(32) NOT NULL); INSERT INTO native_members VALUES ('a');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var opclass = (qualified ? "pg_catalog." : string.Empty) + (compatible ? "text_ops" : "int4_ops");
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_native_members", "native_members",
            [new ExpectedIndexKeyDefinition(column: "value", operatorClass: opclass)]),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-explicit-input"), CancellationToken.None);

        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;
        PostgresException? nativeError = null;
        if (compatible)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-explicit-input-final"), CancellationToken.None);

            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-explicit-input-replay"), CancellationToken.None);
        }
        else
        {
            nativeError = await Assert.ThrowsAsync<PostgresException>(() => ExecuteSqlAsync(connectionString,
                "CREATE INDEX ix_native_members ON native_members (value " + opclass + ");"));
        }

        // Assert
        Assert.Equal(compatible ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        if (compatible)
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight!.Status);
            Assert.All(replay!.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        }
        else
        {
            Assert.Equal("42804", nativeError!.SqlState);
            Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        }
    }

    /// <summary>INCLUDE payloads are not opclass inputs; computed keys retain their actual result contract.</summary>
    /// <param name="shape">The independently supported native key or payload shape.</param>
    [Theory]
    [InlineData("include-json")]
    [InlineData("identifier-json")]
    [InlineData("identifier-integer")]
    [InlineData("identifier-qualified-integer")]
    [InlineData("identifier-schema-qualified-integer")]
    [InlineData("cast-json")]
    [InlineData("cast-integer")]
    [InlineData("lower-text")]
    public async Task PhysicalAliasesIncludesAndComputedResultsRetainDistinctContracts(string shape)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE native_members (id integer NOT NULL, "
            + "payload json NULL, label text NOT NULL); INSERT INTO native_members VALUES (1, '{}', 'a');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var key = shape switch
        {
            "include-json" => new ExpectedIndexKeyDefinition(column: "id"),
            "identifier-json" => new ExpectedIndexKeyDefinition(
                structuredExpression: SafeMigrationSql.Identifier("payload")),
            "identifier-integer" => new ExpectedIndexKeyDefinition(
                structuredExpression: SafeMigrationSql.Identifier("id")),
            "identifier-qualified-integer" => new ExpectedIndexKeyDefinition(
                structuredExpression: SafeMigrationSql.Identifier("native_members", "id")),
            "identifier-schema-qualified-integer" => new ExpectedIndexKeyDefinition(
                structuredExpression: SafeMigrationSql.Identifier("public", "native_members", "id")),
            "cast-json" => new ExpectedIndexKeyDefinition(structuredExpression:
                SafeMigrationSql.Cast(SafeMigrationSql.Identifier("payload"), "text")),
            "cast-integer" => new ExpectedIndexKeyDefinition(structuredExpression:
                SafeMigrationSql.Cast(SafeMigrationSql.Identifier("id"), "bigint")),
            "lower-text" => new ExpectedIndexKeyDefinition(structuredExpression:
                SafeMigrationSql.Function("LOWER", SafeMigrationSql.Identifier("label"))),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        builder.EnsureIndex(new ExpectedIndexDefinition("ix_native_members", "native_members", [key],
            includedColumns: shape == "include-json" ? ["payload"] : null), SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-key-result-boundary"), CancellationToken.None);

        SafeMigrationRunReport? postflight = null;
        SafeMigrationRunReport? replay = null;
        if (shape != "identifier-json")
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
            postflight = await runner.VerifyAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-key-result-boundary-final"), CancellationToken.None);

            replay = await runner.AnalyzeAsync(context, builder.Operations,
                new SafeMigrationRunOptions("native-key-result-boundary-replay"), CancellationToken.None);
        }

        // Assert
        Assert.Equal(shape == "identifier-json" ? SafeMigrationReportStatus.Blocked : SafeMigrationReportStatus.Ready,
            report.Status);

        if (shape == "identifier-json")
        {
            Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
        }
        else
        {
            Assert.Equal(SafeMigrationReportStatus.Ready, postflight!.Status);
            Assert.All(replay!.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
        }
    }

    /// <summary>A physically folded identifier retains both its authored owner and exact member.</summary>
    /// <param name="mismatch">The independently mismatched physical identity.</param>
    [Theory]
    [InlineData("owner")]
    [InlineData("schema")]
    [InlineData("member")]
    public async Task NativeIdentifierAliasRetainsQualifiedPhysicalIdentity(string mismatch)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE native_members (id integer, other integer); "
            + "CREATE INDEX ix_native_members ON native_members (id);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_native_members", "native_members",
            [new ExpectedIndexKeyDefinition(structuredExpression: SafeMigrationSql.Identifier(
                mismatch == "schema" ? "unrelated_schema" : "public",
                mismatch == "owner" ? "unrelated_members" : "native_members",
                mismatch == "member" ? "other" : "id"))]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-identifier-identity"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.NotEqual(SafeMigrationAction.NoOp, Assert.Single(report.Assessments).Action);
    }

    /// <summary>A unique JSON input is rejected before PostgreSQL can bind the duplicate-data GROUP BY.</summary>
    [Fact]
    public async Task UniqueJsonInputIsRejectedBeforeDataProbeBinding()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE native_members (payload json); "
            + "INSERT INTO native_members VALUES ('{}');");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.CreateIndexIfNotExists("ix_native_members", "native_members", ["payload"], unique: true);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-unique-json-input"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
    }

    /// <summary>A pretty native CAST candidate never relaxes its exact operand or target identity.</summary>
    /// <param name="mismatch">The independently mismatched native expression component.</param>
    [Theory]
    [InlineData("target")]
    [InlineData("operand")]
    public async Task NativeCastCandidatePreservesOperandAndTarget(string mismatch)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE native_members (id integer, other integer); "
            + "CREATE INDEX ix_native_members ON native_members ((id::bigint));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_native_members", "native_members",
            [new ExpectedIndexKeyDefinition(structuredExpression: SafeMigrationSql.Cast(
                SafeMigrationSql.Identifier(mismatch == "operand" ? "other" : "id"),
                mismatch == "target" ? "text" : "bigint"))]), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-cast-identity"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
    }

    /// <summary>Known method flags reject impossible layouts before any CREATE or data probe.</summary>
    /// <param name="requirement">The exact independently authored native flag.</param>
    [Theory]
    [InlineData("unique")]
    [InlineData("multiple")]
    [InlineData("include")]
    [InlineData("order")]
    public async Task NativeHashFlagsRejectUnsupportedLayouts(string requirement)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE native_members (id integer, added integer);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        var key = new ExpectedIndexKeyDefinition(column: "id", sortOrder: requirement == "order"
            ? SafeMigrationIndexSortOrder.Descending : SafeMigrationIndexSortOrder.ProviderDefault);

        builder.EnsureIndex(new ExpectedIndexDefinition("ix_native_members", "native_members",
            requirement == "multiple" ? [key, new ExpectedIndexKeyDefinition(column: "added")] : [key],
            unique: requirement == "unique", method: "hash",
            includedColumns: requirement == "include" ? ["added"] : null), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-method-flags"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, Assert.Single(report.Assessments).Action);
    }

    /// <summary>Unknown extension handler identities cannot be inferred from a familiar body or spelling.</summary>
    [Fact]
    public async Task UnprovenMethodFlagsDoNotInvokeCustomHandler()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE FUNCTION custom_btree(internal) RETURNS index_am_handler "
            + "LANGUAGE internal AS 'bthandler'; CREATE ACCESS METHOD custom_btree_alias TYPE INDEX "
            + "HANDLER custom_btree; CREATE TABLE native_members (id integer, added integer);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_native_members", "native_members",
            [new ExpectedIndexKeyDefinition(column: "id"), new ExpectedIndexKeyDefinition(column: "added")],
            method: "custom_btree_alias"), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-custom-handler-unproven"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectPrerequisiteMissing, Assert.Single(report.Assessments).Action);
    }

    /// <summary>A custom method alias uses its actual opclasses and the exact builtin handler's flags.</summary>
    [Fact]
    public async Task CustomMethodAliasRetainsActualMethodAndBuiltinHandlerIdentity()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE ACCESS METHOD native_hash_alias TYPE INDEX "
            + "HANDLER pg_catalog.hashhandler; CREATE OPERATOR CLASS alias_integer_ops DEFAULT FOR TYPE integer "
            + "USING native_hash_alias AS OPERATOR 1 =(integer, integer), FUNCTION 1 hashint4(integer); "
            + "CREATE TABLE native_members (id integer NOT NULL); INSERT INTO native_members VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureIndex(new ExpectedIndexDefinition("ix_native_members", "native_members",
            [new ExpectedIndexKeyDefinition(column: "id")], method: "native_hash_alias"),
            SafeMigrationPolicy.ThrowIfDifferent);

        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-handler-alias"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-handler-alias-final"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("native-handler-alias-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
    }
}
