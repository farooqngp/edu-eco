-- Invitee's email address, so the invite code can be emailed at issuance (in addition to the API response).
-- Nullable here for migration safety on existing rows; required at the API request layer instead.
SET XACT_ABORT ON;

ALTER TABLE [auth].[TenantInvites] ADD [InviteeEmail] nvarchar(256) NULL;
