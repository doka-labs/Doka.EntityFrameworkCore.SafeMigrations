namespace Doka.EntityFrameworkCore.SafeMigrations.Sample;

internal sealed class UserRecord
{
    public Guid Id { get; set; }

    public string Email { get; set; } = string.Empty;

    public string? DisplayName { get; set; }
}

internal sealed class OrderRecord
{
    public Guid Id { get; set; }

    public Guid UserId { get; set; }

    public decimal Total { get; set; }
}
