using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

/// <summary>
/// Evaluates offensive attacks, 1-More two-step lookahead, All-Out Attack triggers, Power/Mind Charge, and Break skills.
/// </summary>
internal sealed class OffenseEvaluator
{
    private const double LethalKillBonus = 200.0;
    private const double AllOutAttackBonus = 1000.0;

    private readonly IBattleEngineBridge _bridge;

    public OffenseEvaluator(IBattleEngineBridge bridge)
    {
        _bridge = bridge;
    }

    public List<SkillMetadata> GatherCandidateAttackSkills(
        CombatantSnapshot actor,
        bool zeroSpOnly,
        int safeHpFloorPercent)
    {
        var list = new List<SkillMetadata>(9) { _bridge.GetSkill(0) }; // Normal Attack always first candidate

        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;

            var skill = _bridge.GetSkill(skillId);
            if (!skill.IsDamaging && !skill.IsInstantKill)
                continue;
            if (!skill.TargetsEnemies || skill.IsSpDrain)
                continue;
            if (!_bridge.CanUseSkill(actor, skillId))
                continue;

            int effCost = _bridge.GetEffectiveCost(actor, skillId);
            if (skill.CostType == 2 && effCost > 0)
            {
                if (zeroSpOnly)
                    continue;
            }
            else if (skill.CostType == 1 && safeHpFloorPercent > 0)
            {
                double hpAfterPct = actor.MaxHp > 0
                    ? 100.0 * (actor.CurrentHp - effCost) / actor.MaxHp
                    : 0.0;
                if (actor.HpRatio * 100.0 < safeHpFloorPercent || hpAfterPct < 20.0)
                    continue;
            }

            list.Add(skill);
        }

