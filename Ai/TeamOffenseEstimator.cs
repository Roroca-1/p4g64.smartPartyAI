using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

internal sealed class AllyOffenseProjection
{
    public CombatantSnapshot Ally { get; init; } = null!;
    public double NextAnyEnemyDmg { get; init; }
    public double SustainedAnyEnemyDmg { get; init; }
    public double NextPrimaryEnemyDmg { get; init; }
    public double SustainedPrimaryEnemyDmg { get; init; }
    public int BestSkillStatStage { get; init; }
    public int BestSkillStatTurns { get; init; }
    public bool HasActiveChargeForBestSkill { get; init; }
    public double BestAoeUnitAvgDmg { get; init; }
    public bool ActsRemainingThisRound { get; init; }
    public bool HitsStandingWeakness { get; init; }
    public double BestPhysNextDmg { get; init; }
    public double BestPhysSustainedDmg { get; init; }
    public double BestPhysExpectedHits { get; init; }
    public byte BestPhysBaseCrit { get; init; }
    public bool BestPhysIsAoE { get; init; }
    public bool BestPhysHitsKnockdownEligibleTarget { get; init; }
    public bool BestPhysHasActivePowerCharge { get; init; }
}

internal sealed class TeamOffenseContext
{
    public List<AllyOffenseProjection> Projections { get; init; } = new();
    public double ActorOpportunityCost { get; init; }
    public double TeamNextDamage { get; init; }
    public double TeamSustainedDamage { get; init; }
    public double TotalEnemyHp { get; init; }
    public double EstimatedRoundsRemaining { get; init; }
    public double OffensiveBuffMultiplier { get; init; }
    public double DefenseDebuffMultiplier { get; init; }
    public bool HasRoundQueueData { get; init; }
    public bool HasDamageModel => TeamNextDamage > 0.01;
}

internal sealed class OffensiveSetupCandidate
{
    public SkillMetadata Skill { get; init; } = null!;
    public CombatantSnapshot Target { get; init; } = null!;
    public double AddedTeamDamage { get; init; }
    public double Score { get; init; }
    public string Reason { get; init; } = string.Empty;
    public uint BuffMask => Skill.BuffMask;
    public bool IsAoE => Skill.IsAoE;
}

/// <summary>
/// Models multi-turn team-wide damage projections so buffs/debuffs (Matarukaja, Rakunda,
/// Marakunda, Debilitate, Tarukaja) are evaluated against immediate attack opportunity cost,
/// active Power/Mind Charge states, and kill horizon.
/// </summary>
internal sealed class TeamOffenseEstimator
{
    private readonly IBattleEngineBridge _bridge;

    public TeamOffenseEstimator(IBattleEngineBridge bridge)
    {
        _bridge = bridge;
    }

    public TeamOffenseContext BuildTeamContext(
        BattleSnapshot battle,
        Config config,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> livingEnemies,
        CombatantSnapshot primaryEnemy,
        EvaluatedOffense? actorBestAttack)
    {
        bool hasQueue = battle.UpcomingRoundTurnOrder.Count > 0;
        var projections = new List<AllyOffenseProjection>(livingAllies.Count);
        double teamNext = 0.0;
        double teamSustained = 0.0;

        foreach (var ally in livingAllies)
        {
            bool actsThisRound = hasQueue && AiCombatHelpers.HasUpcomingTurnInRound(battle, ally);
            var proj = ProjectAllyOffense(ally, livingEnemies, primaryEnemy, config, ignoreAilments: false, actsThisRound);
            projections.Add(proj);
            teamNext += proj.NextAnyEnemyDmg;
            teamSustained += proj.SustainedAnyEnemyDmg;
        }

        double actorCost = actorBestAttack != null
            ? Math.Max(
                actorBestAttack.DirectDamage,
                Math.Max(
                    actorBestAttack.Score * (actorBestAttack.TriggersOneMore ? 1.0 : 0.90),
                    actorBestAttack.TriggersOneMore ? actorBestAttack.TwoStepTotalDamage + 40.0 : 0.0))
            : projections.FirstOrDefault(p => ReferenceEquals(p.Ally, battle.Actor))?.NextAnyEnemyDmg ?? 0.0;

        double totalEnemyHp = livingEnemies.Sum(e => (double)Math.Max(1, e.CurrentHp));
        double estRounds = 0.0;
        if (teamNext > 0.01)
        {
            double remAfterNext = Math.Max(0.0, totalEnemyHp - teamNext);
            estRounds = totalEnemyHp <= teamNext
                ? totalEnemyHp / teamNext
                : 1.0 + remAfterNext / Math.Max(1.0, teamSustained);
        }

        return new TeamOffenseContext
        {
            Projections = projections,
            ActorOpportunityCost = actorCost,
            TeamNextDamage = teamNext,
            TeamSustainedDamage = teamSustained,
            TotalEnemyHp = totalEnemyHp,
            EstimatedRoundsRemaining = estRounds,
            OffensiveBuffMultiplier = Math.Max(1.05, _bridge.GetOffensiveBuffMultiplier()),
            DefenseDebuffMultiplier = Math.Max(1.05, _bridge.GetDefenseDebuffMultiplier()),
            HasRoundQueueData = hasQueue
        };
    }

