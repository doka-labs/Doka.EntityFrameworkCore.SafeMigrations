namespace Doka.EntityFrameworkCore.SafeMigrations;

internal sealed partial class SafeMigrationCSharpMigrationOperationGenerator
{
    /// <summary>Places complete table metadata inside the existing callback before immutable capture.</summary>
    /// <param name="operation">The provider-certified native table operation.</param>
    /// <param name="source">The complete table call rendered by EF Core.</param>
    /// <param name="outerIndent">The indentation depth of the surrounding operation stream.</param>
    /// <returns>The same table call with annotations inside its typed constraints callback.</returns>
    /// <exception cref="InvalidOperationException">
    /// The native source does not have the certified table shape.
    /// </exception>
    private string MoveTableAnnotationsIntoConstraints(
        CreateTableOperation operation,
        string source,
        int outerIndent
    )
    {
        const string method = ".CreateTable(";

        var methodIndex = source.IndexOf(method, StringComparison.Ordinal);
        if (methodIndex < 0
            || source
                .AsSpan(0, methodIndex)
                .ContainsAnyExcept(' ', '\t'))
        {
            throw UnexpectedAnnotatedTableSource();
        }

        var openParenthesis = methodIndex + method.Length - 1;
        var closeParenthesis = FindMatchingParenthesis(source, openParenthesis);
        var argumentIndent = FindArgumentIndent(source, openParenthesis, closeParenthesis);
        var newline = SafeMigrationGeneratedSource.GetConsistentNewLine(source);
        var suffixBuilder = CreateScratchBuilder(outerIndent + 1);
        Annotations(operation.GetAnnotations(), suffixBuilder);
        var annotationSuffix = UseTableSourceNewLine(suffixBuilder.ToString(), newline);

        if (annotationSuffix.Length == 0
            || !source.EndsWith(annotationSuffix, StringComparison.Ordinal)
            || closeParenthesis != source.Length - annotationSuffix.Length - 1)
        {
            throw UnexpectedAnnotatedTableSource();
        }

        // WHY: EF always emits the typed constraints body. Locate its closing
        // brace from the verified tail, not from callback-looking text inside
        // identifiers, defaults, CHECK SQL or comments. EF owns literal syntax.
        var commentSuffix = operation.Comment is null
            ? string.Empty
            : string.Concat(
                ",",
                newline,
                argumentIndent,
                "comment: ",
                Dependencies.CSharpHelper.Literal(operation.Comment));

        var tableTail = string.Concat("}", commentSuffix, ")");
        var callLength = closeParenthesis + 1;
        if (!source
            .AsSpan(0, callLength)
            .EndsWith(tableTail, StringComparison.Ordinal))
        {
            throw UnexpectedAnnotatedTableSource();
        }

        var closingBrace = callLength - tableTail.Length;
        var insertion = source.LastIndexOf('\n', closingBrace) + 1;
        if (!source
            .AsSpan(insertion, closingBrace - insertion)
            .SequenceEqual(argumentIndent.AsSpan()))
        {
            throw UnexpectedAnnotatedTableSource();
        }

        var statements = CreateScratchBuilder(outerIndent + 2);
        statements.Append("table");

        using (statements.Indent())
        {
            Annotations(operation.GetAnnotations(), statements);
        }

        statements.AppendLine(";");
        var annotationStatements = UseTableSourceNewLine(statements.ToString(), newline);

        return string.Concat(
            source.AsSpan(0, insertion),
            annotationStatements,
            source.AsSpan(insertion, callLength - insertion));
    }

    /// <summary>Matches native syntax line endings while retaining EF-escaped literal control characters.</summary>
    private static string UseTableSourceNewLine(
        string source,
        string newline
    ) => StringComparer.Ordinal.Equals(newline, Environment.NewLine)
        ? source
        : source.Replace(Environment.NewLine, newline, StringComparison.Ordinal);

    private static InvalidOperationException UnexpectedAnnotatedTableSource() => new(
        "The EF Core C# generator emitted an unexpected annotated CreateTable shape. "
        + "SafeMigrations stopped instead of moving table metadata to an unproven callback boundary.");
}
