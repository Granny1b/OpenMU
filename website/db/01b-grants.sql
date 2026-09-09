-- Website database grants, against the `openmu` database.
--
--   psql -U postgres -d openmu -f 01b-grants.sql
--
-- RE-RUN THIS after every `-reinit` and after every admin panel Setup -> Install.
-- PersistenceContextProvider.ReCreateDatabaseAsync calls EnsureDeletedAsync(), which is
-- DROP DATABASE openmu. That destroys every grant below, and the site then answers every page
-- with 'permission denied for table Account' until this file is applied again.
--
-- The grants are COLUMN-LEVEL on purpose. `SELECT *` from data."Account" as mu_web_read fails with
-- a permission error, so no careless query can leak PasswordHash, SecurityCode, VaultPassword or
-- EMail. This is an enforced boundary, not a code-review convention.
--
-- No role below is ever granted DELETE. The site cannot delete an account or a character.

\set ON_ERROR_STOP on

-- ---------------------------------------------------------------------------------------------
-- mu_web_read - backs every ANONYMOUS page (rankings, character, guild) and the session-revalidation
-- state re-read. The highest-traffic, most injection-exposed surface, so it gets the least.
-- Deliberately NOT granted: LoginName, EMail, PasswordHash, SecurityCode, VaultPassword.
-- ---------------------------------------------------------------------------------------------
GRANT USAGE ON SCHEMA data, config, guild TO mu_web_read;

GRANT SELECT ("Id", "State", "IsBot", "IsTemplate")                     ON data."Account"   TO mu_web_read;
GRANT SELECT ("Id", "Name", "AccountId", "CharacterClassId", "CharacterStatus",
              "State", "Experience", "MasterExperience", "PlayerKillCount",
              "CreateDate", "CurrentMapId")                             ON data."Character" TO mu_web_read;
GRANT SELECT ("CharacterId", "DefinitionId", "Value")                   ON data."StatAttribute" TO mu_web_read;
GRANT SELECT ("Id", "Number", "Name")                                   ON config."CharacterClass" TO mu_web_read;
GRANT SELECT ("Id", "Designation")                                      ON config."AttributeDefinition" TO mu_web_read;
GRANT SELECT ("Id", "Name")                                             ON config."GameMapDefinition" TO mu_web_read;
GRANT SELECT ("Id", "Name", "Score", "Notice")                          ON guild."Guild"       TO mu_web_read;
GRANT SELECT ("Id", "GuildId", "Status")                                ON guild."GuildMember" TO mu_web_read;

-- ---------------------------------------------------------------------------------------------
-- mu_web_auth - THE ONLY role that can read a password hash. Used by exactly three call sites:
-- login, change-password verification, and the password write of change-password / admin reset.
-- Keeping it separate means the site's most valuable asset is not reachable from registration,
-- ban, unban or any admin listing page.
-- ---------------------------------------------------------------------------------------------
GRANT USAGE ON SCHEMA data TO mu_web_auth;
GRANT SELECT ("Id", "LoginName", "PasswordHash", "State") ON data."Account" TO mu_web_auth;
GRANT UPDATE ("PasswordHash")                             ON data."Account" TO mu_web_auth;

-- ---------------------------------------------------------------------------------------------
-- mu_web_reg - registration, ban/unban and the admin account views. Physically cannot read a hash.
-- EMail lives here rather than on mu_web_read, so email privacy is enforced by the grant.
-- UPDATE is limited to "State": the site can ban and unban, and can change nothing else.
-- ---------------------------------------------------------------------------------------------
GRANT USAGE ON SCHEMA data, config, guild TO mu_web_reg;

GRANT INSERT ("Id", "LoginName", "PasswordHash", "SecurityCode", "EMail", "RegistrationDate",
              "State", "TimeZone", "VaultPassword", "IsVaultExtended", "IsBot", "IsTemplate",
              "LanguageIsoCode")                                        ON data."Account" TO mu_web_reg;
GRANT SELECT ("Id", "LoginName", "EMail", "RegistrationDate", "State",
              "IsBot", "IsTemplate", "ChatBanUntil")                    ON data."Account" TO mu_web_reg;
GRANT UPDATE ("State")                                                  ON data."Account" TO mu_web_reg;

GRANT SELECT ("Id", "Name", "AccountId", "CharacterClassId", "CharacterStatus", "State",
              "Experience", "MasterExperience", "PlayerKillCount", "CreateDate") ON data."Character" TO mu_web_reg;
GRANT SELECT ("CharacterId", "DefinitionId", "Value")                   ON data."StatAttribute" TO mu_web_reg;
GRANT SELECT ("Id", "Number", "Name")                                   ON config."CharacterClass" TO mu_web_reg;
GRANT SELECT ("Id", "GuildId", "Status")                                ON guild."GuildMember" TO mu_web_reg;
GRANT SELECT ("Id", "Name")                                             ON guild."Guild" TO mu_web_reg;

