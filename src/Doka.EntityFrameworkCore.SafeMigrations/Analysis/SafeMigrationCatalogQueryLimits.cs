namespace Doka.EntityFrameworkCore.SafeMigrations;

internal static class SafeMigrationCatalogQueryLimits
{
    public const int MaximumMySqlOperations = 512;
    public const int MaximumPostgreSqlOperations = 128;
    public const int MaximumInventoryValues = 512;
    public const int MaximumParameters = 16_000;
    public const int MaximumUtf8PayloadBytes = 4 * 1024 * 1024;
    public const string Separator = "\nUNION ALL\n";
    public const string Trailer = "\nORDER BY 1;";

    public static bool Exceeded(
        int parameters,
        int utf8PayloadBytes,
        int maximumUtf8PayloadBytes
    )
    {
        ArgumentOutOfRangeException.ThrowIfNegative(parameters);
        ArgumentOutOfRangeException.ThrowIfNegative(utf8PayloadBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUtf8PayloadBytes);

        return parameters > MaximumParameters || utf8PayloadBytes > maximumUtf8PayloadBytes;
    }

    public static int MySqlMaximumUtf8PayloadBytes(
        long maximumPacketBytes
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPacketBytes, 2);

        return (int)Math.Min(MaximumUtf8PayloadBytes, Math.Min(int.MaxValue, maximumPacketBytes / 2));
    }

    public static InvalidOperationException OversizedOperation(
        int ordinal,
        int parameters,
        int utf8PayloadBytes
    ) => new(
        "SafeMigrations catalog classification operation "
        + ordinal.ToString(CultureInfo.InvariantCulture)
        + " exceeds a bounded query limit (parameters="
        + parameters.ToString(CultureInfo.InvariantCulture)
        + ", utf8_payload_bytes="
        + utf8PayloadBytes.ToString(CultureInfo.InvariantCulture)
        + ").");
}
