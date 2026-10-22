namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationRunContractTests
{
    private static readonly HashSet<string> s_supportedSchemaKeywords = new(StringComparer.Ordinal)
    {
        "$schema",
        "$id",
        "$ref",
        "$defs",
        "title",
        "type",
        "additionalProperties",
        "required",
        "properties",
        "const",
        "enum",
        "format",
        "minLength",
        "maxLength",
        "pattern",
        "items",
        "maxItems",
        "minimum",
    };

    /// <summary>Requires each caller-supplied identity component to contain a non-whitespace value.</summary>
    /// <param name="instanceId">The pseudonymous instance identity.</param>
    /// <param name="targetMigrationId">The optional target migration identity.</param>
    [Theory]
    [InlineData(" ", null)]
    [InlineData("instance", " ")]
    public void RunOptions_RejectWhitespaceIdentity(
        string instanceId,
        string? targetMigrationId
    )
    {
        // Arrange: The theory supplies the instance and target identities.

        // Act
        var exception = Record.Exception(() =>
        {
            _ = new SafeMigrationRunOptions(instanceId, targetMigrationId);
        });

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Retains the accepted pseudonymous identity, migration and expected fingerprint.</summary>
    [Fact]
    public void RunOptions_RetainExplicitPseudonymousIdentity()
    {
        // Arrange
        var expectedFingerprint = ModelFingerprint();

        // Act
        var options = new SafeMigrationRunOptions("instance-7f3a", "202608170101_Core", expectedFingerprint);

        // Assert
        Assert.Equal("instance-7f3a", options.InstanceId);
        Assert.Equal("202608170101_Core", options.TargetMigrationId);
        Assert.Equal(expectedFingerprint, options.ExpectedModelFingerprint);
    }

    [Theory]
    [InlineData("", 0, 'a')]
    [InlineData(" ", 0, 'a')]
    [InlineData("", 64, 'a')]
    [InlineData("other-model:v1:npgsql_postgresql:sha256:", 64, 'a')]
    [InlineData("safe-relational-model:v0:npgsql_postgresql:sha256:", 64, 'a')]
    [InlineData("safe-relational-model:v1::sha256:", 64, 'a')]
    [InlineData("safe-relational-model:v1: :sha256:", 64, 'a')]
    [InlineData("safe-relational-model:v1:npgsql:postgresql:sha256:", 64, 'a')]
    [InlineData("safe-relational-model:v1:npgsql_postgresql:sha512:", 64, 'a')]
    [InlineData("safe-relational-model:v1:npgsql_postgresql:sha256:", 0, 'a')]
    [InlineData("safe-relational-model:v1:npgsql_postgresql:sha256:", 63, 'a')]
    [InlineData("safe-relational-model:v1:npgsql_postgresql:sha256:", 65, 'a')]
    [InlineData("safe-relational-model:v1:npgsql_postgresql:sha256:", 64, 'A')]
    [InlineData("safe-relational-model:v1:npgsql_postgresql:sha256:", 64, 'g')]
    public void RunOptions_RejectMalformedExpectedFingerprintsAtConstruction(
        string prefix,
        int digestLength,
        char digestCharacter
    )
    {
        var fingerprint = prefix + new string(digestCharacter, digestLength);

        var exception = Assert.Throws<ArgumentException>(() => new SafeMigrationRunOptions(
            "instance",
            expectedModelFingerprint: fingerprint));

        Assert.Equal("expectedModelFingerprint", exception.ParamName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RunOptions_AcceptAnAbsentOrWellFormedExpectedFingerprint(
        bool requireExpectedFingerprint
    )
    {
        var fingerprint = requireExpectedFingerprint ? ModelFingerprint() : null;

        var options = new SafeMigrationRunOptions("instance", expectedModelFingerprint: fingerprint);

        Assert.Equal(fingerprint, options.ExpectedModelFingerprint);
    }

    [Fact]
    public void Report_IsVersionedImmutableAndNormalizesTimestampToUtc()
    {
        var assessments = new List<SafeMigrationAssessment>
        {
            new(
                0,
                typeof(SafeMigrationOperation).FullName!,
                isSafeOperation: true,
                SafeMigrationOperationKind.EnsureTable,
                "items",
                SafeMigrationObservedState.Missing,
                SafeMigrationAction.Apply,
                postconditionSatisfied: false,
                "apply_missing"),
        };

        var environment = new SafeMigrationProviderEnvironment("npgsql_postgresql", "postgresql", "17.5");
        var localTimestamp = new DateTimeOffset(
            2026,
            8,
            17,
            10,
            30,
            0,
            TimeSpan.FromHours(2));

        var report = new SafeMigrationRunReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Ready,
            localTimestamp,
            "instance-7f3a",
            environment,
            "202608170101_Core",
            ModelFingerprint(),
            new string('b', 64),
            assessments,
            [
                new SafeMigrationUnexpectedObject(
                    SafeMigrationDatabaseObjectKind.Table,
                    "public",
                    table: null,
                    "legacy",
                    "unexpected_table"),
            ]);

        assessments.Clear();

        Assert.Equal(SafeMigrationRunReport.CurrentSchemaVersion, report.SchemaVersion);
        Assert.Equal(TimeSpan.Zero, report.GeneratedAtUtc.Offset);
        Assert.Equal(localTimestamp.UtcDateTime, report.GeneratedAtUtc.UtcDateTime);
        Assert.Single(report.Assessments);
        Assert.Single(report.UnexpectedObjects);
        Assert.Equal("postgresql", report.Environment.EngineFamily);
        Assert.Equal("202608170101_Core", report.TargetMigrationId);
    }

    /// <summary>Serializes the complete report identically through the buffer and caller-owned writer APIs.</summary>
    [Fact]
    public void ReportJson_IsStableCompleteAndCanWriteToACallerOwnedBuffer()
    {
        // Arrange
        var report = CreateReport();

        // Act
        var bytes = SafeMigrationReportJson.SerializeToUtf8Bytes(report);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            SafeMigrationReportJson.Write(writer, report);
        }

        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        // Assert
        Assert.Equal(
            SafeMigrationRunReport.CurrentSchemaVersion,
            root
                .GetProperty("schemaVersion")
                .GetInt32());
        Assert.Equal(
            "preflight",
            root
                .GetProperty("mode")
                .GetString());
        Assert.Equal(
            "ready",
            root
                .GetProperty("status")
                .GetString());
        Assert.Equal(
            "ensure_table",
            root
                .GetProperty("assessments")[0]
                .GetProperty("operationKind")
                .GetString());
        Assert.Equal(
            "missing",
            root
                .GetProperty("assessments")[0]
                .GetProperty("observedState")
                .GetString());
        Assert.Equal(
            "table",
            root
                .GetProperty("unexpectedObjects")[0]
                .GetProperty("objectKind")
                .GetString());

        Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void ReportViewJson_MatchesThePackagedClosedSchema()
    {
        var assessment = new SafeMigrationAssessment(
            7,
            typeof(SafeMigrationOperation).FullName!,
            isSafeOperation: true,
            SafeMigrationOperationKind.EnsureColumn,
            "Config",
            SafeMigrationObservedState.Different,
            SafeMigrationAction.RejectDifferent,
            postconditionSatisfied: false,
            "different_reject",
            "classified_different",
            "different_no_safe_repair",
            SafeMigrationOperationalImpact.NotApplicable,
            [new SafeMigrationFacetDifference("column_collation", "utf8mb4_bin", "utf8mb4_unicode_ci")]);

        var report = CreateReport(
            status: SafeMigrationReportStatus.Blocked,
            assessments: [assessment]);

        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(
            report,
            SafeMigrationReportSelection.BlockingOnly));

        using var schema = LoadReportViewSchema();
        var root = document.RootElement;
        var schemaRoot = schema.RootElement;
        var definitions = schemaRoot.GetProperty("$defs");

        AssertSchemaEnum(
            schemaRoot
                .GetProperty("properties")
                .GetProperty("selection"),
            ["complete", "non_matching", "blocking_only"]);
        AssertClosedObjectSurface(schemaRoot, root);
        AssertClosedObjectSurface(
            definitions.GetProperty("sourceReport"),
            root.GetProperty("sourceReport"));
        AssertClosedObjectSurface(
            definitions.GetProperty("environment"),
            root.GetProperty("sourceReport")
                .GetProperty("environment"));
        AssertClosedObjectSurface(
            definitions.GetProperty("assessment"),
            root.GetProperty("assessments")[0]);
        AssertClosedObjectSurface(
            definitions.GetProperty("difference"),
            root.GetProperty("assessments")[0]
                .GetProperty("differences")[0]);
        AssertMatchesSchema(schemaRoot, root, schemaRoot);
    }

    /// <summary>Matches current schemas to both captured and compatibility-constructed assessment shapes.</summary>
    [Fact]
    public void CurrentSchemasMatchKnownAndUnknownDiagnosticIdentityShapes()
    {
        // Arrange
        var intent = new EnsureForeignKeyIntent(new ExpectedForeignKeyDefinition("fk_child_parent", "children",
            ["Tenant", "OwnerId"], "parents", ["Tenant", "Id"], schema: "dependent", principalSchema: "principal"));

        var known = new SafeMigrationAssessment(0, typeof(SafeMigrationOperation).FullName!, true, intent.Kind,
            intent.ObjectName, SafeMigrationObservedState.PrerequisiteMissing,
            SafeMigrationAction.RejectPrerequisiteMissing, false, "prerequisite_missing",
            "classified_prerequisite_missing", "prerequisite_missing", SafeMigrationOperationalImpact.NotApplicable,
            differences: null, deferredOrigin: null, identity: SafeMigrationOperationIdentity.Create(intent),
            migrationId: "202610140001_Diagnostics", policy: SafeMigrationPolicy.ThrowIfDifferent);

        var unknown = new SafeMigrationAssessment(1, typeof(SafeMigrationOperation).FullName!, true,
            SafeMigrationOperationKind.EnsureColumn, "Id", SafeMigrationObservedState.DataBlocked,
            SafeMigrationAction.RejectDataBlocked, false, "data_blocked");

        var report = CreateReport(status: SafeMigrationReportStatus.Blocked, assessments: [known, unknown]);
        using var completeSchema = LoadReportSchema();
        using var viewSchema = LoadReportViewSchema();

        // Act
        using var complete = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
        using var view = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report,
            SafeMigrationReportSelection.BlockingOnly));

        // Assert
        Assert.Equal(4, complete.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(3, view.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(4, view.RootElement.GetProperty("sourceReport").GetProperty("schemaVersion").GetInt32());
        AssertMatchesSchema(completeSchema.RootElement, complete.RootElement, completeSchema.RootElement);
        AssertMatchesSchema(viewSchema.RootElement, view.RootElement, viewSchema.RootElement);
        var completeAssessmentSchema = completeSchema.RootElement.GetProperty("$defs").GetProperty("assessment");
        var viewAssessmentSchema = viewSchema.RootElement.GetProperty("$defs").GetProperty("assessment");
        var completeAssessments = complete.RootElement.GetProperty("assessments");
        var viewAssessments = view.RootElement.GetProperty("assessments");

        AssertClosedObjectSurface(completeAssessmentSchema, completeAssessments[0]);
        AssertClosedObjectSurface(completeAssessmentSchema, completeAssessments[1]);
        AssertClosedObjectSurface(viewAssessmentSchema, viewAssessments[0]);
        AssertClosedObjectSurface(viewAssessmentSchema, viewAssessments[1]);
        Assert.True(completeAssessments[0].GetProperty("identityKnown").GetBoolean());
        Assert.False(completeAssessments[1].GetProperty("identityKnown").GetBoolean());
        Assert.Equal("children", completeAssessments[0].GetProperty("table").GetString());
        Assert.Equal(JsonValueKind.Null, completeAssessments[1].GetProperty("table").ValueKind);
        Assert.Equal(JsonValueKind.Array, completeAssessments[0].GetProperty("columns").ValueKind);
        Assert.Equal(JsonValueKind.Null, completeAssessments[1].GetProperty("columns").ValueKind);
        Assert.Equal(completeAssessments.GetRawText(), viewAssessments.GetRawText());
    }

    /// <summary>Accepts the SQL Server engine family in the versioned report contract.</summary>
    [Fact]
    public void EnvironmentAcceptsSqlServerEngineFamily()
    {
        var environment = new SafeMigrationProviderEnvironment("efcore_sqlserver", "sqlserver", "16.0");

        Assert.Equal("sqlserver", environment.EngineFamily);
    }

    /// <summary>Rejects an engine family that is absent from the packaged schema.</summary>
    [Fact]
    public void EnvironmentRejectsUnknownEngineFamily()
    {
        // Arrange
        const string engineFamily = "oracle";

        // Act
        var exception = Record.Exception(() =>
            new SafeMigrationProviderEnvironment("provider", engineFamily, "1.0"));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Rejects malformed report fingerprints independently of each other.</summary>
    /// <param name="model">Whether the invalid value is the model rather than contract fingerprint.</param>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReportRejectsMalformedFingerprint(
        bool model
    )
    {
        // Arrange
        var modelFingerprint = model ? new string('a', 64) : null;
        var contractFingerprint = model ? null : "ABC";

        // Act
        var exception = Record.Exception(() => CreateReport(
            modelFingerprint: modelFingerprint,
            contractFingerprint: contractFingerprint));

        // Assert
        Assert.IsType<ArgumentException>(exception);
    }

    /// <summary>Rejects each undefined assessment enum independently at the public construction boundary.</summary>
    /// <param name="field">The serializable invalid-enum field identifier.</param>
    [Theory]
    [InlineData("operation_kind")]
    [InlineData("observed_state")]
    [InlineData("action")]
    [InlineData("impact")]
    public void AssessmentRejectsUndefinedEnum(
        string field
    )
    {
        // Arrange
        var kind = field == "operation_kind"
            ? (SafeMigrationOperationKind)int.MaxValue
            : SafeMigrationOperationKind.EnsureTable;

        var state = field == "observed_state"
            ? (SafeMigrationObservedState)int.MaxValue
            : SafeMigrationObservedState.Matching;

        var action = field == "action" ? (SafeMigrationAction)int.MaxValue : SafeMigrationAction.NoOp;
        var impact = field == "impact"
            ? (SafeMigrationOperationalImpact)int.MaxValue
            : SafeMigrationOperationalImpact.NotApplicable;

        var code = "invalid_" + field;

        // Act
        var exception = Record.Exception(() =>
        {
            _ = new SafeMigrationAssessment(
                0,
                "operation",
                isSafeOperation: true,
                kind,
                "object",
                state,
                action,
                postconditionSatisfied: true,
                code,
                field == "impact" ? "classified_matching" : code,
                field == "impact" ? "matching_no_op" : code,
                impact,
                differences: null);
        });

        // Assert
        Assert.IsType<ArgumentOutOfRangeException>(exception);
    }

    private static SafeMigrationRunReport CreateReport(
        SafeMigrationReportMode mode = SafeMigrationReportMode.Preflight,
        SafeMigrationReportStatus status = SafeMigrationReportStatus.Ready,
        string? targetMigrationId = "202608170101_Core",
        IEnumerable<SafeMigrationAssessment>? assessments = null,
        IEnumerable<SafeMigrationUnexpectedObject>? unexpectedObjects = null,
        string? modelFingerprint = null,
        string? contractFingerprint = null
    )
    {
        var environment = new SafeMigrationProviderEnvironment("npgsql_postgresql", "postgresql", "18.6");

        return new SafeMigrationRunReport(
            mode,
            status,
            new DateTimeOffset(
                2026,
                8,
                17,
                10,
                30,
                0,
                TimeSpan.Zero),
            "instance-7f3a",
            environment,
            targetMigrationId,
            modelFingerprint ?? ModelFingerprint(),
            contractFingerprint ?? new string('b', 64),
            assessments
            ??
            [
                new SafeMigrationAssessment(
                    0,
                    typeof(SafeMigrationOperation).FullName!,
                    isSafeOperation: true,
                    SafeMigrationOperationKind.EnsureTable,
                    "items",
                    SafeMigrationObservedState.Missing,
                    SafeMigrationAction.Apply,
                    postconditionSatisfied: false,
                    "apply_missing"),
            ],
            unexpectedObjects
            ??
            [
                new SafeMigrationUnexpectedObject(
                    SafeMigrationDatabaseObjectKind.Table,
                    "public",
                    table: null,
                    "legacy",
                    "unexpected_table"),
            ]);
    }

    private static string ModelFingerprint() =>
        $"safe-relational-model:v1:npgsql_postgresql:sha256:{new string('a', 64)}";

    private static JsonDocument LoadReportSchema() => LoadReportSchema(
        SafeMigrationRunReport.CurrentSchemaVersion);

    private static JsonDocument LoadReportSchema(
        int version
    ) => JsonDocument.Parse(
        File.ReadAllBytes(
            Path.Combine(
                AppContext.BaseDirectory,
                "schemas",
                $"safe-migration-run-report-v{version}.schema.json")));

    private static JsonDocument LoadReportViewSchema() => JsonDocument.Parse(
        File.ReadAllBytes(
            Path.Combine(
                AppContext.BaseDirectory,
                "schemas",
                "safe-migration-report-view-v3.schema.json")));

    private static void AssertSchemaEnum(
        JsonElement schema,
        IEnumerable<string> expectedValues
    )
    {
        var actual = schema
            .GetProperty("enum")
            .EnumerateArray()
            .Where(static value => value.ValueKind == JsonValueKind.String)
            .Select(static value => value.GetString()!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expectedValues.Order(StringComparer.Ordinal), actual);
    }

    private static void AssertClosedObjectSurface(
        JsonElement schema,
        JsonElement value
    )
    {
        Assert.False(
            schema
                .GetProperty("additionalProperties")
                .GetBoolean());

        var properties = schema
            .GetProperty("properties")
            .EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal);

        var required = schema
            .GetProperty("required")
            .EnumerateArray()
            .Select(static property => property.GetString()!)
            .Order(StringComparer.Ordinal);

        var serialized = value
            .EnumerateObject()
            .Select(static property => property.Name)
            .Order(StringComparer.Ordinal);

        Assert.Equal(properties, required);
        Assert.Equal(properties, serialized);
    }

    private static void AssertMatchesSchema(
        JsonElement schema,
        JsonElement value,
        JsonElement rootSchema
    )
    {
        Assert.All(schema.EnumerateObject(), property => Assert.Contains(property.Name, s_supportedSchemaKeywords));

        if (schema.TryGetProperty("$ref", out var reference))
        {
            const string definitionsPrefix = "#/$defs/";
            var referenceValue = reference.GetString()!;

            Assert.StartsWith(definitionsPrefix, referenceValue, StringComparison.Ordinal);
            AssertMatchesSchema(
                rootSchema
                    .GetProperty("$defs")
                    .GetProperty(referenceValue[definitionsPrefix.Length..]),
                value,
                rootSchema);

            return;
        }

        if (schema.TryGetProperty("type", out var type))
        {
            AssertSchemaType(type, value);
        }

        if (schema.TryGetProperty("const", out var constant))
        {
            Assert.True(JsonElement.DeepEquals(constant, value));
        }

        if (schema.TryGetProperty("enum", out var enumValues))
        {
            Assert.Contains(enumValues.EnumerateArray(), candidate => JsonElement.DeepEquals(candidate, value));
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var stringValue = value.GetString()!;

            if (schema.TryGetProperty("minLength", out var minimumLength))
            {
                Assert.True(stringValue.Length >= minimumLength.GetInt32());
            }

            if (schema.TryGetProperty("maxLength", out var maximumLength))
            {
                Assert.True(stringValue.Length <= maximumLength.GetInt32());
            }

            if (schema.TryGetProperty("pattern", out var pattern))
            {
                Assert.Matches(new Regex(pattern.GetString()!, RegexOptions.CultureInvariant), stringValue);
            }

            if (schema.TryGetProperty("format", out var format))
            {
                Assert.Equal("date-time", format.GetString());
                Assert.True(
                    DateTimeOffset.TryParse(
                        stringValue,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out _));
            }
        }

        if (value.ValueKind == JsonValueKind.Number
            && schema.TryGetProperty("minimum", out var minimum))
        {
            Assert.True(value.GetDecimal() >= minimum.GetDecimal());
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            AssertSchemaObject(schema, value, rootSchema);
        }

        if (value.ValueKind == JsonValueKind.Array
            && schema.TryGetProperty("items", out var items))
        {
            if (schema.TryGetProperty("maxItems", out var maximumItems))
            {
                Assert.True(value.GetArrayLength() <= maximumItems.GetInt32());
            }

            foreach (var item in value.EnumerateArray())
            {
                AssertMatchesSchema(items, item, rootSchema);
            }
        }
    }

    private static void AssertSchemaType(
        JsonElement schemaType,
        JsonElement value
    )
    {
        var allowedTypes = schemaType.ValueKind == JsonValueKind.Array
            ? schemaType
                .EnumerateArray()
                .Select(static item => item.GetString()!)
                .ToArray()
            : [schemaType.GetString()!];

        var actualType = value.ValueKind switch
        {
            JsonValueKind.Object => "object",
            JsonValueKind.Array => "array",
            JsonValueKind.String => "string",
            JsonValueKind.Number when value.TryGetInt64(out _) => "integer",
            JsonValueKind.Number => "number",
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Null => "null",
            _ => throw new InvalidOperationException($"Unsupported JSON value kind '{value.ValueKind}'."),
        };

        Assert.Contains(actualType, allowedTypes);
    }

    private static void AssertSchemaObject(
        JsonElement schema,
        JsonElement value,
        JsonElement rootSchema
    )
    {
        var properties = schema.GetProperty("properties");
        if (schema.TryGetProperty("required", out var required))
        {
            Assert.All(
                required.EnumerateArray(),
                property => Assert.True(value.TryGetProperty(property.GetString()!, out _)));
        }

        foreach (var property in value.EnumerateObject())
        {
            if (!properties.TryGetProperty(property.Name, out var propertySchema))
            {
                Assert.False(
                    schema
                        .GetProperty("additionalProperties")
                        .GetBoolean());
            }

            AssertMatchesSchema(propertySchema, property.Value, rootSchema);
        }
    }
}
