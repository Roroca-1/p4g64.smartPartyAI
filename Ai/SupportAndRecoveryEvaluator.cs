using System.Numerics;
using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

internal sealed class ActFreelyCureCandidate
{
    public AiDecision Decision { get; init; } = null!;
    public bool IsLifesaving { get; init; }
    public double NetAdvantageValue { get; init; }
}

internal sealed record EvaluatedSupportDecision(AiDecision Decision, double TrueNetValue);

/// <summary>
/// Evaluates healing (including Salvation/Youthful Wind turn economy), revive, ailment curing,
/// buff/debuff/wall maintenance, Spirit Drain, and recovery/cure consumables.
/// </summary>
internal sealed class SupportAndRecoveryEvaluator
{
    private readonly IBattleEngineBridge _bridge;
    private readonly TeamOffenseEstimator _teamEstimator;

    public SupportAndRecoveryEvaluator(IBattleEngineBridge bridge, TeamOffenseEstimator? teamEstimator = null)
    {
        _bridge = bridge;
        _teamEstimator = teamEstimator ?? new TeamOffenseEstimator(bridge);
    }

    public AiDecision? EvaluateRevive(
        BattleSnapshot battle,
        Config? config = null,
        bool allowItems = false,
        int maxSkillSpCost = int.MaxValue,
        bool commonItemsOnly = false,
        bool allowPartialHpRevive = true,
        TelegraphedThreatInfo? threats = null,
        bool allowTelegraphWeakRevive = true)
    {
        var deadAllies = battle.Allies
            .Where(a => a.IsDead)
            .ToList();
        if (deadAllies.Count == 0)
            return null;

        var actor = battle.Actor;
        var livingEnemies = battle.Enemies.Where(e => !e.IsDead).ToList();

        if (!allowTelegraphWeakRevive &&
            threats != null &&
            livingEnemies.Count > 0 &&
            (threats.TelegraphedElements.Count > 0 || threats.AnyBossCharged))
        {
            var sampleEnemy = livingEnemies[0];
            deadAllies = deadAllies
                .Where(a => !DefenseAndTelegraphEvaluator.IsCombatantWeakToBossCharge(_bridge, sampleEnemy, a, threats))
                .ToList();
            if (deadAllies.Count == 0)
                return null;
        }

        int GetReviveTier(CombatantSnapshot a)
        {
            if (AiCombatHelpers.IndexInRawTurnOrder(battle, a) >= 0)
                return 0;
            return a.Agility <= actor.Agility ? 1 : 2;
        }

        int GetReviveOrderKey(CombatantSnapshot a)
        {
            int rawIdx = AiCombatHelpers.IndexInRawTurnOrder(battle, a);
            return rawIdx >= 0 ? rawIdx : -a.Agility;
        }

        deadAllies = deadAllies
            .OrderBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
            .ThenBy(GetReviveTier)
            .ThenBy(GetReviveOrderKey)
            .ThenBy(a => a.SlotId)
            .ToList();

        bool deferFullReviveWhenBossAhead = threats != null &&
                                            threats.AnyBossCharged &&
                                            (config?.EnableTelegraphGuard ?? true) &&
                                            actor.HpRatio <= 0.85;
        var fullReviveTargets = deferFullReviveWhenBossAhead
            ? deadAllies.Where(a => !DoesChargedBossActAheadOfAlly(battle, livingEnemies, a)).ToList()
            : deadAllies;

        SkillMetadata? bestFullRevive = null;
        SkillMetadata? bestPartialRevive = null;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var skill = _bridge.GetSkill(skillId);
            if (!skill.IsRevive || !_bridge.CanUseSkill(actor, skillId))
                continue;
            if (skill.CostType == 2 && _bridge.GetEffectiveCost(actor, skillId) > maxSkillSpCost)
                continue;

            bool isFullRevive = skill.Power >= 100 || skill.IsFullHeal;
            if (isFullRevive)
            {
                if (bestFullRevive == null || skill.Power > bestFullRevive.Power)
                    bestFullRevive = skill;
            }
            else if (allowPartialHpRevive)
            {
                if (bestPartialRevive == null || skill.Power > bestPartialRevive.Power)
                    bestPartialRevive = skill;
            }
        }

        if (bestFullRevive != null && fullReviveTargets.Count > 0)
        {
            var target = fullReviveTargets[0];
            return new AiDecision
            {
                ActionKind = AiActionKind.Skill,
                SkillId = bestFullRevive.SkillId,
                Target = target,
                Score = 5000.0,
                Reason = $"{SkillCatalog.GetSkillName(bestFullRevive.SkillId)} -> revive {target.DisplayName}"
            };
        }

        if (bestPartialRevive != null)
        {
            var target = deadAllies[0];
            return new AiDecision
            {
                ActionKind = AiActionKind.Skill,
                SkillId = bestPartialRevive.SkillId,
                Target = target,
                Score = 5000.0,
                Reason = $"{SkillCatalog.GetSkillName(bestPartialRevive.SkillId)} -> revive {target.DisplayName}"
            };
        }

        if (allowItems && config != null && config.EnableSupportItemsInAi)
        {
            static bool IsFullReviveConsumable(BattleConsumableInfo item) =>
                item.Skill.Power >= 100 || item.Skill.IsFullHeal || item.Rarity == ConsumableRarityTier.Rare;

            var reviveItem = _bridge.GetAvailableConsumables(actor)
                .Where(i => i.Kind == BattleConsumableKind.Revive &&
                            (IsFullReviveConsumable(i) ? fullReviveTargets.Count > 0 : allowPartialHpRevive) &&
                            (!commonItemsOnly || i.Rarity == ConsumableRarityTier.Common) &&
                            AiCombatHelpers.IsConsumableAllowed(i, config))
                .OrderBy(i => i.Rarity)
                .ThenByDescending(i => i.EstimatedHpGain)
                .FirstOrDefault();

            if (reviveItem != null)
            {
                var target = IsFullReviveConsumable(reviveItem) ? fullReviveTargets[0] : deadAllies[0];
                return new AiDecision
                {
                    ActionKind = AiActionKind.Item,
                    ItemId = reviveItem.ItemId,
                    SkillId = reviveItem.SkillId,
                    Target = target,
                    Score = 4900.0,
                    Reason = $"Use {reviveItem.Name} -> revive {target.DisplayName} (stock={reviveItem.Quantity})"
                };
            }
        }