        return list;
    }

    public EvaluatedOffense? EvaluateBestOffensiveAction(
        BattleSnapshot battle,
        Config config,
        List<SkillMetadata> candidates,
        int maxSpCost,
        bool preferLowCostTiebreak,
        double heavySpPenalty = 0.05)
    {
        var actor = battle.Actor;
        var livingEnemies = battle.Enemies.Where(e => !e.IsDead).ToList();
        if (livingEnemies.Count == 0)
            return null;

        EvaluatedOffense? best = null;
        int activeAlliesForAoa = battle.Allies.Count(a => !a.IsDead && !a.HasSilenced && (a.StateFlags & 0x1CC) == 0);

        foreach (var skill in candidates)
        {
            int spCost = skill.CostType == 2 ? _bridge.GetEffectiveCost(actor, skill.SkillId) : 0;
            if (spCost > maxSpCost)
                continue;
            int hpCost = skill.CostType == 1 ? _bridge.GetEffectiveCost(actor, skill.SkillId) : 0;

            if (skill.IsAoE)
            {
                var eval = EvaluateAoESkill(
                    battle, config, skill, livingEnemies, candidates, spCost, hpCost,
                    activeAlliesForAoa, preferLowCostTiebreak, heavySpPenalty);
                if (eval != null && (best == null || eval.Score > best.Score))
                    best = eval;
            }
            else
            {
                foreach (var enemy in livingEnemies)
                {
                    var eval = EvaluateSingleTargetSkill(
                        battle, config, skill, enemy, livingEnemies, candidates, spCost, hpCost,
                        activeAlliesForAoa, preferLowCostTiebreak, heavySpPenalty);
                    if (eval != null && (best == null || eval.Score > best.Score))
                        best = eval;
                }
            }
        }

        return best;
    }

    public AiDecision EvaluateOffenseWithFallbacks(
        BattleSnapshot battle,
        Config config,
        List<SkillMetadata> candidates,
        EvaluatedOffense? bestAttack)
    {
        if (!battle.IsActorOnOneMore && (bestAttack == null || !bestAttack.TriggersAllOutAttack))
        {
            var chargeDecision = EvaluateChargeAction(battle, config, candidates, bestAttack?.Score ?? 0.0);
            if (chargeDecision != null && (bestAttack == null || chargeDecision.Score > bestAttack.Score))
                return chargeDecision.Decision;
        }

        if (bestAttack != null && bestAttack.DirectDamage > 0.01)
            return bestAttack.Decision;

        var breakDecision = EvaluateBreakFallback(battle, config, candidates);
        if (breakDecision != null)
            return breakDecision;

        if (bestAttack != null)
            return bestAttack.Decision;

        return new AiDecision
        {
            ActionKind = AiActionKind.Guard,
            SkillId = 0,
            Score = 0,
            Reason = "No safe offensive option; Guarding"
        };
    }

    private EvaluatedOffense? EvaluateSingleTargetSkill(
        BattleSnapshot battle,
        Config config,
        SkillMetadata skill,
        CombatantSnapshot enemy,
        List<CombatantSnapshot> livingEnemies,
        List<SkillMetadata> allCandidates,
        int spCost,
        int hpCost,
        int activeAlliesForAoa,
        bool preferLowCostTiebreak,
        double heavySpPenalty)
    {
        var actor = battle.Actor;
        ElementId effectiveElem = ResolveAttackElement(actor, skill);
        var aff = _bridge.QueryEffectiveAffinity(actor, enemy, effectiveElem, config.RespectAnalysisState);

        // Strictly reject if Repel / Drain / Null / Karn / Shield of Justice / Unbreakable Shield
        if (aff.IsBadHit)
            return null;

        double directDmg = EvaluateTargetExpectedDamage(skill, actor, enemy, aff, config.RespectAnalysisState);
        if (directDmg <= 0.0)
            return null;

        double cappedDmg = Math.Min(directDmg, enemy.CurrentHp);
        bool killsTarget = !skill.IsInstantKill && !skill.IsPercentHpDamage && directDmg >= enemy.CurrentHp;
        bool finishesBattle = killsTarget && livingEnemies.Count == 1;

        // Verified in P4G.exe (0x14009c869): AilmentMask & 0x80000 skills (Hama/Mudo/Judgement) bypass knockdown & 1 More.
        bool canKnockDown = !skill.IsInstantKill && (skill.AilmentMask & 0x80000) == 0;
        bool hitsWeakness = _bridge.IsKnownWeakness(enemy, effectiveElem, aff);
        bool knocksDown = canKnockDown && hitsWeakness && enemy.IsStanding;
        bool grantsOneMore = knocksDown && (!killsTarget || livingEnemies.Count > 1);

        bool triggersAoa = false;
        if (knocksDown && activeAlliesForAoa >= 2)
        {
            bool anyOtherStanding = livingEnemies.Any(other => !ReferenceEquals(other, enemy) && other.IsStanding);
            bool anySurvivorAtAll = !killsTarget || livingEnemies.Count > 1;
            triggersAoa = anySurvivorAtAll && !anyOtherStanding;
        }

        double followupDmg = 0.0;
        string followupNote = string.Empty;
        if (grantsOneMore)
        {
            (followupDmg, followupNote) = SimulateBestOneMoreFollowup(
                battle, config, skill, targetEnemy: enemy, singleTargetStep1Dmg: directDmg, spCost, hpCost, allCandidates);
        }

        double accWeight = GetAccuracyWeight(skill);
        double twoStepTotal = directDmg + followupDmg;
        double score = cappedDmg * accWeight + 0.02 * directDmg;
        if (killsTarget)
            score += LethalKillBonus;
        if (grantsOneMore)
            score += followupDmg;
        if (triggersAoa)
            score += AllOutAttackBonus;

        if (preferLowCostTiebreak)
        {
            double spWeight = killsTarget ? Math.Max(heavySpPenalty, 1.5) : heavySpPenalty;
            score -= spWeight * spCost + 0.02 * hpCost;
        }

        string skillName = SkillCatalog.GetSkillName(skill.SkillId);
        string reason = grantsOneMore
            ? $"{skillName} -> {enemy.DisplayName} ({directDmg:F0} dmg + 1-More [{followupNote}: +{followupDmg:F0}]{(triggersAoa ? " + AOA" : "")})"
            : $"{skillName} -> {enemy.DisplayName} ({directDmg:F0}{(skill.IsInstantKill ? " IK-EV" : " dmg")}{(killsTarget ? ", LETHAL" : "")})";

        return new EvaluatedOffense
        {
            Decision = new AiDecision
            {
                ActionKind = skill.SkillId == 0 ? AiActionKind.NormalAttack : AiActionKind.Skill,
                SkillId = skill.SkillId,
                Target = enemy,
                Score = score,
                Reason = reason
            },
            DirectDamage = directDmg,
            TwoStepTotalDamage = twoStepTotal,
            Score = score,
            TriggersOneMore = grantsOneMore,
            TriggersAllOutAttack = triggersAoa,
            KillsAny = killsTarget,
            FinishesBattle = finishesBattle
        };
    }

    private EvaluatedOffense? EvaluateAoESkill(
        BattleSnapshot battle,
        Config config,
        SkillMetadata skill,
        List<CombatantSnapshot> livingEnemies,
        List<SkillMetadata> allCandidates,
        int spCost,
        int hpCost,
        int activeAlliesForAoa,
        bool preferLowCostTiebreak,
        double heavySpPenalty)
    {
        var actor = battle.Actor;
        double totalRawDmg = 0.0;
        double totalCappedDmg = 0.0;
        int killCount = 0;
        bool knocksDownAny = false;
        int enemiesDamaged = 0;
        var simHpByEnemy = new Dictionary<CombatantSnapshot, double>(livingEnemies.Count);
        var simDownByEnemy = new Dictionary<CombatantSnapshot, bool>(livingEnemies.Count);

        bool canKnockDown = !skill.IsInstantKill && (skill.AilmentMask & 0x80000) == 0;

        foreach (var enemy in livingEnemies)
        {
            var aff = _bridge.QueryEffectiveAffinity(actor, enemy, skill.Element, config.RespectAnalysisState);
            if (AiCombatHelpers.IsUnsafeReflectOrDrain(enemy, skill.Element, aff))
                return null;

            double dmg = 0.0;
            if (!aff.IsNull && !aff.BlockedByShield)
                dmg = EvaluateTargetExpectedDamage(skill, actor, enemy, aff, config.RespectAnalysisState);

            if (dmg > 0.0)
                enemiesDamaged++;

            totalRawDmg += dmg;
            totalCappedDmg += Math.Min(dmg, enemy.CurrentHp);

            bool killed = !skill.IsInstantKill && !skill.IsPercentHpDamage && dmg >= enemy.CurrentHp;
            if (killed)
                killCount++;

            bool hitsWeak = _bridge.IsKnownWeakness(enemy, skill.Element, aff);
            bool down = canKnockDown && hitsWeak && enemy.IsStanding;
            if (down)
                knocksDownAny = true;

            simHpByEnemy[enemy] = skill.IsInstantKill ? enemy.CurrentHp : Math.Max(0.0, enemy.CurrentHp - dmg);
            simDownByEnemy[enemy] = !enemy.IsStanding || down;
        }

        if (enemiesDamaged == 0 || totalRawDmg <= 0.0)
            return null;

        bool finishesBattle = killCount == livingEnemies.Count;
        bool anySurvivor = simHpByEnemy.Values.Any(hp => hp > 0.0);
        bool grantsOneMore = knocksDownAny && anySurvivor;
        bool triggersAoa = grantsOneMore && activeAlliesForAoa >= 2 &&
                           livingEnemies.All(e => simHpByEnemy[e] <= 0.0 || simDownByEnemy[e]);

        double followupDmg = 0.0;
        string followupNote = string.Empty;
        if (grantsOneMore)
        {
            (followupDmg, followupNote) = SimulateBestOneMoreFollowupAoE(
                battle, config, skill, simHpByEnemy, simDownByEnemy, spCost, hpCost, allCandidates);
        }

        double accWeight = GetAccuracyWeight(skill);
        double twoStepTotal = totalRawDmg + followupDmg;
        double score = totalCappedDmg * accWeight + 0.02 * totalRawDmg + killCount * LethalKillBonus;
        if (grantsOneMore)
            score += followupDmg;
        if (triggersAoa)
            score += AllOutAttackBonus;

        // Strongly avoid overpaying SP on AoE when hitting <= 1 enemy (single-target deals same damage cheaper)
        if (enemiesDamaged <= 1 && !skill.IsInstantKill)
            score -= 2.0 * spCost;

        if (preferLowCostTiebreak)
        {
            double spWeight = killCount > 0 ? Math.Max(heavySpPenalty, 1.5) : heavySpPenalty;
            score -= spWeight * spCost + 0.02 * hpCost;
        }

        string skillName = SkillCatalog.GetSkillName(skill.SkillId);
        string reason = grantsOneMore
            ? $"{skillName} (AoE {enemiesDamaged}/{livingEnemies.Count}) ({totalRawDmg:F0} dmg + 1-More [{followupNote}: +{followupDmg:F0}]{(triggersAoa ? " + AOA" : "")})"
            : $"{skillName} (AoE {enemiesDamaged}/{livingEnemies.Count}) ({totalRawDmg:F0}{(skill.IsInstantKill ? " IK-EV" : " dmg")}{(killCount > 0 ? $", {killCount} KO" : "")})";

        return new EvaluatedOffense
        {
            Decision = new AiDecision
            {
                ActionKind = AiActionKind.Skill,
                SkillId = skill.SkillId,
                Target = livingEnemies[0],
                Score = score,
                Reason = reason
            },
            DirectDamage = totalRawDmg,
            TwoStepTotalDamage = twoStepTotal,
            Score = score,
            TriggersOneMore = grantsOneMore,
            TriggersAllOutAttack = triggersAoa,
            KillsAny = killCount > 0,
            FinishesBattle = finishesBattle
        };
    }

    private (double followupDmg, string note) SimulateBestOneMoreFollowup(
        BattleSnapshot battle,
        Config config,
        SkillMetadata step1Skill,
        CombatantSnapshot targetEnemy,
        double singleTargetStep1Dmg,
        int step1SpCost,
        int step1HpCost,
        List<SkillMetadata> candidates)
    {
        var simHp = new Dictionary<CombatantSnapshot, double>(battle.Enemies.Count);
        var simDown = new Dictionary<CombatantSnapshot, bool>(battle.Enemies.Count);

        foreach (var e in battle.Enemies)
        {
            if (e.IsDead)
                continue;

            if (ReferenceEquals(e, targetEnemy))
            {
                simHp[e] = Math.Max(0.0, e.CurrentHp - singleTargetStep1Dmg);
                simDown[e] = true;
            }
            else
            {
                simHp[e] = e.CurrentHp;
                simDown[e] = !e.IsStanding;
            }
        }

        return SimulateBestOneMoreFollowupAoE(battle, config, step1Skill, simHp, simDown, step1SpCost, step1HpCost, candidates);
    }

    private (double followupDmg, string note) SimulateBestOneMoreFollowupAoE(
        BattleSnapshot battle,
        Config config,
        SkillMetadata step1Skill,
        Dictionary<CombatantSnapshot, double> simHpByEnemy,
        Dictionary<CombatantSnapshot, bool> simDownByEnemy,
        int step1SpCost,
        int step1HpCost,
        List<SkillMetadata> candidates)
    {
        var actor = battle.Actor;
        int simSp = actor.CurrentSp - step1SpCost;
        int simHp = actor.CurrentHp - step1HpCost;

        ElementId step1Elem = ResolveAttackElement(actor, step1Skill);
        bool step1ConsumedPower = step1Skill.SkillId == 0 || step1Elem == ElementId.Phys;
        bool simPowerCharged = actor.HasPowerCharge && !step1ConsumedPower;
        bool simMindCharged = actor.HasMindCharge && step1ConsumedPower;

        var surviving = simHpByEnemy.Where(kv => kv.Value > 0.0).Select(kv => kv.Key).ToList();
        if (surviving.Count == 0)
            return (0.0, "All Dead");

        double bestDmg = 0.0;
        string bestName = string.Empty;

        foreach (var skill2 in candidates)
        {
            int sp2 = skill2.CostType == 2 ? _bridge.GetEffectiveCost(actor, skill2.SkillId) : 0;
            int hp2 = skill2.CostType == 1 ? _bridge.GetEffectiveCost(actor, skill2.SkillId) : 0;
            if (sp2 > simSp || hp2 >= simHp)
                continue;

            ElementId elem2 = ResolveAttackElement(actor, skill2);
            bool usesPower2 = skill2.SkillId == 0 || elem2 == ElementId.Phys;
            bool assumeCharged = usesPower2 ? simPowerCharged : simMindCharged;
            bool assumeUncharged = !assumeCharged && ((usesPower2 && actor.HasPowerCharge) || (!usesPower2 && actor.HasMindCharge));
            double acc2 = GetAccuracyWeight(skill2);

            if (skill2.IsAoE)
            {
                double sum = 0.0;
                bool unsafeAoE = false;
                foreach (var enemy in surviving)
                {
                    var aff = _bridge.QueryEffectiveAffinity(actor, enemy, skill2.Element, config.RespectAnalysisState);
                    if (AiCombatHelpers.IsUnsafeReflectOrDrain(enemy, skill2.Element, aff))
                    {
                        unsafeAoE = true;
                        break;
                    }
                    if (!aff.CanDealDamage)
                        continue;

                    double raw = ComputeExpectedSkillDamageWithChargeState(
                        skill2, actor, enemy, aff, assumeCharged, assumeUncharged, config.RespectAnalysisState);
                    sum += Math.Min(raw, simHpByEnemy[enemy]) * acc2 + 0.02 * raw;
                }
                if (!unsafeAoE && sum > bestDmg)
                {
                    bestDmg = sum;
                    bestName = SkillCatalog.GetSkillName(skill2.SkillId);
                }
            }
            else
            {
                foreach (var enemy in surviving)
                {
                    var aff = _bridge.QueryEffectiveAffinity(actor, enemy, elem2, config.RespectAnalysisState);
                    if (aff.IsBadHit)
                        continue;

                    double raw = skill2.IsInstantKill
                        ? EstimateInstantKillExpectedValue(skill2, enemy, aff)
                        : ComputeExpectedSkillDamageWithChargeState(
                            skill2, actor, enemy, aff, assumeCharged, assumeUncharged, config.RespectAnalysisState);
                    if (raw <= 0.0)
                        continue;

                    double score2 = Math.Min(raw, simHpByEnemy[enemy]) * acc2 + 0.02 * raw;
                    if (!skill2.IsInstantKill && !skill2.IsPercentHpDamage && raw >= simHpByEnemy[enemy])
                        score2 += 50.0;

                    if (!skill2.IsInstantKill && (skill2.AilmentMask & 0x80000) == 0 &&
                        !simDownByEnemy[enemy] && _bridge.IsKnownWeakness(enemy, elem2, aff))
                    {
                        score2 += 120.0;
                    }

                    if (score2 > bestDmg)
                    {
                        bestDmg = score2;
                        bestName = $"{SkillCatalog.GetSkillName(skill2.SkillId)}->{enemy.DisplayName}";
                    }
                }
            }
        }

        return (bestDmg, bestName);
    }

    private EvaluatedOffense? EvaluateChargeAction(
        BattleSnapshot battle,
        Config config,
        List<SkillMetadata> candidates,
        double bestDirectScore)
    {
        var actor = battle.Actor;
        var livingEnemies = battle.Enemies.Where(e => !e.IsDead).ToList();
        if (livingEnemies.Count == 0)
            return null;

        int maxEnemyHp = livingEnemies.Max(e => e.CurrentHp);
        if (bestDirectScore >= maxEnemyHp * 0.75)
            return null;

        EvaluatedOffense? bestCharge = null;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            var skill = _bridge.GetSkill(skillId);
            if (skill.Element != ElementId.Support || !_bridge.CanUseSkill(actor, skillId))
                continue;

            bool isPowerCharge = (skill.BuffMask & SkillCatalog.BuffPowerCharge) != 0;
            bool isMindCharge = (skill.BuffMask & SkillCatalog.BuffMindCharge) != 0;
            if (!isPowerCharge && !isMindCharge)
                continue;
            if ((isPowerCharge && actor.HasPowerCharge) || (isMindCharge && actor.HasMindCharge))
                continue;

            int chargeSp = _bridge.GetEffectiveCost(actor, skillId);
            double bestChargedDmg = 0.0;
            double bestUnchargedDmg = 0.0;
            string bestFollowupName = string.Empty;

            foreach (var atk in candidates)
            {
                if (atk.SkillId == 0 || atk.IsInstantKill)
                    continue;

                bool matchesPhys = isPowerCharge && atk.Element == ElementId.Phys;
                bool matchesMag = isMindCharge && atk.Element != ElementId.Phys;
                if (!matchesPhys && !matchesMag)
                    continue;

                int atkSp = atk.CostType == 2 ? _bridge.GetEffectiveCost(actor, atk.SkillId) : 0;
                if (chargeSp + atkSp > actor.CurrentSp)
                    continue;

                if (atk.IsAoE)
                {
                    double groupUncharged = 0.0;
                    bool bad = false;
                    foreach (var enemy in livingEnemies)
                    {
                        var aff = _bridge.QueryEffectiveAffinity(actor, enemy, atk.Element, config.RespectAnalysisState);
                        if (AiCombatHelpers.IsUnsafeReflectOrDrain(enemy, atk.Element, aff))
                        {
                            bad = true;
                            break;
                        }
                        if (aff.CanDealDamage)
                            groupUncharged += ComputeExpectedSkillDamageWithChargeState(atk, actor, enemy, aff, false, true, config.RespectAnalysisState);
                    }
                    if (!bad && groupUncharged * 2.5 > bestChargedDmg)
                    {
                        bestUnchargedDmg = groupUncharged;
                        bestChargedDmg = groupUncharged * 2.5;
                        bestFollowupName = SkillCatalog.GetSkillName(atk.SkillId);
                    }
                    continue;
                }

                foreach (var enemy in livingEnemies)
                {
                    var aff = _bridge.QueryEffectiveAffinity(actor, enemy, atk.Element, config.RespectAnalysisState);
                    if (aff.IsBadHit)
                        continue;

                    double uncharged = ComputeExpectedSkillDamageWithChargeState(
                        atk, actor, enemy, aff, false, true, config.RespectAnalysisState);
                    double charged = uncharged * 2.5;
                    if (charged > bestChargedDmg)
                    {
                        bestChargedDmg = charged;
                        bestUnchargedDmg = uncharged;
                        bestFollowupName = SkillCatalog.GetSkillName(atk.SkillId);
                    }
                }
            }

            // 2-turn Charge + Skill = 2.5 * D vs 2 uncharged attacks = 2.0 * D -> effective 1-turn value is 1.25 * D!
            double amortizedTurnValue = bestChargedDmg * 0.52;
            if (bestUnchargedDmg > 0 && amortizedTurnValue > bestDirectScore * 1.05 && maxEnemyHp > bestUnchargedDmg * 1.6)
            {
                if (bestCharge == null || amortizedTurnValue > bestCharge.Score)
                {
                    bestCharge = new EvaluatedOffense
                    {
                        Decision = new AiDecision
                        {
                            ActionKind = AiActionKind.Skill,
                            SkillId = skillId,
                            Target = actor,
                            Score = amortizedTurnValue,
                            Reason = $"{SkillCatalog.GetSkillName(skillId)} (2.5x {bestFollowupName}: {bestChargedDmg:F0} > 2x {bestUnchargedDmg:F0})"
                        },
                        Score = amortizedTurnValue
                    };
                }
            }
        }

        return bestCharge;
    }

    private AiDecision? EvaluateBreakFallback(BattleSnapshot battle, Config config, List<SkillMetadata> candidates)
    {
        var actor = battle.Actor;
        var livingEnemies = battle.Enemies.Where(e => !e.IsDead).ToList();
        if (livingEnemies.Count == 0)
            return null;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            var skill = _bridge.GetSkill(skillId);
            if (skill.Element != ElementId.Support || !_bridge.CanUseSkill(actor, skillId))
                continue;

            ElementId? breakElem = SkillCatalog.GetBreakElement(skill.BuffMask);
            if (breakElem == null)
                continue;

            bool hasSpellOfElem = candidates.Any(c => ResolveAttackElement(actor, c) == breakElem.Value);
            if (!hasSpellOfElem)
                continue;

            bool needed = livingEnemies.Any(e =>
            {
                if (e.HasBreakForElement(breakElem.Value))
                    return false;
                var aff = _bridge.QueryEffectiveAffinity(actor, e, breakElem.Value, config.RespectAnalysisState);
                return aff.IsNull || aff.IsRepel || aff.IsDrain || aff.IsResist;
            });

            if (needed)
            {
                return new AiDecision
                {
                    ActionKind = AiActionKind.Skill,
                    SkillId = skillId,
                    Target = livingEnemies[0],
                    Score = 50.0,
                    Reason = $"{SkillCatalog.GetSkillName(skillId)} (all direct attacks blocked; stripping {breakElem.Value} resistance)"
                };
            }
        }

        return null;
    }

    private double EvaluateTargetExpectedDamage(
        SkillMetadata skill,
        CombatantSnapshot actor,
        CombatantSnapshot enemy,
        AffinityInfo aff,
        bool respectAnalysis)
    {
        if (skill.IsInstantKill)
            return EstimateInstantKillExpectedValue(skill, enemy, aff);

        double dmg = ComputeExpectedSkillDamage(skill, actor, enemy, aff, respectAnalysis);
        if (skill.IsPercentHpDamage && dmg <= 0.0)
        {
            double pct = Math.Clamp(skill.Power / 100.0, 0.0, 0.99);
            dmg = Math.Max(0.0, Math.Min(enemy.CurrentHp - 1.0, enemy.CurrentHp * pct * (aff.MultiplierPercent / 100.0)));
        }

        return dmg;
    }

    private static double EstimateInstantKillExpectedValue(SkillMetadata skill, CombatantSnapshot enemy, AffinityInfo aff)
    {
        if (AiCombatHelpers.IsLikelyBossOrMiniboss(enemy))
            return 0.0;

        // Instant death scales by hit probability and does not grant 1 More
        double killChance = aff.IsWeak ? 0.60 : (aff.IsResist ? 0.15 : 0.30);
        double accFactor = skill.Accuracy > 0 ? Math.Clamp(skill.Accuracy / 100.0, 0.3, 1.0) : 1.0;
        return enemy.CurrentHp * killChance * accFactor;
    }

    private double ComputeExpectedSkillDamage(
        SkillMetadata skill,
        CombatantSnapshot attacker,
        CombatantSnapshot target,
        AffinityInfo aff,
        bool respectAnalysis) =>
        ComputeExpectedSkillDamageWithChargeState(
            skill, attacker, target, aff,
            assumeChargedOverride: false,
            assumeUnchargedOverride: false,
            respectAnalysis: respectAnalysis);

    private double ComputeExpectedSkillDamageWithChargeState(
        SkillMetadata skill,
        CombatantSnapshot attacker,
        CombatantSnapshot target,
        AffinityInfo aff,
        bool assumeChargedOverride,
        bool assumeUnchargedOverride,
        bool respectAnalysis)
    {
        double singleHit = _bridge.EstimateSingleHitDamage(
            skill.SkillId, attacker, target, aff,
            assumeChargedOverride, assumeUnchargedOverride, respectAnalysis);

        return singleHit <= 0.0 ? 0.0 : singleHit * skill.ExpectedHits;
    }

    private ElementId ResolveAttackElement(CombatantSnapshot actor, SkillMetadata skill) =>
        skill.SkillId == 0 ? _bridge.GetAttackElement(actor) : skill.Element;

    private static double GetAccuracyWeight(SkillMetadata skill) =>
        skill.SkillId != 0 && skill.Accuracy is > 0 and < 95
            ? Math.Clamp(skill.Accuracy / 100.0 + 0.05, 0.5, 1.0)
            : 1.0;
}
