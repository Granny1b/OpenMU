-- DRY RUN for 03-seed-cleanup.sql. Deletes nothing.
--
--   psql -U postgres -d openmu -f 03-seed-cleanup-dryrun.sql
--
-- Run this FIRST, on any server that has real players. 03-seed-cleanup.sql matches
-- `"LoginName" ~ '^test[0-9]+$'` as well as the twenty fixed names, and that pattern would also
-- match a real account someone registered as "test42". The cleanup script prints its list and
-- deletes in a single transaction, so by the time the list reaches your screen the DELETE has
-- already committed - there is no chance to abort. This file is that chance.
--
-- Read the output. Every row is about to be deleted, along with its characters
-- (FK_Character_Account_AccountId is ON DELETE CASCADE). If a row is one of yours, rename that
-- account before running the cleanup.

\set ON_ERROR_STOP on

SELECT a."LoginName",
       a."State",
       a."RegistrationDate",
       (SELECT count(*) FROM data."Character" c WHERE c."AccountId" = a."Id") AS characters,
       CASE WHEN a."LoginName" IN ('test0','test1','test2','test3','test4','test5','test6','test7',
                                   'test8','test9','test300','test400','ancient','socket',
                                   'quest1','quest2','quest3','testgm','testgm2','testunlock')
            THEN 'seeded name'
            ELSE 'MATCHED BY PATTERN - check this is not a real player'
       END AS why
  FROM data."Account" a
 WHERE a."LoginName" IN ('test0','test1','test2','test3','test4','test5','test6','test7','test8','test9',
                         'test300','test400','ancient','socket','quest1','quest2','quest3',
                         'testgm','testgm2','testunlock')
    OR a."LoginName" ~ '^test[0-9]+$'
 -- Pattern-matched rows first: those are the ones that need a human decision. Sorting on `why`
 -- would leave that to the collation, which orders case differently from one locale to the next.
 ORDER BY (a."LoginName" IN ('test0','test1','test2','test3','test4','test5','test6','test7',
                             'test8','test9','test300','test400','ancient','socket',
                             'quest1','quest2','quest3','testgm','testgm2','testunlock')) ASC,
          a."LoginName";
