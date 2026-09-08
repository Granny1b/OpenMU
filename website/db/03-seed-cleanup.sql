-- Remove OpenMU's seeded development accounts.
--
--   psql -U postgres -d openmu -f 03-seed-cleanup.sql
--
-- RE-RUN after every `-reinit`: src/Startup/Program.cs calls CreateInitialDataAsync(3, true), which
-- recreates all twenty.
--
-- Every one of these ships with password == login name
-- (VersionSeasonSix/TestAccounts/AccountInitializerBase.cs:92), and three of them
-- (testgm, testgm2, testunlock) ship with State = 2 (GameMaster).
--
-- The website refuses all twenty at login regardless of this script - see Game/SeedAccounts.cs -
-- but that does nothing for the GAME client, where test400/test400 is a free geared level-400
-- character. This script is what closes that.
--
-- DELETE cascades: FK_Character_Account_AccountId is ON DELETE CASCADE
-- (Migrations/00000000000000_Initial.cs:1116), so an account's characters go with it.
--
-- RUN 03-seed-cleanup-dryrun.sql FIRST on any server that has real players. The SELECT below is
-- inside the same transaction as the DELETE, so its output reaches you only after the delete has
-- committed - it is a record, not a confirmation prompt. The `^test[0-9]+$` pattern would also
-- match a real account registered as "test42".

\set ON_ERROR_STOP on

BEGIN;

-- Show what is about to be removed, so the transcript records it.
SELECT "LoginName", "State", "RegistrationDate"
  FROM data."Account"
 WHERE "LoginName" IN ('test0','test1','test2','test3','test4','test5','test6','test7','test8','test9',
                       'test300','test400','ancient','socket','quest1','quest2','quest3',
                       'testgm','testgm2','testunlock')
    OR "LoginName" ~ '^test[0-9]+$'
 ORDER BY "LoginName";

DELETE FROM data."Account"
 WHERE "LoginName" IN ('test0','test1','test2','test3','test4','test5','test6','test7','test8','test9',
                       'test300','test400','ancient','socket','quest1','quest2','quest3',
                       'testgm','testgm2','testunlock')
    OR "LoginName" ~ '^test[0-9]+$';   -- catches a future change to the test0..testN loop bound

-- Must return zero.
SELECT count(*) AS remaining_gm_seeds
  FROM data."Account"
 WHERE "LoginName" IN ('testgm','testgm2','testunlock');

COMMIT;
