namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationDefaultValueSerializer
{
    public static (string? TypeName, string? Json) Capture(
        object? value
    )
    {
        if (value is null)
        {
            return (null, null);
        }

        var type = value.GetType();
        return (type.AssemblyQualifiedName, JsonSerializer.Serialize(value, type));
    }

    public static string? ToLegacyLiteral(
        object? value
    ) => value switch
    {
        null => null,
        string text => text,
        char character => character.ToString(),
        IFormattable formattable => formattable.ToString(format: null, CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    public static bool TryDeserialize(
        string? typeName,
        string? json,
        out object? value,
        out Type? type
    )
    {
        value = null;
        type = null;

        if (string.IsNullOrWhiteSpace(typeName)
            || string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        type = Type.GetType(typeName, throwOnError: false);
        if (type is null)
        {
            return false;
        }

        try
        {
            value = JsonSerializer.Deserialize(json, type);
            return value is not null;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (NotSupportedException)
        {
            return false;
        }
    }
}
