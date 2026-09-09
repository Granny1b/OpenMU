using System.Globalization;
using MuSite.Data;

namespace MuSite.Game;

/// <summary>The character status a command requires, mirroring OpenMU's CharacterStatus.</summary>
public enum GmStatus
{
    /// <summary>Any player may run it.</summary>
    Normal = 0,

    /// <summary>Requires a Game Master character.</summary>
    GameMaster = 2,
}

/// <summary>How one argument is written.</summary>
public enum GmArgKind
{
    /// <summary>A whole number.</summary>
    Integer,

    /// <summary>A boolean, written as true/false or 1/0.</summary>
    Flag,

    /// <summary>Free text, typically a character name or a map name.</summary>
    Text,
}

/// <summary>
/// One argument of a chat command.
/// </summary>
/// <param name="Name">The property name in the server's argument class.</param>
/// <param name="ShortName">The name used in <c>shortname=value</c> form; empty when positional-only.</param>
/// <param name="Kind">How the value is written.</param>
/// <param name="IsNamed">Whether it can be addressed by name at all.</param>
/// <param name="IsRequired">Whether the server refuses the command without it.</param>
/// <param name="ValidValues">The values the server accepts, when it constrains them.</param>
public sealed record GmArg(
    string Name, string ShortName, GmArgKind Kind, bool IsNamed, bool IsRequired,
    IReadOnlyList<string> ValidValues);

/// <summary>
/// One chat command.
/// </summary>
/// <param name="Command">The command including its leading slash.</param>
/// <param name="Status">The character status required to run it.</param>
/// <param name="IsDisabledByDefault">Whether it must be enabled on the panel's Plugins page first.</param>
/// <param name="Description">The server's own description.</param>
/// <param name="Args">Its arguments, in declaration order - which is also positional order.</param>
public sealed record GmCommand(
    string Command, GmStatus Status, bool IsDisabledByDefault, string Description,
    IReadOnlyList<GmArg> Args)
{
    /// <summary>Whether any argument can be addressed by name.</summary>
    public bool SupportsNamedArguments => this.Args.Any(a => a.IsNamed);

    /// <summary>A usage line: <c>/item &lt;group&gt; &lt;number&gt; [lvl] ...</c>.</summary>
    public string Usage
    {
        get
        {
            var parts = this.Args.Select(a =>
            {
                var label = a.IsNamed ? a.ShortName : a.Name;
                return a.IsRequired ? $"<{label}>" : $"[{label}]";
            });

            return string.Join(' ', new[] { this.Command }.Concat(parts));
        }
    }
}

/// <summary>
/// The GM command catalogue, and the builders that compose a command string from it.
///
/// The site cannot execute these - see the comment on <see cref="GameCatalog"/> for why, in three
/// independently verified parts. What it can do is compose them exactly, validated against the
/// game's own configuration, so that what you paste in-game works the first time.
///
/// The catalogue itself is generated: see GmCommands.Generated.cs and
/// website/tools/generate-gm-commands.py.
/// </summary>
public static partial class GmCommands
{
    /// <summary>Item group 14 - Jewels and other consumables in Season 6.</summary>
    public const int JewelGroup = 14;

