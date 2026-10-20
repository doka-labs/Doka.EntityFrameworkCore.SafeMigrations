namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Checks emitted C# by compiling and executing it, rather than matching its spelling.</summary>
    /// <param name="inline">Whether the CHECK is nested in fresh table creation.</param>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedCheckSourceRoundTripPreservesStructuralPredicate(
        bool inline
    )
    {
        var check = new AddCheckConstraintOperation
        {
            Name = "ck_users_id",
            Table = "users",
            Sql = "`id` >= 0",
        };

        var table = CreateTable();
        table.CheckConstraints.Add(check);
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.Strict);
        var source = new IndentedStringBuilder();
        generator.Generate("migrationBuilder", [inline ? table : check], source);

        var operations = CompileAndExecuteGeneratedOperations(source.ToString());
        var operation = Assert.IsType<SafeMigrationOperation>(Assert.Single(operations));
        var definition = inline
            ? Assert.Single(Assert.IsType<EnsureTableIntent>(operation.Intent).Definition.CheckConstraints)
            : Assert.IsType<EnsureCheckConstraintIntent>(operation.Intent).Definition;

        Assert.NotNull(definition.Expression);
        Assert.Null(definition.Sql);
        Assert.True(SafeMigrationSqlExpressionInspector.IsStructurallyComparable(definition.Expression));
    }

    /// <summary>Uses existing EF design-time compiler dependencies to validate generated source in memory.</summary>
    private static IReadOnlyList<MigrationOperation> CompileAndExecuteGeneratedOperations(
        string operations
    )
    {
        var source = "using Doka.EntityFrameworkCore.SafeMigrations;\n"
            + "using Microsoft.EntityFrameworkCore.Migrations;\n"
            + "public static class GeneratedMigrationProbe {\n"
            + "public static System.Collections.Generic.IReadOnlyList<"
            + "Microsoft.EntityFrameworkCore.Migrations.Operations.MigrationOperation> Build() {\n"
            + "var migrationBuilder = new MigrationBuilder(\"probe\");\n"
            + operations
            + "\nreturn migrationBuilder.Operations; }}";

        var platform = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var references = platform
            .Concat([typeof(Migration).Assembly.Location, typeof(SafeMigrationOperation).Assembly.Location])
            .Distinct(StringComparer.Ordinal)
            .Select(static path => Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(path));

        var compilation = Microsoft.CodeAnalysis.CSharp.CSharpCompilation.Create(
            "GeneratedMigrationProbe_" + Guid.NewGuid().ToString("N"),
            [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(source)],
            references,
            new Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions(
                Microsoft.CodeAnalysis.OutputKind.DynamicallyLinkedLibrary));

        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
        image.Position = 0;
        var context = new System.Runtime.Loader.AssemblyLoadContext("generated-check-probe", isCollectible: true);

        try
        {
            var assembly = context.LoadFromStream(image);
            var method = assembly.GetType("GeneratedMigrationProbe", throwOnError: true)!.GetMethod("Build")!;

            return (IReadOnlyList<MigrationOperation>)method.Invoke(null, null)!;
        }
        finally
        {
            // WHY: Each generated-source test owns its assembly; collectible
            // loading keeps repeated IDE runs from retaining every probe image.
            context.Unload();
        }
    }
}
