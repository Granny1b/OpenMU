namespace MuSite.Data;

/// <summary>
/// Primary keys of the rows in config."AttributeDefinition" that the site reads out of
/// data."StatAttribute". Copied from src/GameLogic/Attributes/Stats.cs; SchemaContract asserts at
/// startup that every one of them still exists, because nothing in the C# compiler will tell us
/// when an upstream migration changes them.
/// </summary>
internal static class StatIds
{
    /// <summary>Stats.Level - Stats.cs:97.</summary>
    public static readonly Guid Level = new("560931AD-0901-4342-B7F4-FD2E2FCC0563");

    /// <summary>Stats.MasterLevel - Stats.cs:133.</summary>
    public static readonly Guid MasterLevel = new("70CD8C10-391A-4C51-9AA4-A854600E3A9F");

    /// <summary>Stats.Resets - Stats.cs:148.</summary>
    public static readonly Guid Resets = new("89A891A7-F9F9-4AB5-AF36-12056E53A5F7");

    /// <summary>Stats.BaseStrength - Stats.cs:17.</summary>
    public static readonly Guid BaseStrength = new("123282FE-FEAD-448E-AD2C-BAECE939B4B1");

    /// <summary>Stats.BaseAgility - Stats.cs:32.</summary>
    public static readonly Guid BaseAgility = new("1AE9C014-E3CD-4703-BD05-1B65F5F94CEB");

    /// <summary>Stats.BaseVitality - Stats.cs:47.</summary>
    public static readonly Guid BaseVitality = new("6CA5C3A6-B109-45A5-87A7-FDCB107B4982");

    /// <summary>Stats.BaseEnergy - Stats.cs:62.</summary>
    public static readonly Guid BaseEnergy = new("01B0EF28-F7A0-46B5-97BA-2B624A54CD75");

    /// <summary>Stats.BaseLeadership - Stats.cs:77.</summary>
    public static readonly Guid BaseLeadership = new("6AF2C9DF-3AE4-4721-8462-9A8EC7F56FE4");

    // ---------------------------------------------------------------------------------------------
    // Monster attributes. These are read out of config."MonsterAttribute", not
    // data."StatAttribute" - the same AttributeDefinition rows serve both, so the ids are shared.
    // Level is deliberately reused rather than duplicated.
    // ---------------------------------------------------------------------------------------------

    /// <summary>Stats.MaximumHealth - Stats.cs:173.</summary>
    public static readonly Guid MaximumHealth = new("A6C39A5C-295F-415E-A314-5E9F9A748D27");

    /// <summary>Stats.MinimumPhysBaseDmg - Stats.cs:255.</summary>
    public static readonly Guid MinimumPhysBaseDmg = new("3E8D6A02-E973-4AE4-9DF3-CDDC3D3183B3");

    /// <summary>Stats.MaximumPhysBaseDmg - Stats.cs:261.</summary>
    public static readonly Guid MaximumPhysBaseDmg = new("8A918EA2-893A-48B2-A684-3E71526CA71F");

    /// <summary>Stats.DefenseBase - Stats.cs:809.</summary>
    public static readonly Guid DefenseBase = new("EB098C46-60D4-4CA6-BBD4-5B6270A1407B");

    /// <summary>Stats.DefenseRatePvm - Stats.cs:859.</summary>
    public static readonly Guid DefenseRatePvm = new("C520DD2D-1B06-4392-95EE-3C41F33E68DA");

    /// <summary>Stats.AttackRatePvm - Stats.cs:217.</summary>
    public static readonly Guid AttackRatePvm = new("1129442A-E1C7-4240-8866-B781C2838C25");

    /// <summary>
    /// The ids the SchemaContract REQUIRES. Every public page depends on these: a missing one means
    /// the rankings, the character page and the account page are all wrong, so the site would
    /// rather show its maintenance notice than serve a board with silent zeroes.
    /// </summary>
    public static readonly IReadOnlyList<Guid> Required =
    [
        Level, MasterLevel, Resets,
        BaseStrength, BaseAgility, BaseVitality, BaseEnergy, BaseLeadership,
    ];

    /// <summary>
    /// Monster stats, read by the GM console's catalogue only.
    ///
    /// DELIBERATELY NOT in <see cref="Required"/>. These are read from config."MonsterAttribute" to
    /// decorate a reference table; if a server's configuration has never seeded one of them, the
    /// console should show a blank column, not put the whole public site into maintenance. The
    /// queries COALESCE a missing attribute to 0 for the same reason.
    /// </summary>
    public static readonly IReadOnlyList<Guid> Catalogue =
    [
        MaximumHealth, MinimumPhysBaseDmg, MaximumPhysBaseDmg,
        DefenseBase, DefenseRatePvm, AttackRatePvm,
    ];

    /// <summary>Every id this file names, required or not.</summary>
    public static readonly IReadOnlyList<Guid> All = [.. Required, .. Catalogue];

    // DO NOT QUERY the Total* / calculated attributes (Stats.TotalStrength and friends). They are
    // computed at runtime by the attribute system from the Base* values plus equipment and buffs,
    // and have no row in data."StatAttribute". A query for them returns nothing, silently.
}
