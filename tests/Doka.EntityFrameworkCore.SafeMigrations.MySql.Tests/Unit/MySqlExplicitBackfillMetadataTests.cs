namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies exact ownership of Doka's explicit-backfill metadata.</summary>
public sealed class MySqlExplicitBackfillMetadataTests
{
    private const string StrictSqlModeCondition = "FIND_IN_SET('STRICT_TRANS_TABLES', @@SESSION.sql_mode) > 0 "
        + "OR FIND_IN_SET('STRICT_ALL_TABLES', @@SESSION.sql_mode) > 0";

    /// <summary>A boolean safety marker must not make supported GUID metadata unknown.</summary>
    /// <param name="required">Whether Doka requests an explicit backfill.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitBackfillBooleanPreservesSupportedColumn(bool required)
    {
        // Arrange
        var definition = BackfillDefinition(required);
        var oppositeDefinition = BackfillDefinition(!required);

        // Act
        var supported = MySqlSafeMigrationColumnMetadata.TryCreate(definition, out var metadata);
        var oppositeSupported = MySqlSafeMigrationColumnMetadata.TryCreate(
            oppositeDefinition, out var oppositeMetadata);

        // Assert
        Assert.True(supported);
        Assert.True(oppositeSupported);
        Assert.Equal(DokaMySqlGuidFormat.Char36, metadata.GuidFormat);
        Assert.Equal(MySqlValueGenerationStrategy.ClientGuid, metadata.ValueGenerationStrategy);
        Assert.Equal(metadata, oppositeMetadata);
        var annotation = Assert.Single(definition.ProviderAnnotations,
            value => value.Name == MySqlSafeMigrationColumnMetadata.ExplicitBackfillAnnotation);
        var oppositeAnnotation = Assert.Single(oppositeDefinition.ProviderAnnotations,
            value => value.Name == MySqlSafeMigrationColumnMetadata.ExplicitBackfillAnnotation);

        Assert.Equal(required, Assert.IsType<bool>(annotation.CreateValueSnapshot()));
        Assert.NotEqual(annotation.Fingerprint, oppositeAnnotation.Fingerprint);
        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition));
    }

    /// <summary>Recognizing one safety marker must not authorize a malformed value.</summary>
    /// <param name="value">The invalid captured annotation value.</param>
    [Theory]
    [InlineData(null)]
    [InlineData(1)]
    [InlineData("true")]
    public void ExplicitBackfillRejectsNonBooleanMetadata(object? value)
    {
        // Arrange
        var definition = BackfillDefinition(value);

        // Act
        var supported = MySqlSafeMigrationColumnMetadata.TryCreate(definition, out _);

        // Assert
        Assert.False(supported);
        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.HasUnsupportedProviderColumnAnnotation(definition));
    }

    /// <summary>Known backfill metadata cannot hide an unrelated unknown annotation.</summary>
    [Fact]
    public void ExplicitBackfillDoesNotAuthorizeUnknownProviderMetadata()
    {
        // Arrange
        var operation = BackfillColumn(true);
        operation["Test:UnknownColumnFeature"] = true;
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);

        // Act
        var supported = MySqlSafeMigrationColumnMetadata.TryCreate(definition, out _);

        // Assert
        Assert.False(supported);
    }

    /// <summary>Renders a required add behind the fresh missing-column row guard without changing its intent.</summary>
    [Fact]
    public void ExplicitBackfillAddRetainsFreshRowGuardAndAuthoredMetadata()
    {
        // Arrange
        using var context = CreateContext();
        var authored = BackfillColumn(true);
        var definition = SafeMigrationExpectedDefinitionFactory.From(authored);
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent(authored.Table, definition),
            SafeMigrationPolicy.ThrowIfDifferent);
        var generator = context.GetService<IMigrationsSqlGenerator>();

        // Act
        var first = Assert.Single(generator.Generate([operation], context.Model)).CommandText;
        var second = Assert.Single(generator.Generate([operation], context.Model)).CommandText;
        var payloads = DecodePayloads(first);

        // Assert
        var guard = Assert.Single(payloads, sql => sql.StartsWith(
            "SELECT CASE WHEN NOT COALESCE(", StringComparison.Ordinal));
        var add = Assert.Single(payloads, sql => sql.StartsWith(
            "ALTER TABLE `items` ADD ", StringComparison.Ordinal));
        var finish = Assert.Single(payloads, sql => sql.StartsWith(
            "ALTER TABLE `items` MODIFY COLUMN ", StringComparison.Ordinal));

        Assert.Contains("WHEN EXISTS (SELECT 1 FROM `items` LIMIT 1) THEN 'data_blocked' ELSE 'missing'",
            guard, StringComparison.Ordinal);
        Assert.True(guard.IndexOf("THEN 'data_blocked'", StringComparison.Ordinal)
            < guard.IndexOf("ELSE 'missing'", StringComparison.Ordinal));
        Assert.True(payloads.IndexOf(guard) < payloads.IndexOf(add));
        Assert.Contains(StrictSqlModeCondition, first, StringComparison.Ordinal);
        Assert.Contains("ADD CONSTRAINT `doka_sm_add1_nn_", add, StringComparison.Ordinal);
        Assert.Contains("CHECK (`id` IS NOT NULL)", add, StringComparison.Ordinal);
        Assert.Contains("NOT NULL", finish, StringComparison.Ordinal);
        var intent = Assert.IsType<EnsureColumnIntent>(operation.Intent);
        var guardName = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(intent);

        Assert.EndsWith(", DROP CHECK `" + guardName + "`", finish, StringComparison.Ordinal);
        Assert.True(payloads.IndexOf(add) < payloads.IndexOf(finish));
        Assert.DoesNotContain("DEFAULT", add, StringComparison.Ordinal);
        Assert.DoesNotContain(payloads, sql => sql.StartsWith("UPDATE ", StringComparison.Ordinal));
        Assert.Equal(MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation(first),
            MySqlRuntimeSqlCanonicalizer.NormalizeOwnedCorrelation(second));
        Assert.True(Assert.IsType<bool>(authored["Doka:MySql:RequiresExplicitBackfill"]));
        Assert.True(Assert.IsType<bool>(Assert.Single(definition.ProviderAnnotations,
            annotation => annotation.Name == "Doka:MySql:RequiresExplicitBackfill").CreateValueSnapshot()));
        Assert.Equal(SafeMigrationDefaultValueKind.None, definition.DefaultValue.Kind);
    }

    /// <summary>Materializes one fresh NULL proof instead of asking Doka to invent an absent backfill.</summary>
    /// <param name="nullDefault">Whether the caller explicitly requests a SQL NULL default.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitBackfillTighteningWithoutNonNullDefaultUsesFreshNullProof(bool nullDefault)
    {
        // Arrange
        using var context = CreateContext();
        var source = BackfillColumn(true);
        source.IsNullable = true;
        var target = BackfillColumn(true);
        target.DefaultValueSql = nullDefault ? "NULL" : null;
        var definition = SafeMigrationExpectedDefinitionFactory.From(target);
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent(target.Table, definition, SafeMigrationExpectedDefinitionFactory.From(source)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var payloads = DecodePayloads(command.CommandText);

        // Assert
        var proof = Assert.Single(payloads, sql => sql.Contains(
            "EXISTS (SELECT 1 FROM `items` WHERE `id` IS NULL LIMIT 1)", StringComparison.Ordinal));
        var state = Assert.Single(payloads, sql => sql.EndsWith("INTO @doka_sm_state", StringComparison.Ordinal));
        var alterations = payloads.Where(sql => sql.StartsWith("ALTER TABLE `items`", StringComparison.Ordinal))
            .ToArray();

        Assert.EndsWith("INTO @doka_sm_nullability_blocked", proof, StringComparison.Ordinal);
        Assert.Contains("@doka_sm_nullability_blocked", state, StringComparison.Ordinal);
        AssertRuntimeSetNullGuard(command.CommandText);
        AssertStrictModeEligibility(command.CommandText);
        Assert.Equal(2, alterations.Length);
        Assert.Equal(alterations[0], alterations[1]);
        Assert.All(alterations, alter =>
        {
            Assert.True(payloads.IndexOf(proof) < payloads.IndexOf(alter));
            Assert.Equal(nullDefault, alter.Contains("DEFAULT", StringComparison.Ordinal));
        });

        Assert.DoesNotContain(payloads, sql => sql.StartsWith("UPDATE ", StringComparison.Ordinal));
        Assert.True(source.IsNullable);
        Assert.False(target.IsNullable);
        Assert.Equal(nullDefault ? "NULL" : null, target.DefaultValueSql);
        Assert.True(Assert.IsType<bool>(target["Doka:MySql:RequiresExplicitBackfill"]));
    }

    /// <summary>Preserves a supplied non-null default and Doka's explicit UPDATE-before-MODIFY sequence.</summary>
    [Fact]
    public void ExplicitBackfillTighteningPreservesExplicitNonNullDefault()
    {
        // Arrange
        using var context = CreateContext();
        var source = BackfillColumn(true);
        source.IsNullable = true;
        var target = BackfillColumn(true);
        var replacement = Guid.Parse("00000000-0000-0000-0000-000000000001");
        target.DefaultValue = replacement;
        var definition = SafeMigrationExpectedDefinitionFactory.From(target);
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent(target.Table, definition, SafeMigrationExpectedDefinitionFactory.From(source)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var payloads = commands.SelectMany(command => DecodePayloads(command.CommandText)).ToList();

        // Assert
        AssertRuntimeSetNullGuard(Assert.Single(commands).CommandText);
        AssertStrictModeEligibility(Assert.Single(commands).CommandText);
        var updates = payloads.Where(sql => sql.StartsWith("UPDATE `items`", StringComparison.Ordinal)).ToArray();
        var alterations = payloads.Where(sql => sql.StartsWith("ALTER TABLE `items`", StringComparison.Ordinal))
            .ToArray();

        Assert.Equal(2, updates.Length);
        Assert.Equal(2, alterations.Length);
        Assert.Equal(updates[0], updates[1]);
        Assert.Equal(alterations[0], alterations[1]);
        Assert.All(updates, update =>
        {
            Assert.Contains(replacement.ToString(), update, StringComparison.Ordinal);
            Assert.Contains("WHERE `id` IS NULL", update, StringComparison.Ordinal);
        });

        Assert.All(alterations, alter => Assert.Contains(replacement.ToString(), alter, StringComparison.Ordinal));
        Assert.True(payloads.FindLastIndex(sql => sql.StartsWith("UPDATE `items`", StringComparison.Ordinal))
            < payloads.FindIndex(sql => sql.StartsWith("ALTER TABLE `items`", StringComparison.Ordinal)));
        Assert.Equal(replacement, definition.DefaultValue.GetLiteralValue());
        Assert.Equal(replacement, target.DefaultValue);
        Assert.True(source.IsNullable);
    }

    /// <summary>Retains exact source and row proofs without capturing an operation's runtime FK guard.</summary>
    /// <param name="explicitDefault">Whether the caller supplies a non-null backfill value.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CapturedTighteningRetainsSourceProofWithoutRuntimeForeignKeyGuard(bool explicitDefault)
    {
        // Arrange
        using var context = CreateContext();
        var source = BackfillColumn(true);
        source.IsNullable = true;
        var target = BackfillColumn(true);
        target.DefaultValue = explicitDefault ? Guid.Parse("00000000-0000-0000-0000-000000000001") : null;
        var oldDefinition = SafeMigrationExpectedDefinitionFactory.From(source);
        var intent = new AlterColumnIntent(target.Table, SafeMigrationExpectedDefinitionFactory.From(target),
            oldDefinition);
        var operation = new SafeMigrationOperation(intent, SafeMigrationPolicy.RepairIfSafe);
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        using var lease = capture.Begin([operation]);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plan = Assert.Single(lease.Complete());
        var repair = plan.RenderRepairPrecondition(parameterizer.Add);
        var matching = plan.RenderPostcondition(parameterizer.Add);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.Contains("c.IS_NULLABLE = 'YES'", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("nullable_fk", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS", repair, StringComparison.Ordinal);
        Assert.Equal(!explicitDefault, plan.NullabilityDataProbe is not null);
        Assert.Contains(StrictSqlModeCondition, repair, StringComparison.Ordinal);
        Assert.DoesNotContain(StrictSqlModeCondition, matching, StringComparison.Ordinal);
        Assert.Same(oldDefinition, intent.OldDefinition);
        Assert.True(oldDefinition.IsNullable);
    }

    /// <summary>Qualifies Ensure repairs without allowing provider annotations to change dependency safety.</summary>
    /// <param name="nullable">Whether repair must exclude live PRIMARY membership.</param>
    /// <param name="annotated">Whether Doka GUID and explicit-backfill metadata are present.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void EnsureRepairUsesRelevantRuntimeDependencyGuard(bool nullable, bool annotated)
    {
        // Arrange
        using var context = CreateContext();
        var definition = EnsureDefinition(nullable, annotated);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", definition),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var sql = command.CommandText;

        // Assert
        if (nullable)
        {
            AssertRuntimePrimaryGuard(sql);
            Assert.DoesNotContain("nullable_fk", sql, StringComparison.Ordinal);
            Assert.DoesNotContain(StrictSqlModeCondition, sql, StringComparison.Ordinal);
        }
        else
        {
            AssertRuntimeSetNullGuard(sql);
            AssertEnsureStrictModeEligibility(sql);
            Assert.DoesNotContain("nullable_primary", sql, StringComparison.Ordinal);
            var guard = Assert.Single(DecodePayloads(sql), payload => payload.StartsWith(
                "SELECT CASE WHEN NOT COALESCE(", StringComparison.Ordinal)
                && payload.EndsWith("INTO @doka_sm_state", StringComparison.Ordinal));

            Assert.Contains("WHEN EXISTS (SELECT 1 FROM `items` LIMIT 1) THEN 'data_blocked' ELSE 'missing'",
                guard, StringComparison.Ordinal);
            Assert.True(guard.IndexOf("THEN 'data_blocked'", StringComparison.Ordinal)
                < guard.IndexOf("ELSE 'missing'", StringComparison.Ordinal));
            Assert.DoesNotContain("nullable_fk", guard, StringComparison.Ordinal);
            Assert.Contains("@doka_sm_add_status", guard, StringComparison.Ordinal);
            Assert.DoesNotContain(StrictSqlModeCondition, guard, StringComparison.Ordinal);
        }

        var dependencyGuard = nullable ? "nullable_primary" : "nullable_fk";
        Assert.Contains("'matching'", sql, StringComparison.Ordinal);
        var matchingDecision = nullable ? "'matching'" : "THEN 'complete'";

        Assert.Contains(matchingDecision, sql, StringComparison.Ordinal);
        Assert.True(sql.IndexOf(matchingDecision, StringComparison.Ordinal)
            < sql.IndexOf(dependencyGuard, StringComparison.Ordinal));
        Assert.DoesNotContain(DecodePayloads(sql), payload => payload.StartsWith("UPDATE ", StringComparison.Ordinal));
    }

    /// <summary>Retains ordinary and annotated Ensure proofs independently of later runtime dependencies.</summary>
    /// <param name="nullable">The requested target nullability.</param>
    /// <param name="annotated">Whether the column carries supported provider metadata.</param>
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void CapturedEnsureRepairOmitsRuntimeDependencyGuards(bool nullable, bool annotated)
    {
        // Arrange
        using var context = CreateContext();
        var operation = new SafeMigrationOperation(
            new EnsureColumnIntent("items", EnsureDefinition(nullable, annotated)),
            SafeMigrationPolicy.RepairIfSafe);
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        using var lease = capture.Begin([operation]);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plan = Assert.Single(lease.Complete());
        // WHY: Atomic resume has its own dependency feasibility. This test
        // isolates the ordinary captured repair certificate, not that branch.
        var ordinaryPlan = plan.RequiredColumnAddPlan is { } protocol
                ? plan with
                {
                    RequiredColumnAddPlan = protocol with
                    {
                        StatusBinding = "'ordinary'",
                    },
                }
                : plan;
        var repair = ordinaryPlan.RenderRepairPrecondition(parameterizer.Add);
        var matching = plan.RenderPostcondition(parameterizer.Add);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.Equal(!nullable, plan.NullabilityDataProbe is not null);
        Assert.DoesNotContain("nullable_fk", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("nullable_primary", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("nullable_fk", matching, StringComparison.Ordinal);
        Assert.DoesNotContain("nullable_primary", matching, StringComparison.Ordinal);
        Assert.Equal(!nullable, repair.Contains(StrictSqlModeCondition, StringComparison.Ordinal));
        Assert.DoesNotContain(StrictSqlModeCondition, matching, StringComparison.Ordinal);
    }

    /// <summary>Requires strict conversion for Ensure's no-UPDATE nullable repair even with a real default.</summary>
    [Fact]
    public void RequiredEnsureWithExplicitDefaultUsesStrictNoUpdateRepair()
    {
        // Arrange
        using var context = CreateContext();
        var authored = BackfillColumn(true);
        var replacement = Guid.Parse("00000000-0000-0000-0000-000000000001");
        authored.DefaultValue = replacement;
        var definition = SafeMigrationExpectedDefinitionFactory.From(authored);
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", definition),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));
        var payloads = DecodePayloads(command.CommandText);

        // Assert
        AssertEnsureStrictModeEligibility(command.CommandText);
        Assert.DoesNotContain(payloads, sql => sql.StartsWith("UPDATE ", StringComparison.Ordinal));
        Assert.Contains(payloads, sql => sql.StartsWith("ALTER TABLE `items` MODIFY", StringComparison.Ordinal)
            && sql.Contains(replacement.ToString(), StringComparison.Ordinal));
        Assert.Equal(replacement, authored.DefaultValue);
        Assert.Equal(replacement, definition.DefaultValue.GetLiteralValue());
    }

    /// <summary>Strict Ensure needs dependency feasibility only when resuming an atomic required addition.</summary>
    /// <param name="nullable">The requested target nullability.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EnsureWithoutRepairUsesDependencyGuardOnlyForAtomicResume(bool nullable)
    {
        // Arrange
        using var context = CreateContext();
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", EnsureDefinition(nullable, false)),
            SafeMigrationPolicy.ThrowIfDifferent);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        // Assert
        Assert.Equal(!nullable, command.CommandText.Contains("nullable_fk", StringComparison.Ordinal));
        Assert.DoesNotContain("nullable_primary", command.CommandText, StringComparison.Ordinal);
        Assert.Equal(!nullable, command.CommandText.Contains(StrictSqlModeCondition, StringComparison.Ordinal));
    }

    /// <summary>Captures intrinsic nullable eligibility independently of removable live PRIMARY membership.</summary>
    [Fact]
    public void CapturedNullableRelaxationOmitsRuntimePrimaryDependencyGuard()
    {
        // Arrange
        using var context = CreateContext();
        var source = BackfillColumn(true);
        var target = BackfillColumn(true);
        target.IsNullable = true;
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent(target.Table, SafeMigrationExpectedDefinitionFactory.From(target),
                SafeMigrationExpectedDefinitionFactory.From(source)), SafeMigrationPolicy.RepairIfSafe);
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        using var lease = capture.Begin([operation]);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plan = Assert.Single(lease.Complete());
        var repair = plan.RenderRepairPrecondition(parameterizer.Add);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.Contains("INFORMATION_SCHEMA.COLUMNS", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("nullable_primary", repair, StringComparison.Ordinal);
        Assert.DoesNotContain("nullable_fk", repair, StringComparison.Ordinal);
        Assert.DoesNotContain(StrictSqlModeCondition, repair, StringComparison.Ordinal);
    }

    /// <summary>Rejects a still-live PRIMARY KEY before attempting a pure nullable relaxation.</summary>
    [Fact]
    public void NullableRelaxationRetainsFreshPrimaryKeyGuard()
    {
        // Arrange
        using var context = CreateContext();
        var source = BackfillColumn(true);
        var target = BackfillColumn(true);
        target.IsNullable = true;
        var operation = new SafeMigrationOperation(
            new AlterColumnIntent(target.Table, SafeMigrationExpectedDefinitionFactory.From(target),
                SafeMigrationExpectedDefinitionFactory.From(source)),
            SafeMigrationPolicy.RepairIfSafe);

        // Act
        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model));

        // Assert
        AssertRuntimePrimaryGuard(command.CommandText);
        Assert.DoesNotContain(DecodePayloads(command.CommandText), sql => sql.StartsWith("UPDATE ",
            StringComparison.Ordinal));
        Assert.DoesNotContain("nullable_fk", command.CommandText, StringComparison.Ordinal);
        Assert.DoesNotContain(StrictSqlModeCondition, command.CommandText, StringComparison.Ordinal);
    }

    /// <summary>Checks strict conversion depends on live nullability, preserving already-required repairs.</summary>
    /// <param name="sql">The generated required Ensure command.</param>
    private static void AssertEnsureStrictModeEligibility(string sql)
    {
        AssertStrictModeEligibility(sql);
        var nativeOwner = sql.Contains(
            "CAST(c.TABLE_NAME AS BINARY) = CAST('items' AS BINARY)", StringComparison.Ordinal)
            ? "AND (CASE WHEN @@lower_case_table_names = 0 THEN "
                + "CAST(c.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY) "
                + "WHEN @@lower_case_table_names IN (1, 2) THEN "
                + "CAST(LOWER(c.TABLE_SCHEMA) AS BINARY) = CAST(LOWER(DATABASE()) AS BINARY) ELSE FALSE END) "
                + "AND (CASE WHEN @@lower_case_table_names = 0 THEN "
                + "CAST(c.TABLE_NAME AS BINARY) = CAST('items' AS BINARY) "
                + "WHEN @@lower_case_table_names IN (1, 2) THEN "
                + "CAST(LOWER(c.TABLE_NAME) AS BINARY) = CAST(LOWER('items') AS BINARY) ELSE FALSE END) "
            : string.Empty;

        Assert.Contains("NOT (EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.COLUMNS c "
            + "WHERE c.TABLE_SCHEMA = DATABASE() AND c.TABLE_NAME = 'items' AND c.COLUMN_NAME = 'id' "
            + nativeOwner + "AND (c.IS_NULLABLE = 'YES'))) OR (" + StrictSqlModeCondition + ")", sql,
            StringComparison.Ordinal);
    }

    /// <summary>Checks strict eligibility precedes action selection and follows exact-match classification.</summary>
    /// <param name="sql">The generated no-backfill tightening command.</param>
    private static void AssertStrictModeEligibility(string sql)
    {
        Assert.Contains(StrictSqlModeCondition, sql, StringComparison.Ordinal);
        Assert.Contains("'matching'", sql, StringComparison.Ordinal);
        var matchingDecision = sql.Contains("SET @doka_sm_add_status", StringComparison.Ordinal)
            ? "THEN 'complete'"
            : "'matching'";

        Assert.Contains(matchingDecision, sql, StringComparison.Ordinal);
        Assert.True(sql.IndexOf(matchingDecision, StringComparison.Ordinal)
            < sql.IndexOf(StrictSqlModeCondition, StringComparison.Ordinal));
        Assert.True(sql.IndexOf(StrictSqlModeCondition, StringComparison.Ordinal)
            < sql.IndexOf("SET @doka_sm_action", StringComparison.Ordinal));
    }

    /// <summary>Creates an ordinary integer or an explicitly annotated GUID Ensure target.</summary>
    /// <param name="nullable">The requested target nullability.</param>
    /// <param name="annotated">Whether to retain Doka's GUID/backfill metadata.</param>
    /// <returns>The immutable Ensure target.</returns>
    private static ExpectedColumnDefinition EnsureDefinition(bool nullable, bool annotated)
    {
        if (!annotated)
        {
            return new ExpectedColumnDefinition("id", typeof(int), nullable, "int");
        }

        var operation = BackfillColumn(true);
        operation.IsNullable = nullable;

        return SafeMigrationExpectedDefinitionFactory.From(operation);
    }

    /// <summary>Checks PRIMARY exclusion is local-column bounded and materialized before choosing an action.</summary>
    /// <param name="sql">The generated runtime command before any mutation.</param>
    private static void AssertRuntimePrimaryGuard(string sql)
    {
        Assert.Contains("NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.STATISTICS nullable_primary "
            + "WHERE nullable_primary.TABLE_SCHEMA = DATABASE() AND nullable_primary.TABLE_NAME = 'items' "
            + "AND nullable_primary.COLUMN_NAME = 'id' AND nullable_primary.INDEX_NAME = 'PRIMARY')",
            sql, StringComparison.Ordinal);
        Assert.True(sql.IndexOf("nullable_primary.INDEX_NAME = 'PRIMARY'", StringComparison.Ordinal)
            < sql.IndexOf("SET @doka_sm_action", StringComparison.Ordinal));
    }

    /// <summary>Checks a fresh FK guard is bound to the local column and only the two SET NULL actions.</summary>
    /// <param name="sql">The generated runtime command before any mutation.</param>
    private static void AssertRuntimeSetNullGuard(string sql)
    {
        Assert.Contains("NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.REFERENTIAL_CONSTRAINTS nullable_fk "
            + "JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE nullable_fk_column "
            + "ON nullable_fk_column.CONSTRAINT_SCHEMA = nullable_fk.CONSTRAINT_SCHEMA "
            + "AND nullable_fk_column.CONSTRAINT_NAME = nullable_fk.CONSTRAINT_NAME "
            + "AND nullable_fk_column.TABLE_SCHEMA = nullable_fk.CONSTRAINT_SCHEMA "
            + "AND nullable_fk_column.TABLE_NAME = nullable_fk.TABLE_NAME "
            + "WHERE nullable_fk.CONSTRAINT_SCHEMA = DATABASE() AND nullable_fk.TABLE_NAME = 'items' "
            + "AND nullable_fk_column.COLUMN_NAME = 'id' "
            + "AND (nullable_fk.DELETE_RULE = 'SET NULL' OR nullable_fk.UPDATE_RULE = 'SET NULL'))",
            sql, StringComparison.Ordinal);
        Assert.DoesNotContain("nullable_fk.DELETE_RULE = 'CASCADE'", sql, StringComparison.Ordinal);
        Assert.DoesNotContain("nullable_fk.UPDATE_RULE = 'CASCADE'", sql, StringComparison.Ordinal);
        Assert.True(sql.IndexOf("nullable_fk.DELETE_RULE = 'SET NULL'", StringComparison.Ordinal)
            < sql.IndexOf("SET @doka_sm_action", StringComparison.Ordinal));
    }

    /// <summary>Creates an offline provider context without opening a server connection.</summary>
    /// <returns>The context used only for SQL generation.</returns>
    private static DbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<DbContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=test;Password=test;Database=test;Allow User Variables=true",
                MySqlServerVersion.MySql(new Version(8, 4, 11)))
            .UseMySqlSafeMigrations()
            .Options;

        return new DbContext(options);
    }

    /// <summary>Decodes guarded statements so tests inspect the actual SQL rather than its transport.</summary>
    /// <param name="sql">The complete generated guarded command.</param>
    /// <returns>The prepared statements in transport order, including alternative action branches.</returns>
    private static List<string> DecodePayloads(string sql)
    {
        const string prefix = "CONVERT(0x";
        const string suffix = " USING utf8mb4)";
        var payloads = new List<string>();
        var offset = 0;
        while ((offset = sql.IndexOf(prefix, offset, StringComparison.Ordinal)) >= 0)
        {
            var start = offset + prefix.Length;
            var end = sql.IndexOf(suffix, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidOperationException("A generated SQL payload is unterminated.");
            }

            payloads.Add(Encoding.UTF8.GetString(Convert.FromHexString(sql.AsSpan(start, end - start))));
            offset = end + suffix.Length;
        }

        return payloads;
    }

    /// <summary>Reads a marker through the real captured EF column boundary.</summary>
    /// <param name="value">The marker value to capture without coercion.</param>
    /// <returns>The immutable target definition.</returns>
    private static ExpectedColumnDefinition BackfillDefinition(object? value)
        => SafeMigrationExpectedDefinitionFactory.From(BackfillColumn(value));

    /// <summary>Creates the provider's required GUID-add shape without synthetic data.</summary>
    /// <param name="value">The marker value to preserve.</param>
    /// <returns>The annotated EF operation.</returns>
    private static AddColumnOperation BackfillColumn(object? value)
    {
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "items",
            ClrType = typeof(Guid),
            ColumnType = "char(36)",
            IsFixedLength = true,
            MaxLength = 36,
            IsNullable = false,
        };

        operation["Doka:MySql:GuidFormat"] = DokaMySqlGuidFormat.Char36;
        operation["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.ClientGuid;
        operation.AddAnnotation("Doka:MySql:RequiresExplicitBackfill", value);

        return operation;
    }
}
