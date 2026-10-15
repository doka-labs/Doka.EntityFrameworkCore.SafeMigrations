namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

/// <summary>Renders owned guard messages without flattening every formatted branch into a temporary string.</summary>
internal sealed class SqlServerSafeMigrationRuntimeDiagnostic
{
    private const int MaximumNativeArgumentLength = 2048;
    private const string PolicySuffix = "; policy=";
    private const string OperationSuffix = "; operation ";
    private readonly SafeMigrationOperation _operation;
    private readonly SafeMigrationOperationIdentity _identity;
    private readonly int? _ordinal;
    private readonly StringBuilder _scratch = new(256);

    /// <summary>Captures the immutable identifier-only identity once for one generated operation.</summary>
    /// <param name="operation">The exact authored operation whose native guard is generated.</param>
    /// <param name="ordinal">The known stream ordinal, or null when unavailable.</param>
    internal SqlServerSafeMigrationRuntimeDiagnostic(SafeMigrationOperation operation, int? ordinal)
    {
        _operation = operation;
        _identity = SafeMigrationOperationIdentity.Create(operation.Intent);
        _ordinal = ordinal;
    }

    /// <summary>Appends the original numbered, state-one THROW with its bounded authored identity.</summary>
    /// <param name="destination">The operation-owned final SQL builder.</param>
    /// <param name="number">The unchanged native SafeMigrations guard number.</param>
    /// <param name="guardCode">The stable native guard token.</param>
    /// <param name="reasonCode">A proven branch-specific reason, or null when unknown.</param>
    internal void AppendThrow(StringBuilder destination, int number, string guardCode, string? reasonCode = null)
    {
        destination.Append("THROW ").Append(number.ToString(CultureInfo.InvariantCulture)).Append(", ");
        AppendLiteral(destination, guardCode, reasonCode);
        destination.Append(", 1;");
    }

    /// <summary>Appends an escaped literal for a failure-only native CASE selecting an already-proven reason.</summary>
    /// <param name="destination">The operation-owned SQL buffer.</param>
    /// <param name="guardCode">The unchanged native guard token.</param>
    /// <param name="reasonCode">The selected branch's proven reason code.</param>
    internal void AppendLiteral(StringBuilder destination, string guardCode, string? reasonCode = null)
    {
        _scratch.Clear();
        SafeMigrationDiagnosticMessages.AppendRuntime(_scratch, _operation, _identity, guardCode, _ordinal,
            reasonCode);
        destination.Append("N'");
        AppendEscaped(destination, _scratch);
        destination.Append('\'');
    }

    /// <summary>Creates a bounded literal for independently owned session bootstrap and recovery templates.</summary>
    /// <param name="message">The already bounded, identifier-only diagnostic.</param>
    /// <returns>The Unicode T-SQL literal, with percent formatting and quote escapes preserved.</returns>
    internal static string FormatLiteral(string message)
    {
        var destination = new StringBuilder(Math.Min(message.Length, MaximumNativeArgumentLength) + 3).Append("N'");
        var truncate = GetNativeLength(message.AsSpan()) > MaximumNativeArgumentLength;
        var remaining = MaximumNativeArgumentLength - (truncate ? 3 : 0);
        foreach (var character in message)
        {
            var width = GetNativeWidth(character);
            if (width > remaining)
            {
                break;
            }

            AppendEscapedCharacter(destination, character);
            remaining -= width;
        }

        if (truncate)
        {
            destination.Append("...");
        }

        return destination.Append('\'').ToString();
    }

    /// <summary>Copies scratch chunks into the final guard without a flattened message allocation.</summary>
    /// <param name="destination">The final operation-owned SQL builder.</param>
    /// <param name="message">The bounded printable ASCII scratch message.</param>
    private void AppendEscaped(StringBuilder destination, StringBuilder message)
    {
        if (GetNativeLength(message, 0) <= MaximumNativeArgumentLength)
        {
            AppendEscapedRange(destination, message, 0, message.Length, MaximumNativeArgumentLength);

            return;
        }

        // WHY: The nvarchar(2048) argument limit applies before THROW interprets
        // doubled percent. Truncate identity text, not the trusted policy/reason
        // suffix, so a large identifier cannot consume the actionable explanation.
        var suffixOffset = FindTrustedSuffix(message);
        var suffixLength = GetNativeLength(message, suffixOffset);
        if (suffixLength > MaximumNativeArgumentLength - 3)
        {
            suffixOffset = message.Length;
            suffixLength = 0;
        }

        var prefixBudget = MaximumNativeArgumentLength - 3 - suffixLength;
        var prefixEnd = GetLogicalPrefixEnd(message, suffixOffset, prefixBudget);
        prefixEnd = SafeMigrationDiagnosticMessages.GetDisplayPrefixLength(message, 0, prefixEnd);

        // WHY: Native percent budgeting counts characters before SQL escaping,
        // but a cut inside Core's backslash/u-code display unit would misdescribe
        // an authored identifier. Cutting back never increases the native width.
        AppendEscapedRange(destination, message, 0, prefixEnd, prefixBudget);
        destination.Append("...");
        AppendEscapedRange(destination, message, suffixOffset, message.Length, suffixLength);
    }

