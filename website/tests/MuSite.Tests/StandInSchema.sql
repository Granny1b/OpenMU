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
    "StorageLimitPerCharacter" integer NOT NULL DEFAULT 0, "PetExperienceFormula" text,
    "SkillId" uuid
);

-- The option chain the /item builder reads, so it can label an item's own options instead of
-- asking a GM what "ex=63" or "anc=2" means. Same rule as above: types copied from the migration.
CREATE TABLE config."Skill" (
    "Id" uuid PRIMARY KEY, "Number" smallint NOT NULL, "Name" text NOT NULL
);
CREATE TABLE config."ItemOptionType" (
    "Id" uuid PRIMARY KEY, "Name" text NOT NULL, "Description" text,
    "IsVisible" boolean NOT NULL DEFAULT false
);
CREATE TABLE config."ItemOptionDefinition" (
    "Id" uuid PRIMARY KEY, "Name" text NOT NULL, "AddChance" real NOT NULL DEFAULT 0,
    "AddsRandomly" boolean NOT NULL DEFAULT false,
    "MaximumOptionsPerItem" integer NOT NULL DEFAULT 1
);
CREATE TABLE config."ItemDefinitionItemOptionDefinition" (
    "ItemDefinitionId" uuid NOT NULL, "ItemOptionDefinitionId" uuid NOT NULL,
    PRIMARY KEY ("ItemDefinitionId", "ItemOptionDefinitionId")
);
CREATE TABLE config."PowerUpDefinitionValue" (
    "Id" uuid PRIMARY KEY, "Value" real NOT NULL DEFAULT 0,
    "AggregateType" integer NOT NULL DEFAULT 0, "MaximumValue" real
);
CREATE TABLE config."PowerUpDefinition" (
    "Id" uuid PRIMARY KEY, "TargetAttributeId" uuid, "BoostId" uuid
);
CREATE TABLE config."AttributeRelationship" (
    "Id" uuid PRIMARY KEY, "PowerUpDefinitionValueId" uuid, "InputAttributeId" uuid,
    "TargetAttributeId" uuid, "InputOperand" real NOT NULL DEFAULT 0,
    "InputOperator" integer NOT NULL DEFAULT 0, "AggregateType" integer NOT NULL DEFAULT 0
);
CREATE TABLE config."IncreasableItemOption" (
    "Id" uuid PRIMARY KEY, "ItemOptionDefinitionId" uuid, "OptionTypeId" uuid,
    "PowerUpDefinitionId" uuid, "Number" integer NOT NULL DEFAULT 0,
    "LevelType" integer NOT NULL DEFAULT 0, "SubOptionType" integer NOT NULL DEFAULT 0,
    "Weight" smallint NOT NULL DEFAULT 1
);
CREATE TABLE config."ItemOptionOfLevel" (
    "Id" uuid PRIMARY KEY, "IncreasableItemOptionId" uuid, "PowerUpDefinitionId" uuid,
    "Level" integer NOT NULL DEFAULT 1, "RequiredItemLevel" integer NOT NULL DEFAULT 0
);
CREATE TABLE config."ItemSetGroup" (
    "Id" uuid PRIMARY KEY, "Name" text NOT NULL, "SetLevel" integer NOT NULL DEFAULT 0,
    "MinimumItemCount" integer NOT NULL DEFAULT 0, "AlwaysApplies" boolean NOT NULL DEFAULT false,
    "CountDistinct" boolean NOT NULL DEFAULT false, "OptionsId" uuid
);
CREATE TABLE config."ItemOfItemSet" (
    "Id" uuid PRIMARY KEY, "ItemDefinitionId" uuid, "ItemSetGroupId" uuid,
    "BonusOptionId" uuid, "AncientSetDiscriminator" integer NOT NULL DEFAULT 0
);
CREATE TABLE config."ItemDefinitionItemSetGroup" (
    "ItemDefinitionId" uuid NOT NULL, "ItemSetGroupId" uuid NOT NULL,
    PRIMARY KEY ("ItemDefinitionId", "ItemSetGroupId")
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

INSERT INTO config."Skill" ("Id","Number","Name") VALUES
  ('50000000-0000-0000-0000-000000000031', 49, 'Dinorant Skill');

-- Item ids are FIXED rather than generated: the option rows below hang off them.
INSERT INTO config."ItemDefinition"
  ("Id","Group","Number","Name","MaximumItemLevel","MaximumSockets","DropLevel","MaximumDropLevel","Width","Height","Durability","IsQuestItem","DropsFromMonsters","SkillId") VALUES
  ('c0000000-0000-0000-0000-000000000001',14, 13,'Jewel of Bless',      0,0, 25, NULL,1,1, 1,false,true, NULL),
  ('c0000000-0000-0000-0000-000000000002',14, 14,'Jewel of Soul',       0,0, 25, NULL,1,1, 1,false,true, NULL),
  ('c0000000-0000-0000-0000-000000000003',14, 16,'Jewel of Life',       0,0, 25, NULL,1,1, 1,false,true, NULL),
  ('c0000000-0000-0000-0000-000000000004', 0, 16,'Dragon Slayer',      15,5,118,  130,2,4,50,false,true, NULL),
  ('c0000000-0000-0000-0000-000000000005',13, 20,'Quest Scroll',        0,0,  1, NULL,1,2, 1,true, false,NULL),
  ('c0000000-0000-0000-0000-000000000006',13,  3,'Dinorant',            0,0, 60, NULL,2,2,50,false,true,
   '50000000-0000-0000-0000-000000000031');


-- ---- item options ------------------------------------------------------------------------------
-- What the /item builder has to get right, and the hazards it has to survive:
--
--   * Dragon Slayer carries EXCELLENT options (bit field), an ORDINARY option (a level), a LUCK
--     option (neither - it must not leak into either list), and TWO ancient sets.
--   * Its excellent option 5 has NO constant value: the bonus is an AttributeRelationship to Total
--     Level. A page reading only the constant would print "+0", which is worse than saying nothing.
--   * The Dinorant's `opt` is a THREE-BIT FIELD, not a level, because its skill number is 49.
--   * Jewel of Bless has no options at all - every query must return empty, not throw.
--   * "Sylph Wind Set" is an ItemOfItemSet row for the Dragon Slayer whose group is NOT in
--     ItemDefinitionItemSetGroup. The command ignores such a set, so the page must too.
-- -------------------------------------------------------------------------------------------------

INSERT INTO config."AttributeDefinition"("Id","Designation") VALUES
  ('9d9761ef-ef47-4e5c-8106-ebc555786f20','Damage Receive Multiplier'),
  ('466bbbba-c1d8-45dc-8832-2eaa1130acfd','Maximum Ability'),
  ('da08473f-df5b-444d-8651-9edb65797922','Attack Speed Any'),
  ('0f9f5c9a-0000-4000-8000-000000000001','Physical Base Dmg'),
  ('0f9f5c9a-0000-4000-8000-000000000002','Excellent Damage Chance'),
  ('0f9f5c9a-0000-4000-8000-000000000003','Total Level'),
  ('0f9f5c9a-0000-4000-8000-000000000004','Total Strength'),
  ('0f9f5c9a-0000-4000-8000-000000000005','Critical Damage Chance')
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO config."ItemOptionType" ("Id","Name","IsVisible") VALUES
  ('6487c498-58e0-48e5-b409-35d7598313fc','Excellent Option', true),
  ('f193f91e-86d7-4456-add8-a3667e731303','Option',           false),
  ('3e3e9be8-4e16-4f27-a7cf-986d48454d76','Luck (Critical Damage Chance 5%)', false),
  ('5e2c10ef-e580-48d5-a48b-0ffcd0678966','Ancient Bonus Option', false);

INSERT INTO config."ItemOptionDefinition" ("Id","Name","MaximumOptionsPerItem") VALUES
  ('d0000000-0000-0000-0000-000000000001','Excellent Physical Attack Options', 2),
  ('d0000000-0000-0000-0000-000000000002','Physical Base Dmg Option',          1),
  ('d0000000-0000-0000-0000-000000000003','Luck',                              1),
  ('d0000000-0000-0000-0000-000000000004','Dinorant Options',                  3),
  ('d0000000-0000-0000-0000-000000000005','Ancient Bonus of Total Strength',   1);

INSERT INTO config."ItemDefinitionItemOptionDefinition" ("ItemDefinitionId","ItemOptionDefinitionId") VALUES
  ('c0000000-0000-0000-0000-000000000004','d0000000-0000-0000-0000-000000000001'),
  ('c0000000-0000-0000-0000-000000000004','d0000000-0000-0000-0000-000000000002'),
  ('c0000000-0000-0000-0000-000000000004','d0000000-0000-0000-0000-000000000003'),
  ('c0000000-0000-0000-0000-000000000006','d0000000-0000-0000-0000-000000000004');

-- Boost values. AggregateType: 0 AddRaw, 1 Multiplicate.
INSERT INTO config."PowerUpDefinitionValue" ("Id","Value","AggregateType") VALUES
  ('e1000000-0000-0000-0000-000000000003', 7,     0),   -- exc 3: attack speed +7
  ('e1000000-0000-0000-0000-000000000004', 1.02,  1),   -- exc 4: damage x1.02
  ('e1000000-0000-0000-0000-000000000005', 0,     0),   -- exc 5: relationship only
  ('e1000000-0000-0000-0000-000000000006', 0.1,   0),   -- exc 6: exc damage chance +10%
  ('e1000000-0000-0000-0000-000000000011', 4,     0),   -- option level 1: +4
  ('e1000000-0000-0000-0000-000000000012', 8,     0),   -- option level 2: +8
  ('e1000000-0000-0000-0000-000000000013',12,     0),   -- option level 3: +12
  ('e1000000-0000-0000-0000-000000000014',16,     0),   -- option level 4: +16
  ('e1000000-0000-0000-0000-000000000021', 0.95,  1),   -- dino: damage receive x0.95
  ('e1000000-0000-0000-0000-000000000022',50,     0),   -- dino: +50 max ability
  ('e1000000-0000-0000-0000-000000000023',10,     0),   -- dino: +10 attack speed
  ('e1000000-0000-0000-0000-000000000031', 5,     0),   -- ancient bonus level 1: +5
  ('e1000000-0000-0000-0000-000000000032',10,     0),   -- ancient bonus level 2: +10
  ('e1000000-0000-0000-0000-000000000041', 0.05,  0);   -- luck

INSERT INTO config."PowerUpDefinition" ("Id","TargetAttributeId","BoostId") VALUES
  ('e2000000-0000-0000-0000-000000000003','da08473f-df5b-444d-8651-9edb65797922','e1000000-0000-0000-0000-000000000003'),
  ('e2000000-0000-0000-0000-000000000004','0f9f5c9a-0000-4000-8000-000000000001','e1000000-0000-0000-0000-000000000004'),
  ('e2000000-0000-0000-0000-000000000005','0f9f5c9a-0000-4000-8000-000000000001','e1000000-0000-0000-0000-000000000005'),
  ('e2000000-0000-0000-0000-000000000006','0f9f5c9a-0000-4000-8000-000000000002','e1000000-0000-0000-0000-000000000006'),
  ('e2000000-0000-0000-0000-000000000011','0f9f5c9a-0000-4000-8000-000000000001','e1000000-0000-0000-0000-000000000011'),
  ('e2000000-0000-0000-0000-000000000012','0f9f5c9a-0000-4000-8000-000000000001','e1000000-0000-0000-0000-000000000012'),
  ('e2000000-0000-0000-0000-000000000013','0f9f5c9a-0000-4000-8000-000000000001','e1000000-0000-0000-0000-000000000013'),
  ('e2000000-0000-0000-0000-000000000014','0f9f5c9a-0000-4000-8000-000000000001','e1000000-0000-0000-0000-000000000014'),
  ('e2000000-0000-0000-0000-000000000021','9d9761ef-ef47-4e5c-8106-ebc555786f20','e1000000-0000-0000-0000-000000000021'),
  ('e2000000-0000-0000-0000-000000000022','466bbbba-c1d8-45dc-8832-2eaa1130acfd','e1000000-0000-0000-0000-000000000022'),
  ('e2000000-0000-0000-0000-000000000023','da08473f-df5b-444d-8651-9edb65797922','e1000000-0000-0000-0000-000000000023'),
  -- The ancient bonus option itself has NO boost: only its two levels carry values.
  ('e2000000-0000-0000-0000-000000000030','0f9f5c9a-0000-4000-8000-000000000004', NULL),
  ('e2000000-0000-0000-0000-000000000031','0f9f5c9a-0000-4000-8000-000000000004','e1000000-0000-0000-0000-000000000031'),
  ('e2000000-0000-0000-0000-000000000032','0f9f5c9a-0000-4000-8000-000000000004','e1000000-0000-0000-0000-000000000032'),
  ('e2000000-0000-0000-0000-000000000041','0f9f5c9a-0000-4000-8000-000000000005','e1000000-0000-0000-0000-000000000041');

-- Excellent option 5 is a relationship, not a constant: physical damage + total level / 20.
INSERT INTO config."AttributeRelationship"
  ("Id","PowerUpDefinitionValueId","InputAttributeId","TargetAttributeId","InputOperand","InputOperator") VALUES
  ('e3000000-0000-0000-0000-000000000005','e1000000-0000-0000-0000-000000000005',
   '0f9f5c9a-0000-4000-8000-000000000003','0f9f5c9a-0000-4000-8000-000000000001', 0.05, 0);

INSERT INTO config."IncreasableItemOption"
  ("Id","ItemOptionDefinitionId","OptionTypeId","PowerUpDefinitionId","Number") VALUES
  -- Dragon Slayer: excellent options 3-6. 1 and 2 are deliberately absent, so the page must not
  -- assume the six are always contiguous or always present.
  ('e4000000-0000-0000-0000-000000000003','d0000000-0000-0000-0000-000000000001','6487c498-58e0-48e5-b409-35d7598313fc','e2000000-0000-0000-0000-000000000003',3),
  ('e4000000-0000-0000-0000-000000000004','d0000000-0000-0000-0000-000000000001','6487c498-58e0-48e5-b409-35d7598313fc','e2000000-0000-0000-0000-000000000004',4),
  ('e4000000-0000-0000-0000-000000000005','d0000000-0000-0000-0000-000000000001','6487c498-58e0-48e5-b409-35d7598313fc','e2000000-0000-0000-0000-000000000005',5),
  ('e4000000-0000-0000-0000-000000000006','d0000000-0000-0000-0000-000000000001','6487c498-58e0-48e5-b409-35d7598313fc','e2000000-0000-0000-0000-000000000006',6),
  -- ... its ordinary option, and a luck option that belongs in neither list.
  ('e4000000-0000-0000-0000-000000000011','d0000000-0000-0000-0000-000000000002','f193f91e-86d7-4456-add8-a3667e731303','e2000000-0000-0000-0000-000000000011',0),
  ('e4000000-0000-0000-0000-000000000041','d0000000-0000-0000-0000-000000000003','3e3e9be8-4e16-4f27-a7cf-986d48454d76','e2000000-0000-0000-0000-000000000041',0),
  -- The Dinorant's three, all Option-type: opt is a bit field over these.
  ('e4000000-0000-0000-0000-000000000021','d0000000-0000-0000-0000-000000000004','f193f91e-86d7-4456-add8-a3667e731303','e2000000-0000-0000-0000-000000000021',0),
  ('e4000000-0000-0000-0000-000000000022','d0000000-0000-0000-0000-000000000004','f193f91e-86d7-4456-add8-a3667e731303','e2000000-0000-0000-0000-000000000022',0),
  ('e4000000-0000-0000-0000-000000000023','d0000000-0000-0000-0000-000000000004','f193f91e-86d7-4456-add8-a3667e731303','e2000000-0000-0000-0000-000000000023',0),
  -- The ancient bonus option, shared by both of the Dragon Slayer's sets.
  ('e4000000-0000-0000-0000-000000000030','d0000000-0000-0000-0000-000000000005','5e2c10ef-e580-48d5-a48b-0ffcd0678966','e2000000-0000-0000-0000-000000000030',0);

INSERT INTO config."ItemOptionOfLevel"
  ("Id","IncreasableItemOptionId","PowerUpDefinitionId","Level") VALUES
  ('e5000000-0000-0000-0000-000000000012','e4000000-0000-0000-0000-000000000011','e2000000-0000-0000-0000-000000000012',2),
  ('e5000000-0000-0000-0000-000000000013','e4000000-0000-0000-0000-000000000011','e2000000-0000-0000-0000-000000000013',3),
  ('e5000000-0000-0000-0000-000000000014','e4000000-0000-0000-0000-000000000011','e2000000-0000-0000-0000-000000000014',4),
  ('e5000000-0000-0000-0000-000000000031','e4000000-0000-0000-0000-000000000030','e2000000-0000-0000-0000-000000000031',1),
  ('e5000000-0000-0000-0000-000000000032','e4000000-0000-0000-0000-000000000030','e2000000-0000-0000-0000-000000000032',2);

INSERT INTO config."ItemSetGroup" ("Id","Name","SetLevel","MinimumItemCount","CountDistinct") VALUES
  ('e6000000-0000-0000-0000-000000000001','Hyon Dragon',   1, 2, true),
  ('e6000000-0000-0000-0000-000000000002','Vicious Dragon',1, 2, true),
  ('e6000000-0000-0000-0000-000000000003','Sylph Wind Set',1, 2, true);

INSERT INTO config."ItemOfItemSet"
  ("Id","ItemDefinitionId","ItemSetGroupId","BonusOptionId","AncientSetDiscriminator") VALUES
  ('e7000000-0000-0000-0000-000000000001','c0000000-0000-0000-0000-000000000004','e6000000-0000-0000-0000-000000000001','e4000000-0000-0000-0000-000000000030',1),
  ('e7000000-0000-0000-0000-000000000002','c0000000-0000-0000-0000-000000000004','e6000000-0000-0000-0000-000000000002','e4000000-0000-0000-0000-000000000030',2),
  -- A second piece of the Hyon set, so the piece count is not trivially one.
  ('e7000000-0000-0000-0000-000000000003','c0000000-0000-0000-0000-000000000005','e6000000-0000-0000-0000-000000000001','e4000000-0000-0000-0000-000000000030',1),
  -- The set the item is NOT linked to: /item would ignore anc=3, and so must the page.
  ('e7000000-0000-0000-0000-000000000004','c0000000-0000-0000-0000-000000000004','e6000000-0000-0000-0000-000000000003','e4000000-0000-0000-0000-000000000030',3);

INSERT INTO config."ItemDefinitionItemSetGroup" ("ItemDefinitionId","ItemSetGroupId") VALUES
  ('c0000000-0000-0000-0000-000000000004','e6000000-0000-0000-0000-000000000001'),
  ('c0000000-0000-0000-0000-000000000004','e6000000-0000-0000-0000-000000000002'),
  ('c0000000-0000-0000-0000-000000000005','e6000000-0000-0000-0000-000000000001');

