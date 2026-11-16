using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

/// <summary>
/// Analyzes P4G boss telegraphs, Mind/Power Charge states, and weakness-protection Guard / Wall / Makarakarn responses.
/// </summary>
internal sealed class DefenseAndTelegraphEvaluator
{
    private readonly IBattleEngineBridge _bridge;

    public DefenseAndTelegraphEvaluator(IBattleEngineBridge bridge)
    {
        _bridge = bridge;
    }

    public static TelegraphedThreatInfo AnalyzeTelegraphedThreats(BattleSnapshot battle, List<CombatantSnapshot> livingEnemies)
    {
        var info = new TelegraphedThreatInfo();

        if (battle.ActiveBattleBits.Contains(466))
            info.MustGuardUltimate = true;

        foreach (var enemy in livingEnemies)
        {
            if (enemy.HasMindCharge)
                info.AnyEnemyMindCharged = true;
            if (enemy.HasPowerCharge)
                info.AnyEnemyPowerCharged = true;

            if (enemy.WaitMessageId == 209)
                info.MustGuardUltimate = true;

            switch (enemy.UnitOrEnemyId)
            {
                case 266: // Shadow Chie (Bottomless Envy / Mazio)
                    if (enemy.WaitMessageId == 164 || enemy.LastSkillId == 353 ||
                        battle.ActiveBattleBits.Contains(450) || battle.ActiveBattleBits.Contains(451))
                    {
                        info.TelegraphedElements.Add(ElementId.Ice);
                        info.TelegraphedElements.Add(ElementId.Elec);
                    }
                    break;

                case 268: // Shadow Yukiko ("passionate stare" -> Burn to Ashes)
                    if (enemy.WaitMessageId == 202 || battle.ActiveBattleBits.Contains(451))
                        info.TelegraphedElements.Add(ElementId.Fire);
                    break;

                case 271: // Shadow Kanji (Roar of Wrath / Fanatic -> Mazio / Kill Rush)
                    if (enemy.LastSkillId is 358 or 359)
                    {
                        info.TelegraphedElements.Add(ElementId.Elec);
                        info.TelegraphedElements.Add(ElementId.Phys);
                    }
                    if (enemy.HasPowerCharge)
                        info.TelegraphedElements.Add(ElementId.Phys);
                    break;

                case 276: // Shadow Teddie (Nihil Hand vs Mind Charge Mabufula)
                    if (enemy.WaitMessageId == 169 || battle.ActiveBattleBits.Contains(458))
                        info.MustGuardUltimate = true;
                    else if (enemy.HasMindCharge)
                        info.TelegraphedElements.Add(ElementId.Ice);
                    break;

                case 279: // Shadow Mitsuo (Whisper / Gigadyne / Elemental Wall -> matching -dyne)
                    if (enemy.WaitMessageId == 181)
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

                case 282: // Shadow Naoto (Mute Ray bitflags 452..455)
                    if (battle.ActiveBattleBits.Contains(452))
                        info.TelegraphedElements.Add(ElementId.Fire);
                    if (battle.ActiveBattleBits.Contains(453))
                        info.TelegraphedElements.Add(ElementId.Ice);
                    if (battle.ActiveBattleBits.Contains(454))
                        info.TelegraphedElements.Add(ElementId.Elec);
                    if (battle.ActiveBattleBits.Contains(455))
                        info.TelegraphedElements.Add(ElementId.Wind);
                    break;

                case 284: // Kunino-sagiri (Quad Converge bitflags 453..456)
                    if (battle.ActiveBattleBits.Contains(453))
                        info.TelegraphedElements.Add(ElementId.Fire);
                    if (battle.ActiveBattleBits.Contains(454))
                        info.TelegraphedElements.Add(ElementId.Ice);
                    if (battle.ActiveBattleBits.Contains(455))
                        info.TelegraphedElements.Add(ElementId.Wind);
                    if (battle.ActiveBattleBits.Contains(456))
                        info.TelegraphedElements.Add(ElementId.Elec);
                    break;

                case 286: // Ameno-sagiri (Quake / Bewildering Fog -> Nebula Oculus)
                    if (enemy.LastSkillId == 384 || battle.ActiveBattleBits.Contains(455))
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
        bool allowGenericChargeGuard)
    {
        if (!config.EnableTelegraphGuard || livingEnemies.Count == 0)
            return null;

        var threats = AnalyzeTelegraphedThreats(battle, livingEnemies);
        var mandatory = TryChooseMandatoryTelegraphGuard(battle, config, threats, livingEnemies, bestOffense);
        if (mandatory != null)
            return mandatory;

        return TryChooseReactiveTelegraphSupportOrChargeGuard(
            battle, config, threats, livingEnemies, livingAllies, allowAllyWallSupport, allowGenericChargeGuard);
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

        // If the party member can finish the entire fight right now, never waste the kill turn Guarding
        if (bestOffense?.FinishesBattle == true)
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

    public AiDecision? TryChooseReactiveTelegraphSupportOrChargeGuard(
        BattleSnapshot battle,
        Config config,
        TelegraphedThreatInfo threats,
        List<CombatantSnapshot> livingEnemies,
        List<CombatantSnapshot> livingAllies,
        bool allowAllyWallSupport,
        bool allowGenericChargeGuard)
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

        if (allowGenericChargeGuard)
        {
            if (threats.AnyEnemyMindCharged)
            {
                ElementId[] magElements = { ElementId.Fire, ElementId.Ice, ElementId.Elec, ElementId.Wind };
                foreach (var elem in magElements)
                {
                    if (_bridge.QueryEffectiveAffinity(sampleEnemy, actor, elem, respectAnalysis: false).IsWeak)
                    {
                        return new AiDecision
                        {
                            ActionKind = AiActionKind.Guard,
                            SkillId = 0,
                            Score = 950.0,
                            Reason = $"Guard against Mind-Charged enemy to protect {elem} weakness"
                        };
                    }
                }

                if (actor.HpRatio <= 0.55)
                {
                    return new AiDecision
                    {
                        ActionKind = AiActionKind.Guard,
                        SkillId = 0,
                        Score = 920.0,
                        Reason = "Guard against Mind-Charged enemy at low HP"
                    };
                }
            }

            if (threats.AnyEnemyPowerCharged && actor.HpRatio <= 0.55)
            {
                return new AiDecision
                {
                    ActionKind = AiActionKind.Guard,
                    SkillId = 0,
                    Score = 920.0,
                    Reason = "Guard against Power-Charged enemy at low HP"
                };
            }
        }

        return null;
    }
}
