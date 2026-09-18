SET XACT_ABORT ON;

-- auth: ASP.NET Core Identity, OpenIddict, permissions, tenant memberships.
IF SCHEMA_ID(N'auth') IS NULL
    EXEC (N'CREATE SCHEMA [auth] AUTHORIZATION [dbo];');
