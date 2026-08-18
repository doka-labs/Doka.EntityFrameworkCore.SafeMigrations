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

    private static SafeMigrationRunReport CreateReport()
    {
        var environment = new SafeMigrationProviderEnvironment("npgsql_postgresql", "postgresql", "18.6");
        return new SafeMigrationRunReport(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Ready,
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
            "202608170101_Core",
            new string('a', 64),
            new string('b', 64),
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
