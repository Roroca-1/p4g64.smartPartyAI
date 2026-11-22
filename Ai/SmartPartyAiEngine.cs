using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

/// <summary>
/// Data-driven combat evaluator replacing P4G party member AI tactics across all presets.
/// </summary>
public sealed class SmartPartyAiEngine
{
    private readonly OffenseEvaluator _offense;
    private readonly DefenseAndTelegraphEvaluator _defense;
    private readonly SupportAndRecoveryEvaluator _support;
    private readonly TeamOffenseEstimator _estimator;
    private readonly AilmentEvaluator _ailments;
    private readonly ActFreelyRoutePlanner _actFreelyPlanner;

    public SmartPartyAiEngine(IBattleEngineBridge bridge)
    {
        _offense = new OffenseEvaluator(bridge);
        _defense = new DefenseAndTelegraphEvaluator(bridge);
        _estimator = new TeamOffenseEstimator(bridge);
        _support = new SupportAndRecoveryEvaluator(bridge, _estimator);
        _ailments = new AilmentEvaluator(bridge);
        _actFreelyPlanner = new ActFreelyRoutePlanner(bridge, _offense, _defense, _support, _estimator, _ailments);
    }

    public AiDecision? DecideAction(BattleSnapshot battle, Config config)
    {
        return battle.Actor.Tactic switch
        {
            TacticMode.FullAssault when config.EnableSmartFullAssault => EvaluateFullAssault(battle, config),
            TacticMode.ConserveSp when config.EnableSmartConserveSp => EvaluateConserveSp(battle, config),
            TacticMode.HealSupport when config.EnableSmartHealSupport => EvaluateHealSupport(battle, config),
            TacticMode.ActFreely when config.EnableSmartActFreely => EvaluateActFreely(battle, config),
            _ => null
        };
    }

    // -------------------------------------------------------------------------
    // PRESET 1: FULL ASSAULT
    // -------------------------------------------------------------------------
    public AiDecision EvaluateFullAssault(BattleSnapshot battle, Config config)
    {
        var livingEnemies = AiCombatHelpers.CollectLiving(battle.Enemies);
        var livingAllies = AiCombatHelpers.CollectLiving(battle.Allies);
        var profile = new OffenseScoringProfile(
            IgnoreOverkillCap: true,
            IgnoreResourceCosts: true,
            AllowDamageItemOutrank: true);

        var candidates = _offense.GatherCandidateAttackSkills(battle.Actor, zeroSpOnly: false, safeHpFloorPercent: 0);
        IReadOnlyList<BattleConsumableInfo>? allowedDamageItems = config.EnableDamageItemsInAi
            ? _offense.GatherAllowedDamageItems(battle.Actor, config)
            : null;
        var bestSkill = _offense.EvaluateBestOffensiveAction(battle, config, candidates, profile, livingEnemies, allowedDamageItems);
        var bestAttack = ApplyDamageItemSelection(battle, config, candidates, bestSkill, profile, livingEnemies, allowedDamageItems);

        var telegraphDefense = _defense.TryChooseTelegraphDefense(
            battle, config, livingEnemies, livingAllies, bestAttack, allowAllyWallSupport: false, allowGenericChargeGuard: false);
        if (telegraphDefense != null)
            return telegraphDefense;

        EvaluatedOffense? chargeEval = null;
        bool hasPrecomputedCharge = false;
        if (!battle.IsActorOnOneMore && !battle.Actor.HasPowerCharge && !battle.Actor.HasMindCharge &&
            livingEnemies.Count > 0 && livingAllies.Count > 0)
        {
            double directScore = bestAttack?.Score ?? 0.0;
            chargeEval = _offense.EvaluateChargeAction(
                battle, config, candidates, directScore, ignoreOverkillCap: true, livingEnemies: livingEnemies);
            hasPrecomputedCharge = true;
            bool wantsCharge = chargeEval != null && chargeEval.Score > directScore;

            var primaryEnemy = AiCombatHelpers.SelectMaxCurrentHpEnemy(livingEnemies);
            var teamCtx = _estimator.BuildTeamContext(battle, config, livingAllies, livingEnemies, primaryEnemy, bestAttack);
            var offensiveSetup = _support.EvaluateOffensiveSetupForAssault(
                battle, config, bestAttack, wantsCharge, livingAllies, livingEnemies, teamCtx, primaryEnemy);
            if (offensiveSetup != null)
                return offensiveSetup;

            var ailmentEval = _ailments.EvaluateBestAilmentAction(
                battle, config, teamCtx, TacticMode.FullAssault, livingEnemies, bestAttack);
            if (ailmentEval != null)
            {
                var fallback = _offense.EvaluateOffenseWithFallbacks(
                    battle, config, candidates, bestAttack, ignoreOverkillCap: true, hasPrecomputedCharge, chargeEval, livingEnemies);
                if (ailmentEval.RawScore > fallback.Score)
                    return ailmentEval.Decision;
                return fallback;
            }
        }

        return _offense.EvaluateOffenseWithFallbacks(
            battle, config, candidates, bestAttack, ignoreOverkillCap: true, hasPrecomputedCharge, chargeEval, livingEnemies);
    }

