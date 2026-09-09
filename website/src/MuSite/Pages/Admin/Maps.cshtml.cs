using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using MuSite.Data;
using MuSite.Game;

namespace MuSite.Pages.Admin;

public sealed class MapsModel(GameCatalog catalog) : PageModel
{
    /// <summary>Every map, with its number for /move.</summary>
    public IReadOnlyList<MapRow> Maps { get; private set; } = [];

    /// <summary>The map whose spawns are expanded, if any.</summary>
    public MapRow? Selected { get; private set; }

    /// <summary>What spawns on the selected map.</summary>
    public IReadOnlyList<MonsterRow> Monsters { get; private set; } = [];

    /// <summary>An example /move for the selected map, to be edited before use.</summary>
    public string? MoveExample { get; private set; }

    public async Task OnGetAsync([FromQuery] int? number, CancellationToken cancellationToken)
    {
        this.Maps = await catalog.MapsAsync(cancellationToken).ConfigureAwait(false);

        if (number is not { } mapNumber)
        {
            return;
        }

        this.Selected = this.Maps.FirstOrDefault(m => m.Number == mapNumber);
        this.Monsters = await catalog.MonstersOnMapAsync(mapNumber, cancellationToken).ConfigureAwait(false);

        // The map is passed by NUMBER rather than name: MapIdOrName accepts either, and a number
        // cannot be ambiguous or contain a space, which the parser splits on.
        this.MoveExample = GmCommands.BuildMovePlayer(
            "CharacterName", mapNumber.ToString(System.Globalization.CultureInfo.InvariantCulture), null, null);
    }
}
