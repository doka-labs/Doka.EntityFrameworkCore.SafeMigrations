namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationModelFingerprint
{
    private static void WriteAnnotations(
        CanonicalHashWriter writer,
        IReadOnlyAnnotatable metadata
    )
    {
        var annotations = metadata
            .GetAnnotations()
            .Where(static annotation => IsMigrationAnnotation(annotation.Name))
            .OrderBy(static annotation => annotation.Name, StringComparer.Ordinal)
            .ToArray();

        writer.Add(annotations.Length);
        foreach (var annotation in annotations)
        {
            writer.Add(annotation.Name);
            WriteValue(writer, annotation.Value, $"annotation '{annotation.Name}'");
        }
    }

    private static bool IsMigrationAnnotation(
        string name
    ) => name.Contains(':', StringComparison.Ordinal)
        && !StringComparer.Ordinal.Equals(name, "BaseTypeDiscoveryConvention:DerivedTypes")
        && !StringComparer.Ordinal.Equals(name, "Relational:RelationalModel")
        && !StringComparer.Ordinal.Equals(name, "Relational:ModelDependencies")
        && !StringComparer.Ordinal.Equals(name, "Relational:DbFunctions")
        && !StringComparer.Ordinal.Equals(name, "Relational:Sequences");

    private static void WriteValue(
        CanonicalHashWriter writer,
        object? value,
        string context
    )
    {
        if (value is null)
        {
            writer.Add("null");
            return;
        }

        writer.Add(value.GetType().FullName);
        switch (value)
        {
            case DBNull:
                writer.Add("db-null");
                break;
            case string text:
                writer.Add(text);
                break;
            case char character:
                writer.Add((int)character);
                break;
            case bool boolean:
                writer.Add(boolean);
                break;
            case byte number:
                writer.Add((int)number);
                break;
            case sbyte number:
                writer.Add((int)number);
                break;
            case short number:
                writer.Add((int)number);
                break;
            case ushort number:
                writer.Add((int)number);
                break;
            case int number:
                writer.Add(number);
                break;
            case uint number:
                writer.Add(number.ToString(CultureInfo.InvariantCulture));
                break;
            case long number:
                writer.Add(number);
                break;
            case ulong number:
                writer.Add(number.ToString(CultureInfo.InvariantCulture));
                break;
            case decimal number:
                writer.Add(number.ToString("G29", CultureInfo.InvariantCulture));
                break;
            case float number:
                writer.Add(BitConverter.SingleToInt32Bits(number));
                break;
            case double number:
                writer.Add(BitConverter.DoubleToInt64Bits(number));
                break;
            case DateOnly date:
                writer.Add(date.DayNumber);
                break;
            case TimeOnly time:
                writer.Add(time.Ticks);
                break;
            case DateTime dateTime:
                writer.Add(dateTime.Ticks);
                writer.Add((int)dateTime.Kind);
                break;
            case DateTimeOffset dateTimeOffset:
                writer.Add(dateTimeOffset.Ticks);
                writer.Add(dateTimeOffset.Offset.Ticks);
                break;
            case TimeSpan duration:
                writer.Add(duration.Ticks);
                break;
            case Guid guid:
                writer.Add(guid.ToByteArray());
                break;
            case byte[] bytes:
                writer.Add(bytes);
                break;
            case Type type:
                writer.Add(type.FullName);
                break;
            case Enum enumeration:
                writer.Add(Convert.ToString(enumeration, CultureInfo.InvariantCulture));
                break;
            case IDictionary dictionary:
                WriteDictionary(writer, dictionary, context);
                break;
            case IEnumerable sequence:
                WriteSequence(writer, sequence, context);
                break;
            default:
                throw new NotSupportedException(
                    $"The {context} value type '{value.GetType().FullName}' is not supported by model fingerprint format {Version}.");
        }
    }

    private static void WriteDictionary(
        CanonicalHashWriter writer,
        IDictionary dictionary,
        string context
    )
    {
        var values = new List<KeyValuePair<string, object?>>(dictionary.Count);
        var enumerator = dictionary.GetEnumerator();

        try
        {
            while (enumerator.MoveNext())
            {
                values.Add(new KeyValuePair<string, object?>(CanonicalKey(enumerator.Key, context), enumerator.Value));
            }
        }
        finally
        {
            if (enumerator is IDisposable disposableEnumerator)
            {
                disposableEnumerator.Dispose();
            }
        }

        var entries = values
            .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
            .ToArray();

        writer.Add(entries.Length);
        foreach (var entry in entries)
        {
            writer.Add(entry.Key);
            WriteValue(writer, entry.Value, context);
        }
    }

    private static void WriteSequence(
        CanonicalHashWriter writer,
        IEnumerable sequence,
        string context
    )
    {
        var values = sequence
            .Cast<object?>()
            .ToArray();
        writer.Add(values.Length);
        foreach (var value in values)
        {
            WriteValue(writer, value, context);
        }
    }

    private static string CanonicalKey(
        object? key,
        string context
    ) => key switch
    {
        null => "null",
        string text => "string:" + text,
        Type type => "type:" + type.FullName,
        Enum enumeration => "enum:"
            + enumeration.GetType().FullName
            + ":"
            + Convert.ToString(enumeration, CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.GetType().FullName
            + ":"
            + formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(
            $"The {context} dictionary key type '{key.GetType().FullName}' is not supported by model fingerprint format {Version}."),
    };
}
