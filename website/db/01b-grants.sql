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
              "Experience", "PlayerKillCount", "CreateDate")            ON data."Character" TO mu_web_reg;
GRANT SELECT ("CharacterId", "DefinitionId", "Value")                   ON data."StatAttribute" TO mu_web_reg;
GRANT SELECT ("Id", "Number", "Name")                                   ON config."CharacterClass" TO mu_web_reg;
GRANT SELECT ("Id", "GuildId", "Status")                                ON guild."GuildMember" TO mu_web_reg;
GRANT SELECT ("Id", "Name")                                             ON guild."Guild" TO mu_web_reg;

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
