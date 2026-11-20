using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

internal sealed class EvaluatedAilment
{
    public AiDecision Decision { get; init; } = null!;
    public double MarginalValue { get; init; }
    public double RawScore { get; init; }
}

/// <summary>
/// Evaluates enemy status ailment skills (Pulinpa/Tentarafoo, Evil Smile, Makajam, Poisma,
/// Valiant Dance, Soul Break, Enervation) and Ghastly Wail (instant kill on Feared targets)
/// against land probability, enemy immunities, existing ailments, fight duration, and tactic mode.
/// </summary>
internal sealed class AilmentEvaluator
{
    private readonly IBattleEngineBridge _bridge;

    public AilmentEvaluator(IBattleEngineBridge bridge)
    {
        _bridge = bridge;
    }

    public EvaluatedAilment? EvaluateBestAilmentAction(
        BattleSnapshot battle,
        Config config,
        TeamOffenseContext teamCtx,
        TacticMode tactic,
        List<CombatantSnapshot> livingEnemies,
        EvaluatedOffense? bestAttack)
    {
        var actor = battle.Actor;
        if (battle.IsActorOnOneMore || livingEnemies.Count == 0)
            return null;
        if (bestAttack?.FinishesBattle == true || bestAttack?.TriggersAllOutAttack == true || bestAttack?.KillsAny == true)
            return null;

        bool anyAllyHasGhastlyWail = battle.Allies.Any(a => !a.IsDead && a.EquippedSkills.Contains((ushort)87));
        double estRounds = teamCtx.HasDamageModel ? teamCtx.EstimatedRoundsRemaining : 2.5;
        int lowSpLimit = Math.Clamp(config.ConserveSpLowCostThreshold, 0, 100);

        EvaluatedAilment? best = null;
        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var skill = _bridge.GetSkill(skillId);
            if (!skill.TargetsEnemies || !_bridge.CanUseSkill(actor, skillId))
                continue;

            int spCost = skill.CostType == 2 ? _bridge.GetEffectiveCost(actor, skillId) : 0;
            if (tactic == TacticMode.ConserveSp && spCost > lowSpLimit)
                continue;

            if (skill.IsFearKillSkill)
            {
                var gw = EvaluateGhastlyWail(actor, skill, livingEnemies, tactic, spCost);
                if (gw != null && (best == null || gw.RawScore > best.RawScore))
                    best = gw;
                continue;
            }

            if (!skill.IsPureAilmentSkill || estRounds <= 1.35)
                continue;

            if (tactic == TacticMode.FullAssault &&
                !SkillCatalog.IsDamageAmplifyingAilment(skill.AilmentMask) &&
                !((skill.AilmentMask & 0x04u) != 0 && anyAllyHasGhastlyWail))
            {
                continue;
            }

            var eval = skill.IsAoE
                ? EvaluateAoEAilment(actor, skill, livingEnemies, teamCtx, config, tactic, estRounds, spCost, anyAllyHasGhastlyWail)
                : EvaluateSingleAilment(actor, skill, livingEnemies, teamCtx, config, tactic, estRounds, spCost, anyAllyHasGhastlyWail);

            if (eval != null && (best == null || eval.RawScore > best.RawScore))
                best = eval;
        }

