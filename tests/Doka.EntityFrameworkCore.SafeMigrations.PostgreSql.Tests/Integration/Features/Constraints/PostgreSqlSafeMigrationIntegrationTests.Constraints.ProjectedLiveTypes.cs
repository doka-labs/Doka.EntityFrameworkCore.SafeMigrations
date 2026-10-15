namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql.Tests;

/// <summary>Runs the ConstraintsProjectedLiveTypes PostgreSQL feature cases independently.</summary>
public sealed class PostgreSqlConstraintsProjectedLiveTypesIntegrationTests : PostgreSqlSafeMigrationIntegrationTests
{
    /// <summary>Uses the assembly-owned container for this feature.</summary>
    /// <param name="fixture">The shared PostgreSQL container fixture.</param>
    public PostgreSqlConstraintsProjectedLiveTypesIntegrationTests(
        PostgreSqlContainerFixture fixture
    ) : base(fixture) { }

    /// <summary>Sparse CHECK typing preserves native functions, explicit casts and common-type expressions.</summary>
    /// <param name="shape">The supported expression family whose binding must remain valid.</param>
    [Theory]
    [InlineData("lower")]
    [InlineData("lower-varchar")]
    [InlineData("lower-irrelevant-variadic")]
    [InlineData("literal-cast")]
    [InlineData("integer-cast")]
    [InlineData("coalesce")]
    [InlineData("nullif")]
    [InlineData("between")]
    [InlineData("negate")]
    [InlineData("not")]
    public async Task EmptyMixedCheckRetainsValidNativeExpressionBinding(string shape)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var existingType = shape is "lower-varchar" or "lower-irrelevant-variadic" ? "varchar(32)" : "text";
        await ExecuteSqlAsync(connectionString, $"CREATE TABLE mixed_columns (id {existingType} NOT NULL);");