    /// <summary>Chooses a logical prefix that fits the native argument width before display-unit cutback.</summary>
    /// <param name="message">The bounded Core scratch text.</param>
    /// <param name="end">The exclusive identity prefix end, before the trusted structural suffix.</param>
    /// <param name="remaining">The available pre-format native width, including doubled percent.</param>
    /// <returns>The absolute logical end that fits the native width.</returns>
    private static int GetLogicalPrefixEnd(StringBuilder message, int end, int remaining)
    {
        var offset = 0;
        foreach (var chunk in message.GetChunks())
        {
            var length = Math.Min(chunk.Length, end - offset);
            for (var index = 0; index < length; index++)
            {
                var width = GetNativeWidth(chunk.Span[index]);
                if (width > remaining)
                {
                    return offset + index;
                }

                remaining -= width;
            }

            offset += chunk.Length;
            if (offset >= end)
            {
                return end;
            }

        }

        return end;
    }

    /// <summary>
    /// Finds only the last typed Core policy suffix, optionally retaining its genuine stream ordinal.
    /// </summary>
    /// <param name="message">The bounded message produced directly by Core, not arbitrary caller text.</param>
    /// <returns>The trusted suffix offset, or the end when no structural suffix exists.</returns>
    private int FindTrustedSuffix(StringBuilder message)
    {
        Span<char> digits = stackalloc char[10];
        var ordinalLength = 0;
        if (_ordinal is { } ordinal)
        {
            _ = ordinal.TryFormat(digits, out ordinalLength, provider: CultureInfo.InvariantCulture);
        }

        // WHY: Core appends the real typed policy after every authored identifier.
        // Its later explanation contains only constants and machine codes. An
        // identifier containing the same marker must not become the trusted suffix.
        for (var offset = message.Length - PolicySuffix.Length; offset >= 0; offset--)
        {
            if (!Matches(message, offset, PolicySuffix))
            {
                continue;
            }

            if (ordinalLength != 0)
            {
                var operationOffset = offset - OperationSuffix.Length - ordinalLength;
                if (operationOffset >= 0
                    && Matches(message, operationOffset, OperationSuffix)
                    && Matches(message, operationOffset + OperationSuffix.Length, digits[..ordinalLength]))
                {
                    return operationOffset;
                }

            }

            return offset;
        }

        return message.Length;
    }

    /// <summary>Checks a bounded structural marker without flattening the scratch buffer.</summary>
    /// <param name="message">The operation-owned scratch text.</param>
    /// <param name="offset">The candidate marker offset.</param>
    /// <param name="expected">The marker or formatted ordinal being checked.</param>
    /// <returns>Whether every expected character matches at the candidate offset.</returns>
    private static bool Matches(StringBuilder message, int offset, ReadOnlySpan<char> expected)
    {
        for (var index = 0; index < expected.Length; index++)
        {
            if (message[offset + index] != expected[index])
            {
                return false;
            }

        }

        return true;
    }

    /// <summary>Counts the native argument width while stopping after the fixed THROW limit.</summary>
    /// <param name="message">The bounded Core scratch text.</param>
    /// <param name="start">The first logical character being counted.</param>
    /// <returns>The argument width, capped immediately after exceeding the native limit.</returns>
    private static int GetNativeLength(StringBuilder message, int start)
    {
        var offset = 0;
        var length = 0;
        foreach (var chunk in message.GetChunks())
        {
            var localStart = Math.Clamp(start - offset, 0, chunk.Length);
            foreach (var character in chunk.Span[localStart..])
            {
                length += GetNativeWidth(character);
                if (length > MaximumNativeArgumentLength)
                {
                    return length;
                }

            }

            offset += chunk.Length;
        }

        return length;
    }

    /// <summary>
    /// Counts plain session-template text without treating policy-like substrings as trusted metadata.
    /// </summary>
    /// <param name="message">The literal text supplied to the session-template codec.</param>
    /// <returns>The native argument width, capped immediately after exceeding the limit.</returns>
    private static int GetNativeLength(ReadOnlySpan<char> message)
    {
        var length = 0;
        foreach (var character in message)
        {
            length += GetNativeWidth(character);
            if (length > MaximumNativeArgumentLength)
            {
                return length;
            }

        }

        return length;
    }

    /// <summary>Copies complete logical characters without splitting quote or percent escape pairs.</summary>
    /// <param name="destination">The final operation-owned SQL buffer.</param>
    /// <param name="message">The bounded scratch text.</param>
    /// <param name="start">The first logical character to copy.</param>
    /// <param name="end">The exclusive logical end of the copied range.</param>
    /// <param name="remaining">The remaining pre-format native argument budget.</param>
    private static void AppendEscapedRange(StringBuilder destination, StringBuilder message, int start,
        int end, int remaining)
    {
        var offset = 0;
        foreach (var chunk in message.GetChunks())
        {
            var localStart = Math.Clamp(start - offset, 0, chunk.Length);
            var localEnd = Math.Clamp(end - offset, localStart, chunk.Length);
            foreach (var character in chunk.Span[localStart..localEnd])
            {
                var width = GetNativeWidth(character);
                if (width > remaining)
                {
                    return;
                }

                AppendEscapedCharacter(destination, character);
                remaining -= width;
            }

            offset += chunk.Length;
            if (offset >= end)
            {
                return;
            }

        }
    }

    /// <summary>Counts percent twice, while SQL quote pairs decode to one argument character.</summary>
    /// <param name="character">The printable ASCII diagnostic character.</param>
    /// <returns>The width before native percent formatting, after SQL quote parsing.</returns>
    private static int GetNativeWidth(char character) => character == '%' ? 2 : 1;

    /// <summary>Writes one indivisible SQL quote or native percent escape pair.</summary>
    /// <param name="destination">The final SQL literal buffer.</param>
    /// <param name="character">The logical diagnostic character.</param>
    private static void AppendEscapedCharacter(StringBuilder destination, char character)
    {
        if (character is '\'' or '%')
        {
            destination.Append(character);
        }

        destination.Append(character);
    }
}
