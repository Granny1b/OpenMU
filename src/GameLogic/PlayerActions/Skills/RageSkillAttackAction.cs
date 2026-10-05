// <copyright file="RageSkillAttackAction.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.GameLogic.PlayerActions.Skills;

using MUnique.OpenMU.GameLogic.Attributes;
using MUnique.OpenMU.GameLogic.PlugIns;
using MUnique.OpenMU.GameLogic.Views;
using MUnique.OpenMU.GameLogic.Views.World;

/// <summary>
/// Action for rage attacks.
/// </summary>
public class RageSkillAttackAction
{
    private const int MaximumTargetsPerAttack = 5;

    private const int HitsPerTarget = 2;

    /// <summary>
    /// The skills which are performed through this action: Dark Side and its master skill.
    /// Any other skill would get the multi-target, multi-hit behavior of Dark Side.
    /// </summary>
    private static readonly short[] RageSkillNumbers = [263, 559];

    /// <summary>
    /// Attacks the target with a rage skill.
    /// </summary>
    /// <param name="player">The player.</param>
    /// <param name="explicitTargetId">The explicit target identifier.</param>
    /// <param name="skillId">The skill identifier.</param>
    public async ValueTask AttackAsync(Player player, ushort explicitTargetId, ushort skillId)
    {
        var explicitTarget = player.GetObject(explicitTargetId) as IAttackable;
        if (player.SkillList is null || player.SkillList.GetSkill(skillId) is not { Skill: { } skillDefinition } skill
                                     || !RageSkillNumbers.Contains(skillDefinition.Number)
                                     || (explicitTarget is not null && !explicitTarget.IsInRange(player.Position, skillDefinition.Range)))
        {
            return;
        }

        if (!player.IsAlive
            || player.Attributes is not { } attributes
            || attributes[Stats.IsStunned] > 0
            || attributes[Stats.IsAsleep] > 0
            || player.IsAtSafezone())
        {
            return;
        }

        if (player.GameContext.PlugInManager.GetPlugInPoint<ISpeedHackCheatCheckPlugIn>() is { } speedCheck)
        {
            var eventArgs = new SpeedHackCheckEventArgs();
            await speedCheck.AttackCheatCheckAsync(player, eventArgs).ConfigureAwait(false);
            if (eventArgs.IsCheatDetected)
            {
                return;
            }
        }

        if (!await player.TryConsumeForSkillAsync(skill).ConfigureAwait(false))
        {
            return;
        }

        var targets = new List<IAttackable>(MaximumTargetsPerAttack);
        if (explicitTarget is not null)
        {
            targets.Add(explicitTarget);
        }

        targets.AddRange(
            player.CurrentMap!
                .GetAttackablesInRange(player.Position, skill.Skill!.Range)
                .Where(t => t != explicitTarget && t != player)
                .Where(t => t is not Player)
                .OrderBy(t => t.GetDistanceTo(player))
                .Take(MaximumTargetsPerAttack - targets.Count));

        await player.InvokeViewPlugInAsync<IShowRageAttackRangePlugIn>(p => p.ShowRageAttackRangeAsync(skillId, targets)).ConfigureAwait(false);

        if (!targets.Any())
        {
            return;
        }

        if (explicitTarget is null)
        {
            await player.ForEachWorldObserverAsync<IShowRageAttackPlugIn>(p => p.ShowAttackAsync(player, targets.First(), skillId), true).ConfigureAwait(false);
        }

        bool isCombo = false;
        if (player.ComboState is { } comboState)
        {
            isCombo = await comboState.RegisterSkillAsync(skill.Skill).ConfigureAwait(false);
            if (isCombo)
            {
                await player.ForEachWorldObserverAsync<IShowSkillAnimationPlugIn>(p => p.ShowComboAnimationAsync(player, explicitTarget), true).ConfigureAwait(false);
            }
        }

        _ = this.RunAttacksAsync(player, skill, targets, isCombo);
    }

    private async ValueTask RunAttacksAsync(Player player, SkillEntry skill, List<IAttackable> targets, bool isCombo)
    {
        try
        {
            for (int i = 0; i < HitsPerTarget; i++)
            {
                await Task.Delay(200).ConfigureAwait(false);
                foreach (var target in targets)
                {
                    await target.AttackByAsync(player, skill, isCombo).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            player.Logger.LogError(ex, "Error running rage attack.");
        }
    }
}