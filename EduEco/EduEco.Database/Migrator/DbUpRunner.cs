using System.Text;
using DbUp;
using DbUp.Engine;
using EduEco.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace EduEco.Database.Migrator;

/// <summary>Builds and runs the DbUp upgrade engine. The only component allowed to execute DDL.</summary>
internal sealed partial class DbUpRunner(
    IOptions<DatabaseOptions> databaseOptions,
    IHostEnvironment environment,
    ILogger<DbUpRunner> logger)
{
    public const string JournalSchema = "migration";
    public const string JournalTable = "SchemaVersions";

    private readonly string _connectionString = databaseOptions.Value.ConnectionString;

    /// <summary>Returns <c>true</c> on success.</summary>
    public bool Run(MigratorCommand command)
    {
        var stages = ScriptStages.For(environment.IsDevelopment());

        var assembly = typeof(DbUpRunner).Assembly;
        var violations = ScriptManifest.Verify(EmbeddedScriptProvider.Load(assembly, stages), ScriptManifest.LoadEmbedded(assembly));
        if (violations.Count > 0)
        {
            foreach (var violation in violations)
            {
                LogManifestViolation(logger, violation);
            }

            return false;
        }

        if (command.IsReadOnly)
        {
            return ReportPending(stages, command.ScriptOutPath);
        }

        if (command.EnsureDatabase)
        {
            LogEnsuringDatabase(logger, DatabaseName);
            EnsureDatabase.For.SqlDatabase(_connectionString);
        }

        EnsureJournalSchema();

        var result = BuildEngine(stages).PerformUpgrade();
        if (!result.Successful)
        {
            LogScriptFailed(logger, result.Error, result.ErrorScript?.Name ?? "<unknown>");
            return false;
        }

        LogUpgradeSucceeded(logger, result.Scripts.Count(), DatabaseName, environment.EnvironmentName);
        return true;
    }

    private string DatabaseName => new SqlConnectionStringBuilder(_connectionString).InitialCatalog;

    private UpgradeEngine BuildEngine(IReadOnlyList<ScriptStage> stages) =>
        DeployChanges.To
            .SqlDatabase(_connectionString)
            .WithScripts(new EmbeddedScriptProvider(typeof(DbUpRunner).Assembly, stages))
            .JournalToSqlTable(JournalSchema, JournalTable)
            .WithTransactionPerScript()
            .WithExecutionTimeout(TimeSpan.FromMinutes(15))
            .WithVariablesDisabled()
            .LogToConsole()
            .Build();

    private bool ReportPending(IReadOnlyList<ScriptStage> stages, string? scriptOutPath)
    {
        IReadOnlyList<(string Name, string Contents)> pending;

        if (DatabaseExists())
        {
            pending = [.. BuildEngine(stages).GetScriptsToExecute().Select(s => (s.Name, s.Contents))];
        }
        else
        {
            LogDatabaseMissing(logger, DatabaseName);
            pending = [.. EmbeddedScriptProvider.Load(typeof(DbUpRunner).Assembly, stages).Select(s => (s.Name, s.Contents))];
        }

        LogPendingCount(logger, pending.Count);
        foreach (var (name, _) in pending)
        {
            LogPendingScript(logger, name);
        }

        if (scriptOutPath is not null)
        {
            var sql = new StringBuilder()
                .AppendLine($"-- EduEco pending migration scripts for [{DatabaseName}] ({environment.EnvironmentName})")
                .AppendLine($"-- Generated {DateTimeOffset.UtcNow:O}. Review only: apply through the migrator so the journal is updated.")
                .AppendLine();

            foreach (var (name, contents) in pending)
            {
                sql.AppendLine($"-- ==== {name} ====").AppendLine(contents.TrimEnd()).AppendLine("GO").AppendLine();
            }

            File.WriteAllText(scriptOutPath, sql.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            LogScriptOut(logger, Path.GetFullPath(scriptOutPath));
        }

        return true;
    }

    private bool DatabaseExists()
    {
        var master = new SqlConnectionStringBuilder(_connectionString) { InitialCatalog = "master" };
        using var connection = new SqlConnection(master.ConnectionString);
        using var sqlCommand = new SqlCommand("SELECT DB_ID(@name)", connection);
        sqlCommand.Parameters.AddWithValue("@name", DatabaseName);
        connection.Open();
        return sqlCommand.ExecuteScalar() is not (null or DBNull);
    }

    private void EnsureJournalSchema()
    {
        using var connection = new SqlConnection(_connectionString);
        using var sqlCommand = new SqlCommand(
            $"IF SCHEMA_ID(N'{JournalSchema}') IS NULL EXEC(N'CREATE SCHEMA [{JournalSchema}] AUTHORIZATION [dbo]');",
            connection);
        connection.Open();
        sqlCommand.ExecuteNonQuery();
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Script manifest violation: {Violation}")]
    private static partial void LogManifestViolation(ILogger logger, string violation);

    [LoggerMessage(Level = LogLevel.Information, Message = "Ensuring database {Database} exists")]
    private static partial void LogEnsuringDatabase(ILogger logger, string database);

    [LoggerMessage(Level = LogLevel.Error, Message = "Database upgrade failed in script {Script}")]
    private static partial void LogScriptFailed(ILogger logger, Exception? exception, string script);

    [LoggerMessage(Level = LogLevel.Information, Message = "Database upgrade succeeded: {Count} script(s) executed on {Database} ({Environment})")]
    private static partial void LogUpgradeSucceeded(ILogger logger, int count, string database, string environment);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Database {Database} does not exist; all scripts are pending")]
    private static partial void LogDatabaseMissing(ILogger logger, string database);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} pending script(s)")]
    private static partial void LogPendingCount(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "  pending: {Script}")]
    private static partial void LogPendingScript(ILogger logger, string script);

    [LoggerMessage(Level = LogLevel.Information, Message = "Pending scripts written to {Path}")]
    private static partial void LogScriptOut(ILogger logger, string path);
}
