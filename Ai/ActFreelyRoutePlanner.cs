using System;
using System.Collections.Generic;
using System.Linq;
using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

/// <summary>
/// Turn-order-aware least-turns route planner for the Act Freely preset.
/// Coordinates immediate finishes, survival triage, selective ailment curing,
/// weak-hit/bomb 1-More setups, Charge, team buffs/debuffs, and fallback attacks.
/// </summary>
internal sealed class ActFreelyRoutePlanner
{
    private readonly IBattleEngineBridge _bridge;
    private readonly OffenseEvaluator _offense;
    private readonly DefenseAndTelegraphEvaluator _defense;
    private readonly SupportAndRecoveryEvaluator _support;
    private readonly TeamOffenseEstimator _estimator;
    private readonly AilmentEvaluator _ailments;

    public ActFreelyRoutePlanner(
        IBattleEngineBridge bridge,
        OffenseEvaluator offense,
        DefenseAndTelegraphEvaluator defense,
        SupportAndRecoveryEvaluator support,
        TeamOffenseEstimator estimator,
        AilmentEvaluator ailments)
    {
        _bridge = bridge;
        _offense = offense;
        _defense = defense;
        _support = support;
        _estimator = estimator;
        _ailments = ailments;
    }

    public AiDecision Evaluate(BattleSnapshot battle, Config config)
    {
        var livingEnemies = AiCombatHelpers.CollectLiving(battle.Enemies);
        var livingAllies = AiCombatHelpers.CollectLiving(battle.Allies);

        var candidates = _offense.GatherCandidateAttackSkills(battle.Actor, zeroSpOnly: false, safeHpFloorPercent: 0);
        var actFreelyProfile = new OffenseScoringProfile(SpCostWeight: 0.35, HpCostWeight: 0.25);
        IReadOnlyList<BattleConsumableInfo>? allowedDamageItems = config.EnableDamageItemsInAi
            ? _offense.GatherAllowedDamageItems(battle.Actor, config)
            : null;

        var bestSkill = _offense.EvaluateBestOffensiveAction(
            battle, config, candidates, actFreelyProfile, livingEnemies, allowedDamageItems);

        var bestItem = _offense.EvaluateBestDamageItem(
            battle,
            config,
            candidates,
            allowNeutralChipItem: battle.Actor.HasSilenced || bestSkill == null || bestSkill.DirectDamage <= 0.01,
            profile: actFreelyProfile,
            livingEnemies: livingEnemies,
            allowedDamageItems: allowedDamageItems);

        var bestAttack = PickBetterOffense(bestSkill, bestItem);

        // 1. Immediate battle victory takes priority over all setup/healing when enabled.
        if (config.PreferLeastTurnsWin && bestAttack?.FinishesBattle == true)
            return bestAttack.Decision;

        if (livingEnemies.Count == 0)
            return _offense.EvaluateOffenseWithFallbacks(battle, config, candidates, bestAttack, livingEnemies: livingEnemies);

        // 2. Mandatory boss telegraph defense and Charged-boss survival guard before risky revives.
        var threatInfo = DefenseAndTelegraphEvaluator.AnalyzeTelegraphedThreats(battle, livingEnemies);
        var mandatoryGuard = _defense.TryChooseMandatoryTelegraphGuard(battle, config, threatInfo, livingEnemies, bestAttack);
        if (mandatoryGuard != null)
            return mandatoryGuard;

        var preReviveChargeGuard = _defense.TryChooseChargedBossSurvivalGuardBeforeRevive(battle, config, threatInfo, livingEnemies, bestAttack);
        if (preReviveChargeGuard != null)
            return preReviveChargeGuard;

        // 3. Critical self/Protagonist survival healing BEFORE revives so a critically hurt survivor heals instead of trading lives.
        double emergencyThreshold = Math.Clamp(config.ActFreelyEmergencyHpThreshold, 10, 90) / 100.0;
        bool hasDeadAllies = battle.Allies.Any(a => a.IsDead);
        bool criticalSelfOrMc = battle.Actor.HpRatio < emergencyThreshold ||
                                livingAllies.Any(a => a.UnitOrEnemyId == 1 && a.HpRatio < 0.30);
        if (criticalSelfOrMc &&
            (hasDeadAllies || !ShouldDeferEmergencyHealToFasterHealer(battle, config, livingAllies, emergencyThreshold)))
        {
            var criticalHeal = _support.EvaluateHealing(
                battle,
                Math.Max(emergencyThreshold, 0.30),
                allowNormalTriage: false,
                config: config,
                allowItems: config.EnableSupportItemsInAi);
            if (criticalHeal != null)
                return criticalHeal;
        }

        // 4. Emergency Revive before anything non-lethal (suppressing partial-HP revives while a boss holds Charge
        //    or when 3+ living allies already stand in a boss fight and a survivor is <= 55% HP).
        bool isBossFight = livingEnemies.Any(AiCombatHelpers.IsLikelyBossOrMiniboss);
        bool suppressPartialForBossTriage = hasDeadAllies &&
                                            isBossFight &&
                                            livingAllies.Count >= 3 &&
                                            livingAllies.Any(a => a.HpRatio <= 0.55);
        bool allowPartialRevive = (!config.EnableTelegraphGuard || !threatInfo.AnyBossCharged) &&
                                  !suppressPartialForBossTriage;
        var revive = _support.EvaluateRevive(
            battle,
            config,
            allowItems: config.EnableSupportItemsInAi,
            allowPartialHpRevive: allowPartialRevive,
            threats: threatInfo,
            allowTelegraphWeakRevive: !config.EnableTelegraphGuard);
        if (revive != null)
            return revive;

        if (suppressPartialForBossTriage)
        {
            var stabilizeHeal = _support.EvaluateHealing(
                battle,
                Math.Max(emergencyThreshold, 0.56),
                allowNormalTriage: true,
                config: config,
                allowItems: config.EnableSupportItemsInAi);
            if (stabilizeHeal != null)
                return stabilizeHeal;
        }

        // 5. Emergency Heal for remaining injured allies unless a faster HealSupport teammate will safely heal first.
        if (!ShouldDeferEmergencyHealToFasterHealer(battle, config, livingAllies, emergencyThreshold))
        {
            var emergencyHeal = _support.EvaluateHealing(
                battle,
                emergencyThreshold,
                allowNormalTriage: false,
                config: config,
                allowItems: config.EnableSupportItemsInAi);
            if (emergencyHeal != null)
                return emergencyHeal;
        }

        var primaryEnemy = AiCombatHelpers.SelectPrimaryEnemy(livingEnemies, bestAttack);
        var teamCtx = _estimator.BuildTeamContext(battle, config, livingAllies, livingEnemies, primaryEnemy, bestAttack);

        // 5. Lifesaving ailment cure (e.g. Poison lethal tick before enemy turn, Confuse/Charm sabotage, MC Down unlocking AOA).
        var cureCandidate = _support.EvaluateActFreelyAilmentCure(
            battle,
            config,
            teamCtx,
            livingAllies,
            livingEnemies,
            primaryEnemy,
            allowItems: config.EnableSupportItemsInAi);

        if (cureCandidate?.IsLifesaving == true)
            return cureCandidate.Decision;

        // 6. Immediate All-Out Attack trigger or kill shot avoids wasting a turn on setup.
        if (bestAttack != null && (bestAttack.TriggersAllOutAttack || bestAttack.KillsAny))
            return bestAttack.Decision;

        // 7. Reactive telegraph mitigation / Charge guard and SP recovery.
        var reactiveDefense = _defense.TryChooseReactiveTelegraphSupportOrChargeGuard(
            battle,
            config,
            threatInfo,
            livingEnemies,
            livingAllies,
            allowAllyWallSupport: true,
            allowGenericChargeGuard: true,
            bestOffense: bestAttack);
        if (reactiveDefense != null)
            return reactiveDefense;

        var spRecovery = _support.EvaluateSpRecoveryItem(
            battle,
            config,
            allowAllies: config.AllowAllySpItemSupport,
            livingEnemies,
            livingAllies,
            bestAttack);
        if (spRecovery != null)
            return spRecovery;

        // 8. Compare non-lifesaving carry ailment cure vs offensive setup buffs/debuffs vs ailments vs Charge/offense.
        EvaluatedSupportDecision? supportEval = null;
        EvaluatedAilment? ailmentEval = null;
        EvaluatedOffense? chargeEval = null;
        bool hasPrecomputedCharge = false;
        if (!battle.IsActorOnOneMore)
        {
            double directScore = bestAttack?.Score ?? 0.0;
            chargeEval = _offense.EvaluateChargeAction(
                battle, config, candidates, directScore, ignoreOverkillCap: false, livingEnemies: livingEnemies);
            hasPrecomputedCharge = true;
            bool wantsToCharge = chargeEval != null && chargeEval.Score > directScore;
            supportEval = _support.EvaluateSupportMaintenanceWithNetValue(
                battle,
                config,
                strictPriority: false,
                actorBestAttack: bestAttack,
                actorWantsToCharge: wantsToCharge,
                livingAllies: livingAllies,
                livingEnemies: livingEnemies,
                prebuiltTeamCtx: teamCtx,
                prebuiltPrimaryEnemy: primaryEnemy);

            ailmentEval = _ailments.EvaluateBestAilmentAction(
                battle, config, teamCtx, TacticMode.ActFreely, livingEnemies, bestAttack);
        }

        var offenseDecision = _offense.EvaluateOffenseWithFallbacks(
            battle,
            config,
            candidates,
            bestAttack,
            ignoreOverkillCap: false,
            hasPrecomputedCharge: hasPrecomputedCharge,
            precomputedCharge: chargeEval,
            livingEnemies: livingEnemies);
        bool selectedCharge = IsSelectedChargeDecision(offenseDecision);
        double chargeNet = selectedCharge
            ? Math.Max(0.0, offenseDecision.Score - teamCtx.ActorOpportunityCost)
            : 0.0;

        AiDecision? bestStrategic = null;
        double bestStrategicNet = chargeNet;

        if (!battle.IsActorOnOneMore && cureCandidate != null)
        {
            double cureNet = cureCandidate.NetAdvantageValue - teamCtx.ActorOpportunityCost;
            double chargeTurnValue = selectedCharge ? offenseDecision.Score : 0.0;
            if (cureNet > bestStrategicNet && cureCandidate.NetAdvantageValue >= chargeTurnValue)
            {
                bestStrategicNet = cureNet;
                bestStrategic = cureCandidate.Decision;
            }
        }

        if (supportEval != null && supportEval.TrueNetValue > 0.0 && supportEval.TrueNetValue > bestStrategicNet)
        {
            bestStrategicNet = supportEval.TrueNetValue;
            bestStrategic = supportEval.Decision;
        }

        if (ailmentEval != null && ailmentEval.MarginalValue > 0.0 && ailmentEval.MarginalValue > bestStrategicNet)
        {
            bestStrategicNet = ailmentEval.MarginalValue;
            bestStrategic = ailmentEval.Decision;
        }

        return bestStrategic ?? offenseDecision;
    }

