namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Identifies safe operations whose resource state is replaced by a later safe
/// operation before postflight observes the final database catalog.
/// </summary>
internal sealed class SafeMigrationPostflightProjection
{
    private readonly HashSet<int> _supersededOrdinals = [];

    /// <summary>Creates the final-writer projection for one ordered migration operation stream.</summary>
    /// <param name="operations">The complete operation stream in execution order.</param>
    public SafeMigrationPostflightProjection(
        IReadOnlyList<MigrationOperation> operations
    )
    {
        var finalWriters = new HashSet<PostflightResource>();

        // Postflight observes only the final catalog. Walk backwards so the
        // final safe writer for one exact resource remains authoritative while
        // earlier transient states cannot make an ordered replacement fail.
        // Ordinary provider operations never enter this reduction because
        // SafeMigrations does not own or infer their final effects.
        for (var ordinal = operations.Count - 1; ordinal >= 0; ordinal--)
        {
            if (operations[ordinal] is not SafeMigrationOperation safeOperation
                || !TryCreateResource(safeOperation.Intent, out var resource))
            {
                continue;
            }

            if (!finalWriters.Add(resource))
            {
                _supersededOrdinals.Add(ordinal);
            }
        }
    }

    /// <summary>Determines whether a later safe operation owns the final state of the same resource.</summary>
    /// <param name="ordinal">The zero-based operation position in the projected stream.</param>
    /// <returns>
    /// <see langword="true" /> when postflight must evaluate a later safe writer instead of this operation;
    /// otherwise, <see langword="false" />.
    /// </returns>
    public bool IsSuperseded(
        int ordinal
    ) => _supersededOrdinals.Contains(ordinal);

    private static bool TryCreateResource(
        SafeMigrationIntent intent,
        out PostflightResource resource
    )
    {
        resource = intent switch
        {
            EnsureSchemaIntent value => new PostflightResource(PostflightResourceKind.Schema, value.Name, null, null),
            DropSchemaIntent value => new PostflightResource(PostflightResourceKind.Schema, value.Name, null, null),
            EnsureTableIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Table,
                    value.Definition.Schema,
                    value.Definition.Table,
                    null),
            DropTableIntent value =>
                new PostflightResource(PostflightResourceKind.Table, value.Schema, value.Table, null),
            RenameTableIntent value =>
                new PostflightResource(PostflightResourceKind.Table, value.Schema, value.Name, null),
            EnsureColumnIntent value =>
                new PostflightResource(PostflightResourceKind.Column, value.Schema, value.Table, value.Definition.Name),
            DropColumnIntent value =>
                new PostflightResource(PostflightResourceKind.Column, value.Schema, value.Table, value.Name),
            RenameColumnIntent value =>
                new PostflightResource(PostflightResourceKind.Column, value.Schema, value.Table, value.Name),
            AlterColumnIntent value =>
                new PostflightResource(PostflightResourceKind.Column, value.Schema, value.Table, value.Definition.Name),
            EnsureIndexIntent value =>
                new PostflightResource(
                    PostflightResourceKind.Index,
                    value.Definition.Schema,
                    value.Definition.Table,
                    value.Definition.Name),
            DropIndexIntent value =>
                new PostflightResource(PostflightResourceKind.Index, value.Schema, value.Table, value.Name),
            RenameIndexIntent value =>
                new PostflightResource(PostflightResourceKind.Index, value.Schema, value.Table, value.Name),
            EnsurePrimaryKeyIntent value =>
                new PostflightResource(
                    PostflightResourceKind.PrimaryKey,
                    value.Definition.Schema,
                    value.Definition.Table,
                    null),
            DropPrimaryKeyIntent value =>
                new PostflightResource(PostflightResourceKind.PrimaryKey, value.Schema, value.Table, null),
            EnsureUniqueConstraintIntent value =>
                new PostflightResource(
                    PostflightResourceKind.UniqueConstraint,
                    value.Definition.Schema,
                    value.Definition.Table,
                    value.Definition.Name),
            DropUniqueConstraintIntent value =>
                new PostflightResource(PostflightResourceKind.UniqueConstraint, value.Schema, value.Table, value.Name),
            EnsureCheckConstraintIntent value => new PostflightResource(
                PostflightResourceKind.CheckConstraint,
                value.Definition.Schema,
                value.Definition.Table,
                value.Definition.Name),
            DropCheckConstraintIntent value => new PostflightResource(
                PostflightResourceKind.CheckConstraint,
                value.Schema,
                value.Table,
                value.Name),
            EnsureForeignKeyIntent value => new PostflightResource(
                PostflightResourceKind.ForeignKey,
                value.Definition.Schema,
                value.Definition.Table,
                value.Definition.Name),
            DropForeignKeyIntent value => new PostflightResource(
                PostflightResourceKind.ForeignKey,
                value.Schema,
                value.Table,
                value.Name),
            _ => default,
        };

        return resource.Kind != PostflightResourceKind.Unknown;
    }

    private enum PostflightResourceKind
    {
        Unknown,
        Schema,
        Table,
        Column,
        Index,
        PrimaryKey,
        UniqueConstraint,
        CheckConstraint,
        ForeignKey,
    }

    private readonly record struct PostflightResource(
        PostflightResourceKind Kind,
        string? Schema,
        string? Table,
        string? Name
    );
}
