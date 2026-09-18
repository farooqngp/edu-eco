using Dapper;
using EduEco.Core.Authorization;
using EduEco.Database.Migrator;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace EduEco.Infrastructure.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public sealed class MigrationTests(SqlServerFixture fixture)
{
    [Fact]
    public async Task Migrates_empty_database_and_journals_only_run_once_scripts()
    {
        fixture.FirstMigrationSucceeded.ShouldBeTrue();

        await using var connection = new SqlConnection(fixture.ConnectionString);
        var journaled = (await connection.QueryAsync<string>(
            $"SELECT ScriptName FROM [{DbUpRunner.JournalSchema}].[{DbUpRunner.JournalTable}]")).ToList();

        journaled.ShouldBe(
        [
            "01_Migrations/V0001__create_schemas.sql",
            "01_Migrations/V0002__auth_identity_tables.sql",
            "01_Migrations/V0003__auth_openiddict_tables.sql",
            "01_Migrations/V0004__tenants.sql",
            "01_Migrations/V0005__permissions_memberships.sql",
            "01_Migrations/V0006__auth_user_passkeys.sql",
            "01_Migrations/V0007__bff_sessions.sql",
        ], ignoreOrder: true);
    }

    [Fact]
    public void Rerun_is_idempotent() =>
        fixture.Services.GetRequiredService<DbUpRunner>().Run(new MigratorCommand()).ShouldBeTrue();

    [Fact]
    public async Task All_primary_keys_are_integer_and_clustered_with_no_guid_columns()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);

        (await connection.ExecuteScalarAsync<int>(
            """
            SELECT COUNT(*)
            FROM sys.columns c
            INNER JOIN sys.tables tb ON tb.object_id = c.object_id
            INNER JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE t.name = 'uniqueidentifier' AND tb.is_ms_shipped = 0
            """))
            .ShouldBe(0);

        var nonIntegerPkColumns = (await connection.QueryAsync<(string Column, string IndexType)>("""
            SELECT OBJECT_SCHEMA_NAME(i.object_id) + '.' + OBJECT_NAME(i.object_id) + '.' + c.name, i.type_desc
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            INNER JOIN sys.types t ON t.user_type_id = c.user_type_id
            WHERE i.is_primary_key = 1 AND OBJECT_SCHEMA_NAME(i.object_id) IN ('dbo', 'auth', 'bff')
              AND c.name LIKE '%Id' AND t.name NOT IN ('bigint', 'int')
            """)).ToList();

        // Only allowed non-integer key: WebAuthn credential id (authenticator-generated natural key, not a surrogate).
        // It must never be the clustering key (random values → page splits).
        nonIntegerPkColumns.ShouldBe([("auth.AspNetUserPasskeys.CredentialId", "NONCLUSTERED")]);

        var surrogateKeysNotClusteredIdentity = await connection.QueryAsync<string>("""
            SELECT OBJECT_SCHEMA_NAME(c.object_id) + '.' + OBJECT_NAME(c.object_id)
            FROM sys.indexes i
            INNER JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            INNER JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
            WHERE i.is_primary_key = 1 AND c.name = 'Id' AND OBJECT_SCHEMA_NAME(c.object_id) IN ('dbo', 'auth', 'bff')
              AND (i.type_desc <> 'CLUSTERED' OR (c.is_identity = 0 AND OBJECT_NAME(c.object_id) <> 'Permissions'))
            """);
        surrogateKeysNotClusteredIdentity.ShouldBeEmpty();
    }

    [Fact]
    public async Task Custom_roles_get_ids_above_reserved_system_range()
    {
        await using var scope = fixture.Services.CreateAsyncScope();
        var roles = scope.ServiceProvider.GetRequiredService<Microsoft.AspNetCore.Identity.RoleManager<Core.Identity.ApplicationRole>>();

        var role = new Core.Identity.ApplicationRole { Name = $"custom-{Guid.NewGuid():N}" };
        (await roles.CreateAsync(role)).Succeeded.ShouldBeTrue();

        role.Id.ShouldBeGreaterThanOrEqualTo(1000);
    }

    [Fact]
    public async Task Reference_data_is_seeded()
    {
        await using var connection = new SqlConnection(fixture.ConnectionString);

        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth.Permissions")).ShouldBe(Permissions.All.Count);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth.AspNetRoles WHERE IsSystem = 1")).ShouldBe(Roles.All.Count);
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM auth.RolePermissions"))
            .ShouldBe(Roles.All.Sum(r => r.Permissions.Count));
        (await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Tenants WHERE Code = 'demo-school'"))
            .ShouldBe(0, "99_Dev scripts must not run outside Development");
    }
}
