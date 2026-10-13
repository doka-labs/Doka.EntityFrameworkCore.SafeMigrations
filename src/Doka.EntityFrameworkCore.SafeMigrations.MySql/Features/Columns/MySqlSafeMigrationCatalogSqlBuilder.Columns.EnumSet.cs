namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationCatalogSqlBuilder
{
    private const string EnumSetSqlWhitespace = " \t\r\n\v\f";
    private const string NativeEnumNoBackslashEscapesExpression =
        "FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0";

    /// <summary>Identifies standard-only literals whose known native comment scope invalidates the protocol.</summary>
    /// <param name="definition">The immutable target with its authored member syntax and comment.</param>
    /// <returns>Whether missing work must refuse staging without rejecting an already exact target.</returns>
    internal static bool HasStandardOnlyEnumSetNativeCommentScope(ExpectedColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition.Comment?.Contains('\\', StringComparison.Ordinal) == true
            && definition.StoreType is { } storeType
            && BuildEnumSetCatalogDisplay(storeType, backslashEscapes: true, out _) is not null
            && BuildEnumSetCatalogDisplay(storeType, backslashEscapes: false, out _) is null;
    }

    /// <summary>Identifies a target whose native comment scope can change ENUM/SET member interpretation.</summary>
    /// <param name="definition">The immutable target, including nullable repair targets.</param>
    /// <returns>Whether non-matching work requires NO_BACKSLASH_ESCAPES to be enabled already.</returns>
    internal static bool RequiresNativeEnumNoBackslashEscapes(ExpectedColumnDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition.Comment?.Contains('\\', StringComparison.Ordinal) == true
            && HasModeSensitiveEnumSetMembers(definition);
    }

    /// <summary>Identifies a CREATE TABLE comment scope affecting any mode-sensitive ENUM/SET column.</summary>
    /// <param name="definition">The immutable table with its table and column comments.</param>
    /// <returns>Whether creation requires NO_BACKSLASH_ESCAPES to be enabled already.</returns>
    internal static bool RequiresNativeEnumNoBackslashEscapes(ExpectedTableDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return (definition.Comment?.Contains('\\', StringComparison.Ordinal) == true
                || definition.Columns.Any(static column =>
                    column.Comment?.Contains('\\', StringComparison.Ordinal) == true))
            && definition.Columns.Any(HasModeSensitiveEnumSetMembers);
    }

    /// <summary>Compares decoded member domains without rewriting authored types or merging mode variants.</summary>
    /// <param name="definition">The immutable target's explicitly authored store type.</param>
    /// <returns>Whether native literal decoding changes the member domain.</returns>
    private static bool HasModeSensitiveEnumSetMembers(ExpectedColumnDefinition definition) =>
        definition.StoreType is { } storeType
        && !StringComparer.Ordinal.Equals(
            BuildEnumSetCatalogDisplay(storeType, backslashEscapes: true, out _),
            BuildEnumSetCatalogDisplay(storeType, backslashEscapes: false, out _));

    /// <summary>Qualifies native comment-mode work while preserving Matching and prerequisite precedence.</summary>
    /// <param name="intent">The authored column or complete table contract.</param>
    /// <param name="plan">The provider's ordinary state and bounded row-proof plan.</param>
    /// <returns>The plan gated by the original current mode before conversion or data probes.</returns>
    private static MySqlSafeMigrationRuntimePlan ApplyNativeEnumModeQualification(
        SafeMigrationIntent intent,
        MySqlSafeMigrationRuntimePlan plan
    )
    {
        var required = intent switch
        {
            EnsureColumnIntent column => RequiresNativeEnumNoBackslashEscapes(column.Definition),
            AlterColumnIntent column => RequiresNativeEnumNoBackslashEscapes(column.Definition),
            EnsureTableIntent table => RequiresNativeEnumNoBackslashEscapes(table.Definition),
            _ => false,
        };
        if (!required || plan.IsStaticallyUnsupported)
        {
            return plan;
        }

        var matching = plan.RequiredColumnAddPlan is null
            ? plan.Postcondition
            : $"({MySqlSafeMigrationRuntimePlan.RequiredColumnAddStatusPlaceholder}) = 'complete'";
        var prerequisite = plan.PrerequisiteExpression;
        var mode = NativeEnumNoBackslashEscapesExpression;
        var ordinaryCode = plan.ClassificationCodeExpression ?? "NULL";

        string QualifyState(string expression) =>
            $"CASE WHEN NOT ({prerequisite}) THEN 'prerequisite_missing' "
            + $"WHEN ({matching}) THEN 'matching' WHEN NOT ({mode}) THEN 'unsupported' ELSE ({expression}) END";

        // WHY: Native comment setup enables NO_BACKSLASH_ESCAPES. A different
        // original mode can create another member domain and falsely satisfy
        // an in-scope postcondition. Requiring that mode already avoids both
        // domain changes and any new session mutation; Matching emits no DDL.
        return plan with
        {
            StateExpression = QualifyState(plan.StateExpression),
            StateEvaluationGuardFailureExpression = plan.StateEvaluationGuardFailureExpression is { } failure
                ? QualifyState(failure)
                : null,
            RepairPrecondition = $"({mode}) AND ({plan.RepairPrecondition})",
            ClassificationCodeExpression = $"CASE WHEN NOT ({matching}) AND NOT ({mode}) "
                + $"THEN 'column_native_enum_mode_conflict' ELSE ({ordinaryCode}) END",
            NullabilityDataProbe = plan.NullabilityDataProbe is { } nullability
                ? nullability with
                {
                    RepairInvariantExpression = $"({mode}) AND ({nullability.RepairInvariantExpression})",
                }
                : null,
            DataProbe = plan.DataProbe is { } data
                ? data with
                {
                    TransitionInvariantExpression = data.TransitionInvariantExpression is { } invariant
                        ? $"({mode}) AND ({invariant})"
                        : null,
                    NarrowingExpression = $"({mode}) AND ({data.NarrowingExpression})",
                }
                : null,
        };
    }

    /// <summary>Compares ordered ENUM/SET members exactly under the native literal-decoding mode.</summary>
    /// <param name="storeType">The complete authored ENUM or SET declaration.</param>
    /// <returns>The binary catalog comparison qualified by current literal syntax.</returns>
    private string BuildEnumSetStoreTypeMatches(string storeType)
    {
        var escaped = BuildEnumSetCatalogDisplay(storeType, backslashEscapes: true, out var escapedDoubleQuotes);
        var literal = BuildEnumSetCatalogDisplay(storeType, backslashEscapes: false, out var literalDoubleQuotes);

        string Match(string? display, bool doubleQuotes) => display is null
            ? "FALSE"
            : $"CAST(c.COLUMN_TYPE AS BINARY) = CAST({Literal(display)} AS BINARY)"
                + (doubleQuotes ? " AND FIND_IN_SET('ANSI_QUOTES', @@SESSION.sql_mode) = 0" : string.Empty);

        // WHY: Folding the complete type also folds member values. Backslash
        // interpretations must remain mode-qualified; accepting both displays
        // together could certify a different stored value domain.
        var escapedMatch = Match(escaped, escapedDoubleQuotes);
        if (StringComparer.Ordinal.Equals(escaped, literal) && escapedDoubleQuotes == literalDoubleQuotes)
        {
            return escapedMatch;
        }

        return "(CASE WHEN FIND_IN_SET('NO_BACKSLASH_ESCAPES', @@SESSION.sql_mode) > 0 "
            + $"THEN ({Match(literal, literalDoubleQuotes)}) ELSE ({escapedMatch}) END)";
    }

    /// <summary>Serializes only a quoted ENUM/SET member list using the native catalog display rules.</summary>
    /// <param name="storeType">The authored keyword and ordered quoted members.</param>
    /// <param name="backslashEscapes">Whether native SQL literal decoding interprets backslashes.</param>
    /// <param name="doubleQuotes">Whether any member uses mode-sensitive double quotation marks.</param>
    /// <returns>The exact catalog display, or null for a malformed or unrecognized member declaration.</returns>
    internal static string? BuildEnumSetCatalogDisplay(
        string storeType,
        bool backslashEscapes,
        out bool doubleQuotes
    )
    {
        ArgumentNullException.ThrowIfNull(storeType);

        doubleQuotes = false;
        var text = storeType.AsSpan().Trim(EnumSetSqlWhitespace);
        var open = text.IndexOf('(');
        if (open <= 0)
        {
            return null;
        }

        var keyword = text[..open].TrimEnd(EnumSetSqlWhitespace);
        var kind = keyword.Equals("enum", StringComparison.OrdinalIgnoreCase) ? "enum"
            : keyword.Equals("set", StringComparison.OrdinalIgnoreCase) ? "set" : null;
        if (kind is null)
        {
            return null;
        }

        var result = new StringBuilder(text.Length).Append(kind).Append('(');
        var index = open + 1;
        while (index < text.Length)
        {
            while (index < text.Length && EnumSetSqlWhitespace.Contains(text[index], StringComparison.Ordinal))
            {
                index++;
            }

            result.Append('\'');
            var fragments = 0;
            while (index < text.Length && text[index] is '\'' or '"')
            {
                var quote = text[index++];
                doubleQuotes |= quote == '"';
                if (!AppendEnumSetMember(text, ref index, quote, backslashEscapes, result))
                {
                    return null;
                }

                fragments++;
                while (index < text.Length && EnumSetSqlWhitespace.Contains(text[index], StringComparison.Ordinal))
                {
                    index++;
                }
            }

            if (fragments == 0 || index >= text.Length)
            {
                return null;
            }

            // WHY: Native ENUM/SET definitions discard trailing member spaces,
            // including spaces decoded from an escaped or concatenated fragment.
            while (result[^1] == ' ')
            {
                result.Length--;
            }

            result.Append('\'');
            if (text[index] == ',')
            {
                result.Append(',');
                index++;

                continue;
            }

            if (text[index++] != ')' || !text[index..].Trim(EnumSetSqlWhitespace).IsEmpty)
            {
                return null;
            }

            return result.Append(')').ToString();
        }

        return null;
    }

    /// <summary>Decodes one quoted fragment without changing member case, order or embedded punctuation.</summary>
    /// <param name="text">The complete authored declaration.</param>
    /// <param name="index">The next unread character after the opening quote.</param>
    /// <param name="quote">The member fragment's quote delimiter.</param>
    /// <param name="backslashEscapes">Whether backslashes have native escape semantics.</param>
    /// <param name="result">The ordered catalog display receiving decoded values.</param>
    /// <returns>Whether the fragment terminated at its exact closing delimiter.</returns>
    private static bool AppendEnumSetMember(
        ReadOnlySpan<char> text,
        ref int index,
        char quote,
        bool backslashEscapes,
        StringBuilder result
    )
    {
        while (index < text.Length)
        {
            var character = text[index++];
            if (character == quote)
            {
                if (index >= text.Length || text[index] != quote)
                {
                    return true;
                }

                index++;
            }
            else if (character == '\\' && backslashEscapes)
            {
                if (index >= text.Length)
                {
                    return false;
                }

                character = text[index++];
                if (character is '%' or '_')
                {
                    AppendEnumSetCatalogCharacter(result, '\\');
                }

                character = character switch
                {
                    '0' => '\0',
                    'b' => '\b',
                    'n' => '\n',
                    'r' => '\r',
                    't' => '\t',
                    'Z' => (char)26,
                    _ => character,
                };
            }

            AppendEnumSetCatalogCharacter(result, character);
        }

        return false;
    }

    /// <summary>Applies the shared native ENUM/SET catalog escaping after literal decoding.</summary>
    /// <param name="result">The ordered member display receiving one character.</param>
    /// <param name="character">The decoded character, with literal case retained.</param>
    private static void AppendEnumSetCatalogCharacter(StringBuilder result, char character)
    {
        _ = character switch
        {
            '\0' => result.Append("\\0"),
            '\n' => result.Append("\\n"),
            '\r' => result.Append("\\r"),
            '\\' => result.Append("\\\\"),
            '\'' => result.Append("''"),
            _ => result.Append(character),
        };
    }
}
