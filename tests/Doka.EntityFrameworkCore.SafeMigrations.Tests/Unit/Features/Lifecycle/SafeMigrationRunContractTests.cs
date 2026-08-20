namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed class SafeMigrationRunContractTests
{
    [Fact]
    public void RunOptions_RequireExplicitNonEmptyPseudonymousIdentity()
    {
        Assert.Throws<ArgumentException>(() => new SafeMigrationRunOptions(" "));
        Assert.Throws<ArgumentException>(() => new SafeMigrationRunOptions("instance", " "));
        Assert.Throws<ArgumentException>(() => new SafeMigrationRunOptions("instance", expectedModelFingerprint: " "));

        var options = new SafeMigrationRunOptions("instance-7f3a", "202608170101_Core", new string('a', 64));

        Assert.Equal("instance-7f3a", options.InstanceId);
        Assert.Equal("202608170101_Core", options.TargetMigrationId);
        Assert.Equal(new string('a', 64), options.ExpectedModelFingerprint);
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
            new string('a', 64),
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

                Assert.Equal(mode.Code, root.GetProperty("mode").GetString());
                Assert.Equal(status.Code, root.GetProperty("status").GetString());
                Assert.Equal(JsonValueKind.Null, root.GetProperty("targetMigrationId").ValueKind);
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
        };

        var actions = new[]
        {
            (Value: SafeMigrationAction.Apply, Code: "apply"),
            (Value: SafeMigrationAction.NoOp, Code: "no_op"),
            (Value: SafeMigrationAction.Repair, Code: "repair"),
            (Value: SafeMigrationAction.RejectUnsupported, Code: "reject_unsupported"),
            (Value: SafeMigrationAction.RejectDifferent, Code: "reject_different"),
            (Value: SafeMigrationAction.RejectDataBlocked, Code: "reject_data_blocked"),
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
            .Select((value, index) => new SafeMigrationAssessment(
                index,
                typeof(SafeMigrationOperation).FullName!,
                isSafeOperation: true,
                value.Value,
                $"object_{index}",
                states[index % states.Length].Value,
                actions[index % actions.Length].Value,
                index % 2 == 0,
                "stable_code"))
            .Append(new SafeMigrationAssessment(
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

        Assert.Equal(operationKinds.Select(static value => value.Code),
            assessmentElements.EnumerateArray().Take(operationKinds.Length)
                .Select(static value => value.GetProperty("operationKind").GetString()));
        Assert.Equal(states.Select(static value => value.Code),
            assessmentElements.EnumerateArray().Take(states.Length)
                .Select(static value => value.GetProperty("observedState").GetString()));
        Assert.Equal(actions.Select(static value => value.Code),
            assessmentElements.EnumerateArray().Take(actions.Length)
                .Select(static value => value.GetProperty("action").GetString()));
        Assert.Equal(objectKinds.Select(static value => value.Code),
            unexpectedElements.EnumerateArray()
                .Select(static value => value.GetProperty("objectKind").GetString()));

        var providerAssessment = assessmentElements[operationKinds.Length];

        Assert.Equal(JsonValueKind.Null, providerAssessment.GetProperty("operationKind").ValueKind);
        Assert.Equal(JsonValueKind.Null, providerAssessment.GetProperty("objectName").ValueKind);
        Assert.Equal(JsonValueKind.Null, providerAssessment.GetProperty("observedState").ValueKind);
        Assert.Equal(JsonValueKind.Null, providerAssessment.GetProperty("action").ValueKind);
        Assert.Equal(JsonValueKind.Null, providerAssessment.GetProperty("postconditionSatisfied").ValueKind);
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

    private static SafeMigrationRunReport CreateReport(
        SafeMigrationReportMode mode = SafeMigrationReportMode.Preflight,
        SafeMigrationReportStatus status = SafeMigrationReportStatus.Ready,
        string? targetMigrationId = "202608170101_Core",
        IEnumerable<SafeMigrationAssessment>? assessments = null,
        IEnumerable<SafeMigrationUnexpectedObject>? unexpectedObjects = null
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
            new string('a', 64),
            new string('b', 64),
            assessments ??
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
            unexpectedObjects ??
            [
                new SafeMigrationUnexpectedObject(
                    SafeMigrationDatabaseObjectKind.Table,
                    "public",
                    table: null,
                    "legacy",
                    "unexpected_table"),
            ]);
    }
}
