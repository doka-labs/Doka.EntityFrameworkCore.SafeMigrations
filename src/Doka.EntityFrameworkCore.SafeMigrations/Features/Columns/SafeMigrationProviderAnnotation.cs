namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Captures one provider-owned column annotation as an immutable value and a
/// deterministic fingerprint.
/// </summary>
/// <remarks>
/// Provider annotations can alter emitted DDL. The capture boundary therefore
/// accepts only value shapes SafeMigrations can snapshot and compare without
/// observing later mutation; unknown shapes fail closed.
/// </remarks>
internal sealed class SafeMigrationProviderAnnotation
{
    private SafeMigrationProviderAnnotation(
        string name,
        object? value
    )
    {
        Name = name;
        Value = Snapshot(value, $"provider annotation '{name}'");

        using var writer = new CanonicalHashWriter();
        WriteValue(writer, Value, $"provider annotation '{name}'");
        Fingerprint = writer.GetHash();
    }

    /// <summary>Gets the deterministic fingerprint of the captured value.</summary>
    public string Fingerprint { get; }

    /// <summary>Gets the provider annotation name.</summary>
    public string Name { get; }

    /// <summary>Gets the immutable captured annotation value.</summary>
    public object? Value { get; }

    /// <summary>Creates an independent value snapshot for a generated EF operation.</summary>
    /// <returns>A value that cannot mutate the captured contract.</returns>
    public object? CreateValueSnapshot() => Snapshot(Value, $"provider annotation '{Name}'");

    /// <summary>Captures and ordinally orders every provider annotation.</summary>
    /// <param name="annotatable">The EF Core metadata source.</param>
    /// <returns>An immutable, deterministically ordered annotation snapshot.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="annotatable"/> is null.</exception>
    /// <exception cref="NotSupportedException">
    /// An annotation value cannot be captured as an immutable supported value shape.
    /// </exception>
    public static IReadOnlyList<SafeMigrationProviderAnnotation> Capture(
        IReadOnlyAnnotatable annotatable
    )
    {
        ArgumentNullException.ThrowIfNull(annotatable);

        return annotatable
            .GetAnnotations()
            .OrderBy(static annotation => annotation.Name, StringComparer.Ordinal)
            .Select(static annotation => new SafeMigrationProviderAnnotation(annotation.Name, annotation.Value))
            .ToArray();
    }

    private static object? Snapshot(
        object? value,
        string context
    ) => value switch
    {
        null => null,
        string
            or char
            or bool
            or byte
            or sbyte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong
            or decimal
            or float
            or double
            or DateOnly
            or TimeOnly
            or DateTime
            or DateTimeOffset
            or TimeSpan
            or Guid
            or Type
            or Enum => value,
        byte[] bytes => bytes.ToArray(),
        Array array => SnapshotArray(array, context),
        _ => throw new NotSupportedException(
            $"The {context} value type '{value.GetType().FullName}' cannot be captured immutably."),
    };

    private static Array SnapshotArray(
        Array source,
        string context
    )
    {
        if (source.Rank != 1
            || source.GetLowerBound(0) != 0)
        {
            throw new NotSupportedException($"The {context} array must be one-dimensional and zero-based.");
        }

        var elementType = source
                .GetType()
                .GetElementType()
            ?? throw new NotSupportedException($"The {context} array has no element type.");

        var snapshot = Array.CreateInstance(elementType, source.Length);
        for (var index = 0; index < source.Length; index++)
        {
            snapshot.SetValue(Snapshot(source.GetValue(index), context), index);
        }

        return snapshot;
    }

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

        writer.Add(value.GetType());
        switch (value)
        {
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
                writer.Add(type);
                break;
            case Enum enumeration:
                writer.Add(
                    Convert.ToString(
                        Convert.ChangeType(
                            enumeration,
                            Enum.GetUnderlyingType(enumeration.GetType()),
                            CultureInfo.InvariantCulture),
                        CultureInfo.InvariantCulture));
                break;
            case Array values:
                writer.Add(values.Length);
                foreach (var item in values)
                {
                    WriteValue(writer, item, context);
                }

                break;
            default:
                throw new NotSupportedException(
                    $"The {context} value type '{value.GetType().FullName}' cannot be fingerprinted.");
        }
    }
}
