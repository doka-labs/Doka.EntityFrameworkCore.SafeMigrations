namespace Doka.EntityFrameworkCore.SafeMigrations.Testing;

internal static class SafeMigrationLiteralContract
{
    public static object?[] CreateRepresentativeValues() =>
    [
        null,
        true,
        (byte)1,
        (sbyte)-1,
        (short)-2,
        (ushort)2,
        -3,
        (uint)3,
        -4L,
        4UL,
        1.25m,
        1.5f,
        1.75d,
        "value",
        'x',
        new byte[] { 1 },
        Guid.Empty,
        new DateOnly(2026, 8, 17),
        new TimeOnly(12, 30),
        DateTime.UnixEpoch,
        DateTimeOffset.UnixEpoch,
        TimeSpan.FromMinutes(1),
        DayOfWeek.Monday,
    ];

    public static Type[] CreateSupportedNonNullTypes() => CreateRepresentativeValues()
        .Where(static value => value is not null)
        .Select(static value => value!.GetType())
        .Distinct()
        .OrderBy(static type => type.FullName, StringComparer.Ordinal)
        .ToArray();
}
