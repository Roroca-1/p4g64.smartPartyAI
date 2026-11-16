using System.Numerics;
using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

/// <summary>
/// Evaluates healing (including Salvation/Youthful Wind turn economy), revive, ailment curing,
/// buff/debuff/wall maintenance, Spirit Drain, and SP recovery consumables.
/// </summary>
internal sealed class SupportAndRecoveryEvaluator
{
    private readonly IBattleEngineBridge _bridge;

    public SupportAndRecoveryEvaluator(IBattleEngineBridge bridge)
    {
        _bridge = bridge;
    }

    public AiDecision? EvaluateRevive(BattleSnapshot battle)
    {
        var deadAllies = battle.Allies
            .Where(a => a.IsDead)
            .OrderBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
            .ThenBy(a => a.SlotId)
            .ToList();
        if (deadAllies.Count == 0)
            return null;

        var actor = battle.Actor;
        SkillMetadata? bestRevive = null;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            var skill = _bridge.GetSkill(skillId);
            if (!skill.IsRevive || !_bridge.CanUseSkill(actor, skillId))
                continue;

            if (bestRevive == null || skill.Power > bestRevive.Power)
                bestRevive = skill;
        }

        if (bestRevive == null)
            return null;

        return new AiDecision
        {
            ActionKind = AiActionKind.Skill,
            SkillId = bestRevive.SkillId,
            Target = deadAllies[0],
            Score = 5000.0,
            Reason = $"{SkillCatalog.GetSkillName(bestRevive.SkillId)} -> revive {deadAllies[0].DisplayName}"
        };
    }

    public AiDecision? EvaluateHealing(BattleSnapshot battle, double hpRatioThreshold, bool allowNormalTriage)
    {
        var actor = battle.Actor;
        var livingAllies = battle.Allies.Where(a => !a.IsDead).ToList();
        if (livingAllies.Count == 0)
            return null;

        var injured = livingAllies
            .Where(a => a.HpRatio < hpRatioThreshold)
            .OrderBy(a => a.HpRatio)
            .ThenBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
            .ToList();

        if (injured.Count == 0)
            return null;

        var healSkills = actor.EquippedSkills
            .Select(id => _bridge.GetSkill(id))
            .Where(s => s.IsHeal && _bridge.CanUseSkill(actor, s.SkillId))
            .ToList();

        if (healSkills.Count == 0)
            return null;

        bool partyNeedsDefBuff = livingAllies.Count(a => a.DefStage <= 0) >= Math.Max(1, (livingAllies.Count + 1) / 2);
        bool anyCuringAilment = livingAllies.Any(a => a.HasCuringAilment);
        bool useAoeHeal = injured.Count >= 2 || (healSkills.All(s => s.IsAoE) && injured.Count >= 1);
        double emergencyBonus = injured[0].HpRatio < 0.35 ? 3000.0 : 1500.0;

        if (useAoeHeal)
        {
            var aoeHeals = healSkills.Where(s => s.IsAoE).ToList();
            if (aoeHeals.Count > 0)
            {
                int worstDeficit = injured.Max(a => Math.Max(0, a.MaxHp - a.CurrentHp));
                var chosen = PickMostEfficientHeal(aoeHeals, actor, injured[0], worstDeficit, anyCuringAilment, partyNeedsDefBuff);
                if (chosen != null)
                {
                    return new AiDecision
                    {
                        ActionKind = AiActionKind.Skill,
                        SkillId = chosen.SkillId,
                        Target = actor,
                        Score = emergencyBonus,
                        Reason = $"{SkillCatalog.GetSkillName(chosen.SkillId)} -> heal {injured.Count} allies (lowest {injured[0].DisplayName} at {injured[0].HpRatio * 100:F0}% HP)"
                    };
                }
            }
        }

        // Single-target heal on lowest HP% ally
        var singleHeals = healSkills.Where(s => !s.IsAoE).ToList();
        var aoeHealsOnly = healSkills.Where(s => s.IsAoE).ToList();
        var lowestAlly = injured[0];
        int deficit = Math.Max(0, lowestAlly.MaxHp - lowestAlly.CurrentHp);

        if (singleHeals.Count > 0)
        {
            var chosen = PickMostEfficientHeal(singleHeals, actor, lowestAlly, deficit, lowestAlly.HasCuringAilment, partyNeedsDefBuff: false);
            if (chosen != null)
            {
                double singleAmt = chosen.IsFullHeal ? lowestAlly.MaxHp : EstimateHealAmount(chosen, actor);
                // If best single heal (e.g. Dia) barely scratches a critical ally's deficit while Mediarama/Mediarahan covers far more, upgrade to AoE heal
                if (singleAmt < deficit * 0.55 && lowestAlly.HpRatio < 0.45 && aoeHealsOnly.Count > 0)
                {
                    var betterAoe = PickMostEfficientHeal(aoeHealsOnly, actor, lowestAlly, deficit, lowestAlly.HasCuringAilment, partyNeedsDefBuff);
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

        if ((allowNormalTriage || lowestAlly.HpRatio < 0.40) && aoeHealsOnly.Count > 0)
        {
            var aoeFallback = PickMostEfficientHeal(aoeHealsOnly, actor, lowestAlly, deficit, lowestAlly.HasCuringAilment, partyNeedsDefBuff);
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

    private SkillMetadata? PickMostEfficientHeal(
        List<SkillMetadata> skills,
        CombatantSnapshot actor,
        CombatantSnapshot sampleTarget,
        int neededHp,
        bool hasAilment,
        bool partyNeedsDefBuff)
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
                // Youthful Wind (247): Mediarama + Marakukaja (saves 36 SP and 1 full turn when party lacks DefStage > 0)
                else if (skill.SkillId == 247 && partyNeedsDefBuff && actor.SpRatio >= 0.30)
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

    public AiDecision? EvaluateAilmentCure(BattleSnapshot battle)
    {
        var actor = battle.Actor;
        var cureTargets = battle.Allies
            .Where(a => !a.IsDead && (a.HasCuringAilment || a.NeedsRecoveryFromDownOrDizzy))
            .OrderByDescending(a => a.HasCriticalCuringAilment ? 2 : (a.HasCuringAilment ? 1 : 0))
            .ToList();

        if (cureTargets.Count == 0)
            return null;

        SkillMetadata? bestCure = null;
        CombatantSnapshot? bestTarget = null;
        int bestPriority = -1;
        int bestCoverCount = 0;
        int bestSp = int.MaxValue;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            var skill = _bridge.GetSkill(skillId);
            if (!skill.IsAilmentCure || !_bridge.CanUseSkill(actor, skillId))
                continue;

            uint curedMask = SkillCatalog.GetCuredStateMask(skill);
            if (curedMask == 0)
                continue;

            int sp = _bridge.GetEffectiveCost(actor, skillId);
            if (skill.IsAoE)
            {
                var matched = cureTargets.Where(a => (!skill.TargetsDownOnly || a.NeedsRecoveryFromDownOrDizzy) && (a.StateFlags & curedMask) != 0).ToList();
                if (matched.Count == 0)
                    continue;

                int maxPrio = matched.Max(a => a.HasCriticalCuringAilment ? 2 : (a.HasCuringAilment ? 1 : 0));
                if (IsBetterCureCandidate(maxPrio, matched.Count, sp, bestPriority, bestCoverCount, bestSp))
                {
                    bestPriority = maxPrio;
                    bestCoverCount = matched.Count;
                    bestSp = sp;
                    bestCure = skill;
                    bestTarget = actor;
                }
            }
            else
            {
                var match = cureTargets.FirstOrDefault(a => (!skill.TargetsDownOnly || a.NeedsRecoveryFromDownOrDizzy) && (a.StateFlags & curedMask) != 0);
                if (match == null)
                    continue;

                int prio = match.HasCriticalCuringAilment ? 2 : (match.HasCuringAilment ? 1 : 0);
                if (IsBetterCureCandidate(prio, 1, sp, bestPriority, bestCoverCount, bestSp))
                {
                    bestPriority = prio;
                    bestCoverCount = 1;
                    bestSp = sp;
                    bestCure = skill;
                    bestTarget = match;
                }
            }
        }

        if (bestCure == null || bestTarget == null)
            return null;

        return new AiDecision
        {
            ActionKind = AiActionKind.Skill,
            SkillId = bestCure.SkillId,
            Target = bestTarget,
            Score = 1400.0,
            Reason = $"{SkillCatalog.GetSkillName(bestCure.SkillId)} -> cure ailment on {bestCoverCount} ally/allies"
        };
    }

    private static bool IsBetterCureCandidate(int prio, int cover, int sp, int bestPrio, int bestCover, int bestSp) =>
        prio > bestPrio ||
        (prio == bestPrio && (cover > bestCover || (cover == bestCover && sp < bestSp)));

    public AiDecision? EvaluateSupportMaintenance(BattleSnapshot battle, Config config, bool strictPriority)
    {
        var actor = battle.Actor;
        var livingAllies = battle.Allies.Where(a => !a.IsDead).ToList();
        var livingEnemies = battle.Enemies.Where(e => !e.IsDead).ToList();
        if (livingAllies.Count == 0 || livingEnemies.Count == 0)
            return null;

        var primaryEnemy = livingEnemies.OrderByDescending(e => e.CurrentHp).First();
        bool isToughFight = strictPriority
            ? (livingEnemies.Sum(e => e.CurrentHp) >= 350 || primaryEnemy.MaxHp >= 300)
            : primaryEnemy.MaxHp >= 500;

        // 1. Dekaja if enemy has >= 2 positive buffs
        var buffedEnemy = livingEnemies.FirstOrDefault(e => e.PositiveBuffCount >= 2);
        if (buffedEnemy != null)
        {
            var dekaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffDekaja);
            if (dekaja != null)
            {
                return BuildSupportDecision(dekaja, buffedEnemy, 900.0,
                    $"Dekaja -> strip {buffedEnemy.PositiveBuffCount} buffs from {buffedEnemy.DisplayName}");
            }
        }

        // 2. Dekunda if party has >= 2 negative debuffs
        if (livingAllies.Sum(a => a.NegativeDebuffCount) >= 2)
        {
            var dekunda = FindUsableSupport(_bridge, actor, SkillCatalog.BuffDekunda);
            if (dekunda != null)
                return BuildSupportDecision(dekunda, actor, 900.0, "Dekunda -> clear party debuffs");
        }

        if (!isToughFight)
            return null;

        // 3. Shield of Justice on high-HP bosses when affordable
        if (primaryEnemy.MaxHp >= 1200 && livingAllies.Any(a => !a.HasShieldOfJustice))
        {
            var soj = FindUsableSupport(_bridge, actor, SkillCatalog.BuffShieldOfJustice);
            if (soj != null && actor.SpRatio >= 0.45)
                return BuildSupportDecision(soj, actor, 880.0, "Shield of Justice -> protect party on tough boss");
        }

        // 4. Debilitate on primary enemy when not already fully debuffed
        if (primaryEnemy.AtkStage >= 0 || primaryEnemy.DefStage >= 0 || primaryEnemy.HitEvaStage >= 0)
        {
            var debilitate = FindExactBuffSkill(actor, SkillCatalog.BuffDebilitate);
            if (debilitate != null)
                return BuildSupportDecision(debilitate, primaryEnemy, 860.0, $"Debilitate -> {primaryEnemy.DisplayName}");
        }

        // 5. Team buffs (only when at least half of living allies lack that buff)
        int majority = Math.Max(1, (livingAllies.Count + 1) / 2);

        var dragonHustle = FindExactBuffSkill(actor, SkillCatalog.BuffHeatRiser, aoeOnly: true);
        if (dragonHustle != null && livingAllies.Count(a => a.AtkStage <= 0 || a.DefStage <= 0) >= majority && actor.SpRatio >= 0.50)
            return BuildSupportDecision(dragonHustle, actor, 850.0, "Dragon Hustle -> team Heat Riser");

        if (livingAllies.Count(a => a.AtkStage <= 0) >= majority)
        {
            var matarukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffTarukaja, aoeOnly: true);
            if (matarukaja != null)
                return BuildSupportDecision(matarukaja, actor, 840.0, "Matarukaja -> buff party attack");
        }

        if (primaryEnemy.DefStage >= 0)
        {
            var rakunda = FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakunda);
            if (rakunda != null)
                return BuildSupportDecision(rakunda, primaryEnemy, 830.0, $"{SkillCatalog.GetSkillName(rakunda.SkillId)} -> lower {primaryEnemy.DisplayName} defense");
        }

        if (livingAllies.Count(a => a.DefStage <= 0) >= majority)
        {
            var marakukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakukaja, aoeOnly: true);
            if (marakukaja != null)
                return BuildSupportDecision(marakukaja, actor, 820.0, "Marakukaja -> buff party defense");
        }

        if (primaryEnemy.AtkStage >= 0 && primaryEnemy.MaxHp >= 700)
        {
            var tarunda = FindUsableSupport(_bridge, actor, SkillCatalog.BuffTarunda);
            if (tarunda != null)
                return BuildSupportDecision(tarunda, primaryEnemy, 810.0, $"{SkillCatalog.GetSkillName(tarunda.SkillId)} -> lower {primaryEnemy.DisplayName} attack");
        }

        if (livingAllies.Count(a => a.HitEvaStage <= 0) >= majority)
        {
            var masukukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffSukukaja, aoeOnly: true);
            if (masukukaja != null)
                return BuildSupportDecision(masukukaja, actor, 800.0, "Masukukaja -> buff party Hit/Eva");
        }

        // Single-target Heat Riser / Tarukaja / Rakukaja & weakness Walls (HealSupport mode)
        if (strictPriority)
        {
            var offenseTarget = livingAllies
                .OrderBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
                .ThenBy(a => a.AtkStage)
                .FirstOrDefault(a => a.AtkStage <= 0);
            if (offenseTarget != null)
            {
                var heatRiser = FindExactBuffSkill(actor, SkillCatalog.BuffHeatRiser, aoeOnly: false);
                if (heatRiser != null)
                    return BuildSupportDecision(heatRiser, offenseTarget, 790.0, $"Heat Riser -> {offenseTarget.DisplayName}");

                var tarukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffTarukaja, aoeOnly: false);
                if (tarukaja != null)
                    return BuildSupportDecision(tarukaja, offenseTarget, 780.0, $"Tarukaja -> {offenseTarget.DisplayName}");
            }

            var defenseTarget = livingAllies
                .OrderBy(a => a.HpRatio)
                .ThenBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
                .FirstOrDefault(a => a.DefStage <= 0);
            if (defenseTarget != null && primaryEnemy.MaxHp >= 700)
            {
                var rakukaja = FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakukaja, aoeOnly: false);
                if (rakukaja != null)
                    return BuildSupportDecision(rakukaja, defenseTarget, 770.0, $"Rakukaja -> {defenseTarget.DisplayName}");
            }

            var wall = EvaluateAllyWeaknessWall(battle, livingAllies);
            if (wall != null)
                return wall;
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
        var candidates = (allowAllies ? livingAllies : new List<CombatantSnapshot> { actor })
            .Where(c => !c.IsDead && c.MaxSp > 0 && c.SpRatio <= spThreshold && (c.MaxSp - c.CurrentSp) >= 10)
            .OrderBy(c => c.SpRatio)
            .ThenBy(c => ReferenceEquals(c, actor) ? 0 : (c.UnitOrEnemyId == 1 ? 1 : 2))
            .ToList();

        if (candidates.Count == 0)
            return null;

        int maxEnemyHp = livingEnemies.Max(e => e.CurrentHp);
        bool isBossPartyEmergency = maxEnemyHp >= 1500 &&
                                    (livingAllies.Count(a => !a.IsDead && a.HpRatio <= 0.45) >= 2 ||
                                     (livingAllies.Count(a => !a.IsDead && a.SpRatio <= spThreshold) >= 2 &&
                                      livingAllies.Any(a => !a.IsDead && a.HpRatio <= 0.45)));

        foreach (var beneficiary in candidates)
        {
            int spMissing = beneficiary.MaxSp - beneficiary.CurrentSp;
            bool allowRareHere = config.AllowRareFullSpItems || (isBossPartyEmergency && beneficiary.SpRatio <= 0.15);

            var items = _bridge.GetAvailableSpRecoveryItems(beneficiary);
            SpRecoveryItemInfo? bestItem = null;
            double bestScore = double.NegativeInfinity;

            foreach (var item in items)
            {
                if (item.Quantity <= 0 || item.EstimatedSpGain <= 0)
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
        var drainSkill = actor.EquippedSkills
            .Select(id => _bridge.GetSkill(id))
            .FirstOrDefault(s => s.IsSpDrain && _bridge.CanUseSkill(actor, s.SkillId));
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

    private SkillMetadata? FindExactBuffSkill(CombatantSnapshot actor, uint exactBuffMask, bool? aoeOnly = null)
    {
        foreach (ushort skillId in actor.EquippedSkills)
        {
            var skill = _bridge.GetSkill(skillId);
            if (skill.Element != ElementId.Support || skill.BuffMask != exactBuffMask)
                continue;
            if (aoeOnly.HasValue && skill.IsAoE != aoeOnly.Value)
                continue;
            if (_bridge.CanUseSkill(actor, skillId))
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
