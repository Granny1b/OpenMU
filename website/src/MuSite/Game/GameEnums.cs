namespace MuSite.Game;

/// <summary>
/// Values copied from the OpenMU data model. Public so the test project can pin them - they are
/// persisted as integers and so are part of the on-disk contract, and a wrong constant here means a
/// wrong badge or a missed ban. The citations are where to look when something stops matching.
/// </summary>
public static class GameEnums
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

    /// <summary>
    /// SpawnTrigger - src/DataModel/Configuration/MonsterSpawnArea.cs:13. Persisted as an int in
    /// config."MonsterSpawnArea"."SpawnTrigger".
    /// </summary>
    public static class SpawnTrigger
    {
        /// <summary>Spawns and respawns on its own.</summary>
        public const int Automatic = 0;

        /// <summary>Spawns automatically while an event runs.</summary>
        public const int AutomaticDuringEvent = 1;

        /// <summary>Spawns once when an event starts. This is what golden monsters use.</summary>
        public const int OnceAtEventStart = 2;

        /// <summary>Spawns automatically during a wave of an event.</summary>
        public const int AutomaticDuringWave = 3;

        /// <summary>Spawns once at the start of a wave.</summary>
        public const int OnceAtWaveStart = 4;

        /// <summary>Placed by the event logic rather than by the map.</summary>
        public const int ManuallyForEvent = 5;

        /// <summary>Wanders rather than holding a spawn point.</summary>
        public const int Wandering = 6;

        /// <summary>A phrase for the console, explaining what the trigger means in practice.</summary>
        public static string Describe(int trigger) => trigger switch
        {
            Automatic => "Always, respawns",
            AutomaticDuringEvent => "During an event",

            // The source comment on this value names golden monsters explicitly - which is why a
            // golden Tantallos is not standing there between invasions.
            OnceAtEventStart => "Once per event start (golden / boss)",
            AutomaticDuringWave => "During an event wave",
            OnceAtWaveStart => "Once per wave start",
            ManuallyForEvent => "Placed by event logic",
            Wandering => "Wanders the map",
            _ => $"Unknown ({trigger})",
        };
    }
}
