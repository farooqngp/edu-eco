using EduEco.Database.Migrator;
using Shouldly;

namespace EduEco.Database.Tests;

public sealed class MigratorCommandTests
{
    [Fact]
    public void No_args_defaults_to_migrate()
    {
        var command = MigratorCommand.Parse([], out var error);

        error.ShouldBeNull();
        command.ShouldNotBeNull().Verb.ShouldBe(MigratorVerb.Migrate);
        command.IsReadOnly.ShouldBeFalse();
    }

    [Fact]
    public void Parses_migrate_flags()
    {
        var command = MigratorCommand.Parse(["migrate", "--ensure-db", "--seed-clients", "--seed-dev-users"], out _).ShouldNotBeNull();

        command.EnsureDatabase.ShouldBeTrue();
        command.SeedClients.ShouldBeTrue();
        command.SeedDevUsers.ShouldBeTrue();
    }

    [Fact]
    public void Script_out_is_read_only()
    {
        var command = MigratorCommand.Parse(["--script-out", "pending.sql"], out _).ShouldNotBeNull();

        command.ScriptOutPath.ShouldBe("pending.sql");
        command.IsReadOnly.ShouldBeTrue();
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("--script-out")]
    [InlineData("--unknown")]
    public void Rejects_invalid_arguments(string arg)
    {
        MigratorCommand.Parse([arg], out var error).ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Rejects_dry_run_with_seeding()
    {
        MigratorCommand.Parse(["--dry-run", "--seed-clients"], out var error).ShouldBeNull();
        error.ShouldNotBeNullOrEmpty();
    }
}
