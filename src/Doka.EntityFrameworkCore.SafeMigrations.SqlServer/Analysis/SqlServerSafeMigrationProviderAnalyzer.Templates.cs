namespace Doka.EntityFrameworkCore.SafeMigrations.SqlServer;

internal sealed partial class SqlServerSafeMigrationProviderAnalyzer
{
    /// <summary>Shares immutable classifier text within one bounded statement, never live evidence or parameters.</summary>
    private sealed class DelayedCatalogTemplates
    {
        private readonly Dictionary<(string Template, string Definitions), string> _variables = [];

        /// <summary>Prepares a dispatcher without publishing a template until payload admission succeeds.</summary>
        public DelayedCatalogSelection Prepare(int ordinal, SqlServerSafeMigrationRuntimePlan plan, int slot)
        {
            var definitions = BuildDelayedParameterDefinitions(plan);
            var template = BuildDelayedCatalogTemplate(plan, definitions);
            var identity = (template, definitions);
            var shared = _variables.TryGetValue(identity, out var variable);
            variable ??= "@doka_template" + _variables.Count.ToString(CultureInfo.InvariantCulture);
            var invocation = BuildDelayedCatalogInvocation(ordinal, plan, slot, variable, definitions);

            // WHY: Template sharing must not admit an individually oversized classifier. Measure the
            // equivalent escaped literal invocation without allocating a second heavyweight SQL buffer.
            var literalBytes = checked(Encoding.UTF8.GetByteCount(template) + 3);
            foreach (var character in template)
            {
                if (character == '\'')
                {
                    literalBytes = checked(literalBytes + 1);
                }
            }

            var standaloneBytes = checked(literalBytes + Encoding.UTF8.GetByteCount(invocation)
                - Encoding.UTF8.GetByteCount(variable));

            var declaration = shared ? null
                : "DECLARE " + variable + " nvarchar(max) = N'"
                    + template.Replace("'", "''", StringComparison.Ordinal) + "';\n";

            return new DelayedCatalogSelection(template, definitions, variable, invocation, declaration,
                standaloneBytes);
        }

        /// <summary>Publishes only a newly admitted template in the current statement's private namespace.</summary>
        public void Accept(DelayedCatalogSelection selection)
        {
            if (selection.Declaration is not null)
            {
                _variables.Add((selection.Template, selection.Definitions), selection.Variable);
            }
        }
    }

    /// <summary>Carries a prepared invocation and its exact, independently validated payload contribution.</summary>
    private readonly record struct DelayedCatalogSelection(
        string Template,
        string Definitions,
        string Variable,
        string Invocation,
        string? Declaration,
        int StandaloneBytes
    );
}
