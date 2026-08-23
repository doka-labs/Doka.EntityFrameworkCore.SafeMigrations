namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal static class MySqlCatalogSqlTemplate
{
    private const char EndMarker = '\u001f';
    private const char StartMarker = '\u001e';

    public static string Marker(
        int ordinal
    ) => string.Concat(StartMarker, ordinal.ToString(CultureInfo.InvariantCulture), EndMarker);

    public static string Render(
        string template,
        IReadOnlyList<string> values,
        Func<string, string> renderValue
    )
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(renderValue);

        var builder = new StringBuilder(template.Length);
        var position = 0;
        while (position < template.Length)
        {
            var markerStart = template.IndexOf(StartMarker, position);
            if (markerStart < 0)
            {
                builder.Append(template, position, template.Length - position);
                break;
            }

            builder.Append(template, position, markerStart - position);
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
            position = markerEnd + 1;
        }

        return builder.ToString();
    }

    public static string RenderPrepared(
        string template,
        IReadOnlyList<string> renderedValues
    ) => Render(template, renderedValues, static value => value);
}
