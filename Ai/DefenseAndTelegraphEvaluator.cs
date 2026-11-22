using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

/// <summary>
/// Analyzes P4G boss telegraphs, Mind/Power Charge states, and weakness-protection Guard / Wall / Makarakarn responses.
/// </summary>
internal sealed class DefenseAndTelegraphEvaluator
{
    private static readonly ElementId[] MagicElements =
    {
        ElementId.Fire,
        ElementId.Ice,
        ElementId.Elec,
        ElementId.Wind
    };

    private readonly IBattleEngineBridge _bridge;

    public DefenseAndTelegraphEvaluator(IBattleEngineBridge bridge)
    {
        _bridge = bridge;
    }

    public static TelegraphedThreatInfo AnalyzeTelegraphedThreats(BattleSnapshot battle, List<CombatantSnapshot> livingEnemies)
    {
        var info = new TelegraphedThreatInfo();

        bool anyAllyDown = battle.Allies.Any(a => !a.IsDead && a.IsDown);
        bool anyKanjiGuardAlive = livingEnemies.Any(e => e.UnitOrEnemyId is 272 or 273);

        foreach (var enemy in livingEnemies)
        {
            bool isBoss = AiCombatHelpers.IsLikelyBossOrMiniboss(enemy);
            if (enemy.HasMindCharge)
            {
                info.AnyEnemyMindCharged = true;
                if (isBoss)
                    info.AnyBossMindCharged = true;
            }
            if (enemy.HasPowerCharge)
            {
                info.AnyEnemyPowerCharged = true;
                if (isBoss)
                    info.AnyBossPowerCharged = true;
            }

            bool waitActive = !enemy.IsDown && !enemy.HasLossOfControl && enemy.LastActionType is 0 or 7;
            bool skillFresh = !enemy.IsDown && enemy.LastActionType is 0 or 2;

            // Only Kusumi-no-Okami (276) / Marie (277) uses wait message 209 ("Marie's ego is struggling for control!").
            if (waitActive && enemy.WaitMessageId == 209 && enemy.UnitOrEnemyId is 276 or 277)
                info.MustGuardUltimate = true;

            switch (enemy.UnitOrEnemyId)
            {
                case 257: // Shadow Chie (Bottomless Envy / Mazio)
                    if ((waitActive && enemy.WaitMessageId == 164) ||
                        (skillFresh && enemy.LastSkillId == 353) ||
                        battle.ActiveBattleBits.Contains(450) || battle.ActiveBattleBits.Contains(451))
                    {
                        info.TelegraphedElements.Add(ElementId.Ice);
                        info.TelegraphedElements.Add(ElementId.Elec);
                    }
                    break;

                case 258: // Shadow Yukiko ("passionate stare" -> Burn to Ashes)
                    if ((waitActive && enemy.WaitMessageId == 202) || battle.ActiveBattleBits.Contains(451))
                        info.TelegraphedElements.Add(ElementId.Fire);
                    break;

                case 259: // Shadow Kanji: AI_kanji_P4G_100 (guards alive or HP > 50%) only chains 358/359 -> Mazio/Zio when !anyAllyDown and !PowerCharge.
                {
                    bool inPhase100 = anyKanjiGuardAlive || enemy.HpRatio > 0.50;
                    if (skillFresh && enemy.LastSkillId is 358 or 359)
                    {
                        if (inPhase100 && !anyAllyDown && !enemy.HasPowerCharge)
                            info.TelegraphedElements.Add(ElementId.Elec);
                        else if (!inPhase100)
                            info.TelegraphedElements.Add(ElementId.Phys);
                    }
                    if (enemy.HasPowerCharge)
                        info.TelegraphedElements.Add(ElementId.Phys);
                    break;
                }

                case 261: // Shadow Teddie (Nihil Hand vs Mind Charge Mabufula)
                    if ((waitActive && enemy.WaitMessageId == 169) || battle.ActiveBattleBits.Contains(458))
                        info.MustGuardUltimate = true;
                    else if (enemy.HasMindCharge)
                        info.TelegraphedElements.Add(ElementId.Ice);
                    break;

                case 262: // Shadow Mitsuo (Whisper / Gigadyne / Elemental Wall -> matching -dyne)
                    if (waitActive && enemy.WaitMessageId == 181)
                        info.MustGuardUltimate = true;
                    if (enemy.HasRedWall)
                        info.TelegraphedElements.Add(ElementId.Fire);
                    if (enemy.HasBlueWall)
                        info.TelegraphedElements.Add(ElementId.Ice);
                    if (enemy.HasGreenWall)
                        info.TelegraphedElements.Add(ElementId.Wind);
                    if (enemy.HasWhiteWall)
                        info.TelegraphedElements.Add(ElementId.Elec);
                    break;

                case 263: // Shadow Naoto (Mute Ray bitflags 452..455)
                    if (battle.ActiveBattleBits.Contains(452))
                        info.TelegraphedElements.Add(ElementId.Fire);
                    if (battle.ActiveBattleBits.Contains(453))
                        info.TelegraphedElements.Add(ElementId.Ice);
                    if (battle.ActiveBattleBits.Contains(454))
                        info.TelegraphedElements.Add(ElementId.Elec);
                    if (battle.ActiveBattleBits.Contains(455))
                        info.TelegraphedElements.Add(ElementId.Wind);
                    break;

                case 264: // Kunino-sagiri (Quad Converge bitflags 453..456)
                    if (battle.ActiveBattleBits.Contains(453))
                        info.TelegraphedElements.Add(ElementId.Fire);
                    if (battle.ActiveBattleBits.Contains(454))
                        info.TelegraphedElements.Add(ElementId.Ice);
                    if (battle.ActiveBattleBits.Contains(455))
                        info.TelegraphedElements.Add(ElementId.Wind);
                    if (battle.ActiveBattleBits.Contains(456))
                        info.TelegraphedElements.Add(ElementId.Elec);
                    break;

                case 266: // Ameno-sagiri (beware Skill 384 = Bewildering Fog itself; only guard true wait telegraphs)
                    if (waitActive && enemy.WaitMessageId != 0)
                        info.MustGuardUltimate = true;
                    break;
            }
        }

        return info;
    }