        if (shape == "lower-irrelevant-variadic")
        {
            // WHY: Scalar variadic calls require at least one variadic value.
            // This two-input overload cannot replace builtin LOWER(varchar)
            // merely because its variadic array could be empty when explicit.
            await ExecuteSqlAsync(connectionString,
                "CREATE FUNCTION public.lower(varchar, VARIADIC integer[]) RETURNS text "
                + "LANGUAGE sql IMMUTABLE AS 'SELECT upper($1::text)';");
        }

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        var operand = shape switch
        {
            "lower" or "lower-varchar" or "lower-irrelevant-variadic"
                => SafeMigrationSql.Function("LOWER", SafeMigrationSql.Identifier("id")),
            "literal-cast" => SqlBinary(SafeMigrationSql.Identifier("added"), SafeMigrationSqlBinaryOperator.Add,
                SafeMigrationSql.Cast(SafeMigrationSql.Literal("1"), "integer")),
            "integer-cast" => SafeMigrationSql.Cast(SafeMigrationSql.Identifier("added"), "bigint"),
            "coalesce" => SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Identifier("added"),
                SafeMigrationSql.Literal(0)),
            "nullif" => SafeMigrationSql.Function("NULLIF", SafeMigrationSql.Identifier("added"),
                SafeMigrationSql.Literal(0)),
            "between" => SafeMigrationSql.Between(SafeMigrationSql.Identifier("added"),
                SafeMigrationSql.Literal(0), SafeMigrationSql.Literal(10)),
            "negate" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Negate,
                SafeMigrationSql.Identifier("added")),
            "not" => SafeMigrationSql.Unary(SafeMigrationSqlUnaryOperator.Not, SqlColumnAndInt("added",
                SafeMigrationSqlBinaryOperator.GreaterThan, 0)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var usesLower = shape is "lower" or "lower-varchar" or "lower-irrelevant-variadic";
        var comparison = usesLower
            ? SafeMigrationSql.Literal(string.Empty)
            : SafeMigrationSql.Literal(0);

        var condition = shape is "between" or "not" ? operand
            : SqlBinary(operand, SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, comparison);

        var expression = SqlBinary(condition, SafeMigrationSqlBinaryOperator.And,
            usesLower ? SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)
                : SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("id")));

        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression(
            "ck_mixed_valid", "mixed_columns", expression), SafeMigrationPolicy.ThrowIfDifferent);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var report = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-valid-binding"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-valid-binding-final"), CancellationToken.None);

        var replay = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-valid-binding-replay"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
        Assert.All(postflight.Assessments, static item => Assert.True(item.PostconditionSatisfied));
        Assert.Equal(SafeMigrationReportStatus.Ready, replay.Status);
        Assert.All(replay.Assessments, static item => Assert.Equal(SafeMigrationAction.NoOp, item.Action));
    }

    /// <summary>A nonexecuting existence-only proposal cannot replace the accepted native column type.</summary>
    /// <param name="policy">The later wrong proposal's compatibility policy.</param>
    [Theory]
    [InlineData(SafeMigrationPolicy.ExistenceOnly)]
    [InlineData(SafeMigrationPolicy.ThrowIfDifferent)]
    public async Task ForeignKeyNewNullableColumnUsesAcceptedTypeInsteadOfLaterProposal(SafeMigrationPolicy policy)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY); INSERT INTO live_child VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("parent_id", "live_child", type: "integer", nullable: true);
        builder.EnsureColumn("live_child", new ExpectedColumnDefinition("parent_id", typeof(string),
            isNullable: true, storeType: "text"), policy);

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-accepted-type"), CancellationToken.None);

        if (policy == SafeMigrationPolicy.ExistenceOnly)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(policy == SafeMigrationPolicy.ExistenceOnly
            ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(policy == SafeMigrationPolicy.ExistenceOnly
            ? SafeMigrationAction.NoOp : SafeMigrationAction.RejectDifferent, report.Assessments[1].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
    }

    /// <summary>A same-name CHECK prevents an earlier FK drop from authorizing an occupied symbol.</summary>
    /// <param name="dropReplacement">Whether the accepted replacement is independently removed before the FK.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignKeyNewNullableColumnDoesNotReuseReoccupiedConstraintName(bool dropReplacement)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id integer NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL PRIMARY KEY, old_parent_id integer NULL, "
            + "CONSTRAINT fk_live_child_parent FOREIGN KEY (old_parent_id) REFERENCES live_parent (id));");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.DropForeignKeyIfExists("fk_live_child_parent", "live_child");
        builder.AddColumnIfNotExists<int>("parent_id", "live_child", type: "integer", nullable: true);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression(
            "fk_live_child_parent", "live_child", SqlColumnAndInt("id",
                SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)), SafeMigrationPolicy.ThrowIfDifferent);

        if (dropReplacement)
        {
            builder.DropCheckConstraintIfExists("fk_live_child_parent", "live_child");
        }

        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"],
            "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-symbol-reoccupied"), CancellationToken.None);

        if (dropReplacement)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(dropReplacement ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);

        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[2].Action);
        Assert.Equal(dropReplacement ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[^1].Action);
    }

    /// <summary>A new owner and separate nullable addition retain the independently live parent proof.</summary>
    [Fact]
    public async Task ForeignKeyNewTableThenSeparateColumnUsesUnchangedLivePrincipal()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE live_parent (id integer NOT NULL PRIMARY KEY);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.ConvergeTable(new ExpectedTableDefinition("new_child",
            [new ExpectedColumnDefinition("id", typeof(int), isNullable: false, storeType: "integer")],
            primaryKey: new ExpectedPrimaryKeyDefinition("pk_new_child", "new_child", ["id"])));

        builder.AddColumnIfNotExists<int>("parent_id", "new_child", type: "integer", nullable: true);
        builder.CreateIndexIfNotExistsFromModel("ix_new_child_parent", "new_child", "parent_id");
        builder.AddForeignKeyIfNotExists("fk_new_child_parent", "new_child", ["parent_id"],
            "live_parent", ["id"]);
        var runner = context.GetService<ISafeMigrationRunner>();

        // Act
        var preflight = await runner.AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-created-child-separate-column"), CancellationToken.None);

        preflight.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var postflight = await runner.VerifyAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-created-child-separate-column-final"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, preflight.Status);
        Assert.Equal(SafeMigrationAction.Apply, preflight.Assessments[0].Action);
        Assert.All(preflight.Assessments.TakeLast(3),
            static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));

        Assert.Equal(SafeMigrationReportStatus.Ready, postflight.Status);
    }

    /// <summary>Functions and explicit casts cannot hide an unsupported scalar operand conversion.</summary>
    /// <param name="shape">The incompatible builtin signature family.</param>
    [Theory]
    [InlineData("function")]
    [InlineData("cast")]
    [InlineData("coalesce")]
    public async Task EmptyMixedCheckRejectsInvalidNativeExpressionBinding(string shape)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE mixed_columns (id uuid NOT NULL);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        var operand = shape switch
        {
            "function" => SafeMigrationSql.Function("LENGTH", SafeMigrationSql.Identifier("added")),
            "cast" => SafeMigrationSql.Cast(SafeMigrationSql.Identifier("id"), "integer"),
            "coalesce" => SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Identifier("id"),
                SafeMigrationSql.Identifier("added")),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var expression = SqlBinary(SqlBinary(operand,
                SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, SafeMigrationSql.Literal(0)),
            SafeMigrationSqlBinaryOperator.And,
            SqlBinary(SafeMigrationSql.IsNotNull(SafeMigrationSql.Identifier("id")),
                SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0)));

        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression(
            "ck_mixed_invalid", "mixed_columns", expression),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-invalid-binding"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.Apply, report.Assessments[0].Action);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Equal("structured_check_operand_type", report.Assessments[1].AnalysisCode);
    }

    /// <summary>Native candidates retain literal values, function identity and different CAST targets.</summary>
    /// <param name="shape">The predicate facet changed from the independently created native CHECK.</param>
    [Theory]
    [InlineData("lower")]
    [InlineData("literal-cast")]
    [InlineData("integer-cast")]
    [InlineData("coalesce")]
    public async Task CheckNativeDeparseDoesNotApproveDifferentPredicate(string shape)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var current = shape switch
        {
            "lower" => "lower(id) >= ''",
            "literal-cast" => "CAST('1' AS integer) >= 0",
            "integer-cast" => "CAST(added AS bigint) >= 0",
            "coalesce" => "COALESCE(added, 0) >= 0",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE mixed_columns (id text NOT NULL, added integer NOT NULL, "
            + "CONSTRAINT ck_mixed CHECK ((" + current + ") AND (added >= 0)));");

        await using var context = CreateContext(connectionString);
        var operand = shape switch
        {
            "lower" => SafeMigrationSql.Function("UPPER", SafeMigrationSql.Identifier("id")),
            "literal-cast" => SafeMigrationSql.Cast(SafeMigrationSql.Literal("2"), "integer"),
            "integer-cast" => SafeMigrationSql.Cast(SafeMigrationSql.Identifier("added"), "integer"),
            "coalesce" => SafeMigrationSql.Function("COALESCE", SafeMigrationSql.Identifier("added"),
                SafeMigrationSql.Literal(1)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        var expression = SqlBinary(SqlBinary(operand, SafeMigrationSqlBinaryOperator.GreaterThanOrEqual,
            shape == "lower" ? SafeMigrationSql.Literal(string.Empty) : SafeMigrationSql.Literal(0)),
            SafeMigrationSqlBinaryOperator.And,
            SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0));

        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_mixed",
            "mixed_columns", expression), SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("check-native-deparse-different"), CancellationToken.None);

        var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteOperationsAsync(context,
            builder.Operations));

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectDifferent, Assert.Single(report.Assessments).Action);
        Assert.Equal("P1001", exception.SqlState);
    }

    /// <summary>Unresolved binding retains its prerequisite blocker without invoking callbacks.</summary>
    /// <param name="shape">The native binding family not certified by the builtin scalar metadata bridge.</param>
    [Theory]
    [InlineData("domain")]
    [InlineData("polymorphic")]
    [InlineData("variadic")]
    [InlineData("opaque")]
    [InlineData("polymorphic-cast")]
    public async Task EmptyMixedCheckDoesNotApproveUnresolvedNativeBinding(string shape)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var setup = shape switch
        {
            "domain" => "CREATE DOMAIN owned_integer AS integer; CREATE TABLE mixed_columns (id owned_integer);",
            "polymorphic" or "polymorphic-cast" => "CREATE TABLE mixed_columns (id integer[]);",
            "variadic" => "CREATE TABLE mixed_columns (id integer);",
            "opaque" => "CREATE TABLE mixed_columns (id integer); CREATE TABLE callback_audit (id integer); "
                + "CREATE FUNCTION metadata_callback(integer) RETURNS boolean LANGUAGE plpgsql AS $$ "
                + "BEGIN INSERT INTO callback_audit VALUES (1); RETURN true; END $$;",
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        await ExecuteSqlAsync(connectionString, setup);
        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        var existing = shape switch
        {
            "domain" => SqlColumnAndInt("id", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0),
            "polymorphic" => SqlBinary(SafeMigrationSql.Function("ARRAY_LENGTH",
                    SafeMigrationSql.Identifier("id"), SafeMigrationSql.Literal(1)),
                SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, SafeMigrationSql.Literal(0)),
            "polymorphic-cast" => SqlBinary(SafeMigrationSql.Cast(SafeMigrationSql.Function("ARRAY_LENGTH",
                    SafeMigrationSql.Identifier("id"), SafeMigrationSql.Literal(1)), "integer"),
                SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, SafeMigrationSql.Literal(0)),
            "variadic" => SqlBinary(SafeMigrationSql.Function("NUM_NONNULLS",
                    SafeMigrationSql.Identifier("id"), SafeMigrationSql.Identifier("added")),
                SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, SafeMigrationSql.Literal(0)),
            "opaque" => SafeMigrationSql.ProviderFragment("npgsql_postgresql", "metadata_callback(id)"),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_unresolved",
            "mixed_columns", SqlBinary(existing, SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-unresolved-binding"), CancellationToken.None);

        var callbackCount = shape == "opaque"
            ? await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM callback_audit;") : 0;

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(shape == "opaque" ? SafeMigrationAction.RejectUnsupported
            : SafeMigrationAction.RejectPrerequisiteMissing, report.Assessments[1].Action);

        Assert.Equal(shape == "opaque" ? "opaque_sql_expression" : "projected_check_binding_unproven",
            report.Assessments[1].AnalysisCode);

        Assert.Equal(0, callbackCount);
    }

    /// <summary>A catalog-resolved user function's native signature is read without executing its body.</summary>
    [Fact]
    public async Task EmptyMixedCheckQualifiesUserFunctionWithoutInvokingIt()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE mixed_columns (id integer); CREATE TABLE callback_audit (id integer); "
            + "CREATE FUNCTION metadata_callback(integer) RETURNS boolean LANGUAGE plpgsql AS $$ "
            + "BEGIN INSERT INTO callback_audit VALUES (1); RETURN true; END $$;");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_callback",
            "mixed_columns", SqlBinary(SafeMigrationSql.Function("METADATA_CALLBACK",
                SafeMigrationSql.Identifier("id")), SafeMigrationSqlBinaryOperator.And,
                SqlColumnAndInt("added", SafeMigrationSqlBinaryOperator.GreaterThanOrEqual, 0))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-catalog-function"), CancellationToken.None);

        report.ThrowIfBlocked();
        await ExecuteOperationsAsync(context, builder.Operations);
        var callbackCount = await ScalarIntAsync(connectionString, "SELECT COUNT(*) FROM callback_audit;");

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Ready, report.Status);
        Assert.All(report.Assessments, static item => Assert.Equal(SafeMigrationAction.Apply, item.Action));
        Assert.Equal(0, callbackCount);
    }

    /// <summary>A fully resolved TEXT result cannot become a Boolean CHECK solely because its owner is empty.</summary>
    [Fact]
    public async Task EmptyMixedCheckRejectsResolvedNonBooleanPredicate()
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        await ExecuteSqlAsync(connectionString, "CREATE TABLE mixed_columns (id text);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        builder.AddColumnIfNotExists<int>("added", "mixed_columns", type: "integer", nullable: false);
        builder.EnsureCheckConstraint(ExpectedCheckConstraintDefinition.FromExpression("ck_not_boolean",
            "mixed_columns", SafeMigrationSql.Function("COALESCE",
                SafeMigrationSql.Function("LOWER", SafeMigrationSql.Identifier("id")),
                SafeMigrationSql.Cast(SafeMigrationSql.Identifier("added"), "text"))),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-check-nonboolean"), CancellationToken.None);

        // Assert
        Assert.Equal(SafeMigrationReportStatus.Blocked, report.Status);
        Assert.Equal(SafeMigrationAction.RejectUnsupported, report.Assessments[1].Action);
        Assert.Equal("structured_check_operand_type", report.Assessments[1].AnalysisCode);
    }

    /// <summary>Only an accepted owned DROP of the actual family releases an existing constraint symbol.</summary>
    /// <param name="kind">The independently observed native constraint family occupying the requested FK name.</param>
    /// <param name="correctFamily">Whether the stream uses the actual constraint's DROP family.</param>
    [Theory]
    [InlineData("primary", false)]
    [InlineData("primary", true)]
    [InlineData("unique", false)]
    [InlineData("unique", true)]
    [InlineData("check", false)]
    [InlineData("check", true)]
    public async Task ForeignKeyNewNullableColumnHonorsExactDroppedConstraintFamily(string kind, bool correctFamily)
    {
        // Arrange
        var connectionString = await CreateDatabaseAsync(CancellationToken.None);
        var constraint = kind switch
        {
            "primary" => "PRIMARY KEY (id)",
            "unique" => "UNIQUE (id)",
            "check" => "CHECK (id > 0)",
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await ExecuteSqlAsync(connectionString,
            "CREATE TABLE live_parent (id uuid NOT NULL PRIMARY KEY); "
            + "CREATE TABLE live_child (id integer NOT NULL, CONSTRAINT fk_live_child_parent "
            + constraint + "); INSERT INTO live_child VALUES (1);");

        await using var context = CreateContext(connectionString);
        var builder = new MigrationBuilder(context.Database.ProviderName!);
        if (!correctFamily)
        {
            builder.DropForeignKeyIfExists("fk_live_child_parent", "live_child");
        }
        else
        {
            switch (kind)
            {
                case "primary": builder.DropPrimaryKeyIfExists("fk_live_child_parent", "live_child"); break;
                case "unique": builder.DropUniqueConstraintIfExists("fk_live_child_parent", "live_child"); break;
                case "check": builder.DropCheckConstraintIfExists("fk_live_child_parent", "live_child"); break;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        builder.AddColumnIfNotExists<Guid>("parent_id", "live_child", type: "uuid", nullable: true);
        builder.AddForeignKeyIfNotExists("fk_live_child_parent", "live_child", ["parent_id"], "live_parent", ["id"]);

        // Act
        var report = await context.GetService<ISafeMigrationRunner>().AnalyzeAsync(context, builder.Operations,
            new SafeMigrationRunOptions("sparse-live-fk-owned-family-drop"), CancellationToken.None);

        if (correctFamily)
        {
            report.ThrowIfBlocked();
            await ExecuteOperationsAsync(context, builder.Operations);
        }

        // Assert
        Assert.Equal(correctFamily ? SafeMigrationReportStatus.Ready : SafeMigrationReportStatus.Blocked,
            report.Status);

        Assert.Equal(correctFamily ? SafeMigrationAction.Apply : SafeMigrationAction.NoOp,
            report.Assessments[0].Action);
        Assert.Equal(correctFamily ? SafeMigrationAction.Apply : SafeMigrationAction.RejectPrerequisiteMissing,
            report.Assessments[2].Action);
    }

}
