-- A stand-in for the OpenMU schema, with exactly the schema, table and column names taken from the
-- EF model snapshot. CI applies this to a throwaway PostgreSQL so PublicQueries' SQL is executed for
-- real: a typo, a wrong column name or a construct PostgreSQL rejects fails the build instead of a
-- player's page.
--
-- The seed data deliberately contains every hazard the queries must survive:
--   * a character with TWO Level rows (no unique index exists on (CharacterId, DefinitionId))
--   * a character with NO master level row at all
--   * a character with no stat rows whatsoever
--   * a character whose "AccountId" is null
--   * banned, temporarily banned, bot and template accounts, each holding a top-level character
--   * an empty guild

DROP SCHEMA IF EXISTS data CASCADE;
DROP SCHEMA IF EXISTS config CASCADE;
DROP SCHEMA IF EXISTS guild CASCADE;
CREATE SCHEMA data; CREATE SCHEMA config; CREATE SCHEMA guild;

CREATE TABLE data."Account" (
  "Id" uuid PRIMARY KEY, "ChatBanUntil" timestamptz, "EMail" text NOT NULL,
  "IsBot" boolean NOT NULL DEFAULT false, "IsTemplate" boolean NOT NULL DEFAULT false,
  "IsVaultExtended" boolean NOT NULL DEFAULT false, "LanguageIsoCode" varchar(3) NOT NULL DEFAULT 'en',
  "LoginName" varchar(10) NOT NULL UNIQUE, "PasswordHash" text NOT NULL,
  "RegistrationDate" timestamptz NOT NULL, "SecurityCode" text NOT NULL, "State" integer NOT NULL,
  "TimeZone" smallint NOT NULL, "VaultId" uuid, "VaultPassword" text NOT NULL);

CREATE TABLE config."CharacterClass" (
  "Id" uuid PRIMARY KEY, "Number" smallint NOT NULL, "Name" text NOT NULL,
  "CanGetCreated" boolean NOT NULL DEFAULT true);

CREATE TABLE config."AttributeDefinition" (
  "Id" uuid PRIMARY KEY, "Designation" text NOT NULL, "Description" text);

CREATE TABLE config."GameMapDefinition" (
  "Id" uuid PRIMARY KEY, "Name" text NOT NULL, "Number" smallint);

CREATE TABLE data."Character" (
  "Id" uuid PRIMARY KEY,
  "AccountId" uuid REFERENCES data."Account"("Id") ON DELETE CASCADE,
  "CharacterClassId" uuid NOT NULL, "CharacterSlot" smallint,
  "CharacterStatus" integer NOT NULL DEFAULT 0, "CreateDate" timestamptz NOT NULL DEFAULT now(),
  "CurrentMapId" uuid, "Experience" bigint NOT NULL DEFAULT 0,
  "MasterExperience" bigint NOT NULL DEFAULT 0, "Name" text NOT NULL UNIQUE,
  "PlayerKillCount" integer NOT NULL DEFAULT 0, "State" integer NOT NULL DEFAULT 0);

CREATE TABLE data."StatAttribute" (
  "Id" uuid PRIMARY KEY, "AccountId" uuid,
  "CharacterId" uuid REFERENCES data."Character"("Id") ON DELETE CASCADE,
  "DefinitionId" uuid, "Value" real NOT NULL);

CREATE TABLE guild."Guild" (
  "Id" uuid PRIMARY KEY, "Name" text NOT NULL UNIQUE, "Score" integer NOT NULL DEFAULT 0, "Notice" text);

CREATE TABLE guild."GuildMember" (
  "Id" uuid PRIMARY KEY, "GuildId" uuid NOT NULL, "Status" smallint NOT NULL DEFAULT 1);

INSERT INTO config."CharacterClass"("Id","Number","Name") VALUES
 ('00000000-0000-0000-0000-000000000007',7,'Blade Master'),
 ('00000000-0000-0000-0000-00000000000b',11,'High Elf'),
 ('00000000-0000-0000-0000-000000000003',3,'Grand Master');

