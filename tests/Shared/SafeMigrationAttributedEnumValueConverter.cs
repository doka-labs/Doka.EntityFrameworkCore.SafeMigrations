namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

internal static class SafeMigrationAttributedEnumName
{
    public static TEnum Parse<TEnum>(
        string value
    ) where TEnum : struct, Enum
    {
        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            var member = Member(candidate);
            var jsonName = member.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name;
            if (StringComparer.OrdinalIgnoreCase.Equals(jsonName, value))
            {
                return candidate;
            }

            var contractName = member.GetCustomAttribute<EnumMemberAttribute>()?.Value;
            if (StringComparer.OrdinalIgnoreCase.Equals(contractName, value))
            {
                return candidate;
            }
        }

        // This mirrors the application converter: database values unknown to
        // the enum map to its default. IsComplete(false) must keep such rows out
        // of hierarchy queries before EF invokes the converter.
        return default;
    }

    public static string Format<TEnum>(
        TEnum value
    ) where TEnum : struct, Enum
    {
        var member = Member(value);

        return member.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
            ?? member.GetCustomAttribute<EnumMemberAttribute>()?.Value
            ?? value.ToString();
    }

    public static int MaximumLength<TEnum>(
        int defaultMaximumLength = 0
    ) where TEnum : struct, Enum
    {
        var maximumLength = defaultMaximumLength;
        foreach (var name in Enum.GetNames<TEnum>())
        {
            maximumLength = Math.Max(maximumLength, name.Length);
        }

        foreach (var value in Enum.GetValues<TEnum>())
        {
            maximumLength = Math.Max(maximumLength, Format(value).Length);
        }

        return maximumLength;
    }

    private static MemberInfo Member<TEnum>(
        TEnum value
    ) where TEnum : struct, Enum
    {
        var members = typeof(TEnum).GetMember(value.ToString());

        return members.Length == 0
            ? throw new InvalidOperationException(
                $"Enum value '{value}' has no corresponding member on '{typeof(TEnum).FullName}'.")
            : members[0];
    }
}

internal sealed class SafeMigrationAttributedEnumValueConverter<TEnum> : ValueConverter<TEnum, string>
    where TEnum : struct, Enum
{
    public SafeMigrationAttributedEnumValueConverter()
        : base(
            value => SafeMigrationAttributedEnumName.Format(value),
            value => SafeMigrationAttributedEnumName.Parse<TEnum>(value)) { }
}
