using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Data;
using MuSite.Game;

namespace MuSite.Pages.Admin;

/// <summary>
/// The /item builder.
///
/// The point of this page is that `/item 0 16 lvl=13 ex=63 opt=4 anc=1` is three different kinds of
/// number wearing the same clothes, and no amount of staring at the command tells you which:
///
///   * `lvl` is a level.
///   * `ex`  is a BIT FIELD over the item's own excellent options - bit 1 &lt;&lt; (Number - 1) each - and
///           an armour's option 1 is a completely different bonus from a weapon's option 1.
///   * `opt` is an option LEVEL for every item except the Dinorant, where it is a three-bit field.
///   * `anc` is an AncientSetDiscriminator - 1 and 2 name two different real sets, per item.
///
/// So the form asks in the item's own terms and does the arithmetic itself. Every label on it comes
/// out of the game's configuration; see GameCatalog.ItemExcellentOptionsAsync and friends.
/// </summary>
public sealed class ItemsModel(GameCatalog catalog) : PageModel
{
    /// <summary>Rows per page. The Season 6 catalogue runs to several hundred definitions, so a
    /// single capped list silently hid most of them.</summary>
    private const int PageSize = 50;

    /// <summary>The search text.</summary>
    public string? Query { get; private set; }

    /// <summary>The item group filter, when one is applied.</summary>
    public int? Group { get; private set; }

    /// <summary>The matching item definitions on this page.</summary>
    public IReadOnlyList<ItemRow> Results { get; private set; } = [];

    /// <summary>How many definitions match in total, across every page.</summary>
    public int Total { get; private set; }

    /// <summary>1-based page number. NOT called "Page": that would hide PageModel.Page().</summary>
    public int PageNumber { get; private set; } = 1;

    /// <summary>How many pages the current search fills.</summary>
    public int PageCount => Math.Max(1, (int)Math.Ceiling(this.Total / (double)PageSize));

    /// <summary>The first row of the current page.</summary>
    public int FirstRow => this.Total == 0 ? 0 : ((this.PageNumber - 1) * PageSize) + 1;

    /// <summary>The last row of the current page.</summary>
    public int LastRow => Math.Min(this.PageNumber * PageSize, this.Total);

    /// <summary>A link to another page of the same search, keeping any built item selected.</summary>
    public string PageLink(int page)
    {
        var link = $"/admin/items?page={page}";

        if (!string.IsNullOrEmpty(this.Query))
        {
            link += $"&q={Uri.EscapeDataString(this.Query)}";
        }

        if (this.Group is { } group)
        {
            link += $"&group={group}";
        }

        // Keeping the selection means paging the list does not throw away a half-built command.
        // Group is always set alongside it - the builder needs both to identify an item - so it is
        // already on the link from the branch above.
        if (this.Selected is { } selected)
        {
            link += $"&number={selected.Number}";
        }

        return link;
    }

    /// <summary>The item the builder is configured for.</summary>
    public ItemRow? Selected { get; private set; }

    /// <summary>
    /// What the builder was asked for. Named Requested rather than Request because PageModel
    /// already has a Request - the HttpRequest - and shadowing it on a page is a trap.
    /// </summary>
    public ItemRequest? Requested { get; private set; }

    /// <summary>The built command, when the request is valid.</summary>
    public string? Command { get; private set; }

    /// <summary>Anything wrong with the request, checked against the item's own limits.</summary>
    public IReadOnlyList<string> Problems { get; private set; } = [];

    /// <summary>The excellent options this item can carry, each with the bit that selects it.</summary>
    public IReadOnlyList<ExcellentOptionRow> ExcellentOptions { get; private set; } = [];

    /// <summary>
    /// The levels of the item's ordinary option, lowest first - what `opt` selects between.
    /// Empty for an item that has no Option-type option at all, such as a jewel.
    /// </summary>
    public IReadOnlyList<OptionLevelRow> OptionLevels { get; private set; } = [];

    /// <summary>The ancient sets this item belongs to - what `anc=1` and `anc=2` actually mean.</summary>
    public IReadOnlyList<AncientSetRow> AncientSets { get; private set; } = [];

    /// <summary>
    /// The Dinorant's three options, paired with the bit of `opt` that turns each one on.
    /// Empty for every other item, where `opt` is a level instead.
    /// </summary>
    public IReadOnlyList<(int Bit, OptionLevelRow Option)> DinorantOptions { get; private set; } = [];

    /// <summary>Whether this item's `opt` is a bit field rather than a level.</summary>
    public bool OptIsBitField => this.DinorantOptions.Count > 0;

    /// <summary>
    /// True when the item has more than one ordinary option and the command's choice between them
    /// is therefore not something this page can predict - AddOption takes whichever the server
    /// enumerates first.
    /// </summary>
    public bool OptionIsAmbiguous { get; private set; }

    /// <summary>Whether /item needs enabling on the panel's Plugins page first.</summary>
    public bool CommandDisabledByDefault => GmCommands.Find("/item")?.IsDisabledByDefault ?? false;