INSERT INTO config."AttributeDefinition"("Id","Designation") VALUES
 ('560931ad-0901-4342-b7f4-fd2e2fcc0563','Level'),
 ('70cd8c10-391a-4c51-9aa4-a854600e3a9f','Master Level'),
 ('89a891a7-f9f9-4ab5-af36-12056e53a5f7','Resets'),
 ('123282fe-fead-448e-ad2c-baece939b4b1','Base Strength'),
 ('1ae9c014-e3cd-4703-bd05-1b65f5f94ceb','Base Agility'),
 ('6ca5c3a6-b109-45a5-87a7-fdcb107b4982','Base Vitality'),
 ('01b0ef28-f7a0-46b5-97ba-2b624a54cd75','Base Energy'),
 ('6af2c9df-3ae4-4721-8462-9a8ec7f56fe4','Base Leadership');

INSERT INTO config."GameMapDefinition"("Id","Name","Number") VALUES
 ('00000000-0000-0000-0000-0000000000f8','Tarkan',8);

INSERT INTO data."Account"("Id","EMail","LoginName","PasswordHash","RegistrationDate","SecurityCode","State","TimeZone","VaultPassword","IsBot","IsTemplate") VALUES
 ('a0000000-0000-0000-0000-000000000001','','valdrenn','h',now(),'',0,0,'',false,false),
 ('a0000000-0000-0000-0000-000000000002','','sirenya','h',now(),'',2,0,'',false,false),
 ('a0000000-0000-0000-0000-000000000003','','banned1','h',now(),'',4,0,'',false,false),
 ('a0000000-0000-0000-0000-000000000004','','tempban','h',now(),'',5,0,'',false,false),
 ('a0000000-0000-0000-0000-000000000005','','botacct','h',now(),'',0,0,'',true,false),
 ('a0000000-0000-0000-0000-000000000006','','template','h',now(),'',0,0,'',false,true);

INSERT INTO data."Character"("Id","AccountId","CharacterClassId","Name","CharacterStatus","State","Experience","MasterExperience","PlayerKillCount","CurrentMapId") VALUES
 ('c0000000-0000-0000-0000-000000000001','a0000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000007','Valdrenn',0,1,999,50,12,'00000000-0000-0000-0000-0000000000f8'),
 ('c0000000-0000-0000-0000-000000000002','a0000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-00000000000b','Sirenya',32,3,900,40,0,NULL),
 ('c0000000-0000-0000-0000-000000000003','a0000000-0000-0000-0000-000000000003','00000000-0000-0000-0000-000000000007','BannedChar',0,3,800,0,99,NULL),
 ('c0000000-0000-0000-0000-000000000004','a0000000-0000-0000-0000-000000000004','00000000-0000-0000-0000-000000000007','TempBanChar',0,3,700,0,98,NULL),
 ('c0000000-0000-0000-0000-000000000005','a0000000-0000-0000-0000-000000000005','00000000-0000-0000-0000-000000000007','BotChar',0,3,600,0,97,NULL),
 ('c0000000-0000-0000-0000-000000000006','a0000000-0000-0000-0000-000000000006','00000000-0000-0000-0000-000000000007','TemplateChar',0,3,500,0,96,NULL),
 ('c0000000-0000-0000-0000-000000000007','a0000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-000000000003','NoStats',0,0,0,0,0,NULL),
 ('c0000000-0000-0000-0000-000000000008',NULL,'00000000-0000-0000-0000-000000000003','Orphaned',0,0,0,0,0,NULL);

INSERT INTO data."StatAttribute"("Id","CharacterId","DefinitionId","Value") VALUES
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000001','560931ad-0901-4342-b7f4-fd2e2fcc0563',400),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000001','560931ad-0901-4342-b7f4-fd2e2fcc0563',399),  -- duplicate
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000001','70cd8c10-391a-4c51-9aa4-a854600e3a9f',220),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000001','89a891a7-f9f9-4ab5-af36-12056e53a5f7',37),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000001','123282fe-fead-448e-ad2c-baece939b4b1',18400),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000001','1ae9c014-e3cd-4703-bd05-1b65f5f94ceb',9850),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000001','6ca5c3a6-b109-45a5-87a7-fdcb107b4982',3100),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000001','01b0ef28-f7a0-46b5-97ba-2b624a54cd75',1250),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000002','560931ad-0901-4342-b7f4-fd2e2fcc0563',400),  -- no master row
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000002','89a891a7-f9f9-4ab5-af36-12056e53a5f7',36),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000003','560931ad-0901-4342-b7f4-fd2e2fcc0563',401),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000004','560931ad-0901-4342-b7f4-fd2e2fcc0563',402),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000005','560931ad-0901-4342-b7f4-fd2e2fcc0563',403),
 (gen_random_uuid(),'c0000000-0000-0000-0000-000000000006','560931ad-0901-4342-b7f4-fd2e2fcc0563',404);

