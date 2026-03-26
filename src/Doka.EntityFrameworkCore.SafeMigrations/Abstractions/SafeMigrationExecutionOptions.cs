namespace Doka.EntityFrameworkCore.SafeMigrations;

/// <summary>
/// Describes the execution strategy for the extended safe migration pipeline.
/// </summary>
/// <param name="ConflictMode">Controls how existing conflicting objects are handled.</param>
/// <param name="PreflightOnly">When <see langword="true"/>, analyzes the operation without emitting DDL.</param>
public sealed record SafeMigrationExecutionOptions
(
    SafeMigrationConflictMode ConflictMode,
    bool PreflightOnly = false
);
