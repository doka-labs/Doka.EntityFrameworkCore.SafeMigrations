namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationCSharpMigrationOperationGenerator
{
    private void AppendStringArray(
        IndentedStringBuilder builder,
        IReadOnlyList<string> values
    )
    {
        builder.Append('[');
        // WHY: The first member determines separator placement in the generated ordered array.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < values.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            builder.Append(Dependencies.CSharpHelper.Literal(values[index]));
        }

        builder.Append(']');
    }

    private string AppendLegacyConvergencePolicy(
        string source
    )
    {
        const string method = ".CreateTable(";

        var methodIndex = source.IndexOf(method, StringComparison.Ordinal);
        if (methodIndex < 0
            || source.IndexOf(method, methodIndex + method.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected CreateTable shape. "
                + "SafeMigrations stopped instead of generating an ambiguous policy argument.");
        }

        var openParenthesis = methodIndex + method.Length - 1;
        var closeParenthesis = FindMatchingParenthesis(source, openParenthesis);
        var argumentIndent = FindArgumentIndent(source, openParenthesis, closeParenthesis);
        var newline = SafeMigrationGeneratedSource.GetConsistentNewLine(source);
        var policy = _configuration.LegacyConvergencePolicy switch
        {
            SafeMigrationPolicy.ThrowIfDifferent => nameof(SafeMigrationPolicy.ThrowIfDifferent),
            SafeMigrationPolicy.RepairIfSafe => nameof(SafeMigrationPolicy.RepairIfSafe),
            _ => throw new InvalidOperationException(
                "The legacy-convergence policy is invalid. Select ThrowIfDifferent or RepairIfSafe "
                + "through UseLegacyConvergencePolicy before generating migrations."),
        };

        // Freeze the policy into generated source. Runtime option changes must
        // never reinterpret a migration that was already reviewed.
        return source.Insert(
            closeParenthesis,
            string.Concat(
                ",",
                newline,
                argumentIndent,
                "policy: global::Doka.EntityFrameworkCore.SafeMigrations.SafeMigrationPolicy.",
                policy));
    }

    private static int FindMatchingParenthesis(
        string source,
        int openParenthesis
    )
    {
        var depth = 1;
        var inString = false;
        var inCharacter = false;
        var verbatimString = false;
        var escaped = false;

        // WHY: Track source offsets and quoted-token widths while finding the matching parenthesis.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = openParenthesis + 1; index < source.Length; index++)
        {
            var current = source[index];
            if (inString)
            {
                if (verbatimString)
                {
                    if (current != '"')
                    {
                        continue;
                    }

                    if (index + 1 < source.Length
                        && source[index + 1] == '"')
                    {
                        index++;
                        continue;
                    }

                    inString = false;
                    continue;
                }

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (inCharacter)
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                }
                else if (current == '\'')
                {
                    inCharacter = false;
                }

                continue;
            }

            if (current == '"')
            {
                inString = true;
                verbatimString = index > 0 && source[index - 1] == '@';
                continue;
            }

            if (current == '\'')
            {
                inCharacter = true;
                continue;
            }

            if (current == '(')
            {
                depth++;
            }
            else if (current == ')'
                     && --depth == 0)
            {
                return index;
            }
        }

        throw new InvalidOperationException(
            "The EF Core C# operation generator emitted an unterminated CreateTable call. "
            + "SafeMigrations stopped instead of generating an ambiguous policy argument.");
    }

    private static string FindArgumentIndent(
        string source,
        int openParenthesis,
        int closeParenthesis
    )
    {
        var index = openParenthesis + 1;
        if (index >= closeParenthesis)
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected single-line CreateTable call.");
        }

        if (source[index] == '\r'
            && index + 1 < closeParenthesis
            && source[index + 1] == '\n')
        {
            index += 2;
        }
        else if (source[index] == '\n')
        {
            index++;
        }
        else
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected single-line CreateTable call.");
        }

        var indentStart = index;
        while (index < closeParenthesis
               && source[index] is ' ' or '\t')
        {
            index++;
        }

        if (index == indentStart
            || index >= closeParenthesis)
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected CreateTable argument layout.");
        }

        return source[indentStart..index];
    }

    private void AppendOptionalSchemaAndClose(
        IndentedStringBuilder builder,
        string? schema
    )
    {
        if (schema is not null)
        {
            builder
                .AppendLine(",")
                .Append("schema: ")
                .Append(Dependencies.CSharpHelper.Literal(schema));
        }

        builder
            .DecrementIndent()
            .Append(')');
    }

    private void GenerateWithReplacement(
        EnsureSchemaOperation operation,
        IndentedStringBuilder builder,
        string expected,
        string replacement
    )
    {
        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);
        AppendReplaced(builder, baseline.ToString(), expected, replacement);
    }

    private void GenerateWithReplacement(
        DropSchemaOperation operation,
        IndentedStringBuilder builder,
        string expected,
        string replacement
    )
    {
        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);
        AppendReplaced(builder, baseline.ToString(), expected, replacement);
    }

    private void GenerateWithReplacement(
        DropTableOperation operation,
        IndentedStringBuilder builder,
        string expected,
        string replacement
    )
    {
        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);
        AppendReplaced(builder, baseline.ToString(), expected, replacement);
    }

    private void GenerateWithReplacement(
        DropIndexOperation operation,
        IndentedStringBuilder builder,
        string expected,
        string replacement
    )
    {
        var baseline = CreateScratchBuilder(builder);
        base.Generate(operation, baseline);
        AppendReplaced(builder, baseline.ToString(), expected, replacement);
    }

    private static void AppendReplaced(
        IndentedStringBuilder builder,
        string baseline,
        string expected,
        string replacement
    )
    {
        var methodIndex = baseline.IndexOf(expected, StringComparison.Ordinal);
        if (methodIndex < 0
            || baseline
                .AsSpan(0, methodIndex)
                .ContainsAnyExcept(' ', '\t')
            || baseline.IndexOf(expected, methodIndex + expected.Length, StringComparison.Ordinal) >= 0)
        {
            throw new InvalidOperationException(
                $"The EF Core C# operation generator emitted an unexpected '{expected}' shape. "
                + "SafeMigrations stopped instead of generating ambiguous migration code.");
        }

        builder
            .Append(replacement)
            .Append(baseline[(methodIndex + expected.Length)..]);
    }

    private string ReplaceCompositePrincipalColumnArrays(
        CreateTableOperation operation,
        string source
    )
    {
        var literals = operation
            .ForeignKeys
            .Where(static foreignKey => foreignKey.PrincipalColumns?.Length > 1)
            .Select(foreignKey => Dependencies.CSharpHelper.Literal(foreignKey.PrincipalColumns!))
            .GroupBy(static literal => literal, StringComparer.Ordinal);

        foreach (var group in literals)
        {
            source = ReplaceArrayLiteral(source, group.Key, group.Count());
        }

        return source;
    }

    private static string ReplaceArrayLiteral(
        string source,
        string literal,
        int expectedOccurrences
    )
    {
        const string prefix = "new[] { ";
        const string suffix = " }";

        if (!literal.StartsWith(prefix, StringComparison.Ordinal)
            || !literal.EndsWith(suffix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The EF Core C# helper emitted an unexpected one-dimensional array literal shape.");
        }

        var occurrenceCount = 0;
        var searchIndex = 0;
        while ((searchIndex = source.IndexOf(literal, searchIndex, StringComparison.Ordinal)) >= 0)
        {
            occurrenceCount++;
            searchIndex += literal.Length;
        }

        if (occurrenceCount != expectedOccurrences)
        {
            throw new InvalidOperationException(
                "The EF Core C# operation generator emitted an unexpected array-literal shape. "
                + "SafeMigrations stopped instead of generating analyzer-incompatible migration code.");
        }

        var collectionExpression = string.Concat(
            "[",
            literal.AsSpan(prefix.Length, literal.Length - prefix.Length - suffix.Length),
            "]");

        return source.Replace(literal, collectionExpression, StringComparison.Ordinal);
    }

    private static IndentedStringBuilder CreateScratchBuilder(
        IndentedStringBuilder source
    )
    {
        var scratch = new IndentedStringBuilder();
        // WHY: Reproduce the exact indentation depth without allocating an enumerable of levels.
        // ReSharper disable once ForCanBeConvertedToForeach
        // ReSharper disable once LoopCanBeConvertedToQuery
        for (var index = 0; index < source.IndentCount; index++)
        {
            scratch.IncrementIndent();
        }

        return scratch;
    }
}