    public AiDecision? TryChooseTelegraphDefense(
        BattleSnapshot battle,
        Config config,
        List<CombatantSnapshot> livingEnemies,
        List<CombatantSnapshot> livingAllies,
        EvaluatedOffense? bestOffense,
        bool allowAllyWallSupport,
        bool allowGenericChargeGuard,
        TelegraphedThreatInfo? precomputedThreats = null)
    {
        if (!config.EnableTelegraphGuard || livingEnemies.Count == 0)
            return null;

        var threats = precomputedThreats ?? AnalyzeTelegraphedThreats(battle, livingEnemies);
        var mandatory = TryChooseMandatoryTelegraphGuard(battle, config, threats, livingEnemies, bestOffense);
        if (mandatory != null)
            return mandatory;

        return TryChooseReactiveTelegraphSupportOrChargeGuard(
            battle, config, threats, livingEnemies, livingAllies, allowAllyWallSupport, allowGenericChargeGuard, bestOffense);
    }

    public AiDecision? TryChooseMandatoryTelegraphGuard(
        BattleSnapshot battle,
        Config config,
        TelegraphedThreatInfo threats,
        List<CombatantSnapshot> livingEnemies,
        EvaluatedOffense? bestOffense)
    {
        if (!config.EnableTelegraphGuard || livingEnemies.Count == 0)
            return null;

        // If the party member can finish the entire fight right now or already holds Shield of Justice, never waste the turn Guarding
        if (bestOffense?.FinishesBattle == true || battle.Actor.HasShieldOfJustice)
            return null;

        if (threats.MustGuardUltimate)
        {
            return new AiDecision
            {
                ActionKind = AiActionKind.Guard,
                SkillId = 0,
                Score = 4500.0,
                Reason = "Guard against telegraphed boss ultimate attack"
            };
        }

        var actor = battle.Actor;
        var sampleEnemy = livingEnemies[0];
        foreach (var elem in threats.TelegraphedElements)
        {
            var selfAff = _bridge.QueryEffectiveAffinity(sampleEnemy, actor, elem, respectAnalysis: false);
            if (selfAff.IsWeak)
            {
                return new AiDecision
                {
                    ActionKind = AiActionKind.Guard,
                    SkillId = 0,
                    Score = 4200.0,
                    Reason = $"Guard against telegraphed {elem} attack to protect weakness and deny 1 More"
                };
            }
        }

        return null;
    }

