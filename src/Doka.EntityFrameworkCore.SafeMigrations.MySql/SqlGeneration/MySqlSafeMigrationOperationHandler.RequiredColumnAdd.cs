namespace Doka.EntityFrameworkCore.SafeMigrations.MySql;

internal sealed partial class MySqlSafeMigrationOperationHandler
{
    /// <summary>Builds two atomic ALTER bodies sharing one exactly equivalent native resource scope.</summary>
    /// <param name="operation">The authored logical operation whose immutable target is preserved.</param>
    /// <param name="runtimePlan">The catalog-certified required ADD protocol, when eligible.</param>
    /// <param name="context">The native renderer with the original operation model.</param>
    private RequiredColumnAddBaseline? RenderRequiredColumnAddBaseline(
        SafeMigrationOperation operation,
        MySqlSafeMigrationRuntimePlan runtimePlan,
        MySqlMigrationOperationContext context
    )
    {
        if (runtimePlan.RequiredColumnAddPlan is not { } protocol || runtimePlan.IsStaticallyUnsupported)
        {
            return null;
        }

        if (operation.Intent is not EnsureColumnIntent intent)
        {
            throw new InvalidOperationException("The required-column ADD protocol requires an EnsureColumn intent.");
        }

        var nullableAdd = (AddColumnOperation)SafeMigrationStandardOperationFactory.Create(
            intent, _expressionRenderer.Render, static collation => collation.Schema is null ? collation.Name : null);

        // WHY: Keep provider metadata, including its backfill marker, intact.
        // Nullable native ADD passes validation without inventing replacement
        // data; the same atomic ALTER installs the owned non-null barrier.
        nullableAdd.IsNullable = true;
        var apply = GetBaselineBatch(RenderNativeOperation(context, nullableAdd, intent));
        var requiredModify = SafeMigrationStandardOperationFactory.CreateFullDefinitionRepair(
            intent, _expressionRenderer.Render, static collation => collation.Schema is null ? collation.Name : null,
            MySqlSafeMigrationColumnMetadata.CanSafelyConverge);

        var finish = GetBaselineBatch(RenderNativeOperation(context, requiredModify, intent));
        if (apply.Commands.Count != 1 || finish.Commands.Count != 1
            || apply.TransactionSuppressed != finish.TransactionSuppressed
            || !apply.Commands[0].SetupCommands.SequenceEqual(finish.Commands[0].SetupCommands, StringComparer.Ordinal)
            || !apply.Commands[0].CleanupCommands.SequenceEqual(finish.Commands[0].CleanupCommands,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException("Atomic required-column stages require one equivalent native scope.");
        }

        if (protocol.BackslashEscapes
            && (apply.Commands[0].SetupCommands.Count != 0 || finish.Commands[0].SetupCommands.Count != 0))
        {
            // WHY: A catalog certificate proves the current standard literal
            // mode, not an opaque provider setup's effective mode. Doka's
            // comment scope can force NO_BACKSLASH_ESCAPES before both ALTERs.
            throw new InvalidOperationException("A backslash-escape certificate cannot cross a native setup scope.");
        }

        var addBody = ValidateRequiredColumnAddNativeBody(apply.Commands[0].BodyCommand, intent,
            isModify: false, backslashEscapes: protocol.BackslashEscapes);

        var modifyBody = ValidateRequiredColumnAddNativeBody(finish.Commands[0].BodyCommand, intent,
            isModify: true, backslashEscapes: protocol.BackslashEscapes);
        var guard = _sqlGenerationHelper.DelimitIdentifier(protocol.GuardName);
        var column = _sqlGenerationHelper.DelimitIdentifier(intent.Definition.Name);
        var stageOne = addBody + ", ADD CONSTRAINT " + guard + " CHECK (" + column + " IS NOT NULL)";
        var stageTwo = modifyBody + (protocol.IsMariaDb ? ", DROP CONSTRAINT " : ", DROP CHECK ") + guard;
        var repairBody = operation.Policy == SafeMigrationPolicy.RepairIfSafe
            && runtimePlan.RepairCapability == SafeMigrationRepairCapability.Safe
                ? modifyBody
                : null;

        return new RequiredColumnAddBaseline(apply, stageOne, stageTwo, repairBody);
    }

    /// <summary>Reads protocol catalog status once after identity and prerequisite qualification.</summary>
    /// <param name="runtimePlan">The raw status expression, rendered independently of its later binding.</param>
    /// <param name="renderedParameterValues">The already rendered operation-local provider literals.</param>
    private static MySqlSafeMigrationSetupFragment BuildRequiredColumnAddStatusAssignment(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues
    ) => MySqlSafeMigrationSetupFragment.Compose(
        "SET @doka_sm_add_status = CASE WHEN @doka_sm_state IS NULL THEN (",
        runtimePlan.RenderPreparedRequiredColumnAddStatusExpression(renderedParameterValues),
        ") ELSE NULL END;");

    /// <summary>Materializes exact resume ownership only for freshly accepted Apply work.</summary>
    /// <param name="runtimePlan">The current catalog plan containing the exact nullable target and owned CHECK.</param>
    /// <param name="renderedParameterValues">The already rendered operation-local provider literals.</param>
    private static MySqlSafeMigrationSetupFragment BuildRequiredColumnAddResumeAssignment(
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues
    ) => MySqlSafeMigrationSetupFragment.Compose(
        "SET @doka_sm_required_add_resume = CASE WHEN @doka_sm_action = 'apply' THEN COALESCE((",
        runtimePlan.RenderPreparedRequiredColumnAddResumeExpression(renderedParameterValues),
        "), FALSE) ELSE FALSE END;");

    /// <summary>Keeps both atomic stages in the original logical scope and checks final state only afterward.</summary>
    /// <param name="baseline">The atomic native stages with their separately admitted ordinary repair body.</param>
    /// <param name="runtimePlan">The final target postcondition, including owned CHECK absence.</param>
    /// <param name="renderedParameterValues">The already rendered operation-local provider literals.</param>
    /// <param name="postconditionSuffix">The same correlated assertion and optional script result used by ordinary bodies.</param>
    private static string BuildRequiredColumnAddBody(
        RequiredColumnAddBaseline baseline,
        MySqlSafeMigrationRuntimePlan runtimePlan,
        IReadOnlyList<string> renderedParameterValues,
        string postconditionSuffix
    )
    {
        // WHY: This path previously materialized two hex assignments and the
        // postcondition, copied them into a growing builder, then copied the
        // complete body. Retained parts write the same bytes once at final size.
        return MySqlSafeMigrationSetupFragment.Compose(
            "SET @doka_sm_sql = CASE WHEN @doka_sm_action = 'apply' "
            + "AND NOT @doka_sm_required_add_resume THEN CONVERT(0x",
            MySqlSafeMigrationSetupFragment.Part.Hex(baseline.StageOneBody),
            PreparedSqlAssignmentSuffix,
            Environment.NewLine,
            EvaluatePreparedStatementSql,
            Environment.NewLine,
            PreparedApplySqlPrefix,
            MySqlSafeMigrationSetupFragment.Part.Hex(baseline.StageTwoBody),
            baseline.RepairBody is null ? string.Empty : PreparedRepairSqlPrefix,
            MySqlSafeMigrationSetupFragment.Part.Hex(baseline.RepairBody ?? string.Empty),
            PreparedSqlAssignmentSuffix,
            Environment.NewLine,
            EvaluatePreparedStatementSql,
            Environment.NewLine,
            PostconditionBodyPrefix,
            runtimePlan.RenderPreparedExecutionPostcondition(renderedParameterValues),
            postconditionSuffix).ToSql();
    }

    /// <summary>Admits only a native single-column ALTER before appending one owned atomic CHECK clause.</summary>
    /// <param name="body">The native body with its one terminal statement separator already normalized.</param>
    /// <param name="intent">The exact owner and column to which the native body must be bound.</param>
    /// <param name="isModify">Whether this is the required native MODIFY rather than nullable native ADD.</param>
    /// <param name="backslashEscapes">Whether catalog certifies standard string decoding without native setup.</param>
    private string ValidateRequiredColumnAddNativeBody(
        string body,
        EnsureColumnIntent intent,
        bool isModify,
        bool backslashEscapes
    )
    {
        var table = _sqlGenerationHelper.DelimitIdentifier(intent.Table, intent.Schema);
        var column = _sqlGenerationHelper.DelimitIdentifier(intent.Definition.Name);
        var prefix = "ALTER TABLE " + table + (isModify ? " MODIFY COLUMN " : " ADD ");
        if (!body.StartsWith(prefix + column + " ", StringComparison.Ordinal)
            && (isModify || !body.StartsWith(prefix + "COLUMN " + column + " ", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("Atomic required-column stages require an exact native column ALTER.");
        }

        // WHY: Native comments and quoted identifiers may contain semicolons
        // or apparent SQL. Verify boundaries outside native quoted tokens;
        // never replace text or split literals to attach the owned clause.
        var quote = '\0';
        var depth = 0;
        for (var index = 0; index < body.Length; index++)
        {
            var token = body[index];
            if (quote != '\0')
            {
                if (backslashEscapes && quote != '`' && token == '\\')
                {
                    // WHY: Only a mode-qualified string consumes its next
                    // character; native backtick identifiers still use the
                    // provider's doubled-quote boundary, never string escapes.
                    index++;

                    continue;
                }

                if (token == quote)
                {
                    if (index + 1 < body.Length && body[index + 1] == quote)
                    {
                        index++;
                    }
                    else
                    {
                        quote = '\0';
                    }
                }

                continue;
            }

            if (token is '\'' or '"' or '`')
            {
                quote = token;

                continue;
            }

            if (token == '(')
            {
                depth++;
            }
            else if (token == ')')
            {
                depth--;
            }

            if (depth < 0 || token == ';' || token == ',' && depth == 0 || token == '#'
                || index + 1 < body.Length && (token == '-' && body[index + 1] == '-'
                    || token == '/' && body[index + 1] == '*'))
            {
                throw new InvalidOperationException("Atomic required-column stages require one native ALTER clause.");
            }
        }

        if (quote != '\0' || depth != 0)
        {
            throw new InvalidOperationException("The native required-column ALTER has unbalanced quoted boundaries.");
        }

        return body;
    }

    /// <summary>Retains one native resource scope and the separately guarded atomic stage bodies.</summary>
    /// <param name="Baseline">The nullable ADD's original provider setup, cleanup and transaction contract.</param>
    /// <param name="StageOneBody">The nullable ADD with its atomically installed owned non-null CHECK.</param>
    /// <param name="StageTwoBody">The required MODIFY with its atomically removed owned CHECK.</param>
    /// <param name="RepairBody">The original native MODIFY for ordinary approved repair, without CHECK removal.</param>
    private sealed record RequiredColumnAddBaseline(
        BaselineBatch Baseline,
        string StageOneBody,
        string StageTwoBody,
        string? RepairBody
    );
}