    // -------------------------------------------------------------------------
    // PRESET 2: CONSERVE SP
    // -------------------------------------------------------------------------
    public AiDecision EvaluateConserveSp(BattleSnapshot battle, Config config)
    {
        var actor = battle.Actor;
        int safeHpPct = Math.Clamp(config.ConserveSpSafeHpPercent, 10, 95);
        int lowSpLimit = Math.Clamp(config.ConserveSpLowCostThreshold, 0, 100);
        var livingEnemies = AiCombatHelpers.CollectLiving(battle.Enemies);
        var livingAllies = AiCombatHelpers.CollectLiving(battle.Allies);

        var freeProfile = new OffenseScoringProfile(
            MaxSpCost: 0,
            PreferLowCostTiebreak: true,
            SpCostWeight: 0.0,
            HpCostWeight: 0.20);
        var minimalSpProfile = new OffenseScoringProfile(
            MaxSpCost: int.MaxValue,
            PreferLowCostTiebreak: true,
            SpCostWeight: 2.0,
            HpCostWeight: 0.15);

        // 1. Evaluate 0-SP options (Normal Attack + HP-cost Physical skills when HP is safe)
        // Conserve SP never executes damage consumables, so exclude them from 1-More follow-up projections too.
        var freeCandidates = _offense.GatherCandidateAttackSkills(actor, zeroSpOnly: true, safeHpFloorPercent: safeHpPct);
        var bestFree = _offense.EvaluateBestOffensiveAction(battle, config, freeCandidates, freeProfile, livingEnemies, allowedDamageItems: null);

        // Compute minimal-SP full-kill check when 0-SP cannot finish the whole encounter cleanly this turn
        var allCandidates = _offense.GatherCandidateAttackSkills(actor, zeroSpOnly: false, safeHpFloorPercent: safeHpPct);
        var bestMinimalSp = bestFree?.FinishesBattle == true
            ? null
            : _offense.EvaluateBestOffensiveAction(battle, config, allCandidates, minimalSpProfile, livingEnemies, allowedDamageItems: null);
        var effectiveFinishCheck = bestFree?.FinishesBattle == true
            ? bestFree
            : (bestMinimalSp?.FinishesBattle == true ? bestMinimalSp : bestFree);

        var threats = DefenseAndTelegraphEvaluator.AnalyzeTelegraphedThreats(battle, livingEnemies);
        var telegraphDefense = _defense.TryChooseTelegraphDefense(
            battle, config, livingEnemies, livingAllies, effectiveFinishCheck, allowAllyWallSupport: false, allowGenericChargeGuard: false, precomputedThreats: threats);
        if (telegraphDefense != null)
            return telegraphDefense;

        var preReviveChargeGuard = _defense.TryChooseChargedBossSurvivalGuardBeforeRevive(battle, config, threats, livingEnemies, effectiveFinishCheck);
        if (preReviveChargeGuard != null)
            return preReviveChargeGuard;

        // If 0-SP already kills or triggers All-Out Attack, take it without spending SP
        if (bestFree != null && (bestFree.FinishesBattle || bestFree.TriggersAllOutAttack || bestFree.KillsAny))
            return bestFree.Decision;

        // Allow spending minimal SP when 0-SP cannot finish the fight this turn and a skill ends it immediately
        if (bestMinimalSp != null && bestMinimalSp.FinishesBattle)
            return bestMinimalSp.Decision;

        TeamOffenseContext? teamCtx = null;
        CombatantSnapshot? primaryEnemy = null;
        if (livingEnemies.Count > 0 && livingAllies.Count > 0)
        {
            primaryEnemy = AiCombatHelpers.SelectMaxCurrentHpEnemy(livingEnemies);
            teamCtx = _estimator.BuildTeamContext(
                battle, config, livingAllies, livingEnemies, primaryEnemy, bestFree ?? bestMinimalSp);

            var conserveSupport = _support.EvaluateConserveSpSupport(
                battle, config, teamCtx, livingAllies, livingEnemies, primaryEnemy, bestFree ?? bestMinimalSp, precomputedThreats: threats);
            if (conserveSupport != null)
                return conserveSupport;
        }

        // 2. Check cheap SP knockdown starters (e.g., Agi/Bufu/Zio/Garu) that grant 1-More -> heavy free follow-up or AOA
        var cheapProfile = new OffenseScoringProfile(
            MaxSpCost: lowSpLimit,
            PreferLowCostTiebreak: true,
            SpCostWeight: 0.35,
            HpCostWeight: 0.20);
        var cheapLookahead = _offense.EvaluateBestOffensiveAction(battle, config, allCandidates, cheapProfile, livingEnemies, allowedDamageItems: null);

        double freeDirect = bestFree?.DirectDamage ?? 0.0;
        if (cheapLookahead != null &&
            cheapLookahead.TriggersOneMore &&
            (cheapLookahead.TriggersAllOutAttack ||
             cheapLookahead.TwoStepTotalDamage >= Math.Max(freeDirect * 1.25, freeDirect + 30.0)))
        {
            return cheapLookahead.Decision;
        }

        // 3. High-ROI cheap ailment / Ghastly Wail (before burning SP on probes)
        if (teamCtx != null)
        {
            var ailmentEval = _ailments.EvaluateBestAilmentAction(
                battle, config, teamCtx, TacticMode.ConserveSp, livingEnemies, bestFree ?? bestMinimalSp);
            if (ailmentEval != null)
                return ailmentEval.Decision;
        }

        // 4. Optional cheap unrevealed-affinity probe on standing non-boss shadows when SP is healthy
        var cheapProbe = _offense.EvaluateConserveSpProbe(battle, config, allCandidates, lowSpLimit, freeDirect, livingEnemies);
        if (cheapProbe != null)
            return cheapProbe;

        // 5. SP Recovery (Spirit Drain first since it is free, skipping if battle ends this round; then SP items)
        bool battleEndsThisRound = (bestFree?.FinishesBattle == true) ||
                                   (teamCtx?.HasDamageModel == true && teamCtx.EstimatedRoundsRemaining <= 1.25);
        if (!battleEndsThisRound && actor.MaxSp > 0 && actor.SpRatio < 0.35)
        {
            var spiritDrain = _support.EvaluateSpiritDrain(battle, config);
            if (spiritDrain != null)
                return spiritDrain;
        }

        var spItemDecision = _support.EvaluateSpRecoveryItem(
            battle, config, allowAllies: false, livingEnemies, livingAllies, effectiveFinishCheck);
        if (spItemDecision != null)
            return spItemDecision;

        // 6. Use minimal SP skill when 0-SP deals < 25% of its damage in a dangerous multi-round fight
        bool zeroSpPoorInDanger =
            bestMinimalSp != null &&
            bestMinimalSp.DirectDamage >= Math.Max(40.0, freeDirect * 4.0) &&
            (teamCtx == null || !teamCtx.HasDamageModel || teamCtx.EstimatedRoundsRemaining >= 2.0) &&
            (livingEnemies.Any(e => e.MaxHp >= 500) || livingAllies.Any(a => a.HpRatio < 0.55));
        if (zeroSpPoorInDanger)
            return bestMinimalSp!.Decision;

        // 7. Take best safe 0-SP attack if it actually damages something
        if (bestFree != null && bestFree.DirectDamage > 0.01)
            return bestFree.Decision;

        // 8. Fallback: if all enemies block/null 0-SP Phys/Normal Attack, spend minimal SP to actually damage
        if (bestMinimalSp != null && bestMinimalSp.DirectDamage > 0.01)
            return bestMinimalSp.Decision;

        return new AiDecision
        {
            ActionKind = AiActionKind.Guard,
            SkillId = 0,
            Score = 0,
            Reason = "ConserveSP: No safe/damaging action; Guarding"
        };
    }