    private static EvaluatedOffense? PickBetterOffense(EvaluatedOffense? bestSkill, EvaluatedOffense? bestItem)
    {
        if (bestSkill == null)
            return bestItem;
        if (bestItem == null)
            return bestSkill;
        if (bestSkill.FinishesBattle && bestItem.FinishesBattle)
            return bestSkill;
        return bestSkill.Score >= bestItem.Score ? bestSkill : bestItem;
    }

    private bool ShouldDeferEmergencyHealToFasterHealer(
        BattleSnapshot battle,
        Config config,
        List<CombatantSnapshot> livingAllies,
        double emergencyThreshold)
    {
        var actor = battle.Actor;
        if ((!actor.HasPowerCharge && !actor.HasMindCharge) || actor.HasLossOfControl)
            return false;

        if (battle.UpcomingRoundTurnOrder.Count == 0)
            return false;

        // Only consider deferring when someone is actually below the emergency threshold,
        // and never defer if Protagonist < 25% or any ally < 20% HP.
        bool anyEmergencyInjured = false;
        foreach (var ally in livingAllies)
        {
            if (ally.MaxHp <= 0)
                continue;

            double hpRatio = ally.HpRatio;
            if (hpRatio < emergencyThreshold)
                anyEmergencyInjured = true;
            if (ally.UnitOrEnemyId == 1 && hpRatio < 0.25)
                return false;
            if (hpRatio < 0.20)
                return false;
        }

        if (!anyEmergencyInjured)
            return false;

        int firstEnemyIdx = AiCombatHelpers.GetFirstUpcomingEnemyIndex(battle);
        foreach (var ally in livingAllies)
        {
            if (ReferenceEquals(ally, actor) || ally.SlotId == actor.SlotId)
                continue;
            if (ally.HasLossOfControl || ally.IsDown || ally.IsDizzy)
                continue;
            if (ally.Tactic != TacticMode.HealSupport)
                continue;

            int healerIdx = AiCombatHelpers.IndexInUpcomingTurnOrder(battle, ally);
            if (healerIdx < 0)
                continue;
            if (firstEnemyIdx >= 0 && healerIdx >= firstEnemyIdx)
                continue;

            if (HasUsableHealingAction(ally, config))
                return true;
        }

        return false;
    }

    private bool HasUsableHealingAction(CombatantSnapshot ally, Config config)
    {
        foreach (ushort skillId in ally.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var meta = _bridge.GetSkill(skillId);
            if (meta.IsHeal && _bridge.CanUseSkill(ally, skillId))
                return true;
        }

        if (!config.EnableSupportItemsInAi)
            return false;

        foreach (var item in _bridge.GetAvailableConsumables(ally))
        {
            if (item.Kind == BattleConsumableKind.Heal && AiCombatHelpers.IsConsumableAllowed(item, config))
                return true;
        }

        return false;
    }

    private bool IsSelectedChargeDecision(AiDecision decision)
    {
        if (decision.ActionKind != AiActionKind.Skill || decision.SkillId == 0)
            return false;

        var meta = _bridge.GetSkill(decision.SkillId);
        return meta.Element == ElementId.Support &&
               (meta.BuffMask & (SkillCatalog.BuffPowerCharge | SkillCatalog.BuffMindCharge)) != 0;
    }
}
