namespace EduEco.Database.Migrator;

internal enum MigratorVerb
{
    Migrate,
    GenerateAuthDdl,
    PrintManifest,
}

/// <summary>Parsed command line. Parsed manually so flags never leak into IConfiguration.</summary>
internal sealed record MigratorCommand
{
    public const string Usage = """
        Usage:
          EduEco.Database [migrate] [--ensure-db] [--dry-run] [--script-out <file>] [--seed-clients] [--seed-dev-users]
          EduEco.Database generate-auth-ddl [--output <file>]
          EduEco.Database print-manifest

        migrate (default)   Apply pending DbUp scripts (PreDeploy, Migrations, ReferenceData, PostDeploy, Dev*).
          --ensure-db       Create the database when missing (local/Docker only; production DBs are provisioned by IaC).
          --dry-run         List pending scripts; no changes.
          --script-out      Write pending scripts to a single SQL file for DBA review; no changes.
          --seed-clients    Upsert OpenIddict scopes and client registrations from configuration (Seed:Clients).
          --seed-dev-users  Create demo users and memberships (Development environment only).
        print-manifest      Print manifest.json content for the embedded migrations (register new V-scripts).
        generate-auth-ddl   Emit CREATE script for the Identity/OpenIddict EF model (reference when authoring auth DbUp scripts).

        * 99_Dev scripts run only when DOTNET_ENVIRONMENT=Development.
        """;

    public MigratorVerb Verb { get; init; } = MigratorVerb.Migrate;

    public bool EnsureDatabase { get; init; }

    public bool DryRun { get; init; }

    public string? ScriptOutPath { get; init; }

    public bool SeedClients { get; init; }

    public bool SeedDevUsers { get; init; }

    public string? OutputPath { get; init; }

    /// <summary>Returns <c>null</c> and an error message when arguments are invalid.</summary>
    public static MigratorCommand? Parse(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        var command = new MigratorCommand();
        var index = 0;

        if (args.Count > 0 && !args[0].StartsWith("--", StringComparison.Ordinal))
        {
            switch (args[0])
            {
                case "migrate":
                    break;
                case "print-manifest":
                    command = command with { Verb = MigratorVerb.PrintManifest };
                    break;
                case "generate-auth-ddl":
                    command = command with { Verb = MigratorVerb.GenerateAuthDdl };
                    break;
                default:
                    error = $"Unknown command '{args[0]}'.";
                    return null;
            }

            index = 1;
        }

        for (; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--ensure-db":
                    command = command with { EnsureDatabase = true };
                    break;
                case "--dry-run":
                    command = command with { DryRun = true };
                    break;
                case "--seed-clients":
                    command = command with { SeedClients = true };
                    break;
                case "--seed-dev-users":
                    command = command with { SeedDevUsers = true };
                    break;
                case "--script-out" when index + 1 < args.Count:
                    command = command with { ScriptOutPath = args[++index] };
                    break;
                case "--output" when index + 1 < args.Count:
                    command = command with { OutputPath = args[++index] };
                    break;
                case "-h" or "--help":
                    error = string.Empty;
                    return null;
                default:
                    error = $"Unknown or incomplete option '{args[index]}'.";
                    return null;
            }
        }

        if (command.Verb == MigratorVerb.Migrate && (command.DryRun || command.ScriptOutPath is not null)
            && (command.SeedClients || command.SeedDevUsers))
        {
            error = "--dry-run/--script-out cannot be combined with seeding options.";
            return null;
        }

        return command;
    }

    public bool IsReadOnly => DryRun || ScriptOutPath is not null;
}
