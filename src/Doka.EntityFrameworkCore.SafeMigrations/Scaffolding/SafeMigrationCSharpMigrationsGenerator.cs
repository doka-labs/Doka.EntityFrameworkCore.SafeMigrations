namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Finalizes SafeMigrations C# migration files after EF Core has generated the
/// operation bodies.
/// </summary>
internal sealed class SafeMigrationCSharpMigrationsGenerator : CSharpMigrationsGenerator
{
    private const string SafeMigrationsNamespace = "Doka.EntityFrameworkCore.SafeMigrations";

    private readonly SafeMigrationScaffoldingConfiguration _configuration;

    /// <summary>Initializes the SafeMigrations migration-file generator.</summary>
    /// <param name="dependencies">The shared EF Core migrations-code dependencies.</param>
    /// <param name="csharpDependencies">The EF Core C# generator dependencies.</param>
    /// <param name="configuration">The immutable scaffolding configuration.</param>
    public SafeMigrationCSharpMigrationsGenerator(
        MigrationsCodeGeneratorDependencies dependencies,
        CSharpMigrationsGeneratorDependencies csharpDependencies,
        SafeMigrationScaffoldingConfiguration configuration
    ) : base(dependencies, csharpDependencies)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        _configuration = configuration;
    }

    /// <inheritdoc />
    public override string GenerateMigration(
        string? migrationNamespace,
        string migrationName,
        IReadOnlyList<MigrationOperation> upOperations,
        IReadOnlyList<MigrationOperation> downOperations
    )
    {
        IReadOnlyList<MigrationOperation> effectiveDownOperations;
        if (!_configuration.IsEnabled
            || _configuration.Mode != SafeMigrationScaffoldingMode.LegacyConvergence)
        {
            effectiveDownOperations = downOperations;
        }
        else
        {
            // A convergence baseline may adopt objects that existed before the
            // migration. Replacing Down with a deterministic rejection prevents a
            // rollback from deleting data or schema the migration did not create.
            effectiveDownOperations = [new SafeMigrationLegacyRollbackOperation()];
        }

        var source = base.GenerateMigration(
            migrationNamespace,
            migrationName,
            upOperations,
            effectiveDownOperations);

        if (_configuration.IsEnabled)
        {
            source = EnsureSafeMigrationsUsingDirective(source);
        }

        return _configuration.IsEnabled && migrationNamespace is not null
            ? UseFileScopedNamespace(
                source,
                CSharpDependencies.CSharpHelper.Namespace(migrationNamespace))
            : source;
    }

    /// <inheritdoc />
    protected override IEnumerable<string> GetNamespaces(
        IEnumerable<MigrationOperation> operations
    )
    {
        var namespaces = base.GetNamespaces(operations);

        return _configuration.IsEnabled
            ? namespaces.Append(SafeMigrationsNamespace)
            : namespaces;
    }

    /// <summary>
    /// Ensures that a generated migration can resolve SafeMigrations extension
    /// methods independently of consumer-owned global usings.
    /// </summary>
    /// <param name="source">The complete EF Core-generated migration source.</param>
    /// <returns>The migration source containing exactly one required using directive.</returns>
    /// <exception cref="InvalidOperationException">
    /// The generated source does not contain EF Core's migrations namespace
    /// anchor or contains duplicate SafeMigrations namespace directives.
    /// </exception>
    internal static string EnsureSafeMigrationsUsingDirective(
        string source
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        var newline = Environment.NewLine;
        var directive = $"using {SafeMigrationsNamespace};";
        var directiveIndex = FindExactLineStart(source, directive, newline, startIndex: 0);
        if (directiveIndex >= 0)
        {
            var nextLineStart = directiveIndex + directive.Length + newline.Length;
            if (FindExactLineStart(source, directive, newline, nextLineStart) >= 0)
            {
                throw new InvalidOperationException(
                    "The EF Core C# migrations generator emitted more than one SafeMigrations namespace "
                    + "directive. SafeMigrations stopped instead of preserving ambiguous generated source.");
            }

            return source;
        }

        const string anchor = "using Microsoft.EntityFrameworkCore.Migrations;";
        var anchorIndex = FindExactLineStart(source, anchor, newline, startIndex: 0);
        if (anchorIndex < 0)
        {
            throw new InvalidOperationException(
                "The EF Core C# migrations generator omitted its migrations namespace directive. "
                + "SafeMigrations stopped instead of emitting source with an unresolved extension method.");
        }

        return source.Insert(anchorIndex, string.Concat(directive, newline));
    }

    /// <summary>
    /// Finds an exact generated-source line without treating the same text in
    /// a C# string literal or comment as structural evidence.
    /// </summary>
    private static int FindExactLineStart(
        string source,
        string expectedLine,
        string newline,
        int startIndex
    )
    {
        var lineStart = startIndex;
        while (lineStart <= source.Length)
        {
            var newlineIndex = source.IndexOf(newline, lineStart, StringComparison.Ordinal);
            var lineEnd = newlineIndex < 0 ? source.Length : newlineIndex;
            var line = source.AsSpan(lineStart, lineEnd - lineStart);

            if (line.SequenceEqual(expectedLine))
            {
                return lineStart;
            }

            if (newlineIndex < 0)
            {
                break;
            }

            lineStart = newlineIndex + newline.Length;
        }

        return -1;
    }

    /// <summary>
    /// Converts the one validated block-scoped namespace emitted by EF Core to
    /// a file-scoped namespace without reformatting the generated migration body.
    /// </summary>
    /// <param name="source">The complete EF Core-generated migration source.</param>
    /// <param name="formattedNamespace">The escaped namespace identifier.</param>
    /// <returns>The migration source with a file-scoped namespace.</returns>
    /// <exception cref="InvalidOperationException">
    /// The generated source does not have the single namespace shape or
    /// indentation contract expected from EF Core.
    /// </exception>
    internal static string UseFileScopedNamespace(
        string source,
        string formattedNamespace
    )
    {
        var newline = Environment.NewLine;
        var declaration = $"namespace {formattedNamespace}";
        var blockHeader = string.Concat(declaration, newline, "{", newline);
        var headerIndex = source.IndexOf(blockHeader, StringComparison.Ordinal);
        var footer = string.Concat("}", newline);

        if (headerIndex < 0
            || source.IndexOf(blockHeader, headerIndex + blockHeader.Length, StringComparison.Ordinal) >= 0
            || !source.EndsWith(footer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The EF Core C# migrations generator emitted an unexpected namespace shape. "
                + "SafeMigrations stopped instead of generating analyzer-incompatible migration code.");
        }

        var bodyStart = headerIndex + blockHeader.Length;
        var bodyEnd = source.Length - footer.Length;
        var result = new StringBuilder(source.Length);

        result
            .Append(source.AsSpan(0, headerIndex))
            .Append(declaration)
            .AppendLine(";")
            .AppendLine();

        // EF emits block-scoped namespaces for migration bodies. Removing one
        // validated indentation level keeps the generated source compatible
        // with file-scoped namespace analyzers without reformatting user code.
        var lineStart = bodyStart;
        while (lineStart < bodyEnd)
        {
            var newlineIndex = source.IndexOf(newline, lineStart, StringComparison.Ordinal);
            var lineEnd = newlineIndex < 0 || newlineIndex > bodyEnd
                ? bodyEnd
                : newlineIndex;

            var line = source.AsSpan(lineStart, lineEnd - lineStart);

            if (!line.IsEmpty)
            {
                if (!line.StartsWith("    ", StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "The EF Core C# migrations generator emitted an unexpected namespace indentation shape.");
                }

                result.Append(line[4..]);
            }

            if (lineEnd < bodyEnd)
            {
                result.Append(newline);
            }

            lineStart = lineEnd + newline.Length;
        }

        return result.ToString();
    }
}
