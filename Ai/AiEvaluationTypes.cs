using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

internal sealed record OffenseScoringProfile(
    int MaxSpCost = int.MaxValue,
    bool PreferLowCostTiebreak = true,
    double SpCostWeight = 0.35,
    double HpCostWeight = 0.02,
    bool IgnoreOverkillCap = false,
    bool IgnoreResourceCosts = false,
    bool AllowDamageItemOutrank = false);

internal sealed class EvaluatedOffense
{
    public AiDecision Decision { get; init; } = null!;
    public double DirectDamage { get; init; }
    public double TwoStepTotalDamage { get; init; }
    public double Score { get; init; }
    public bool TriggersOneMore { get; init; }
    public bool TriggersAllOutAttack { get; init; }
    public bool KillsAny { get; init; }
    public bool FinishesBattle { get; init; }
}

internal sealed class TelegraphedThreatInfo
{
    public bool MustGuardUltimate { get; set; }
    public bool AnyEnemyMindCharged { get; set; }
    public bool AnyEnemyPowerCharged { get; set; }
    public bool AnyBossMindCharged { get; set; }
    public bool AnyBossPowerCharged { get; set; }
    public bool AnyBossCharged => AnyBossMindCharged || AnyBossPowerCharged;
    public HashSet<ElementId> TelegraphedElements { get; } = new();
}

internal static class AiCombatHelpers
{
    /// <summary>
    /// Shared safety gate for AoE evaluations: never hit into Repel, Drain, or elemental/physical Karn reflection.
    /// </summary>
    public static bool IsUnsafeReflectOrDrain(CombatantSnapshot target, ElementId element, AffinityInfo aff) =>
        aff.IsRepel ||
        aff.IsDrain ||
        (aff.BlockedByShield &&
         ((element == ElementId.Phys && target.HasTetrakarn) ||
          (element != ElementId.Phys && element != ElementId.Almighty && (byte)element <= 7 && target.HasMakarakarn)));

    /// <summary>
    /// Identifies bosses and minibosses that are immune to Hama/Mudo instant death in P4G.
    /// </summary>
    public static bool IsLikelyBossOrMiniboss(CombatantSnapshot enemy) =>
        enemy.MaxHp >= 500 ||
        SkillCatalog.IsKnownBossUnitId(enemy.UnitOrEnemyId);

    /// <summary>
    /// Counts party members eligible to initiate/join an All-Out Attack matching P4G.exe (0x140047d00).
    /// Returns 0 if the Protagonist is present in the battle snapshot and incapacitated/blocked from AOA.
    /// </summary>
    public static int CountAlliesEligibleForAoa(BattleSnapshot battle)
    {
        int count = 0;
        var allies = battle.Allies;
        for (int i = 0; i < allies.Count; i++)
        {
            var ally = allies[i];
            bool eligible = ally.CanParticipateInAllOutAttack;
            if (ally.UnitOrEnemyId == 1 && !eligible)
                return 0;
            if (eligible)
                count++;
        }

        return count;
    }

    public static List<CombatantSnapshot> CollectLiving(List<CombatantSnapshot> combatants)
    {
        var result = new List<CombatantSnapshot>(combatants.Count);
        for (int i = 0; i < combatants.Count; i++)
        {
            var c = combatants[i];
            if (!c.IsDead)
                result.Add(c);
        }
        return result;
    }

    public static CombatantSnapshot SelectMaxCurrentHpEnemy(List<CombatantSnapshot> livingEnemies)
    {
        CombatantSnapshot best = livingEnemies[0];
        for (int i = 1; i < livingEnemies.Count; i++)
        {
            if (livingEnemies[i].CurrentHp > best.CurrentHp)
                best = livingEnemies[i];
        }
        return best;
    }

    public static bool IsConsumableAllowed(BattleConsumableInfo item, Config config)
    {
        int reserve = Math.Clamp(config.MinConsumableReserveCount, 0, 99);
        if (item.Quantity <= reserve)
            return false;
        if (item.Rarity == ConsumableRarityTier.Rare && !config.AllowRareConsumables)
            return false;
        return true;
    }

    public static bool HasUpcomingTurnInRound(BattleSnapshot battle, CombatantSnapshot unit)
    {
        for (int i = 0; i < battle.UpcomingRoundTurnOrder.Count; i++)
        {
            if (ReferenceEquals(battle.UpcomingRoundTurnOrder[i], unit))
                return true;
        }
        return false;
    }

    public static int IndexInUpcomingTurnOrder(BattleSnapshot battle, CombatantSnapshot unit)
    {
        for (int i = 0; i < battle.UpcomingRoundTurnOrder.Count; i++)
        {
            if (ReferenceEquals(battle.UpcomingRoundTurnOrder[i], unit))
                return i;
        }
        return -1;
    }

    public static int IndexInRawTurnOrder(BattleSnapshot battle, CombatantSnapshot unit)
    {
        int actorIdx = -1;
        int unitIdx = -1;
        for (int i = 0; i < battle.RawRoundTurnOrder.Count; i++)
        {
            var entry = battle.RawRoundTurnOrder[i];
            if (actorIdx < 0 && ReferenceEquals(entry, battle.Actor))
                actorIdx = i;
            if (unitIdx < 0 && ReferenceEquals(entry, unit))
                unitIdx = i;
        }

        if (unitIdx < 0)
            return -1;
        if (actorIdx >= 0)
            return unitIdx > actorIdx ? unitIdx - actorIdx - 1 : -1;
        return unitIdx;
    }

    public static int GetFirstUpcomingEnemyIndex(BattleSnapshot battle)
    {
        for (int i = 0; i < battle.UpcomingRoundTurnOrder.Count; i++)
        {
            if (battle.UpcomingRoundTurnOrder[i].IsEnemy && !battle.UpcomingRoundTurnOrder[i].IsDead)
                return i;
        }
        return int.MaxValue;
    }

    /// <summary>
    /// Avoids redundant re-buffing when a buff/debuff still has >= 2 turns remaining, while allowing
    /// proactive refreshes at 1 turn remaining in longer battles.
    /// </summary>
    public static bool NeedsStatRefresh(int stage, int effectiveTurnsRemaining, double estimatedRoundsRemaining, bool isDebuff) =>
        (isDebuff ? stage >= 0 : stage <= 0) ||
        (effectiveTurnsRemaining <= 1 && estimatedRoundsRemaining >= 2.2);
}
