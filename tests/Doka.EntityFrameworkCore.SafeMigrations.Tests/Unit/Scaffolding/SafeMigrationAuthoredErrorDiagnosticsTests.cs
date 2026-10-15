namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Verifies actionable authored errors without changing their public exception contracts.</summary>
public sealed class SafeMigrationAuthoredErrorDiagnosticsTests
{
    /// <summary>Undefined scaffolding modes name the setting and its supported choices.</summary>
    [Fact]
    public void InvalidScaffoldingModePreservesParameterAndExplainsSupportedChoices()
    {
        // Arrange
        var builder = new SafeMigrationOptionsBuilder();

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseScaffoldingMode((SafeMigrationScaffoldingMode)int.MaxValue));

        // Assert
        Assert.Equal("mode", exception.ParamName);
        Assert.Contains("Strict", exception.Message, StringComparison.Ordinal);
        Assert.Contains("LegacyConvergence", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SafeMigrationScaffoldingMode.Strict, builder.Mode);
    }

    /// <summary>Unsupported convergence policies retain the parameter and identify safe alternatives.</summary>
    [Fact]
    public void InvalidConvergencePolicyPreservesParameterAndExplainsSupportedChoices()
    {
        // Arrange
        var builder = new SafeMigrationOptionsBuilder();

        // Act
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.UseLegacyConvergencePolicy(SafeMigrationPolicy.ExistenceOnly));

        // Assert
        Assert.Equal("policy", exception.ParamName);
        Assert.Contains("ThrowIfDifferent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("RepairIfSafe", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SafeMigrationPolicy.ThrowIfDifferent, builder.LegacyConvergencePolicy);
    }

    /// <summary>Conflicting provider setup names the registration boundary and corrective action.</summary>
    [Fact]
    public void ConflictingProviderRegistrationExplainsSingleContextConfiguration()
    {
        // Arrange
        var services = new ServiceCollection();
        SafeMigrationCanonicalContextConfiguration.Register(services, typeof(FirstProvider), typeof(FirstContext));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationCanonicalContextConfiguration.Register(
                services,
                typeof(SecondProvider),
                typeof(FirstContext)));

        // Assert
        Assert.Contains("different provider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("one provider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("internal service provider", exception.Message, StringComparison.Ordinal);
        Assert.Single(services);
    }

    /// <summary>An identical registration remains idempotent and does not duplicate services.</summary>
    [Fact]
    public void IdenticalProviderRegistrationRemainsIdempotent()
    {
        // Arrange
        var services = new ServiceCollection();
        SafeMigrationCanonicalContextConfiguration.Register(services, typeof(FirstProvider), typeof(FirstContext));

        // Act
        SafeMigrationCanonicalContextConfiguration.Register(services, typeof(FirstProvider), typeof(FirstContext));

        // Assert
        Assert.Single(services);
    }

    /// <summary>Mismatched model errors retain evidence without leaking arbitrary constructor inputs.</summary>
    [Fact]
    public void ModelMismatchExplainsModelBoundaryWithoutIncludingFingerprintPayloads()
    {
        // Arrange
        const string expected = "sensitive-expected\r\nforged-log";
        const string actual = "sensitive-actual";

        // Act
        var exception = new SafeMigrationModelMismatchException(expected, actual);

        // Assert
        Assert.Equal(expected, exception.ExpectedFingerprint);
        Assert.Equal(actual, exception.ActualFingerprint);
        Assert.Contains("canonical migration model", exception.Message, StringComparison.Ordinal);
        Assert.Contains("model configuration", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not a live database", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("forged-log", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Matching expected fingerprints remain accepted without changing canonical identity.</summary>
    [Fact]
    public void MatchingFingerprintRemainsAccepted()
    {
        // Arrange
        var fingerprint = $"safe-relational-model:v1:test:sha256:{new string('a', 64)}";

        // Act
        var exception = Record.Exception(() =>
            SafeMigrationModelFingerprint.ValidateExpected(fingerprint, fingerprint));

        // Assert
        Assert.Null(exception);
    }

    /// <summary>A known context is diagnostic evidence only and does not alter the fingerprint contract.</summary>
    [Fact]
    public void ModelMismatchIdentifiesKnownRuntimeContext()
    {
        // Arrange
        var expected = $"safe-relational-model:v1:test:sha256:{new string('a', 64)}";
        var actual = $"safe-relational-model:v1:test:sha256:{new string('b', 64)}";

        // Act
        var exception = Assert.Throws<SafeMigrationModelMismatchException>(() =>
            SafeMigrationModelFingerprint.ValidateExpected(actual, expected, typeof(FirstContext)));

        // Assert
        Assert.Contains(nameof(FirstContext), exception.Message, StringComparison.Ordinal);
        Assert.Equal(expected, exception.ExpectedFingerprint);
        Assert.Equal(actual, exception.ActualFingerprint);
        Assert.DoesNotContain(expected, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(actual, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Malformed fingerprint errors retain the parameter rather than rendering untrusted input.</summary>
    [Fact]
    public void MalformedFingerprintRetainsParameterWithoutIncludingSuppliedValue()
    {
        // Arrange
        const string fingerprint = "sensitive-input\r\nforged-log";

        // Act
        var exception = Assert.Throws<ArgumentException>(() =>
            SafeMigrationModelFingerprint.ValidateExpected(fingerprint, expectedFingerprint: null));

        // Assert
        Assert.Equal("actualFingerprint", exception.ParamName);
        Assert.Contains("safe-relational-model:v1", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-input", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("forged-log", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Null fingerprints keep their original public parameter contract.</summary>
    [Fact]
    public void NullFingerprintRetainsParameterContract()
    {
        // Arrange
        string fingerprint = null!;

        // Act
        var exception = Assert.Throws<ArgumentNullException>(() =>
            SafeMigrationModelFingerprint.ValidateExpected(fingerprint, expectedFingerprint: null));

        // Assert
        Assert.Equal("actualFingerprint", exception.ParamName);
    }

    /// <summary>Unsupported operations identify their structural owner but never their SQL payload.</summary>
    [Fact]
    public void UnsupportedOperationIncludesBoundedOwnerAndCorrectiveAction()
    {
        // Arrange
        var operation = new AlterTableOperation { Name = "items\nforged-log", Schema = "tenant" };
        operation["Test:Secret"] = "sensitive-annotation";

        // Act
        var exception = SafeMigrationOperationCompatibility.Unsupported(operation);

        // Assert
        Assert.Contains("AlterTableOperation", exception.Message, StringComparison.Ordinal);
        Assert.Contains("items", exception.Message, StringComparison.Ordinal);
        Assert.Contains("tenant", exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', exception.Message);
        Assert.DoesNotContain("sensitive-annotation", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Raw SQL rejection does not echo the SQL or attached annotation values.</summary>
    [Fact]
    public void UnsupportedRawSqlDoesNotExposePayload()
    {
        // Arrange
        var operation = new SqlOperation { Sql = "SELECT 'sensitive-row';" };

        // Act
        var exception = SafeMigrationOperationCompatibility.Unsupported(operation);

        // Assert
        Assert.Contains("SqlOperation", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-row", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SELECT", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Large authored identifiers cannot turn an unsupported-operation error into an unbounded log.</summary>
    [Fact]
    public void UnsupportedOperationBoundsLongMetadataWithoutGuessingAnotherOwner()
    {
        // Arrange
        var operation = new AlterTableOperation { Name = new string('x', 10_000), Schema = "tenant" };

        // Act
        var exception = SafeMigrationOperationCompatibility.Unsupported(operation);

        // Assert
        Assert.Contains("tenant", exception.Message, StringComparison.Ordinal);
        Assert.Contains("...", exception.Message, StringComparison.Ordinal);
        Assert.True(exception.Message.Length <= 2048);
        Assert.DoesNotContain(new string('x', 129), exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Missing relational metadata names the known owner without including seed values.</summary>
    [Fact]
    public void MissingModelManagedTableExplainsOwnerWithoutExposingSeedValues()
    {
        // Arrange
        var operation = new InsertDataOperation
        {
            Schema = "tenant",
            Table = "roles\nforged-log",
            Columns = ["id", "description"],
            Values = new object?[,] { { 1, "sensitive-seed-value" } },
        };

        var differ = new SafeMigrationMigrationsModelDiffer(
            new MissingTableModelDiffer(operation),
            new SafeMigrationScaffoldingConfiguration(
                true,
                SafeMigrationScaffoldingMode.Strict,
                SafeMigrationPolicy.ThrowIfDifferent));

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(null, null));

        // Assert
        Assert.Contains("tenant", exception.Message, StringComparison.Ordinal);
        Assert.Contains("roles", exception.Message, StringComparison.Ordinal);
        Assert.Contains("table mappings", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', exception.Message);
        Assert.DoesNotContain("sensitive-seed-value", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Invalid catalog ownership remains fatal and identifies a safe diagnostic next step.</summary>
    [Fact]
    public void OutOfOrderCatalogResultExplainsInvariantWithoutAcceptingEvidence()
    {
        // Arrange
        var consumed = 0;

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationCatalogWorkOrder.ValidateResultOrdinal(7, [4], ref consumed));

        // Assert
        Assert.Contains("out-of-order ordinal", exception.Message, StringComparison.Ordinal);
        Assert.Contains("redacted evidence", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, consumed);
    }

    /// <summary>Incomplete catalog evidence remains rejected rather than described as a successful result.</summary>
    [Fact]
    public void IncompleteCatalogResultExplainsInvariantAndRemainsRejected()
    {
        // Arrange
        int[] ordinals = [4, 7];

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationCatalogWorkOrder.ValidateCompletion(1, ordinals));

        // Assert
        Assert.Contains("inconsistent submitted row count", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Stop migration analysis", exception.Message, StringComparison.Ordinal);
        Assert.Contains("redacted evidence", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Invalid source layout reports a recovery step without printing source contents.</summary>
    [Fact]
    public void InvalidGeneratedSourceExplainsRecoveryWithoutExposingSource()
    {
        // Arrange
        const string source = "sensitive-source\r\ninvalid\n";

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationGeneratedSource.GetConsistentNewLine(source));

        // Assert
        Assert.Contains("generator", exception.Message, StringComparison.Ordinal);
        Assert.Contains("compatible", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sensitive-source", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Definition validation keeps its original parameter and actionable rule.</summary>
    [Fact]
    public void InvalidDefinitionRetainsParameterAndRule()
    {
        // Arrange
        const string parameterName = "columns";

        // Act
        var exception = Assert.Throws<ArgumentException>(() =>
            SafeMigrationDefinitionValidator.Identifiers(["id", "id"], parameterName));

        // Assert
        Assert.Equal(parameterName, exception.ParamName);
        Assert.Contains("Duplicate identifiers", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FirstProvider { }

    private sealed class SecondProvider { }

    private sealed class FirstContext { }

    private sealed class MissingTableModelDiffer(
        MigrationOperation operation
    ) : IMigrationsModelDiffer
    {
        public bool HasDifferences(
            IRelationalModel? source,
            IRelationalModel? target
        ) => true;

        public IReadOnlyList<MigrationOperation> GetDifferences(
            IRelationalModel? source,
            IRelationalModel? target
        ) => [operation];
    }
}
