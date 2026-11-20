namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

/// <summary>Proves that the actual isolated-consumer metadata guard detects emitted annotation leaks.</summary>
public sealed class PackageAnnotationMetadataTests
{
    /// <summary>Reads clean and deliberately contaminated images without loading their types.</summary>
    /// <param name="kind">The independently emitted metadata fixture.</param>
    /// <param name="expectedMessage">The exact rejection, or null for a clean assembly.</param>
    [Theory]
    [InlineData("clean", null)]
    [InlineData("reference", "A build-only annotation assembly reference was emitted.")]
    [InlineData("local_attribute", "A build-only annotation attribute was emitted.")]
    [InlineData("external_attribute", "A build-only annotation attribute was emitted.")]
    public void VerifyNoBuildOnlyAnnotations_DetectsActualMetadata(
        string kind,
        string? expectedMessage
    )
    {
        // Arrange
        using var fixture = new MetadataFixture();
        var assemblyPath = fixture.Create(kind);

        // Act
        var failure = Record.Exception(() =>
            GeneratedExpressionConsumerContract.VerifyNoBuildOnlyAnnotations(assemblyPath));

        // Assert
        if (expectedMessage is null)
        {
            Assert.Null(failure);
        }
        else
        {
            var rejected = Assert.IsType<InvalidOperationException>(failure);
            Assert.Equal(expectedMessage, rejected.Message);
        }
    }

    /// <summary>Invalid PE data cannot acquire a false clean-metadata verdict.</summary>
    [Fact]
    public void VerifyNoBuildOnlyAnnotations_InvalidImageIsRejected()
    {
        // Arrange
        using var fixture = new MetadataFixture();
        var assemblyPath = Path.Combine(fixture.DirectoryPath, "invalid.dll");
        File.WriteAllText(assemblyPath, "not a portable executable");

        // Act
        var failure = Record.Exception(() =>
            GeneratedExpressionConsumerContract.VerifyNoBuildOnlyAnnotations(assemblyPath));

        // Assert
        Assert.IsType<BadImageFormatException>(failure);
    }

    /// <summary>Owns only emitted files; no assembly is loaded or left pending during deletion.</summary>
    private sealed class MetadataFixture : IDisposable
    {
        /// <summary>Allocates a test-owned temporary directory.</summary>
        public MetadataFixture()
        {
            DirectoryPath = Path.Combine(
                Path.GetTempPath(),
                "safe-migrations-metadata-"
                + Guid
                    .NewGuid()
                    .ToString("N"));

            Directory.CreateDirectory(DirectoryPath);
        }

        /// <summary>Gets the exact directory owned by this test invocation.</summary>
        public string DirectoryPath { get; }

        /// <inheritdoc />
        public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);

        /// <summary>Emits one selected PE shape through the existing Roslyn compiler dependency.</summary>
        /// <param name="kind">The clean or deliberately contaminated image shape.</param>
        /// <returns>The image path, never a loaded Assembly.</returns>
        public string Create(
            string kind
        )
        {
            if (kind is not ("clean" or "reference" or "local_attribute" or "external_attribute"))
            {
                throw new ArgumentOutOfRangeException(nameof(kind));
            }

            MetadataReference[] references = [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)];
            var source = "public sealed class Subject { }";
            if (kind is "reference" or "external_attribute")
            {
                var dependencyName = kind == "reference"
                    ? "JetBrains.Annotations"
                    : "Fixture.Annotations";

                var dependencyPath = Compile(
                    dependencyName,
                    """
                    namespace JetBrains.Annotations
                    {
                        public sealed class Probe { }
                        public sealed class UsedImplicitlyAttribute : System.Attribute { }
                    }
                    """,
                    references);

                // WHY: This tiny owned image must not leave a compiler file mapping across fixture cleanup.
                references = [references[0], MetadataReference.CreateFromImage(File.ReadAllBytes(dependencyPath))];
                source = kind == "reference"
                    ? "public sealed class Subject { public JetBrains.Annotations.Probe Value = new(); }"
                    : "[JetBrains.Annotations.UsedImplicitly] public sealed class Subject { }";
            }
            else if (kind == "local_attribute")
            {
                source = """
                namespace JetBrains.Annotations
                {
                    public sealed class UsedImplicitlyAttribute : System.Attribute { }
                }
                [JetBrains.Annotations.UsedImplicitly] public sealed class Subject { }
                """;
            }

            return Compile("MetadataProbe", source, references);
        }

        /// <summary>Emits a deterministic small library without executing its code.</summary>
        /// <param name="name">The assembly identity and file stem.</param>
        /// <param name="source">The source for the selected shape.</param>
        /// <param name="references">The exact compile-only dependency references.</param>
        /// <returns>The owned PE file path.</returns>
        private string Compile(
            string name,
            string source,
            IReadOnlyList<MetadataReference> references
        )
        {
            var path = Path.Combine(DirectoryPath, name + ".dll");
            var compilation = CSharpCompilation.Create(
                name,
                [CSharpSyntaxTree.ParseText(source)],
                references,
                new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));

            var result = compilation.Emit(path);
            if (!result.Success)
            {
                throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
            }

            return path;
        }
    }
}