        return best;
    }

    private static EvaluatedAilment? EvaluateGhastlyWail(
        CombatantSnapshot actor,
        SkillMetadata skill,
        List<CombatantSnapshot> livingEnemies,
        TacticMode tactic,
        int spCost)
    {
        CombatantSnapshot? firstFeared = null;
        int fearedCount = 0;
        double killValue = 0.0;
        for (int i = 0; i < livingEnemies.Count; i++)
        {
            var e = livingEnemies[i];
            if ((e.StateFlags & 0x04u) != 0 && !AiCombatHelpers.IsLikelyBossOrMiniboss(e))
            {
                firstFeared ??= e;
                fearedCount++;
                killValue += e.CurrentHp + 200.0;
            }
        }
        if (fearedCount == 0 || firstFeared == null)
            return null;

        double spPenalty = tactic switch
        {
            TacticMode.FullAssault => 0.0,
            TacticMode.ConserveSp => spCost * 2.0,
            _ => spCost * 0.35
        };
        double score = killValue - spPenalty;
        if (tactic == TacticMode.ConserveSp && killValue < spCost * 18.0)
            return null;

        return new EvaluatedAilment
        {
            Decision = new AiDecision
            {
                ActionKind = AiActionKind.Skill,
                SkillId = skill.SkillId,
                Target = firstFeared,
                Score = score,
                Reason = $"Ghastly Wail -> instant-kill {fearedCount} Feared enemy/enemies"
            },
            MarginalValue = score,
            RawScore = score
        };
    }

    private EvaluatedAilment? EvaluateAoEAilment(
        CombatantSnapshot actor,
        SkillMetadata skill,
        List<CombatantSnapshot> livingEnemies,
        TeamOffenseContext teamCtx,
        Config config,
        TacticMode tactic,
        double estRounds,
        int spCost,
        bool anyAllyHasGhastlyWail)
    {
        double totalEv = 0.0;
        int validTargets = 0;
        CombatantSnapshot? firstValid = null;

        foreach (var enemy in livingEnemies)
        {
            double ev = EvaluateTargetAilmentEv(actor, skill, enemy, teamCtx, config, tactic, estRounds, anyAllyHasGhastlyWail);
            if (ev <= 0.0)
                continue;

            totalEv += ev;
            validTargets++;
            firstValid ??= enemy;
        }

        if (validTargets == 0 || firstValid == null)
            return null;

        return FinalizeAilmentCandidate(skill, firstValid, totalEv, validTargets, spCost, teamCtx, tactic);
    }

    private EvaluatedAilment? EvaluateSingleAilment(
        CombatantSnapshot actor,
        SkillMetadata skill,
        List<CombatantSnapshot> livingEnemies,
        TeamOffenseContext teamCtx,
        Config config,
        TacticMode tactic,
        double estRounds,
        int spCost,
        bool anyAllyHasGhastlyWail)
    {
        CombatantSnapshot? bestTarget = null;
        double bestEv = 0.0;

        foreach (var enemy in livingEnemies)
        {
            double ev = EvaluateTargetAilmentEv(actor, skill, enemy, teamCtx, config, tactic, estRounds, anyAllyHasGhastlyWail);
            if (ev > bestEv)
            {
                bestEv = ev;
                bestTarget = enemy;
            }
        }

        if (bestTarget == null || bestEv <= 0.0)
            return null;

        return FinalizeAilmentCandidate(skill, bestTarget, bestEv, 1, spCost, teamCtx, tactic);
    }

    private double EvaluateTargetAilmentEv(
        CombatantSnapshot actor,
        SkillMetadata skill,
        CombatantSnapshot target,
        TeamOffenseContext teamCtx,
        Config config,
        TacticMode tactic,
        double estRounds,
        bool anyAllyHasGhastlyWail)
    {
        if (target.IsDead || AiCombatHelpers.IsLikelyBossOrMiniboss(target))
            return 0.0;
        if (target.HasAilmentFromSkill(skill))
            return 0.0;

        var aff = _bridge.QueryAilmentAffinity(actor, target, config.RespectAnalysisState);
        if (aff.IsBadHit || aff.MultiplierPercent <= 0)
            return 0.0;

        double basePct = skill.BaseAilmentChance > 0 ? skill.BaseAilmentChance : (skill.IsAoE ? 40.0 : 70.0);
        double pLand = Math.Clamp((aff.MultiplierPercent / 100.0) * (basePct / 100.0), 0.0, 1.0);
        if (pLand < 0.25)
            return 0.0;

        double teamNonActorDmg = Math.Max(0.0, teamCtx.TeamSustainedDamage - teamCtx.ActorOpportunityCost);
        double futureDmgOnTarget = Math.Min(target.CurrentHp, Math.Max(120.0, teamNonActorDmg * Math.Min(estRounds - 0.35, 2.0)));
        double horizonFactor = Math.Clamp(estRounds - 0.65, 0.5, 2.0);
        uint mask = skill.AilmentMask;
        double rawEv = 0.0;

        if ((mask & 0x02u) != 0) // Rage: 2.0x damage taken (and 2.0x enemy Phys dealt)
        {
            double dmgBoost = 0.90 * futureDmgOnTarget;
            double rageRiskPenalty = tactic switch
            {
                TacticMode.FullAssault => 0.0,
                TacticMode.ActFreely => 45.0,
                _ => 120.0
            };
            rawEv += dmgBoost - rageRiskPenalty;
        }

        if ((mask & 0x40u) != 0) // Exhaustion: 1.25x damage taken + SP drain
        {
            rawEv += 0.25 * futureDmgOnTarget + (tactic == TacticMode.FullAssault ? 0.0 : 45.0);
        }

        if ((mask & 0x20u) != 0) // Poison: 10% MaxHp bleed/round + 0.70x enemy attack dealt
        {
            double bleed = Math.Clamp(target.MaxHp * 0.10, 1.0, target.CurrentHp * 0.35) * horizonFactor;
            rawEv += bleed + (tactic == TacticMode.FullAssault ? 0.0 : 65.0);
        }

        if ((mask & 0x04u) != 0) // Fear: skip turns/flee + Ghastly Wail instant-kill setup
        {
            if (anyAllyHasGhastlyWail)
                rawEv += Math.Min(target.CurrentHp * 0.85, 320.0);
            rawEv += tactic switch
            {
                TacticMode.FullAssault => 0.0,
                TacticMode.HealSupport => 135.0,
                _ => 65.0
            };
        }

        if ((mask & 0x10u) != 0 || (mask & 0x08u) != 0 || (mask & 0x80u) != 0) // Confuse / Silence / Enervate
        {
            rawEv += tactic switch
            {
                TacticMode.FullAssault => 0.0,
                TacticMode.HealSupport => 140.0,
                _ => estRounds >= 2.0 ? 85.0 : 40.0
            };
        }

        return Math.Max(0.0, pLand * rawEv);
    }

    private static EvaluatedAilment? FinalizeAilmentCandidate(
        SkillMetadata skill,
        CombatantSnapshot primaryTarget,
        double totalEv,
        int validTargets,
        int spCost,
        TeamOffenseContext teamCtx,
        TacticMode tactic)
    {
        double spPenalty = tactic switch
        {
            TacticMode.FullAssault => 0.0,
            TacticMode.ConserveSp => spCost * 2.0,
            _ => spCost * 0.35
        };

        double marginal = totalEv - spPenalty;
        double minOpp = tactic switch
        {
            TacticMode.FullAssault => Math.Max(1.0, teamCtx.ActorOpportunityCost * 1.10),
            TacticMode.ConserveSp => Math.Max(teamCtx.ActorOpportunityCost * 1.40, spCost * 18.0),
            TacticMode.HealSupport => 65.0,
            _ => Math.Max(55.0, teamCtx.ActorOpportunityCost * 1.10)
        };

        if (marginal < minOpp)
            return null;

        double netAdvantage = marginal - teamCtx.ActorOpportunityCost;
        string scopeText = skill.IsAoE ? $"({validTargets} enemies)" : primaryTarget.DisplayName;
        return new EvaluatedAilment
        {
            Decision = new AiDecision
            {
                ActionKind = AiActionKind.Skill,
                SkillId = skill.SkillId,
                Target = primaryTarget,
                Score = marginal,
                Reason = $"{SkillCatalog.GetSkillName(skill.SkillId)} -> inflict ailment on {scopeText} (EV {totalEv:F0})"
            },
            MarginalValue = netAdvantage,
            RawScore = marginal
        };
    }
}
