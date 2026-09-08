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

    /// <summary>Every id the SchemaContract verifies.</summary>
    public static readonly IReadOnlyList<Guid> All =
    [
        Level, MasterLevel, Resets,
        BaseStrength, BaseAgility, BaseVitality, BaseEnergy, BaseLeadership,
    ];

    // DO NOT QUERY the Total* / calculated attributes (Stats.TotalStrength and friends). They are
    // computed at runtime by the attribute system from the Base* values plus equipment and buffs,
    // and have no row in data."StatAttribute". A query for them returns nothing, silently.
}
