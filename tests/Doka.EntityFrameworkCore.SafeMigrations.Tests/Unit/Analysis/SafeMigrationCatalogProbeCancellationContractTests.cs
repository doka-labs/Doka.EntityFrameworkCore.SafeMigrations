namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Qualifies the internal catalog-probe cancellation declaration and actual consumer binding.</summary>
public sealed class SafeMigrationCatalogProbeCancellationContractTests
{
    /// <summary>The probe contract has one explicit final token and no optional parameter escape.</summary>
    [Fact]
    public void ReadAsync_RequiresOneExplicitFinalCancellationToken()
    {
        // Arrange
        var owner = typeof(SafeMigrationCatalogProbeBatch);

        // Act
        // WHY: Assertions inspect one captured declaration inventory without re-enumerating reflection results.
        // ReSharper disable once UseCollectionExpression
        var declarations = owner
            .GetMethods()
            .Where(static method => method.Name == nameof(SafeMigrationCatalogProbeBatch.ReadAsync))
            .Select(static method => method.GetParameters())
            .ToArray();

        // Assert
        var parameters = Assert.Single(declarations);
        Assert.Equal(9, parameters.Length);
        Assert.Equal(typeof(DbTransaction), parameters[^3].ParameterType);
        Assert.Equal(typeof(int), parameters[^2].ParameterType);
        Assert.Equal(typeof(CancellationToken), parameters[^1].ParameterType);
        Assert.Equal("cancellationToken", parameters[^1].Name);
        Assert.Same(parameters[^1],
            Assert.Single(parameters,
                static parameter =>
                    parameter.ParameterType == typeof(CancellationToken)));

        Assert.All(parameters,
            static parameter =>
            {
                Assert.False(parameter.IsOptional);
                Assert.False(parameter.HasDefaultValue);
            });
    }

    /// <summary>Real consumer compilation accepts the final token and rejects its omission.</summary>
    /// <param name="includeToken">Whether the authored call explicitly passes its operation token.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReadAsync_ConsumerRequiresTheFinalOperationToken(
        bool includeToken
    )
    {
        // Arrange
        var compilation = CompileProbeInvocation(includeToken);

        // Act
        // WHY: Assertions inspect one diagnostic snapshot rather than changing materialization semantics.
        // ReSharper disable once UseCollectionExpression
        var errors = compilation
            .GetDiagnostics()
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .ToArray();

        // Assert
        if (includeToken)
        {
            Assert.Empty(errors);
        }
        else
        {
            var error = Assert.Single(errors);
            Assert.Equal("CS7036", error.Id);
            Assert.Contains(
                "cancellationToken",
                error.GetMessage(CultureInfo.InvariantCulture),
                StringComparison.Ordinal);
        }
    }

    /// <summary>Compiles the actual internal probe boundary under its authorized test friend identity.</summary>
    /// <param name="includeToken">Whether the invocation includes its required final operation token.</param>
    /// <returns>A consumer compilation against the current product assembly, without executing database work.</returns>
    private static CSharpCompilation CompileProbeInvocation(
        bool includeToken
    )
    {
        var tokenArgument = includeToken
            ? ", cancellationToken"
            : string.Empty;

        var source = $$"""
            using System.Data.Common;
            using System.Threading;
            using System.Threading.Tasks;
            using Doka.EntityFrameworkCore.SafeMigrations;

            public static class ProbeInvocationFixture
            {
                public static Task ReadAsync(DbConnection connection, CancellationToken cancellationToken) =>
                    SafeMigrationCatalogProbeBatch.ReadAsync(connection, 0, 1024, null,
                        static (_, _) => new SafeMigrationCatalogProbeStatement(1, 0, 0),
                        static (_, _, _, _) => Task.CompletedTask,
                        null, 16000{{tokenArgument}});
            }
            """;

        var platform = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string
            ?? throw new InvalidOperationException("The compiler fixture requires the runtime's platform assemblies.");

        var references = platform
            .Split(Path.PathSeparator)
            .Where(static path => !StringComparer.Ordinal.Equals(
                path,
                typeof(SafeMigrationCatalogProbeCancellationContractTests).Assembly.Location))
            .Append(typeof(SafeMigrationCatalogProbeBatch).Assembly.Location)
            .Distinct(StringComparer.Ordinal)
            .Select(static path => MetadataReference.CreateFromFile(path));

        return CSharpCompilation.Create(
            typeof(SafeMigrationCatalogProbeCancellationContractTests).Assembly.GetName()
                .Name!,
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
