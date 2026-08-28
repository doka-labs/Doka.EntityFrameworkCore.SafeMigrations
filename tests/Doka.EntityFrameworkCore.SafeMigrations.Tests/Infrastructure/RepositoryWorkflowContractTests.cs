namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>
/// Pins security-relevant workflow structure without adding a YAML parser to
/// the product or engineering dependency graph.
/// </summary>
public sealed class RepositoryWorkflowContractTests
{
    private const string DependencyReviewAction =
        "actions/dependency-review-action@a1d282b36b6f3519aa1f3fc636f609c47dddb294 # v5.0.0";

    [Fact]
    public void DependencyReview_IsReadOnlyPinnedAndFailClosed()
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot(), ".github", "workflows", "dependency-review.yml"));

        Assert.Contains("name: dependency-review", workflow, StringComparison.Ordinal);
        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Contains("branches:\n      - main", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:\n  contents: read", workflow, StringComparison.Ordinal);
        Assert.Contains($"uses: {DependencyReviewAction}", workflow, StringComparison.Ordinal);
        Assert.Contains("fail-on-severity: high", workflow, StringComparison.Ordinal);
        Assert.Contains("comment-summary-in-pr: never", workflow, StringComparison.Ordinal);
        Assert.Contains("show-openssf-scorecard: true", workflow, StringComparison.Ordinal);
        Assert.Contains("retry-on-snapshot-warnings: true", workflow, StringComparison.Ordinal);
        Assert.Contains("retry-on-snapshot-warnings-timeout: 180", workflow, StringComparison.Ordinal);
        Assert.Contains("Require complete dependency snapshots", workflow, StringComparison.Ordinal);
        Assert.Contains("dependency-graph/compare/${BASE_SHA}...${HEAD_SHA}", workflow, StringComparison.Ordinal);
        Assert.Contains("verify-dependency-snapshot-headers.sh", workflow, StringComparison.Ordinal);
        Assert.Contains("persist-credentials: false", workflow, StringComparison.Ordinal);

        Assert.DoesNotContain("pull_request_target", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("pull-requests: write", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("warn-only:", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("continue-on-error:", workflow, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Apache-2.0")]
    [InlineData("BSD-2-Clause")]
    [InlineData("BSD-3-Clause")]
    [InlineData("CC0-1.0")]
    [InlineData("ISC")]
    [InlineData("MIT")]
    [InlineData("MIT-0")]
    [InlineData("Unlicense")]
    public void DependencyReview_DeclaresEveryApprovedLicense(
        string license
    )
    {
        var workflow = File.ReadAllText(
            Path.Combine(RepositoryRoot(), ".github", "workflows", "dependency-review.yml"));

        Assert.Contains(license, workflow, StringComparison.Ordinal);
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
