namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Serializes and deserializes expected schema definitions that are attached to migration operations.
/// </summary>
public static class SafeMigrationDefinitionSerializer
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Serializes an expected definition to JSON for operation annotations.
    /// </summary>
    /// <typeparam name="TDefinition">The expected-definition type to serialize.</typeparam>
    /// <param name="definition">The definition instance to serialize.</param>
    /// <returns>The serialized JSON representation.</returns>
    public static string Serialize<TDefinition>(TDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return JsonSerializer.Serialize(definition, _options);
    }

    /// <summary>
    /// Deserializes an expected definition from JSON.
    /// </summary>
    /// <typeparam name="TDefinition">The expected-definition type to deserialize.</typeparam>
    /// <param name="json">The serialized JSON text.</param>
    /// <returns>The deserialized definition, or <see langword="null"/> when the input is empty.</returns>
    public static TDefinition? Deserialize<TDefinition>(string? json)
        => string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<TDefinition>(json, _options);
}
