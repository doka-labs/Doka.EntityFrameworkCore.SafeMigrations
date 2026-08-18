namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    private string BuildCustomCreateIndexSql(
        ExpectedIndexDefinition definition
    )
    {
        var builder = new StringBuilder("CREATE ");
        if (definition.Unique)
        {
            builder.Append("UNIQUE ");
        }

        builder
            .Append("INDEX ")
            .Append(_sqlGenerationHelper.DelimitIdentifier(definition.Name));

        if (definition.Method is not null)
        {
            builder
                .Append(" USING ")
                .Append(ValidateIndexMethod(definition.Method));
        }

        builder
            .Append(" ON ")
            .Append(_sqlGenerationHelper.DelimitIdentifier(definition.Table))
            .Append(" (");

        for (var index = 0; index < definition.Keys.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(", ");
            }

            var key = definition.Keys[index];
            if (key.Column is not null)
            {
                builder.Append(_sqlGenerationHelper.DelimitIdentifier(key.Column));
                if (key.PrefixLength is not null)
                {
                    builder
                        .Append('(')
                        .Append(key.PrefixLength.Value.ToString(CultureInfo.InvariantCulture))
                        .Append(')');
                }
            }
            else
            {
                builder
                    .Append("((")
                    .Append(key.Expression)
                    .Append("))");
            }

            builder.Append(key.Descending ? " DESC" : " ASC");
        }

        return builder
            .Append(");")
            .ToString();
    }

    private static string ValidateIndexMethod(
        string method
    ) => method.ToUpperInvariant() switch
    {
        "BTREE" => "BTREE",
        "HASH" => "HASH",
        _ => throw new NotSupportedException("MySQL SafeMigrations supports only BTREE and HASH index methods."),
    };
}