        return null;
    }

    internal static bool DoesChargedBossActAheadOfAlly(
        BattleSnapshot battle,
        List<CombatantSnapshot> livingEnemies,
        CombatantSnapshot deadAlly)
    {
        var chargedBosses = livingEnemies
            .Where(e => AiCombatHelpers.IsLikelyBossOrMiniboss(e) && (e.HasPowerCharge || e.HasMindCharge))
            .ToList();
        if (chargedBosses.Count == 0)
            return false;

        if (battle.RawRoundTurnOrder.Count > 0)
        {
            int deadIdx = AiCombatHelpers.IndexInRawTurnOrder(battle, deadAlly);
            foreach (var boss in chargedBosses)
            {
                int bossIdx = AiCombatHelpers.IndexInRawTurnOrder(battle, boss);
                if (bossIdx >= 0 && (deadIdx < 0 || bossIdx < deadIdx))
                    return true;
                if (bossIdx < 0 && deadIdx < 0 && boss.Agility > 0 && deadAlly.Agility > 0 && boss.Agility >= deadAlly.Agility)
                    return true;
            }
            return false;
        }

        if (battle.UpcomingRoundTurnOrder.Count > 0)
        {
            foreach (var boss in chargedBosses)
            {
                int bossIdx = AiCombatHelpers.IndexInUpcomingTurnOrder(battle, boss);
                if (bossIdx >= 0)
                {
                    if ((battle.Actor.Agility > 0 && deadAlly.Agility > battle.Actor.Agility) ||
                        (boss.Agility > 0 && deadAlly.Agility > 0 && boss.Agility >= deadAlly.Agility))
                    {
                        return true;
                    }
                }
                else
                {
                    bool deadThisRound = battle.Actor.Agility > 0 &&
                                         deadAlly.Agility > 0 &&
                                         deadAlly.Agility <= battle.Actor.Agility;
                    if (!deadThisRound && boss.Agility > 0 && deadAlly.Agility > 0 && boss.Agility >= deadAlly.Agility)
                        return true;
                }
            }
            return false;
        }

        foreach (var boss in chargedBosses)
        {
            if (boss.Agility <= 0 || deadAlly.Agility <= 0)
                continue;

            bool bossThisRound = boss.Agility <= battle.Actor.Agility;
            bool deadThisRound = deadAlly.Agility <= battle.Actor.Agility;
            if (bossThisRound && !deadThisRound)
                return true;
            if (!bossThisRound && deadThisRound)
                continue;
            if (boss.Agility >= deadAlly.Agility)
                return true;
        }

        return false;
    }

    public AiDecision? EvaluateHealing(
        BattleSnapshot battle,
        double hpRatioThreshold,
        bool allowNormalTriage,
        Config? config = null,
        bool allowItems = false,
        int maxSkillSpCost = int.MaxValue,
        bool commonItemsOnly = false)
    {
        var actor = battle.Actor;
        var livingAllies = battle.Allies.Where(a => !a.IsDead).ToList();
        if (livingAllies.Count == 0)
            return null;

        double effectiveThreshold = hpRatioThreshold;
        var injured = livingAllies
            .Where(a => a.HpRatio < effectiveThreshold)
            .OrderBy(a => a.HpRatio)
            .ThenBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
            .ToList();

        if (injured.Count == 0)
            return null;

        var healSkills = new List<SkillMetadata>(actor.EquippedSkills.Length);
        foreach (ushort id in actor.EquippedSkills)
        {
            if (id == 0)
                continue;
            var s = _bridge.GetSkill(id);
            if (s.IsHeal &&
                _bridge.CanUseSkill(actor, s.SkillId) &&
                (s.CostType != 2 || _bridge.GetEffectiveCost(actor, s.SkillId) <= maxSkillSpCost))
            {
                healSkills.Add(s);
            }
        }

        double emergencyBonus = injured[0].HpRatio < 0.35 ? 3000.0 : 1500.0;
        IReadOnlyList<BattleConsumableInfo> healItems = (allowItems && config != null && config.EnableSupportItemsInAi)
            ? GetAllowedHealConsumables(actor, config, commonItemsOnly)
            : Array.Empty<BattleConsumableInfo>();

        if (healSkills.Count == 0)
            return EvaluateHealingConsumable(healItems, livingAllies, injured, emergencyBonus);

        bool partyNeedsDefBuff = livingAllies.Count(a => AiCombatHelpers.NeedsStatRefresh(a.DefStage, a.EffectiveDefTurns, 2.5, isDebuff: false)) >= Math.Max(1, (livingAllies.Count + 1) / 2);
        bool partyNeedsHitBuff = livingAllies.Count(a => AiCombatHelpers.NeedsStatRefresh(a.HitEvaStage, a.EffectiveHitEvaTurns, 2.5, isDebuff: false)) >= Math.Max(1, (livingAllies.Count + 1) / 2);
        bool anyCuringAilment = livingAllies.Any(a => a.HasCuringAilment);
        var lowestAlly = injured[0];
        int deficit = Math.Max(0, lowestAlly.MaxHp - lowestAlly.CurrentHp);

        var aoeHeals = new List<SkillMetadata>(healSkills.Count);
        var singleHeals = new List<SkillMetadata>(healSkills.Count);
        for (int i = 0; i < healSkills.Count; i++)
        {
            if (healSkills[i].IsAoE)
                aoeHeals.Add(healSkills[i]);
            else
                singleHeals.Add(healSkills[i]);
        }

        if (injured.Count >= 2 || (singleHeals.Count == 0 && aoeHeals.Count > 0 && injured.Count >= 1))
        {
            if (aoeHeals.Count > 0)
            {
                int worstDeficit = injured.Max(a => Math.Max(0, a.MaxHp - a.CurrentHp));
                var chosen = PickMostEfficientHeal(aoeHeals, actor, lowestAlly, worstDeficit, anyCuringAilment, partyNeedsDefBuff, partyNeedsHitBuff);
                if (chosen != null)
                {
                    return new AiDecision
                    {
                        ActionKind = AiActionKind.Skill,
                        SkillId = chosen.SkillId,
                        Target = actor,
                        Score = emergencyBonus,
                        Reason = $"{SkillCatalog.GetSkillName(chosen.SkillId)} -> heal {injured.Count} allies (lowest {lowestAlly.DisplayName} at {lowestAlly.HpRatio * 100:F0}% HP)"
                    };
                }
            }

            // When lacking any AoE heal skill (e.g. only Dia), use one AoE item instead of 2+ turns of single-target heals
            var aoeItemDecision = TryPickAoEHealConsumable(healItems, livingAllies, injured.Count, lowestAlly, deficit, emergencyBonus);
            if (aoeItemDecision != null)
                return aoeItemDecision;
        }

        // Single-target heal on lowest HP% ally
        if (singleHeals.Count > 0)
        {
            var chosen = PickMostEfficientHeal(singleHeals, actor, lowestAlly, deficit, lowestAlly.HasCuringAilment, false, false);
            if (chosen != null)
            {
                double singleAmt = chosen.IsFullHeal ? lowestAlly.MaxHp : EstimateHealAmount(chosen, actor);
                // If best single heal (e.g. Dia) barely scratches a critical ally's deficit, upgrade to AoE skill/item or stronger 1-turn item
                if (singleAmt < deficit * 0.55 && lowestAlly.HpRatio < 0.45)
                {
                    if (aoeHeals.Count > 0)
                    {
                        var betterAoe = PickMostEfficientHeal(aoeHeals, actor, lowestAlly, deficit, anyCuringAilment, partyNeedsDefBuff, partyNeedsHitBuff);
                        if (betterAoe != null)
                        {
                            double aoeAmt = betterAoe.IsFullHeal ? lowestAlly.MaxHp : EstimateHealAmount(betterAoe, actor);
                            if (aoeAmt >= singleAmt * 1.6)
                            {
                                return new AiDecision
                                {
                                    ActionKind = AiActionKind.Skill,
                                    SkillId = betterAoe.SkillId,
                                    Target = actor,
                                    Score = emergencyBonus,
                                    Reason = $"{SkillCatalog.GetSkillName(betterAoe.SkillId)} (stronger heal needed) -> {lowestAlly.DisplayName} ({lowestAlly.HpRatio * 100:F0}%)"
                                };
                            }
                        }
                    }

                    var itemUpgrade = TryPickUpgradedHealConsumable(healItems, livingAllies, lowestAlly, deficit, singleAmt, emergencyBonus);
                    if (itemUpgrade != null)
                        return itemUpgrade;
                }

                return new AiDecision
                {
                    ActionKind = AiActionKind.Skill,
                    SkillId = chosen.SkillId,
                    Target = lowestAlly,
                    Score = emergencyBonus,
                    Reason = $"{SkillCatalog.GetSkillName(chosen.SkillId)} -> {lowestAlly.DisplayName} ({lowestAlly.CurrentHp}/{lowestAlly.MaxHp}, {lowestAlly.HpRatio * 100:F0}%)"
                };
            }
        }

        if ((allowNormalTriage || lowestAlly.HpRatio < Math.Max(0.40, effectiveThreshold)) && aoeHeals.Count > 0)
        {
            var aoeFallback = PickMostEfficientHeal(aoeHeals, actor, lowestAlly, deficit, anyCuringAilment, partyNeedsDefBuff, partyNeedsHitBuff);
            if (aoeFallback != null)
            {
                return new AiDecision
                {
                    ActionKind = AiActionKind.Skill,
                    SkillId = aoeFallback.SkillId,
                    Target = actor,
                    Score = emergencyBonus,
                    Reason = $"{SkillCatalog.GetSkillName(aoeFallback.SkillId)} (only AoE heal) -> save {lowestAlly.DisplayName} ({lowestAlly.HpRatio * 100:F0}%)"
                };
            }
        }

        return null;
    }

    private List<BattleConsumableInfo> GetAllowedHealConsumables(
        CombatantSnapshot actor,
        Config config,
        bool commonItemsOnly) =>
        _bridge.GetAvailableConsumables(actor)
            .Where(i => i.Kind == BattleConsumableKind.Heal &&
                        (!commonItemsOnly || i.Rarity == ConsumableRarityTier.Common) &&
                        AiCombatHelpers.IsConsumableAllowed(i, config))
            .ToList();

    private static AiDecision? TryPickAoEHealConsumable(
        IReadOnlyList<BattleConsumableInfo> items,
        List<CombatantSnapshot> livingAllies,
        int injuredCount,
        CombatantSnapshot lowestAlly,
        int deficit,
        double emergencyBonus)
    {
        if (items.Count == 0)
            return null;

        var aoeItem = items
            .Where(i => i.IsPartyWide)
            .OrderBy(i => EstimateHealItemAmount(i, lowestAlly) >= deficit * 0.70 ? 0 : 1)
            .ThenBy(i => i.Rarity)
            .ThenBy(i => EstimateHealItemAmount(i, lowestAlly))
            .FirstOrDefault();

        if (aoeItem == null)
            return null;

        return new AiDecision
        {
            ActionKind = AiActionKind.Item,
            ItemId = aoeItem.ItemId,
            SkillId = aoeItem.SkillId,
            Target = livingAllies[0],
            Targets = livingAllies,
            Score = emergencyBonus - 40.0,
            Reason = $"Use {aoeItem.Name} -> heal {(injuredCount == 1 ? $"1 ally ({lowestAlly.DisplayName})" : $"{injuredCount} allies")} (stock={aoeItem.Quantity})"
        };
    }

    private static AiDecision? TryPickUpgradedHealConsumable(
        IReadOnlyList<BattleConsumableInfo> items,
        List<CombatantSnapshot> livingAllies,
        CombatantSnapshot lowestAlly,
        int deficit,
        double singleSpellAmt,
        double emergencyBonus)
    {
        if (items.Count == 0)
            return null;

        double singleUseful = Math.Min(singleSpellAmt, deficit);
        int hurtAlliesCount = livingAllies.Count(a => a.HpRatio < 0.70);

        if (hurtAlliesCount >= 2)
        {
            var aoeUpgrade = items
                .Where(i => i.IsPartyWide &&
                            livingAllies.Sum(a => Math.Min(EstimateHealItemAmount(i, a), Math.Max(0, a.MaxHp - a.CurrentHp))) >= singleUseful * 1.6)
                .OrderBy(i => EstimateHealItemAmount(i, lowestAlly) >= deficit * 0.65 ? 0 : 1)
                .ThenBy(i => i.Rarity)
                .ThenBy(i => EstimateHealItemAmount(i, lowestAlly))
                .FirstOrDefault();

            if (aoeUpgrade != null)
            {
                return new AiDecision
                {
                    ActionKind = AiActionKind.Item,
                    ItemId = aoeUpgrade.ItemId,
                    SkillId = aoeUpgrade.SkillId,
                    Target = livingAllies[0],
                    Targets = livingAllies,
                    Score = emergencyBonus - 40.0,
                    Reason = $"Use {aoeUpgrade.Name} -> 1-turn party rescue for {lowestAlly.DisplayName} ({lowestAlly.HpRatio * 100:F0}%, stock={aoeUpgrade.Quantity})"
                };
            }
        }

        var stUpgrade = items
            .Where(i => !i.IsPartyWide && EstimateHealItemAmount(i, lowestAlly) >= singleSpellAmt * 1.6)
            .OrderBy(i => EstimateHealItemAmount(i, lowestAlly) >= deficit * 0.65 ? 0 : 1)
            .ThenBy(i => i.Rarity)
            .ThenBy(i => Math.Abs(EstimateHealItemAmount(i, lowestAlly) - deficit))
            .FirstOrDefault();

        if (stUpgrade == null)
            return null;

        return new AiDecision
        {
            ActionKind = AiActionKind.Item,
            ItemId = stUpgrade.ItemId,
            SkillId = stUpgrade.SkillId,
            Target = lowestAlly,
            Score = emergencyBonus - 40.0,
            Reason = $"Use {stUpgrade.Name} -> 1-turn save {lowestAlly.DisplayName} ({lowestAlly.HpRatio * 100:F0}%, stock={stUpgrade.Quantity})"
        };
    }

    private static AiDecision? EvaluateHealingConsumable(
        IReadOnlyList<BattleConsumableInfo> items,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> injured,
        double emergencyBonus)
    {
        if (items.Count == 0)
            return null;

        var targetAlly = injured[0];
        int deficit = Math.Max(1, targetAlly.MaxHp - targetAlly.CurrentHp);

        if (injured.Count >= 2)
        {
            var aoeDecision = TryPickAoEHealConsumable(items, livingAllies, injured.Count, targetAlly, deficit, emergencyBonus);
            if (aoeDecision != null)
                return aoeDecision;
        }

        var singleItem = items
            .Where(i => !i.IsPartyWide)
            .OrderBy(i => EstimateHealItemAmount(i, targetAlly) >= deficit * 0.65 ? 0 : 1)
            .ThenBy(i => i.Rarity)
            .ThenBy(i => Math.Abs(EstimateHealItemAmount(i, targetAlly) - deficit))
            .FirstOrDefault();

        if (singleItem != null)
        {
            return new AiDecision
            {
                ActionKind = AiActionKind.Item,
                ItemId = singleItem.ItemId,
                SkillId = singleItem.SkillId,
                Target = targetAlly,
                Score = emergencyBonus - 40.0,
                Reason = $"Use {singleItem.Name} -> heal {targetAlly.DisplayName} ({targetAlly.HpRatio * 100:F0}% HP, stock={singleItem.Quantity})"
            };
        }

        return TryPickAoEHealConsumable(items, livingAllies, injured.Count, targetAlly, deficit, emergencyBonus);
    }

    private static int EstimateHealItemAmount(BattleConsumableInfo item, CombatantSnapshot target) =>
        item.Skill.IsFullHeal
            ? target.MaxHp
            : (item.EstimatedHpGain > 0 ? item.EstimatedHpGain : item.Skill.Power);

    private SkillMetadata? PickMostEfficientHeal(
        List<SkillMetadata> skills,
        CombatantSnapshot actor,
        CombatantSnapshot sampleTarget,
        int neededHp,
        bool hasAilment,
        bool partyNeedsDefBuff,
        bool partyNeedsHitBuff)
    {
        SkillMetadata? sufficientCheapest = null;
        int cheapestSortKey = int.MaxValue;

        SkillMetadata? strongest = null;
        double strongestHeal = -1.0;
        int strongestSp = int.MaxValue;

        foreach (var skill in skills)
        {
            int sp = _bridge.GetEffectiveCost(actor, skill.SkillId);
            double estHeal = skill.IsFullHeal ? sampleTarget.MaxHp : EstimateHealAmount(skill, actor);

            if (estHeal > strongestHeal || (Math.Abs(estHeal - strongestHeal) < 1.0 && sp < strongestSp))
            {
                strongestHeal = estHeal;
                strongest = skill;
                strongestSp = sp;
            }

            if (estHeal >= neededHp * 0.85)
            {
                int sortKey = sp;

                // Salvation (198): saves a turn when both HP & ailments need fixing; otherwise avoid overpaying over Mediarahan (197)
                if (skill.SkillId == 198)
                    sortKey = hasAilment ? Math.Max(0, sp - 18) : (skills.Any(x => x.SkillId == 197) ? sp + 100 : sp);
                // Hybrid heal+buffs (e.g. Youthful Wind = Mediarama + Masukukaja): inspect BuffMask dynamically
                else if (actor.SpRatio >= 0.30 &&
                         (((skill.BuffMask & SkillCatalog.BuffSukukaja) != 0 && partyNeedsHitBuff) ||
                          ((skill.BuffMask & SkillCatalog.BuffRakukaja) != 0 && partyNeedsDefBuff)))
                    sortKey = Math.Max(0, sp - 15);

                if (sortKey < cheapestSortKey)
                {
                    cheapestSortKey = sortKey;
                    sufficientCheapest = skill;
                }
            }
        }

        return sufficientCheapest ?? strongest;
    }

    private static double EstimateHealAmount(SkillMetadata skill, CombatantSnapshot actor)
    {
        double baseAmt = skill.Power * 1.45;
        if (actor.MagStage > 0)
            baseAmt *= 1.15;
        return baseAmt;
    }

    public AiDecision? EvaluateAilmentCure(
        BattleSnapshot battle,
        Config? config = null,
        bool allowItems = false,
        int maxSkillSpCost = int.MaxValue,
        bool commonItemsOnly = false,
        bool criticalOnly = false)
    {
        var actor = battle.Actor;
        var livingAllies = battle.Allies.Where(a => !a.IsDead).ToList();
        var cureTargets = livingAllies
            .Where(a => criticalOnly ? a.HasCriticalCuringAilment : (a.HasCuringAilment || a.NeedsRecoveryFromDownOrDizzy))
            .OrderByDescending(a => a.HasCriticalCuringAilment ? 2 : (a.HasCuringAilment ? 1 : 0))
            .ThenBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
            .ToList();

        if (cureTargets.Count == 0)
            return null;

        SkillMetadata? bestCure = null;
        CombatantSnapshot? bestTarget = null;
        int bestPriority = -1;
        bool bestTargetsProtag = false;
        int bestCoverCount = 0;
        int bestSp = int.MaxValue;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var skill = _bridge.GetSkill(skillId);
            if (!skill.IsAilmentCure || !_bridge.CanUseSkill(actor, skillId))
                continue;

            uint curedMask = SkillCatalog.GetCuredStateMask(skill);
            if (curedMask == 0)
                continue;

            int sp = skill.CostType == 2 ? _bridge.GetEffectiveCost(actor, skillId) : 0;
            if (sp > maxSkillSpCost)
                continue;
            if (skill.IsAoE)
            {
                var matched = cureTargets.Where(a => MatchesCureMask(a, skill, curedMask)).ToList();
                if (matched.Count == 0)
                    continue;

                int maxPrio = matched.Max(a => a.HasCriticalCuringAilment ? 2 : (a.HasCuringAilment ? 1 : 0));
                bool targetsProtag = matched.Any(a => a.UnitOrEnemyId == 1);
                if (IsBetterCureCandidate(maxPrio, targetsProtag, matched.Count, sp, bestPriority, bestTargetsProtag, bestCoverCount, bestSp))
                {
                    bestPriority = maxPrio;
                    bestTargetsProtag = targetsProtag;
                    bestCoverCount = matched.Count;
                    bestSp = sp;
                    bestCure = skill;
                    bestTarget = actor;
                }
            }
            else
            {
                var match = cureTargets.FirstOrDefault(a => MatchesCureMask(a, skill, curedMask));
                if (match == null)
                    continue;

                int prio = match.HasCriticalCuringAilment ? 2 : (match.HasCuringAilment ? 1 : 0);
                bool targetsProtag = match.UnitOrEnemyId == 1;
                if (IsBetterCureCandidate(prio, targetsProtag, 1, sp, bestPriority, bestTargetsProtag, bestCoverCount, bestSp))
                {
                    bestPriority = prio;
                    bestTargetsProtag = targetsProtag;
                    bestCoverCount = 1;
                    bestSp = sp;
                    bestCure = skill;
                    bestTarget = match;
                }
            }
        }

        if (bestCure != null && bestTarget != null)
        {
            return new AiDecision
            {
                ActionKind = AiActionKind.Skill,
                SkillId = bestCure.SkillId,
                Target = bestTarget,
                Score = 1400.0,
                Reason = $"{SkillCatalog.GetSkillName(bestCure.SkillId)} -> cure ailment on {bestCoverCount} ally/allies"
            };
        }

        if (allowItems && config != null && config.EnableSupportItemsInAi)
            return EvaluateBestCureItemForTargets(actor, livingAllies, cureTargets, config, scoreBase: 1360.0, commonItemsOnly);

        return null;
    }

    public ActFreelyCureCandidate? EvaluateActFreelyAilmentCure(
        BattleSnapshot battle,
        Config config,
        TeamOffenseContext teamCtx,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> livingEnemies,
        CombatantSnapshot primaryEnemy,
        bool allowItems)
    {
        if (!config.ActFreelySmartAilmentCure || livingEnemies.Count == 0)
            return null;

        var actor = battle.Actor;
        var afflicted = livingAllies
            .Where(a => a.HasCuringAilment || a.NeedsRecoveryFromDownOrDizzy)
            .ToList();
        if (afflicted.Count == 0)
            return null;

        var cureActions = GatherCureActions(actor, config, allowItems);
        if (cureActions.Count == 0)
            return null;

        bool allEnemiesDown = livingEnemies.All(e => !e.IsStanding);
        bool anyStandingEnemy = !allEnemiesDown;
        int enemyCountLeftToDown = livingEnemies.Count(e => e.IsStanding);
        var perAllyImpact = new Dictionary<CombatantSnapshot, (bool isLifesaving, double netGain)>(afflicted.Count);

        foreach (var target in afflicted)
        {
            bool lifesaving = false;
            double lifesaveScore = 0.0;

            // 1. Imminent Poison lethal tick (0x20 drains 10% MaxHP clamped to 1 HP before enemy hits)
            if (target.HasPoison)
            {
                int poisonBleed = Math.Clamp((int)(target.MaxHp * 0.10), 1, 999);
                if (target.CurrentHp <= poisonBleed + 20 || target.HpRatio <= 0.25)
                {
                    lifesaving = true;
                    lifesaveScore = Math.Max(lifesaveScore, target.UnitOrEnemyId == 1 ? 3600.0 : 3000.0);
                }
            }

            // 2. Confuse (0x10), Enervate (0x80), or Charm (0x100) sabotage risk before their upcoming turn
            if ((target.StateFlags & 0x190) != 0 &&
                (battle.UpcomingRoundTurnOrder.Count == 0 || AiCombatHelpers.HasUpcomingTurnInRound(battle, target)))
            {
                lifesaving = true;
                lifesaveScore = Math.Max(lifesaveScore, 2600.0);
            }

            // 3. Protagonist blocked from initiating All-Out Attack when party has downed the field
            if (target.UnitOrEnemyId == 1 &&
                !target.CanParticipateInAllOutAttack &&
                allEnemiesDown &&
                livingAllies.Count(a => !ReferenceEquals(a, target) && a.CanParticipateInAllOutAttack) >= 1)
            {
                lifesaving = true;
                lifesaveScore = Math.Max(lifesaveScore, 2400.0);
            }

            // 4. Quantified pre-turn carry DPS / Weakness unlock + status risk recovery value
            var currentProj = teamCtx.Projections.FirstOrDefault(p => ReferenceEquals(p.Ally, target));
            var curedProj = _teamEstimator.ProjectAllyPotentialIfCured(battle, target, livingEnemies, primaryEnemy, config);

            double curedDmg = curedProj.NextAnyEnemyDmg;
            double impairedDmg = (target.HasLossOfControl || target.IsDizzy || target.IsDown)
                ? 0.0
                : (target.HasPoison && !target.HasSilenced ? curedDmg * 0.70 : (currentProj?.NextAnyEnemyDmg ?? 0.0));

            double recoveredDmg = Math.Max(0.0, curedDmg - impairedDmg);
            bool blockedFromSkillsOrTurn = target.HasSilenced || target.HasLossOfControl || target.IsDizzy || target.IsDown;
            if (blockedFromSkillsOrTurn && curedProj.HitsStandingWeakness)
                recoveredDmg += anyStandingEnemy && enemyCountLeftToDown <= 1 ? 260.0 : 120.0;

            bool actsThisRound = battle.UpcomingRoundTurnOrder.Count == 0 ||
                                 AiCombatHelpers.HasUpcomingTurnInRound(battle, target);
            if (!actsThisRound)
                recoveredDmg *= 0.35;
            else if (ReferenceEquals(target, actor))
                recoveredDmg *= 0.60;

            double recoveredValue = recoveredDmg;

            // Rage (0x02): 2.0x incoming damage risk + loss of control
            if (target.HasRage)
            {
                bool bossFight = livingEnemies.Any(AiCombatHelpers.IsLikelyBossOrMiniboss);
                double rageRisk = (target.HpRatio < 0.70 ? 140.0 : 90.0) + (bossFight ? 60.0 : 0.0);
                if (target.UnitOrEnemyId == 1)
                    rageRisk += 50.0;
                recoveredValue += rageRisk;
            }

            // Healer Silence (0x08): recover healing/revive capability when teammates are hurt or KO'd
            if (target.HasSilenced)
            {
                bool isHealer = target.EquippedSkills.Any(id =>
                {
                    if (id == 0)
                        return false;
                    var s = _bridge.GetSkill(id);
                    return s.IsHeal || s.IsRevive;
                });
                if (isHealer)
                {
                    bool anyDead = battle.Allies.Any(a => a.IsDead);
                    int hurtCount = livingAllies.Count(a => a.HpRatio < 0.65);
                    bool anyCriticalHp = livingAllies.Any(a => a.HpRatio < 0.45);
                    if (anyDead || hurtCount > 0)
                    {
                        double healerBonus = anyDead
                            ? 220.0
                            : (anyCriticalHp ? 180.0 : 100.0 + Math.Min(2, hurtCount - 1) * 40.0);
                        if (!actsThisRound)
                            healerBonus *= 0.60;
                        recoveredValue += healerBonus;
                    }
                }
            }

            // Exhaust (0x40): 10% MaxSP drain/turn + 1.25x incoming damage risk in multi-round fights
            if (target.HasExhaust && !allEnemiesDown && (!teamCtx.HasDamageModel || teamCtx.EstimatedRoundsRemaining >= 1.8))
            {
                bool bossFight = livingEnemies.Any(AiCombatHelpers.IsLikelyBossOrMiniboss);
                double roundsWeight = teamCtx.HasDamageModel
                    ? Math.Clamp(teamCtx.EstimatedRoundsRemaining - 1.0, 0.8, 2.5)
                    : 1.5;
                double spValue = target.MaxSp > 0 ? Math.Min(45.0, target.MaxSp * 0.10 * 1.5) * roundsWeight : 0.0;
                double incomingRisk = (target.HpRatio < 0.70 ? 75.0 : 40.0) + (bossFight ? 35.0 : 0.0);
                recoveredValue += spValue + incomingRisk;
            }

            // Dizzy (0x40000): skipped-turn recovery value
            if (target.IsDizzy)
            {
                double dizzyTurnValue = actsThisRound ? 90.0 : 35.0;
                if (target.UnitOrEnemyId == 1)
                    dizzyTurnValue += 40.0;
                recoveredValue += dizzyTurnValue;
            }

            perAllyImpact[target] = (lifesaving, lifesaving ? Math.Max(lifesaveScore, recoveredValue) : recoveredValue);
        }

        ActFreelyCureCandidate? best = null;
        double bestSortKey = double.NegativeInfinity;

        foreach (var action in cureActions)
        {
            if (action.Skill.IsAoE)
            {
                var covered = afflicted.Where(a => MatchesCureMask(a, action.Skill, action.CuredMask)).ToList();
                if (covered.Count == 0)
                    continue;

                bool anyLifesaving = covered.Any(a => perAllyImpact[a].isLifesaving);
                double sumGain = covered.Sum(a => perAllyImpact[a].netGain) - action.ResourcePenalty;
                double key = (anyLifesaving ? 10000.0 : 0.0) + sumGain;

                if (!anyLifesaving && sumGain <= 0.0)
                    continue;

                if (key > bestSortKey)
                {
                    bestSortKey = key;
                    best = new ActFreelyCureCandidate
                    {
                        IsLifesaving = anyLifesaving,
                        NetAdvantageValue = sumGain,
                        Decision = BuildCureDecision(action, livingAllies[0], livingAllies, Math.Max(1400.0, sumGain),
                            $"{action.Label} -> cure {covered.Count} high-value allies")
                    };
                }
            }
            else
            {
                foreach (var target in afflicted)
                {
                    if (!MatchesCureMask(target, action.Skill, action.CuredMask))
                        continue;

                    var (isLifesaving, netGainRaw) = perAllyImpact[target];
                    double netGain = netGainRaw - action.ResourcePenalty;
                    if (!isLifesaving && netGain <= 0.0)
                        continue;

                    double key = (isLifesaving ? 10000.0 : 0.0) + netGain;
                    if (key > bestSortKey)
                    {
                        bestSortKey = key;
                        best = new ActFreelyCureCandidate
                        {
                            IsLifesaving = isLifesaving,
                            NetAdvantageValue = netGain,
                            Decision = BuildCureDecision(action, target, new[] { target }, Math.Max(1400.0, netGain),
                                $"{action.Label} -> {(isLifesaving ? "save" : "restore carry")} {target.DisplayName} (+{netGain:F0} net)")
                        };
                    }
                }
            }
        }

        return best;
    }

    private readonly record struct CureActionOption(
        AiActionKind Kind,
        SkillMetadata Skill,
        ushort ItemId,
        uint CuredMask,
        double ResourcePenalty,
        string Label);

    private List<CureActionOption> GatherCureActions(
        CombatantSnapshot actor,
        Config config,
        bool allowItems)
    {
        var list = new List<CureActionOption>();
        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var skill = _bridge.GetSkill(skillId);
            if (!skill.IsAilmentCure || !_bridge.CanUseSkill(actor, skillId))
                continue;
            uint mask = SkillCatalog.GetCuredStateMask(skill);
            if (mask == 0)
                continue;

            int sp = _bridge.GetEffectiveCost(actor, skillId);
            list.Add(new CureActionOption(
                AiActionKind.Skill, skill, 0, mask, sp * 0.5, SkillCatalog.GetSkillName(skillId)));
        }

        if (allowItems && config.EnableSupportItemsInAi)
        {
            foreach (var item in _bridge.GetAvailableConsumables(actor))
            {
                if (!item.CanCureAilment || !AiCombatHelpers.IsConsumableAllowed(item, config))
                    continue;

                uint mask = SkillCatalog.GetCuredStateMask(item.Skill);
                if (mask == 0)
                    continue;

                double pen = item.Rarity switch
                {
                    ConsumableRarityTier.Rare => 150.0,
                    ConsumableRarityTier.Mid => 30.0,
                    _ => 10.0
                };
                list.Add(new CureActionOption(
                    AiActionKind.Item, item.Skill, item.ItemId, mask, pen, $"Use {item.Name}"));
            }
        }

        return list;
    }

    private AiDecision? EvaluateBestCureItemForTargets(
        CombatantSnapshot actor,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> cureTargets,
        Config config,
        double scoreBase,
        bool commonItemsOnly = false)
    {
        BattleConsumableInfo? bestItem = null;
        CombatantSnapshot? bestTarget = null;
        int bestPriority = -1;
        bool bestTargetsProtag = false;
        int bestCover = 0;
        ConsumableRarityTier bestRarity = ConsumableRarityTier.Rare;

        foreach (var item in _bridge.GetAvailableConsumables(actor))
        {
            if (!item.CanCureAilment ||
                (commonItemsOnly && item.Rarity != ConsumableRarityTier.Common) ||
                !AiCombatHelpers.IsConsumableAllowed(item, config))
                continue;

            uint curedMask = SkillCatalog.GetCuredStateMask(item.Skill);
            if (curedMask == 0)
                continue;

            if (item.IsPartyWide)
            {
                var matched = cureTargets.Where(a => MatchesCureMask(a, item.Skill, curedMask)).ToList();
                if (matched.Count == 0)
                    continue;

                int maxPrio = matched.Max(a => a.HasCriticalCuringAilment ? 2 : (a.HasCuringAilment ? 1 : 0));
                bool targetsProtag = matched.Any(a => a.UnitOrEnemyId == 1);
                if (bestItem == null ||
                    IsBetterCureCandidate(maxPrio, targetsProtag, matched.Count, (int)item.Rarity,
                        bestPriority, bestTargetsProtag, bestCover, (int)bestRarity))
                {
                    bestPriority = maxPrio;
                    bestTargetsProtag = targetsProtag;
                    bestCover = matched.Count;
                    bestRarity = item.Rarity;
                    bestItem = item;
                    bestTarget = livingAllies[0];
                }
            }
            else
            {
                var match = cureTargets.FirstOrDefault(a => MatchesCureMask(a, item.Skill, curedMask));
                if (match == null)
                    continue;

                int prio = match.HasCriticalCuringAilment ? 2 : (match.HasCuringAilment ? 1 : 0);
                bool targetsProtag = match.UnitOrEnemyId == 1;
                if (bestItem == null ||
                    IsBetterCureCandidate(prio, targetsProtag, 1, (int)item.Rarity,
                        bestPriority, bestTargetsProtag, bestCover, (int)bestRarity))
                {
                    bestPriority = prio;
                    bestTargetsProtag = targetsProtag;
                    bestCover = 1;
                    bestRarity = item.Rarity;
                    bestItem = item;
                    bestTarget = match;
                }
            }
        }

        if (bestItem == null || bestTarget == null)
            return null;

        return new AiDecision
        {
            ActionKind = AiActionKind.Item,
            ItemId = bestItem.ItemId,
            SkillId = bestItem.SkillId,
            Target = bestTarget,
            Targets = bestItem.IsPartyWide ? livingAllies : new[] { bestTarget },
            Score = scoreBase,
            Reason = $"Use {bestItem.Name} -> cure ailment on {bestCover} ally/allies (stock={bestItem.Quantity})"
        };
    }

    private static bool MatchesCureMask(CombatantSnapshot ally, SkillMetadata skill, uint curedMask) =>
        (!skill.TargetsDownOnly || ally.NeedsRecoveryFromDownOrDizzy) && (ally.StateFlags & curedMask) != 0;

    private static AiDecision BuildCureDecision(
        CureActionOption opt,
        CombatantSnapshot target,
        IReadOnlyList<CombatantSnapshot> targets,
        double score,
        string reason) => new()
    {
        ActionKind = opt.Kind,
        SkillId = opt.Skill.SkillId,
        ItemId = opt.ItemId,
        Target = target,
        Targets = opt.Kind == AiActionKind.Item && opt.Skill.IsAoE ? targets : null,
        Score = score,
        Reason = reason
    };

    private static bool IsBetterCureCandidate(
        int prio,
        bool targetsProtag,
        int cover,
        int costOrRarity,
        int bestPrio,
        bool bestTargetsProtag,
        int bestCover,
        int bestCostOrRarity) =>
        prio > bestPrio ||
        (prio == bestPrio && (cover > bestCover || (cover == bestCover && (targetsProtag != bestTargetsProtag ? targetsProtag : costOrRarity < bestCostOrRarity))));

    public AiDecision? EvaluateOffensiveSetupForAssault(
        BattleSnapshot battle,
        Config config,
        EvaluatedOffense? actorBestAttack,
        bool actorWantsToCharge,
        List<CombatantSnapshot>? livingAllies = null,
        List<CombatantSnapshot>? livingEnemies = null,
        TeamOffenseContext? prebuiltTeamCtx = null,
        CombatantSnapshot? prebuiltPrimaryEnemy = null)
    {
        if (!config.AllowOffensiveBuffsInFullAssault || battle.IsActorOnOneMore)
            return null;

        var actor = battle.Actor;
        if (actor.HasPowerCharge || actor.HasMindCharge)
            return null;

        livingAllies ??= AiCombatHelpers.CollectLiving(battle.Allies);
        livingEnemies ??= AiCombatHelpers.CollectLiving(battle.Enemies);
        if (livingAllies.Count == 0 || livingEnemies.Count == 0)
            return null;

        var primaryEnemy = AiCombatHelpers.SelectMaxCurrentHpEnemy(livingEnemies);
        var ctx = (prebuiltTeamCtx != null && ReferenceEquals(prebuiltPrimaryEnemy, primaryEnemy))
            ? prebuiltTeamCtx
            : _teamEstimator.BuildTeamContext(battle, config, livingAllies, livingEnemies, primaryEnemy, actorBestAttack);
        if (!ctx.HasDamageModel || ctx.EstimatedRoundsRemaining < 1.85)
            return null;

        var setup = _teamEstimator.SelectBestOffensiveSetup(
            battle,
            config,
            ctx,
            livingAllies,
            livingEnemies,
            primaryEnemy,
            actorBestAttack,
            includeUtilityBonuses: false,
            oppCostMultiplier: 1.02,
            ignoreResourceCosts: true);
        if (setup == null || setup.AddedTeamDamage < ctx.ActorOpportunityCost * 1.02)
            return null;

        if (_teamEstimator.ShouldCarryDeferSetupForCharge(battle, config, ctx, setup, actorWantsToCharge))
            return null;

        return BuildSupportDecision(setup.Skill, setup.Target, setup.Score, setup.Reason);
    }

    /// <summary>
    /// Evaluates emergency cheap recovery (<= 8 SP or common items) and high-ROI cheap
    /// offensive setups (Rebellion/Rakunda/Tarukaja <= 15 SP in multi-round fights) for Conserve SP.
    /// </summary>
    public AiDecision? EvaluateConserveSpSupport(
        BattleSnapshot battle,
        Config config,
        TeamOffenseContext teamCtx,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> livingEnemies,
        CombatantSnapshot primaryEnemy,
        EvaluatedOffense? bestFreeOrMinimalAttack,
        TelegraphedThreatInfo? precomputedThreats = null)
    {
        if (bestFreeOrMinimalAttack?.FinishesBattle == true || bestFreeOrMinimalAttack?.TriggersAllOutAttack == true)
            return null;

        bool battleLastsMoreThanOneRound = !teamCtx.HasDamageModel || teamCtx.EstimatedRoundsRemaining > 1.25;
        if (battleLastsMoreThanOneRound)
        {
            var actor = battle.Actor;
            bool hasDeadAllies = battle.Allies.Any(a => a.IsDead);
            bool criticalSelfOrMc = actor.HpRatio < 0.351 ||
                                    livingAllies.Any(a => a.UnitOrEnemyId == 1 && a.HpRatio < 0.30);
            if (criticalSelfOrMc)
            {
                var criticalHeal = EvaluateHealing(
                    battle,
                    hpRatioThreshold: 0.351,
                    allowNormalTriage: false,
                    config,
                    allowItems: true,
                    maxSkillSpCost: 8,
                    commonItemsOnly: true);
                if (criticalHeal != null)
                    return criticalHeal;
            }

            var threats = precomputedThreats ?? DefenseAndTelegraphEvaluator.AnalyzeTelegraphedThreats(battle, livingEnemies);
            bool isBossFight = livingEnemies.Any(AiCombatHelpers.IsLikelyBossOrMiniboss);
            bool suppressPartialForBossTriage = hasDeadAllies &&
                                                isBossFight &&
                                                livingAllies.Count >= 3 &&
                                                livingAllies.Any(a => a.HpRatio <= 0.55);
            bool allowPartialRevive = (!config.EnableTelegraphGuard || !threats.AnyBossCharged) &&
                                      !suppressPartialForBossTriage;
            var revive = EvaluateRevive(
                battle,
                config,
                allowItems: true,
                maxSkillSpCost: 8,
                commonItemsOnly: true,
                allowPartialHpRevive: allowPartialRevive,
                threats: threats,
                allowTelegraphWeakRevive: !config.EnableTelegraphGuard);
            if (revive != null)
                return revive;

            if (suppressPartialForBossTriage)
            {
                var stabilizeHeal = EvaluateHealing(
                    battle,
                    hpRatioThreshold: 0.56,
                    allowNormalTriage: true,
                    config,
                    allowItems: true,
                    maxSkillSpCost: 8,
                    commonItemsOnly: true);
                if (stabilizeHeal != null)
                    return stabilizeHeal;
            }

            var heal = EvaluateHealing(
                battle,
                hpRatioThreshold: 0.351,
                allowNormalTriage: false,
                config,
                allowItems: true,
                maxSkillSpCost: 8,
                commonItemsOnly: true);
            if (heal != null)
                return heal;

            var critCure = EvaluateAilmentCure(
                battle,
                config,
                allowItems: true,
                maxSkillSpCost: 8,
                commonItemsOnly: true,
                criticalOnly: true);
            if (critCure != null)
                return critCure;
        }

        var actorAfterTriage = battle.Actor;
        if (battle.IsActorOnOneMore ||
            actorAfterTriage.HasPowerCharge ||
            actorAfterTriage.HasMindCharge ||
            !teamCtx.HasDamageModel ||
            teamCtx.EstimatedRoundsRemaining < 2.8 ||
            teamCtx.TotalEnemyHp < 400)
        {
            return null;
        }

        var setup = _teamEstimator.SelectBestOffensiveSetup(
            battle,
            config,
            teamCtx,
            livingAllies,
            livingEnemies,
            primaryEnemy,
            bestFreeOrMinimalAttack,
            includeUtilityBonuses: false,
            oppCostMultiplier: 1.35,
            conserveSpMode: true);
        if (setup == null)
            return null;

        if (_teamEstimator.ShouldCarryDeferSetupForCharge(battle, config, teamCtx, setup, actorWantsToCharge: false))
            return null;

        return BuildSupportDecision(setup.Skill, setup.Target, setup.Score, setup.Reason);
    }

    public EvaluatedSupportDecision? EvaluateSupportMaintenanceWithNetValue(
        BattleSnapshot battle,
        Config config,
        bool strictPriority,
        EvaluatedOffense? actorBestAttack = null,
        bool actorWantsToCharge = false,
        List<CombatantSnapshot>? livingAllies = null,
        List<CombatantSnapshot>? livingEnemies = null,
        TeamOffenseContext? prebuiltTeamCtx = null,
        CombatantSnapshot? prebuiltPrimaryEnemy = null)
    {
        var actor = battle.Actor;
        livingAllies ??= AiCombatHelpers.CollectLiving(battle.Allies);
        livingEnemies ??= AiCombatHelpers.CollectLiving(battle.Enemies);
        if (livingAllies.Count == 0 || livingEnemies.Count == 0)
            return null;

        var primaryEnemy = AiCombatHelpers.SelectMaxCurrentHpEnemy(livingEnemies);
        int totalEnemyHp = 0;
        for (int i = 0; i < livingEnemies.Count; i++)
            totalEnemyHp += livingEnemies[i].CurrentHp;
        bool isToughFight = strictPriority
            ? (totalEnemyHp >= 350 || primaryEnemy.MaxHp >= 300)
            : (primaryEnemy.MaxHp >= 500 || totalEnemyHp >= 600);

        // 1. Dekaja if enemy has >= 2 positive buffs
        var buffedEnemy = livingEnemies.FirstOrDefault(e => e.PositiveBuffCount >= 2);
        if (buffedEnemy != null)
        {
            var dekaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffDekaja);
            if (dekaja != null)
            {
                return new EvaluatedSupportDecision(
                    BuildSupportDecision(dekaja, buffedEnemy, 900.0,
                        $"Dekaja -> strip {buffedEnemy.PositiveBuffCount} buffs from {buffedEnemy.DisplayName}"),
                    500.0);
            }
        }

        // 2. Dekunda if party has >= 2 negative debuffs
        if (livingAllies.Sum(a => a.NegativeDebuffCount) >= 2)
        {
            var dekunda = FindUsableSupport(_bridge, actor, SkillCatalog.BuffDekunda);
            if (dekunda != null)
                return new EvaluatedSupportDecision(BuildSupportDecision(dekunda, actor, 900.0, "Dekunda -> clear party debuffs"), 500.0);
        }

        if (!isToughFight)
            return null;

        var ctx = (prebuiltTeamCtx != null && ReferenceEquals(prebuiltPrimaryEnemy, primaryEnemy))
            ? prebuiltTeamCtx
            : _teamEstimator.BuildTeamContext(battle, config, livingAllies, livingEnemies, primaryEnemy, actorBestAttack);
        if (ctx.HasDamageModel &&
            (actorBestAttack?.FinishesBattle == true ||
             actorBestAttack?.TriggersAllOutAttack == true ||
             ctx.EstimatedRoundsRemaining <= 1.15))
        {
            return null;
        }

        // 3. Shield of Justice on high-HP bosses when affordable & battle isn't ending this round
        if (primaryEnemy.MaxHp >= 1200 &&
            livingAllies.Any(a => !a.HasShieldOfJustice) &&
            actor.SpRatio >= 0.45 &&
            (!ctx.HasDamageModel || ctx.EstimatedRoundsRemaining >= 1.8) &&
            !actor.HasPowerCharge && !actor.HasMindCharge)
        {
            var soj = FindUsableSupport(_bridge, actor, SkillCatalog.BuffShieldOfJustice);
            if (soj != null)
                return new EvaluatedSupportDecision(BuildSupportDecision(soj, actor, 880.0, "Shield of Justice -> protect party on tough boss"), 450.0);
        }

        int majority = Math.Max(1, (livingAllies.Count + 1) / 2);

        // 4. Data-driven Value-of-Setup (VoS) evaluation for offensive buffs/debuffs when damage model exists
        if (ctx.HasDamageModel)
        {
            var setup = _teamEstimator.SelectBestOffensiveSetup(
                battle, config, ctx, livingAllies, livingEnemies, primaryEnemy, actorBestAttack, includeUtilityBonuses: true);
            if (setup != null &&
                !_teamEstimator.ShouldCarryDeferSetupForCharge(battle, config, ctx, setup, actorWantsToCharge))
            {
                return new EvaluatedSupportDecision(
                    BuildSupportDecision(setup.Skill, setup.Target, Math.Max(780.0, setup.Score), setup.Reason),
                    setup.Score);
            }

            // Do not distract an already-Charged ally with passive defensive buffs on Act Freely
            if (!strictPriority && (actor.HasPowerCharge || actor.HasMindCharge))
                return null;

            var defFallback = EvaluateDefensiveSupportFallback(
                battle, actor, livingAllies, livingEnemies, primaryEnemy, majority, strictPriority, ctx.EstimatedRoundsRemaining);
            if (defFallback == null)
                return null;

            double defGross = ctx.EstimatedRoundsRemaining >= 2.5 ? 240.0 : 120.0;
            double defNet = strictPriority ? defGross : defGross - ctx.ActorOpportunityCost;
            return new EvaluatedSupportDecision(defFallback, defNet);
        }

        // Fallback priority table when synthetic unit tests omit offense damage values
        var heurFallback = EvaluateHeuristicSupportFallback(battle, actor, livingAllies, livingEnemies, primaryEnemy, majority, strictPriority);
        return heurFallback != null ? new EvaluatedSupportDecision(heurFallback, 250.0) : null;
    }

    private AiDecision? EvaluateDefensiveSupportFallback(
        BattleSnapshot battle,
        CombatantSnapshot actor,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> livingEnemies,
        CombatantSnapshot primaryEnemy,
        int majority,
        bool strictPriority,
        double estimatedRounds)
    {
        if (estimatedRounds < 1.8)
            return null;
        if (!strictPriority && (estimatedRounds < 2.2 || primaryEnemy.MaxHp < 700))
            return null;

        if (livingAllies.Count(a => AiCombatHelpers.NeedsStatRefresh(a.DefStage, a.EffectiveDefTurns, estimatedRounds, isDebuff: false)) >= majority)
        {
            var marakukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakukaja, aoeOnly: true);
            if (marakukaja != null)
                return BuildSupportDecision(marakukaja, actor, 820.0, "Marakukaja -> buff party defense");
        }

        if (AiCombatHelpers.NeedsStatRefresh(primaryEnemy.AtkStage, primaryEnemy.EffectiveAtkTurns, estimatedRounds, isDebuff: true) &&
            primaryEnemy.MaxHp >= 700)
        {
            var tarunda = FindUsableSupport(_bridge, actor, SkillCatalog.BuffTarunda);
            if (tarunda != null)
                return BuildSupportDecision(tarunda, primaryEnemy, 810.0, $"{SkillCatalog.GetSkillName(tarunda.SkillId)} -> lower {primaryEnemy.DisplayName} attack");
        }

        if (livingAllies.Count(a => AiCombatHelpers.NeedsStatRefresh(a.HitEvaStage, a.EffectiveHitEvaTurns, estimatedRounds, isDebuff: false)) >= majority)
        {
            var masukukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffSukukaja, aoeOnly: true);
            if (masukukaja != null)
                return BuildSupportDecision(masukukaja, actor, 800.0, "Masukukaja -> buff party Hit/Eva");
        }

        if (!strictPriority)
            return null;

        var defenseTarget = livingAllies
            .OrderBy(a => a.HpRatio)
            .ThenBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
            .FirstOrDefault(a => AiCombatHelpers.NeedsStatRefresh(a.DefStage, a.EffectiveDefTurns, estimatedRounds, isDebuff: false));
        if (defenseTarget != null && primaryEnemy.MaxHp >= 700)
        {
            var rakukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakukaja, aoeOnly: false);
            if (rakukaja != null)
                return BuildSupportDecision(rakukaja, defenseTarget, 770.0, $"Rakukaja -> {defenseTarget.DisplayName}");
        }

        return EvaluateAllyWeaknessWall(battle, livingAllies);
    }

    private AiDecision? EvaluateHeuristicSupportFallback(
        BattleSnapshot battle,
        CombatantSnapshot actor,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> livingEnemies,
        CombatantSnapshot primaryEnemy,
        int majority,
        bool strictPriority)
    {
        const double assumedRounds = 3.0;
        if (AiCombatHelpers.NeedsStatRefresh(primaryEnemy.AtkStage, primaryEnemy.EffectiveAtkTurns, assumedRounds, isDebuff: true) ||
            AiCombatHelpers.NeedsStatRefresh(primaryEnemy.DefStage, primaryEnemy.EffectiveDefTurns, assumedRounds, isDebuff: true) ||
            AiCombatHelpers.NeedsStatRefresh(primaryEnemy.HitEvaStage, primaryEnemy.EffectiveHitEvaTurns, assumedRounds, isDebuff: true))
        {
            var debilitate = FindExactBuffSkill(_bridge, actor, SkillCatalog.BuffDebilitate);
            if (debilitate != null)
                return BuildSupportDecision(debilitate, primaryEnemy, 860.0, $"Debilitate -> {primaryEnemy.DisplayName}");
        }

        var dragonHustle = FindExactBuffSkill(_bridge, actor, SkillCatalog.BuffHeatRiser, aoeOnly: true);
        if (dragonHustle != null &&
            livingAllies.Count(a =>
                AiCombatHelpers.NeedsStatRefresh(a.AtkStage, a.EffectiveAtkTurns, assumedRounds, isDebuff: false) ||
                AiCombatHelpers.NeedsStatRefresh(a.DefStage, a.EffectiveDefTurns, assumedRounds, isDebuff: false)) >= majority &&
            actor.SpRatio >= 0.50)
        {
            return BuildSupportDecision(dragonHustle, actor, 850.0, "Dragon Hustle -> team Heat Riser");
        }

        if (livingAllies.Count(a => AiCombatHelpers.NeedsStatRefresh(a.AtkStage, a.EffectiveAtkTurns, assumedRounds, isDebuff: false)) >= majority)
        {
            var matarukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffTarukaja, aoeOnly: true);
            if (matarukaja != null)
                return BuildSupportDecision(matarukaja, actor, 840.0, "Matarukaja -> buff party attack");
        }

        if (livingEnemies.Count(e => AiCombatHelpers.NeedsStatRefresh(e.DefStage, e.EffectiveDefTurns, assumedRounds, isDebuff: true)) >= 2)
        {
            var marakunda = FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakunda, aoeOnly: true);
            if (marakunda != null)
                return BuildSupportDecision(marakunda, primaryEnemy, 835.0, $"{SkillCatalog.GetSkillName(marakunda.SkillId)} -> lower enemies' defense");
        }

        if (AiCombatHelpers.NeedsStatRefresh(primaryEnemy.DefStage, primaryEnemy.EffectiveDefTurns, assumedRounds, isDebuff: true))
        {
            var rakunda = FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakunda, aoeOnly: false);
            if (rakunda != null)
                return BuildSupportDecision(rakunda, primaryEnemy, 830.0, $"{SkillCatalog.GetSkillName(rakunda.SkillId)} -> lower {primaryEnemy.DisplayName} defense");
        }

        var defResult = EvaluateDefensiveSupportFallback(
            battle, actor, livingAllies, livingEnemies, primaryEnemy, majority, strictPriority, estimatedRounds: assumedRounds);
        if (defResult != null)
            return defResult;

        if (strictPriority)
        {
            var offenseTarget = livingAllies
                .OrderBy(a => a.AtkStage)
                .ThenBy(a => a.EffectiveAtkTurns)
                .ThenBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
                .FirstOrDefault(a => AiCombatHelpers.NeedsStatRefresh(a.AtkStage, a.EffectiveAtkTurns, assumedRounds, isDebuff: false));
            if (offenseTarget != null)
            {
                var heatRiser = FindExactBuffSkill(_bridge, actor, SkillCatalog.BuffHeatRiser, aoeOnly: false);
                if (heatRiser != null)
                    return BuildSupportDecision(heatRiser, offenseTarget, 790.0, $"Heat Riser -> {offenseTarget.DisplayName}");

                var tarukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffTarukaja, aoeOnly: false);
                if (tarukaja != null)
                    return BuildSupportDecision(tarukaja, offenseTarget, 780.0, $"Tarukaja -> {offenseTarget.DisplayName}");
            }

            var critTarget = livingAllies
                .Where(a => AiCombatHelpers.NeedsStatRefresh(a.CritRateStage, a.EffectiveCritRateTurns, assumedRounds, isDebuff: false))
                .OrderByDescending(a => a.EquippedSkills.Count(id => id != 0 && _bridge.GetSkill(id).Element == ElementId.Phys))
                .ThenBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
                .FirstOrDefault();
            if (critTarget != null)
            {
                var rebellion = FindUsableSupport(_bridge, actor, SkillCatalog.BuffCritRateUp, aoeOnly: false);
                if (rebellion != null)
                    return BuildSupportDecision(rebellion, critTarget, 760.0, $"Rebellion -> {critTarget.DisplayName}");
            }
        }

        return null;
    }

    public AiDecision? EvaluateSpRecoveryItem(
        BattleSnapshot battle,
        Config config,
        bool allowAllies,
        List<CombatantSnapshot> livingEnemies,
        List<CombatantSnapshot> livingAllies,
        EvaluatedOffense? bestAttack)
    {
        if (!config.EnableSpRecoveryItems || battle.IsActorOnOneMore || livingEnemies.Count == 0)
            return null;

        if (bestAttack != null && (bestAttack.TriggersAllOutAttack || bestAttack.FinishesBattle))
            return null;

        int remainingEnemyHp = livingEnemies.Sum(e => e.CurrentHp);
        double bestDirect = bestAttack?.DirectDamage ?? 0.0;
        if (remainingEnemyHp <= Math.Max(120.0, bestDirect * 1.35))
            return null;

        var actor = battle.Actor;
        double spThreshold = Math.Clamp(config.SpRecoveryItemThresholdPercent, 1, 90) / 100.0;
        if (!allowAllies && (actor.IsDead || actor.MaxSp <= 0 || actor.SpRatio > spThreshold || (actor.MaxSp - actor.CurrentSp) < 10))
            return null;

        var candidates = new List<CombatantSnapshot>(allowAllies ? livingAllies.Count : 1);
        if (allowAllies)
        {
            for (int i = 0; i < livingAllies.Count; i++)
            {
                var c = livingAllies[i];
                if (!c.IsDead && c.MaxSp > 0 && c.SpRatio <= spThreshold && (c.MaxSp - c.CurrentSp) >= 10)
                    candidates.Add(c);
            }
            if (candidates.Count == 0)
                return null;
            if (candidates.Count > 1)
            {
                candidates.Sort((x, y) =>
                {
                    int cmp = x.SpRatio.CompareTo(y.SpRatio);
                    if (cmp != 0)
                        return cmp;
                    int xPrio = ReferenceEquals(x, actor) ? 0 : (x.UnitOrEnemyId == 1 ? 1 : 2);
                    int yPrio = ReferenceEquals(y, actor) ? 0 : (y.UnitOrEnemyId == 1 ? 1 : 2);
                    return xPrio.CompareTo(yPrio);
                });
            }
        }
        else
        {
            candidates.Add(actor);
        }

        int maxEnemyHp = livingEnemies.Max(e => e.CurrentHp);
        bool isBossPartyEmergency = maxEnemyHp >= 1500 &&
                                    (livingAllies.Count(a => !a.IsDead && a.HpRatio <= 0.45) >= 2 ||
                                     (livingAllies.Count(a => !a.IsDead && a.SpRatio <= spThreshold) >= 2 &&
                                      livingAllies.Any(a => !a.IsDead && a.HpRatio <= 0.45)));

        int reserve = Math.Clamp(config.MinConsumableReserveCount, 0, 99);
        foreach (var beneficiary in candidates)
        {
            int spMissing = beneficiary.MaxSp - beneficiary.CurrentSp;
            bool allowRareHere = config.AllowRareFullSpItems || config.AllowRareConsumables || (isBossPartyEmergency && beneficiary.SpRatio <= 0.15);

            var items = _bridge.GetAvailableSpRecoveryItems(beneficiary);
            SpRecoveryItemInfo? bestItem = null;
            double bestScore = double.NegativeInfinity;

            foreach (var item in items)
            {
                if (item.Quantity <= reserve || item.EstimatedSpGain <= 0)
                    continue;
                if ((item.IsFullSp || item.IsPartyWide) && !allowRareHere)
                    continue;
                if (item.EstimatedSpGain > spMissing * 2.5 && item.EstimatedSpGain > 20)
                    continue;

                int usefulSp = item.IsPartyWide
                    ? livingAllies.Sum(a => Math.Min(item.EstimatedSpGain, Math.Max(0, a.MaxSp - a.CurrentSp)))
                    : Math.Min(item.EstimatedSpGain, spMissing);

                if (usefulSp < Math.Min(spMissing, 5))
                    continue;

                int excessSp = item.IsPartyWide ? 0 : Math.Max(0, item.EstimatedSpGain - spMissing);
                int hpNeed = item.IsPartyWide
                    ? livingAllies.Sum(a => Math.Max(0, a.MaxHp - a.CurrentHp))
                    : Math.Max(0, beneficiary.MaxHp - beneficiary.CurrentHp);
                double hpBonus = Math.Min(item.EstimatedHpGain, hpNeed) * 0.05;
                double rarePenalty = (item.IsFullSp ? 120.0 : 0.0) + (item.IsPartyWide ? 180.0 : 0.0);

                double score = usefulSp - 0.35 * excessSp - 0.15 * item.EstimatedSpGain + hpBonus - rarePenalty;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestItem = item;
                }
            }

            if (bestItem != null)
            {
                string targetLabel = bestItem.IsPartyWide ? "Party" : beneficiary.DisplayName;
                return new AiDecision
                {
                    ActionKind = AiActionKind.Item,
                    ItemId = bestItem.ItemId,
                    SkillId = bestItem.SkillId,
                    Target = bestItem.IsPartyWide ? livingAllies[0] : beneficiary,
                    Targets = bestItem.IsPartyWide ? livingAllies : new[] { beneficiary },
                    Score = 650.0,
                    Reason = $"Use {bestItem.Name} -> {targetLabel} (+{Math.Min(bestItem.EstimatedSpGain, spMissing)} SP at {beneficiary.SpRatio * 100:F0}%, stock={bestItem.Quantity})"
                };
            }
        }

        return null;
    }

    public AiDecision? EvaluateSpiritDrain(BattleSnapshot battle, Config config)
    {
        var actor = battle.Actor;
        SkillMetadata? drainSkill = null;
        foreach (ushort id in actor.EquippedSkills)
        {
            if (id == 0)
                continue;
            var s = _bridge.GetSkill(id);
            if (s.IsSpDrain && _bridge.CanUseSkill(actor, s.SkillId))
            {
                drainSkill = s;
                break;
            }
        }
        if (drainSkill == null)
            return null;

        var validTarget = battle.Enemies
            .Where(e => !e.IsDead)
            .FirstOrDefault(e =>
            {
                var aff = _bridge.QueryEffectiveAffinity(actor, e, drainSkill.Element, config.RespectAnalysisState);
                return !aff.IsBadHit && e.CurrentSp > 5;
            });

        if (validTarget == null)
            return null;

        return new AiDecision
        {
            ActionKind = AiActionKind.Skill,
            SkillId = drainSkill.SkillId,
            Target = validTarget,
            Score = 400.0,
            Reason = $"{SkillCatalog.GetSkillName(drainSkill.SkillId)} -> {validTarget.DisplayName} (recover SP at {actor.SpRatio * 100:F0}%)"
        };
    }

    private AiDecision? EvaluateAllyWeaknessWall(BattleSnapshot battle, List<CombatantSnapshot> livingAllies)
    {
        var actor = battle.Actor;
        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var skill = _bridge.GetSkill(skillId);
            if (skill.Element != ElementId.Support || !_bridge.CanUseSkill(actor, skillId))
                continue;

            ElementId? wallElem = SkillCatalog.GetWallElement(skill.BuffMask);
            if (wallElem == null)
                continue;

            foreach (var ally in livingAllies.OrderBy(a => a.UnitOrEnemyId == 1 ? 0 : 1))
            {
                if (ally.HasWallForElement(wallElem.Value))
                    continue;

                var aff = _bridge.QueryEffectiveAffinity(actor, ally, wallElem.Value, respectAnalysis: false);
                if (aff.IsWeak)
                {
                    return BuildSupportDecision(skill, ally, 760.0,
                        $"{SkillCatalog.GetSkillName(skillId)} -> cover {ally.DisplayName}'s {wallElem.Value} weakness");
                }
            }
        }

        return null;
    }

    internal static SkillMetadata? FindUsableSupport(
        IBattleEngineBridge bridge,
        CombatantSnapshot actor,
        uint requiredMask,
        bool? aoeOnly = null)
    {
        SkillMetadata? best = null;
        int bestRank = int.MaxValue;
        int bestSp = int.MaxValue;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var skill = bridge.GetSkill(skillId);
            if (skill.Element != ElementId.Support && skill.Element != ElementId.Recovery)
                continue;
            if ((skill.BuffMask & requiredMask) == 0)
                continue;
            if (aoeOnly.HasValue && skill.IsAoE != aoeOnly.Value)
                continue;
            if (!bridge.CanUseSkill(actor, skillId))
                continue;

            // Prefer pure Support skills over hybrid heal-buffs (Youthful Wind) and single-purpose buffs over Dragon Hustle/Heat Riser
            int rank = (skill.Element == ElementId.Support ? 0 : 100) + BitOperations.PopCount(skill.BuffMask);
            int sp = bridge.GetEffectiveCost(actor, skillId);
            if (rank < bestRank || (rank == bestRank && sp < bestSp))
            {
                best = skill;
                bestRank = rank;
                bestSp = sp;
            }
        }

        return best;
    }

    internal static SkillMetadata? FindExactBuffSkill(IBattleEngineBridge bridge, CombatantSnapshot actor, uint exactBuffMask, bool? aoeOnly = null)
    {
        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var skill = bridge.GetSkill(skillId);
            if (skill.Element != ElementId.Support || skill.BuffMask != exactBuffMask)
                continue;
            if (aoeOnly.HasValue && skill.IsAoE != aoeOnly.Value)
                continue;
            if (bridge.CanUseSkill(actor, skillId))
                return skill;
        }
        return null;
    }

    internal static AiDecision BuildSupportDecision(SkillMetadata skill, CombatantSnapshot target, double score, string reason) =>
        new()
        {
            ActionKind = AiActionKind.Skill,
            SkillId = skill.SkillId,
            Target = target,
            Score = score,
            Reason = reason
        };
}
