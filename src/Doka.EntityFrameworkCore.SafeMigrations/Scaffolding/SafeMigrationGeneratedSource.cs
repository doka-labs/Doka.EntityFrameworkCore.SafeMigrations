namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Provides structural helpers for source emitted by EF Core migrations generators.
/// </summary>
internal static class SafeMigrationGeneratedSource
{
    /// <summary>
    /// Resolves the one line-ending convention used by generated source.
    /// </summary>
    /// <param name="source">The generated source to inspect.</param>
    /// <returns>
    /// The detected LF or CRLF sequence, or the current platform sequence when
    /// the source does not contain a line ending.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// The source mixes line-ending conventions or contains a standalone
    /// carriage return.
    /// </exception>
    internal static string GetConsistentNewLine(
        string source
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        string? newline = null;
        for (var index = 0; index < source.Length; index++)
        {
            string current;
            if (source[index] == '\r')
            {
                if (index + 1 >= source.Length || source[index + 1] != '\n')
                {
                    throw InconsistentLineEndings();
                }

                current = "\r\n";
                index++;
            }
            else if (source[index] == '\n')
            {
                current = "\n";
            }
            else
            {
                continue;
            }

            if (newline is null)
            {
                newline = current;
            }
            else if (!StringComparer.Ordinal.Equals(newline, current))
            {
                throw InconsistentLineEndings();
            }
        }

        return newline ?? Environment.NewLine;
    }

    private static InvalidOperationException InconsistentLineEndings() => new(
        "The EF Core migrations generator emitted inconsistent line endings. "
        + "SafeMigrations stopped instead of producing malformed migration source.");
}
