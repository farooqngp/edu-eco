-- Tenants (schools / districts). Dapper entity: EduEco.Core.Tenants.Tenant.
SET XACT_ABORT ON;

CREATE TABLE [dbo].[Tenants] (
    [Id] bigint NOT NULL IDENTITY(1, 1),
    [Code] nvarchar(50) NOT NULL,
    [Name] nvarchar(200) NOT NULL,
    [IsActive] bit NOT NULL CONSTRAINT [DF_Tenants_IsActive] DEFAULT (1),
    [CreatedAtUtc] datetimeoffset(7) NOT NULL,
    [CreatedBy] nvarchar(256) NOT NULL,
    [UpdatedAtUtc] datetimeoffset(7) NULL,
    [UpdatedBy] nvarchar(256) NULL,
    [RowVersion] rowversion NOT NULL,
    CONSTRAINT [PK_Tenants] PRIMARY KEY CLUSTERED ([Id]),
    CONSTRAINT [UQ_Tenants_Code] UNIQUE NONCLUSTERED ([Code])
);
