namespace p4g64.smartPartyAI.Core;

/// <summary>
/// Elemental and skill category indices from P4G battle/SKILL.TBL section 0.
/// </summary>
public enum ElementId : byte
{
    Phys = 0,
    Fire = 1,
    Ice = 2,
    Elec = 3,
    Wind = 4,
    Almighty = 5,
    Light = 6,
    Dark = 7,
    SpecialPoison = 8,
    SpecialExhaust = 9,
    SpecialRage = 10,
    SpecialSilence = 11,
    SpecialFear = 12,
    SpecialConfuse = 13,
    SpecialEnervate = 14,
    SpecialBrainwash = 15,
    Recovery = 16,
    Support = 17,
    PassSpecial = 18,
    Passive = 255
}

/// <summary>
/// Party member tactic presets stored at Combatant + 0x10.
/// </summary>
public enum TacticMode : byte
{
    ActFreely = 0,
    FullAssault = 1,
    ConserveSp = 2,
    HealSupport = 3,
    DirectCommands = 4
}

/// <summary>
/// High-level action types mapped to BattleUnit + 0xAC and SetAiAction codes.
/// </summary>
public enum AiActionKind
{
    NormalAttack,
    Skill,
    Item,
    Guard,
    Wait
}

/// <summary>
/// Parsed SKILL.TBL Section 0 + Section 1 data for a single skill (or Normal Attack ID 0).
/// </summary>
public sealed class SkillMetadata
{
    public ushort SkillId { get; init; }
    public string Name { get; init; } = string.Empty;
    public ElementId Element { get; init; }
    public byte SkillType { get; init; }
    public byte StatType { get; init; }
    public byte CostType { get; init; } // 0=Free, 1=%MaxHP, 2=FlatSP
    public ushort BaseCost { get; init; }
    public byte Scope { get; init; } // 0=Single, 1=All, 2=Field
    public byte TargetSide { get; init; } // 1=Allies, 2=Enemies, 3=Any
    public byte TargetCond { get; init; } // 0=Living, 1=SelfOnly, 2=NormalAttack/Special, 4=KO/DeadOnly
    public byte Accuracy { get; init; } = 100;
    public byte MinHits { get; init; } = 1;
    public byte MaxHits { get; init; } = 1;
    public byte EffectType { get; init; } // 1=Damage, 2=Heal(Power), 8=Light/Dark+Dmg, 11=FullHeal/Revive
    public ushort Power { get; init; }
    public byte Mode { get; init; } // e.g., 4=LifeDrain, 5=SpiritDrain
    public uint AilmentMask { get; init; }
    public uint BuffMask { get; init; }

    public double ExpectedHits => 0.5 * (Math.Max((byte)1, MinHits) + Math.Max(Math.Max((byte)1, MinHits), MaxHits));
    public bool IsAoE => Scope == 1;
    public bool IsSelfOnly => (TargetCond & 1) != 0;
    public bool TargetsDeadOnly => (TargetCond & 4) != 0;
    public bool TargetsDownOnly => (TargetCond & 8) != 0;
    public bool TargetsEnemies => TargetSide == 2 || TargetSide == 3;
    public bool TargetsAllies => TargetSide == 1 || TargetSide == 3;
    public bool IsOffensiveElement => (byte)Element <= (byte)ElementId.Dark;
    // Note: Divine Judgement / Demonic Decree (EffectType=8, Power=50) carry 0x80000 alongside HP-halving; exclude them from pure Hama/Mudo instant-kills.
    public bool IsInstantKill => (Element == ElementId.Light || Element == ElementId.Dark) && EffectType == 0 && Power <= 1 && (AilmentMask & 0x80000) != 0;
    public bool IsPercentHpDamage => IsOffensiveElement && EffectType == 8 && Power > 0;
    public bool IsDamaging => SkillId == 0 || (IsOffensiveElement && Power > 0 && (EffectType == 1 || EffectType == 8));
    // Exclude Kamui Miracle (Sk245 / TargetCond == 2) because its chaotic RNG can heal enemies or inflict ailments on the party.
    public bool IsHeal => Element == ElementId.Recovery && !TargetsDeadOnly && !TargetsDownOnly && TargetCond != 2 && SkillId != 245 && (EffectType == 2 || EffectType == 11) && Power > 0;
    public bool IsFullHeal => Element == ElementId.Recovery && !TargetsDeadOnly && !TargetsDownOnly && TargetCond != 2 && SkillId != 245 && EffectType == 11 && Power >= 100;
    public bool IsRevive => Element == ElementId.Recovery && TargetsDeadOnly && (AilmentMask & 0x80000) != 0;
    public bool IsAilmentCure => Element == ElementId.Recovery && !TargetsDeadOnly && (TargetsDownOnly ? (AilmentMask & 0x100001) != 0 : (AilmentMask & 0xFF) != 0);
    public bool IsSpDrain => Mode == 5 || SkillId is 48 or 49;
}