    /// <summary>
    /// Guards a wounded, charge-weak, or unable-to-safely-revive survivor BEFORE attempting a risky revive
    /// when a boss holds Power/Mind Charge or telegraphs an ultimate attack, avoiding wipe loops.
    /// </summary>
    public AiDecision? TryChooseChargedBossSurvivalGuardBeforeRevive(
        BattleSnapshot battle,
        Config config,
        TelegraphedThreatInfo threats,
        List<CombatantSnapshot> livingEnemies,
        EvaluatedOffense? bestOffense)
    {
        if (!config.EnableTelegraphGuard || livingEnemies.Count == 0 || (!threats.AnyBossCharged && !threats.MustGuardUltimate))
            return null;

        if (bestOffense?.FinishesBattle == true || battle.Actor.HasShieldOfJustice || !battle.Allies.Any(a => a.IsDead))
            return null;

        if (threats.MustGuardUltimate)
        {
            return new AiDecision
            {
                ActionKind = AiActionKind.Guard,
                SkillId = 0,
                Score = 4500.0,
                Reason = "Guard against telegraphed boss ultimate attack before attempting revive"
            };
        }

        var (hasFullRevive, hasPartialRevive) = GetUsableReviveCapability(battle, config);
        if (!hasFullRevive && !hasPartialRevive)
            return null;

        var sampleEnemy = livingEnemies[0];
        bool anyDeadSafeFromCharge = battle.Allies.Any(d =>
            d.IsDead && !IsCombatantWeakToBossCharge(_bridge, sampleEnemy, d, threats));
        if (!anyDeadSafeFromCharge)
            return null;

        var actor = battle.Actor;
        string chargeType = threats.AnyBossPowerCharged ? "Power-Charged" : "Mind-Charged";
        if (actor.HpRatio <= 0.85)
        {
            return new AiDecision
            {
                ActionKind = AiActionKind.Guard,
                SkillId = 0,
                Score = 4100.0,
                Reason = $"Guard against {chargeType} boss before attempting risky revive at {actor.HpRatio * 100:F0}% HP"
            };
        }

        // At > 85% HP, partial-HP revives are already suppressed by AnyBossCharged in EvaluateRevive;
        // only guard a charge-weak reviver who actually has a usable full-HP revive to attempt.
        if (!hasFullRevive)
            return null;

        if (IsCombatantWeakToBossCharge(_bridge, sampleEnemy, actor, threats))
        {
            return new AiDecision
            {
                ActionKind = AiActionKind.Guard,
                SkillId = 0,
                Score = 4150.0,
                Reason = $"Guard against {chargeType} boss to protect weakness before attempting revive"
            };
        }

        return null;
    }

    internal static bool IsCombatantWeakToBossCharge(
        IBattleEngineBridge bridge,
        CombatantSnapshot sampleEnemy,
        CombatantSnapshot target,
        TelegraphedThreatInfo threats)
    {
        foreach (var elem in threats.TelegraphedElements)
        {
            if (bridge.QueryEffectiveAffinity(sampleEnemy, target, elem, respectAnalysis: false).IsWeak)
                return true;
        }

        if (threats.AnyBossPowerCharged &&
            bridge.QueryEffectiveAffinity(sampleEnemy, target, ElementId.Phys, respectAnalysis: false).IsWeak)
        {
            return true;
        }

        if (threats.AnyBossMindCharged && threats.TelegraphedElements.Count == 0)
        {
            foreach (var elem in MagicElements)
            {
                if (bridge.QueryEffectiveAffinity(sampleEnemy, target, elem, respectAnalysis: false).IsWeak)
                    return true;
            }
        }

        return false;
    }

    private (bool HasFullRevive, bool HasPartialRevive) GetUsableReviveCapability(BattleSnapshot battle, Config config)
    {
        var actor = battle.Actor;
        if (actor.Tactic == TacticMode.FullAssault)
            return (false, false);

        int maxSkillSp = actor.Tactic == TacticMode.ConserveSp ? 8 : int.MaxValue;
        bool commonItemsOnly = actor.Tactic == TacticMode.ConserveSp;
        bool hasFull = false;
        bool hasPartial = false;

        foreach (ushort skillId in actor.EquippedSkills)
        {
            if (skillId == 0)
                continue;
            var skill = _bridge.GetSkill(skillId);
            if (!skill.IsRevive || !_bridge.CanUseSkill(actor, skillId))
                continue;
            if (skill.CostType == 2 && _bridge.GetEffectiveCost(actor, skillId) > maxSkillSp)
                continue;

            if (skill.Power >= 100 || skill.IsFullHeal)
                hasFull = true;
            else
                hasPartial = true;

            if (hasFull && hasPartial)
                return (true, true);
        }

        if (config.EnableSupportItemsInAi)
        {
            foreach (var item in _bridge.GetAvailableConsumables(actor))
            {
                if (item.Kind != BattleConsumableKind.Revive)
                    continue;
                if (commonItemsOnly && item.Rarity != ConsumableRarityTier.Common)
                    continue;
                if (!AiCombatHelpers.IsConsumableAllowed(item, config))
                    continue;

                if (item.Skill.Power >= 100 || item.Skill.IsFullHeal || item.Rarity == ConsumableRarityTier.Rare)
                    hasFull = true;
                else
                    hasPartial = true;

                if (hasFull && hasPartial)
                    return (true, true);
            }
        }

        return (hasFull, hasPartial);
    }