    // -------------------------------------------------------------------------
    // PRESET 3: HEAL / SUPPORT
    // -------------------------------------------------------------------------
    public AiDecision EvaluateHealSupport(BattleSnapshot battle, Config config)
    {
        double healThreshold = Math.Clamp(config.HealSupportHpThreshold, 10, 95) / 100.0;
        var livingEnemies = AiCombatHelpers.CollectLiving(battle.Enemies);
        var livingAllies = AiCombatHelpers.CollectLiving(battle.Allies);

        var profile = new OffenseScoringProfile(SpCostWeight: 0.35, HpCostWeight: 0.05);
        var candidates = _offense.GatherCandidateAttackSkills(battle.Actor, zeroSpOnly: false, safeHpFloorPercent: 0);
        IReadOnlyList<BattleConsumableInfo>? allowedDamageItems = config.EnableDamageItemsInAi
            ? _offense.GatherAllowedDamageItems(battle.Actor, config)
            : null;
        var bestSkill = _offense.EvaluateBestOffensiveAction(battle, config, candidates, profile, livingEnemies, allowedDamageItems);
        var bestAttack = ApplyDamageItemSelection(battle, config, candidates, bestSkill, profile, livingEnemies, allowedDamageItems);

        if (bestAttack?.FinishesBattle == true)
            return bestAttack.Decision;

        var threats = DefenseAndTelegraphEvaluator.AnalyzeTelegraphedThreats(battle, livingEnemies);
        var mandatoryGuard = _defense.TryChooseMandatoryTelegraphGuard(battle, config, threats, livingEnemies, bestAttack);
        if (mandatoryGuard != null)
            return mandatoryGuard;

        if (bestAttack?.TriggersAllOutAttack == true &&
            battle.Allies.All(a => !a.IsDead && a.HpRatio >= 0.45))
        {
            return bestAttack.Decision;
        }

        TeamOffenseContext? teamCtx = null;
        CombatantSnapshot? primaryEnemy = null;
        if (livingEnemies.Count > 0 && livingAllies.Count > 0)
        {
            primaryEnemy = AiCombatHelpers.SelectMaxCurrentHpEnemy(livingEnemies);
            teamCtx = _estimator.BuildTeamContext(battle, config, livingAllies, livingEnemies, primaryEnemy, bestAttack);
        }

        var preReviveChargeGuard = _defense.TryChooseChargedBossSurvivalGuardBeforeRevive(battle, config, threats, livingEnemies, bestAttack);
        if (preReviveChargeGuard != null)
            return preReviveChargeGuard;

        bool hasDeadAllies = battle.Allies.Any(a => a.IsDead);
        double criticalSelfThreshold = Math.Min(healThreshold, 0.40);
        bool criticalSelfOrMc = battle.Actor.HpRatio < criticalSelfThreshold ||
                                livingAllies.Any(a => a.UnitOrEnemyId == 1 && a.HpRatio < 0.30);
        if (criticalSelfOrMc)
        {
            var criticalHeal = _support.EvaluateHealing(
                battle,
                Math.Max(criticalSelfThreshold, 0.30),
                allowNormalTriage: true,
                config: config,
                allowItems: config.EnableSupportItemsInAi);
            if (criticalHeal != null)
                return criticalHeal;
        }

        bool isBossFight = livingEnemies.Any(AiCombatHelpers.IsLikelyBossOrMiniboss);
        bool suppressPartialForBossTriage = hasDeadAllies &&
                                            isBossFight &&
                                            livingAllies.Count >= 3 &&
                                            livingAllies.Any(a => a.HpRatio <= 0.55);
        bool allowPartialRevive = (!config.EnableTelegraphGuard || !threats.AnyBossCharged) &&
                                  !suppressPartialForBossTriage;
        var revive = _support.EvaluateRevive(
            battle,
            config,
            allowItems: config.EnableSupportItemsInAi,
            allowPartialHpRevive: allowPartialRevive,
            threats: threats,
            allowTelegraphWeakRevive: !config.EnableTelegraphGuard);
        if (revive != null)
            return revive;

        if (suppressPartialForBossTriage)
        {
            var stabilizeHeal = _support.EvaluateHealing(
                battle,
                Math.Max(healThreshold, 0.56),
                allowNormalTriage: true,
                config: config,
                allowItems: config.EnableSupportItemsInAi);
            if (stabilizeHeal != null)
                return stabilizeHeal;
        }

        bool allowNonEmergencyHeal = teamCtx == null || !teamCtx.HasDamageModel || teamCtx.EstimatedRoundsRemaining > 1.2;
        var heal = _support.EvaluateHealing(
            battle,
            allowNonEmergencyHeal ? healThreshold : Math.Min(healThreshold, 0.45),
            allowNormalTriage: allowNonEmergencyHeal,
            config: config,
            allowItems: config.EnableSupportItemsInAi);
        if (heal != null)
            return heal;

        var cure = _support.EvaluateAilmentCure(battle, config, allowItems: config.EnableSupportItemsInAi);
        if (cure != null)
            return cure;

        var reactiveDefense = _defense.TryChooseReactiveTelegraphSupportOrChargeGuard(
            battle, config, threats, livingEnemies, livingAllies, allowAllyWallSupport: true, allowGenericChargeGuard: true, bestOffense: bestAttack);
        if (reactiveDefense != null)
            return reactiveDefense;

        var spItemDecision = _support.EvaluateSpRecoveryItem(
            battle, config, allowAllies: config.AllowAllySpItemSupport, livingEnemies, livingAllies, bestAttack);
        if (spItemDecision != null)
            return spItemDecision;

        double directScore = bestAttack?.Score ?? 0.0;
        var chargeEval = _offense.EvaluateChargeAction(
            battle, config, candidates, directScore, ignoreOverkillCap: false, livingEnemies: livingEnemies);
        bool wantsCharge = chargeEval != null && chargeEval.Score > directScore;
        var support = _support.EvaluateSupportMaintenanceWithNetValue(
            battle, config, strictPriority: true, bestAttack, wantsCharge, livingAllies, livingEnemies, teamCtx, primaryEnemy);

        EvaluatedAilment? ailmentEval = teamCtx != null
            ? _ailments.EvaluateBestAilmentAction(battle, config, teamCtx, TacticMode.HealSupport, livingEnemies, bestAttack)
            : null;

        bool highValueSupport = support != null &&
                                (support.TrueNetValue >= 150.0 ||
                                 (bestAttack?.KillsAny != true && (ailmentEval == null || support.TrueNetValue >= ailmentEval.MarginalValue)));
        if (highValueSupport)
            return support!.Decision;

        if (ailmentEval != null && bestAttack?.TriggersOneMore != true)
            return ailmentEval.Decision;

        if (support != null && (bestAttack == null || (!bestAttack.KillsAny && bestAttack.DirectDamage < 200.0)))
            return support.Decision;

        return _offense.EvaluateOffenseWithFallbacks(
            battle, config, candidates, bestAttack, ignoreOverkillCap: false, hasPrecomputedCharge: true, precomputedCharge: chargeEval, livingEnemies: livingEnemies);
    }

