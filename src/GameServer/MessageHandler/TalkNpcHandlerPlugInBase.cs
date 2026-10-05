// <copyright file="TalkNpcHandlerPlugInBase.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameServer.MessageHandler;

using MUnique.OpenMU.GameLogic;
using MUnique.OpenMU.GameLogic.NPC;
using MUnique.OpenMU.GameLogic.PlayerActions;
using MUnique.OpenMU.Network.Packets.ClientToServer;

/// <summary>
/// Handler for talk npc request packets.
/// </summary>
internal abstract class TalkNpcHandlerPlugInBase : IPacketHandlerPlugIn
{
    /// <summary>
    /// The maximum distance between the player and the NPC. The client walks next to the NPC before it
    /// requests to talk; the tolerance covers the server-side position lagging a few steps behind.
    /// Without this check, NPCs like the vault or the chaos machine could be used from anywhere on the map.
    /// </summary>
    private const int MaximumNpcDistance = 8;

    /// <inheritdoc/>
    public virtual bool IsEncryptionExpected => false;

    /// <inheritdoc/>
    public byte Key => TalkToNpcRequest.Code;

    /// <summary>
    /// Gets the talk NPC action.
    /// </summary>
    protected abstract TalkNpcAction TalkNpcAction { get; }

    /// <inheritdoc/>
    public async ValueTask HandlePacketAsync(Player player, Memory<byte> packet)
    {
        if (packet.Length < TalkToNpcRequest.Length)
        {
            return;
        }

        TalkToNpcRequest message = packet;
        if (player.CurrentMap?.GetObject(message.NpcId) is NonPlayerCharacter npc
            && player.IsAlive
            && player.IsInRange(npc.Position, MaximumNpcDistance))
        {
            await this.TalkNpcAction.TalkToNpcAsync(player, npc).ConfigureAwait(false);
        }
    }
}