    public AiDecision? TryChooseReactiveTelegraphSupportOrChargeGuard(
        BattleSnapshot battle,
        Config config,
        TelegraphedThreatInfo threats,
        List<CombatantSnapshot> livingEnemies,
        List<CombatantSnapshot> livingAllies,
        bool allowAllyWallSupport,
        bool allowGenericChargeGuard,
        EvaluatedOffense? bestOffense = null)
    {
        if (!config.EnableTelegraphGuard || livingEnemies.Count == 0)
            return null;

        var actor = battle.Actor;
        var sampleEnemy = livingEnemies[0];

        if (allowAllyWallSupport && threats.TelegraphedElements.Count > 0)
        {
            foreach (var elem in threats.TelegraphedElements)
            {
                uint wallMask = SkillCatalog.GetWallMaskForElement(elem);
                if (wallMask == 0)
                    continue;

                var weakAlly = livingAllies
                    .Where(a => !ReferenceEquals(a, actor))
                    .OrderBy(a => a.UnitOrEnemyId == 1 ? 0 : 1)
                    .ThenBy(a => a.HpRatio)
                    .FirstOrDefault(a => _bridge.QueryEffectiveAffinity(sampleEnemy, a, elem, respectAnalysis: false).IsWeak);

                if (weakAlly == null)
                    continue;

                var wallSkill = SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, wallMask);
                if (wallSkill != null)
                {
                    return SupportAndRecoveryEvaluator.BuildSupportDecision(wallSkill, weakAlly, 1100.0,
                        $"{SkillCatalog.GetSkillName(wallSkill.SkillId)} -> protect {weakAlly.DisplayName} from telegraphed {elem} attack");
                }

                var makarakarn = SupportAndRecoveryEvaluator.FindUsableSupport(_bridge, actor, SkillCatalog.BuffMakarakarn);
                if (makarakarn != null)
                {
                    return SupportAndRecoveryEvaluator.BuildSupportDecision(makarakarn, weakAlly, 1080.0,
                        $"{SkillCatalog.GetSkillName(makarakarn.SkillId)} -> protect {weakAlly.DisplayName} from telegraphed {elem} attack");
                }
            }
        }

        // Never abandon an immediate kill or All-Out Attack trigger (or waste Shield of Justice) for generic reactive Guarding
        if (allowGenericChargeGuard && !actor.HasShieldOfJustice && bestOffense?.FinishesBattle != true &&
            bestOffense?.TriggersAllOutAttack != true && bestOffense?.KillsAny != true)
        {
            double weakGuardHpCap = actor.Tactic == TacticMode.HealSupport ? 0.65 : 0.50;
            if (threats.AnyEnemyMindCharged && actor.HpRatio <= weakGuardHpCap)
            {
                foreach (var elem in MagicElements)
                {
                    if (_bridge.QueryEffectiveAffinity(sampleEnemy, actor, elem, respectAnalysis: false).IsWeak)
                    {
                        return new AiDecision
                        {
                            ActionKind = AiActionKind.Guard,
                            SkillId = 0,
                            Score = 950.0,
                            Reason = $"Guard against Mind-Charged enemy at {actor.HpRatio * 100:F0}% HP to protect {elem} weakness"
                        };
                    }
                }
            }

            double chargeGuardHpCap = threats.AnyBossCharged
                ? (actor.Tactic == TacticMode.HealSupport ? 0.75 : 0.70)
                : 0.45;
            if ((threats.AnyEnemyMindCharged || threats.AnyEnemyPowerCharged) && actor.HpRatio <= chargeGuardHpCap)
            {
                string chargeType = threats.AnyEnemyPowerCharged ? "Power-Charged" : "Mind-Charged";
                return new AiDecision
                {
                    ActionKind = AiActionKind.Guard,
                    SkillId = 0,
                    Score = 920.0,
                    Reason = $"Guard against {chargeType} {(threats.AnyBossCharged ? "boss" : "enemy")} at {actor.HpRatio * 100:F0}% HP"
                };
            }
        }

        return null;
    }
}