-- ---------------------------------------------------------------------------------------------
-- The GM console's reference catalogue. Pure game CONFIGURATION - monster and item definitions,
-- spawn areas, maps. It carries no player data at all, so it goes to mu_web_read, the pool that
-- cannot read a login name, an email or a password hash. The console pages are admin-only, but
-- the credential they read with does not need to be.
--
-- Whole-table SELECT here rather than column lists: these tables ARE the public reference data
-- that OpenMU ships, there is nothing in them to withhold, and a column list would need editing
-- on every upstream migration that adds a field.
-- ---------------------------------------------------------------------------------------------
GRANT SELECT ON config."MonsterDefinition"  TO mu_web_read;
GRANT SELECT ON config."MonsterAttribute"   TO mu_web_read;
GRANT SELECT ON config."MonsterSpawnArea"   TO mu_web_read;
GRANT SELECT ON config."ItemDefinition"     TO mu_web_read;

-- GameMapDefinition already grants ("Id", "Name") for the character page; the console additionally
-- needs the map number to name the map a monster spawns on. TerrainData - a byte[] of map geometry
-- that would be megabytes across a listing - is deliberately still excluded.
--
-- The REVOKE is for databases that ran an earlier version of this file: ExpMultiplier was granted
-- for a maps page that no longer exists, and a grant nothing reads should not linger.
GRANT SELECT ("Number") ON config."GameMapDefinition" TO mu_web_read;
REVOKE SELECT ("ExpMultiplier") ON config."GameMapDefinition" FROM mu_web_read;

-- The /item builder resolves what an item's own options ARE, so it can label them instead of
-- asking a GM to work out a bit field. That is this whole chain:
--
--   ItemDefinition
--     -> ItemDefinitionItemOptionDefinition -> ItemOptionDefinition -> IncreasableItemOption
--          -> ItemOptionType        (is this an Excellent option, or an ordinary one?)
--          -> ItemOptionOfLevel     (+4 / +8 / +12 / +16)
--          -> PowerUpDefinition -> PowerUpDefinitionValue / AttributeRelationship
--               -> AttributeDefinition   (what the option actually increases)
--     -> ItemOfItemSet -> ItemSetGroup   (Hyon vs Vicious, for `anc`)
--
-- All of it is the configuration the server ships. None of it references a player, a character or
-- an account, so whole-table SELECT is right here for the same reason it is above.
GRANT SELECT ON config."ItemDefinitionItemOptionDefinition" TO mu_web_read;
GRANT SELECT ON config."ItemOptionDefinition"               TO mu_web_read;
GRANT SELECT ON config."IncreasableItemOption"              TO mu_web_read;
GRANT SELECT ON config."ItemOptionType"                     TO mu_web_read;
GRANT SELECT ON config."ItemOptionOfLevel"                  TO mu_web_read;
GRANT SELECT ON config."PowerUpDefinition"                  TO mu_web_read;
GRANT SELECT ON config."PowerUpDefinitionValue"             TO mu_web_read;
GRANT SELECT ON config."AttributeRelationship"              TO mu_web_read;
GRANT SELECT ON config."AttributeDefinition"                TO mu_web_read;
GRANT SELECT ON config."ItemOfItemSet"                      TO mu_web_read;
GRANT SELECT ON config."ItemSetGroup"                       TO mu_web_read;
GRANT SELECT ON config."ItemDefinitionItemSetGroup"         TO mu_web_read;

-- Skill is granted by COLUMN rather than whole-table: the builder needs exactly one fact from it -
-- whether an item's skill number is 49, which is how ItemChatCommandPlugIn recognises a Dinorant
-- and switches `opt` from an option level to a bit field.
GRANT SELECT ("Id", "Number", "Name") ON config."Skill" TO mu_web_read;

-- ---------------------------------------------------------------------------------------------
-- Verification. Each of these MUST behave as annotated; run them after applying this file.
-- ---------------------------------------------------------------------------------------------
--   psql -U mu_web_read -d openmu -c 'SELECT * FROM data."Account" LIMIT 1'
--     -> ERROR: permission denied for table Account          (a bare SELECT * cannot leak a hash)
--   psql -U mu_web_read -d openmu -c 'SELECT "Id","State" FROM data."Account" LIMIT 1'
--     -> one row                                             (the columns it is allowed to read)
--   psql -U mu_web_reg  -d openmu -c 'SELECT "PasswordHash" FROM data."Account" LIMIT 1'
--     -> ERROR: permission denied for table Account          (registration cannot read hashes)
--   psql -U mu_web_auth -d openmu -c 'DELETE FROM data."Account"'
--     -> ERROR: permission denied for table Account          (nothing can delete accounts)
--
-- AND THEN: log in with the actual game client. If players cannot log in after applying this file,
-- something revoked PUBLIC's CONNECT - see the warning at the top of 01-roles.sql.