    /// <summary>Finds one command by its string, or null.</summary>
    public static GmCommand? Find(string? command)
        => All.FirstOrDefault(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase));

    /// <summary>The commands a GM can run, in catalogue order.</summary>
    public static IReadOnlyList<GmCommand> GameMasterCommands
        => All.Where(c => c.Status == GmStatus.GameMaster).ToList();

    /// <summary>
    /// Composes a command from named arguments, skipping blanks.
    ///
    /// Named form is used wherever the server supports it, because the positional form requires
    /// every preceding argument to be supplied: `/item 14 13` cannot reach `anc` without spelling
    /// out the six arguments in between. The server switches to named parsing when the command
    /// contains an '=' at all (CommandExtensions.TryParseArgumentsAsync), and matches short names
    /// EXACTLY and CASE-SENSITIVELY - which is why `ancBonuslvl` is spelled the way it is.
    /// </summary>
    public static string BuildNamed(GmCommand command, IReadOnlyDictionary<string, string?> values)
    {
        var parts = new List<string> { command.Command };

        foreach (var arg in command.Args)
        {
            if (!arg.IsNamed
                || !values.TryGetValue(arg.Name, out var value)
                || string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            parts.Add($"{arg.ShortName}={value.Trim()}");
        }

        return string.Join(' ', parts);
    }

    /// <summary>Composes a command from positional arguments, stopping at the first blank.</summary>
    public static string BuildPositional(GmCommand command, IReadOnlyList<string?> values)
    {
        var parts = new List<string> { command.Command };

        // Positional arguments cannot have a hole: the server assigns them to properties by index,
        // so a blank in the middle would shift everything after it onto the wrong property.
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                break;
            }

            parts.Add(value.Trim());
        }

        return string.Join(' ', parts);
    }

    /// <summary>
    /// What is wrong with a requested item, checked against that item's own configuration.
    ///
    /// The three optional arguments are what the item can actually do, read out of the catalogue.
    /// They default to "not known", in which case only the limits ItemChatCommandArgs enforces are
    /// checked - which is what the pure command tests want. When the caller does know, the checks
    /// get sharper: /item silently IGNORES an excellent bit the item has no option for, and an
    /// ancient discriminator that names no set, so a command carrying either looks like it worked
    /// and produces a plainer item than the GM asked for.
    /// </summary>
    public static IReadOnlyList<string> ValidateItem(
        ItemRow? definition,
        ItemRequest request,
        int? excellentMask = null,
        int? highestOptionLevel = null,
        IReadOnlyList<int>? ancientDiscriminators = null)
    {
        var problems = new List<string>();

        if (definition is null)
        {
            problems.Add($"No item definition has group {request.Group} and number {request.Number}.");
            return problems;
        }

        if (request.Level > definition.MaximumItemLevel)
        {
            problems.Add(
                $"{definition.Name} goes up to +{definition.MaximumItemLevel}; +{request.Level} would be refused.");
        }

        // ItemChatCommandArgs constrains these itself, via [ValidValues].
        if (request.Ancient is < 0 or > 2)
        {
            problems.Add("Ancient must be 0, 1 or 2.");
        }

        if (request.Ancient > 0 && request.AncientBonusLevel is < 1 or > 2)
        {
            problems.Add("Ancient bonus level must be 1 or 2.");
        }

        if (request.Ancient == 0 && request.AncientBonusLevel > 1)
        {
            problems.Add("Ancient bonus level only applies when Ancient is 1 or 2.");
        }

        // The excellent options are a bit field of six, so 63 is every option at once.
        if (request.ExcellentNumber is < 0 or > 63)
        {
            problems.Add("Excellent options are a bit field from 0 to 63.");
        }

        if (request.ExcellentNumber > 0 && excellentMask is { } mask)
        {
            if (mask == 0)
            {
                problems.Add($"{definition.Name} carries no excellent options, so ex would do nothing.");
            }
            else if ((request.ExcellentNumber & ~mask) != 0)
            {
                problems.Add(
                    $"{definition.Name} has no excellent option for every bit of ex={request.ExcellentNumber}; "
                    + $"the ones it does have add up to {mask}.");
            }
        }

        if (request.Opt > 0 && highestOptionLevel is { } highest && request.Opt > highest)
        {
            problems.Add(highest == 0
                ? $"{definition.Name} carries no ordinary option, so opt would do nothing."
                : $"{definition.Name}'s option goes up to level {highest}; opt={request.Opt} has no value configured.");
        }

        if (request.Ancient > 0 && ancientDiscriminators is { Count: > 0 }
            && !ancientDiscriminators.Contains(request.Ancient))
        {
            problems.Add(
                $"{definition.Name} has no ancient set {request.Ancient}; "
                + $"it belongs to {string.Join(" and ", ancientDiscriminators)}.");
        }

        if (request.Ancient > 0 && ancientDiscriminators is { Count: 0 })
        {
            problems.Add($"{definition.Name} is not part of any ancient set, so anc would do nothing.");
        }

        if (definition.IsQuestItem)
        {
            problems.Add($"{definition.Name} is a quest item; dropping one may not behave as you expect.");
        }

        return problems;
    }

    /// <summary>Composes the <c>/item</c> command for a request.</summary>
    public static string BuildItem(ItemRequest request)
    {
        var command = Find("/item")
            ?? throw new InvalidOperationException("/item is missing from the generated catalogue.");

        // Only the non-default values are emitted: a command carrying nine arguments where two
        // would do is harder to read and to retype from a screenshot.
        var values = new Dictionary<string, string?>
        {
            ["Group"] = request.Group.ToString(CultureInfo.InvariantCulture),
            ["Number"] = request.Number.ToString(CultureInfo.InvariantCulture),
            ["Level"] = Optional(request.Level),
            ["ExcellentNumber"] = Optional(request.ExcellentNumber),
            ["Skill"] = request.Skill ? "true" : null,
            ["Luck"] = request.Luck ? "true" : null,
            ["Opt"] = Optional(request.Opt),
            ["Ancient"] = Optional(request.Ancient),
            ["AncientBonusLevel"] = request.Ancient > 0 && request.AncientBonusLevel > 1
                ? request.AncientBonusLevel.ToString(CultureInfo.InvariantCulture)
                : null,
        };

        return BuildNamed(command, values);

        static string? Optional(int value)
            => value > 0 ? value.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>Composes <c>/createmonster</c> for a monster number.</summary>
    public static string BuildCreateMonster(int monsterNumber, bool intelligent)
    {
        var command = Find("/createmonster")
            ?? throw new InvalidOperationException("/createmonster is missing from the generated catalogue.");

        return BuildNamed(command, new Dictionary<string, string?>
        {
            ["MonsterNumber"] = monsterNumber.ToString(CultureInfo.InvariantCulture),
            ["IsIntelligent"] = intelligent ? "true" : null,
        });
    }

    /// <summary>
    /// Composes <c>/move</c> to warp another character.
    ///
    /// /move is declared CharacterStatus.Normal because a player uses it on themselves, but warping
    /// SOMEONE ELSE needs a GM: MoveChatCommandPlugIn only takes that branch when
    /// `senderIsGameMaster && !string.IsNullOrWhiteSpace(arguments.MapIdOrName)` - so the map is not
    /// optional here even when the target is already on it.
    /// </summary>
    public static string BuildMovePlayer(string characterName, string mapIdOrName, int? x, int? y)
    {
        var command = Find("/move")
            ?? throw new InvalidOperationException("/move is missing from the generated catalogue.");

        return BuildNamed(command, new Dictionary<string, string?>
        {
            ["Target"] = characterName,
            ["MapIdOrName"] = mapIdOrName,
            ["X"] = x?.ToString(CultureInfo.InvariantCulture),
            ["Y"] = y?.ToString(CultureInfo.InvariantCulture),
        });
    }
}

/// <summary>What the item builder was asked for.</summary>
public sealed record ItemRequest(
    int Group, int Number, int Level, int ExcellentNumber, bool Skill, bool Luck,
    int Opt, int Ancient, int AncientBonusLevel);
