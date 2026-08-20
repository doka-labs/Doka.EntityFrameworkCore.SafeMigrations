namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Writes the versioned SafeMigrations report JSON contract.</summary>
public static class SafeMigrationReportJson
{
    /// <summary>Serializes a report to a compact UTF-8 JSON document.</summary>
    /// <param name="report">The report to serialize.</param>
    /// <returns>A compact UTF-8 JSON document.</returns>
    public static byte[] SerializeToUtf8Bytes(
        SafeMigrationRunReport report
    )
    {
        ArgumentNullException.ThrowIfNull(report);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        Write(writer, report);
        writer.Flush();

        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Writes one report to a caller-owned writer without reflection or an
    /// intermediate object graph.
    /// </summary>
    /// <param name="writer">The caller-owned UTF-8 JSON writer.</param>
    /// <param name="report">The report to serialize.</param>
    public static void Write(
        Utf8JsonWriter writer,
        SafeMigrationRunReport report
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(report);

        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", report.SchemaVersion);
        writer.WriteString("mode", ModeCode(report.Mode));
        writer.WriteString("status", StatusCode(report.Status));
        writer.WriteString("generatedAtUtc", report.GeneratedAtUtc);
        writer.WriteString("instanceId", report.InstanceId);
        WriteEnvironment(writer, report.Environment);
        WriteNullableString(writer, "targetMigrationId", report.TargetMigrationId);
        writer.WriteString("modelFingerprint", report.ModelFingerprint);
        writer.WriteString("contractFingerprint", report.ContractFingerprint);
        writer.WriteStartArray("assessments");

        foreach (var assessment in report.Assessments)
        {
            WriteAssessment(writer, assessment);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("unexpectedObjects");

        foreach (var unexpectedObject in report.UnexpectedObjects)
        {
            WriteUnexpectedObject(writer, unexpectedObject);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteEnvironment(
        Utf8JsonWriter writer,
        SafeMigrationProviderEnvironment environment
    )
    {
        writer.WriteStartObject("environment");

        writer.WriteString("providerId", environment.ProviderId);
        writer.WriteString("engineFamily", environment.EngineFamily);
        writer.WriteString("serverVersion", environment.ServerVersion);

        writer.WriteEndObject();
    }

    private static void WriteAssessment(
        Utf8JsonWriter writer,
        SafeMigrationAssessment assessment
    )
    {
        writer.WriteStartObject();
        writer.WriteNumber("ordinal", assessment.Ordinal);
        writer.WriteString("operationType", assessment.OperationType);
        writer.WriteBoolean("isSafeOperation", assessment.IsSafeOperation);

        WriteNullableString(
            writer,
            "operationKind",
            assessment.OperationKind is null ? null : OperationKindCode(assessment.OperationKind.Value));
        WriteNullableString(writer, "objectName", assessment.ObjectName);
        WriteNullableString(
            writer,
            "observedState",
            assessment.ObservedState is null ? null : ObservedStateCode(assessment.ObservedState.Value));
        WriteNullableString(writer, "action", assessment.Action is null ? null : ActionCode(assessment.Action.Value));

        if (assessment.PostconditionSatisfied is null)
        {
            writer.WriteNull("postconditionSatisfied");
        }
        else
        {
            writer.WriteBoolean("postconditionSatisfied", assessment.PostconditionSatisfied.Value);
        }

        writer.WriteString("code", assessment.Code);
        writer.WriteEndObject();
    }

    private static void WriteUnexpectedObject(
        Utf8JsonWriter writer,
        SafeMigrationUnexpectedObject value
    )
    {
        writer.WriteStartObject();
        writer.WriteString("objectKind", ObjectKindCode(value.ObjectKind));

        WriteNullableString(writer, "schema", value.Schema);
        WriteNullableString(writer, "table", value.Table);

        writer.WriteString("name", value.Name);
        writer.WriteString("code", value.Code);
        writer.WriteEndObject();
    }

    private static void WriteNullableString(
        Utf8JsonWriter writer,
        string propertyName,
        string? value
    )
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    private static string ModeCode(
        SafeMigrationReportMode value
    ) => value switch
    {
        SafeMigrationReportMode.Preflight => "preflight",
        SafeMigrationReportMode.Postflight => "postflight",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string StatusCode(
        SafeMigrationReportStatus value
    ) => value switch
    {
        SafeMigrationReportStatus.NoOperations => "no_operations",
        SafeMigrationReportStatus.Ready => "ready",
        SafeMigrationReportStatus.ReadyWithProviderOperations => "ready_with_provider_operations",
        SafeMigrationReportStatus.Blocked => "blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string OperationKindCode(
        SafeMigrationOperationKind value
    ) => ToSnakeCase(value.ToString());

    private static string ObservedStateCode(
        SafeMigrationObservedState value
    ) => value switch
    {
        SafeMigrationObservedState.Missing => "missing",
        SafeMigrationObservedState.Matching => "matching",
        SafeMigrationObservedState.Different => "different",
        SafeMigrationObservedState.Unsupported => "unsupported",
        SafeMigrationObservedState.DataBlocked => "data_blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ActionCode(
        SafeMigrationAction value
    ) => value switch
    {
        SafeMigrationAction.Apply => "apply",
        SafeMigrationAction.NoOp => "no_op",
        SafeMigrationAction.Repair => "repair",
        SafeMigrationAction.RejectDifferent => "reject_different",
        SafeMigrationAction.RejectUnsupported => "reject_unsupported",
        SafeMigrationAction.RejectDataBlocked => "reject_data_blocked",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ObjectKindCode(
        SafeMigrationDatabaseObjectKind value
    ) => value switch
    {
        SafeMigrationDatabaseObjectKind.Table => "table",
        SafeMigrationDatabaseObjectKind.Column => "column",
        SafeMigrationDatabaseObjectKind.Index => "index",
        SafeMigrationDatabaseObjectKind.PrimaryKey => "primary_key",
        SafeMigrationDatabaseObjectKind.UniqueConstraint => "unique_constraint",
        SafeMigrationDatabaseObjectKind.CheckConstraint => "check_constraint",
        SafeMigrationDatabaseObjectKind.ForeignKey => "foreign_key",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static string ToSnakeCase(
        string value
    )
    {
        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character)
                && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