/// <summary>
/// Effective affinity flags and multiplier against a specific target and element.
/// </summary>
public readonly struct AffinityInfo
{
    public bool IsWeak { get; init; }
    public bool IsResist { get; init; }
    public bool IsNull { get; init; }
    public bool IsRepel { get; init; }
    public bool IsDrain { get; init; }
    public bool IsRevealed { get; init; }
    public bool BlockedByShield { get; init; } // Shield of Justice, Unbreakable Shield, Tetrakarn, Makarakarn
    public int MultiplierPercent { get; init; }

    public bool CanDealDamage => !IsNull && !IsRepel && !IsDrain && !BlockedByShield && MultiplierPercent > 0;
    public bool IsBadHit => IsRepel || IsDrain || BlockedByShield || IsNull;
}

/// <summary>
/// Live snapshot of a battle unit (party member or enemy).
/// </summary>
public sealed class CombatantSnapshot
{
    public nint UnitPtr { get; init; }
    public nint CombatantPtr { get; init; }
    public int SlotId { get; init; }
    public bool IsPartyMember { get; init; }
    public bool IsEnemy { get; init; }
    public ushort UnitOrEnemyId { get; init; }
    public ushort PersonaOrEnemyId { get; init; }
    public string DisplayName { get; init; } = string.Empty;

    public TacticMode Tactic { get; init; }
    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }
    public int CurrentSp { get; init; }
    public int MaxSp { get; init; }
    public double HpRatio => MaxHp > 0 ? Math.Clamp((double)CurrentHp / MaxHp, 0.0, 1.0) : 0.0;
    public double SpRatio => MaxSp > 0 ? Math.Clamp((double)CurrentSp / MaxSp, 0.0, 1.0) : 0.0;

    public uint StateFlags { get; init; }   // Combatant + 0x0C
    public uint ChargeFlags { get; init; }  // Combatant + 0x14
    public ushort BaseFlags { get; init; }  // Combatant + 0x00
    public ushort ShieldFlags { get; init; } // Combatant + 0x16

    public bool IsDead => (StateFlags & 0x80000) != 0 || CurrentHp <= 0;
    public bool IsDown => (StateFlags & 0x100000) != 0;
    public bool IsDizzy => (StateFlags & 0x40000) != 0;
    public bool IsStanding => !IsDead && !IsDown && !IsDizzy;
    public bool IsGuarding => (BaseFlags & 0x200) != 0;
    public bool HasUnbreakableShield => (BaseFlags & 0x100) != 0;
    public bool HasShieldOfJustice => (ShieldFlags & 0x1) != 0;
    public bool HasTetrakarn => (ChargeFlags & 0x20000) != 0;
    public bool HasMakarakarn => (ChargeFlags & 0x40000) != 0;
    public bool HasPowerCharge => (ChargeFlags & 0x80000) != 0;
    public bool HasMindCharge => (ChargeFlags & 0x100000) != 0;
    public bool HasSilenced => (StateFlags & 0x20) != 0;
    public bool HasCuringAilment => (StateFlags & 0x1FF) != 0; // Poison..Brainwash/Exhaust (0x01..0x100)
    public bool HasCriticalCuringAilment => (StateFlags & 0x1E4) != 0; // Rage(0x04), Exhaust(0x20), Fear(0x40), Confuse(0x80), Charm(0x100)
    public bool NeedsRecoveryFromDownOrDizzy => !IsDead && (IsDown || IsDizzy);

    // Buff/Debuff stages (positive = buffed, negative = debuffed)
    public int AtkStage { get; init; }
    public int MagStage { get; init; }
    public int DefStage { get; init; }
    public int HitEvaStage { get; init; }

    // Elemental Walls (+0x22/+0x23 > 0) & Breaks (+0x20/+0x21 > 0)
    public bool HasRedWall { get; init; }
    public bool HasBlueWall { get; init; }
    public bool HasGreenWall { get; init; }
    public bool HasWhiteWall { get; init; }
    public bool HasFireBreak { get; init; }
    public bool HasIceBreak { get; init; }
    public bool HasWindBreak { get; init; }
    public bool HasElecBreak { get; init; }

    public ushort[] EquippedSkills { get; init; } = Array.Empty<ushort>();

    // Last executed / pending telegraph state from BattleUnit (+0xBC, +0xBE, +0x188)
    public ushort LastActionType { get; init; }
    public ushort LastSkillId { get; init; }
    public ushort WaitMessageId { get; init; }

    public int PositiveBuffCount =>
        (AtkStage > 0 ? 1 : 0) + (MagStage > 0 ? 1 : 0) + (DefStage > 0 ? 1 : 0) + (HitEvaStage > 0 ? 1 : 0);

    public int NegativeDebuffCount =>
        (AtkStage < 0 ? 1 : 0) + (MagStage < 0 ? 1 : 0) + (DefStage < 0 ? 1 : 0) + (HitEvaStage < 0 ? 1 : 0);

    public bool HasWallForElement(ElementId elem) => elem switch
    {
        ElementId.Fire => HasRedWall,
        ElementId.Ice => HasBlueWall,
        ElementId.Wind => HasGreenWall,
        ElementId.Elec => HasWhiteWall,
        _ => false
    };

    public bool HasBreakForElement(ElementId elem) => elem switch
    {
        ElementId.Fire => HasFireBreak,
        ElementId.Ice => HasIceBreak,
        ElementId.Wind => HasWindBreak,
        ElementId.Elec => HasElecBreak,
        _ => false
    };
}

