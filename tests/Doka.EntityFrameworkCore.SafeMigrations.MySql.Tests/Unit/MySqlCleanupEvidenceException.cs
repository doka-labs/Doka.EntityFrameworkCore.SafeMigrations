namespace Doka.EntityFrameworkCore.SafeMigrations.MySql.Tests;

/// <summary>Models the two legal Exception.Data extension points that reject additional cleanup evidence.</summary>
internal sealed class MySqlCleanupEvidenceException : Exception
{
    private readonly IDictionary _data = new Dictionary<object, object?>().AsReadOnly();
    private readonly bool _throwing;

    /// <summary>Selects a throwing getter or an actually read-only diagnostic dictionary.</summary>
    /// <param name="throwing">Whether diagnostic access itself throws instead of returning read-only data.</param>
    internal MySqlCleanupEvidenceException(
        bool throwing
    )
    {
        _throwing = throwing;
    }

    /// <summary>Gets the actual diagnostic dictionary access count.</summary>
    internal int DataAccesses { get; private set; }

    /// <inheritdoc />
    public override IDictionary Data
    {
        get
        {
            DataAccesses++;
            if (_throwing)
            {
                throw new InvalidOperationException("The native exception rejects diagnostic dictionary access.");
            }

            return _data;
        }
    }
}
