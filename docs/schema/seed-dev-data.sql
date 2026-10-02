-- Dev-only seed data for the `iams` database. Idempotent — safe to re-run.
--
-- Seeds:
--   1. One Activation Key user (docs/api/activation-key-authentication.md), under the
--      pre-existing "Unassigned" company (Id 11111111-1111-1111-1111-111111111111, created by
--      the Users.CompanyId column default in migration RemoveTenantAndCompanyConnections).
--   2. One Location under "Unassigned" (fixed Id so it's a stable reference for seed data).
--   3. One UserLocationAssignment binding the seed user to that Location.

BEGIN;

-- User. ActivationKeyHash = Convert.ToHexString(SHA256(UTF8(activationKey))) — see
-- Common/Security/TokenGenerator.cs. The raw key is never stored; only the hash.
--   GISO-9F6A77CD4B5F -> 255769AD5F9ECC507DEDA9CE564ECAA508DEDE5D17D7FAEB72D27ED4098B4193
INSERT INTO "Users"
  ("Id", "Username", "Email", "CompanyId", "Role", "ActivationKeyHash", "ActivationStatus",
   "SecurityStamp", "IsActive", "CreatedAtUtc")
VALUES
  (gen_random_uuid(), 'giso-9f6a77cd4b5f', NULL, '11111111-1111-1111-1111-111111111111', 200,
   '255769AD5F9ECC507DEDA9CE564ECAA508DEDE5D17D7FAEB72D27ED4098B4193',
   'NotActivated', gen_random_uuid()::text, TRUE, now())
ON CONFLICT ("ActivationKeyHash") DO NOTHING;

-- Location under "Unassigned". Fixed Id (rather than gen_random_uuid()) so the
-- UserLocationAssignment insert below has a stable target and the whole file stays idempotent
-- via ON CONFLICT ("Id").
INSERT INTO "Locations" ("Id", "CompanyId", "Region", "Name", "IsActive", "CreatedAtUtc")
VALUES ('22222222-2222-2222-2222-222222222222', '11111111-1111-1111-1111-111111111111',
        NULL, 'Main', TRUE, now())
ON CONFLICT ("Id") DO NOTHING;

-- Assign the GISO-9F6A77CD4B5F seed user to the seed Location.
INSERT INTO "UserLocationAssignments" ("Id", "UserId", "LocationId", "CreatedAtUtc")
SELECT gen_random_uuid(), u."Id", '22222222-2222-2222-2222-222222222222', now()
FROM "Users" u
WHERE u."ActivationKeyHash" = '255769AD5F9ECC507DEDA9CE564ECAA508DEDE5D17D7FAEB72D27ED4098B4193'
ON CONFLICT ("UserId", "LocationId") DO NOTHING;

COMMIT;
