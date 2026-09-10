namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>Specifies which report entries are written to an operator-facing report view.</summary>
public enum SafeMigrationReportSelection
{
    /// <summary>Includes every assessment and unexpected object from the source report.</summary>
    Complete = 1,

    /// <summary>Excludes only safe assessments that have fully converged.</summary>
    NonMatching = 2,

    /// <summary>Includes only assessments that block the source report's migration phase.</summary>
    BlockingOnly = 3,
}
