namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

/// <summary>
/// Appends bounded native MESSAGE literals using one identifier snapshot and reusable scratch buffer.
/// </summary>
internal sealed class PostgreSqlSafeMigrationRuntimeDiagnostic
{
    private readonly SafeMigrationOperation _operation;
    private readonly SafeMigrationOperationIdentity _identity;
    private readonly int? _ordinal;
    private readonly StringBuilder _scratch = new(256);

    /// <summary>Captures only immutable authored identifiers, never row values or SQL payloads.</summary>
    /// <param name="operation">The exact authored operation guarded by this SQL batch.</param>
    /// <param name="ordinal">The known stream ordinal, or null when unavailable.</param>
    internal PostgreSqlSafeMigrationRuntimeDiagnostic(SafeMigrationOperation operation, int? ordinal)
    {
        _operation = operation;
        _identity = SafeMigrationOperationIdentity.Create(operation.Intent);
        _ordinal = ordinal;
        DelimiterContent = SafeMigrationDiagnosticMessages.FormatIdentity(_identity);
    }

    /// <summary>Gets the bounded identifier text that must not collide with the anonymous-block delimiter.</summary>
    internal string DelimiterContent { get; }

    /// <summary>Appends escaped messages without allocating a complete formatted string for each guard arm.</summary>
    /// <param name="destination">The final operation-owned SQL buffer.</param>
    /// <param name="guardCode">The unchanged native guard token.</param>
    /// <param name="reasonCode">A branch-established machine reason, or null when unknown.</param>
    /// <param name="maximumLength">
    /// The message budget, at least three characters, reserved before native suffixes.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The budget cannot accommodate an explicit truncation marker.
    /// </exception>
    internal void AppendMessage(StringBuilder destination, string guardCode, string? reasonCode = null,
        int maximumLength = 2048)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumLength, 3);
        _scratch.Clear();
        SafeMigrationDiagnosticMessages.AppendRuntime(_scratch, _operation, _identity, guardCode, _ordinal,
            reasonCode);

        // WHY: Escape-string syntax is independent of standard_conforming_strings.
        // Both diagnostic escape sequences and authored apostrophes remain data.
        destination.Append("E'");
        var truncate = _scratch.Length > maximumLength;
        var prefixLength = truncate ? maximumLength - 3 : _scratch.Length;
        var remaining = SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(_scratch, 0, prefixLength);

        // WHY: A reserved native suffix must not turn a complete Core display
        // escape into ambiguous text such as a trailing backslash or partial u-code.
        foreach (var chunk in _scratch.GetChunks())
        {
            var characters = chunk.Span[..Math.Min(chunk.Length, remaining)];
            foreach (var character in characters)
            {
                if (character is '\'' or '\\')
                {
                    destination.Append(character);
                }

                destination.Append(character);
            }

            remaining -= characters.Length;
            if (remaining == 0)
            {
                break;
            }

        }

        if (truncate)
        {
            destination.Append("...");
        }

        destination.Append('\'');
    }
}
