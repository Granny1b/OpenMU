using System.Text.RegularExpressions;

namespace MuSite.Game;

/// <summary>
/// OpenMU's seeded development accounts. Every one of them ships with password == login name
/// (src/Persistence/Initialization/VersionSeasonSix/TestAccounts/AccountInitializerBase.cs:92) and
/// three - testgm, testgm2, testunlock - ship with State = 2 (GameMaster).
///
/// The site refuses all of them at login and never resolves any of them to an admin role, whether
/// or not the owner has run db/03-seed-cleanup.sql. Names verified against
/// VersionSeasonSix/TestAccounts/TestAccountsInitialization.cs:26-41 and the constructor arguments
/// of the ten named initializers.
/// </summary>
internal static partial class SeedAccounts
{
    private static readonly HashSet<string> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        // TestAccountsInitialization.cs:27-30 - for (int i = 0; i < 10; i++) => "test" + i
        "test0", "test1", "test2", "test3", "test4", "test5", "test6", "test7", "test8", "test9",

        // The ten named initializers, :32-41
        "test300",    // Level300
        "test400",    // Level400  - a geared level 400 character
        "ancient",    // Ancient
        "socket",     // Socket
        "quest1",     // Quest150
        "quest2",     // Quest220
        "quest3",     // Quest400
        "testgm",     // GameMaster      - State = GameMaster
        "testgm2",    // GameMaster2     - State = GameMaster
        "testunlock", // Unlocked        - State = GameMaster
    };

    /// <summary>
    /// True when the login name is one of OpenMU's seeded accounts. The regex is a second line of
    /// defence: if a future OpenMU release widens the test0..testN loop, the new names are still
    /// denied without anyone having to remember to edit this file.
    /// </summary>
    public static bool IsSeedName(string? loginName)
        => !string.IsNullOrEmpty(loginName)
           && (Names.Contains(loginName) || TestNumberPattern().IsMatch(loginName));

    /// <summary>The full list, for the admin dashboard's "these still exist" warning.</summary>
    public static IReadOnlyCollection<string> All => Names;

    [GeneratedRegex(@"^test\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TestNumberPattern();
}
