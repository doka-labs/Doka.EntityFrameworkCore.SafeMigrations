namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Pins iteration callback signatures to the native BenchmarkDotNet action contract.</summary>
public sealed class BenchmarkIterationHookContractTests
{
    /// <summary>Checks actual sources, including fixtures deliberately excluded from default benchmark runs.</summary>
    [Fact]
    public void EveryActualIterationHookIsSynchronousVoid()
    {
        // Arrange
        var benchmarkRoot = Path.Combine(RepositoryRoot(), "benchmarks");
        var projects = Directory
            .GetDirectories(benchmarkRoot, "Doka.EntityFrameworkCore.SafeMigrations.*")
            .Where(path => path.EndsWith(".Benchmarks", StringComparison.Ordinal))
            .ToArray();

        var sources = projects
            .SelectMany(project => Directory
                .EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
                .Where(path => !Path
                    .GetRelativePath(project, path)
                    .Split(Path.DirectorySeparatorChar)
                    .Any(static segment => segment is "bin" or "obj" or "artifacts" or "BenchmarkDotNet.Artifacts")))
            .Select(File.ReadAllText)
            .ToArray();

        // Act
        var hooks = sources
            .SelectMany(IterationHooks)
            .ToArray();

        var invalid = hooks
            .Where(static method => !IsSynchronousVoid(method))
            .Select(static method => method.Identifier.ValueText)
            .ToArray();

        // Assert
        Assert.Equal(5, projects.Length);
        Assert.NotEmpty(hooks);
        Assert.Empty(invalid);
        Assert.Contains(hooks,
            method => method.Identifier.ValueText == "ValidateReport"
                && method.Parent is ClassDeclarationSyntax
                {
                    Identifier.ValueText: "SqlServerLiveCatalogBenchmarks",
                });
    }

    /// <summary>Rejects asynchronous iteration callbacks while retaining asynchronous global hooks.</summary>
    /// <param name="attribute">The real native callback attribute spelling.</param>
    /// <param name="declaration">The complete method declaration used by the adversarial fixture.</param>
    /// <param name="expected">Whether its callback signature satisfies the iteration contract.</param>
    [Theory]
    [InlineData("IterationSetup", "public void Hook() { }", true)]
    [InlineData("IterationCleanup", "public void Hook() { }", true)]
    [InlineData("BenchmarkDotNet.Attributes.IterationCleanupAttribute", "public void Hook() { }", true)]
    [InlineData("IterationSetup", "public Task Hook() => Task.CompletedTask;", false)]
    [InlineData("IterationCleanup", "public Task Hook() => Task.CompletedTask;", false)]
    [InlineData("IterationCleanup", "public ValueTask Hook() => ValueTask.CompletedTask;", false)]
    [InlineData("IterationCleanup", "public async void Hook() { await Task.Yield(); }", false)]
    [InlineData("GlobalSetup", "public Task Hook() => Task.CompletedTask;", true)]
    [InlineData("GlobalCleanup", "public Task Hook() => Task.CompletedTask;", true)]
    public void CallbackContractDistinguishesIterationActionsFromGlobalAwaitables(
        string attribute,
        string declaration,
        bool expected
    )
    {
        // Arrange
        var source = $"class Fixture {{ [{attribute}] {declaration} }}";

        // Act
        var hooks = IterationHooks(source)
            .ToArray();

        var accepted = hooks.All(IsSynchronousVoid);

        // Assert
        Assert.Equal(attribute.StartsWith("Global", StringComparison.Ordinal)
                ? 0
                : 1,
            hooks.Length);

        Assert.Equal(expected, accepted);
    }

    /// <summary>Reads actual iteration attributes rather than relying on default program registration.</summary>
    /// <param name="source">The actual benchmark source or a targeted adversarial control.</param>
    /// <returns>Every method carrying an iteration setup or cleanup attribute.</returns>
    private static IEnumerable<MethodDeclarationSyntax> IterationHooks(
        string source
    ) => CSharpSyntaxTree
        .ParseText(source)
        .GetRoot()
        .DescendantNodes()
        .OfType<MethodDeclarationSyntax>()
        .Where(static method => method
            .AttributeLists
            .SelectMany(static list => list.Attributes)
            .Any(static attribute => attribute
                    .Name
                    .ToString()
                    .Split('.')
                    .Last() is "IterationSetup"
                or "IterationSetupAttribute"
                or "IterationCleanup"
                or "IterationCleanupAttribute"));

    /// <summary>Matches the void action signature emitted by the pinned benchmark host.</summary>
    /// <param name="method">The actual attributed callback.</param>
    /// <returns>Whether the callback completes synchronously through the native action delegate.</returns>
    private static bool IsSynchronousVoid(
        MethodDeclarationSyntax method
    ) => method.ReturnType is PredefinedTypeSyntax type
        && type.Keyword.IsKind(SyntaxKind.VoidKeyword)
        && !method.Modifiers.Any(SyntaxKind.AsyncKeyword);

    /// <summary>Finds benchmark sources independently of the test process working directory.</summary>
    /// <returns>The repository containing this test assembly's source.</returns>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
            directory is not null;
            directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Doka.EntityFrameworkCore.SafeMigrations.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Unable to locate benchmark callback source.");
    }
}
