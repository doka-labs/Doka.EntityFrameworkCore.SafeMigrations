namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Decorates the active provider's C# migration generator and finalizes only
/// SafeMigrations-owned migration source.
/// </summary>
/// <remarks>
/// Snapshot and metadata generation remain provider-owned so provider-specific
/// namespace discovery and model rendering cannot be bypassed.
/// </remarks>
internal sealed class SafeMigrationCSharpMigrationsGenerator : IMigrationsCodeGenerator
{
    private const string SafeMigrationsNamespace = "Doka.EntityFrameworkCore.SafeMigrations";

    private readonly ICSharpHelper _csharpHelper;
    private readonly SafeMigrationScaffoldingConfiguration _configuration;
    private readonly IMigrationsCodeGenerator _providerGenerator;

    /// <summary>Initializes the SafeMigrations migration-file generator.</summary>
    /// <param name="providerGenerator">The active provider's migrations-code generator.</param>
    /// <param name="csharpHelper">The EF Core C# identifier formatter.</param>
    /// <param name="configuration">The immutable scaffolding configuration.</param>
    public SafeMigrationCSharpMigrationsGenerator(
        IMigrationsCodeGenerator providerGenerator,
        ICSharpHelper csharpHelper,
        SafeMigrationScaffoldingConfiguration configuration
    )
    {
        ArgumentNullException.ThrowIfNull(providerGenerator);
        ArgumentNullException.ThrowIfNull(csharpHelper);
        ArgumentNullException.ThrowIfNull(configuration);

        _providerGenerator = providerGenerator;
        _csharpHelper = csharpHelper;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public string FileExtension => _providerGenerator.FileExtension;

    /// <inheritdoc />
    public string? Language => _providerGenerator.Language;

    /// <inheritdoc />
    public string GenerateMigration(
        string? migrationNamespace,
        string migrationName,
        IReadOnlyList<MigrationOperation> upOperations,
        IReadOnlyList<MigrationOperation> downOperations
    )
    {
        var effectiveUpOperations = upOperations;
        IReadOnlyList<MigrationOperation> effectiveDownOperations;
        if (!_configuration.IsEnabled)
        {
            effectiveDownOperations = downOperations;
        }
        else
        {
            // EF's inverse operations are the only authoritative source for
            // pre-change model-managed values. Pair both directions before a
            // legacy rollback is replaced so Up never loses that evidence.
            effectiveUpOperations = SafeMigrationModelManagedDataPairer.Pair(upOperations, downOperations);
            var pairedDownOperations = SafeMigrationModelManagedDataPairer.Pair(downOperations, upOperations);

            if (_configuration.Mode != SafeMigrationScaffoldingMode.LegacyConvergence)
            {
                effectiveDownOperations = pairedDownOperations;
            }
            else
            {
                // A convergence baseline may adopt objects that existed before
                // the migration. Replacing Down with a deterministic rejection
                // prevents a rollback from deleting data or schema the migration
                // did not create.
                effectiveDownOperations = [new SafeMigrationLegacyRollbackOperation()];
            }
        }

        var source = _providerGenerator.GenerateMigration(
            migrationNamespace,
            migrationName,
            effectiveUpOperations,
            effectiveDownOperations);

        if (_configuration.IsEnabled)
        {
            source = EnsureSafeMigrationsUsingDirective(source);
        }

        return _configuration.IsEnabled && migrationNamespace is not null
            ? UseFileScopedNamespace(
                source,
                _csharpHelper.Namespace(migrationNamespace))
            : source;
    }

    /// <inheritdoc />
    public string GenerateMetadata(
        string? migrationNamespace,
        Type contextType,
        string migrationName,
        string migrationId,
        IModel targetModel
    ) => _providerGenerator.GenerateMetadata(
        migrationNamespace,
        contextType,
        migrationName,
        migrationId,
        targetModel);

    /// <inheritdoc />
    public string GenerateSnapshot(
        string? modelSnapshotNamespace,
        Type contextType,
        string modelSnapshotName,
        IModel model
    ) => _providerGenerator.GenerateSnapshot(
        modelSnapshotNamespace,
        contextType,
        modelSnapshotName,
        model);

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

        var newline = SafeMigrationGeneratedSource.GetConsistentNewLine(source);
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
        var newline = SafeMigrationGeneratedSource.GetConsistentNewLine(source);
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
            .Append(';')
            .Append(newline)
            .Append(newline);

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
