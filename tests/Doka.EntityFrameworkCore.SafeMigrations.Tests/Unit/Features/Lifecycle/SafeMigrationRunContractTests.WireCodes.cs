namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationRunContractTests
{
    private static readonly (SafeMigrationReportMode Value, string Code)[] s_modes =
    {
        (Value: SafeMigrationReportMode.Preflight, Code: "preflight"),
        (Value: SafeMigrationReportMode.Postflight, Code: "postflight"),
    };

    private static readonly (SafeMigrationReportStatus Value, string Code)[] s_statuses =
    {
        (Value: SafeMigrationReportStatus.NoOperations, Code: "no_operations"),
        (Value: SafeMigrationReportStatus.Ready, Code: "ready"),
        (Value: SafeMigrationReportStatus.ReadyWithProviderOperations, Code: "ready_with_provider_operations"),
        (Value: SafeMigrationReportStatus.Blocked, Code: "blocked"),
        (Value: SafeMigrationReportStatus.RuntimeValidationRequired, Code: "runtime_validation_required"),
    };

    private static readonly (SafeMigrationOperationKind Value, string Code)[] s_operationKinds =
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
        (Value: SafeMigrationOperationKind.EnsureModelManagedData, Code: "ensure_model_managed_data"),
        (Value: SafeMigrationOperationKind.UpdateModelManagedData, Code: "update_model_managed_data"),
        (Value: SafeMigrationOperationKind.DeleteModelManagedData, Code: "delete_model_managed_data"),
    };

    private static readonly (SafeMigrationObservedState Value, string Code)[] s_states =
    {
        (Value: SafeMigrationObservedState.Missing, Code: "missing"),
        (Value: SafeMigrationObservedState.Matching, Code: "matching"),
        (Value: SafeMigrationObservedState.Different, Code: "different"),
        (Value: SafeMigrationObservedState.Unsupported, Code: "unsupported"),
        (Value: SafeMigrationObservedState.DataBlocked, Code: "data_blocked"),
        (Value: SafeMigrationObservedState.PrerequisiteMissing, Code: "prerequisite_missing"),
        (Value: SafeMigrationObservedState.TransitionReady, Code: "transition_ready"),
    };

    private static readonly (SafeMigrationAction Value, string Code)[] s_actions =
    {
        (Value: SafeMigrationAction.Apply, Code: "apply"), (Value: SafeMigrationAction.NoOp, Code: "no_op"),
        (Value: SafeMigrationAction.Repair, Code: "repair"),
        (Value: SafeMigrationAction.RejectUnsupported, Code: "reject_unsupported"),
        (Value: SafeMigrationAction.RejectDifferent, Code: "reject_different"),
        (Value: SafeMigrationAction.RejectDataBlocked, Code: "reject_data_blocked"),
        (Value: SafeMigrationAction.RejectPrerequisiteMissing, Code: "reject_prerequisite_missing"),
        (Value: SafeMigrationAction.ValidateAtRuntime, Code: "validate_at_runtime"),
    };

    private static readonly (SafeMigrationOperationalImpact Value, string Code)[] s_operationalImpacts =
    {
        (Value: SafeMigrationOperationalImpact.NotApplicable, Code: "not_applicable"),
        (Value: SafeMigrationOperationalImpact.TableRewritePossible, Code: "table_rewrite_possible"),
        (Value: SafeMigrationOperationalImpact.Unknown, Code: "unknown"),
    };

    private static readonly (SafeMigrationDatabaseObjectKind Value, string Code)[] s_objectKinds =
    {
        (Value: SafeMigrationDatabaseObjectKind.Table, Code: "table"),
        (Value: SafeMigrationDatabaseObjectKind.Column, Code: "column"),
        (Value: SafeMigrationDatabaseObjectKind.Index, Code: "index"),
        (Value: SafeMigrationDatabaseObjectKind.PrimaryKey, Code: "primary_key"),
        (Value: SafeMigrationDatabaseObjectKind.UniqueConstraint, Code: "unique_constraint"),
        (Value: SafeMigrationDatabaseObjectKind.CheckConstraint, Code: "check_constraint"),
        (Value: SafeMigrationDatabaseObjectKind.ForeignKey, Code: "foreign_key"),
    };

    /// <summary>Gets every original report-mode and status pairing as serializable discovery arguments.</summary>
    public static IEnumerable<object[]> ModeStatusCodes
    {
        get
        {
            foreach (var mode in s_modes)
            {
                foreach (var status in s_statuses)
                {
                    yield return [mode.Value, status.Value, mode.Code, status.Code];
                }
            }
        }
    }

    /// <summary>Gets the stable operation kinds wire mappings as serializable discovery arguments.</summary>
    public static IEnumerable<object[]> OperationKindCodes =>
        s_operationKinds.Select(static value => new object[] { value.Value, value.Code });

    /// <summary>Gets the stable states wire mappings as serializable discovery arguments.</summary>
    public static IEnumerable<object[]> ObservedStateCodes =>
        s_states.Select(static value => new object[] { value.Value, value.Code });

    /// <summary>Gets the stable actions wire mappings as serializable discovery arguments.</summary>
    public static IEnumerable<object[]> ActionCodes =>
        s_actions.Select(static value => new object[] { value.Value, value.Code });

    /// <summary>Gets the stable operational impacts wire mappings as serializable discovery arguments.</summary>
    public static IEnumerable<object[]> OperationalImpactCodes =>
        s_operationalImpacts.Select(static value => new object[] { value.Value, value.Code });

    /// <summary>Gets the stable object kinds wire mappings as serializable discovery arguments.</summary>
    public static IEnumerable<object[]> DatabaseObjectKindCodes =>
        s_objectKinds.Select(static value => new object[] { value.Value, value.Code });

    /// <summary>Requires the wire fixtures to cover every value in each closed public enum family.</summary>
    [Fact]
    public void WireCodeFixturesCoverEveryPublicEnumValue()
    {
        // Arrange: The independently serialized wire fixtures define the expected closed inventories.

        // Act
        var modes = Enum.GetValues<SafeMigrationReportMode>();
        var statuses = Enum.GetValues<SafeMigrationReportStatus>();
        var kinds = Enum.GetValues<SafeMigrationOperationKind>();
        var states = Enum.GetValues<SafeMigrationObservedState>();
        var actions = Enum.GetValues<SafeMigrationAction>();
        var impacts = Enum.GetValues<SafeMigrationOperationalImpact>();
        var objects = Enum.GetValues<SafeMigrationDatabaseObjectKind>();

        // Assert
        Assert.Equal(modes.Order(), s_modes.Select(static value => value.Value).Order());
        Assert.Equal(statuses.Order(), s_statuses.Select(static value => value.Value).Order());
        Assert.Equal(kinds.Order(), s_operationKinds.Select(static value => value.Value).Order());
        Assert.Equal(states.Order(), s_states.Select(static value => value.Value).Order());
        Assert.Equal(actions.Order(), s_actions.Select(static value => value.Value).Order());
        Assert.Equal(impacts.Order(), s_operationalImpacts.Select(static value => value.Value).Order());
        Assert.Equal(objects.Order(), s_objectKinds.Select(static value => value.Value).Order());
    }

    /// <summary>Serializes every mode/status pairing and preserves an absent migration target.</summary>
    /// <param name="mode">The report mode.</param>
    /// <param name="status">The report status.</param>
    /// <param name="expectedMode">The stable report-mode wire code.</param>
    /// <param name="expectedStatus">The stable status wire code.</param>
    [Theory]
    [MemberData(nameof(ModeStatusCodes))]
    public void ReportJsonPreservesModeAndStatusCodes(
        SafeMigrationReportMode mode,
        SafeMigrationReportStatus status,
        string expectedMode,
        string expectedStatus
    )
    {
        // Arrange
        var report = CreateReport(mode, status, targetMigrationId: null);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));

        // Assert
        Assert.Equal(expectedMode, document.RootElement.GetProperty("mode").GetString());
        Assert.Equal(expectedStatus, document.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, document.RootElement.GetProperty("targetMigrationId").ValueKind);
    }

    /// <summary>Serializes each stable operationKind code independently of the other wire families.</summary>
    /// <param name="value">The public enum value.</param>
    /// <param name="expectedCode">The exact stable wire code.</param>
    [Theory]
    [MemberData(nameof(OperationKindCodes))]
    public void ReportJsonPreservesOperationKindCode(
        SafeMigrationOperationKind value,
        string expectedCode
    )
    {
        // Arrange
        var report = CreateReport(assessments: [CreateWireAssessment(kind: value)]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));

        // Assert
        Assert.Equal(expectedCode, document.RootElement.GetProperty("assessments")[0]
            .GetProperty("operationKind").GetString());
    }

    /// <summary>Serializes each stable observedState code independently of the other wire families.</summary>
    /// <param name="value">The public enum value.</param>
    /// <param name="expectedCode">The exact stable wire code.</param>
    [Theory]
    [MemberData(nameof(ObservedStateCodes))]
    public void ReportJsonPreservesObservedStateCode(
        SafeMigrationObservedState value,
        string expectedCode
    )
    {
        // Arrange
        var report = CreateReport(assessments: [CreateWireAssessment(state: value)]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));

        // Assert
        Assert.Equal(expectedCode, document.RootElement.GetProperty("assessments")[0]
            .GetProperty("observedState").GetString());
    }

    /// <summary>Serializes each stable action code independently of the other wire families.</summary>
    /// <param name="value">The public enum value.</param>
    /// <param name="expectedCode">The exact stable wire code.</param>
    [Theory]
    [MemberData(nameof(ActionCodes))]
    public void ReportJsonPreservesActionCode(
        SafeMigrationAction value,
        string expectedCode
    )
    {
        // Arrange
        var report = CreateReport(assessments: [CreateWireAssessment(action: value)]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));

        // Assert
        Assert.Equal(expectedCode, document.RootElement.GetProperty("assessments")[0]
            .GetProperty("action").GetString());
    }

    /// <summary>Serializes each stable operationalImpact code independently of the other wire families.</summary>
    /// <param name="value">The public enum value.</param>
    /// <param name="expectedCode">The exact stable wire code.</param>
    [Theory]
    [MemberData(nameof(OperationalImpactCodes))]
    public void ReportJsonPreservesOperationalImpactCode(
        SafeMigrationOperationalImpact value,
        string expectedCode
    )
    {
        // Arrange
        var report = CreateReport(assessments: [CreateWireAssessment(impact: value)]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));

        // Assert
        Assert.Equal(expectedCode, document.RootElement.GetProperty("assessments")[0]
            .GetProperty("operationalImpact").GetString());
    }

    /// <summary>Serializes each unexpected-object kind independently of assessment codes.</summary>
    /// <param name="value">The public database-object kind.</param>
    /// <param name="expectedCode">The exact stable wire code.</param>
    [Theory]
    [MemberData(nameof(DatabaseObjectKindCodes))]
    public void ReportJsonPreservesDatabaseObjectKindCode(
        SafeMigrationDatabaseObjectKind value,
        string expectedCode
    )
    {
        // Arrange
        var report = CreateReport(unexpectedObjects:
            [new SafeMigrationUnexpectedObject(value, null, null, expectedCode, "unexpected")]);

        // Act
        using var document = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(report));

        // Assert
        Assert.Equal(expectedCode, document.RootElement.GetProperty("unexpectedObjects")[0]
            .GetProperty("objectKind").GetString());
    }

    /// <summary>Retains complete wire-family, closed-schema, fingerprint and provider-null-field coverage.</summary>
    [Fact]
    public void ReportJsonPreservesCompleteSchemaAndNullableFields()
    {
        // Arrange
        var modes = s_modes;

        var statuses = s_statuses;

        var operationKinds = s_operationKinds;

        var states = s_states;

        var actions = s_actions;

        var operationalImpacts = s_operationalImpacts;

        var objectKinds = s_objectKinds;

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
                actions[index % actions.Length].Value == SafeMigrationAction.ValidateAtRuntime
                    ? null
                    : index % 2 == 0,
                "stable_code",
                "provider_analysis",
                "policy_decision",
                operationalImpacts[index % operationalImpacts.Length].Value,
                index == 0
                    ? [new SafeMigrationFacetDifference("column_max_length", "200", "10")]
                    : null,
                actions[index % actions.Length].Value == SafeMigrationAction.ValidateAtRuntime
                    ? new SafeMigrationDeferredOrigin(null, 0, typeof(SqlOperation).FullName!)
                    : null))
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

        using var schema = LoadReportSchema();

        // Act
        using var completeDocument = JsonDocument.Parse(SafeMigrationReportJson.SerializeToUtf8Bytes(completeReport));
        var assessmentElements = completeDocument.RootElement.GetProperty("assessments");
        var unexpectedElements = completeDocument.RootElement.GetProperty("unexpectedObjects");

        // Assert
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
            operationalImpacts.Select(static value => value.Code),
            assessmentElements
                .EnumerateArray()
                .Take(operationalImpacts.Length)
                .Select(static value => value
                    .GetProperty("operationalImpact")
                    .GetString()));
        Assert.Equal(
            objectKinds.Select(static value => value.Code),
            unexpectedElements
                .EnumerateArray()
                .Select(static value => value
                    .GetProperty("objectKind")
                    .GetString()));

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
            ["mysql", "mariadb", "postgresql", "sqlite", "sqlserver"]);
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
                .GetProperty("assessment")
                .GetProperty("properties")
                .GetProperty("operationalImpact"),
            operationalImpacts.Select(static value => value.Code));
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
        AssertClosedObjectSurface(
            definitions.GetProperty("difference"),
            assessmentElements[0]
                .GetProperty("differences")[0]);
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

    /// <summary>Builds a valid assessment while varying exactly one serialized enum family.</summary>
    private static SafeMigrationAssessment CreateWireAssessment(
        SafeMigrationOperationKind kind = SafeMigrationOperationKind.EnsureTable,
        SafeMigrationObservedState state = SafeMigrationObservedState.Matching,
        SafeMigrationAction action = SafeMigrationAction.NoOp,
        SafeMigrationOperationalImpact impact = SafeMigrationOperationalImpact.NotApplicable
    ) => new(
        0,
        typeof(SafeMigrationOperation).FullName!,
        isSafeOperation: true,
        kind,
        "object_0",
        state,
        action,
        action == SafeMigrationAction.ValidateAtRuntime ? null : true,
        "stable_code",
        "provider_analysis",
        "policy_decision",
        impact,
        differences: null,
        action == SafeMigrationAction.ValidateAtRuntime
            ? new SafeMigrationDeferredOrigin(null, 0, typeof(SqlOperation).FullName!)
            : null);
}