INSERT INTO guild."Guild"("Id","Name","Score","Notice") VALUES
 ('90000000-0000-0000-0000-000000000001','Ironveil',41280,'Siege practice Thursdays'),
 ('90000000-0000-0000-0000-000000000002','Nightfall',12000,NULL),
 ('90000000-0000-0000-0000-000000000003','EmptyGuild',0,NULL);

INSERT INTO guild."GuildMember"("Id","GuildId","Status") VALUES
 ('c0000000-0000-0000-0000-000000000001','90000000-0000-0000-0000-000000000001',2),
 ('c0000000-0000-0000-0000-000000000002','90000000-0000-0000-0000-000000000001',4),
 ('c0000000-0000-0000-0000-000000000003','90000000-0000-0000-0000-000000000002',1);

-- =================================================================================================
-- The GM console's reference catalogue, in the `config` schema.
--
-- Column TYPES matter here and are copied from
-- src/Persistence/EntityFramework/Migrations/00000000000000_Initial.cs: every C# `byte` property is
-- PostgreSQL `smallint`, which Npgsql reports as Int16. A fixture that used `integer` would let a
-- record declaring `byte` pass here and fail against the real database - which is exactly how the
-- character page and the news feed both shipped broken.
-- =================================================================================================

CREATE TABLE config."MonsterDefinition" (
    "Id" uuid PRIMARY KEY, "Number" smallint NOT NULL, "Designation" text NOT NULL,
    "MoveRange" smallint NOT NULL DEFAULT 0, "AttackRange" smallint NOT NULL DEFAULT 0,
    "ViewRange" smallint NOT NULL DEFAULT 0, "Attribute" smallint NOT NULL DEFAULT 0,
    "NumberOfMaximumItemDrops" integer NOT NULL DEFAULT 0,
    "NpcWindow" integer NOT NULL DEFAULT 0, "ObjectKind" integer NOT NULL DEFAULT 0,
    "IntelligenceTypeName" text
);
CREATE TABLE config."MonsterAttribute" (
    "Id" uuid PRIMARY KEY, "MonsterDefinitionId" uuid, "AttributeDefinitionId" uuid,
    "Value" real NOT NULL
);
CREATE TABLE config."MonsterSpawnArea" (
    "Id" uuid PRIMARY KEY, "MonsterDefinitionId" uuid, "GameMapId" uuid,
    "X1" smallint NOT NULL, "Y1" smallint NOT NULL, "X2" smallint NOT NULL, "Y2" smallint NOT NULL,
    "Direction" integer NOT NULL DEFAULT 0, "Quantity" smallint NOT NULL DEFAULT 1,
    "SpawnTrigger" integer NOT NULL DEFAULT 0, "WaveNumber" smallint NOT NULL DEFAULT 0,
    "MaximumHealthOverride" integer
);
CREATE TABLE config."ItemDefinition" (
    "Id" uuid PRIMARY KEY, "Number" smallint NOT NULL, "Group" smallint NOT NULL,
    "Name" text NOT NULL, "Width" smallint NOT NULL DEFAULT 1, "Height" smallint NOT NULL DEFAULT 1,
    "DropsFromMonsters" boolean NOT NULL DEFAULT true, "IsAmmunition" boolean NOT NULL DEFAULT false,
    "IsBoundToCharacter" boolean NOT NULL DEFAULT false, "IsQuestItem" boolean NOT NULL DEFAULT false,
    "DropLevel" smallint NOT NULL DEFAULT 0, "MaximumDropLevel" smallint,
    "MaximumItemLevel" smallint NOT NULL DEFAULT 0, "Durability" smallint NOT NULL DEFAULT 0,
    "Value" integer NOT NULL DEFAULT 0, "MaximumSockets" integer NOT NULL DEFAULT 0,
    "StorageLimitPerCharacter" integer NOT NULL DEFAULT 0, "PetExperienceFormula" text
);


