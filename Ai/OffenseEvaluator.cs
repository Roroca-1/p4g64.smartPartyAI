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
        OffenseScoringProfile? profile = null,
        List<CombatantSnapshot>? livingEnemies = null,
        IReadOnlyList<BattleConsumableInfo>? allowedDamageItems = null)
    {
        profile ??= new OffenseScoringProfile();
        var actor = battle.Actor;
        livingEnemies ??= AiCombatHelpers.CollectLiving(battle.Enemies);
        if (livingEnemies.Count == 0)
            return null;

        EvaluatedOffense? best = null;
        int activeAlliesForAoa = AiCombatHelpers.CountAlliesEligibleForAoa(battle);
        IReadOnlyList<BattleConsumableInfo>? followupItems = config.EnableDamageItemsInAi
            ? (allowedDamageItems ?? GatherAllowedDamageItems(battle.Actor, config))
            : null;
        Dictionary<CombatantSnapshot, bool>? revealedWeakCache =
            config.RespectAnalysisState && config.ProbeUnrevealedElements
                ? new Dictionary<CombatantSnapshot, bool>(livingEnemies.Count)
                : null;

        foreach (var skill in candidates)
        {
            int spCost = skill.CostType == 2 ? _bridge.GetEffectiveCost(actor, skill.SkillId) : 0;
            if (spCost > profile.MaxSpCost)
                continue;
            int hpCost = skill.CostType == 1 ? _bridge.GetEffectiveCost(actor, skill.SkillId) : 0;

            if (skill.IsAoE)
            {
                var eval = EvaluateAoECandidate(
                    battle, config, skill, livingEnemies, candidates, followupItems, spCost, hpCost,
                    activeAlliesForAoa, profile, item: null, allowNeutralChipItem: true, revealedWeakCache);
                if (eval != null && (best == null || eval.Score > best.Score))
                    best = eval;
            }
            else
            {
                foreach (var enemy in livingEnemies)
                {
                    var eval = EvaluateSingleTargetCandidate(
                        battle, config, skill, enemy, livingEnemies, candidates, followupItems, spCost, hpCost,
                        activeAlliesForAoa, profile, item: null, allowNeutralChipItem: true, revealedWeakCache);
                    if (eval != null && (best == null || eval.Score > best.Score))
                        best = eval;
                }
            }
        }

        return best;
    }

    public EvaluatedOffense? EvaluateBestDamageItem(
        BattleSnapshot battle,
        Config config,
        List<SkillMetadata> skillCandidates,
        bool allowNeutralChipItem,
        OffenseScoringProfile? profile = null,
        List<CombatantSnapshot>? livingEnemies = null,
        IReadOnlyList<BattleConsumableInfo>? allowedDamageItems = null)
    {
        if (!config.EnableDamageItemsInAi)
            return null;

        profile ??= new OffenseScoringProfile();
        livingEnemies ??= AiCombatHelpers.CollectLiving(battle.Enemies);
        if (livingEnemies.Count == 0)
            return null;

        var damageItems = allowedDamageItems ?? GatherAllowedDamageItems(battle.Actor, config);
        if (damageItems.Count == 0)
            return null;

        EvaluatedOffense? best = null;
        int activeAlliesForAoa = AiCombatHelpers.CountAlliesEligibleForAoa(battle);

        foreach (var item in damageItems)
        {
            if (item.Skill.IsAoE)
            {
                var eval = EvaluateAoECandidate(
                    battle, config, item.Skill, livingEnemies, skillCandidates, damageItems, 0, 0,
                    activeAlliesForAoa, profile, item: item, allowNeutralChipItem: allowNeutralChipItem, revealedWeakCache: null);
                if (eval != null && (best == null || eval.Score > best.Score))
                    best = eval;
            }
            else
            {
                foreach (var enemy in livingEnemies)
                {
                    var eval = EvaluateSingleTargetCandidate(
                        battle, config, item.Skill, enemy, livingEnemies, skillCandidates, damageItems, 0, 0,
                        activeAlliesForAoa, profile, item: item, allowNeutralChipItem: allowNeutralChipItem, revealedWeakCache: null);
                    if (eval != null && (best == null || eval.Score > best.Score))
                        best = eval;
                }
            }
        }

        return best;
    }

    internal List<BattleConsumableInfo> GatherAllowedDamageItems(CombatantSnapshot actor, Config config) =>
        _bridge.GetAvailableConsumables(actor)
            .Where(i => i.Kind == BattleConsumableKind.Damage &&
                        i.Skill.IsDamaging &&
                        i.Skill.TargetsEnemies &&
                        AiCombatHelpers.IsConsumableAllowed(i, config))
            .OrderBy(i => i.Rarity)
            .ThenByDescending(i => i.Quantity)
            .ToList();

    public AiDecision? EvaluateConserveSpProbe(
        BattleSnapshot battle,
        Config config,
        List<SkillMetadata> allCandidates,
        int maxSpCost,
        double freeDirectDamage,
        List<CombatantSnapshot>? livingEnemies = null)
    {
        var actor = battle.Actor;
        if (!config.RespectAnalysisState || !config.ConserveSpProbeUnrevealed || !config.ProbeUnrevealedElements)
            return null;
        if (battle.IsActorOnOneMore || actor.SpRatio < 0.35 || actor.HasPowerCharge || actor.HasMindCharge)
            return null;

        livingEnemies ??= AiCombatHelpers.CollectLiving(battle.Enemies);
        int activeAlliesForAoa = AiCombatHelpers.CountAlliesEligibleForAoa(battle);
        var revealedWeakCache = new Dictionary<CombatantSnapshot, bool>(livingEnemies.Count);
        SkillMetadata? bestSkill = null;
        CombatantSnapshot? bestTarget = null;
        double bestScore = double.NegativeInfinity;

        foreach (var skill in allCandidates)
        {
            if (skill.CostType != 2 || skill.IsAoE || skill.IsInstantKill)
                continue;
            int spCost = _bridge.GetEffectiveCost(actor, skill.SkillId);
            if (spCost <= 0 || spCost > maxSpCost)
                continue;

            ElementId elem = ResolveAttackElement(actor, skill);
            if ((byte)elem > (byte)ElementId.Wind || elem == ElementId.Phys)
                continue;

            foreach (var enemy in livingEnemies)
            {
                if (!enemy.IsStanding || ShouldSkipElementProbe(enemy))
                    continue;
                if (enemy.CurrentHp <= Math.Max(freeDirectDamage * 1.5, 90.0))
                    continue;

                var aff = _bridge.QueryEffectiveAffinity(actor, enemy, elem, respectAnalysis: true);
                if (aff.IsRevealed || aff.IsBadHit)
                    continue;

                double dmg = EvaluateTargetExpectedDamage(skill, actor, enemy, aff, respectAnalysis: true);
                if (dmg <= 0.0)
                    continue;

                double probe = ComputeUnrevealedProbeBonus(
                    battle, config, skill, enemy, livingEnemies, elem, aff, dmg, killsTarget: false, activeAlliesForAoa, spCost, revealedWeakCache);
                if (probe <= 0.0)
                    continue;

                double score = dmg + probe - 1.5 * spCost;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestSkill = skill;
                    bestTarget = enemy;
                }
            }
        }

        if (bestSkill == null || bestTarget == null)
            return null;

        return new AiDecision
        {
            ActionKind = AiActionKind.Skill,
            SkillId = bestSkill.SkillId,
            Target = bestTarget,
            Score = bestScore,
            Reason = $"{SkillCatalog.GetSkillName(bestSkill.SkillId)} -> probe unrevealed {ResolveAttackElement(actor, bestSkill)} on {bestTarget.DisplayName}"
        };
    }

    public AiDecision EvaluateOffenseWithFallbacks(
        BattleSnapshot battle,
        Config config,
        List<SkillMetadata> candidates,
        EvaluatedOffense? bestAttack,
        bool ignoreOverkillCap = false,
        bool hasPrecomputedCharge = false,
        EvaluatedOffense? precomputedCharge = null,
        List<CombatantSnapshot>? livingEnemies = null)
    {
        if (!battle.IsActorOnOneMore && bestAttack?.FinishesBattle != true && bestAttack?.TriggersAllOutAttack != true)
        {
            var chargeDecision = hasPrecomputedCharge
                ? precomputedCharge
                : EvaluateChargeAction(battle, config, candidates, bestAttack?.Score ?? 0.0, ignoreOverkillCap, livingEnemies);
            if (chargeDecision != null && (bestAttack == null || chargeDecision.Score > bestAttack.Score))
                return chargeDecision.Decision;
        }

        if (bestAttack != null && bestAttack.DirectDamage > 0.01)
            return bestAttack.Decision;

        var breakDecision = EvaluateBreakFallback(battle, config, candidates, livingEnemies);
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

    private EvaluatedOffense? EvaluateSingleTargetCandidate(
        BattleSnapshot battle,
        Config config,
        SkillMetadata skill,
        CombatantSnapshot enemy,
        List<CombatantSnapshot> livingEnemies,
        List<SkillMetadata> allCandidates,
        IReadOnlyList<BattleConsumableInfo>? followupItems,
        int spCost,
        int hpCost,
        int activeAlliesForAoa,
        OffenseScoringProfile profile,
        BattleConsumableInfo? item,
        bool allowNeutralChipItem,
        Dictionary<CombatantSnapshot, bool>? revealedWeakCache)
    {
        var actor = battle.Actor;
        ElementId effectiveElem = ResolveAttackElement(actor, skill);
        var aff = _bridge.QueryEffectiveAffinity(actor, enemy, effectiveElem, config.RespectAnalysisState);

        if (aff.IsBadHit)
            return null;

        if (item != null && !config.AllowDamageItemProbing && !aff.IsRevealed && enemy.IsEnemy && (byte)effectiveElem <= 7 && effectiveElem != ElementId.Almighty)
            return null;

        double directDmg = EvaluateTargetExpectedDamage(skill, actor, enemy, aff, config.RespectAnalysisState);
        if (directDmg <= 0.0)
            return null;

        double cappedDmg = Math.Min(directDmg, enemy.CurrentHp);
        bool killsTarget = !skill.IsInstantKill && !skill.IsPercentHpDamage && directDmg >= enemy.CurrentHp;
        bool finishesBattle = killsTarget && livingEnemies.Count == 1;

        // Verified in P4G.exe (0x14009c869): AilmentMask & 0x80000 skills bypass knockdown & 1 More.
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

        if (item != null && !allowNeutralChipItem && !killsTarget && !grantsOneMore && !triggersAoa)
            return null;

        double followupDmg = 0.0;
        string followupNote = string.Empty;
        if (grantsOneMore)
        {
            (followupDmg, followupNote) = SimulateBestOneMoreFollowup(
                battle, config, skill, enemy, directDmg, spCost, hpCost, livingEnemies, allCandidates, followupItems, item, profile);
        }

        double accWeight = GetAccuracyWeight(skill);
        double twoStepTotal = directDmg + followupDmg;
        double probeBonus = item == null
            ? ComputeUnrevealedProbeBonus(battle, config, skill, enemy, livingEnemies, effectiveElem, aff, directDmg, killsTarget, activeAlliesForAoa, spCost, revealedWeakCache)
            : 0.0;

        double score = ComputeUnifiedOffenseScore(
            profile, skill,
            effectiveDmg: profile.IgnoreOverkillCap ? directDmg : cappedDmg,
            rawDmg: directDmg,
            accWeight: accWeight,
            probeBonus: probeBonus,
            killCount: killsTarget ? 1 : 0,
            finishesBattle: finishesBattle,
            grantsOneMore: grantsOneMore,
            followupDmg: followupDmg,
            triggersAoa: triggersAoa,
            enemiesDamaged: 1,
            spCost: spCost,
            hpCost: hpCost,
            item: item);

        // Mild tie-breaker against Phys when target carries Counter (BaseFlags & 0x100); never blocks Phys.
        if (enemy.HasEnemyCounter && effectiveElem == ElementId.Phys && !killsTarget && score > 0.0)
            score *= 0.90;

        string actionName = item != null ? item.Name : SkillCatalog.GetSkillName(skill.SkillId);
        string probeTag = probeBonus > 0.0 ? $" [probe {effectiveElem}]" : string.Empty;
        string reason = grantsOneMore
            ? $"{actionName} -> {enemy.DisplayName} ({directDmg:F0} dmg + 1-More [{followupNote}: +{followupDmg:F0}]{(triggersAoa ? " + AOA" : "")})"
            : $"{actionName} -> {enemy.DisplayName} ({directDmg:F0}{(skill.IsInstantKill ? " IK-EV" : " dmg")}{(killsTarget ? ", LETHAL" : "")}){probeTag}";

        return new EvaluatedOffense
        {
            Decision = new AiDecision
            {
                ActionKind = item != null ? AiActionKind.Item : (skill.SkillId == 0 ? AiActionKind.NormalAttack : AiActionKind.Skill),
                SkillId = skill.SkillId,
                ItemId = item?.ItemId ?? 0,
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

    private EvaluatedOffense? EvaluateAoECandidate(
        BattleSnapshot battle,
        Config config,
        SkillMetadata skill,
        List<CombatantSnapshot> livingEnemies,
        List<SkillMetadata> allCandidates,
        IReadOnlyList<BattleConsumableInfo>? followupItems,
        int spCost,
        int hpCost,
        int activeAlliesForAoa,
        OffenseScoringProfile profile,
        BattleConsumableInfo? item,
        bool allowNeutralChipItem,
        Dictionary<CombatantSnapshot, bool>? revealedWeakCache)
    {
        var actor = battle.Actor;
        int count = livingEnemies.Count;
        double totalRawDmg = 0.0;
        double totalCappedDmg = 0.0;
        int killCount = 0;
        bool knocksDownAny = false;
        bool anyRevealedTarget = false;
        int enemiesDamaged = 0;
        double[]? simHp = null;
        bool[]? simDown = null;
        bool anySurvivor = false;
        bool allDownOrDead = true;
        bool canAoeProbe = item == null && (byte)skill.Element is >= (byte)ElementId.Fire and <= (byte)ElementId.Wind;
        double maxProbe = 0.0;
        double sumProbe = 0.0;
        int probeCount = 0;

        bool canKnockDown = !skill.IsInstantKill && (skill.AilmentMask & 0x80000) == 0;

        for (int i = 0; i < count; i++)
        {
            var enemy = livingEnemies[i];
            var aff = _bridge.QueryEffectiveAffinity(actor, enemy, skill.Element, config.RespectAnalysisState);
            if (AiCombatHelpers.IsUnsafeReflectOrDrain(enemy, skill.Element, aff))
                return null;

            if (aff.IsRevealed || (byte)skill.Element > 7 || skill.Element == ElementId.Almighty)
                anyRevealedTarget = true;

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

            if (canAoeProbe && dmg > 0.0 && !killed && !hitsWeak)
            {
                double targetProbe = ComputeTargetUnrevealedProbeBonus(
                    battle, config, skill, enemy, livingEnemies, skill.Element, aff, dmg, killsTarget: false, activeAlliesForAoa, spCost, revealedWeakCache);
                if (targetProbe > 0.0)
                {
                    if (targetProbe > maxProbe)
                        maxProbe = targetProbe;
                    sumProbe += targetProbe;
                    probeCount++;
                }
            }

            double remHp = skill.IsInstantKill ? enemy.CurrentHp : Math.Max(0.0, enemy.CurrentHp - dmg);
            bool postDown = !enemy.IsStanding || down;
            if (remHp > 0.0)
            {
                anySurvivor = true;
                if (!postDown)
                    allDownOrDead = false;
            }

            if (knocksDownAny && simHp == null)
            {
                simHp = new double[count];
                simDown = new bool[count];
                for (int prev = 0; prev < i; prev++)
                {
                    // Re-fill earlier targets lazily only when an AoE actually knocks someone down
                    var pe = livingEnemies[prev];
                    var pAff = _bridge.QueryEffectiveAffinity(actor, pe, skill.Element, config.RespectAnalysisState);
                    double pDmg = (!pAff.IsNull && !pAff.BlockedByShield)
                        ? EvaluateTargetExpectedDamage(skill, actor, pe, pAff, config.RespectAnalysisState)
                        : 0.0;
                    simHp[prev] = skill.IsInstantKill ? pe.CurrentHp : Math.Max(0.0, pe.CurrentHp - pDmg);
                    simDown[prev] = !pe.IsStanding || (canKnockDown && _bridge.IsKnownWeakness(pe, skill.Element, pAff) && pe.IsStanding);
                }
            }

            if (simHp != null)
            {
                simHp[i] = remHp;
                simDown![i] = postDown;
            }
        }

        if (enemiesDamaged == 0 || totalRawDmg <= 0.0)
            return null;

        if (item != null && !config.AllowDamageItemProbing && !anyRevealedTarget)
            return null;

        double aoeProbeBonus = (canAoeProbe && enemiesDamaged >= 2 && probeCount > 0)
            ? Math.Min(300.0, maxProbe + 0.50 * (sumProbe - maxProbe))
            : 0.0;

        bool finishesBattle = killCount == count;
        bool grantsOneMore = knocksDownAny && anySurvivor;
        bool triggersAoa = grantsOneMore && activeAlliesForAoa >= 2 && allDownOrDead;

        if (item != null && !allowNeutralChipItem && killCount == 0 && !grantsOneMore && !triggersAoa)
            return null;

        double followupDmg = 0.0;
        string followupNote = string.Empty;
        if (grantsOneMore && simHp != null && simDown != null)
        {
            (followupDmg, followupNote) = SimulateBestOneMoreFollowupAoE(
                battle, config, skill, livingEnemies, simHp, simDown, spCost, hpCost, allCandidates, followupItems, item, profile);
        }

        double accWeight = GetAccuracyWeight(skill);
        double twoStepTotal = totalRawDmg + followupDmg;
        double score = ComputeUnifiedOffenseScore(
            profile, skill,
            effectiveDmg: profile.IgnoreOverkillCap ? totalRawDmg : totalCappedDmg,
            rawDmg: totalRawDmg,
            accWeight: accWeight,
            probeBonus: aoeProbeBonus,
            killCount: killCount,
            finishesBattle: finishesBattle,
            grantsOneMore: grantsOneMore,
            followupDmg: followupDmg,
            triggersAoa: triggersAoa,
            enemiesDamaged: enemiesDamaged,
            spCost: spCost,
            hpCost: hpCost,
            item: item);

        if (skill.Element == ElementId.Phys && !finishesBattle && score > 0.0 &&
            livingEnemies.Any(e => e.HasEnemyCounter))
        {
            score *= 0.92;
        }

        string actionName = item != null ? item.Name : SkillCatalog.GetSkillName(skill.SkillId);
        string probeTag = aoeProbeBonus > 0.0 ? $" [probe {skill.Element}]" : string.Empty;
        string reason = grantsOneMore
            ? $"{actionName} (AoE {enemiesDamaged}/{livingEnemies.Count}) ({totalRawDmg:F0} dmg + 1-More [{followupNote}: +{followupDmg:F0}]{(triggersAoa ? " + AOA" : "")})"
            : $"{actionName} (AoE {enemiesDamaged}/{livingEnemies.Count}) ({totalRawDmg:F0}{(skill.IsInstantKill ? " IK-EV" : " dmg")}{(killCount > 0 ? $", {killCount} KO" : "")}){probeTag}";

        return new EvaluatedOffense
        {
            Decision = new AiDecision
            {
                ActionKind = item != null ? AiActionKind.Item : AiActionKind.Skill,
                SkillId = skill.SkillId,
                ItemId = item?.ItemId ?? 0,
                Target = livingEnemies[0],
                Targets = item != null ? livingEnemies : null,
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

    private static double GetDamageItemPenalty(
        BattleConsumableInfo item,
        bool finishesBattle,
        bool triggersAoa,
        bool killsAny,
        bool grantsOneMore)
    {
        if (finishesBattle)
            return 5.0;

        return item.Rarity switch
        {
            ConsumableRarityTier.Rare => (triggersAoa || killsAny) ? 80.0 : 220.0,
            ConsumableRarityTier.Mid => (triggersAoa || killsAny) ? 35.0 : 110.0,
            _ => (triggersAoa || killsAny) ? 12.0 : (grantsOneMore ? 28.0 : 55.0)
        };
    }

    private static double ComputeUnifiedOffenseScore(
        OffenseScoringProfile profile,
        SkillMetadata skill,
        double effectiveDmg,
        double rawDmg,
        double accWeight,
        double probeBonus,
        int killCount,
        bool finishesBattle,
        bool grantsOneMore,
        double followupDmg,
        bool triggersAoa,
        int enemiesDamaged,
        int spCost,
        int hpCost,
        BattleConsumableInfo? item)
    {
        double score = effectiveDmg * accWeight + (profile.IgnoreOverkillCap ? 0.0 : 0.02 * rawDmg) + probeBonus;
        if (!profile.IgnoreOverkillCap && killCount > 0)
            score += killCount * LethalKillBonus;
        if (grantsOneMore)
            score += followupDmg;
        if (triggersAoa)
            score += AllOutAttackBonus;

        if (item == null && skill.IsAoE && enemiesDamaged <= 1 && !skill.IsInstantKill)
            score -= profile.IgnoreResourceCosts ? 0.5 : Math.Max(0.5, 2.0 * spCost);

        if (item != null)
        {
            score -= profile.IgnoreResourceCosts
                ? 1.0 // Tiny tiebreak so equal-damage reusable skills beat burning a consumable item
                : GetDamageItemPenalty(item, finishesBattle, triggersAoa, killCount > 0, grantsOneMore);
        }
        else if (!profile.IgnoreResourceCosts && profile.PreferLowCostTiebreak)
        {
            double spWeight = killCount > 0 ? Math.Max(profile.SpCostWeight, 1.5) : profile.SpCostWeight;
            score -= spWeight * spCost + profile.HpCostWeight * hpCost;
        }

        return score;
    }

    private (double followupDmg, string note) SimulateBestOneMoreFollowup(
        BattleSnapshot battle,
        Config config,
        SkillMetadata step1Skill,
        CombatantSnapshot targetEnemy,
        double singleTargetStep1Dmg,
        int step1SpCost,
        int step1HpCost,
        List<CombatantSnapshot> livingEnemies,
        List<SkillMetadata> candidates,
        IReadOnlyList<BattleConsumableInfo>? followupItems,
        BattleConsumableInfo? step1Item,
        OffenseScoringProfile profile)
    {
        int count = livingEnemies.Count;
        var simHp = new double[count];
        var simDown = new bool[count];

        for (int i = 0; i < count; i++)
        {
            var e = livingEnemies[i];
            if (ReferenceEquals(e, targetEnemy))
            {
                simHp[i] = Math.Max(0.0, e.CurrentHp - singleTargetStep1Dmg);
                simDown[i] = true;
            }
            else
            {
                simHp[i] = e.CurrentHp;
                simDown[i] = !e.IsStanding;
            }
        }

        return SimulateBestOneMoreFollowupAoE(
            battle, config, step1Skill, livingEnemies, simHp, simDown, step1SpCost, step1HpCost, candidates, followupItems, step1Item, profile);
    }

    private (double followupDmg, string note) SimulateBestOneMoreFollowupAoE(
        BattleSnapshot battle,
        Config config,
        SkillMetadata step1Skill,
        List<CombatantSnapshot> livingEnemies,
        double[] simHpByIdx,
        bool[] simDownByIdx,
        int step1SpCost,
        int step1HpCost,
        List<SkillMetadata> candidates,
        IReadOnlyList<BattleConsumableInfo>? followupItems,
        BattleConsumableInfo? step1Item,
        OffenseScoringProfile profile)
    {
        var actor = battle.Actor;
        bool ignoreOverkillCap = profile.IgnoreOverkillCap;
        int simSp = actor.CurrentSp - step1SpCost;
        int simHp = actor.CurrentHp - step1HpCost;

        ElementId step1Elem = ResolveAttackElement(actor, step1Skill);
        bool step1ConsumedPower = step1Item == null && (step1Skill.SkillId == 0 || step1Elem == ElementId.Phys);
        bool step1ConsumedMind = step1Item == null && !step1ConsumedPower;
        bool simPowerCharged = actor.HasPowerCharge && !step1ConsumedPower;
        bool simMindCharged = actor.HasMindCharge && !step1ConsumedMind;

        int survivingCount = 0;
        for (int i = 0; i < simHpByIdx.Length; i++)
        {
            if (simHpByIdx[i] > 0.0)
                survivingCount++;
        }
        if (survivingCount == 0)
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
            bool usesPower2 = !skill2.IsFixedPowerDamage && (skill2.SkillId == 0 || elem2 == ElementId.Phys);
            bool usesMind2 = !skill2.IsFixedPowerDamage && skill2.SkillId != 0 && elem2 != ElementId.Phys;
            bool assumeCharged = (usesPower2 && simPowerCharged) || (usesMind2 && simMindCharged);
            bool assumeUncharged = !assumeCharged && !skill2.IsFixedPowerDamage &&
                                   ((usesPower2 && actor.HasPowerCharge) || (usesMind2 && actor.HasMindCharge));
            EvaluateFollowupOption(
                skill2, elem2, assumeCharged, assumeUncharged,
                itemPenalty: 0.0, label: SkillCatalog.GetSkillName(skill2.SkillId),
                requireRevealed: false, allowNeutralItem: true);
        }

        if (followupItems != null)
        {
            // Match actual 1-More turn item gating so lookahead never plans neutral chip items ActFreely/HealSupport reject
            bool allowNeutralItemFollowup = profile.AllowDamageItemOutrank || actor.HasSilenced || bestDmg <= 0.01;
            int reserve = Math.Clamp(config.MinConsumableReserveCount, 0, 99);
            foreach (var item2 in followupItems)
            {
                int minNeeded = reserve + (step1Item?.ItemId == item2.ItemId ? 1 : 0);
                if (item2.Quantity <= minNeeded)
                    continue;

                double pen = item2.Rarity == ConsumableRarityTier.Rare ? 120.0 : (item2.Rarity == ConsumableRarityTier.Mid ? 55.0 : 25.0);
                EvaluateFollowupOption(
                    item2.Skill, item2.Skill.Element,
                    assumeCharged: false,
                    assumeUncharged: !item2.Skill.IsFixedPowerDamage && actor.HasMindCharge,
                    itemPenalty: pen,
                    label: item2.Name,
                    requireRevealed: !config.AllowDamageItemProbing,
                    allowNeutralItem: allowNeutralItemFollowup);
            }
        }

        return (bestDmg, bestName);

        void EvaluateFollowupOption(
            SkillMetadata skill2,
            ElementId elem2,
            bool assumeCharged,
            bool assumeUncharged,
            double itemPenalty,
            string label,
            bool requireRevealed,
            bool allowNeutralItem)
        {
            double acc2 = GetAccuracyWeight(skill2);
            bool canKnockDown2 = !skill2.IsInstantKill && (skill2.AilmentMask & 0x80000) == 0;
            bool isItem = itemPenalty > 0.0;

            if (skill2.IsAoE)
            {
                double sum = 0.0;
                bool anyRevealed = !requireRevealed;
                int enemiesDamaged2 = 0;
                int kills2 = 0;
                bool knocksDownAny2 = false;
                for (int i = 0; i < livingEnemies.Count; i++)
                {
                    double remHp = simHpByIdx[i];
                    if (remHp <= 0.0)
                        continue;

                    var enemy = livingEnemies[i];
                    var aff = _bridge.QueryEffectiveAffinity(actor, enemy, skill2.Element, config.RespectAnalysisState);
                    if (AiCombatHelpers.IsUnsafeReflectOrDrain(enemy, skill2.Element, aff))
                        return;
                    if (!aff.CanDealDamage)
                        continue;
                    if (aff.IsRevealed || (byte)skill2.Element > 7 || skill2.Element == ElementId.Almighty)
                        anyRevealed = true;

                    double raw = skill2.IsInstantKill
                        ? EstimateInstantKillExpectedValue(skill2, enemy, aff)
                        : ComputeExpectedSkillDamageWithChargeState(
                            skill2, actor, enemy, aff, assumeCharged, assumeUncharged, config.RespectAnalysisState);
                    if (raw > 0.0)
                        enemiesDamaged2++;
                    double eff = ignoreOverkillCap ? raw : Math.Min(raw, remHp);
                    sum += eff * acc2 + (ignoreOverkillCap ? 0.0 : 0.02 * raw);

                    if (!skill2.IsInstantKill && !skill2.IsPercentHpDamage && raw >= remHp)
                        kills2++;
                    if (canKnockDown2 && !simDownByIdx[i] && _bridge.IsKnownWeakness(enemy, skill2.Element, aff))
                        knocksDownAny2 = true;
                }

                if (isItem && !allowNeutralItem && kills2 == 0 && !knocksDownAny2)
                    return;

                if (!ignoreOverkillCap && kills2 > 0)
                    sum += kills2 * 50.0;
                if (knocksDownAny2)
                    sum += 120.0;

                if (!isItem && enemiesDamaged2 <= 1 && !skill2.IsInstantKill && sum > 0.0)
                    sum -= 0.5;

                sum -= itemPenalty;
                if (anyRevealed && sum > bestDmg)
                {
                    bestDmg = sum;
                    bestName = label;
                }
                return;
            }

            for (int i = 0; i < livingEnemies.Count; i++)
            {
                double remHp = simHpByIdx[i];
                if (remHp <= 0.0)
                    continue;

                var enemy = livingEnemies[i];
                var aff = _bridge.QueryEffectiveAffinity(actor, enemy, elem2, config.RespectAnalysisState);
                if (aff.IsBadHit)
                    continue;
                if (requireRevealed && !aff.IsRevealed && enemy.IsEnemy && (byte)elem2 <= 7 && elem2 != ElementId.Almighty)
                    continue;

                double raw = skill2.IsInstantKill
                    ? EstimateInstantKillExpectedValue(skill2, enemy, aff)
                    : ComputeExpectedSkillDamageWithChargeState(
                        skill2, actor, enemy, aff, assumeCharged, assumeUncharged, config.RespectAnalysisState);
                if (raw <= 0.0)
                    continue;

                bool kills2 = !skill2.IsInstantKill && !skill2.IsPercentHpDamage && raw >= remHp;
                bool knocksDown2 = canKnockDown2 && !simDownByIdx[i] && _bridge.IsKnownWeakness(enemy, elem2, aff);
                if (isItem && !allowNeutralItem && !kills2 && !knocksDown2)
                    continue;

                double eff2 = ignoreOverkillCap ? raw : Math.Min(raw, remHp);
                double score2 = eff2 * acc2 + (ignoreOverkillCap ? 0.0 : 0.02 * raw) - itemPenalty;
                if (!ignoreOverkillCap && kills2)
                    score2 += 50.0;
                if (knocksDown2)
                    score2 += 120.0;

                if (score2 > bestDmg)
                {
                    bestDmg = score2;
                    bestName = $"{label}->{enemy.DisplayName}";
                }
            }
        }
    }

    internal EvaluatedOffense? EvaluateChargeAction(
        BattleSnapshot battle,
        Config config,
        List<SkillMetadata> candidates,
        double bestDirectScore,
        bool ignoreOverkillCap = false,
        List<CombatantSnapshot>? livingEnemies = null)
    {
        var actor = battle.Actor;
        if (battle.IsActorOnOneMore || actor.HasPowerCharge || actor.HasMindCharge)
            return null;

        livingEnemies ??= AiCombatHelpers.CollectLiving(battle.Enemies);
        if (livingEnemies.Count == 0)
            return null;

        int maxEnemyHp = livingEnemies[0].CurrentHp;
        for (int i = 1; i < livingEnemies.Count; i++)
        {
            if (livingEnemies[i].CurrentHp > maxEnemyHp)
                maxEnemyHp = livingEnemies[i].CurrentHp;
        }
        if (!ignoreOverkillCap && bestDirectScore >= maxEnemyHp * 0.75)
            return null;

        EvaluatedOffense? bestCharge = null;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
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
                if (atk.SkillId == 0 || atk.IsInstantKill || atk.IsFixedPowerDamage || atk.IsPercentHpDamage)
                    continue;

                bool matchesPhys = isPowerCharge && atk.Element == ElementId.Phys;
                bool matchesMag = isMindCharge && atk.Element != ElementId.Phys;
                if (!matchesPhys && !matchesMag)
                    continue;

                int atkSp = atk.CostType == 2 ? _bridge.GetEffectiveCost(actor, atk.SkillId) : 0;
                if (chargeSp + atkSp > actor.CurrentSp)
                    continue;

                double atkAcc = GetAccuracyWeight(atk);

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
                    double weightedUncharged = groupUncharged * atkAcc;
                    if (!bad && weightedUncharged * 2.5 > bestChargedDmg)
                    {
                        bestUnchargedDmg = weightedUncharged;
                        bestChargedDmg = weightedUncharged * 2.5;
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
                        atk, actor, enemy, aff, false, true, config.RespectAnalysisState) * atkAcc;
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
            bool hpAllowsCharge = ignoreOverkillCap
                ? (livingEnemies.Count > 1 || maxEnemyHp > bestUnchargedDmg)
                : (maxEnemyHp > bestUnchargedDmg * 1.6);
            double amortizedTurnValue = bestChargedDmg * 0.52;
            if (bestUnchargedDmg > 0 && amortizedTurnValue > bestDirectScore * 1.05 && hpAllowsCharge)
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

    private AiDecision? EvaluateBreakFallback(
        BattleSnapshot battle,
        Config config,
        List<SkillMetadata> candidates,
        List<CombatantSnapshot>? livingEnemies = null)
    {
        var actor = battle.Actor;
        livingEnemies ??= AiCombatHelpers.CollectLiving(battle.Enemies);
        if (livingEnemies.Count == 0)
            return null;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var skill = _bridge.GetSkill(skillId);
            if (skill.Element != ElementId.Support || !_bridge.CanUseSkill(actor, skillId))
                continue;

            ElementId? breakElem = SkillCatalog.GetBreakElement(skill.BuffMask);
            if (breakElem == null)
                continue;

            bool hasSpellOfElem = candidates.Any(c => ResolveAttackElement(actor, c) == breakElem.Value);
            if (!hasSpellOfElem)
                continue;

            CombatantSnapshot? breakTarget = null;
            for (int i = 0; i < livingEnemies.Count; i++)
            {
                var e = livingEnemies[i];
                if (e.HasBreakForElement(breakElem.Value))
                    continue;
                var aff = _bridge.QueryEffectiveAffinity(actor, e, breakElem.Value, config.RespectAnalysisState);
                if (aff.IsNull || aff.IsRepel || aff.IsDrain || aff.IsResist)
                {
                    breakTarget = e;
                    break;
                }
            }

            if (breakTarget != null)
            {
                return new AiDecision
                {
                    ActionKind = AiActionKind.Skill,
                    SkillId = skillId,
                    Target = breakTarget,
                    Score = 50.0,
                    Reason = $"{SkillCatalog.GetSkillName(skillId)} -> {breakTarget.DisplayName} (all direct attacks blocked; stripping {breakElem.Value} resistance)"
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

    private static bool ShouldSkipElementProbe(CombatantSnapshot enemy)
    {
        if (enemy.UnitOrEnemyId is 272 or 273)
            return false;

        return enemy.MaxHp >= 1200 ||
               (SkillCatalog.IsKnownBossUnitId(enemy.UnitOrEnemyId) && enemy.UnitOrEnemyId != 268);
    }

    private bool HasStandingRevealedWeakness(
        CombatantSnapshot actor,
        CombatantSnapshot enemy,
        Dictionary<CombatantSnapshot, bool>? cache)
    {
        if (cache != null && cache.TryGetValue(enemy, out bool cached))
            return cached;

        bool found = false;
        for (byte e = (byte)ElementId.Phys; e <= (byte)ElementId.Dark; e++)
        {
            var elem = (ElementId)e;
            var aff = _bridge.QueryEffectiveAffinity(actor, enemy, elem, respectAnalysis: true);
            if (_bridge.IsKnownWeakness(enemy, elem, aff))
            {
                found = true;
                break;
            }
        }

        if (cache != null)
            cache[enemy] = found;
        return found;
    }

    private double ComputeUnrevealedProbeBonus(
        BattleSnapshot battle,
        Config config,
        SkillMetadata skill,
        CombatantSnapshot enemy,
        List<CombatantSnapshot> livingEnemies,
        ElementId effectiveElem,
        AffinityInfo aff,
        double directDmg,
        bool killsTarget,
        int activeAlliesForAoa,
        int spCost,
        Dictionary<CombatantSnapshot, bool>? revealedWeakCache)
    {
        if (skill.IsAoE)
            return 0.0;

        return ComputeTargetUnrevealedProbeBonus(
            battle, config, skill, enemy, livingEnemies, effectiveElem, aff, directDmg, killsTarget, activeAlliesForAoa, spCost, revealedWeakCache);
    }

    private double ComputeTargetUnrevealedProbeBonus(
        BattleSnapshot battle,
        Config config,
        SkillMetadata skill,
        CombatantSnapshot enemy,
        List<CombatantSnapshot> livingEnemies,
        ElementId effectiveElem,
        AffinityInfo aff,
        double directDmg,
        bool killsTarget,
        int activeAlliesForAoa,
        int spCost,
        Dictionary<CombatantSnapshot, bool>? revealedWeakCache)
    {
        if (!config.RespectAnalysisState || !config.ProbeUnrevealedElements)
            return 0.0;
        if (aff.IsRevealed || aff.IsBadHit || killsTarget || skill.IsInstantKill)
            return 0.0;
        if (!enemy.IsStanding || ShouldSkipElementProbe(enemy))
            return 0.0;

        var actor = battle.Actor;
        if (actor.HasPowerCharge || actor.HasMindCharge || actor.SpRatio < 0.25)
            return 0.0;
        if ((byte)effectiveElem < (byte)ElementId.Fire || (byte)effectiveElem > (byte)ElementId.Wind)
            return 0.0;
        if (enemy.CurrentHp < 180 || enemy.CurrentHp <= directDmg * 1.10)
            return 0.0;
        if (HasStandingRevealedWeakness(actor, enemy, revealedWeakCache))
            return 0.0;

        double baseProbe = Math.Clamp(enemy.CurrentHp * 0.28, 85.0, 210.0);
        int otherStanding = livingEnemies.Count(e => !ReferenceEquals(e, enemy) && e.IsStanding);
        if (otherStanding == 0 && activeAlliesForAoa >= 2)
            baseProbe += 25.0;
        else if (otherStanding == 1 && activeAlliesForAoa >= 2)
            baseProbe += 10.0;

        if (spCost >= 16 && actor.SpRatio < 0.35)
            baseProbe *= 0.4;

        return baseProbe;
    }

    private ElementId ResolveAttackElement(CombatantSnapshot actor, SkillMetadata skill) =>
        skill.SkillId == 0 ? _bridge.GetAttackElement(actor) : skill.Element;

    private static double GetAccuracyWeight(SkillMetadata skill) =>
        skill.SkillId != 0 && skill.Accuracy is > 0 and < 95
            ? Math.Clamp(skill.Accuracy / 100.0 + 0.05, 0.5, 1.0)
            : 1.0;
}
