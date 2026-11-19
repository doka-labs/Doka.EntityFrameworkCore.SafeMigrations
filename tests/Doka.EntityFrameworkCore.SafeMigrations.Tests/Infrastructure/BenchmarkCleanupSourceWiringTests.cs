namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Pins real benchmark cleanup to the already behavior-tested nullable ownership foundation.</summary>
public sealed class BenchmarkCleanupSourceWiringTests
{
    /// <summary>Every acquired resource must use and await the protected disposal boundary.</summary>
    /// <param name="provider">The owning benchmark source project.</param>
    /// <param name="file">The actual source file containing the fixture.</param>
    /// <param name="fixture">The exact benchmark fixture whose cleanup is inspected.</param>
    /// <param name="resources">The ordered resource fields owned by that fixture.</param>
    [Theory]
    [InlineData("MySql", "MySqlMaintenanceAttribution.cs", "MySqlMaintenanceAttribution", "_context")]
    [InlineData("PostgreSql",
        "PostgreSqlColumnAttributionWorkload.cs",
        "PostgreSqlColumnAttributionWorkload",
        "_context")]
    [InlineData("PostgreSql",
        "PostgreSqlMaintenanceAttribution.cs",
        "PostgreSqlMaintenanceAttribution",
        "_connection,_context")]
    [InlineData("Sqlite", "SqliteMaintenanceBenchmarks.cs", "SqliteMaintenanceAttribution", "_fixture")]
    [InlineData("Sqlite", "SqliteMaintenanceBenchmarks.cs", "SqliteContiguousMaintenanceAttribution", "_fixture")]
    public void DisposeAsync_ActualFixturesAwaitProtectedResourceCompletion(
        string provider,
        string file,
        string fixture,
        string resources
    )
    {
        // Arrange
        var path = Path.Combine(
            RepositoryRoot(),
            "benchmarks",
            $"Doka.EntityFrameworkCore.SafeMigrations.{provider}.Benchmarks",
            file);

        var source = File.ReadAllText(path);
        var expected = resources.Split(',');

        // Act
        var protectedCleanup = HasAwaitedFoundationDisposal(source, fixture, expected);

        // Assert
        Assert.True(protectedCleanup, $"{fixture} must await protected cleanup for {resources}.");
    }

    /// <summary>The wiring guard cannot accept a direct, unawaited or incomplete cleanup lookalike.</summary>
    /// <param name="body">The isolated cleanup body, including positive and adversarial controls.</param>
    /// <param name="expected">Whether all required protected ownership awaits are present.</param>
    [Theory]
    [InlineData("await SafeMigrationAnalysisCleanup.DisposeAsync(_context, null, \"evidence\");", true)]
    [InlineData("await _context.DisposeAsync();", false)]
    [InlineData("SafeMigrationAnalysisCleanup.DisposeAsync(_context, null, \"evidence\");", false)]
    [InlineData("_ = SafeMigrationAnalysisCleanup.DisposeAsync(_context, null, \"evidence\");", false)]
    [InlineData("await SafeMigrationAnalysisCleanup.DisposeAsync(_other, null, \"evidence\");", false)]
    [InlineData("", false)]
    public void WiringGuard_RejectsUnprotectedOrUnawaitedDisposal(
        string body,
        bool expected
    )
    {
        // Arrange
        var source = $$"""
            class Fixture
            {
                async ValueTask DisposeAsync()
                {
                    {{body}}
                }
            }
            """;

        // Act
        var protectedCleanup = HasAwaitedFoundationDisposal(source, "Fixture", ["_context"]);

        // Assert
        Assert.Equal(expected, protectedCleanup);
    }

    /// <summary>A later resource cannot lose the earlier cleanup failure's identity.</summary>
    /// <param name="primary">The earlier failure forwarded to the second ownership boundary.</param>
    /// <param name="expected">Whether the second cleanup preserves the first failure.</param>
    [Theory]
    [InlineData("originalFailure", true)]
    [InlineData("null", false)]
    public void WiringGuard_RejectsLostPrimaryFailure(
        string primary,
        bool expected
    )
    {
        // Arrange
        var source = $$"""
            class Fixture
            {
                async ValueTask DisposeAsync()
                {
                    await SafeMigrationAnalysisCleanup.DisposeAsync(_connection, null, "connection");
                    await SafeMigrationAnalysisCleanup.DisposeAsync(_context, {{primary}}, "context");
                }
            }
            """;

        // Act
        var protectedCleanup = HasAwaitedFoundationDisposal(source, "Fixture", ["_connection", "_context"]);

        // Assert
        Assert.Equal(expected, protectedCleanup);
    }

    /// <summary>Checks syntax rather than formatting or a comment that merely names the foundation.</summary>
    /// <param name="source">The complete source containing the real or adversarial fixture.</param>
    /// <param name="fixture">The exact class identity to inspect.</param>
    /// <param name="resources">The ordered acquired fields requiring protected disposal.</param>
    /// <returns>Whether every disposal call is an awaited foundation call for the exact owned fields.</returns>
    private static bool HasAwaitedFoundationDisposal(
        string source,
        string fixture,
        string[] resources
    )
    {
        var declaration = CSharpSyntaxTree
            .ParseText(source)
            .GetRoot()
            .DescendantNodes()
            .Single(node => node.IsKind(SyntaxKind.ClassDeclaration)
                && node
                    .ChildTokens()
                    .Any(token => token.ValueText == fixture));

        var cleanup = declaration
            .ChildNodes()
            .Single(node => node.IsKind(SyntaxKind.MethodDeclaration)
                && node
                    .ChildTokens()
                    .Any(token => token.ValueText == "DisposeAsync"));

        var calls = cleanup
            .DescendantNodes()
            .Where(node => node.IsKind(SyntaxKind.InvocationExpression)
                && node
                    .ChildNodes()
                    .First()
                    .ToString()
                    .EndsWith(".DisposeAsync", StringComparison.Ordinal))

            // WHY: Retain the owned syntax snapshot used for exact positional comparison.
            // ReSharper disable once UseCollectionExpression
            .ToArray();

        if (calls.Length != resources.Length)
        {
            return false;
        }

        // WHY: Each source disposal must correspond to the same ordered owned resource.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            var arguments = call
                .ChildNodes()
                .Last()
                .ChildNodes()

                // WHY: Retain the argument snapshot for exact arity and resource-identity checks.
                // ReSharper disable once UseCollectionExpression
                .ToArray();

            var primary = resources.Length == 2 && index == 1
                ? "originalFailure"
                : "null";

            if (call
                    .ChildNodes()
                    .First()
                    .ToString()
                != "SafeMigrationAnalysisCleanup.DisposeAsync"
                || call.Parent?.IsKind(SyntaxKind.AwaitExpression) != true
                || arguments.Length != 3
                || arguments[0]
                    .ToString()
                != resources[index]
                || arguments[1]
                    .ToString()
                != primary)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Finds the actual source root independently of the test process working directory.</summary>
    /// <returns>The repository containing the benchmark projects.</returns>
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Doka.EntityFrameworkCore.SafeMigrations.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Unable to locate benchmark fixture source.");
    }
}
