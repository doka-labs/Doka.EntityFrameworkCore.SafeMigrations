namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationRunContractTests
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
        "pattern",
        "items",
        "minimum",
    };

    [Fact]
    public void RunOptions_RequireExplicitNonEmptyPseudonymousIdentity()
    {
        Assert.Throws<ArgumentException>(() => new SafeMigrationRunOptions(" "));
        Assert.Throws<ArgumentException>(() => new SafeMigrationRunOptions("instance", " "));

        var expectedFingerprint = ModelFingerprint();
        var options = new SafeMigrationRunOptions("instance-7f3a", "202608170101_Core", expectedFingerprint);

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

    [Fact]
    public void ReportJson_IsStableCompleteAndCanWriteToACallerOwnedBuffer()
    {
        var report = CreateReport();

        var bytes = SafeMigrationReportJson.SerializeToUtf8Bytes(report);
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;

        Assert.Equal(
            1,
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

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            SafeMigrationReportJson.Write(writer, report);
        }

        Assert.Equal(Encoding.UTF8.GetString(bytes), Encoding.UTF8.GetString(stream.ToArray()));
    }

    [Fact]
    public void ReportJsonCoversEveryStableWireCodeAndNullableField()
    {
        var modes = new[]
        {
            (Value: SafeMigrationReportMode.Preflight, Code: "preflight"),
            (Value: SafeMigrationReportMode.Postflight, Code: "postflight"),
        };

        var statuses = new[]
        {
            (Value: SafeMigrationReportStatus.NoOperations, Code: "no_operations"),
            (Value: SafeMigrationReportStatus.Ready, Code: "ready"),
            (Value: SafeMigrationReportStatus.ReadyWithProviderOperations, Code: "ready_with_provider_operations"),
            (Value: SafeMigrationReportStatus.Blocked, Code: "blocked"),
        };

        foreach (var mode in modes)
        {
            foreach (var status in statuses)
            {
                var report = CreateReport(mode.Value, status.Value, targetMigrationId: null);
                using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));
                var root = document.RootElement;

                Assert.Equal(
                    mode.Code,
                    root
                        .GetProperty("mode")
                        .GetString());
                Assert.Equal(
                    status.Code,
                    root
                        .GetProperty("status")
                        .GetString());
                Assert.Equal(
                    JsonValueKind.Null,
                    root.GetProperty("targetMigrationId")
                        .ValueKind);
            }
        }

        var operationKinds = new[]
        {
            (Value: SafeMigrationOperationKind.EnsureSchema, Code: "ensure_schema"),
            (Value: SafeMigrationOperationKind.DropSchema, Code: "drop_schema"),
            (Value: SafeMigrationOperationKind.EnsureTable, Code: "ensure_table"),
            (Value: SafeMigrationOperationKind.DropTable, Code: "drop_table"),
            (Value: SafeMigrationOperationKind.RenameTable, Code: "rename_table"),
            (Value: SafeMigrationOperationKind.EnsureColumn, Code: "ensure_column"),
            (Value: SafeMigrationOperationKind.DropColumn, Code: "drop_column"),
            (Value: SafeMigrationOperationKind.RenameColumn, Code: "rename_column"),
            (Value: SafeMigrationOperationKind.AlterColumn, Code: "alter_column"),
            (Value: SafeMigrationOperationKind.EnsureIndex, Code: "ensure_index"),
            (Value: SafeMigrationOperationKind.DropIndex, Code: "drop_index"),
            (Value: SafeMigrationOperationKind.RenameIndex, Code: "rename_index"),
            (Value: SafeMigrationOperationKind.EnsurePrimaryKey, Code: "ensure_primary_key"),
            (Value: SafeMigrationOperationKind.DropPrimaryKey, Code: "drop_primary_key"),
            (Value: SafeMigrationOperationKind.EnsureUniqueConstraint, Code: "ensure_unique_constraint"),
            (Value: SafeMigrationOperationKind.DropUniqueConstraint, Code: "drop_unique_constraint"),
            (Value: SafeMigrationOperationKind.EnsureCheckConstraint, Code: "ensure_check_constraint"),
            (Value: SafeMigrationOperationKind.DropCheckConstraint, Code: "drop_check_constraint"),
            (Value: SafeMigrationOperationKind.EnsureForeignKey, Code: "ensure_foreign_key"),
            (Value: SafeMigrationOperationKind.DropForeignKey, Code: "drop_foreign_key"),
        };

        var states = new[]
        {
            (Value: SafeMigrationObservedState.Missing, Code: "missing"),
            (Value: SafeMigrationObservedState.Matching, Code: "matching"),
            (Value: SafeMigrationObservedState.Different, Code: "different"),
            (Value: SafeMigrationObservedState.Unsupported, Code: "unsupported"),
            (Value: SafeMigrationObservedState.DataBlocked, Code: "data_blocked"),
            (Value: SafeMigrationObservedState.PrerequisiteMissing, Code: "prerequisite_missing"),
        };

        var actions = new[]
        {
            (Value: SafeMigrationAction.Apply, Code: "apply"), (Value: SafeMigrationAction.NoOp, Code: "no_op"),
            (Value: SafeMigrationAction.Repair, Code: "repair"),
            (Value: SafeMigrationAction.RejectUnsupported, Code: "reject_unsupported"),
            (Value: SafeMigrationAction.RejectDifferent, Code: "reject_different"),
            (Value: SafeMigrationAction.RejectDataBlocked, Code: "reject_data_blocked"),
            (Value: SafeMigrationAction.RejectPrerequisiteMissing, Code: "reject_prerequisite_missing"),
        };

        var objectKinds = new[]
        {
            (Value: SafeMigrationDatabaseObjectKind.Table, Code: "table"),
            (Value: SafeMigrationDatabaseObjectKind.Column, Code: "column"),
            (Value: SafeMigrationDatabaseObjectKind.Index, Code: "index"),
            (Value: SafeMigrationDatabaseObjectKind.PrimaryKey, Code: "primary_key"),
            (Value: SafeMigrationDatabaseObjectKind.UniqueConstraint, Code: "unique_constraint"),
            (Value: SafeMigrationDatabaseObjectKind.CheckConstraint, Code: "check_constraint"),
            (Value: SafeMigrationDatabaseObjectKind.ForeignKey, Code: "foreign_key"),
        };

        var assessments = operationKinds
            .Select((
                value,
                index
            ) => new SafeMigrationAssessment(
                index,
                typeof(SafeMigrationOperation).FullName!,
                isSafeOperation: true,
                value.Value,
                $"object_{index}",
                states[index % states.Length].Value,
                actions[index % actions.Length].Value,
                index % 2 == 0,
                "stable_code"))
            .Append(
                new SafeMigrationAssessment(
                    operationKinds.Length,
                    typeof(MigrationOperation).FullName!,
                    isSafeOperation: false,
                    operationKind: null,
                    objectName: null,
                    observedState: null,
                    action: null,
                    postconditionSatisfied: null,
                    "provider_operation"))
            .ToArray();

        var unexpectedObjects = objectKinds
            .Select(value => new SafeMigrationUnexpectedObject(value.Value, null, null, value.Code, "unexpected"))
            .ToArray();

        var completeReport = CreateReport(
            SafeMigrationReportMode.Postflight,
            SafeMigrationReportStatus.Blocked,
            assessments: assessments,
            unexpectedObjects: unexpectedObjects);

        using var completeDocument = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(completeReport));
        var assessmentElements = completeDocument.RootElement.GetProperty("assessments");
        var unexpectedElements = completeDocument.RootElement.GetProperty("unexpectedObjects");

        Assert.Equal(
            operationKinds.Select(static value => value.Code),
            assessmentElements
                .EnumerateArray()
                .Take(operationKinds.Length)
                .Select(static value => value
                    .GetProperty("operationKind")
                    .GetString()));
        Assert.Equal(
            states.Select(static value => value.Code),
            assessmentElements
                .EnumerateArray()
                .Take(states.Length)
                .Select(static value => value
                    .GetProperty("observedState")
                    .GetString()));
        Assert.Equal(
            actions.Select(static value => value.Code),
            assessmentElements
                .EnumerateArray()
                .Take(actions.Length)
                .Select(static value => value
                    .GetProperty("action")
                    .GetString()));
        Assert.Equal(
            objectKinds.Select(static value => value.Code),
            unexpectedElements
                .EnumerateArray()
                .Select(static value => value
                    .GetProperty("objectKind")
                    .GetString()));

        using var schema = LoadReportSchema();
        var schemaRoot = schema.RootElement;
        var definitions = schemaRoot.GetProperty("$defs");

        AssertSchemaEnum(
            schemaRoot
                .GetProperty("properties")
                .GetProperty("mode"),
            modes.Select(static value => value.Code));
        AssertSchemaEnum(
            schemaRoot
                .GetProperty("properties")
                .GetProperty("status"),
            statuses.Select(static value => value.Code));
        AssertSchemaEnum(
            definitions
                .GetProperty("environment")
                .GetProperty("properties")
                .GetProperty("engineFamily"),
            ["mysql", "mariadb", "postgresql"]);
        AssertSchemaEnum(
            definitions
                .GetProperty("assessment")
                .GetProperty("properties")
                .GetProperty("operationKind"),
            operationKinds.Select(static value => value.Code));
        AssertSchemaEnum(
            definitions
                .GetProperty("assessment")
                .GetProperty("properties")
                .GetProperty("observedState"),
            states.Select(static value => value.Code));
        AssertSchemaEnum(
            definitions
                .GetProperty("assessment")
                .GetProperty("properties")
                .GetProperty("action"),
            actions.Select(static value => value.Code));
        AssertSchemaEnum(
            definitions
                .GetProperty("unexpectedObject")
                .GetProperty("properties")
                .GetProperty("objectKind"),
            objectKinds.Select(static value => value.Code));

        AssertClosedObjectSurface(schemaRoot, completeDocument.RootElement);
        AssertClosedObjectSurface(
            definitions.GetProperty("environment"),
            completeDocument.RootElement.GetProperty("environment"));
        AssertClosedObjectSurface(definitions.GetProperty("assessment"), assessmentElements[0]);
        AssertClosedObjectSurface(definitions.GetProperty("unexpectedObject"), unexpectedElements[0]);

        var modelPattern = schemaRoot
            .GetProperty("properties")
            .GetProperty("modelFingerprint")
            .GetProperty("pattern")
            .GetString()!;
        var contractPattern = schemaRoot
            .GetProperty("properties")
            .GetProperty("contractFingerprint")
            .GetProperty("pattern")
            .GetString()!;

        Assert.Matches(new Regex(modelPattern, RegexOptions.CultureInvariant), completeReport.ModelFingerprint);
        Assert.Matches(new Regex(contractPattern, RegexOptions.CultureInvariant), completeReport.ContractFingerprint);
        AssertMatchesSchema(schemaRoot, completeDocument.RootElement, schemaRoot);

        var providerAssessment = assessmentElements[operationKinds.Length];

        Assert.Equal(
            JsonValueKind.Null,
            providerAssessment.GetProperty("operationKind")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            providerAssessment.GetProperty("objectName")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            providerAssessment.GetProperty("observedState")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            providerAssessment.GetProperty("action")
                .ValueKind);
        Assert.Equal(
            JsonValueKind.Null,
            providerAssessment.GetProperty("postconditionSatisfied")
                .ValueKind);
    }

    [Fact]
    public void FailureTelemetry_ContainsOnlyBoundedLowCardinalityTags()
    {
        var measurements = new List<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (
            instrument,
            meterListener
        ) =>
        {
            if (instrument.Meter.Name == SafeMigrationDiagnostics.MeterName
                && instrument.Name == SafeMigrationDiagnostics.RunFailureCountMetricName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((
            _,
            _,
            tags,
            _
        ) => measurements.Add(tags.ToArray()));
        listener.Start();

        SafeMigrationTelemetry.RecordFailure(
            SafeMigrationReportMode.Preflight,
            SafeMigrationTelemetry.FailureCode(new InvalidOperationException("sensitive payload")));

        var tags = Assert.Single(measurements);
        Assert.Equal(2, tags.Length);
        Assert.Contains(tags, value => value.Key == "safe_migrations.mode" && Equals(value.Value, "preflight"));
        Assert.Contains(
            tags,
            value => value.Key == "safe_migrations.failure_code" && Equals(value.Value, "runtime_contract_invalid"));
        Assert.DoesNotContain(
            tags,
            value => Convert
                    .ToString(value.Value, CultureInfo.InvariantCulture)
                    ?.Contains("sensitive", StringComparison.OrdinalIgnoreCase)
                == true);
    }

    [Fact]
    public void ReportAndEnvironmentRejectValuesOutsideThePackagedSchemaContract()
    {
        Assert.Throws<ArgumentException>(() => new SafeMigrationProviderEnvironment("provider", "sqlite", "1.0"));
        Assert.Throws<ArgumentException>(() => CreateReport(modelFingerprint: new string('a', 64)));
        Assert.Throws<ArgumentException>(() => CreateReport(contractFingerprint: "ABC"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SafeMigrationAssessment(
            0,
            "operation",
            isSafeOperation: true,
            (SafeMigrationOperationKind)int.MaxValue,
            "object",
            SafeMigrationObservedState.Matching,
            SafeMigrationAction.NoOp,
            postconditionSatisfied: true,
            "invalid_operation_kind"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SafeMigrationAssessment(
            0,
            "operation",
            isSafeOperation: true,
            SafeMigrationOperationKind.EnsureTable,
            "object",
            (SafeMigrationObservedState)int.MaxValue,
            SafeMigrationAction.NoOp,
            postconditionSatisfied: true,
            "invalid_observed_state"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SafeMigrationAssessment(
            0,
            "operation",
            isSafeOperation: true,
            SafeMigrationOperationKind.EnsureTable,
            "object",
            SafeMigrationObservedState.Matching,
            (SafeMigrationAction)int.MaxValue,
            postconditionSatisfied: true,
            "invalid_action"));
    }

    [Fact]
    public void SuccessfulTelemetry_UsesStableDatabaseSemanticConventionTags()
    {
        var measurements = new List<(string Instrument, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (
            instrument,
            meterListener
        ) =>
        {
            if (instrument.Meter.Name == SafeMigrationDiagnostics.MeterName
                && instrument.Name is SafeMigrationDiagnostics.RunCountMetricName
                    or SafeMigrationDiagnostics.RunDurationMetricName
                    or SafeMigrationDiagnostics.OperationCountMetricName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((
            instrument,
            _,
            tags,
            _
        ) => measurements.Add((instrument.Name, tags.ToArray())));
        listener.SetMeasurementEventCallback<double>((
            instrument,
            _,
            tags,
            _
        ) => measurements.Add((instrument.Name, tags.ToArray())));
        listener.Start();

        SafeMigrationTelemetry.Record(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Ready,
            "npgsql_postgresql",
            "postgresql",
            operationCount: 3,
            TimeSpan.FromMilliseconds(5));

        Assert.Equal(3, measurements.Count);
        Assert.Equal(
            [
                SafeMigrationDiagnostics.RunCountMetricName,
                SafeMigrationDiagnostics.RunDurationMetricName,
                SafeMigrationDiagnostics.OperationCountMetricName,
            ],
            measurements.Select(static measurement => measurement.Instrument));
        Assert.All(
            measurements,
            measurement =>
            {
                Assert.Equal(4, measurement.Tags.Length);
                Assert.Contains(
                    measurement.Tags,
                    value => value.Key == "db.system.name" && Equals(value.Value, "postgresql"));
                Assert.DoesNotContain(measurement.Tags, value => value.Key == "db.system");
                Assert.Contains(
                    measurement.Tags,
                    value => value.Key == "safe_migrations.provider" && Equals(value.Value, "npgsql_postgresql"));
                Assert.Contains(
                    measurement.Tags,
                    value => value.Key == "safe_migrations.mode" && Equals(value.Value, "preflight"));
                Assert.Contains(
                    measurement.Tags,
                    value => value.Key == "safe_migrations.status" && Equals(value.Value, "ready"));
            });
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

    private static JsonDocument LoadReportSchema() => JsonDocument.Parse(
        File.ReadAllBytes(
            Path.Combine(AppContext.BaseDirectory, "schemas", "safe-migration-run-report-v1.schema.json")));

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