    // -------------------------------------------------------------------------
    // PRESET 4: ACT FREELY (Balanced All-Rounder / Least-Turns Route Planner)
    // -------------------------------------------------------------------------
    public AiDecision EvaluateActFreely(BattleSnapshot battle, Config config) =>
        _actFreelyPlanner.Evaluate(battle, config);

    private EvaluatedOffense? ApplyDamageItemSelection(
        BattleSnapshot battle,
        Config config,
        List<SkillMetadata> candidates,
        EvaluatedOffense? bestAttack,
        OffenseScoringProfile profile,
        List<CombatantSnapshot> livingEnemies,
        IReadOnlyList<BattleConsumableInfo>? allowedDamageItems)
    {
        if (bestAttack != null && (bestAttack.TriggersAllOutAttack || bestAttack.FinishesBattle))
            return bestAttack;

        bool allowNeutral = profile.AllowDamageItemOutrank ||
                            battle.Actor.HasSilenced ||
                            bestAttack == null ||
                            bestAttack.DirectDamage <= 0.01;
        var bestItem = _offense.EvaluateBestDamageItem(
            battle, config, candidates, allowNeutral, profile, livingEnemies, allowedDamageItems);
        if (bestItem == null)
            return bestAttack;

        if (profile.AllowDamageItemOutrank)
        {
            if (bestAttack == null || bestItem.Score > bestAttack.Score || bestItem.TriggersAllOutAttack)
                return bestItem;
            return bestAttack;
        }

        bool allowedFallback = bestItem.TriggersAllOutAttack ||
                               battle.Actor.HasSilenced ||
                               bestAttack == null ||
                               bestAttack.DirectDamage <= 0.01;
        if (!allowedFallback)
            return bestAttack;

        if (bestAttack == null || bestItem.Score >= bestAttack.Score || bestItem.TriggersAllOutAttack)
            return bestItem;

        return bestAttack;
    }
}
