using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EduEco.Database.Migrator;

/// <summary>
/// Immutability guard for RunOnce migrations: <c>Scripts/manifest.json</c> pins a SHA-256 per migration script.
/// Editing an already-registered script, or adding one without registering it, fails the build tests and the migrator.
/// Hashes use LF-normalised content so Windows/Linux checkouts agree.
/// </summary>
internal static class ScriptManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IReadOnlyDictionary<string, string> LoadEmbedded(Assembly assembly)
    {
        var resource = $"{assembly.GetName().Name}.Scripts.manifest.json";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded resource '{resource}' not found.");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }

    public static string ComputeHash(string contents)
    {
        var normalised = contents.Replace("\r\n", "\n", StringComparison.Ordinal).TrimStart('﻿');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)));
    }

    /// <summary>Returns human-readable violations; empty when the manifest matches the embedded migration scripts.</summary>
    public static IReadOnlyList<string> Verify(IReadOnlyList<ScriptFile> scripts, IReadOnlyDictionary<string, string> manifest)
    {
        var migrations = scripts.Where(s => s.Stage.Folder == ScriptStages.MigrationsFolder).ToList();
        var violations = new List<string>();

        foreach (var script in migrations)
        {
            if (!manifest.TryGetValue(script.Name, out var expected))
            {
                violations.Add($"Unregistered migration '{script.Name}' (add to Scripts/manifest.json).");
            }
            else if (!string.Equals(expected, ComputeHash(script.Contents), StringComparison.OrdinalIgnoreCase))
            {
                violations.Add($"Migration '{script.Name}' changed after registration. Never edit released migrations; add a new V-script.");
            }
        }

        violations.AddRange(manifest.Keys
            .Except(migrations.Select(m => m.Name), StringComparer.Ordinal)
            .Select(name => $"Manifest entry '{name}' has no matching script (migrations must never be deleted)."));

        return violations;
    }

    public static string Render(IReadOnlyList<ScriptFile> scripts) =>
        JsonSerializer.Serialize(
            scripts.Where(s => s.Stage.Folder == ScriptStages.MigrationsFolder)
                .OrderBy(s => s.Name, StringComparer.Ordinal)
                .ToDictionary(s => s.Name, s => ComputeHash(s.Contents)),
            JsonOptions);
}
