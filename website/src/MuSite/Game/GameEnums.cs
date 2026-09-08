namespace MuSite.Game;

/// <summary>
/// Values copied from the OpenMU data model. They are persisted as integers, so they are part of
/// the on-disk contract; the citations are where to look when something stops matching.
/// </summary>
internal static class GameEnums
{
    /// <summary>
    /// AccountState - src/DataModel/Entities/Account.cs:14. Implicit numbering, persisted as integer
    /// in data."Account"."State".
    /// </summary>
    public static class AccountState
    {
        public const int Normal = 0;
        public const int Spectator = 1;
        public const int GameMaster = 2;
        public const int GameMasterInvisible = 3;
        public const int Banned = 4;
        public const int TemporarilyBanned = 5;

        public static bool IsBanned(int state) => state is Banned or TemporarilyBanned;

        public static bool IsGameMaster(int state) => state is GameMaster or GameMasterInvisible;

        public static string Describe(int state) => state switch
        {
            Normal => "Normal",
            Spectator => "Spectator",
            GameMaster => "Game Master",
            GameMasterInvisible => "Game Master (invisible)",
            Banned => "Banned",
            TemporarilyBanned => "Temporarily banned",
            _ => $"Unknown ({state})",
        };
    }

    /// <summary>
    /// CharacterStatus - src/DataModel/Entities/Character.cs:55. NOT contiguous: GameMaster is 32.
    /// Persisted in data."Character"."CharacterStatus".
    /// </summary>
    public static class CharacterStatus
    {
        public const int Normal = 0;
        public const int Banned = 1;
        public const int GameMaster = 32;
    }

    /// <summary>
    /// HeroState - src/DataModel/Entities/Character.cs:14. Persisted in data."Character"."State".
    /// </summary>
    public static class HeroState
    {
        public const int New = 0;
        public const int Hero = 1;
        public const int LightHero = 2;
        public const int Normal = 3;
        public const int PlayerKillWarning = 4;
        public const int PlayerKiller1stStage = 5;
        public const int PlayerKiller2ndStage = 6;

        public static string Describe(int state) => state switch
        {
            New => "New",
            Hero => "Hero",
            LightHero => "Light Hero",
            Normal => "Normal",
            PlayerKillWarning => "Warning",
            PlayerKiller1stStage => "Outlaw",
            PlayerKiller2ndStage => "Murderer",
            _ => "Normal",
        };
    }

    /// <summary>
    /// GuildPosition - src/Interfaces/GuildPosition.cs:11, a byte. Implicit numbering, and NOT
    /// ordered by authority: BattleMaster (3) outranks nothing, GuildMaster (2) is the leader.
    /// Persisted in guild."GuildMember"."Status".
    /// </summary>
    public static class GuildPosition
    {
        public const int Undefined = 0;
        public const int NormalMember = 1;
        public const int GuildMaster = 2;
        public const int BattleMaster = 3;
        public const int AssistantMaster = 4;

        public static string Describe(int position) => position switch
        {
            GuildMaster => "Guild Master",
            BattleMaster => "Battle Master",
            AssistantMaster => "Assistant Master",
            _ => "Member",
        };

        /// <summary>Display order for a roster: master first, then officers, then members.</summary>
        public static int SortKey(int position) => position switch
        {
            GuildMaster => 0,
            AssistantMaster => 1,
            BattleMaster => 2,
            _ => 3,
        };
    }
}
