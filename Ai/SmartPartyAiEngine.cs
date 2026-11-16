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

    public SmartPartyAiEngine(IBattleEngineBridge bridge)
    {
        _offense = new OffenseEvaluator(bridge);
        _defense = new DefenseAndTelegraphEvaluator(bridge);
        _support = new SupportAndRecoveryEvaluator(bridge);
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
        var livingEnemies = battle.Enemies.Where(e => !e.IsDead).ToList();
        var livingAllies = battle.Allies.Where(a => !a.IsDead).ToList();
        var candidates = _offense.GatherCandidateAttackSkills(battle.Actor, zeroSpOnly: false, safeHpFloorPercent: 0);
        var bestAttack = _offense.EvaluateBestOffensiveAction(
            battle, config, candidates, maxSpCost: int.MaxValue, preferLowCostTiebreak: true, heavySpPenalty: 0.05);

        var telegraphDefense = _defense.TryChooseTelegraphDefense(
            battle, config, livingEnemies, livingAllies, bestAttack, allowAllyWallSupport: false, allowGenericChargeGuard: false);
        if (telegraphDefense != null)
            return telegraphDefense;

        var spItemDecision = _support.EvaluateSpRecoveryItem(
            battle, config, allowAllies: false, livingEnemies, livingAllies, bestAttack);
        if (spItemDecision != null)
            return spItemDecision;

        return _offense.EvaluateOffenseWithFallbacks(battle, config, candidates, bestAttack);
    }

    // -------------------------------------------------------------------------
    // PRESET 2: CONSERVE SP
    // -------------------------------------------------------------------------
    public AiDecision EvaluateConserveSp(BattleSnapshot battle, Config config)
    {
        var actor = battle.Actor;
        int safeHpPct = Math.Clamp(config.ConserveSpSafeHpPercent, 10, 95);
        int lowSpLimit = Math.Clamp(config.ConserveSpLowCostThreshold, 0, 100);
        var livingEnemies = battle.Enemies.Where(e => !e.IsDead).ToList();
        var livingAllies = battle.Allies.Where(a => !a.IsDead).ToList();

        // 1. Evaluate 0-SP options (Normal Attack + HP-cost Physical skills when HP is safe)
        var freeCandidates = _offense.GatherCandidateAttackSkills(actor, zeroSpOnly: true, safeHpFloorPercent: safeHpPct);
        var bestFree = _offense.EvaluateBestOffensiveAction(battle, config, freeCandidates, maxSpCost: 0, preferLowCostTiebreak: false);

        // Compute minimal-SP full-kill check when 0-SP cannot finish the whole encounter cleanly this turn
        var allCandidates = _offense.GatherCandidateAttackSkills(actor, zeroSpOnly: false, safeHpFloorPercent: safeHpPct);
        var bestMinimalSp = bestFree?.FinishesBattle == true
            ? null
            : _offense.EvaluateBestOffensiveAction(
                battle, config, allCandidates, maxSpCost: int.MaxValue, preferLowCostTiebreak: true, heavySpPenalty: 2.0);
        var effectiveFinishCheck = bestFree?.FinishesBattle == true
            ? bestFree
            : (bestMinimalSp?.FinishesBattle == true ? bestMinimalSp : bestFree);

        var telegraphDefense = _defense.TryChooseTelegraphDefense(
            battle, config, livingEnemies, livingAllies, effectiveFinishCheck, allowAllyWallSupport: false, allowGenericChargeGuard: false);
        if (telegraphDefense != null)
            return telegraphDefense;

        // If 0-SP already kills or triggers All-Out Attack, take it without spending SP
        if (bestFree != null && (bestFree.TriggersAllOutAttack || bestFree.KillsAny))
            return bestFree.Decision;

        // Allow spending minimal SP when 0-SP cannot finish the fight this turn and a skill ends it immediately
        if (bestMinimalSp != null && bestMinimalSp.FinishesBattle)
            return bestMinimalSp.Decision;

        // 2. Check cheap SP knockdown starters (e.g., Agi/Bufu/Zio/Garu) that grant 1-More -> heavy free follow-up or AOA
        var cheapLookahead = _offense.EvaluateBestOffensiveAction(
            battle, config, allCandidates, maxSpCost: lowSpLimit, preferLowCostTiebreak: true);

        double freeDirect = bestFree?.DirectDamage ?? 0.0;
        if (cheapLookahead != null &&
            cheapLookahead.TriggersOneMore &&
            (cheapLookahead.TriggersAllOutAttack ||
             cheapLookahead.TwoStepTotalDamage >= Math.Max(freeDirect * 1.25, freeDirect + 30.0)))
        {
            return cheapLookahead.Decision;
        }

        // 3. SP Recovery (Spirit Drain first since it is free, then SP items if configured)
        if (actor.MaxSp > 0 && actor.SpRatio < 0.35)
        {
            var spiritDrain = _support.EvaluateSpiritDrain(battle, config);
            if (spiritDrain != null)
                return spiritDrain;
        }

        var spItemDecision = _support.EvaluateSpRecoveryItem(
            battle, config, allowAllies: false, livingEnemies, livingAllies, effectiveFinishCheck);
        if (spItemDecision != null)
            return spItemDecision;

        // 4. Take best safe 0-SP attack if it actually damages something
        if (bestFree != null && bestFree.DirectDamage > 0.01)
            return bestFree.Decision;

        // 5. Fallback: if all enemies block/null 0-SP Phys/Normal Attack, spend minimal SP to actually damage
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
        var livingEnemies = battle.Enemies.Where(e => !e.IsDead).ToList();
        var livingAllies = battle.Allies.Where(a => !a.IsDead).ToList();

        var candidates = _offense.GatherCandidateAttackSkills(battle.Actor, zeroSpOnly: false, safeHpFloorPercent: 0);
        var bestAttack = _offense.EvaluateBestOffensiveAction(
            battle, config, candidates, maxSpCost: int.MaxValue, preferLowCostTiebreak: true, heavySpPenalty: 0.35);

        if (bestAttack?.FinishesBattle == true)
            return bestAttack.Decision;

        var threats = DefenseAndTelegraphEvaluator.AnalyzeTelegraphedThreats(battle, livingEnemies);
        var mandatoryGuard = _defense.TryChooseMandatoryTelegraphGuard(battle, config, threats, livingEnemies, bestAttack);
        if (mandatoryGuard != null)
            return mandatoryGuard;

        var revive = _support.EvaluateRevive(battle);
        if (revive != null)
            return revive;

        var heal = _support.EvaluateHealing(battle, healThreshold, allowNormalTriage: true);
        if (heal != null)
            return heal;

        var cure = _support.EvaluateAilmentCure(battle);
        if (cure != null)
            return cure;

        var reactiveDefense = _defense.TryChooseReactiveTelegraphSupportOrChargeGuard(
            battle, config, threats, livingEnemies, livingAllies, allowAllyWallSupport: true, allowGenericChargeGuard: true);
        if (reactiveDefense != null)
            return reactiveDefense;

        var spItemDecision = _support.EvaluateSpRecoveryItem(
            battle, config, allowAllies: config.AllowAllySpItemSupport, livingEnemies, livingAllies, bestAttack);
        if (spItemDecision != null)
            return spItemDecision;

        var support = _support.EvaluateSupportMaintenance(battle, config, strictPriority: true);
        if (support != null)
            return support;

        return _offense.EvaluateOffenseWithFallbacks(battle, config, candidates, bestAttack);
    }

    // -------------------------------------------------------------------------
    // PRESET 4: ACT FREELY (Balanced All-Rounder)
    // -------------------------------------------------------------------------
    public AiDecision EvaluateActFreely(BattleSnapshot battle, Config config)
    {
        double emergencyThreshold = Math.Clamp(config.ActFreelyEmergencyHpThreshold, 10, 90) / 100.0;
        var livingEnemies = battle.Enemies.Where(e => !e.IsDead).ToList();
        var livingAllies = battle.Allies.Where(a => !a.IsDead).ToList();

        var candidates = _offense.GatherCandidateAttackSkills(battle.Actor, zeroSpOnly: false, safeHpFloorPercent: 0);
        var bestAttack = _offense.EvaluateBestOffensiveAction(
            battle, config, candidates, maxSpCost: int.MaxValue, preferLowCostTiebreak: true, heavySpPenalty: 0.35);

        var threats = DefenseAndTelegraphEvaluator.AnalyzeTelegraphedThreats(battle, livingEnemies);
        var mandatoryGuard = _defense.TryChooseMandatoryTelegraphGuard(battle, config, threats, livingEnemies, bestAttack);
        if (mandatoryGuard != null)
            return mandatoryGuard;

        var revive = _support.EvaluateRevive(battle);
        if (revive != null)
            return revive;

        var emergencyHeal = _support.EvaluateHealing(battle, emergencyThreshold, allowNormalTriage: false);
        if (emergencyHeal != null)
            return emergencyHeal;

        if (bestAttack != null && (bestAttack.TriggersAllOutAttack || bestAttack.KillsAny))
            return bestAttack.Decision;

        var reactiveDefense = _defense.TryChooseReactiveTelegraphSupportOrChargeGuard(
            battle, config, threats, livingEnemies, livingAllies, allowAllyWallSupport: true, allowGenericChargeGuard: true);
        if (reactiveDefense != null)
            return reactiveDefense;

        var spItemDecision = _support.EvaluateSpRecoveryItem(
            battle, config, allowAllies: config.AllowAllySpItemSupport, livingEnemies, livingAllies, bestAttack);
        if (spItemDecision != null)
            return spItemDecision;

        if (!battle.IsActorOnOneMore && livingEnemies.Count > 0 && livingEnemies.Max(e => e.MaxHp) >= 500)
        {
            var support = _support.EvaluateSupportMaintenance(battle, config, strictPriority: false);
            if (support != null)
                return support;
        }

        return _offense.EvaluateOffenseWithFallbacks(battle, config, candidates, bestAttack);
    }
}