    public OffensiveSetupCandidate? SelectBestOffensiveSetup(
        BattleSnapshot battle,
        Config config,
        TeamOffenseContext ctx,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> livingEnemies,
        CombatantSnapshot primaryEnemy,
        EvaluatedOffense? actorBestAttack,
        bool includeUtilityBonuses,
        double oppCostMultiplier = 1.15,
        bool conserveSpMode = false,
        bool ignoreResourceCosts = false)
    {
        if (!ctx.HasDamageModel || battle.IsActorOnOneMore || livingEnemies.Count == 0)
            return null;

        if (conserveSpMode && ctx.EstimatedRoundsRemaining < 2.8)
            return null;

        if (actorBestAttack?.FinishesBattle == true || actorBestAttack?.TriggersAllOutAttack == true)
            return null;

        var actor = battle.Actor;
        // An already-Charged actor pays 2.5x opportunity cost delaying their strike; keep Charge momentum.
        if (actor.HasPowerCharge || actor.HasMindCharge)
            return null;

        // Short fight / overkill gate: skip setups if remaining unbuffed allies this round already win,
        // or if the fight ends within ~1.35 rounds unless an AoE buff flips remaining allies into a Round-1 finish.
        double buffDelta = ctx.OffensiveBuffMultiplier - 1.0;
        double debuffDelta = ctx.DefenseDebuffMultiplier - 1.0;
        if (ctx.HasRoundQueueData)
        {
            double remainingUnbuffedThisRound = ctx.Projections
                .Where(p => ReferenceEquals(p.Ally, actor) || p.ActsRemainingThisRound)
                .Sum(p => p.NextAnyEnemyDmg);
            if (remainingUnbuffedThisRound >= ctx.TotalEnemyHp)
                return null;

            double otherThisRound = ctx.Projections
                .Where(p => !ReferenceEquals(p.Ally, actor) && p.ActsRemainingThisRound)
                .Sum(p => p.NextAnyEnemyDmg);
            double bestRoundOneScale = 1.0 + Math.Max(
                SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, SkillCatalog.BuffTarukaja, aoeOnly: true) != null ? buffDelta : 0.0,
                SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakunda, aoeOnly: livingEnemies.Count >= 2 ? true : null) != null ? debuffDelta : 0.0);
            bool setupWinsThisRound = otherThisRound * bestRoundOneScale >= ctx.TotalEnemyHp;
            if (!setupWinsThisRound && ctx.EstimatedRoundsRemaining <= 1.35)
                return null;
        }
        else if (ctx.EstimatedRoundsRemaining <= 1.35)
        {
            return null;
        }

        double actorFutureRounds = Math.Clamp(ctx.EstimatedRoundsRemaining - 1.35, 0.0, 2.0);
        double otherBuffRounds = Math.Clamp(ctx.EstimatedRoundsRemaining - 0.65, 0.0, 2.5);
        double otherDebuffRounds = Math.Clamp(ctx.EstimatedRoundsRemaining - 0.65, 0.0, 2.0);

        int majority = Math.Max(1, (livingAllies.Count + 1) / 2);
        var candidates = new List<OffensiveSetupCandidate>(6);

        // 1. Party Attack Up: Matarukaja & Dragon Hustle (party Heat Riser)
        double partyAtkGain = ComputePartyAttackBuffGain(actor, ctx, buffDelta, otherBuffRounds, actorFutureRounds);
        if (partyAtkGain > 0.0)
        {
            int unbuffedCount = ctx.Projections.Count(p =>
                AiCombatHelpers.NeedsStatRefresh(p.BestSkillStatStage, p.BestSkillStatTurns, ctx.EstimatedRoundsRemaining, isDebuff: false));
            if (unbuffedCount >= majority || partyAtkGain > ctx.ActorOpportunityCost * oppCostMultiplier)
            {
                if (includeUtilityBonuses && actor.SpRatio >= 0.50)
                {
                    var dragonHustle = SupportAndRecoveryEvaluator.FindExactBuffSkill(_bridge, actor, SkillCatalog.BuffHeatRiser, aoeOnly: true);
                    if (dragonHustle != null)
                    {
                        double utilBonus = livingAllies.Count(a => a.DefStage <= 0) >= majority ? 120.0 : 40.0;
                        candidates.Add(new OffensiveSetupCandidate
                        {
                            Skill = dragonHustle,
                            Target = actor,
                            AddedTeamDamage = partyAtkGain,
                            Score = partyAtkGain - ctx.ActorOpportunityCost + utilBonus,
                            Reason = $"Dragon Hustle -> team offense+defense (+{partyAtkGain:F0} est team dmg over {ctx.EstimatedRoundsRemaining:F1}r vs {ctx.ActorOpportunityCost:F0} cost)"
                        });
                    }
                }

                var matarukaja = SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, SkillCatalog.BuffTarukaja, aoeOnly: true);
                if (matarukaja != null)
                {
                    candidates.Add(new OffensiveSetupCandidate
                    {
                        Skill = matarukaja,
                        Target = actor,
                        AddedTeamDamage = partyAtkGain,
                        Score = partyAtkGain - ctx.ActorOpportunityCost,
                        Reason = $"Matarukaja -> buff party attack (+{partyAtkGain:F0} est team dmg over {ctx.EstimatedRoundsRemaining:F1}r vs {ctx.ActorOpportunityCost:F0} cost)"
                    });
                }
            }
        }

        // 2. Enemy Defense Down (Single Target): Debilitate / Rakunda on primaryEnemy
        if (AiCombatHelpers.NeedsStatRefresh(primaryEnemy.DefStage, primaryEnemy.EffectiveDefTurns, ctx.EstimatedRoundsRemaining, isDebuff: true))
        {
            double rakundaGain = ComputePrimaryDebuffGain(actor, ctx, primaryEnemy, debuffDelta, otherDebuffRounds, actorFutureRounds);
            if (rakundaGain > 0.0)
            {
                if (includeUtilityBonuses && (primaryEnemy.AtkStage >= 0 || primaryEnemy.HitEvaStage >= 0) && primaryEnemy.MaxHp >= 500)
                {
                    var debilitate = SupportAndRecoveryEvaluator.FindExactBuffSkill(_bridge, actor, SkillCatalog.BuffDebilitate);
                    if (debilitate != null)
                    {
                        double utilBonus = primaryEnemy.MaxHp >= 1000 ? 140.0 : 80.0;
                        candidates.Add(new OffensiveSetupCandidate
                        {
                            Skill = debilitate,
                            Target = primaryEnemy,
                            AddedTeamDamage = rakundaGain,
                            Score = rakundaGain - ctx.ActorOpportunityCost + utilBonus,
                            Reason = $"Debilitate -> {primaryEnemy.DisplayName} (+{rakundaGain:F0} est team dmg vs {ctx.ActorOpportunityCost:F0} cost)"
                        });
                    }
                }

                var rakunda = SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakunda, aoeOnly: false);
                if (rakunda != null)
                {
                    candidates.Add(new OffensiveSetupCandidate
                    {
                        Skill = rakunda,
                        Target = primaryEnemy,
                        AddedTeamDamage = rakundaGain,
                        Score = rakundaGain - ctx.ActorOpportunityCost,
                        Reason = $"{SkillCatalog.GetSkillName(rakunda.SkillId)} -> lower {primaryEnemy.DisplayName} Def (+{rakundaGain:F0} est team dmg vs {ctx.ActorOpportunityCost:F0} cost)"
                    });
                }
            }
        }

        // 3. Enemy Defense Down (AoE): Marakunda when multiple tough enemies lack DefStage < 0 or are expiring
        var unDebuffedEnemies = livingEnemies
            .Where(e => AiCombatHelpers.NeedsStatRefresh(e.DefStage, e.EffectiveDefTurns, ctx.EstimatedRoundsRemaining, isDebuff: true))
            .ToList();
        if (unDebuffedEnemies.Count >= 2 && unDebuffedEnemies.Count(e => e.CurrentHp >= 400) >= 2)
        {
            var marakunda = SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, SkillCatalog.BuffRakunda, aoeOnly: true);
            if (marakunda != null)
            {
                double aoeDebuffGain = ComputeAoeDebuffGain(actor, ctx, unDebuffedEnemies.Count, debuffDelta, otherDebuffRounds, actorFutureRounds);
                if (aoeDebuffGain > 0.0)
                {
                    candidates.Add(new OffensiveSetupCandidate
                    {
                        Skill = marakunda,
                        Target = primaryEnemy,
                        AddedTeamDamage = aoeDebuffGain,
                        Score = aoeDebuffGain - ctx.ActorOpportunityCost + 25.0,
                        Reason = $"{SkillCatalog.GetSkillName(marakunda.SkillId)} -> lower {unDebuffedEnemies.Count} enemies' Def (+{aoeDebuffGain:F0} est team dmg)"
                    });
                }
            }
        }

        // 4. Single-Target Attack Up: Heat Riser / Tarukaja on highest-gain unbuffed party member (including self)
        // Skip single-target refresh while the target still has active buff turns remaining so we spread buffs or attack instead.
        var bestCarry = ctx.Projections
            .Where(p => p.NextAnyEnemyDmg > 0.01 &&
                        (p.BestSkillStatStage <= 0 || p.BestSkillStatTurns <= 0))
            .OrderByDescending(p => ComputeSingleTargetAttackBuffGain(ctx, actor, p, buffDelta, otherBuffRounds, actorFutureRounds))
            .FirstOrDefault();

        if (bestCarry != null)
        {
            double carryGain = ComputeSingleTargetAttackBuffGain(ctx, actor, bestCarry, buffDelta, otherBuffRounds, actorFutureRounds);
            if (carryGain > 0.0)
            {
                if (includeUtilityBonuses)
                {
                    var heatRiser = SupportAndRecoveryEvaluator.FindExactBuffSkill(_bridge, actor, SkillCatalog.BuffHeatRiser, aoeOnly: false);
                    if (heatRiser != null)
                    {
                        double utilBonus = bestCarry.Ally.UnitOrEnemyId == 1 ? 60.0 : 30.0;
                        candidates.Add(new OffensiveSetupCandidate
                        {
                            Skill = heatRiser,
                            Target = bestCarry.Ally,
                            AddedTeamDamage = carryGain,
                            Score = carryGain - ctx.ActorOpportunityCost + utilBonus,
                            Reason = $"Heat Riser -> {bestCarry.Ally.DisplayName} (+{carryGain:F0} est carry dmg vs {ctx.ActorOpportunityCost:F0} cost)"
                        });
                    }
                }

                var tarukaja = SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, SkillCatalog.BuffTarukaja, aoeOnly: false);
                if (tarukaja != null)
                {
                    candidates.Add(new OffensiveSetupCandidate
                    {
                        Skill = tarukaja,
                        Target = bestCarry.Ally,
                        AddedTeamDamage = carryGain,
                        Score = carryGain - ctx.ActorOpportunityCost,
                        Reason = $"Tarukaja -> {bestCarry.Ally.DisplayName} (+{carryGain:F0} est carry dmg vs {ctx.ActorOpportunityCost:F0} cost)"
                    });
                }
            }
        }

        // 5. Critical Rate Up: Rebellion (+7% single ally, risk-free) & Revolution (+15% field-wide)
        AddCritBuffCandidates(
            candidates, actor, ctx, livingAllies, livingEnemies, otherBuffRounds, actorFutureRounds);

        return candidates
            .Select(c =>
            {
                int spCost = c.Skill.CostType == 2 ? _bridge.GetEffectiveCost(actor, c.Skill.SkillId) : 0;
                double spPenalty = (!ignoreResourceCosts && !conserveSpMode && spCost > 0)
                    ? spCost * (actor.MaxSp > 0 && actor.SpRatio < 0.45 ? 2.5 : 1.5)
                    : 0.0;
                return (Candidate: c, SpCost: spCost, AdjustedScore: c.Score - spPenalty);
            })
            .Where(x =>
            {
                var c = x.Candidate;
                double effectiveMult = (!ignoreResourceCosts && !conserveSpMode && !c.IsAoE && (c.BuffMask & SkillCatalog.BuffTarukaja) != 0)
                    ? Math.Max(oppCostMultiplier, 1.45)
                    : oppCostMultiplier;
                if (c.AddedTeamDamage <= ctx.ActorOpportunityCost * effectiveMult)
                    return false;
                if (!ignoreResourceCosts && !conserveSpMode && x.AdjustedScore <= 0.0)
                    return false;
                if (!conserveSpMode)
                    return true;
                return x.SpCost <= 15 && c.AddedTeamDamage >= x.SpCost * 18.0;
            })
            .OrderByDescending(x => x.AdjustedScore)
            .ThenBy(x => x.SpCost)
            .Select(x => new OffensiveSetupCandidate
            {
                Skill = x.Candidate.Skill,
                Target = x.Candidate.Target,
                AddedTeamDamage = x.Candidate.AddedTeamDamage,
                Score = x.AdjustedScore,
                Reason = x.Candidate.Reason
            })
            .FirstOrDefault();
    }

    /// <summary>
    /// If an uncharged carry can immediately Power/Mind Charge this turn while a lower-DPS AI-controlled
    /// teammate can apply the same buff/debuff, let the carry Charge on Turn 1 so Turn 2 fires Charged + Buffed.
    /// Never defers to the human-controlled Protagonist since their actions are unpredictable.
    /// </summary>
    public bool ShouldCarryDeferSetupForCharge(
        BattleSnapshot battle,
        Config config,
        TeamOffenseContext ctx,
        OffensiveSetupCandidate setup,
        bool actorWantsToCharge)
    {
        if (!actorWantsToCharge)
            return false;

        var actor = battle.Actor;
        foreach (var proj in ctx.Projections)
        {
            var ally = proj.Ally;
            if (ReferenceEquals(ally, actor) || ally.UnitOrEnemyId == 1)
                continue;
            if (ally.IsDead || ally.IsDizzy || ally.IsDown || ally.HasLossOfControl)
                continue;

            bool aiCanBuff = ally.Tactic is TacticMode.HealSupport or TacticMode.ActFreely ||
                             (ally.Tactic == TacticMode.FullAssault && config.AllowOffensiveBuffsInFullAssault);
            if (!aiCanBuff)
                continue;

            if (proj.NextAnyEnemyDmg >= ctx.ActorOpportunityCost * 0.90)
                continue;

            uint reqMask = (setup.BuffMask & SkillCatalog.BuffRakunda) != 0
                ? SkillCatalog.BuffRakunda
                : (setup.BuffMask & SkillCatalog.BuffTarukaja) != 0
                    ? SkillCatalog.BuffTarukaja
                    : (setup.BuffMask & SkillCatalog.BuffRevolutionCritUp) != 0
                        ? SkillCatalog.BuffRevolutionCritUp
                        : SkillCatalog.BuffCritRateUp;

            if (SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, ally, reqMask, aoeOnly: setup.IsAoE) != null ||
                SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, ally, reqMask, aoeOnly: null) != null)
            {
                return true;
            }
        }

        return false;
    }

    public AllyOffenseProjection ProjectAllyPotentialIfCured(
        BattleSnapshot battle,
        CombatantSnapshot ally,
        List<CombatantSnapshot> livingEnemies,
        CombatantSnapshot primaryEnemy,
        Config config)
    {
        bool actsThisRound = battle.UpcomingRoundTurnOrder.Count == 0 || AiCombatHelpers.HasUpcomingTurnInRound(battle, ally);
        return ProjectAllyOffense(ally, livingEnemies, primaryEnemy, config, ignoreAilments: true, actsThisRound);
    }

    private AllyOffenseProjection ProjectAllyOffense(
        CombatantSnapshot ally,
        List<CombatantSnapshot> livingEnemies,
        CombatantSnapshot primaryEnemy,
        Config config,
        bool ignoreAilments,
        bool actsRemainingThisRound)
    {
        if (!ignoreAilments && (ally.IsDead || ally.IsDizzy || ally.IsDown || ally.HasLossOfControl))
            return new AllyOffenseProjection { Ally = ally, ActsRemainingThisRound = actsRemainingThisRound };
        if (ally.IsDead)
            return new AllyOffenseProjection { Ally = ally, ActsRemainingThisRound = actsRemainingThisRound };

        double bestAnyNext = 0.0;
        double bestAnySustained = 0.0;
        double bestPrimaryNext = 0.0;
        double bestPrimarySustained = 0.0;
        double bestAoeTotal = 0.0;
        int bestStatStage = ally.AtkStage;
        int bestStatTurns = ally.EffectiveAtkTurns;
        bool bestUsesCharge = false;
        bool hitsStandingWeakness = false;
        double bestPhysNext = 0.0;
        double bestPhysSustained = 0.0;
        double bestPhysExpectedHits = 1.0;
        byte bestPhysBaseCrit = 0;
        bool bestPhysIsAoE = false;
        bool bestPhysKnockEligible = false;
        bool bestPhysHasPowerCharge = false;

        var seen = new HashSet<ushort> { 0 };
        EvaluateAllySkill(0);
        foreach (ushort id in ally.EquippedSkills)
        {
            if (seen.Add(id))
                EvaluateAllySkill(id);
        }

        return new AllyOffenseProjection
        {
            Ally = ally,
            NextAnyEnemyDmg = bestAnyNext,
            SustainedAnyEnemyDmg = bestAnySustained,
            NextPrimaryEnemyDmg = bestPrimaryNext,
            SustainedPrimaryEnemyDmg = bestPrimarySustained,
            BestSkillStatStage = bestStatStage,
            BestSkillStatTurns = bestStatTurns,
            HasActiveChargeForBestSkill = bestUsesCharge,
            BestAoeUnitAvgDmg = bestAoeTotal / Math.Max(1, livingEnemies.Count),
            ActsRemainingThisRound = actsRemainingThisRound,
            HitsStandingWeakness = hitsStandingWeakness,
            BestPhysNextDmg = bestPhysNext,
            BestPhysSustainedDmg = bestPhysSustained,
            BestPhysExpectedHits = bestPhysExpectedHits,
            BestPhysBaseCrit = bestPhysBaseCrit,
            BestPhysIsAoE = bestPhysIsAoE,
            BestPhysHitsKnockdownEligibleTarget = bestPhysKnockEligible,
            BestPhysHasActivePowerCharge = bestPhysHasPowerCharge
        };

        bool CanAllyUseForProjection(SkillMetadata skill)
        {
            if (!ignoreAilments || !ally.HasSilenced)
                return _bridge.CanUseSkill(ally, skill.SkillId);
            if (skill.SkillId == 0)
                return !ally.IsDead;

            int effCost = _bridge.GetEffectiveCost(ally, skill.SkillId);
            return skill.CostType switch
            {
                2 => ally.CurrentSp >= effCost,
                1 => ally.CurrentHp > effCost,
                _ => true
            };
        }

        void EvaluateAllySkill(ushort skillId)
        {
            var skill = _bridge.GetSkill(skillId);
            if (!skill.IsDamaging || skill.IsInstantKill || !skill.TargetsEnemies || skill.IsSpDrain)
                return;
            if (!CanAllyUseForProjection(skill))
                return;

            ElementId elem = skillId == 0 ? _bridge.GetAttackElement(ally) : skill.Element;
            bool usesPower = skillId == 0 || elem == ElementId.Phys;
            bool isCritEligiblePhys = elem == ElementId.Phys && skill.BaseCrit > 0;
            bool isChargedNow = usesPower ? ally.HasPowerCharge : ally.HasMindCharge;
            int stage = skill.StatType == 1 ? ally.AtkStage : ally.MagStage;
            int stageTurns = skill.StatType == 1 ? ally.EffectiveAtkTurns : ally.EffectiveMagTurns;

            if (skill.IsAoE)
            {
                double sumNext = 0.0;
                double sumSustained = 0.0;
                double priNext = 0.0;
                double priSust = 0.0;
                bool aoeCritKnock = false;

                foreach (var enemy in livingEnemies)
                {
                    var aff = _bridge.QueryEffectiveAffinity(ally, enemy, elem, config.RespectAnalysisState);
                    if (AiCombatHelpers.IsUnsafeReflectOrDrain(enemy, elem, aff))
                        return;
                    if (!aff.CanDealDamage)
                        continue;
                    bool hitsWeak = _bridge.IsKnownWeakness(enemy, elem, aff);
                    if (enemy.IsStanding && hitsWeak)
                        hitsStandingWeakness = true;

                    double nextHit = _bridge.EstimateSingleHitDamage(skillId, ally, enemy, aff, false, false, config.RespectAnalysisState) * skill.ExpectedHits;
                    double sustHit = isChargedNow
                        ? _bridge.EstimateSingleHitDamage(skillId, ally, enemy, aff, false, true, config.RespectAnalysisState) * skill.ExpectedHits
                        : nextHit;

                    if (isCritEligiblePhys && nextHit > 0.0 && enemy.IsStanding && !hitsWeak &&
                        !AiCombatHelpers.IsLikelyBossOrMiniboss(enemy))
                    {
                        aoeCritKnock = true;
                    }

                    sumNext += Math.Min(nextHit, enemy.CurrentHp);
                    sumSustained += sustHit;
                    if (ReferenceEquals(enemy, primaryEnemy))
                    {
                        priNext = nextHit;
                        priSust = sustHit;
                    }
                }

                if (sumSustained > bestAoeTotal)
                    bestAoeTotal = sumSustained;
                if (sumNext > bestAnyNext)
                {
                    bestAnyNext = sumNext;
                    bestAnySustained = sumSustained;
                    bestStatStage = stage;
                    bestStatTurns = stageTurns;
                    bestUsesCharge = isChargedNow;
                }
                if (isCritEligiblePhys && (sumNext > bestPhysNext || (Math.Abs(sumNext - bestPhysNext) < 0.01 && aoeCritKnock && !bestPhysKnockEligible)))
                {
                    bestPhysNext = sumNext;
                    bestPhysSustained = sumSustained;
                    bestPhysExpectedHits = skill.ExpectedHits;
                    bestPhysBaseCrit = skill.BaseCrit;
                    bestPhysIsAoE = true;
                    bestPhysKnockEligible = aoeCritKnock;
                    bestPhysHasPowerCharge = isChargedNow;
                }
                if (priNext > bestPrimaryNext)
                {
                    bestPrimaryNext = priNext;
                    bestPrimarySustained = priSust;
                }
                return;
            }

            foreach (var enemy in livingEnemies)
            {
                var aff = _bridge.QueryEffectiveAffinity(ally, enemy, elem, config.RespectAnalysisState);
                if (aff.IsBadHit)
                    continue;
                bool hitsWeak = _bridge.IsKnownWeakness(enemy, elem, aff);
                if (enemy.IsStanding && hitsWeak)
                    hitsStandingWeakness = true;

                double next = _bridge.EstimateSingleHitDamage(skillId, ally, enemy, aff, false, false, config.RespectAnalysisState) * skill.ExpectedHits;
                double sust = isChargedNow
                    ? _bridge.EstimateSingleHitDamage(skillId, ally, enemy, aff, false, true, config.RespectAnalysisState) * skill.ExpectedHits
                    : next;

                if (next > bestAnyNext)
                {
                    bestAnyNext = next;
                    bestAnySustained = sust;
                    bestStatStage = stage;
                    bestStatTurns = stageTurns;
                    bestUsesCharge = isChargedNow;
                }
                if (isCritEligiblePhys && next > 0.0)
                {
                    bool critKnock = enemy.IsStanding && !hitsWeak && !AiCombatHelpers.IsLikelyBossOrMiniboss(enemy);
                    if (next > bestPhysNext || (Math.Abs(next - bestPhysNext) < 0.01 && critKnock && !bestPhysKnockEligible))
                    {
                        bestPhysNext = next;
                        bestPhysSustained = sust;
                        bestPhysExpectedHits = skill.ExpectedHits;
                        bestPhysBaseCrit = skill.BaseCrit;
                        bestPhysIsAoE = false;
                        bestPhysKnockEligible = critKnock;
                        bestPhysHasPowerCharge = isChargedNow;
                    }
                }
                if (ReferenceEquals(enemy, primaryEnemy) && next > bestPrimaryNext)
                {
                    bestPrimaryNext = next;
                    bestPrimarySustained = sust;
                }
            }
        }
    }

    private static double ResolveAllyBuffRounds(
        TeamOffenseContext ctx,
        CombatantSnapshot actor,
        AllyOffenseProjection proj,
        double defaultOtherRounds,
        double actorFutureRounds,
        double maxCap)
    {
        if (ReferenceEquals(proj.Ally, actor))
            return actorFutureRounds;
        if (!ctx.HasRoundQueueData)
            return defaultOtherRounds;

        double afterThisRound = Math.Max(0.0, ctx.EstimatedRoundsRemaining - 1.0);
        double allyRounds = (proj.ActsRemainingThisRound ? 1.0 : 0.0) + afterThisRound;
        return Math.Clamp(allyRounds, 0.0, maxCap);
    }

    private static double ComputePartyAttackBuffGain(
        CombatantSnapshot actor,
        TeamOffenseContext ctx,
        double buffDelta,
        double otherBuffRounds,
        double actorFutureRounds)
    {
        double gain = 0.0;
        foreach (var p in ctx.Projections)
        {
            if (!AiCombatHelpers.NeedsStatRefresh(p.BestSkillStatStage, p.BestSkillStatTurns, ctx.EstimatedRoundsRemaining, isDebuff: false))
                continue;

            gain += ComputeSingleTargetAttackBuffGain(ctx, actor, p, buffDelta, otherBuffRounds, actorFutureRounds);
        }

        return gain;
    }

    private static double ComputeSingleTargetAttackBuffGain(
        TeamOffenseContext ctx,
        CombatantSnapshot actor,
        AllyOffenseProjection carry,
        double buffDelta,
        double otherBuffRounds,
        double actorFutureRounds)
    {
        bool isRefresh = carry.BestSkillStatStage > 0;
        double activeTurnsAlreadyCovered = isRefresh ? Math.Max(1.0, carry.BestSkillStatTurns) : 0.0;

        if (ReferenceEquals(carry.Ally, actor))
        {
            double actorRounds = Math.Max(0.0, actorFutureRounds - activeTurnsAlreadyCovered);
            return buffDelta * carry.SustainedAnyEnemyDmg * actorRounds;
        }

        double effectiveRounds = ctx.HasRoundQueueData
            ? Math.Clamp((carry.ActsRemainingThisRound ? 1.0 : 0.0) + Math.Max(0.0, ctx.EstimatedRoundsRemaining - 1.0), 0.0, 2.5)
            : otherBuffRounds;

        effectiveRounds = Math.Max(0.0, effectiveRounds - activeTurnsAlreadyCovered);

        if (!isRefresh && carry.HasActiveChargeForBestSkill && effectiveRounds > 0.01)
        {
            double firstWeight = Math.Min(1.0, effectiveRounds);
            double extraSustainedRounds = Math.Max(0.0, effectiveRounds - 1.0);
            return buffDelta * (carry.NextAnyEnemyDmg * 0.90 * firstWeight + carry.SustainedAnyEnemyDmg * extraSustainedRounds);
        }

        return buffDelta * carry.SustainedAnyEnemyDmg * effectiveRounds;
    }

    private static double ComputePrimaryDebuffGain(
        CombatantSnapshot actor,
        TeamOffenseContext ctx,
        CombatantSnapshot primaryEnemy,
        double debuffDelta,
        double otherDebuffRounds,
        double actorFutureRounds)
    {
        bool isRefresh = primaryEnemy.DefStage < 0;
        double gain = 0.0;
        foreach (var p in ctx.Projections)
        {
            double rounds = ResolveAllyBuffRounds(ctx, actor, p, otherDebuffRounds, actorFutureRounds, 2.0);
            if (isRefresh)
                rounds = Math.Max(0.0, rounds - 0.5);

            if (!isRefresh && !ReferenceEquals(p.Ally, actor) && p.HasActiveChargeForBestSkill && rounds > 0.01)
            {
                double firstWeight = Math.Min(1.0, rounds);
                double extraSustained = Math.Max(0.0, rounds - 1.0);
                gain += debuffDelta * (p.NextPrimaryEnemyDmg * 0.90 * firstWeight + p.SustainedPrimaryEnemyDmg * extraSustained);
            }
            else
            {
                gain += debuffDelta * p.SustainedPrimaryEnemyDmg * rounds;
            }
        }

        return gain;
    }

    private static double ComputeAoeDebuffGain(
        CombatantSnapshot actor,
        TeamOffenseContext ctx,
        int unDebuffedCount,
        double debuffDelta,
        double otherDebuffRounds,
        double actorFutureRounds)
    {
        double gain = 0.0;
        foreach (var p in ctx.Projections)
        {
            double effectiveDmg = Math.Max(p.BestAoeUnitAvgDmg * unDebuffedCount, p.SustainedAnyEnemyDmg);
            double rounds = ResolveAllyBuffRounds(ctx, actor, p, otherDebuffRounds, actorFutureRounds, 2.0);
            gain += debuffDelta * effectiveDmg * rounds;
        }
        return gain;
    }

    private void AddCritBuffCandidates(
        List<OffensiveSetupCandidate> candidates,
        CombatantSnapshot actor,
        TeamOffenseContext ctx,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> livingEnemies,
        double otherBuffRounds,
        double actorFutureRounds)
    {
        // Rebellion (+7% crit, single ally, zero enemy downside)
        var rebellion = SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, SkillCatalog.BuffCritRateUp, aoeOnly: false);
        if (rebellion != null)
        {
            AllyOffenseProjection? bestTarget = null;
            double bestRebGain = 0.0;
            foreach (var p in ctx.Projections)
            {
                if (!AiCombatHelpers.NeedsStatRefresh(p.Ally.CritRateStage, p.Ally.EffectiveCritRateTurns, ctx.EstimatedRoundsRemaining, isDebuff: false))
                    continue;
                bool isRefresh = p.Ally.CritRateStage > 0;
                double g = ComputeAllyCritGain(ctx, actor, p, deltaCrit: 0.07, otherBuffRounds, actorFutureRounds, isRefresh);
                if (g > bestRebGain)
                {
                    bestRebGain = g;
                    bestTarget = p;
                }
            }

            if (bestTarget != null && bestRebGain > 0.0)
            {
                candidates.Add(new OffensiveSetupCandidate
                {
                    Skill = rebellion,
                    Target = bestTarget.Ally,
                    AddedTeamDamage = bestRebGain,
                    Score = bestRebGain - ctx.ActorOpportunityCost,
                    Reason = $"Rebellion -> {bestTarget.Ally.DisplayName} (+{bestRebGain:F0} crit EV vs {ctx.ActorOpportunityCost:F0} cost)"
                });
            }
        }

        // Revolution (+15% crit field-wide to both sides: weigh party crit EV against enemy crit risk)
        if (!livingAllies.Any(a => AiCombatHelpers.NeedsStatRefresh(a.RevolutionCritStage, a.EffectiveRevolutionCritTurns, ctx.EstimatedRoundsRemaining, isDebuff: false)))
            return;

        var revolution = SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, SkillCatalog.BuffRevolutionCritUp, aoeOnly: true);
        if (revolution == null)
            return;

        double risk = ComputeRevolutionEnemyRiskPenalty(ctx, livingAllies, livingEnemies);
        if (double.IsPositiveInfinity(risk))
            return;

        double rawRevGain = 0.0;
        int physCarryCount = 0;
        foreach (var p in ctx.Projections)
        {
            if (!AiCombatHelpers.NeedsStatRefresh(p.Ally.RevolutionCritStage, p.Ally.EffectiveRevolutionCritTurns, ctx.EstimatedRoundsRemaining, isDebuff: false))
                continue;
            bool isRefresh = p.Ally.RevolutionCritStage > 0;
            double g = ComputeAllyCritGain(ctx, actor, p, deltaCrit: 0.15, otherBuffRounds, actorFutureRounds, isRefresh);
            if (g > 0.0)
            {
                rawRevGain += g;
                physCarryCount++;
            }
        }

        if (physCarryCount == 0)
            return;

        double netRevGain = rawRevGain - risk;
        bool partyPhysSafe = risk <= 0.01;
        if (netRevGain <= 0.0 || (!partyPhysSafe && physCarryCount < 2 && netRevGain <= ctx.ActorOpportunityCost * 1.5))
            return;

        candidates.Add(new OffensiveSetupCandidate
        {
            Skill = revolution,
            Target = actor,
            AddedTeamDamage = netRevGain,
            Score = netRevGain - ctx.ActorOpportunityCost,
            Reason = $"Revolution -> field crit up (+{rawRevGain:F0} party EV - {risk:F0} risk vs {ctx.ActorOpportunityCost:F0} cost)"
        });
    }

    private static double ComputeAllyCritGain(
        TeamOffenseContext ctx,
        CombatantSnapshot actor,
        AllyOffenseProjection proj,
        double deltaCrit,
        double defaultOtherRounds,
        double actorFutureRounds,
        bool isRefresh = false)
    {
        if (proj.BestPhysBaseCrit == 0 || proj.BestPhysNextDmg <= 0.01)
            return 0.0;

        if (proj.BestPhysNextDmg < proj.NextAnyEnemyDmg * 0.55)
            return 0.0;

        double effectiveRounds = ResolveAllyBuffRounds(ctx, actor, proj, defaultOtherRounds, actorFutureRounds, 2.5);
        if (isRefresh)
            effectiveRounds = Math.Max(0.0, effectiveRounds - 0.5);
        if (effectiveRounds <= 0.01)
            return 0.0;

        double directDmgBudget;
        if (!isRefresh && !ReferenceEquals(proj.Ally, actor) && proj.BestPhysHasActivePowerCharge)
        {
            double firstWeight = Math.Min(1.0, effectiveRounds);
            double extraSustained = Math.Max(0.0, effectiveRounds - 1.0);
            directDmgBudget = proj.BestPhysNextDmg * 0.90 * firstWeight + proj.BestPhysSustainedDmg * extraSustained;
        }
        else
        {
            directDmgBudget = proj.BestPhysSustainedDmg * effectiveRounds;
        }

        double directCritEv = 0.50 * deltaCrit * directDmgBudget;

        double oneMoreEv = 0.0;
        if (proj.BestPhysHitsKnockdownEligibleTarget)
        {
            bool includeRebInBase = !(isRefresh && Math.Abs(deltaCrit - 0.07) < 0.01);
            bool includeRevInBase = !(isRefresh && Math.Abs(deltaCrit - 0.15) < 0.01);
            double p0 = Math.Clamp(
                (proj.BestPhysBaseCrit / 100.0)
                + (includeRebInBase && proj.Ally.CritRateStage > 0 ? 0.07 : 0.0)
                + (includeRevInBase && proj.Ally.RevolutionCritStage > 0 ? 0.15 : 0.0),
                0.01,
                0.95);
            double p1 = Math.Clamp(p0 + deltaCrit, 0.01, 1.0);
            double hits = Math.Clamp(proj.BestPhysExpectedHits, 1.0, 8.0);
            double prob0 = 1.0 - Math.Pow(1.0 - p0, hits);
            double prob1 = 1.0 - Math.Pow(1.0 - p1, hits);
            double deltaProb = Math.Max(0.0, prob1 - prob0);

            double followupAndAoaValue = proj.SustainedAnyEnemyDmg + (proj.BestPhysIsAoE ? 120.0 : 45.0);
            double opportunities = Math.Min(effectiveRounds, 2.0);
            oneMoreEv = deltaProb * followupAndAoaValue * opportunities;
        }

        return directCritEv + oneMoreEv;
    }

    private static double ComputeRevolutionEnemyRiskPenalty(
        TeamOffenseContext ctx,
        List<CombatantSnapshot> livingAllies,
        List<CombatantSnapshot> livingEnemies)
    {
        var exposedAllies = livingAllies
            .Where(a => !a.HasShieldOfJustice && !a.HasTetrakarn)
            .ToList();
        if (exposedAllies.Count == 0)
            return 0.0;

        double lowestExposedHp = exposedAllies.Min(a => a.HpRatio);
        double hpRiskMult = lowestExposedHp < 0.70 ? 1.75 : 1.0;
        double rounds = Math.Clamp(ctx.EstimatedRoundsRemaining - 0.5, 0.5, 2.0);

        double risk = 0.0;
        foreach (var enemy in livingEnemies)
        {
            if (enemy.IsDown || enemy.IsDizzy || enemy.HasLossOfControl)
                continue;

            if (enemy.HasPowerCharge)
                return double.PositiveInfinity;

            double perEnemyThreat = enemy.MaxHp >= 500 ? 70.0 : 28.0;
            risk += perEnemyThreat * rounds * 0.50 * hpRiskMult;
        }

        return risk;
    }
}