/// <summary>
/// Full battle snapshot for AI evaluation.
/// </summary>
public sealed class BattleSnapshot
{
    public CombatantSnapshot Actor { get; init; } = null!;
    public bool IsActorOnOneMore { get; init; }
    public List<CombatantSnapshot> Allies { get; init; } = new();
    public List<CombatantSnapshot> Enemies { get; init; } = new();
    public HashSet<int> ActiveBattleBits { get; init; } = new();
}

/// <summary>
/// Final chosen decision returned by SmartPartyAiEngine.
/// </summary>
public sealed class AiDecision
{
    public AiActionKind ActionKind { get; init; }
    public ushort SkillId { get; init; }
    public ushort ItemId { get; init; }
    public CombatantSnapshot? Target { get; init; }
    public IReadOnlyList<CombatantSnapshot>? Targets { get; init; }
    public double Score { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class SpRecoveryItemInfo
{
    public ushort ItemId { get; init; }
    public string Name { get; init; } = string.Empty;
    public ushort SkillId { get; init; }
    public int Quantity { get; init; }
    public bool IsPartyWide { get; init; }
    public int EstimatedSpGain { get; init; }
    public int EstimatedHpGain { get; init; }
    public bool IsFullSp { get; init; }
    public bool IsFullHp { get; init; }
}

/// <summary>
/// Abstraction between SmartPartyAiEngine and either native P4G memory or unit-test mocks.
/// </summary>
public interface IBattleEngineBridge
{
    SkillMetadata GetSkill(ushort skillId);
    ElementId GetAttackElement(CombatantSnapshot actor);
    int GetEffectiveCost(CombatantSnapshot actor, ushort skillId);
    bool CanUseSkill(CombatantSnapshot actor, ushort skillId);
    AffinityInfo QueryEffectiveAffinity(CombatantSnapshot actor, CombatantSnapshot target, ElementId element, bool respectAnalysis);
    double EstimateSingleHitDamage(ushort skillId, CombatantSnapshot attacker, CombatantSnapshot target, AffinityInfo effectiveAffinity, bool assumeChargedOverride, bool assumeUnchargedOverride, bool respectAnalysis);
    bool IsKnownWeakness(CombatantSnapshot target, ElementId element, AffinityInfo aff);
    IReadOnlyList<SpRecoveryItemInfo> GetAvailableSpRecoveryItems(CombatantSnapshot beneficiary);
}
