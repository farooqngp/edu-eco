-- RunAlways, idempotent. Least-privilege database role for application logins (Api, Identity).
-- DBAs map environment-specific users to this role; the migrator login alone holds DDL rights.
SET NOCOUNT ON;
SET XACT_ABORT ON;

IF DATABASE_PRINCIPAL_ID(N'eduEco_app') IS NULL
    CREATE ROLE [eduEco_app] AUTHORIZATION [dbo];

GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[dbo] TO [eduEco_app];
GRANT SELECT, INSERT, UPDATE, DELETE, EXECUTE ON SCHEMA::[auth] TO [eduEco_app];

IF SCHEMA_ID(N'bff') IS NOT NULL
    GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[bff] TO [eduEco_app];