    /// <summary>The Dinorant's bits, in the order ItemChatCommandPlugIn.AddOption tests them.</summary>
    private static readonly (int Bit, Guid Attribute)[] DinorantBits =
    [
        (1, StatIds.DamageReceiveDecrement),
        (2, StatIds.MaximumAbility),
        (4, StatIds.AttackSpeedAny),
    ];

    public async Task OnGetAsync(
        [FromQuery] string? q,
        [FromQuery] int? group,
        [FromQuery] int? number,
        [FromQuery] int page,
        [FromQuery] int lvl,
        [FromQuery] int[]? exbit,
        [FromQuery] bool sk,
        [FromQuery] bool lu,
        [FromQuery] int opt,
        [FromQuery] int[]? optbit,
        [FromQuery] int anc,
        [FromQuery] int ancLvl,
        CancellationToken cancellationToken)
    {
        this.Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim();
        this.Group = group;
        this.Total = await catalog.ItemCountAsync(this.Query, group, cancellationToken).ConfigureAwait(false);

        // Clamped both ways: ?page=0 and ?page=9999 are one edit of the address bar away, and an
        // out-of-range OFFSET returns an empty table that looks like "no items match".
        this.PageNumber = Math.Clamp(page < 1 ? 1 : page, 1, this.PageCount);

        this.Results = await catalog
            .ItemsAsync(this.Query, group, (this.PageNumber - 1) * PageSize, PageSize, cancellationToken)
            .ConfigureAwait(false);

        if (group is not { } selectedGroup || number is not { } selectedNumber)
        {
            return;
        }

        this.Selected = await catalog.ItemAsync(selectedGroup, selectedNumber, cancellationToken).ConfigureAwait(false);
        if (this.Selected is null)
        {
            this.Problems = [$"No item definition has group {selectedGroup} and number {selectedNumber}."];
            return;
        }

        this.ExcellentOptions = await catalog
            .ItemExcellentOptionsAsync(selectedGroup, selectedNumber, cancellationToken).ConfigureAwait(false);
        this.OptionLevels = await catalog
            .ItemOptionLevelsAsync(selectedGroup, selectedNumber, cancellationToken).ConfigureAwait(false);
        this.AncientSets = await catalog
            .ItemAncientSetsAsync(selectedGroup, selectedNumber, cancellationToken).ConfigureAwait(false);

        if (this.Selected.SkillNumber == StatIds.DinorantSkillNumber)
        {
            // AddOption matches the Dinorant's options by target attribute, so the page must too -
            // a bit whose option this item does not actually carry is one the command would ignore.
            var dinorant = new List<(int Bit, OptionLevelRow Option)>();
            foreach (var (bit, attribute) in DinorantBits)
            {
                if (this.OptionLevels.FirstOrDefault(o => o.AttributeId == attribute) is { } carried)
                {
                    dinorant.Add((bit, carried));
                }
            }

            this.DinorantOptions = dinorant;
        }

        this.OptionIsAmbiguous = !this.OptIsBitField
            && this.OptionLevels.Select(o => o.AttributeId).Distinct().Count() > 1;

        // The form posts bits; the command wants their sum. Doing it here rather than asking a GM
        // to add up 4 + 8 + 32 is the entire reason this page exists.
        var excellent = Sum(exbit);
        var option = this.OptIsBitField ? Sum(optbit) : opt;

        // AncientBonusLevel is 1 server-side when unset, so an unfilled form must not read as 0 -
        // that would trip the "only applies when Ancient is 1 or 2" check on every plain item.
        this.Requested = new ItemRequest(
            selectedGroup, selectedNumber, lvl, excellent, sk, lu, option, anc, ancLvl <= 0 ? 1 : ancLvl);

        var problems = GmCommands.ValidateItem(
            this.Selected,
            this.Requested,
            excellentMask: this.ExcellentOptions.Aggregate(0, (mask, o) => mask | o.BitValue),

            // null, not 0: a Dinorant's opt is not a level at all, so there is no maximum level to
            // check it against. Passing 0 would read as "this item has no option" and be wrong.
            highestOptionLevel: this.OptIsBitField
                ? (int?)null
                : this.OptionLevels.Select(o => o.Level).DefaultIfEmpty(0).Max(),
            ancientDiscriminators: [.. this.AncientSets.Select(a => a.Discriminator)]).ToList();

        if (this.OptIsBitField)
        {
            var optMask = this.DinorantOptions.Aggregate(0, (mask, pair) => mask | pair.Bit);
            if ((option & ~optMask) != 0)
            {
                problems.Add(
                    $"{this.Selected.Name} has no option for every bit of opt={option}; "
                    + $"the ones it does have add up to {optMask}.");
            }
        }

        this.Problems = problems;

        // A command is only offered when it would work. Handing over one that the server will
        // reject is worse than handing over nothing, because the failure happens in game.
        if (this.Problems.Count == 0)
        {
            this.Command = GmCommands.BuildItem(this.Requested);
        }

        static int Sum(int[]? bits) => bits is null ? 0 : bits.Where(b => b > 0).Distinct().Sum();
    }
}
