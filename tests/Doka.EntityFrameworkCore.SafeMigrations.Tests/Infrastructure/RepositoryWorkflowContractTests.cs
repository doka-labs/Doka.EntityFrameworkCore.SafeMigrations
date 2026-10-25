namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Pins required workflow structure without adding a YAML parser to
/// the product or engineering dependency graph.
/// </summary>
public sealed class RepositoryWorkflowContractTests
{
    private const string DependencyReviewAction =
        "actions/dependency-review-action@a1d282b36b6f3519aa1f3fc636f609c47dddb294 # v5.0.0";

    /// <summary>
    /// Keeps every benchmark budget informational without suppressing execution or qualification failures.
    /// </summary>
    [Fact]
    public void QualityWorkflow_ReportsAllBenchmarkBudgetsWithoutIgnoringFailures()
    {
        var workflowPath = Path.Combine(RepositoryRoot(), ".github", "workflows", "quality-gates.yml");
        var providers = new[] { "core", "mysql", "postgresql", "sqlite", "sqlserver" };

        var workflow = File.ReadAllText(workflowPath);

        Assert.Equal(providers.Length, Regex.Count(workflow, "--report-only"));

        foreach (var provider in providers)
        {
            Assert.Contains($"--report-only --output artifacts/performance/{provider}-results.json",
                workflow, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("continue-on-error:", workflow, StringComparison.Ordinal);
        Assert.Contains("- name: Core tests", workflow, StringComparison.Ordinal);
        Assert.Contains("- name: Qualify reproducible packages", workflow, StringComparison.Ordinal);
        Assert.Contains("- name: Generate and validate SPDX SBOM", workflow, StringComparison.Ordinal);
        Assert.Contains("artifacts/performance", workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Keeps untrusted restore separate from snapshot writes and binds verdicts to the PR head.
    /// </summary>
    [Fact]
    public void DependencyReview_SeparatesSnapshotWritesAndRemainsFailClosed()
    {
        // Arrange
        var workflowDirectory = Path.Combine(RepositoryRoot(), ".github", "workflows");
        var capture = File.ReadAllText(Path.Combine(workflowDirectory, "dependency-review.yml"));
        var receiver = File.ReadAllText(Path.Combine(workflowDirectory, "dependency-review-results.yml"));
        var restore = File.ReadAllText(Path.Combine(workflowDirectory, "dependency-snapshot.yml"));

        // Act
        var submit = receiver.Split("  submit-nuget:\n", StringSplitOptions.None)[1]
            .Split("  review:\n", StringSplitOptions.None)[0];

        var review = receiver.Split("  review:\n", StringSplitOptions.None)[1];

        // Assert
        Assert.Contains("pull_request:", capture, StringComparison.Ordinal);
        // WHY: Registered release-line admission belongs to the trusted receiver, not a main-only trigger.
        Assert.DoesNotContain("branches:", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("branches-ignore:", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", capture + restore + review, StringComparison.Ordinal);
        Assert.DoesNotContain("checks: write", capture + restore + submit, StringComparison.Ordinal);
        Assert.DoesNotContain("pull_request_target:", capture + receiver, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow_dispatch:", capture + receiver, StringComparison.Ordinal);
        Assert.DoesNotContain("actions/checkout@", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet restore", capture, StringComparison.Ordinal);
        Assert.Contains("workflow_run:", receiver, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ github.workflow_sha }}", receiver, StringComparison.Ordinal);
        Assert.Contains("contents: write", submit, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ github.workflow_sha }}", submit, StringComparison.Ordinal);
        Assert.DoesNotContain("ref: ${{ matrix.sha }}", submit, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet restore", submit, StringComparison.Ordinal);
        Assert.Contains("artifact-ids: ${{ matrix.artifact_id }}", submit, StringComparison.Ordinal);
        Assert.Contains("prepare-dependency-snapshot-inputs.py receive", submit, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/dependency-snapshot.yml", capture, StringComparison.Ordinal);
        Assert.DoesNotContain("uses: ./.github/workflows/dependency-snapshot.yml", receiver,
            StringComparison.Ordinal);

        Assert.Contains("cache-mode: none", capture, StringComparison.Ordinal);
        Assert.Contains("cache-mode: none", restore, StringComparison.Ordinal);
        Assert.Contains("ref: ${{ inputs.sha }}", restore, StringComparison.Ordinal);
        Assert.Contains("--locked-mode --disable-build-servers", restore, StringComparison.Ordinal);
        Assert.Contains("detectorsFilter: MSBuildBinaryLog", submit, StringComparison.Ordinal);
        Assert.Contains("correlator: safemigrations-nuget-resolved-v1", submit, StringComparison.Ordinal);
        Assert.Contains($"uses: {DependencyReviewAction}", review, StringComparison.Ordinal);
        Assert.Contains("fail-on-severity: high", review, StringComparison.Ordinal);
        Assert.Contains("show-openssf-scorecard: true", review, StringComparison.Ordinal);
        Assert.Contains("retry-on-snapshot-warnings-timeout: 180", review, StringComparison.Ordinal);
        Assert.Contains("verify-dependency-snapshot-headers.sh", review, StringComparison.Ordinal);
        Assert.Contains("gh api --paginate --slurp", review, StringComparison.Ordinal);
        Assert.Contains("verify-dependency-license-exceptions.py", review, StringComparison.Ordinal);
        Assert.Contains("dependency-review-handoff.py finish", review, StringComparison.Ordinal);
        Assert.Contains("steps.guards.outcome == 'success'", review, StringComparison.Ordinal);
        Assert.DoesNotContain("pkg:nuget/Doka.EntityFrameworkCore.MySql@", receiver, StringComparison.Ordinal);
        Assert.DoesNotContain("warn-only:", receiver, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error:", receiver, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Apache-2.0")]
    [InlineData("BSD-2-Clause")]
    [InlineData("BSD-3-Clause")]
    [InlineData("CC0-1.0")]
    [InlineData("ISC")]
    [InlineData("MIT")]
    [InlineData("MIT-0")]
    [InlineData("PostgreSQL")]
    [InlineData("Unlicense")]
    public void DependencyReview_DeclaresEveryApprovedLicense(
        string license
    )
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot(), ".github", "workflows", "dependency-review-results.yml"));

        Assert.Contains(license, workflow, StringComparison.Ordinal);
    }

    /// <summary>
    /// Starts the same dependency pipeline without replacing or skipping required qualification.
    /// </summary>
    [Fact]
    public void DependencyReview_ManualStartupReusesThePipelineWithoutChangingQualification()
    {
        // Arrange
        var workflowDirectory = Path.Combine(RepositoryRoot(), ".github", "workflows");
        var ci = File.ReadAllText(Path.Combine(workflowDirectory, "ci.yml"));
        var receiver = File.ReadAllText(Path.Combine(workflowDirectory, "dependency-review-results.yml"));

        // Act
        var quality = ci.Split("  quality-gates:\n", StringSplitOptions.None)[1]
            .Split("  dependency-review-startup:\n", StringSplitOptions.None)[0];

        var startup = ci.Split("  dependency-review-startup:\n", StringSplitOptions.None)[1];

        // Assert
        Assert.Contains("workflow_dispatch:", ci, StringComparison.Ordinal);
        Assert.Contains("workflow_call:", receiver, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/quality-gates.yml", quality, StringComparison.Ordinal);
        Assert.Contains("contents: read", quality, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", quality, StringComparison.Ordinal);
        Assert.DoesNotContain("if:", quality, StringComparison.Ordinal);
        Assert.Contains("name: Dependency review startup", startup, StringComparison.Ordinal);
        Assert.Contains("github.event_name == 'workflow_dispatch'", startup, StringComparison.Ordinal);
        Assert.Contains("inputs.dependency-review-pr != ''", startup, StringComparison.Ordinal);
        Assert.Contains("uses: ./.github/workflows/dependency-review-results.yml", startup,
            StringComparison.Ordinal);

        Assert.Contains("dependency-review-pr: ${{ inputs.dependency-review-pr }}", startup,
            StringComparison.Ordinal);

        Assert.Equal(2, Regex.Count(receiver, "DEPENDENCY_REVIEW_PR: "));
        Assert.Equal(1, Regex.Count(receiver, $"uses: {Regex.Escape(DependencyReviewAction)}"));
        Assert.Contains("steps.guards.outcome == 'success'", receiver, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error:", ci + receiver, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureCodeRunbook_CoversGeneratedConstraintAndDependencyCodes()
    {
        var repositoryRoot = RepositoryRoot();
        var runbook = File.ReadAllText(
            Path.Combine(repositoryRoot, "docs", "runbooks", "failure-codes.md"));

        var analyzer = File.ReadAllText(
            Path.Combine(
                repositoryRoot,
                "src",
                "Doka.EntityFrameworkCore.SafeMigrations.MySql",
                "Analysis",
                "MySqlSafeMigrationProviderAnalyzer.Indexes.cs"));

        var prefixes = Regex
            .Matches(
                analyzer,
                "QualifyProjectedConstraint\\(\\s*result,\\s*liveAnalysis,\\s*projectedAnalysis,"
                + "\\s*\"(?<value>[a-z_]+)\"\\);",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Groups["value"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var suffixes = Regex
            .Matches(
                analyzer,
                "\\$\"\\{codePrefix\\}_(?<value>[a-z_]+)\"",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Groups["value"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(["primary_key", "unique_constraint"], prefixes);
        Assert.Equal(
            [
                "exceeds_physical_limit",
                "too_many_columns",
                "storage_engine_unsupported",
                "length_unverifiable",
            ],
            suffixes);

        foreach (var prefix in prefixes)
        {
            foreach (var suffix in suffixes)
            {
                Assert.Contains($"`{prefix}_{suffix}`", runbook, StringComparison.Ordinal);
            }
        }

        Assert.Contains("`foreign_key_column_order`", runbook, StringComparison.Ordinal);
        Assert.Contains("`foreign_key_principal_column_order`", runbook, StringComparison.Ordinal);
        Assert.Contains("`projected_dependency_handoff`", runbook, StringComparison.Ordinal);
    }

    /// <summary>Retains exact emitted generated-expression codes in the shared runbook across providers.</summary>
    /// <param name="provider">The independent provider package containing the actual code producer.</param>
    /// <param name="sourceFile">The producer path relative to that package.</param>
    /// <param name="code">The stable emitted code whose documentation must not drift.</param>
    [Theory]
    [InlineData("PostgreSql", "Analysis/PostgreSqlSafeMigrationProviderAnalyzer.GeneratedCaseTypes.cs",
        "projected_generated_case_target_occupied")]
    [InlineData("PostgreSql", "Analysis/PostgreSqlSafeMigrationProviderAnalyzer.GeneratedCaseTypes.cs",
        "projected_generated_case_binding_unproven")]
    [InlineData("MySql", "Features/Columns/MySqlGeneratedColumnExpressionCapability.cs",
        "generated_column_nondeterministic_expression")]
    [InlineData("SqlServer", "Features/Columns/SqlServerSafeMigrationCatalogSqlBuilder.ComputedColumns.cs",
        "computed_expression_unproven")]
    public void FailureCodeRunbook_CoversEmittedGeneratedExpressionCodesAcrossProviders(
        string provider, string sourceFile, string code)
    {
        // Arrange
        var repositoryRoot = RepositoryRoot();
        var sourcePath = Path.Combine(repositoryRoot, "src",
            "Doka.EntityFrameworkCore.SafeMigrations." + provider,
            sourceFile.Replace('/', Path.DirectorySeparatorChar));

        var source = File.ReadAllText(sourcePath);
        var runbook = File.ReadAllText(Path.Combine(repositoryRoot, "docs", "runbooks", "failure-codes.md"));

        // Act
        var emittedCodes = Regex.Matches(source, "\"(?<code>" + Regex.Escape(code) + ")\"",
                RegexOptions.CultureInvariant)
            .Select(static match => match.Groups["code"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal([code], emittedCodes);
        Assert.Contains("`" + code + "`", runbook, StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "global.json")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate the SafeMigrations repository root.");
    }
}
