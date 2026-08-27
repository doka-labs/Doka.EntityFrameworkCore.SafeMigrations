namespace Doka.EntityFrameworkCore.SafeMigrations;

public static partial class SafeMigrationContractFingerprint
{
    private static void WriteIntent(
        CanonicalHashWriter writer,
        EnsureColumnIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        WriteColumn(writer, intent.Definition);
    }

    private static void WriteIntent(
        CanonicalHashWriter writer,
        DropColumnIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        writer.Add(intent.Name);
    }

    private static void WriteIntent(
        CanonicalHashWriter writer,
        RenameColumnIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        writer.Add(intent.Name);
        writer.Add(intent.NewName);
    }

    private static void WriteIntent(
        CanonicalHashWriter writer,
        AlterColumnIntent intent
    )
    {
        WriteTableIdentity(writer, intent.Table, intent.Schema);
        WriteColumn(writer, intent.Definition);
        writer.Add(intent.OldDefinition is not null);
        if (intent.OldDefinition is not null)
        {
            WriteColumn(writer, intent.OldDefinition);
        }
    }

    private static void WriteColumn(
        CanonicalHashWriter writer,
        ExpectedColumnDefinition definition
    )
    {
        writer.Add(definition.Name);
        writer.Add(definition.ClrType.FullName ?? definition.ClrType.Name);
        writer.Add(definition.IsNullable);
        writer.Add(definition.StoreType);
        writer.Add(definition.IsUnicode);
        writer.Add(definition.MaxLength);
        writer.Add(definition.IsFixedLength);
        writer.Add(definition.IsRowVersion);
        writer.Add(definition.Precision);
        writer.Add(definition.Scale);
        WriteCollation(writer, definition.Collation);
        writer.Add(definition.Comment);

        WriteDefault(writer, definition.DefaultValue);

        writer.Add(definition.ComputedColumnSql);
        SafeMigrationSqlExpressionContract.Write(writer, definition.ComputedExpression);
        writer.Add(definition.IsStored);

        writer.Add(definition.ProviderAnnotations.Count);
        foreach (var annotation in definition.ProviderAnnotations)
        {
            writer.Add(annotation.Name);
            writer.Add(annotation.Fingerprint);
        }
    }

    private static void WriteCollation(
        CanonicalHashWriter writer,
        SafeMigrationCollationIdentifier? collation
    )
    {
        writer.Add(collation is not null);
        if (collation is null)
        {
            return;
        }

        writer.Add(collation.Schema);
        writer.Add(collation.Name);
    }

    private static void WriteDefault(
        CanonicalHashWriter writer,
        SafeMigrationDefaultValue value
    )
    {
        writer.Add((int)value.Kind);
        if (value.Kind == SafeMigrationDefaultValueKind.Sql)
        {
            writer.Add(value.SqlExpression);
            SafeMigrationSqlExpressionContract.Write(writer, value.StructuredExpression);
            return;
        }

        if (value.Kind != SafeMigrationDefaultValueKind.Literal)
        {
            return;
        }

        var literal = value.GetLiteralValue();
        if (literal is null)
        {
            writer.Add("null");
            return;
        }

        writer.Add(literal.GetType().FullName ?? literal.GetType().Name);
        switch (literal)
        {
            case byte[] bytes:
                writer.Add(bytes);
                break;
            case bool boolean:
                writer.Add(boolean);
                break;
            case float single:
                writer.Add(single.ToString("R", CultureInfo.InvariantCulture));
                break;
            case double number:
                writer.Add(number.ToString("R", CultureInfo.InvariantCulture));
                break;
            case decimal number:
                writer.Add(number.ToString("G29", CultureInfo.InvariantCulture));
                break;
            case DateOnly date:
                writer.Add(date.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeOnly time:
                writer.Add(time.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTime dateTime:
                writer.Add(dateTime.ToString("O", CultureInfo.InvariantCulture));
                break;
            case DateTimeOffset dateTimeOffset:
                writer.Add(dateTimeOffset.ToString("O", CultureInfo.InvariantCulture));
                break;
            case TimeSpan timeSpan:
                writer.Add(timeSpan.ToString("c", CultureInfo.InvariantCulture));
                break;
            case Guid guid:
                writer.Add(guid.ToString("D"));
                break;
            case Enum enumeration:
                writer.Add(Convert.ToString(enumeration, CultureInfo.InvariantCulture));
                break;
            default:
                writer.Add(Convert.ToString(literal, CultureInfo.InvariantCulture));
                break;
        }
    }
}
