namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Verifies exact ownership and fail-closed catalog qualification for atomic required additions.</summary>
public sealed class MySqlRequiredColumnAddCatalogTests
{
    /// <summary>Stable names bind the complete raw owner and authored target without depending on policy.</summary>
    [Fact]
    public void GuardNameBindsCompleteRawContract()
    {
        // Arrange
        var intent = new EnsureColumnIntent("items", Column());
        EnsureColumnIntent[] differentContracts =
        [
            new("other_items", Column()),
            new("items", Column(), "selected"),
            new("items", new ExpectedColumnDefinition("other_id", typeof(int), false, "int")),
            new("items", new ExpectedColumnDefinition("id", typeof(int), false, "int", comment: "changed")),
        ];

        // Act
        var name = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(intent);
        var repeated = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            new EnsureColumnIntent("items", Column()));
        var different = differentContracts.Select(MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName);

        // Assert
        Assert.Equal(name, repeated);
        Assert.StartsWith("doka_sm_add1_nn_", name, StringComparison.Ordinal);
        Assert.Equal(64, name.Length);
        Assert.NotEqual(name, name.ToUpperInvariant());
        Assert.All(name, character => Assert.InRange((int)character, 0, 127));
        Assert.All(different, candidate => Assert.NotEqual(name, candidate));
    }

    /// <summary>A cheap name-shape filter cannot admit broad prefixes, uppercase suffixes or other versions.</summary>
    [Fact]
    public void GuardNameCandidateIsExactShapeOnly()
    {
        // Arrange
        const string prefix = "doka_sm_add1_nn_";
        var actual = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(
            new EnsureColumnIntent("items", Column()));
        string[] valid = [actual, prefix + new string('0', 48), prefix + new string('f', 48)];
        string[] invalid =
        [
            string.Empty,
            prefix,
            prefix + new string('0', 47),
            prefix + new string('0', 49),
            prefix.ToUpperInvariant() + new string('0', 48),
            "doka_sm_add2_nn_" + new string('0', 48),
            prefix + "A" + new string('0', 47),
            prefix + "g" + new string('0', 47),
            prefix + "_" + new string('0', 47),
        ];

        // Act
        var accepted = valid.Select(MySqlSafeMigrationCatalogSqlBuilder.IsRequiredColumnAddGuardNameCandidate);
        var rejected = invalid.Select(MySqlSafeMigrationCatalogSqlBuilder.IsRequiredColumnAddGuardNameCandidate);

        // Assert
        Assert.All(accepted, Assert.True);
        Assert.All(rejected, Assert.False);
        Assert.NotEqual(actual, valid[1]);
    }

    /// <summary>Only ordinary no-default required targets enter the CHECK-protected protocol.</summary>
    /// <param name="storeType">The authored store type, including excluded native TIMESTAMP shapes.</param>
    /// <param name="supported">Whether the target can use the protocol.</param>
    [Theory]
    [InlineData("datetime(6)", true)]
    [InlineData("timestamp", false)]
    [InlineData("timestamp(6)", false)]
    [InlineData(" TIMESTAMP(3)", false)]
    public void NativeTimestampIsNotAnIntrinsicNoDefaultAddition(string storeType, bool supported)
    {
        // Arrange
        var definition = new ExpectedColumnDefinition("id", typeof(DateTime), false, storeType);

        // Act
        var eligible = MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(definition);
        var unsupportedMissing = MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(definition);

        // Assert
        Assert.Equal(supported, eligible);
        Assert.Equal(!supported, unsupportedMissing);
    }

    /// <summary>Verified ENUM and SET atomic stages retain the authored no-default contract.</summary>
    /// <param name="storeType">The explicit ENUM or independently controlled SET store type.</param>
    /// <param name="eligible">Whether the verified atomic protocol is available.</param>
    [Theory]
    [InlineData("enum('a','b')", true)]
    [InlineData(" ENUM('a','b')", true)]
    [InlineData("set('a','b')", true)]
    public void EnumAndSetRemainEligibleAfterVerifiedAtomicStages(string storeType, bool eligible)
    {
        // Arrange
        var definition = new ExpectedColumnDefinition("id", typeof(string), false, storeType);

        // Act
        var supported = MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(definition);
        var unsupportedMissing = MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(definition);

        // Assert
        Assert.Equal(eligible, supported);
        Assert.Equal(!eligible, unsupportedMissing);
    }

    /// <summary>Only the type keyword is canonicalized; ordered member values remain byte-exact.</summary>
    /// <param name="storeType">The authored keyword, quoting and outside-literal spacing.</param>
    /// <param name="expected">The verified native catalog member spelling.</param>
    [Theory]
    [InlineData(" ENUM ('Alpha' , 'Beta') ", "enum('Alpha','Beta')")]
    [InlineData("SET ('Beta' , 'Alpha')", "set('Beta','Alpha')")]
    [InlineData("enum('quo''te','a,b','a b','')", "enum('quo''te','a,b','a b','')")]
    [InlineData("enum('a' 'b')", "enum('ab')")]
    [InlineData("enum('Alpha  ',' Beta ','  ')", "enum('Alpha',' Beta','')")]
    public void EnumSetCatalogDisplayPreservesExactMembers(string storeType, string expected)
    {
        // Arrange
        var original = storeType;

        // Act
        var escaped = MySqlSafeMigrationCatalogSqlBuilder.BuildEnumSetCatalogDisplay(
            storeType, backslashEscapes: true, out var escapedDoubleQuotes);
        var literal = MySqlSafeMigrationCatalogSqlBuilder.BuildEnumSetCatalogDisplay(
            storeType, backslashEscapes: false, out var literalDoubleQuotes);

        // Assert
        Assert.Equal(expected, escaped);
        Assert.Equal(expected, literal);
        Assert.False(escapedDoubleQuotes);
        Assert.False(literalDoubleQuotes);
        Assert.Equal(original, storeType);
    }

    /// <summary>Backslash decoding and catalog escaping remain qualified by the exact native SQL mode.</summary>
    [Fact]
    public void EnumSetBackslashDisplayDoesNotMergeDifferentDomains()
    {
        // Arrange
        const string source = "ENUM ('A', 'quo''te', 'slash\\\\tail','back\\\\slash\\\\')";
        const string escapedExpected = "enum('A','quo''te','slash\\\\tail','back\\\\slash\\\\')";
        const string literalExpected = "enum('A','quo''te','slash\\\\\\\\tail','back\\\\\\\\slash\\\\\\\\')";

        // Act
        var escaped = MySqlSafeMigrationCatalogSqlBuilder.BuildEnumSetCatalogDisplay(
            source, backslashEscapes: true, out _);
        var literal = MySqlSafeMigrationCatalogSqlBuilder.BuildEnumSetCatalogDisplay(
            source, backslashEscapes: false, out _);

        // Assert
        Assert.Equal(escapedExpected, escaped);
        Assert.Equal(literalExpected, literal);
        Assert.NotEqual(escaped, literal);
        Assert.Equal("656E756D282741272C2771756F27277465272C27736C6173685C5C7461696C272C"
            + "276261636B5C5C736C6173685C5C2729", Convert.ToHexString(Encoding.UTF8.GetBytes(
                Assert.IsType<string>(escaped))));
        Assert.Equal("656E756D282741272C2771756F27277465272C27736C6173685C5C5C5C7461696C272C"
            + "276261636B5C5C5C5C736C6173685C5C5C5C2729", Convert.ToHexString(Encoding.UTF8.GetBytes(
                Assert.IsType<string>(literal))));
    }

    /// <summary>Native control display distinguishes escaped values from literal backslash sequences.</summary>
    [Fact]
    public void EnumSetControlDisplayMatchesVerifiedNativeSerialization()
    {
        // Arrange
        const string source = "ENUM('nul\\0','line\\n','return\\r','tab\\t','back\\b','eof\\Z',"
            + "'percent\\%','under\\_','unknown\\q')";
        const string expected = "enum('nul\\0','line\\n','return\\r','tab\t','back\b','eof\u001a',"
            + "'percent\\\\%','under\\\\_','unknownq')";

        // Act
        var display = MySqlSafeMigrationCatalogSqlBuilder.BuildEnumSetCatalogDisplay(
            source, backslashEscapes: true, out _);

        // Assert
        Assert.Equal(expected, display);
        Assert.Equal("656E756D28276E756C5C30272C276C696E655C6E272C2772657475726E5C72272C2774616209272C"
            + "276261636B08272C27656F661A272C2770657263656E745C5C25272C27756E6465725C5C5F272C27756E6B6E6F776E712729",
            Convert.ToHexString(Encoding.UTF8.GetBytes(Assert.IsType<string>(display))));
    }

    /// <summary>Unknown or malformed member syntax cannot fall back to case-folded type equality.</summary>
    /// <param name="storeType">The incomplete or unsupported member declaration.</param>
    [Theory]
    [InlineData("enum()")]
    [InlineData("enum('a',)")]
    [InlineData("enum('unfinished)")]
    [InlineData("enum(a)")]
    [InlineData("enum('a') trailing")]
    [InlineData("enum\u00a0('a')")]
    public void MalformedEnumSetDisplayFailsClosed(string storeType)
    {
        // Arrange
        var original = storeType;

        // Act
        var display = MySqlSafeMigrationCatalogSqlBuilder.BuildEnumSetCatalogDisplay(
            storeType, backslashEscapes: true, out _);

        // Assert
        Assert.Null(display);
        Assert.Equal(original, storeType);
    }

    /// <summary>Every full target and owned stage compares member case exactly instead of folding the type.</summary>
    /// <param name="mariaDb">Whether the MariaDB catalog model applies.</param>
    /// <param name="keyword">The authored ENUM or SET keyword.</param>
    [Theory]
    [InlineData(false, "ENUM")]
    [InlineData(false, "SET")]
    [InlineData(true, "ENUM")]
    [InlineData(true, "SET")]
    public void EnumSetMatchingAndStagingUseBinaryMemberIdentity(bool mariaDb, string keyword)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var definition = new ExpectedColumnDefinition("id", typeof(string), false, keyword + " ('Alpha', 'Beta')");
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.ThrowIfDifferent);
        var values = RenderValues(context, plan);
        var expected = "CAST(c.COLUMN_TYPE AS BINARY) = CAST('" + keyword.ToLowerInvariant()
            + "(''Alpha'',''Beta'')' AS BINARY)";

        // Act
        var status = plan.RenderPreparedRequiredColumnAddStatusExpression(values);
        var terminal = plan.RenderPreparedExecutionPostcondition(values);

        // Assert
        Assert.NotNull(plan.RequiredColumnAddPlan);
        Assert.Contains(expected, status, StringComparison.Ordinal);
        Assert.Contains(expected, terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("LOWER(c.COLUMN_TYPE)", status, StringComparison.Ordinal);
        Assert.DoesNotContain("LOWER(c.COLUMN_TYPE)", terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("(''alpha'',''beta'')", status, StringComparison.Ordinal);
    }

    /// <summary>Backslash-sensitive comparisons choose one current-mode display rather than a union.</summary>
    [Fact]
    public void EnumSetMatcherQualifiesBackslashAlternativesByCurrentMode()
    {
        // Arrange
        using var context = CreateContext(mariaDb: true);
        var definition = new ExpectedColumnDefinition("id", typeof(string), false, "enum('slash\\\\tail')");
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.ThrowIfDifferent);
        var values = RenderValues(context, plan);

        // Act
        var terminal = plan.RenderPreparedExecutionPostcondition(values);

        // Assert
        Assert.Contains("CASE WHEN FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0 THEN "
            + "(CAST(c.COLUMN_TYPE AS BINARY) = CAST(", terminal, StringComparison.Ordinal);
        Assert.Contains("ELSE (CAST(c.COLUMN_TYPE AS BINARY) = CAST(", terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("c.COLUMN_TYPE IN (", terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("LOWER(c.COLUMN_TYPE)", terminal, StringComparison.Ordinal);
    }

    /// <summary>Native quote and escape requirements qualify protocol modes independently.</summary>
    /// <param name="storeType">The authored member quotation and escaping.</param>
    /// <param name="backslashEscapes">Whether native bodies need the qualified escape decoder.</param>
    /// <param name="doubleQuotes">Whether member quotation requires ANSI_QUOTES to remain disabled.</param>
    [Theory]
    [InlineData("enum('quo\\'te')", true, false)]
    [InlineData("set(\"quo\\\"te\")", true, true)]
    [InlineData("enum(\"Alpha\")", false, true)]
    [InlineData("enum('quo''te')", false, false)]
    [InlineData("set('slash\\\\tail')", false, false)]
    public void StandardOnlyEscapedMembersQualifyProtocolEnvironment(
        string storeType,
        bool backslashEscapes,
        bool doubleQuotes
    )
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        var definition = new ExpectedColumnDefinition("id", typeof(string), false, storeType);
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.ThrowIfDifferent);
        var protocol = Assert.IsType<MySqlSafeMigrationRequiredColumnAddPlan>(plan.RequiredColumnAddPlan);
        var values = RenderValues(context, plan);

        // Act
        var status = plan.RenderPreparedRequiredColumnAddStatusExpression(values);
        var terminal = plan.RenderPreparedExecutionPostcondition(values);
        var environment = status[status.IndexOf("UPPER(atomic_table.ENGINE)", StringComparison.Ordinal)
            ..status.IndexOf("ELSE 'fresh' END", StringComparison.Ordinal)];

        // Assert
        Assert.Equal(backslashEscapes, protocol.BackslashEscapes);
        Assert.Equal(backslashEscapes,
            environment.Contains("FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) = 0",
                StringComparison.Ordinal));
        Assert.Equal(doubleQuotes,
            environment.Contains("FIND_IN_SET('ANSI_QUOTES', @@SESSION.sql_mode) = 0", StringComparison.Ordinal));
        Assert.DoesNotContain("STRICT_TRANS_TABLES", terminal, StringComparison.Ordinal);
        Assert.DoesNotContain("UPPER(atomic_table.ENGINE)", terminal, StringComparison.Ordinal);
        Assert.True(status.IndexOf("THEN 'complete'", StringComparison.Ordinal)
            < status.IndexOf("UPPER(atomic_table.ENGINE)", StringComparison.Ordinal));
        Assert.Equal(storeType, definition.StoreType);
    }

    /// <summary>A known native comment scope excludes only the standard-only protocol, not exact Matching.</summary>
    /// <param name="keyword">The authored ENUM or SET member declaration.</param>
    [Theory]
    [InlineData("enum")]
    [InlineData("set")]
    public void StandardOnlyEscapedCommentUsesMissingOnlyRefusal(string keyword)
    {
        // Arrange
        using var context = CreateContext(mariaDb: true);
        var storeType = keyword + "('quo\\'te')";
        var ordinary = new ExpectedColumnDefinition("id", typeof(string), false, storeType);
        var commented = new ExpectedColumnDefinition("id", typeof(string), false, storeType,
            comment: "mode\\guard");
        var plan = Capture(context, new EnsureColumnIntent("items", commented), SafeMigrationPolicy.RepairIfSafe);
        var values = RenderValues(context, plan);
        var commentLiteral = values[Array.FindIndex(plan.ParameterValues,
            value => Equals(value.Value, commented.Comment))];

        // Act
        var excluded = MySqlSafeMigrationCatalogSqlBuilder.HasStandardOnlyEnumSetNativeCommentScope(commented);
        var eligible = MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(commented);
        var unsupportedMissing = MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(commented);
        var matching = plan.RenderPreparedPostcondition(values);
        var state = plan.RenderPreparedStateExpression(values, "FALSE", "TRUE");
        var missing = plan.RenderPreparedStateEvaluationGuardFailureExpression(values);

        // Assert
        Assert.True(excluded);
        Assert.False(eligible);
        Assert.True(unsupportedMissing);
        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(ordinary));
        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(ordinary));
        Assert.Null(plan.RequiredColumnAddPlan);
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains("CAST(c.COLUMN_TYPE AS BINARY) = CAST('" + keyword + "(''quo''''te'')' AS BINARY)", matching,
            StringComparison.Ordinal);
        Assert.Contains("CAST(COALESCE(c.COLUMN_COMMENT, '') AS BINARY) = " + commentLiteral,
            matching, StringComparison.Ordinal);
        Assert.True(state.IndexOf("THEN 'matching'", StringComparison.Ordinal)
            < state.IndexOf("THEN 'unsupported'", StringComparison.Ordinal));
        Assert.Contains("ELSE ('unsupported') END", missing, StringComparison.Ordinal);
        Assert.DoesNotContain("EXISTS (SELECT 1 FROM `items`", missing, StringComparison.Ordinal);
        Assert.Equal(storeType, commented.StoreType);
        Assert.Equal("mode\\guard", commented.Comment);
    }

    /// <summary>Native comment scopes qualify only mode-sensitive member domains.</summary>
    /// <param name="storeType">The authored ENUM/SET declaration or ordinary store type.</param>
    /// <param name="comment">The target comment whose backslash can trigger the native scope.</param>
    /// <param name="required">Whether conversion needs the current no-backslash mode.</param>
    [Theory]
    [InlineData("enum('slash\\\\tail')", "comment\\tail", true)]
    [InlineData("set('slash\\\\tail')", "comment\\tail", true)]
    [InlineData("enum('quo\\'te')", "comment\\tail", true)]
    [InlineData("enum('slash\\\\tail')", "plain", false)]
    [InlineData("enum('Alpha','Beta')", "comment\\tail", false)]
    [InlineData("varchar(40)", "comment\\tail", false)]
    public void NativeEnumCommentQualificationUsesDecodedDomainDifference(
        string storeType,
        string comment,
        bool required
    )
    {
        // Arrange
        var definition = new ExpectedColumnDefinition("id", typeof(string), true, storeType, comment: comment);

        // Act
        var qualification = MySqlSafeMigrationCatalogSqlBuilder.RequiresNativeEnumNoBackslashEscapes(definition);

        // Assert
        Assert.Equal(required, qualification);
        Assert.Equal(storeType, definition.StoreType);
        Assert.Equal(comment, definition.Comment);
    }

    /// <summary>Native mode conflicts reject work before probes while exact Matching remains NoOp.</summary>
    /// <param name="ensure">Whether the operation is Ensure rather than Alter.</param>
    /// <param name="nullable">Whether the target remains nullable.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void NativeEnumCommentQualificationPreservesMatchingAndGatesRepair(bool ensure, bool nullable)
    {
        // Arrange
        using var context = CreateContext(mariaDb: true);
        const string storeType = "enum('slash\\\\tail')";
        var target = new ExpectedColumnDefinition("id", typeof(string), nullable, storeType, comment: "comment\\tail");
        var source = new ExpectedColumnDefinition("id", typeof(string), true, storeType);
        SafeMigrationIntent intent = ensure
            ? new EnsureColumnIntent("items", target)
            : new AlterColumnIntent("items", target, source);
        var original = Capture(context, intent, SafeMigrationPolicy.RepairIfSafe);
        var plan = original.RequiredColumnAddPlan is { } protocol
            ? original with { RequiredColumnAddPlan = protocol with { StatusBinding = "@atomic_status" } }
            : original;
        var values = RenderValues(context, plan);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());

        // Act
        var state = plan.RenderPreparedStateExpression(values, "FALSE", "TRUE");
        var repair = plan.RenderPreparedRepairPrecondition(values, "FALSE", "TRUE");
        var matching = plan.RenderPreparedPostcondition(values);
        var code = plan.RenderClassificationCodeExpression(parameterizer.Add);

        // Assert
        Assert.Contains("THEN 'prerequisite_missing'", state, StringComparison.Ordinal);
        Assert.True(state.IndexOf("THEN 'matching'", StringComparison.Ordinal)
            < state.IndexOf("THEN 'unsupported'", StringComparison.Ordinal));
        Assert.Contains("FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0", repair,
            StringComparison.Ordinal);
        Assert.Contains("column_native_enum_mode_conflict", code, StringComparison.Ordinal);
        Assert.DoesNotContain("column_native_enum_mode_conflict", matching, StringComparison.Ordinal);
        if (plan.NullabilityDataProbe is not null)
        {
            Assert.Contains("FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0",
                plan.RenderPreparedNullabilityRepairInvariantExpression(values), StringComparison.Ordinal);
        }

        if (ensure && plan.StateEvaluationGuardFailureExpression is not null)
        {
            var missing = plan.RenderPreparedStateEvaluationGuardFailureExpression(values);

            Assert.True(missing.IndexOf("THEN 'unsupported'", StringComparison.Ordinal)
                < missing.LastIndexOf("'missing'", StringComparison.Ordinal));
        }
    }

    /// <summary>A table or sibling-column comment scope also affects mode-sensitive members in CREATE TABLE.</summary>
    /// <param name="tableComment">Whether the scope comes from the table instead of another column.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeEnumTableCommentScopeQualifiesEveryColumn(bool tableComment)
    {
        // Arrange
        using var context = CreateContext(mariaDb: true);
        var member = new ExpectedColumnDefinition("id", typeof(string), false, "enum('slash\\\\tail')");
        var sibling = new ExpectedColumnDefinition("other", typeof(int), false, "int",
            comment: tableComment ? null : "comment\\tail");
        var definition = new ExpectedTableDefinition("items", [member, sibling],
            comment: tableComment ? "comment\\tail" : null);
        var plan = Capture(context, new EnsureTableIntent(definition, SafeMigrationTableMode.StrictDefinition),
            SafeMigrationPolicy.ThrowIfDifferent);
        var values = RenderValues(context, plan);

        // Act
        var qualification = MySqlSafeMigrationCatalogSqlBuilder.RequiresNativeEnumNoBackslashEscapes(definition);
        var state = plan.RenderPreparedStateExpression(values);

        // Assert
        Assert.True(qualification);
        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.RequiresNativeEnumNoBackslashEscapes(member));
        Assert.Contains("FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0", state,
            StringComparison.Ordinal);
        Assert.True(state.IndexOf("THEN 'matching'", StringComparison.Ordinal)
            < state.IndexOf("THEN 'unsupported'", StringComparison.Ordinal));
        Assert.Contains("column_native_enum_mode_conflict", plan.ClassificationCodeExpression!,
            StringComparison.Ordinal);
    }

    /// <summary>Detailed type evidence keeps authored member case and order visible in rejected domains.</summary>
    /// <param name="mariaDb">Whether MariaDB catalog syntax is selected.</param>
    /// <param name="keyword">The authored ENUM or SET keyword.</param>
    [Theory]
    [InlineData(false, "ENUM")]
    [InlineData(false, "SET")]
    [InlineData(true, "ENUM")]
    [InlineData(true, "SET")]
    public void EnumSetDiagnosticsPreserveLiteralCaseAndMemberOrder(bool mariaDb, string keyword)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var definition = new ExpectedColumnDefinition("id", typeof(string), true, keyword + "('Alpha','Beta')");
        var operation = new SafeMigrationOperation(new EnsureColumnIntent("items", definition),
            SafeMigrationPolicy.ThrowIfDifferent);
        var capture = context.GetService<MySqlSafeMigrationPlanCapture>();
        using var lease = capture.Begin([operation],
            MySqlSafeMigrationPlanCapture.CreateExpectedUniqueIndexes([operation]), includeAnalysisEvidence: true);

        // Act
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);
        var plan = Assert.Single(lease.Complete());
        var values = RenderValues(context, plan);
        var evidence = MySqlCatalogSqlTemplate.RenderPrepared(
            plan.DiagnosticEvidenceExpression
                ?? throw new InvalidOperationException("The diagnostic fixture has no detailed evidence."),
            values);

        // Assert
        Assert.Contains("'column_store_type', '" + keyword + "(''Alpha'',''Beta'')', "
            + "LEFT(COALESCE(c.COLUMN_TYPE, 'none'), 256)", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("LOWER(c.COLUMN_TYPE)", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("(''alpha'',''beta'')", evidence, StringComparison.Ordinal);
        Assert.Equal(keyword + "('Alpha','Beta')", definition.StoreType);
    }

    /// <summary>Legitimate server identity generation is preserved rather than replaced by a CHECK protocol.</summary>
    [Fact]
    public void DeclaredAutoIncrementRetainsOrdinaryNativePath()
    {
        // Arrange
        var operation = new AddColumnOperation
        {
            Name = "id",
            Table = "items",
            ClrType = typeof(int),
            ColumnType = "int",
            IsNullable = false,
        };

        operation["Doka:MySql:ValueGenerationStrategy"] = MySqlValueGenerationStrategy.AutoIncrement;
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);

        // Act
        var eligible = MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol(definition);
        var unsupported = MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(definition);

        // Assert
        Assert.False(eligible);
        Assert.False(unsupported);
    }

    /// <summary>Nullable, computed, rowversion and default-bearing targets cannot acquire no-default staging.</summary>
    [Fact]
    public void NonIntrinsicOrDefaultBearingTargetsDoNotAcquireProtocol()
    {
        // Arrange
        ExpectedColumnDefinition[] definitions =
        [
            new("id", typeof(int), true, "int"),
            new("id", typeof(int), false, "int", defaultValue: SafeMigrationDefaultValue.Literal(7)),
            new("id", typeof(int), false, "int", computedColumnSql: "1", isStored: true),
            new("id", typeof(byte[]), false, "binary(8)", isRowVersion: true),
        ];

        // Act
        var eligibility = definitions.Select(MySqlSafeMigrationCatalogSqlBuilder.CanUseRequiredColumnAddProtocol);

        // Assert
        Assert.All(eligibility, eligible => Assert.False(eligible));
    }

    /// <summary>Missing qualification precedes the empty-row decision even on the early lazy guard path.</summary>
    /// <param name="mariaDb">Whether table-scoped MariaDB CHECK rules apply.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingGuardRequiresEngineModesAndCollisionAbsence(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var plan = Capture(context, new EnsureColumnIntent("items", Column()), SafeMigrationPolicy.ThrowIfDifferent);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());
        var protocol = Assert.IsType<MySqlSafeMigrationRequiredColumnAddPlan>(plan.RequiredColumnAddPlan);
        var bound = plan with
        {
            RequiredColumnAddPlan = protocol with { StatusBinding = "@atomic_status" },
        };

        // Act
        var status = plan.RenderRequiredColumnAddStatusExpression(parameterizer.Add);
        var guard = bound.RenderStateEvaluationGuardFailureExpression(parameterizer.Add);
        var codes = bound.RenderClassificationCodeExpression(parameterizer.Add);

        // Assert
        Assert.True(protocol.SupportsCheck);
        Assert.Contains("UPPER(atomic_table.ENGINE) = 'INNODB'", status, StringComparison.Ordinal);
        Assert.Contains("STRICT_TRANS_TABLES", status, StringComparison.Ordinal);
        Assert.Contains("STRICT_ALL_TABLES", status, StringComparison.Ordinal);
        Assert.Equal(mariaDb, status.Contains("@@SESSION.check_constraint_checks = 1", StringComparison.Ordinal));
        Assert.Contains("INFORMATION_SCHEMA.TABLE_CONSTRAINTS", status, StringComparison.Ordinal);
        Assert.Contains("CASE WHEN (EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS", status,
            StringComparison.Ordinal);
        Assert.StartsWith("CASE WHEN (@atomic_status) <> 'fresh' THEN 'unsupported'", guard, StringComparison.Ordinal);
        Assert.DoesNotContain("INFORMATION_SCHEMA.TABLE_CONSTRAINTS", guard, StringComparison.Ordinal);
        Assert.True(guard.IndexOf("THEN 'unsupported'", StringComparison.Ordinal)
            < guard.IndexOf("THEN 'data_blocked'", StringComparison.Ordinal));
        Assert.Contains("column_atomic_add_unavailable", codes, StringComparison.Ordinal);
        Assert.Contains("column_atomic_add_guard_conflict", codes, StringComparison.Ordinal);
        Assert.Contains("column_set_null_dependency", codes, StringComparison.Ordinal);
    }

    /// <summary>Resume owns one exact nullable target and CHECK, never a broad name prefix or semantic alias.</summary>
    /// <param name="mariaDb">Whether MariaDB table-local CHECK ownership applies.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ResumeRequiresExactNullableTargetAndOwnedEnforcedCheck(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var operation = GuidColumn();
        var definition = SafeMigrationExpectedDefinitionFactory.From(operation);
        var fingerprint = SafeMigrationContractFingerprint.Create(
        [
            new SafeMigrationOperation(new EnsureColumnIntent("items", definition),
                SafeMigrationPolicy.ThrowIfDifferent),
        ]);
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.ThrowIfDifferent);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());

        // Act
        var resume = plan.RenderRequiredColumnAddResumeExpression(parameterizer.Add);
        var postcondition = plan.RenderPostcondition(parameterizer.Add);

        // Assert
        Assert.Contains("c.IS_NULLABLE = 'YES'", resume, StringComparison.Ordinal);
        Assert.Contains("CAST(COALESCE(c.COLUMN_COMMENT, '') AS BINARY) = ", resume, StringComparison.Ordinal);
        Assert.Contains("c.COLUMN_DEFAULT IS NULL", resume, StringComparison.Ordinal);
        Assert.Contains("SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS", resume, StringComparison.Ordinal);
        Assert.Contains("= 1", resume, StringComparison.Ordinal);
        Assert.Contains("tc.CONSTRAINT_TYPE = 'CHECK'", resume, StringComparison.Ordinal);
        Assert.Contains("CAST(tc.CONSTRAINT_NAME AS BINARY) = CAST(", resume, StringComparison.Ordinal);
        Assert.Contains("CAST(tc.TABLE_NAME AS BINARY) = CAST(", resume, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(tc.CONSTRAINT_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", resume, StringComparison.Ordinal);
        Assert.Contains("CAST(c.TABLE_NAME AS BINARY) = CAST(", resume, StringComparison.Ordinal);
        Assert.Contains("CAST(c.COLUMN_NAME AS BINARY) = CAST(", resume, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(c.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", resume, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(cc.CONSTRAINT_NAME AS BINARY) = CAST(tc.CONSTRAINT_NAME AS BINARY)",
            resume, StringComparison.Ordinal);
        Assert.DoesNotContain("UPPER(tc.CONSTRAINT_NAME)", resume, StringComparison.Ordinal);
        Assert.DoesNotContain("LOWER(tc.CONSTRAINT_NAME)", resume, StringComparison.Ordinal);
        Assert.Contains("nullable_fk.DELETE_RULE = 'SET NULL' OR nullable_fk.UPDATE_RULE = 'SET NULL'", resume,
            StringComparison.Ordinal);
        Assert.DoesNotContain("nullable_fk.DELETE_RULE = 'CASCADE'", resume, StringComparison.Ordinal);
        Assert.Contains("CAST(LOWER(CONVERT(cc.CHECK_CLAUSE USING utf8mb4)) AS BINARY)", resume,
            StringComparison.Ordinal);
        Assert.Contains("CAST(cc.CHECK_CLAUSE AS BINARY) IN (", resume, StringComparison.Ordinal);
        Assert.Equal(mariaDb, resume.Contains("UPPER(c.COLUMN_DEFAULT) = 'NULL'", StringComparison.Ordinal));
        Assert.Equal(!mariaDb, resume.Contains("tc.ENFORCED = 'YES'", StringComparison.Ordinal));
        Assert.Equal(mariaDb, resume.Contains("cc.TABLE_NAME = tc.TABLE_NAME", StringComparison.Ordinal));
        Assert.DoesNotContain("tc.CONSTRAINT_NAME <>", resume, StringComparison.Ordinal);
        Assert.DoesNotContain(" LIKE ", resume, StringComparison.Ordinal);
        Assert.DoesNotContain("EXISTS (SELECT 1 FROM `items`", resume, StringComparison.Ordinal);
        Assert.Contains(command.Parameters.Cast<DbParameter>(),
            parameter => Equals(parameter.Value, "`id` IS NOT NULL"));
        Assert.Contains("NOT (EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS", postcondition,
            StringComparison.Ordinal);
        Assert.DoesNotContain("STRICT_TRANS_TABLES", postcondition, StringComparison.Ordinal);
        Assert.False(operation.IsNullable);
        Assert.True(Assert.IsType<bool>(operation["Doka:MySql:RequiresExplicitBackfill"]));
        Assert.Equal(fingerprint, SafeMigrationContractFingerprint.Create(
        [
            new SafeMigrationOperation(new EnsureColumnIntent("items", definition),
                SafeMigrationPolicy.ThrowIfDifferent),
        ]));
    }

    /// <summary>Every policy retains a separate resume certificate and bounded NULL rejection.</summary>
    [Fact]
    public void ResumeIsApplyCertifiedAcrossPoliciesWithoutInferringEmptyRows()
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        var intent = new EnsureColumnIntent("items", Column());
        var policies = Enum.GetValues<SafeMigrationPolicy>();

        // Act
        var plans = policies.Select(policy => Capture(context, intent, policy)).ToArray();

        // Assert
        Assert.All(plans, plan =>
        {
            Assert.IsType<MySqlSafeMigrationRequiredColumnAddPlan>(plan.RequiredColumnAddPlan);
            var probe = Assert.IsType<MySqlSafeMigrationNullabilityDataProbe>(plan.NullabilityDataProbe);

            Assert.Contains("THEN 'data_blocked' ELSE 'missing'", plan.StateExpression, StringComparison.Ordinal);
            Assert.Contains("column_atomic_add_resume", plan.ClassificationCodeExpression!, StringComparison.Ordinal);
            Assert.Contains(" IS NULL LIMIT 1)", probe.BlockedExpression, StringComparison.Ordinal);
            Assert.DoesNotContain(" IS NULL LIMIT 1)", MySqlSafeMigrationRequiredColumnAddPlan.ResumeExpression,
                StringComparison.Ordinal);
        });
    }

    /// <summary>Excluded native TIMESTAMP rejects only missing work, retaining exact existing contracts.</summary>
    [Fact]
    public void ExcludedTimestampUsesMissingOnlyRejection()
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        var definition = new ExpectedColumnDefinition("id", typeof(DateTime), false, "timestamp(6)");

        // Act
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.ThrowIfDifferent);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Null(plan.RequiredColumnAddPlan);
        Assert.Equal("'unsupported'", plan.StateEvaluationGuardFailureExpression);
        Assert.Contains("THEN 'unsupported'", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("THEN 'matching'", plan.StateExpression, StringComparison.Ordinal);
        Assert.Contains("column_atomic_add_unavailable", plan.ClassificationCodeExpression!, StringComparison.Ordinal);
    }

    /// <summary>Mode-independent identifier quoting also binds the exact CHECK-expression candidates.</summary>
    [Fact]
    public void EscapedColumnNameRemainsExactInOwnedCheckCandidates()
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        const string name = "odd`'\\column";
        var definition = new ExpectedColumnDefinition(name, typeof(int), false, "int");
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.ThrowIfDifferent);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());
        var expected = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(name) + " IS NOT NULL";

        // Act
        var resume = plan.RenderRequiredColumnAddResumeExpression(parameterizer.Add);

        // Assert
        Assert.Contains(command.Parameters.Cast<DbParameter>(), parameter => Equals(parameter.Value, expected));
        Assert.Contains("CAST(c.COLUMN_NAME AS BINARY) = CAST(", resume, StringComparison.Ordinal);
        Assert.DoesNotContain(name, resume, StringComparison.Ordinal);
    }

    /// <summary>Protocol ownership follows native owner case modes without weakening exact CHECK identity.</summary>
    /// <param name="mariaDb">Whether MariaDB's table-local CHECK ownership is used.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MixedCaseProtocolOwnerUsesNativeTableNameModes(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var intent = new EnsureColumnIntent("MixedItems", Column());
        var plan = Capture(context, intent, SafeMigrationPolicy.ThrowIfDifferent);
        var values = RenderValues(context, plan);
        var guard = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(intent);

        // Act
        var status = plan.RenderPreparedRequiredColumnAddStatusExpression(values);
        var terminal = plan.RenderPreparedExecutionPostcondition(values);

        // Assert
        Assert.Contains("CASE WHEN @@lower_case_table_names = 0 THEN "
            + "CAST(tc.TABLE_NAME AS BINARY) = CAST('MixedItems' AS BINARY) "
            + "WHEN @@lower_case_table_names IN (1, 2) THEN "
            + "CAST(LOWER(tc.TABLE_NAME) AS BINARY) = CAST(LOWER('MixedItems') AS BINARY) ELSE FALSE END", status,
            StringComparison.Ordinal);
        Assert.Contains("CASE WHEN @@lower_case_table_names = 0 THEN "
            + "CAST(c.TABLE_NAME AS BINARY) = CAST('MixedItems' AS BINARY) "
            + "WHEN @@lower_case_table_names IN (1, 2) THEN "
            + "CAST(LOWER(c.TABLE_NAME) AS BINARY) = CAST(LOWER('MixedItems') AS BINARY) ELSE FALSE END", status,
            StringComparison.Ordinal);
        Assert.Contains("CAST(tc.CONSTRAINT_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY) "
            + "WHEN @@lower_case_table_names IN (1, 2) THEN "
            + "CAST(LOWER(tc.CONSTRAINT_SCHEMA) AS BINARY) = CAST(LOWER(DATABASE()) AS BINARY) ELSE FALSE END", status,
            StringComparison.Ordinal);
        Assert.Contains("CAST(c.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY) "
            + "WHEN @@lower_case_table_names IN (1, 2) THEN "
            + "CAST(LOWER(c.TABLE_SCHEMA) AS BINARY) = CAST(LOWER(DATABASE()) AS BINARY) ELSE FALSE END", status,
            StringComparison.Ordinal);
        Assert.Contains("CAST(tc.CONSTRAINT_NAME AS BINARY) = CAST('" + guard + "' AS BINARY)", status,
            StringComparison.Ordinal);
        Assert.Contains("CAST(c.COLUMN_NAME AS BINARY) = CAST('id' AS BINARY)", status, StringComparison.Ordinal);
        Assert.Contains("CAST(cc.CHECK_CLAUSE AS BINARY) IN (", status, StringComparison.Ordinal);
        Assert.DoesNotContain("LOWER(tc.CONSTRAINT_NAME)", status, StringComparison.Ordinal);
        Assert.DoesNotContain("LOWER(c.COLUMN_NAME)", status, StringComparison.Ordinal);
        Assert.DoesNotContain("LOWER(cc.CHECK_CLAUSE)", status, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(c.TABLE_NAME AS BINARY) = CAST('MixedItems' AS BINARY)", terminal, StringComparison.Ordinal);
        Assert.Contains("CAST(LOWER(c.TABLE_NAME) AS BINARY) = CAST(LOWER('MixedItems') AS BINARY)", terminal,
            StringComparison.Ordinal);
        Assert.Equal("MixedItems", intent.Table);
    }

    /// <summary>A case-distinct catalog shadow cannot supply presence, ordinary matching or final completion.</summary>
    /// <param name="mariaDb">Whether MariaDB catalog syntax is selected.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProtocolPresenceAndCompletionKeepNativeOwnerIdentity(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var original = Capture(context, new EnsureColumnIntent("MixedItems", Column()),
            SafeMigrationPolicy.RepairIfSafe);
        var protocol = Assert.IsType<MySqlSafeMigrationRequiredColumnAddPlan>(original.RequiredColumnAddPlan);
        var plan = original with
        {
            RequiredColumnAddPlan = protocol with { StatusBinding = "@atomic_status" },
        };
        var values = RenderValues(context, plan);

        // Act
        var prerequisite = plan.RenderPreparedPrerequisiteExpression(values);
        var presence = plan.RenderPreparedStateEvaluationGuardExpression(values);
        var nullable = plan.RenderPreparedNullabilityColumnExpression(values);
        var state = plan.RenderPreparedStateExpression(values);
        var terminal = plan.RenderPreparedExecutionPostcondition(values);
        var status = plan.RenderPreparedRequiredColumnAddStatusExpression(values);

        // Assert
        Assert.All(new[] { presence, nullable, state, terminal }, expression =>
        {
            Assert.Contains(
                "CAST(c.TABLE_NAME AS BINARY) = CAST('MixedItems' AS BINARY)", expression, StringComparison.Ordinal);
            Assert.Contains("CAST(LOWER(c.TABLE_NAME) AS BINARY) = CAST(LOWER('MixedItems') AS BINARY)", expression,
                StringComparison.Ordinal);
            Assert.Contains(
                "CAST(c.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", expression, StringComparison.Ordinal);
            Assert.Contains("ELSE FALSE END", expression, StringComparison.Ordinal);
            Assert.DoesNotContain("CAST(c.COLUMN_NAME AS BINARY)", expression, StringComparison.Ordinal);
        });
        Assert.Contains("CAST(t.TABLE_NAME AS BINARY) = CAST('MixedItems' AS BINARY)", state, StringComparison.Ordinal);
        Assert.Contains("CAST(t.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", state, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(t.TABLE_NAME AS BINARY) = CAST('MixedItems' AS BINARY)", prerequisite, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(t.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", prerequisite, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(atomic_table.TABLE_NAME AS BINARY) = CAST('MixedItems' AS BINARY)", status, StringComparison.Ordinal);
        Assert.Contains(
            "CAST(atomic_table.TABLE_SCHEMA AS BINARY) = CAST(DATABASE() AS BINARY)", status, StringComparison.Ordinal);
        Assert.Contains("CAST(c.COLUMN_NAME AS BINARY) = CAST('id' AS BINARY)", status, StringComparison.Ordinal);
        Assert.DoesNotContain("@atomic_status", terminal, StringComparison.Ordinal);
    }

    /// <summary>SQL-default and excluded missing additions cannot borrow a complete case-distinct shadow.</summary>
    /// <param name="sqlDefault">Whether the target uses the separately qualified SQL-default ADD path.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OtherQualifiedRequiredAddsKeepNativeOwnerIdentity(bool sqlDefault)
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        var definition = sqlDefault
            ? new ExpectedColumnDefinition("id", typeof(int), false, "int",
                defaultValue: SafeMigrationDefaultValue.Sql(new SafeMigrationSqlLiteralExpression(null)))
            : new ExpectedColumnDefinition("id", typeof(DateTime), false, "timestamp(6)");
        var plan = Capture(context, new EnsureColumnIntent("MixedItems", definition),
            SafeMigrationPolicy.ThrowIfDifferent);
        var values = RenderValues(context, plan);

        // Act
        var presence = plan.RenderPreparedStateEvaluationGuardExpression(values);
        var matching = plan.RenderPreparedPostcondition(values);
        var prerequisite = plan.RenderPreparedPrerequisiteExpression(values);
        var state = plan.RenderPreparedStateExpression(values);

        // Assert
        Assert.Null(plan.RequiredColumnAddPlan);
        Assert.All(new[] { presence, matching, state }, expression =>
        {
            Assert.Contains(
                "CAST(c.TABLE_NAME AS BINARY) = CAST('MixedItems' AS BINARY)", expression, StringComparison.Ordinal);
            Assert.Contains("CAST(LOWER(c.TABLE_NAME) AS BINARY) = CAST(LOWER('MixedItems') AS BINARY)", expression,
                StringComparison.Ordinal);
            Assert.DoesNotContain("CAST(c.COLUMN_NAME AS BINARY)", expression, StringComparison.Ordinal);
        });
        Assert.Contains(
            "CAST(t.TABLE_NAME AS BINARY) = CAST('MixedItems' AS BINARY)", prerequisite, StringComparison.Ordinal);
        Assert.Contains(sqlDefault ? "column_required_sql_add_unavailable" : "column_atomic_add_unavailable",
            plan.ClassificationCodeExpression!, StringComparison.Ordinal);
    }

    /// <summary>Native owner aliases do not merge raw authored guard identities or ordinary comparisons.</summary>
    [Fact]
    public void DifferentlyCasedContractsRetainDistinctRecoveryNames()
    {
        // Arrange
        using var context = CreateContext(mariaDb: true);
        var upper = new EnsureColumnIntent("MixedItems", Column());
        var lower = new EnsureColumnIntent("mixeditems", Column());
        var nullable = new EnsureColumnIntent("MixedItems",
            new ExpectedColumnDefinition("id", typeof(int), true, "int"));

        // Act
        var upperName = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(upper);
        var lowerName = MySqlSafeMigrationCatalogSqlBuilder.GetRequiredColumnAddGuardName(lower);
        var ordinary = Capture(context, nullable, SafeMigrationPolicy.RepairIfSafe);
        var matching = ordinary.RenderPreparedPostcondition(RenderValues(context, ordinary));

        // Assert
        Assert.NotEqual(upperName, lowerName);
        Assert.Null(ordinary.RequiredColumnAddPlan);
        Assert.DoesNotContain("@@lower_case_table_names", matching, StringComparison.Ordinal);
        Assert.DoesNotContain("CAST(c.TABLE_NAME AS BINARY)", matching, StringComparison.Ordinal);
    }

    /// <summary>One bound catalog status feeds compact consumers while terminal proof stays fresh.</summary>
    /// <param name="mariaDb">Whether MariaDB catalog relations are selected.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BoundStatusDoesNotRepeatOwnedCheckProof(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var original = Capture(context, new EnsureColumnIntent("items", Column()), SafeMigrationPolicy.RepairIfSafe);
        var protocol = Assert.IsType<MySqlSafeMigrationRequiredColumnAddPlan>(original.RequiredColumnAddPlan);
        var plan = original with
        {
            RequiredColumnAddPlan = protocol with { StatusBinding = "@atomic_status" },
        };
        var values = RenderValues(context, plan);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());

        // Act
        var status = plan.RenderPreparedRequiredColumnAddStatusExpression(values);
        string[] consumers =
        [
            plan.RenderPreparedStateExpression(values),
            plan.RenderPreparedStateEvaluationGuardFailureExpression(values),
            plan.RenderClassificationCodeExpression(parameterizer.Add),
            plan.RenderPreparedNullabilityRepairInvariantExpression(values),
            plan.RenderPreparedRequiredColumnAddResumeExpression(values),
        ];
        var terminal = plan.RenderPreparedExecutionPostcondition(values);

        // Assert
        Assert.Contains("INFORMATION_SCHEMA.CHECK_CONSTRAINTS", status, StringComparison.Ordinal);
        Assert.DoesNotContain("@atomic_status", status, StringComparison.Ordinal);
        Assert.DoesNotContain(MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder, status,
            StringComparison.Ordinal);
        Assert.DoesNotContain("EXISTS (SELECT 1 FROM `items`", status, StringComparison.Ordinal);
        Assert.True(status.IndexOf("THEN 'complete'", StringComparison.Ordinal)
            < status.IndexOf("UPPER(atomic_table.ENGINE)", StringComparison.Ordinal));
        Assert.All(consumers, expression =>
        {
            Assert.Contains("@atomic_status", expression, StringComparison.Ordinal);
            Assert.DoesNotContain("INFORMATION_SCHEMA.CHECK_CONSTRAINTS", expression, StringComparison.Ordinal);
            Assert.DoesNotContain(MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder, expression,
                StringComparison.Ordinal);
        });
        Assert.Equal("(@atomic_status) = 'staged'", consumers[^1]);
        Assert.Contains("c.IS_NULLABLE = 'NO'", terminal, StringComparison.Ordinal);
        Assert.Contains("NOT (EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS", terminal,
            StringComparison.Ordinal);
        Assert.DoesNotContain("@atomic_status", terminal, StringComparison.Ordinal);
        Assert.Null(protocol.StatusBinding);
    }

    /// <summary>Authored comments and escaped identifiers cannot be interpreted as structural status tokens.</summary>
    [Fact]
    public void StatusBindingPreservesQuotedCommentAndEscapedColumnName()
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        const string token = MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder;
        const string name = "odd`" + token;
        var definition = new ExpectedColumnDefinition(name, typeof(int), false, "int", comment: token);
        var original = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.RepairIfSafe);
        var protocol = Assert.IsType<MySqlSafeMigrationRequiredColumnAddPlan>(original.RequiredColumnAddPlan);
        var plan = original with
        {
            RequiredColumnAddPlan = protocol with { StatusBinding = "@atomic_status" },
        };
        var values = RenderValues(context, plan);
        var identifier = context.GetService<ISqlGenerationHelper>().DelimitIdentifier(name);

        // Act
        var status = plan.RenderPreparedRequiredColumnAddStatusExpression(values);
        var state = plan.RenderPreparedStateExpression(values);
        var rows = plan.RenderPreparedNullabilityDataProbeBlockedExpression(values);

        // Assert
        Assert.Contains("CAST(COALESCE(c.COLUMN_COMMENT, '') AS BINARY) = '" + token + "'",
            status, StringComparison.Ordinal);
        Assert.Contains("CAST(COALESCE(c.COLUMN_COMMENT, '') AS BINARY) = '" + token + "'",
            state, StringComparison.Ordinal);
        Assert.Contains("@atomic_status", state, StringComparison.Ordinal);
        Assert.Equal("EXISTS (SELECT 1 FROM `items` WHERE " + identifier + " IS NULL LIMIT 1)", rows);
        Assert.DoesNotContain("@atomic_status", rows, StringComparison.Ordinal);
        Assert.Equal(token, definition.Comment);
        Assert.Equal(name, definition.Name);
    }

    /// <summary>A structured default containing the token remains a value without requiring a protocol.</summary>
    [Fact]
    public void StructuredLiteralStatusTokenDoesNotAcquireProtocolBinding()
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        const string token = MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder;
        var expression = new SafeMigrationSqlLiteralExpression(token);
        var definition = new ExpectedColumnDefinition("id", typeof(string), true, "varchar(100)",
            defaultValue: SafeMigrationDefaultValue.Sql(expression));
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.ThrowIfDifferent);
        var values = RenderValues(context, plan);

        // Act
        var matching = plan.RenderPreparedPostcondition(values);
        var state = plan.RenderPreparedStateExpression(values);

        // Assert
        Assert.Null(plan.RequiredColumnAddPlan);
        Assert.Contains(token, matching, StringComparison.Ordinal);
        Assert.Contains(token, state, StringComparison.Ordinal);
        Assert.DoesNotContain("@atomic_status", matching, StringComparison.Ordinal);
        Assert.Same(expression, definition.DefaultValue.StructuredExpression);
    }

    /// <summary>A status classifier cannot recursively request its own unresolved structural status.</summary>
    [Fact]
    public void RecursiveStatusTemplateFailsClosed()
    {
        // Arrange
        var plan = new MySqlSafeMigrationRuntimePlan("'matching'", "TRUE", SafeMigrationRepairCapability.None, "FALSE")
        {
            RequiredColumnAddPlan = new MySqlSafeMigrationRequiredColumnAddPlan(
                "guard", MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder, false),
        };

        // Act
        var error = Assert.Throws<InvalidOperationException>(
            () => plan.RenderPreparedRequiredColumnAddStatusExpression([]));

        // Assert
        Assert.Contains("cannot depend on its own status marker", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Required TIMESTAMP repair qualifies explicit semantics without changing matching.</summary>
    /// <param name="mariaDb">Whether the captured engine is MariaDB.</param>
    /// <param name="ensure">Whether ordinary Ensure repair rather than Alter is captured.</param>
    /// <param name="sourceNullable">Whether Alter also performs nullability tightening.</param>
    /// <param name="realDefault">Whether the target also carries an approved fixed temporal default.</param>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(true, false, true, true)]
    [InlineData(true, true, false, true)]
    public void TimestampRepairRequiresExplicitSemantics(
        bool mariaDb,
        bool ensure,
        bool sourceNullable,
        bool realDefault
    )
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var target = new ExpectedColumnDefinition("id", typeof(DateTime), false, "timestamp(6)", comment: "target",
            defaultValue: realDefault ? SafeMigrationDefaultValue.Literal(new DateTime(2026, 1, 1)) : null);
        var source = new ExpectedColumnDefinition("id", typeof(DateTime), sourceNullable, "timestamp(6)");
        SafeMigrationIntent intent = ensure
            ? new EnsureColumnIntent("items", target)
            : new AlterColumnIntent("items", target, source);
        var plan = Capture(context, intent, SafeMigrationPolicy.RepairIfSafe);
        var values = RenderValues(context, plan);

        // Act
        var repair = plan.RenderPreparedRepairPrecondition(values);
        var matching = plan.RenderPreparedPostcondition(values);
        var eligibility = plan.NullabilityDataProbe is not null
            ? plan.RenderPreparedNullabilityRepairInvariantExpression(values)
            : repair;

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.Contains("@@SESSION.explicit_defaults_for_timestamp = 1", eligibility, StringComparison.Ordinal);
        Assert.DoesNotContain("@@SESSION.explicit_defaults_for_timestamp", matching, StringComparison.Ordinal);
        Assert.DoesNotContain("SET @@SESSION.explicit_defaults_for_timestamp", eligibility, StringComparison.Ordinal);
        Assert.Null(plan.RequiredColumnAddPlan);
    }

    /// <summary>Only required TIMESTAMP targets acquire the native timestamp-mode repair qualifier.</summary>
    /// <param name="nullable">Whether the target remains nullable.</param>
    /// <param name="realDefault">Whether the target has an explicit non-null default.</param>
    /// <param name="storeType">The authored temporal store type.</param>
    [Theory]
    [InlineData(true, false, "timestamp(6)")]
    [InlineData(false, true, "timestamp(6)")]
    [InlineData(false, false, "datetime(6)")]
    public void ExplicitTimestampSemanticsDoNotExpandToOtherTargets(bool nullable, bool realDefault, string storeType)
    {
        // Arrange
        using var context = CreateContext(mariaDb: true);
        var target = new ExpectedColumnDefinition("id", typeof(DateTime), nullable, storeType,
            defaultValue: realDefault ? SafeMigrationDefaultValue.Literal(new DateTime(2026, 1, 1)) : null);
        var source = new ExpectedColumnDefinition("id", typeof(DateTime), true, storeType);
        var plan = Capture(context, new AlterColumnIntent("items", target, source), SafeMigrationPolicy.RepairIfSafe);
        var values = RenderValues(context, plan);

        // Act
        var repair = plan.RenderPreparedRepairPrecondition(values);

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        var eligibility = plan.NullabilityDataProbe is not null
            ? plan.RenderPreparedNullabilityRepairInvariantExpression(values)
            : repair;

        Assert.Equal(!nullable && storeType.StartsWith("timestamp", StringComparison.Ordinal),
            eligibility.Contains("@@SESSION.explicit_defaults_for_timestamp = 1", StringComparison.Ordinal));
        Assert.DoesNotContain("@@SESSION.explicit_defaults_for_timestamp", plan.RenderPreparedPostcondition(values),
            StringComparison.Ordinal);
    }

    /// <summary>Opaque SQL stays rejected while structured NULL obeys the engine's representation bounds.</summary>
    /// <param name="mariaDb">Whether MariaDB rejects a required SQL NULL default.</param>
    /// <param name="structured">Whether the default uses a structured NULL literal.</param>
    /// <param name="nullable">Whether NULL defaults are representable for the target.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void RequiredSqlNullDefaultUsesEngineSpecificRepresentation(
        bool mariaDb,
        bool structured,
        bool nullable
    )
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var defaultValue = structured
            ? SafeMigrationDefaultValue.Sql(new SafeMigrationSqlLiteralExpression(null))
            : SafeMigrationDefaultValue.Sql("(NULL)");
        var definition = new ExpectedColumnDefinition("id", typeof(int), nullable, "int", defaultValue: defaultValue);

        // Act
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.RepairIfSafe);

        // Assert
        var expectedCode = !structured
            ? "opaque_sql_expression"
            : mariaDb && !nullable ? "literal_default_catalog_representation" : null;

        Assert.Equal(expectedCode is not null, plan.IsStaticallyUnsupported);
        Assert.Equal(expectedCode, plan.UnsupportedCode);
        Assert.Same(defaultValue, definition.DefaultValue);
    }

    /// <summary>An explicitly parsed NULL tree is structured without weakening the opaque SQL gate.</summary>
    /// <param name="mariaDb">Whether MariaDB rejects the required structured SQL NULL target.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ParsedNullDefaultRetainsEngineSpecificTargetQualification(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var parsed = SafeMigrationSqlExpressionParser.TryParse("NULL", out var expression);
        var definition = new ExpectedColumnDefinition("id", typeof(int), false, "int",
            defaultValue: SafeMigrationDefaultValue.Sql(expression
                ?? throw new InvalidOperationException("The SQL NULL fixture did not parse.")));

        // Act
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.RepairIfSafe);

        // Assert
        Assert.True(parsed);
        Assert.Null(Assert.IsType<SafeMigrationSqlLiteralExpression>(expression).Value);
        Assert.Equal(mariaDb, plan.IsStaticallyUnsupported);
        Assert.Equal(mariaDb ? "literal_default_catalog_representation" : null, plan.UnsupportedCode);
    }

    /// <summary>Uncast ISO temporal literals retain exact values while catalog precision adds zeros.</summary>
    /// <param name="storeType">The resolved timestamp or datetime precision.</param>
    /// <param name="value">The caller's exact SQL literal payload.</param>
    /// <param name="expected">The value serialized at the target precision.</param>
    [Theory]
    [InlineData("timestamp(6)", "2001-01-02 03:04:05", "2001-01-02 03:04:05.000000")]
    [InlineData("TIMESTAMP(3)", "2001-01-02 03:04:05.12", "2001-01-02 03:04:05.120")]
    [InlineData("datetime(6)", "2001-01-02 03:04:05.123456", "2001-01-02 03:04:05.123456")]
    [InlineData("datetime(2)", "2001-01-02 03:04:05", "2001-01-02 03:04:05.00")]
    [InlineData("timestamp", "2001-01-02 03:04:05", "2001-01-02 03:04:05")]
    public void StructuredTemporalSqlLiteralCatalogValuePadsOnlyExactPrecision(
        string storeType,
        string value,
        string expected
    )
    {
        // Arrange
        var original = value;

        // Act
        var display = MySqlSafeMigrationCatalogSqlBuilder.BuildTemporalSqlLiteralCatalogValue(storeType, value);

        // Assert
        Assert.Equal(expected, display);
        Assert.Equal(original, value);
    }

    /// <summary>Catalog candidates cannot invent calendar, timezone, time-only or precision conversions.</summary>
    /// <param name="storeType">The target family and precision.</param>
    /// <param name="value">The unsupported or lossy SQL literal spelling.</param>
    [Theory]
    [InlineData("timestamp(6)", "2001-02-30 03:04:05")]
    [InlineData("timestamp(6)", "2001-01-02T03:04:05")]
    [InlineData("timestamp(6)", "2001-01-02 03:04:05Z")]
    [InlineData("timestamp(6)", "2001-01-02 03:04:05+01:00")]
    [InlineData("timestamp(6)", "03:04:05")]
    [InlineData("timestamp(6)", "2001-01-02")]
    [InlineData("timestamp(6)", "2001-01-02 03:04:05.")]
    [InlineData("timestamp(6)", "2001-01-02 03:04:05.1234567")]
    [InlineData("timestamp(3)", "2001-01-02 03:04:05.1234")]
    [InlineData("timestamp", "2001-01-02 03:04:05.1")]
    [InlineData("timestamp(7)", "2001-01-02 03:04:05")]
    [InlineData("time(6)", "2001-01-02 03:04:05")]
    [InlineData("varchar(40)", "2001-01-02 03:04:05")]
    public void StructuredTemporalSqlLiteralCatalogValueDoesNotNormalizeOtherDomains(string storeType, string value)
    {
        // Arrange
        var original = value;

        // Act
        var display = MySqlSafeMigrationCatalogSqlBuilder.BuildTemporalSqlLiteralCatalogValue(storeType, value);

        // Assert
        Assert.Null(display);
        Assert.Equal(original, value);
    }

    /// <summary>Temporal catalog padding preserves the SQL-default kind and caller-owned uncast expression.</summary>
    /// <param name="mariaDb">Whether quoted MariaDB catalog display is used.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StructuredTimestampDefaultMatchingIncludesExactNativePrecision(bool mariaDb)
    {
        // Arrange
        using var context = CreateContext(mariaDb);
        var literal = SafeMigrationSql.Literal("2001-01-02 03:04:05");
        var defaultValue = SafeMigrationDefaultValue.Sql(literal);
        var target = new ExpectedColumnDefinition("id", typeof(DateTime), false, "timestamp(6)",
            defaultValue: defaultValue);
        var plan = Capture(context, new EnsureColumnIntent("items", target), SafeMigrationPolicy.RepairIfSafe);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());

        // Act
        var matching = plan.RenderPostcondition(parameterizer.Add);

        // Assert
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.Contains(mariaDb ? "c.COLUMN_DEFAULT IN (" : "ELSE CAST(c.COLUMN_DEFAULT AS BINARY) IN (",
            matching, StringComparison.Ordinal);
        Assert.Contains(command.Parameters.Cast<DbParameter>(),
            parameter => Equals(parameter.Value, "'2001-01-02 03:04:05.000000'"));
        Assert.DoesNotContain(command.Parameters.Cast<DbParameter>(),
            parameter => Equals(parameter.Value, "'2001-01-02 03:04:06.000000'"));
        Assert.Equal(SafeMigrationDefaultValueKind.Sql, target.DefaultValue.Kind);
        Assert.Same(defaultValue, target.DefaultValue);
        Assert.Same(literal, target.DefaultValue.StructuredExpression);
        Assert.Equal("2001-01-02 03:04:05", Assert.IsType<SafeMigrationSqlLiteralExpression>(literal).Value);
    }

    /// <summary>Generated temporal defaults require an exact expression display.</summary>
    [Fact]
    public void MySqlTemporalSqlLiteralSeparatesGeneratedAndPlainCatalogIdentity()
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        const string value = "2001-01-02 03:04:05";
        const string expressionDisplay = "_utf8mb4\\'2001-01-02 03:04:05\\'";
        var target = new ExpectedColumnDefinition("id", typeof(DateTime), false, "timestamp(6)",
            defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value)));
        var plan = Capture(context, new EnsureColumnIntent("items", target), SafeMigrationPolicy.RepairIfSafe);
        var values = RenderValues(context, plan);
        var expressionLiteral = values[Array.FindIndex(plan.ParameterValues,
            parameter => Equals(parameter.Value, expressionDisplay))];
        var stableLiteral = values[Array.FindIndex(plan.ParameterValues,
            parameter => Equals(parameter.Value, "convert(0x323030312d30312d30322030333a30343a3035 using utf8mb4)"))];

        // Act
        var matching = plan.RenderPreparedPostcondition(values);

        // Assert
        Assert.Contains("CASE WHEN LOCATE('DEFAULT_GENERATED', UPPER(COALESCE(c.EXTRA, ''))) > 0 "
            + "THEN CAST(c.COLUMN_DEFAULT AS BINARY) IN (" + expressionLiteral + ", " + stableLiteral + ")"
            + " ELSE CAST(c.COLUMN_DEFAULT AS BINARY) IN (", matching, StringComparison.Ordinal);
        Assert.Contains(plan.ParameterValues, parameter => Equals(parameter.Value, "2001-01-02 03:04:05.000000"));
        Assert.Contains(plan.ParameterValues,
            parameter => Equals(parameter.Value, "'2001-01-02 03:04:05.000000'"));
        Assert.DoesNotContain(plan.ParameterValues,
            parameter => Equals(parameter.Value, "_utf8mb4\\'2001-01-02 03:04:05.000000\\'"));
        Assert.DoesNotContain(plan.ParameterValues,
            parameter => Equals(parameter.Value, "_latin1\\'2001-01-02 03:04:05\\'"));
        Assert.DoesNotContain(plan.ParameterValues,
            parameter => Equals(parameter.Value, "_utf8mb4\\'2001-01-02 03:04:06\\'"));
        Assert.DoesNotContain(" OR CAST(c.COLUMN_DEFAULT AS BINARY)", matching, StringComparison.Ordinal);
    }

    /// <summary>Invalid temporal quote payloads cannot acquire the validated expression-default display path.</summary>
    [Fact]
    public void MySqlTemporalSqlLiteralDoesNotCertifyQuotePayloadAsCalendarValue()
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        const string value = "2001-01-02 03:04:05'payload";
        var target = new ExpectedColumnDefinition("id", typeof(DateTime), false, "timestamp(6)",
            defaultValue: SafeMigrationDefaultValue.Sql(SafeMigrationSql.Literal(value)));
        var plan = Capture(context, new EnsureColumnIntent("items", target), SafeMigrationPolicy.RepairIfSafe);
        var values = RenderValues(context, plan);

        // Act
        var matching = plan.RenderPreparedPostcondition(values);

        // Assert
        Assert.Null(MySqlSafeMigrationCatalogSqlBuilder.BuildTemporalSqlLiteralCatalogValue("timestamp(6)", value));
        Assert.DoesNotContain("THEN CAST(c.COLUMN_DEFAULT AS BINARY)", matching, StringComparison.Ordinal);
        Assert.DoesNotContain(plan.ParameterValues, parameter => parameter.Value is string text
            && text.StartsWith("_utf8mb4", StringComparison.Ordinal));
        Assert.Equal(value,
            Assert.IsType<SafeMigrationSqlLiteralExpression>(target.DefaultValue.StructuredExpression).Value);
    }

    /// <summary>Required SQL-default missing work qualifies its actual engine and mode on both state paths.</summary>
    /// <param name="repair">Whether existing-column repair also needs the lazy column guard.</param>
    /// <param name="nullDefault">Whether the approved SQL expression is a known literal NULL.</param>
    /// <param name="timestamp">Whether native TIMESTAMP semantics also require qualification.</param>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public void RequiredSqlDefaultMissingWorkHasFreshConversionQualification(
        bool repair,
        bool nullDefault,
        bool timestamp
    )
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        object? value = nullDefault ? null : timestamp ? new DateTime(2026, 1, 1) : 7;
        var defaultValue = SafeMigrationDefaultValue.Sql(new SafeMigrationSqlLiteralExpression(value));
        var definition = new ExpectedColumnDefinition("id", timestamp ? typeof(DateTime) : typeof(int), false,
            timestamp ? "timestamp(6)" : "int", defaultValue: defaultValue);
        var plan = Capture(context, new EnsureColumnIntent("items", definition),
            repair ? SafeMigrationPolicy.RepairIfSafe : SafeMigrationPolicy.ThrowIfDifferent);
        var values = RenderValues(context, plan);
        using var command = new MySqlCommand();
        var parameterizer = new MySqlCatalogQueryParameterizer(
            command, context.GetService<IRelationalTypeMappingSource>());

        // Act
        var state = plan.RenderPreparedStateExpression(values);
        var missing = plan.RenderPreparedStateEvaluationGuardFailureExpression(values);
        var codes = plan.RenderClassificationCodeExpression(parameterizer.Add);
        var matching = plan.RenderPreparedPostcondition(values);

        // Assert
        Assert.True(MySqlSafeMigrationCatalogSqlBuilder.RequiresRequiredSqlDefaultAddQualification(definition));
        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.RequiresUnsupportedMissingRequiredAdd(definition));
        Assert.Null(plan.RequiredColumnAddPlan);
        Assert.False(plan.IsStaticallyUnsupported);
        Assert.StartsWith("CASE WHEN NOT EXISTS (SELECT 1 FROM INFORMATION_SCHEMA.TABLES", state,
            StringComparison.Ordinal);
        Assert.Contains("THEN 'prerequisite_missing'", state, StringComparison.Ordinal);
        Assert.All(new[] { state, missing }, expression =>
        {
            Assert.Contains("UPPER(sql_default_table.ENGINE) = 'INNODB'", expression, StringComparison.Ordinal);
            Assert.Contains("FIND_IN_SET('STRICT_TRANS_TABLES', @@SESSION.sql_mode) > 0", expression,
                StringComparison.Ordinal);
            Assert.Contains("FIND_IN_SET('STRICT_ALL_TABLES', @@SESSION.sql_mode) > 0", expression,
                StringComparison.Ordinal);
            Assert.Equal(timestamp,
                expression.Contains("@@SESSION.explicit_defaults_for_timestamp = 1", StringComparison.Ordinal));
        });
        Assert.Contains("column_required_sql_add_unavailable", codes, StringComparison.Ordinal);
        Assert.Equal(nullDefault,
            missing.Contains("EXISTS (SELECT 1 FROM `items` LIMIT 1)", StringComparison.Ordinal));
        Assert.Equal(nullDefault, missing.Contains("'data_blocked'", StringComparison.Ordinal));
        if (nullDefault)
        {
            Assert.Contains("THEN (CASE WHEN EXISTS (SELECT 1 FROM `items` LIMIT 1) "
                + "THEN 'data_blocked' ELSE 'missing' END) ELSE 'unsupported' END", missing,
                StringComparison.Ordinal);
            Assert.True(missing.IndexOf("UPPER(sql_default_table.ENGINE)", StringComparison.Ordinal)
                < missing.IndexOf("EXISTS (SELECT 1 FROM `items` LIMIT 1)", StringComparison.Ordinal));
            Assert.Contains("EXISTS (SELECT 1 FROM `items` LIMIT 1)", state, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("sql_default_table", matching, StringComparison.Ordinal);
        Assert.DoesNotContain("STRICT_TRANS_TABLES", matching, StringComparison.Ordinal);
        Assert.DoesNotContain("explicit_defaults_for_timestamp", matching, StringComparison.Ordinal);
        Assert.Same(defaultValue, definition.DefaultValue);
    }

    /// <summary>Nullable SQL and approved non-null literal defaults retain their distinct ordinary paths.</summary>
    /// <param name="nullable">Whether the target can accept NULL.</param>
    /// <param name="sqlDefault">Whether the default is a SQL literal rather than a typed CLR literal.</param>
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void SqlDefaultQualificationDoesNotBroadenOtherAddContracts(bool nullable, bool sqlDefault)
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        var defaultValue = sqlDefault
            ? SafeMigrationDefaultValue.Sql(new SafeMigrationSqlLiteralExpression(null))
            : SafeMigrationDefaultValue.Literal(7);
        var definition = new ExpectedColumnDefinition("id", typeof(int), nullable, "int", defaultValue: defaultValue);

        // Act
        var plan = Capture(context, new EnsureColumnIntent("items", definition), SafeMigrationPolicy.ThrowIfDifferent);

        // Assert
        Assert.False(MySqlSafeMigrationCatalogSqlBuilder.RequiresRequiredSqlDefaultAddQualification(definition));
        Assert.DoesNotContain("sql_default_table", plan.StateExpression, StringComparison.Ordinal);
        Assert.Null(plan.RequiredColumnAddPlan);
        Assert.Null(plan.ClassificationCodeExpression);
    }

    /// <summary>A required literal NULL remains invalid before a provider plan can be authored.</summary>
    [Fact]
    public void RequiredLiteralNullCannotBecomeAnImplicitMissingDefault()
    {
        // Arrange
        var defaultValue = SafeMigrationDefaultValue.Literal(null);

        // Act
        var error = Assert.Throws<ArgumentException>(() => new ExpectedColumnDefinition(
            "id", typeof(int), false, "int", defaultValue: defaultValue));

        // Assert
        Assert.Equal("defaultValue", error.ParamName);
        Assert.Contains("literal NULL default requires a nullable column", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Strict conversion rejects raced NULL values even after an approved literal UPDATE.</summary>
    /// <param name="transition">Whether the repair also narrows the string's physical type.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ApprovedDefaultStillRequiresStrictNullableAlter(bool transition)
    {
        // Arrange
        using var context = CreateContext(mariaDb: false);
        var source = new ExpectedColumnDefinition("id", typeof(string), true, "varchar(40)", maxLength: 40);
        var target = new ExpectedColumnDefinition("id", typeof(string), false,
            transition ? "varchar(20)" : "varchar(40)", maxLength: transition ? 20 : 40,
            defaultValue: SafeMigrationDefaultValue.Literal("approved"));
        var plan = Capture(context, new AlterColumnIntent("items", target, source), SafeMigrationPolicy.RepairIfSafe);
        var values = RenderValues(context, plan);

        // Act
        // WHY: Capture intentionally omits transition evidence. The structural
        // oracle supplies resolved probe inputs instead of inventing a source
        // proof or asking the renderer to expand evidence that is absent.
        var repair = plan.RenderPreparedRepairPrecondition(values, "FALSE", "TRUE");
        var matching = plan.RenderPreparedPostcondition(values);
        var physical = plan.NullabilityDataProbe is not null
            ? plan.RenderPreparedNullabilityRepairInvariantExpression(values)
            : repair;

        // Assert
        Assert.Equal(SafeMigrationRepairCapability.Safe, plan.RepairCapability);
        Assert.Equal(transition, plan.DataProbe is not null);
        Assert.Contains("FIND_IN_SET('STRICT_TRANS_TABLES', @@SESSION.sql_mode) > 0", physical,
            StringComparison.Ordinal);
        Assert.Contains("FIND_IN_SET('STRICT_ALL_TABLES', @@SESSION.sql_mode) > 0", physical, StringComparison.Ordinal);
        Assert.DoesNotContain("STRICT_TRANS_TABLES", matching, StringComparison.Ordinal);
        Assert.Equal("approved", target.DefaultValue.GetLiteralValue());
        Assert.True(source.IsNullable);
    }

    /// <summary>Captures generated catalog plans without opening a connection or rendering executable DDL.</summary>
    /// <param name="context">The offline provider context.</param>
    /// <param name="intent">The immutable column intent.</param>
    /// <param name="policy">The caller's ordinary difference policy.</param>
    /// <returns>The captured provider catalog plan.</returns>
    private static MySqlSafeMigrationRuntimePlan Capture(
        DbContext context,
        SafeMigrationIntent intent,
        SafeMigrationPolicy policy
    )
    {
        var operation = new SafeMigrationOperation(intent, policy);
        using var lease = context.GetService<MySqlSafeMigrationPlanCapture>().Begin([operation]);
        _ = context.GetService<IMigrationsSqlGenerator>().Generate([operation], context.Model);

        return Assert.Single(lease.Complete());
    }

    /// <summary>Uses provider literals so quoted user tokens remain visible in prepared-render assertions.</summary>
    /// <param name="context">The offline provider context.</param>
    /// <param name="plan">The captured parameterized catalog plan.</param>
    /// <returns>The typed literals in captured placeholder order.</returns>
    private static string[] RenderValues(DbContext context, MySqlSafeMigrationRuntimePlan plan)
    {
        var source = context.GetService<IRelationalTypeMappingSource>();

        return plan.ParameterValues.Select(value => value.Value is null
            ? "NULL"
            : (value.StoreType is { } storeType
                ? MySqlCatalogTypeMapping.Resolve(source, value.Value, storeType)
                : source.FindMapping(value.Value.GetType())
                    ?? throw new InvalidOperationException("The catalog value has no provider mapping."))
            .GenerateSqlLiteral(value.Value)).ToArray();
    }

    /// <summary>Creates a server-free context with pinned CHECK-capable feature metadata.</summary>
    /// <param name="mariaDb">Whether MariaDB catalog semantics apply.</param>
    /// <returns>The offline SQL-generation context.</returns>
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

    /// <summary>Creates an ordinary intrinsic required target without a default.</summary>
    /// <returns>The immutable integer target.</returns>
    private static ExpectedColumnDefinition Column() => new("id", typeof(int), false, "int");

    /// <summary>Creates real provider metadata while leaving the authored required target unchanged.</summary>
    /// <returns>The annotated authored operation.</returns>
    private static AddColumnOperation GuidColumn()
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
        operation["Doka:MySql:RequiresExplicitBackfill"] = true;

        return operation;
    }
}
