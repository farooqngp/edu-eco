-- Development only (never executed outside DOTNET_ENVIRONMENT=Development).
-- Code must match EduEco.Database.Seeders.DevUserSeeder.DemoTenantCode.
SET XACT_ABORT ON;

IF NOT EXISTS (SELECT 1 FROM [dbo].[Tenants] WHERE [Code] = N'demo-school')
    INSERT INTO [dbo].[Tenants] ([Code], [Name], [IsActive], [CreatedAtUtc], [CreatedBy])
    VALUES (N'demo-school', N'Demo School', 1, SYSDATETIMEOFFSET(), N'system:dev-seed');
