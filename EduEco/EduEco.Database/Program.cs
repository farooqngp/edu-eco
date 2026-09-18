using System.Text;
using EduEco.Database.Migrator;
using EduEco.Database.Seeders;
using EduEco.Infrastructure.Persistence.Auth;
using EduEco.ServiceRegistry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var command = MigratorCommand.Parse(args, out var error);
if (command is null)
{
    if (!string.IsNullOrEmpty(error))
    {
        await Console.Error.WriteLineAsync(error);
    }

    Console.WriteLine(MigratorCommand.Usage);
    return string.IsNullOrEmpty(error) ? 0 : 2;
}

if (command.Verb == MigratorVerb.PrintManifest)
{
    // Includes every stage so the manifest is environment-independent.
    Console.WriteLine(ScriptManifest.Render(EmbeddedScriptProvider.Load(typeof(DbUpRunner).Assembly, ScriptStages.All)));
    return 0;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

// Args are parsed above and deliberately not passed to the configuration system.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = [],
    ContentRootPath = AppContext.BaseDirectory,
});

builder.Services.AddEduEcoPersistence(builder.Configuration);
builder.Services.AddEduEcoAuthStores(builder.Configuration);
builder.Services.AddEduEcoSystemContext("migrator");
builder.Services.AddOptions<SeedOptions>().Bind(builder.Configuration.GetSection(SeedOptions.SectionName));
builder.Services.AddSingleton<DbUpRunner>();
builder.Services.AddScoped<OpenIddictClientSeeder>();
builder.Services.AddScoped<DevUserSeeder>();

using var host = builder.Build();

try
{
    if (command.Verb == MigratorVerb.GenerateAuthDdl)
    {
        await using var ddlScope = host.Services.CreateAsyncScope();
        var ddl = ddlScope.ServiceProvider.GetRequiredService<AuthDbContext>().Database.GenerateCreateScript();

        if (command.OutputPath is null)
        {
            Console.WriteLine(ddl);
        }
        else
        {
            await File.WriteAllTextAsync(command.OutputPath, ddl, new UTF8Encoding(false), cancellation.Token);
        }

        return 0;
    }

    if (!host.Services.GetRequiredService<DbUpRunner>().Run(command))
    {
        return 1;
    }

    if (command.SeedClients)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OpenIddictClientSeeder>().SeedAsync(cancellation.Token);
    }

    if (command.SeedDevUsers)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DevUserSeeder>().SeedAsync(cancellation.Token);
    }

    return 0;
}
catch (OperationCanceledException)
{
    await Console.Error.WriteLineAsync("Cancelled.");
    return 130;
}
#pragma warning disable CA1031 // Top-level boundary: report any failure as a non-zero exit code for CI/CD.
catch (Exception ex)
#pragma warning restore CA1031
{
    await Console.Error.WriteLineAsync($"Migrator failed: {ex}");
    return 1;
}
