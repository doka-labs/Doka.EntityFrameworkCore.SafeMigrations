namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies exact process-wide Meter measurements without competing telemetry emitters.</summary>
// WHY: MeterListener observes every matching instrument in this process, not only this test's emissions.
[Collection(ProcessWideTestGroup.Name)]
public sealed class SafeMigrationTelemetryMeasurementTests
{
    /// <summary>Failure measurements expose bounded tags without exception payloads.</summary>
    [Fact]
    public void FailureTelemetry_ContainsOnlyBoundedLowCardinalityTags()
    {
        // Arrange
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

        // Act
        SafeMigrationTelemetry.RecordFailure(
            SafeMigrationReportMode.Preflight,
            SafeMigrationTelemetry.FailureCode(new InvalidOperationException("sensitive payload")));

        // Assert
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

    /// <summary>Successful measurements retain the stable database semantic-convention tags.</summary>
    [Fact]
    public void SuccessfulTelemetry_UsesStableDatabaseSemanticConventionTags()
    {
        // Arrange
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

        // Act
        SafeMigrationTelemetry.Record(
            SafeMigrationReportMode.Preflight,
            SafeMigrationReportStatus.Ready,
            "npgsql_postgresql",
            "postgresql",
            operationCount: 3,
            TimeSpan.FromMilliseconds(5));

        // Assert
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
}
