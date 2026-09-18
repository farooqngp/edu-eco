-- RunAlways: fail fast on unsupported engines before any migration executes.
SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @EngineEdition int = CAST(SERVERPROPERTY('EngineEdition') AS int);
DECLARE @MajorVersion int = CAST(PARSENAME(CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)), 4) AS int);

-- 5 = Azure SQL Database, 8 = Azure SQL Managed Instance (always current).
IF @EngineEdition NOT IN (5, 8) AND @MajorVersion < 15
    THROW 50001, 'EduEco requires SQL Server 2019 (15.x) or later.', 1;

IF DB_NAME() IN (N'master', N'model', N'msdb', N'tempdb')
    THROW 50002, 'Refusing to deploy EduEco schema into a system database.', 1;
