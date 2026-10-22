namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Compiles generated consumer sources against the exact assemblies restored by the test project.</summary>
internal static class MySqlGeneratedMigrationCompiler
{
    /// <summary>
    /// Loads a uniquely named generated migration assembly without referencing local provider sources.
    /// </summary>
    /// <param name="sources">Complete migration, metadata or snapshot compilation units.</param>
    /// <returns>The generated assembly containing the consumer's executable migration.</returns>
    internal static Assembly Compile(params string[] sources)
    {
        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "GeneratedMySqlMigration_" + Guid.NewGuid().ToString("N"),
            sources.Select(static source => Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)),
            CreateReferences(),
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var output = new MemoryStream();
        var emitted = compilation.Emit(output);
        if (!emitted.Success)
        {
            throw new InvalidOperationException("Generated MySQL migration did not compile: "
                + string.Join(Environment.NewLine, emitted.Diagnostics));
        }

        return Assembly.Load(output.ToArray());
    }

    /// <summary>Preserves the same published runtime references in Rider and command-line test hosts.</summary>
    private static IEnumerable<Microsoft.CodeAnalysis.MetadataReference> CreateReferences()
    {
        var platformPaths = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator);

        // WHY: Hosts differ in their platform assembly set; generated metadata also
        // names the public test context, so explicitly retain every consumer assembly.
        var paths = platformPaths.Concat(
        [
            typeof(MigrationBuilder).Assembly.Location,
            typeof(DbContext).Assembly.Location,
            typeof(IInfrastructure<>).Assembly.Location,
            typeof(SafeMigrationOperation).Assembly.Location,
            typeof(DokaMySqlGuidFormat).Assembly.Location,
            typeof(MySqlGeneratedMigrationCompiler).Assembly.Location,
        ]);

        return paths.Distinct(StringComparer.Ordinal)
            .Select(static path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));
    }
}
