namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationModelManagedValue
{
    public static object? Clone(
        object? value
    )
    {
        Validate(value);

        return value is byte[] bytes ? bytes.ToArray() : value;
    }

    public static bool AreEqual(
        object? left,
        object? right
    ) => left switch
    {
        byte[] leftBytes when right is byte[] rightBytes => leftBytes.AsSpan().SequenceEqual(rightBytes),
        _ => object.Equals(left, right),
    };

    public static void Write(
        CanonicalHashWriter writer,
        object? value
    )
    {
        ArgumentNullException.ThrowIfNull(writer);
        Validate(value);

        if (value is null)
        {
            writer.Add("null");
            return;
        }

        // The CLR type marker and culture-independent binary encoding keep
        // values such as 1, 1L, and "1" in distinct canonical hash domains.
        writer.Add(value.GetType());
        switch (value)
        {
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
                writer.Add(unchecked((int)number));
                break;
            case long number:
                writer.Add(number);
                break;
            case ulong number:
                writer.Add(unchecked((long)number));
                break;
            case decimal number:
                foreach (var part in decimal.GetBits(number))
                {
                    writer.Add(part);
                }

                break;
            case float number:
                writer.Add(BitConverter.SingleToInt32Bits(number));
                break;
            case double number:
                writer.Add(BitConverter.DoubleToInt64Bits(number));
                break;
            case string text:
                writer.Add(text);
                break;
            case char character:
                writer.Add((int)character);
                break;
            case byte[] bytes:
                writer.Add(bytes);
                break;
            case Guid guid:
                Span<byte> guidBytes = stackalloc byte[16];
                _ = guid.TryWriteBytes(guidBytes, bigEndian: true, out _);
                writer.Add(guidBytes.ToArray());
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
            case TimeSpan timeSpan:
                writer.Add(timeSpan.Ticks);
                break;
            case Enum enumeration:
                WriteEnum(writer, enumeration);
                break;
            default:
                throw new UnreachableException();
        }
    }

    private static void Validate(
        object? value
    )
    {
        if (value is null
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
            or string
            or char
            or byte[]
            or Guid
            or DateOnly
            or TimeOnly
            or DateTime
            or DateTimeOffset
            or TimeSpan
            or Enum)
        {
            return;
        }

        throw new ArgumentException(
            $"Model-managed values of CLR type '{value.GetType().FullName}' are not supported.",
            nameof(value));
    }

    private static void WriteEnum(
        CanonicalHashWriter writer,
        Enum value
    )
    {
        var underlyingType = Enum.GetUnderlyingType(value.GetType());
        var underlyingValue = Convert.ChangeType(value, underlyingType, CultureInfo.InvariantCulture);

        Write(writer, underlyingValue);
    }
}
