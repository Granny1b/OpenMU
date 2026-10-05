// <copyright file="QuestStartAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Quests;

using MUnique.OpenMU.DataModel.Configuration.Quests;
using MUnique.OpenMU.GameLogic.Views.Inventory;
using MUnique.OpenMU.GameLogic.Views.Quest;

/// <summary>
/// A player action which implements the starting of a quest.
/// </summary>
public class QuestStartAction
{
    private const short LegacyQuestGroup = 0;

    /// <summary>
    /// Tries to start the quest of the given group and number for the specified player.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="group">The group.</param>
    /// <param name="number">The number.</param>
    public async ValueTask StartQuestAsync(Player player, short group, short number)
    {
        using var loggerScope = player.Logger.BeginScope(this.GetType());
        var quest = player.GetQuest(group, number);
        if (quest is null)
        {
            player.Logger.LogWarning("Failed, quest not found");
            return;
        }

        if (quest.MinimumCharacterLevel > player.Level || (quest.MaximumCharacterLevel > 0 && quest.MaximumCharacterLevel < player.Level))
        {
            player.Logger.LogDebug("Failed, character level {0} not in allowed range {1} to {2}.", player.Level, quest.MinimumCharacterLevel, quest.MaximumCharacterLevel);
            return;
        }

        var questState = player.SelectedCharacter!.QuestStates.FirstOrDefault(q => q.Group == group);
        if (questState is null)
        {
            questState = player.PersistenceContext.CreateNew<CharacterQuestState>();
            questState.Group = group;
            player.SelectedCharacter.QuestStates.Add(questState);
        }

        if (questState.ActiveQuest != null)
        {
            player.Logger.LogDebug("There is already an active quest of this group.");
            await player.InvokeViewPlugInAsync<IQuestProgressPlugIn>(p => p.ShowQuestProgressAsync(questState.ActiveQuest, false)).ConfigureAwait(false);
            return;
        }

        if (Equals(questState.LastFinishedQuest, quest) && !quest.Repeatable)
        {
            player.Logger.LogDebug("The quest is not repeatable.");
            return;
        }

        if (group == LegacyQuestGroup && !quest.Repeatable && !this.IsNextLegacyQuest(player, questState, quest))
        {
            player.Logger.LogWarning("Probably Hacker - player {Player} tried to start legacy quest {Quest} out of order.", player, quest.Number);
            return;
        }

        if (quest.RequiredStartMoney > 0)
        {
            if (player.TryRemoveMoney(quest.RequiredStartMoney))
            {
                await player.InvokeViewPlugInAsync<IUpdateMoneyPlugIn>(p => p.UpdateMoneyAsync()).ConfigureAwait(false);
            }
            else
            {
                await player.ShowLocalizedBlueMessageAsync(nameof(PlayerMessage.NotEnoughMoneyToProceed)).ConfigureAwait(false);
                return;
            }
        }

        await questState.ClearAsync(player.PersistenceContext).ConfigureAwait(false);
        questState.ActiveQuest = quest;
        await player.InvokeViewPlugInAsync<IQuestStartedPlugIn>(p => p.QuestStartedAsync(quest)).ConfigureAwait(false);
    }

    /// <summary>
    /// Determines whether the quest is the next legacy quest for the player.
    /// </summary>
    /// <remarks>
    /// The legacy quests build a chain, but only the last finished quest is stored. The client offers the
    /// first quest after the last finished one which applies to the character class. Without this check,
    /// earlier quests could be repeated (e.g. alternating between two quests for unlimited stat points),
    /// and quests with a class change could be started without the previous ones.
    /// </remarks>
    private bool IsNextLegacyQuest(Player player, CharacterQuestState questState, QuestDefinition quest)
    {
        var lastFinishedNumber = questState.LastFinishedQuest?.Number ?? -1;
        if (quest.Number <= lastFinishedNumber)
        {
            return false;
        }

        var characterClass = player.SelectedCharacter?.CharacterClass;
        var skipsApplicableQuest = player.GameContext.Configuration.Monsters
            .SelectMany(npc => npc.Quests)
            .Any(q => q.Group == quest.Group
                      && !q.Repeatable
                      && q.Number > lastFinishedNumber
                      && q.Number < quest.Number
                      && (q.QualifiedCharacter is null || Equals(q.QualifiedCharacter, characterClass)));
        return !skipsApplicableQuest;
    }
}