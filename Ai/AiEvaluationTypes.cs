using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Ai;

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
        enemy.HasUnbreakableShield ||
        enemy.MaxHp >= 500 ||
        SkillCatalog.IsKnownBossUnitId(enemy.UnitOrEnemyId);
}
