using System.Globalization;
using System.Text.RegularExpressions;
using EduEco.Core.Authorization;
using EduEco.Database.Migrator;
using EduEco.Database.Seeders;
using Shouldly;

namespace EduEco.Database.Tests;

public sealed partial class ScriptIntegrityTests
{
    private static readonly IReadOnlyList<ScriptFile> Scripts =
        EmbeddedScriptProvider.Load(typeof(DbUpRunner).Assembly, ScriptStages.All);

    [Fact]
    public void Manifest_matches_embedded_migrations()
    {
        var violations = ScriptManifest.Verify(Scripts, ScriptManifest.LoadEmbedded(typeof(DbUpRunner).Assembly));

        violations.ShouldBeEmpty(
            "Run `dotnet run --project EduEco.Database -- print-manifest` only when ADDING scripts. Current:\n"
            + ScriptManifest.Render(Scripts));
    }

    [Fact]
    public void Every_stage_has_scripts_and_names_are_stage_prefixed()
    {
        foreach (var stage in ScriptStages.All)
        {
            Scripts.ShouldContain(s => s.Stage == stage, $"stage {stage.Folder} has no scripts");
        }

        Scripts.Select(s => s.Name).ShouldAllBe(name => Regex.IsMatch(name, @"^\d{2}_[A-Za-z]+/[A-Z]\d{4}__[a-z0-9_]+\.sql$"));
        Scripts.Select(s => s.Name).ShouldBeUnique();
    }

    [Fact]
    public void Migration_scripts_are_versioned_with_V_prefix_and_contiguous()
    {
        var versions = Scripts
            .Where(s => s.Stage.Folder == ScriptStages.MigrationsFolder)
            .Select(s => int.Parse(s.Name.Split('/')[1][1..5], CultureInfo.InvariantCulture))
            .ToList();

        versions.ShouldBe(Enumerable.Range(1, versions.Count).ToList());
    }

    [Fact]
    public void Permissions_script_mirrors_core_catalogue()
    {
        var rows = TupleRegex().Matches(Script("02_ReferenceData/R0002__permissions.sql"))
            .Select(m => (Id: long.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture), Name: m.Groups["name"].Value))
            .ToHashSet();

        rows.ShouldBe(Permissions.All.Select(p => (p.Id, p.Name)).ToHashSet(), ignoreOrder: true);
    }

    [Fact]
    public void Roles_script_mirrors_core_roles()
    {
        var rows = TupleRegex().Matches(Script("02_ReferenceData/R0001__roles.sql"))
            .Select(m => (Id: long.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture), Name: m.Groups["name"].Value))
            .ToHashSet();

        rows.ShouldBe(Roles.All.Select(r => (r.Id, r.Name)).ToHashSet(), ignoreOrder: true);
    }

    [Fact]
    public void Role_permission_script_mirrors_core_matrix()
    {
        var rows = PairRegex().Matches(Script("02_ReferenceData/R0003__role_permissions.sql"))
            .Select(m => (Role: long.Parse(m.Groups["role"].Value, CultureInfo.InvariantCulture), Permission: long.Parse(m.Groups["perm"].Value, CultureInfo.InvariantCulture)))
            .ToHashSet();

        var permissionIds = Permissions.All.ToDictionary(p => p.Name, p => p.Id);
        var expected = Roles.All
            .SelectMany(r => r.Permissions.Select(p => (Role: r.Id, Permission: permissionIds[p])))
            .ToHashSet();

        rows.ShouldBe(expected, ignoreOrder: true);
    }

    [Fact]
    public void Migrations_use_no_guid_columns() =>
        Scripts.Where(s => s.Stage.Folder == ScriptStages.MigrationsFolder)
            .ShouldAllBe(s => !s.Contents.Contains("uniqueidentifier", StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void System_role_ids_stay_in_reserved_range() =>
        Roles.All.ShouldAllBe(r => r.Id >= 1 && r.Id <= 999);

    [Fact]
    public void Dev_tenant_script_matches_seeder_constant() =>
        Script("99_Dev/D0001__demo_tenant.sql").ShouldContain($"N'{DevUserSeeder.DemoTenantCode}'");

    [Fact]
    public void Dev_stage_is_excluded_outside_development()
    {
        ScriptStages.For(isDevelopment: false).ShouldNotContain(s => s.DevelopmentOnly);
        ScriptStages.For(isDevelopment: true).ShouldContain(s => s.DevelopmentOnly);
    }

    [Fact]
    public void Hash_ignores_line_endings_and_bom() =>
        ScriptManifest.ComputeHash("﻿SELECT 1;\r\nGO\r\n").ShouldBe(ScriptManifest.ComputeHash("SELECT 1;\nGO\n"));

    private static string Script(string name) => Scripts.Single(s => s.Name == name).Contents;

    [GeneratedRegex(@"\((?<id>\d+),\s*N'(?<name>[^']+)'")]
    private static partial Regex TupleRegex();

    [GeneratedRegex(@"\((?<role>\d+),\s*(?<perm>\d+)\)")]
    private static partial Regex PairRegex();
}
