namespace Doka.EntityFrameworkCore.SafeMigrations.Tests;

public sealed partial class SafeMigrationScaffoldingTests
{
    /// <summary>Preserves supported newlines when transforming the migration namespace.</summary>
    /// <param name="newline">The newline convention preserved by source generation.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void MigrationGenerationUsesAnalyzerCompatibleFileScopedNamespace(
        string newline
    )
    {
        // Arrange
        var blockScopedSource = string.Concat(
            "#nullable disable",
            newline,
            newline,
            "namespace Company.Product.Migrations",
            newline,
            "{",
            newline,
            "    public partial class CreateUsers : Migration",
            newline,
            "    {",
            newline,
            "    }",
            newline,
            "}",
            newline);

        // Act
        var source = SafeMigrationCSharpMigrationsGenerator.UseFileScopedNamespace(
            blockScopedSource,
            "Company.Product.Migrations");
        var actualNewline = SafeMigrationGeneratedSource.GetConsistentNewLine(source);

        // Assert
        Assert.Contains("namespace Company.Product.Migrations;", source, StringComparison.Ordinal);
        Assert.DoesNotContain(
            $"namespace Company.Product.Migrations{newline}{{",
            source,
            StringComparison.Ordinal);
        Assert.Contains("public partial class CreateUsers : Migration", source, StringComparison.Ordinal);
        Assert.Equal(newline, actualNewline);
    }

    /// <summary>Adds one safe namespace import and keeps repeated transformation idempotent.</summary>
    /// <param name="newline">The newline convention preserved by source generation.</param>
    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void MigrationGenerationAddsMissingSafeMigrationsNamespaceImportExactlyOnce(
        string newline
    )
    {
        // Arrange
        var source = string.Concat(
            "using Doka.EntityFrameworkCore.MySql;",
            newline,
            "using Microsoft.EntityFrameworkCore.Migrations;",
            newline,
            newline,
            "#nullable disable",
            newline);

        // Act
        var withImport = SafeMigrationCSharpMigrationsGenerator
            .EnsureSafeMigrationsUsingDirective(source);

        var repeated = SafeMigrationCSharpMigrationsGenerator
            .EnsureSafeMigrationsUsingDirective(withImport);
        var actualNewline = SafeMigrationGeneratedSource.GetConsistentNewLine(repeated);

        // Assert
        Assert.Contains(
            string.Concat(
                "using Doka.EntityFrameworkCore.MySql;",
                newline,
                "using Doka.EntityFrameworkCore.SafeMigrations;",
                newline,
                "using Microsoft.EntityFrameworkCore.Migrations;"),
            withImport,
            StringComparison.Ordinal);
        Assert.Equal(withImport, repeated);

        const string directive = "using Doka.EntityFrameworkCore.SafeMigrations;";
        var directiveIndex = repeated.IndexOf(directive, StringComparison.Ordinal);

        Assert.True(directiveIndex >= 0);
        Assert.DoesNotContain(
            directive,
            repeated[(directiveIndex + directive.Length)..],
            StringComparison.Ordinal);
        Assert.Equal(newline, actualNewline);
    }

    /// <summary>Rejects unsupported newline shapes without guessing a source convention.</summary>
    /// <param name="source">The source text with the rejected newline shape.</param>
    [Theory]
    [InlineData("first\r\nsecond\n")]
    [InlineData("first\rsecond")]
    public void MigrationGenerationRejectsInconsistentLineEndings(
        string source
    )
    {
        // Arrange: This contract supplies its fixed rejection input.

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationGeneratedSource.GetConsistentNewLine(source));

        // Assert
        Assert.Contains("inconsistent line endings", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects source with no EF namespace anchor for safe import insertion.</summary>
    [Fact]
    public void MigrationGenerationRejectsSourceWithoutEfCoreNamespaceAnchor()
    {
        // Arrange: This contract supplies its fixed rejection input.

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationCSharpMigrationsGenerator.EnsureSafeMigrationsUsingDirective(
                "#nullable disable"));

        // Assert
        Assert.Contains("namespace directive", exception.Message, StringComparison.Ordinal);
        Assert.Contains("unresolved extension method", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects duplicate safe namespace directives instead of silently rewriting them.</summary>
    [Fact]
    public void MigrationGenerationRejectsDuplicateSafeMigrationsNamespaceDirective()
    {
        // Arrange
        var newline = Environment.NewLine;
        var source = string.Join(
            newline,
            "using Doka.EntityFrameworkCore.SafeMigrations;",
            "using Doka.EntityFrameworkCore.SafeMigrations;",
            "using Microsoft.EntityFrameworkCore.Migrations;",
            string.Empty);

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationCSharpMigrationsGenerator.EnsureSafeMigrationsUsingDirective(source));

        // Assert
        Assert.Contains(
            "more than one SafeMigrations namespace directive",
            exception.Message,
            StringComparison.Ordinal);
    }

    /// <summary>Distinguishes quoted namespace-directive text from an actual import.</summary>
    [Fact]
    public void MigrationGenerationDoesNotMistakeLiteralTextForUsingDirective()
    {
        // Arrange
        var newline = Environment.NewLine;
        var source = string.Join(
            newline,
            "using Microsoft.EntityFrameworkCore.Migrations;",
            string.Empty,
            "var text = \"using Doka.EntityFrameworkCore.SafeMigrations;\";",
            string.Empty);

        // Act
        var result = SafeMigrationCSharpMigrationsGenerator
            .EnsureSafeMigrationsUsingDirective(source);

        // Assert
        Assert.StartsWith(
            string.Concat(
                "using Doka.EntityFrameworkCore.SafeMigrations;",
                newline,
                "using Microsoft.EntityFrameworkCore.Migrations;"),
            result,
            StringComparison.Ordinal);
    }

    /// <summary>Rejects source with a namespace different from the requested migration namespace.</summary>
    [Fact]
    public void UnexpectedMigrationNamespaceShapeIsRejected()
    {
        // Arrange: This contract supplies its fixed rejection input.

        // Act
        var exception = Assert.Throws<InvalidOperationException>(() =>
            SafeMigrationCSharpMigrationsGenerator.UseFileScopedNamespace(
                "namespace Unexpected;",
                "Company.Product.Migrations"));

        // Assert
        Assert.Contains("unexpected namespace shape", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Emits an explicit rejection for an unsafe legacy-convergence rollback.</summary>
    [Fact]
    public void LegacyRollbackGenerationFailsClosedInGeneratedSource()
    {
        // Arrange
        var generator = CreateOperationGenerator(SafeMigrationScaffoldingMode.LegacyConvergence);
        var builder = new IndentedStringBuilder();

        // Act
        generator.Generate("migrationBuilder", [new SafeMigrationLegacyRollbackOperation()], builder);

        // Assert
        Assert.StartsWith(
            "throw new global::System.NotSupportedException(",
            builder.ToString(),
            StringComparison.Ordinal);
        Assert.Contains("cannot be rolled back safely", builder.ToString(), StringComparison.Ordinal);
    }
}
