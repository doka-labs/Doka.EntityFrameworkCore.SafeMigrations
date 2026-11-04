namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Pins current-run evidence and safe build logging with native argument parsing only.</summary>
public sealed class BenchmarkProgramInvocationTests
{
    /// <summary>Preserves ordinary native measurement, discovery and statistics options.</summary>
    /// <param name="arguments">The native argument vector, parsed without running any workload.</param>
    [Theory]
    [InlineData("--filter", "*", "--job", "Dry")]
    [InlineData("--list", "flat")]
    [InlineData("--info")]
    [InlineData("--allStats", "--memory", "--join")]
    public void IsSafeInvocation_AcceptsOrdinaryNativeOptions(
        params string[] arguments
    )
    {
        // Arrange
        var (parsed, _, options) = ConfigParser.Parse(arguments, NullLogger.Instance, DefaultConfig.Instance);

        // Act
        var safe = parsed && BenchmarkProgram.IsSafeInvocation(options);

        // Assert
        Assert.True(parsed);
        Assert.True(safe);
    }

    /// <summary>Rejects prior-run evidence and sensitive build logs, including discovery combinations.</summary>
    /// <param name="arguments">The native argument vector containing at least one disallowed option.</param>
    [Theory]
    [InlineData("--resume")]
    [InlineData("--logBuildOutput")]
    [InlineData("--generateBinLog")]
    [InlineData("--RESUME")]
    [InlineData("--list", "flat", "--resume")]
    [InlineData("--info", "--logBuildOutput")]
    [InlineData("--info", "--generateBinLog")]
    [InlineData("--resume", "--logBuildOutput", "--generateBinLog")]
    public void IsSafeInvocation_RejectsUnsafeNativeOptions(
        params string[] arguments
    )
    {
        // Arrange
        var (parsed, _, options) = ConfigParser.Parse(arguments, NullLogger.Instance, DefaultConfig.Instance);

        // Act
        var safe = parsed && BenchmarkProgram.IsSafeInvocation(options);

        // Assert
        Assert.True(parsed);
        Assert.False(safe);
    }

    /// <summary>Rejects unsafe options after native response-file expansion.</summary>
    /// <param name="argument">The disallowed option supplied through the native response file.</param>
    [Theory]
    [InlineData("--resume")]
    [InlineData("--logBuildOutput")]
    [InlineData("--generateBinLog")]
    public void IsSafeInvocation_RejectsUnsafeNativeResponseFileOptions(
        string argument
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var responsePath = Path.Combine(evidence.DirectoryPath, "benchmark.rsp");
        File.WriteAllText(responsePath, argument);
        var (parsed, _, options) = ConfigParser.Parse(
            ["@" + responsePath], NullLogger.Instance, DefaultConfig.Instance);

        // Act
        var safe = parsed && BenchmarkProgram.IsSafeInvocation(options);

        // Assert
        Assert.True(parsed);
        Assert.False(safe);
    }

    /// <summary>Protects driver-owned variable names without rejecting unrelated or longer variable names.</summary>
    /// <param name="variable">The independent synthetic native environment-variable setting.</param>
    /// <param name="expectedSafe">Whether the variable preserves driver-owned evidence and connection inputs.</param>
    [Theory]
    [InlineData("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT:synthetic", false)]
    [InlineData("safe_migrations_attribution_output:synthetic", false)]
    [InlineData("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT:synthetic", false)]
    [InlineData("safe_migrations_sqlserver_attribution_output:synthetic", false)]
    [InlineData("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_CONNECTION:synthetic", false)]
    [InlineData("safe_migrations_sqlserver_attribution_connection:synthetic", false)]
    [InlineData("DOKA_BENCHMARK_TEST_LABEL:synthetic", true)]
    [InlineData("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT_BACKUP:synthetic", true)]
    [InlineData("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT_BACKUP:synthetic", true)]
    [InlineData("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_CONNECTION_BACKUP:synthetic", true)]
    public void IsSafeInvocation_ProtectsOwnedNativeEnvironmentVariables(
        string variable,
        bool expectedSafe
    )
    {
        // Arrange
        var (parsed, _, options) = ConfigParser.Parse(
            ["--envVars", variable], NullLogger.Instance, DefaultConfig.Instance);

        // Act
        var safe = parsed && BenchmarkProgram.IsSafeInvocation(options);

        // Assert
        Assert.True(parsed);
        Assert.Equal(variable, Assert.Single(options.EnvironmentVariables));
        Assert.Equal(expectedSafe, safe);
    }

    /// <summary>Applies the same owned-variable boundary after native response-file expansion.</summary>
    /// <param name="variable">The independent synthetic variable setting stored in the response file.</param>
    /// <param name="expectedSafe">Whether the expanded variable preserves driver-owned inputs.</param>
    [Theory]
    [InlineData("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT:synthetic", false)]
    [InlineData("safe_migrations_attribution_output:synthetic", false)]
    [InlineData("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT:synthetic", false)]
    [InlineData("safe_migrations_sqlserver_attribution_output:synthetic", false)]
    [InlineData("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_CONNECTION:synthetic", false)]
    [InlineData("safe_migrations_sqlserver_attribution_connection:synthetic", false)]
    [InlineData("DOKA_BENCHMARK_TEST_LABEL:synthetic", true)]
    [InlineData("SAFE_MIGRATIONS_ATTRIBUTION_OUTPUT_BACKUP:synthetic", true)]
    [InlineData("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_OUTPUT_BACKUP:synthetic", true)]
    [InlineData("SAFE_MIGRATIONS_SQLSERVER_ATTRIBUTION_CONNECTION_BACKUP:synthetic", true)]
    public void IsSafeInvocation_ProtectsOwnedNativeResponseFileEnvironmentVariables(
        string variable,
        bool expectedSafe
    )
    {
        // Arrange
        using var evidence = new BenchmarkProgramTestEvidence();
        var responsePath = Path.Combine(evidence.DirectoryPath, "benchmark.rsp");
        File.WriteAllText(responsePath, "--envVars " + variable);
        var (parsed, _, options) = ConfigParser.Parse(
            ["@" + responsePath], NullLogger.Instance, DefaultConfig.Instance);

        // Act
        var safe = parsed && BenchmarkProgram.IsSafeInvocation(options);

        // Assert
        Assert.True(parsed);
        Assert.Equal(variable, Assert.Single(options.EnvironmentVariables));
        Assert.Equal(expectedSafe, safe);
    }
}
