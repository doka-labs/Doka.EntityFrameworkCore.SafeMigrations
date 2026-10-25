namespace Doka.EntityFrameworkCore.SafeMigrations.PostgreSql;

internal sealed partial class PostgreSqlSafeMigrationSqlExpressionRenderer
{
    /// <summary>Resolves a validated native catalog spelling or rejects an unsupported CAST target.</summary>
    private string FindCanonicalStoreType(
        string storeType
    )
    {
        if (!TryFindCanonicalStoreType(storeType, out var canonicalStoreType))
        {
            throw new NotSupportedException(
                $"PostgreSQL has no type mapping for structured CAST target '{storeType}'.");
        }

        return canonicalStoreType;
    }

    /// <summary>Validates through Npgsql before canonicalizing PostgreSQL's built-in aliases.</summary>
    private bool TryFindCanonicalStoreType(
        string storeType,
        out string canonicalStoreType
    )
    {
        if (!TryNormalizePreMappingAlias(storeType, out var mappingStoreType))
        {
            canonicalStoreType = string.Empty;
            return false;
        }

        var mapping = _typeMappingSource.FindMapping(mappingStoreType);
        if (mapping is null)
        {
            canonicalStoreType = string.Empty;
            return false;
        }

        // Npgsql validates the type but intentionally preserves caller aliases.
        // PostgreSQL's catalog deparser emits canonical built-in names, so
        // normalize documented aliases before building both DDL and candidates.
        canonicalStoreType = CanonicalizeBuiltInAliases(mapping.StoreType);
        return true;
    }

    /// <summary>Validates PostgreSQL FLOAT precision while preserving custom names and array suffixes.</summary>
    private static bool TryNormalizePreMappingAlias(
        string storeType,
        out string normalizedStoreType
    )
    {
        var candidate = storeType
            .AsSpan()
            .Trim();

        var scalarLength = candidate.Length;
        while (scalarLength >= 2
               && candidate[..scalarLength]
                   .EndsWith("[]", StringComparison.Ordinal))
        {
            scalarLength -= 2;
        }

        var scalarType = candidate[..scalarLength].TrimEnd();
        var arraySuffix = candidate[scalarLength..];
        if (!scalarType.StartsWith("float", StringComparison.OrdinalIgnoreCase))
        {
            normalizedStoreType = storeType;
            return true;
        }

        var precisionClause = scalarType["float".Length..]
            .Trim();
        if (precisionClause.Length == 0)
        {
            normalizedStoreType = AppendArraySuffix("double precision", arraySuffix);
            return true;
        }

        // Preserve custom types that merely share the keyword prefix. Once an
        // opening parenthesis is present, however, the input claims PostgreSQL
        // FLOAT(p) grammar and must satisfy its documented binary-precision
        // range before reaching Npgsql.
        if (precisionClause[0] != '(')
        {
            normalizedStoreType = storeType;
            return true;
        }

        if (precisionClause[^1] != ')'
            || !int.TryParse(
                precisionClause[1..^1].Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var precision)
            || precision is < 1 or > 53)
        {
            normalizedStoreType = string.Empty;
            return false;
        }

        normalizedStoreType = AppendArraySuffix(precision <= 24 ? "real" : "double precision", arraySuffix);
        return true;
    }

    /// <summary>Retains every array dimension without copying an unchanged scalar name.</summary>
    private static string AppendArraySuffix(
        string scalarType,
        ReadOnlySpan<char> arraySuffix
    ) => arraySuffix.Length == 0 ? scalarType : string.Concat(scalarType.AsSpan(), arraySuffix);

    /// <summary>Normalizes only known built-in aliases, preserving dimensions and custom scalar names.</summary>
    private static string CanonicalizeBuiltInAliases(
        string storeType
    )
    {
        var candidate = storeType.Trim();
        var scalarLength = candidate.Length;
        while (scalarLength >= 2
               && candidate
                   .AsSpan(0, scalarLength)
                   .EndsWith("[]", StringComparison.Ordinal))
        {
            scalarLength -= 2;
        }

        var scalarType = candidate[..scalarLength];
        var arraySuffix = candidate[scalarLength..];
        var canonicalScalarType = scalarType.ToLowerInvariant() switch
        {
            "int" or "int4" => "integer",
            "int2" => "smallint",
            "int8" => "bigint",
            "float4" => "real",
            "float8" => "double precision",
            "bool" => "boolean",
            _ => CanonicalizeParameterizedBuiltInAlias(scalarType),
        };

        return canonicalScalarType + arraySuffix;
    }

    /// <summary>Preserves native precision and time-zone spelling for supported parameterized aliases.</summary>
    private static string CanonicalizeParameterizedBuiltInAlias(
        string storeType
    )
    {
        if (TryReplaceAlias(storeType, "decimal", "numeric", out var canonical)
            || TryReplaceAlias(storeType, "varchar", "character varying", out canonical)
            || TryReplaceAlias(storeType, "bpchar", "character", out canonical)
            || TryReplaceAlias(storeType, "char", "character", out canonical)
            || TryReplaceAlias(storeType, "varbit", "bit varying", out canonical))
        {
            return canonical;
        }

        if (TryReplaceAlias(storeType, "timestamptz", "timestamp", out canonical)
            || TryReplaceAlias(storeType, "timetz", "time", out canonical))
        {
            return canonical + " with time zone";
        }

        if (TryReplaceAlias(storeType, "timestamp", "timestamp", out canonical)
            || TryReplaceAlias(storeType, "time", "time", out canonical))
        {
            return canonical + " without time zone";
        }

        return storeType;
    }

    /// <summary>Accepts an exact alias or complete parameter clause, never an arbitrary name prefix.</summary>
    private static bool TryReplaceAlias(
        string storeType,
        string alias,
        string canonicalName,
        out string canonical
    )
    {
        if (!storeType.StartsWith(alias, StringComparison.OrdinalIgnoreCase))
        {
            canonical = string.Empty;
            return false;
        }

        var suffix = storeType[alias.Length..];
        if (suffix.Length > 0
            && (suffix[0] != '(' || suffix[^1] != ')'))
        {
            canonical = string.Empty;
            return false;
        }

        canonical = canonicalName + suffix;
        return true;
    }
}
