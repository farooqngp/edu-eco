using System.Reflection;
using DbUp.Engine;
using DbUp.Engine.Transactions;

namespace EduEco.Database.Migrator;

internal sealed record ScriptFile(string Name, string Contents, ScriptStage Stage);

/// <summary>
/// Loads embedded SQL scripts and names them <c>{stage}/{file}</c> (e.g. <c>01_Migrations/V0001__create_schemas.sql</c>),
/// so journal entries are independent of assembly/namespace names and build OS.
/// </summary>
internal sealed class EmbeddedScriptProvider(Assembly assembly, IReadOnlyList<ScriptStage> stages) : IScriptProvider
{
    public IEnumerable<SqlScript> GetScripts(IConnectionManager connectionManager) =>
        Load(assembly, stages).Select(s => new SqlScript(
            s.Name,
            s.Contents,
            new SqlScriptOptions { ScriptType = s.Stage.ScriptType, RunGroupOrder = s.Stage.RunGroupOrder }));

    public static IReadOnlyList<ScriptFile> Load(Assembly assembly, IReadOnlyList<ScriptStage> stages)
    {
        var resourceRoot = $"{assembly.GetName().Name}.Scripts.";
        var resources = assembly.GetManifestResourceNames();
        var scripts = new List<ScriptFile>();

        foreach (var stage in stages)
        {
            // MSBuild prefixes folder segments starting with a digit with '_'.
            var prefix = $"{resourceRoot}_{stage.Folder}.";
            foreach (var resource in resources.Where(r => r.StartsWith(prefix, StringComparison.Ordinal)
                                                          && r.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)))
            {
                using var stream = assembly.GetManifestResourceStream(resource)
                    ?? throw new InvalidOperationException($"Embedded script '{resource}' could not be opened.");
                using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);

                scripts.Add(new ScriptFile($"{stage.Folder}/{resource[prefix.Length..]}", reader.ReadToEnd(), stage));
            }
        }

        return [.. scripts.OrderBy(s => s.Stage.RunGroupOrder).ThenBy(s => s.Name, StringComparer.Ordinal)];
    }
}
