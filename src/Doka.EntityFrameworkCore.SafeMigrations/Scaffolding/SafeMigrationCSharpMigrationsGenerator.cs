namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Decorates the active provider's C# migration generator and finalizes only
/// SafeMigrations-owned migration source.
/// </summary>
/// <remarks>
/// Snapshot and metadata rendering remain provider-owned. Only the shared migration type name
/// is normalized so EF11's timestamp identity remains unique without a generated underscore prefix.
/// </remarks>
internal sealed class SafeMigrationCSharpMigrationsGenerator : IMigrationsCodeGenerator
{
    private const string SafeMigrationsNamespace = "Doka.EntityFrameworkCore.SafeMigrations";
    private readonly SafeMigrationScaffoldingConfiguration _configuration;

    private readonly ICSharpHelper _csharpHelper;
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
            var guardedUpOperations = ConsumeDesignTimeServicesGuard(upOperations, "Up");
            var guardedDownOperations = ConsumeDesignTimeServicesGuard(downOperations, "Down");

            // EF's inverse operations are the only authoritative source for
            // pre-change model-managed values. Pair both directions before a
            // legacy rollback is replaced so Up never loses that evidence.
            effectiveUpOperations = SafeMigrationModelManagedDataPairer.Pair(
                guardedUpOperations,
                guardedDownOperations);

            var pairedDownOperations = SafeMigrationModelManagedDataPairer.Pair(
                guardedDownOperations,
                guardedUpOperations);

            // WHY: A convergence baseline can adopt pre-existing objects; its rollback must never delete them.
            effectiveDownOperations = _configuration.Mode != SafeMigrationScaffoldingMode.LegacyConvergence
                ? pairedDownOperations
                : [new SafeMigrationLegacyRollbackOperation()];
        }

        var source = _providerGenerator.GenerateMigration(
            migrationNamespace,
            GetMigrationTypeName(migrationName),
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
        GetMigrationTypeName(migrationName),
        migrationId,
        targetModel);

    /// <summary>Retains EF11's timestamp uniqueness while normalizing only its canonical type-name separator.</summary>
    /// <param name="migrationName">The framework-supplied migration name or canonical timestamp-prefixed ID.</param>
    /// <returns>The provider class-name input; the separate migration-history identity is never changed.</returns>
    // WHY: EF11 passes the full ID to both class renderers. Prefixing its timestamp with a word avoids
    // CA1707 without dropping uniqueness, suppressing diagnostics or rewriting provider-generated source.
    private string GetMigrationTypeName(
        string migrationName
    ) => !_configuration.IsEnabled
        || migrationName.Length <= 15
        || migrationName[14] != '_'
        || migrationName.AsSpan(0, 14).ContainsAnyExceptInRange('0', '9')
            ? migrationName
            : string.Concat("Migration", migrationName.AsSpan(0, 14), migrationName.AsSpan(15));

    // WHY: EF11 carries the latest migration identity into the provider-owned snapshot.
    // Keep snapshot generation provider-owned, including its latest-migration annotation.
    /// <inheritdoc />
    public string GenerateSnapshot(
        string? modelSnapshotNamespace,
        Type contextType,
        string modelSnapshotName,
        IModel model,
        string? latestMigrationId = null
    ) => _providerGenerator.GenerateSnapshot(
        modelSnapshotNamespace,
        contextType,
        modelSnapshotName,
        model,
        latestMigrationId);

    private static IReadOnlyList<MigrationOperation> ConsumeDesignTimeServicesGuard(
        IReadOnlyList<MigrationOperation> operations,
        string direction
    )
    {
        if (operations.Count == 0)
        {
            return operations;
        }

        if (operations[0] is not SafeMigrationDesignTimeServicesRequiredOperation)
        {
            throw new InvalidOperationException(
                $"SafeMigrations {direction} operations are missing the design-time services guard. "
                + "The model differ and migrations code generator must be registered together. "
                + "Register the active provider's SafeMigrations design-time services before scaffolding.");
        }

        var result = new MigrationOperation[operations.Count - 1];

        // WHY: Copy only the operation suffix after consuming the single leading design-time guard.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var ordinal = 1; ordinal < operations.Count; ordinal++)
        {
            if (operations[ordinal] is SafeMigrationDesignTimeServicesRequiredOperation)
            {
                throw new InvalidOperationException(
                    $"SafeMigrations {direction} operations contain more than one design-time services guard. "
                    + "Remove duplicate model-differ decoration or design-time service registrations.");
            }

            result[ordinal - 1] = operations[ordinal];
        }

        return result;
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

        var newline = SafeMigrationGeneratedSource.GetConsistentNewLine(source);
        const string directive = $"using {SafeMigrationsNamespace};";
        var directiveIndex = FindExactLineStart(source, directive, newline, startIndex: 0);
        if (directiveIndex >= 0)
        {
            var nextLineStart = directiveIndex + directive.Length + newline.Length;
            if (FindExactLineStart(source, directive, newline, nextLineStart) >= 0)
            {
                throw new InvalidOperationException(
                    "The EF Core C# migrations generator emitted more than one SafeMigrations namespace "
                    + "directive. SafeMigrations stopped instead of preserving ambiguous generated source. "
                    + "Check custom generator replacements and duplicate design-time service registration.");
            }

            return source;
        }

        const string anchor = "using Microsoft.EntityFrameworkCore.Migrations;";
        var anchorIndex = FindExactLineStart(source, anchor, newline, startIndex: 0);
        if (anchorIndex < 0)
        {
            throw new InvalidOperationException(
                "The EF Core C# migrations generator omitted its migrations namespace directive. "
                + "SafeMigrations stopped instead of emitting source with an unresolved extension method. "
                + "Use a compatible provider C# generator and check custom generator replacements.");
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
            var lineEnd = newlineIndex < 0
                ? source.Length
                : newlineIndex;

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
    /// EF11's already file-scoped output is preserved unchanged.
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

        // WHY: EF11 already emits file-scoped namespaces. Preserve its exact body,
        // but do not accept duplicate or mixed declarations as an identity fast path.
        var fileDeclaration = declaration + ";";
        var fileHeaderIndex = FindExactLineStart(source, fileDeclaration, newline, startIndex: 0);
        if (fileHeaderIndex >= 0)
        {
            if (headerIndex >= 0
                || FindExactLineStart(
                    source, fileDeclaration, newline, fileHeaderIndex + fileDeclaration.Length + newline.Length) >= 0)
            {
                throw new InvalidOperationException(
                    "The EF Core C# migrations generator emitted duplicate or mixed namespace declarations.");
            }

            return source;
        }

        if (headerIndex < 0
            || source.IndexOf(blockHeader, headerIndex + blockHeader.Length, StringComparison.Ordinal) >= 0
            || !source.EndsWith(footer, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The EF Core C# migrations generator emitted an unexpected namespace shape. "
                + "SafeMigrations stopped instead of generating analyzer-incompatible migration code. "
                + "Use a compatible provider C# generator and check custom generator replacements.");
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
                        "The EF Core C# migrations generator emitted an unexpected namespace indentation shape. "
                        + "Use a compatible provider C# generator and check custom generator replacements.");
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
