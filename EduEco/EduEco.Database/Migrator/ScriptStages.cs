using DbUp.Support;

namespace EduEco.Database.Migrator;

internal sealed record ScriptStage(string Folder, ScriptType ScriptType, int RunGroupOrder, bool DevelopmentOnly = false);

/// <summary>
/// Execution pipeline. Order: stage (RunGroupOrder) then script name (ordinal).
/// RunOnce scripts are journaled in <c>migration.SchemaVersions</c>; RunAlways scripts must be idempotent.
/// </summary>
internal static class ScriptStages
{
    public const string MigrationsFolder = "01_Migrations";

    public static IReadOnlyList<ScriptStage> All { get; } =
    [
        new("00_PreDeploy", ScriptType.RunAlways, 0),
        new(MigrationsFolder, ScriptType.RunOnce, 1),
        new("02_ReferenceData", ScriptType.RunAlways, 2),
        new("03_PostDeploy", ScriptType.RunAlways, 3),
        new("99_Dev", ScriptType.RunOnce, 99, DevelopmentOnly: true),
    ];

    public static IReadOnlyList<ScriptStage> For(bool isDevelopment) =>
        [.. All.Where(s => isDevelopment || !s.DevelopmentOnly)];
}
