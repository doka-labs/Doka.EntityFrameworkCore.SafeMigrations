namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

/// <summary>Renders captured catalog values and optional table-source bindings in a single pass.</summary>
/// <remarks>
/// WHY: Rendering is the only place every captured template passes through, so this is where a
/// catalog relation can be redirected to a snapshot of it. A template names the live views and a
/// caller opts in by supplying a binding, so a path that does not ask for redirection, such as
/// runtime migration generation, performs no scan at all. See D-014.
/// </remarks>
internal static class MySqlCatalogSqlTemplate
{
    private const char EndMarker = '\u001f';
    private const char StartMarker = '\u001e';

    /// <summary>Creates the opaque placeholder for one captured value.</summary>
    /// <param name="ordinal">The captured value's zero-based position.</param>
    /// <returns>The placeholder embedded in a template.</returns>
    public static string Marker(
        int ordinal
    ) => string.Concat(StartMarker, ordinal.ToString(CultureInfo.InvariantCulture), EndMarker);

    /// <summary>Renders typed values while retaining lexical context between their placeholders.</summary>
    /// <param name="template">The captured SQL template.</param>
    /// <param name="values">The captured typed values.</param>
    /// <param name="renderValue">Renders one value as a SQL literal or parameter reference.</param>
    /// <param name="relations">The optional catalog table-source binding.</param>
    /// <returns>The rendered SQL.</returns>
    public static string Render(
        string template,
        IReadOnlyList<MySqlCatalogParameterValue> values,
        Func<MySqlCatalogParameterValue, string> renderValue,
        Func<string, string>? relations = null
    )
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(renderValue);

        var builder = new StringBuilder(template.Length);
        var reader = new MySqlCatalogRelations.Reader();
        var position = 0;
        while (position < template.Length)
        {
            var markerStart = template.IndexOf(StartMarker, position);
            if (markerStart < 0)
            {
                reader.AppendResolved(
                    builder, template, position, template.Length - position, relations);

                break;
            }

            reader.AppendResolved(builder, template, position, markerStart - position, relations);
            var markerEnd = template.IndexOf(EndMarker, markerStart + 1);
            if (markerEnd < 0
                || !int.TryParse(
                    template.AsSpan(markerStart + 1, markerEnd - markerStart - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ordinal)
                || (uint)ordinal >= (uint)values.Count)
            {
                throw new InvalidOperationException("The MySQL catalog SQL template contains an invalid value marker.");
            }

            builder.Append(renderValue(values[ordinal]));
            reader.ConsumeValue();
            position = markerEnd + 1;
        }

        return builder.ToString();
    }

    /// <summary>Renders captured string values with an optional catalog table-source binding.</summary>
    /// <param name="template">The captured SQL template.</param>
    /// <param name="values">The captured string values.</param>
    /// <param name="renderValue">Renders one value as a SQL literal or parameter reference.</param>
    /// <param name="relations">The optional catalog table-source binding.</param>
    /// <returns>The rendered SQL.</returns>
    public static string Render(
        string template,
        IReadOnlyList<string> values,
        Func<string, string> renderValue,
        Func<string, string>? relations = null
    )
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(renderValue);

        return RenderPrepared(template, values.Select(renderValue).ToArray(), relations);
    }

    /// <summary>Substitutes prepared values without interpreting or redirecting their SQL text.</summary>
    /// <param name="template">The captured SQL template.</param>
    /// <param name="renderedValues">Values already rendered as SQL literals or parameter references.</param>
    /// <param name="relations">The optional catalog table-source binding.</param>
    /// <returns>The rendered SQL, or the original template for the unbound, value-free fast path.</returns>
    public static string RenderPrepared(
        string template,
        IReadOnlyList<string> renderedValues,
        Func<string, string>? relations = null
    )
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(renderedValues);

        if (renderedValues.Count == 0
            && template.IndexOf(StartMarker, StringComparison.Ordinal) < 0)
        {
            return MySqlCatalogRelations.Resolve(template, relations);
        }

        var builder = new StringBuilder(template.Length);
        var reader = new MySqlCatalogRelations.Reader();
        var position = 0;
        while (position < template.Length)
        {
            var markerStart = template.IndexOf(StartMarker, position);
            if (markerStart < 0)
            {
                reader.AppendResolved(
                    builder, template, position, template.Length - position, relations);

                break;
            }

            reader.AppendResolved(builder, template, position, markerStart - position, relations);
            var markerEnd = template.IndexOf(EndMarker, markerStart + 1);
            if (markerEnd < 0
                || !int.TryParse(
                    template.AsSpan(markerStart + 1, markerEnd - markerStart - 1),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var ordinal)
                || (uint)ordinal >= (uint)renderedValues.Count)
            {
                throw new InvalidOperationException("The MySQL catalog SQL template contains an invalid value marker.");
            }

            builder.Append(renderedValues[ordinal]);
            reader.ConsumeValue();
            position = markerEnd + 1;
        }

        return builder.ToString();
    }
}