-- GameMapDefinition is declared above with "Number" already; the console additionally reads the
-- experience rate.
ALTER TABLE config."GameMapDefinition" ADD COLUMN IF NOT EXISTS "ExpMultiplier" double precision NOT NULL DEFAULT 1;

-- ---- catalogue data ----------------------------------------------------------------------------
-- Seeded with the hazards the queries must survive: a monster carrying a DUPLICATE level row (MAX
-- must win and it must stay ONE row), a monster with NO attributes at all (must still list, at
-- level 0, rather than vanish), a point spawn and a box spawn, and a quest item.
 ------------------------------------------------------------------------------------
-- Tarkan is already inserted further up for the character page - reuse it rather than adding a
-- second row with the same number, which would make every "monsters on map 8" assertion ambiguous.
UPDATE config."GameMapDefinition" SET "ExpMultiplier" = 1.0
 WHERE "Id" = '00000000-0000-0000-0000-0000000000f8';

INSERT INTO config."GameMapDefinition" ("Id","Name","Number","ExpMultiplier") VALUES
  ('a0000000-0000-0000-0000-00000000000a','Icarus',10,1.5)
ON CONFLICT ("Id") DO NOTHING;

-- Monsters: a golden with stats and two spawn areas, an ordinary one, and one with NO attributes
-- at all (the hazard: it must still list, at level 0, not vanish).
INSERT INTO config."MonsterDefinition" ("Id","Number","Designation","ObjectKind") VALUES
  ('b0000000-0000-0000-0000-000000000001', 78, 'Golden Tantallos', 0),
  ('b0000000-0000-0000-0000-000000000002', 45, 'Iron Wheel',       0),
  ('b0000000-0000-0000-0000-000000000003',253, 'Statueless Monster',0);

INSERT INTO config."MonsterAttribute" ("Id","MonsterDefinitionId","AttributeDefinitionId","Value") VALUES
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000001','560931AD-0901-4342-B7F4-FD2E2FCC0563',104),
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000001','A6C39A5C-295F-415E-A314-5E9F9A748D27',35000),
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000001','3E8D6A02-E973-4AE4-9DF3-CDDC3D3183B3',310),
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000001','8A918EA2-893A-48B2-A684-3E71526CA71F',340),
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000001','EB098C46-60D4-4CA6-BBD4-5B6270A1407B',200),
  -- A DUPLICATE level row, higher: MAX must win and the monster must stay one row.
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000001','560931AD-0901-4342-B7F4-FD2E2FCC0563',106),
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000002','560931AD-0901-4342-B7F4-FD2E2FCC0563',64),
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000002','A6C39A5C-295F-415E-A314-5E9F9A748D27',6500);

INSERT INTO config."MonsterSpawnArea"
  ("Id","MonsterDefinitionId","GameMapId","X1","Y1","X2","Y2","Quantity","SpawnTrigger") VALUES
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000001','00000000-0000-0000-0000-0000000000f8',120,80,140,100,3,0),
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000001','a0000000-0000-0000-0000-00000000000a', 60,40, 60,40,1,1),
  (gen_random_uuid(),'b0000000-0000-0000-0000-000000000002','00000000-0000-0000-0000-0000000000f8', 20,20, 40,40,10,0);

INSERT INTO config."ItemDefinition"
  ("Id","Group","Number","Name","MaximumItemLevel","MaximumSockets","DropLevel","MaximumDropLevel","Width","Height","Durability","IsQuestItem","DropsFromMonsters") VALUES
  (gen_random_uuid(),14, 13,'Jewel of Bless',      0,0, 25, NULL,1,1, 1,false,true),
  (gen_random_uuid(),14, 14,'Jewel of Soul',       0,0, 25, NULL,1,1, 1,false,true),
  (gen_random_uuid(),14, 16,'Jewel of Life',       0,0, 25, NULL,1,1, 1,false,true),
  (gen_random_uuid(), 0, 16,'Dragon Slayer',      15,5,118,  130,2,4,50,false,true),
  (gen_random_uuid(),13, 20,'Quest Scroll',        0,0,  1, NULL,1,2, 1,true, false);

