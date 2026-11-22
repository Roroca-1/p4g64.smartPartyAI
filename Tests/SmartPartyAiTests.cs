using p4g64.smartPartyAI.Ai;
using p4g64.smartPartyAI.Core;
using Xunit;

namespace p4g64.smartPartyAI.Tests;

internal sealed class MockBattleBridge : IBattleEngineBridge
{
    public Dictionary<ushort, SkillMetadata> Skills { get; } = new();
    public Dictionary<(CombatantSnapshot target, ElementId elem), AffinityInfo> Affinities { get; } = new();
    public Dictionary<CombatantSnapshot, AffinityInfo> AilmentAffinities { get; } = new();
    public Dictionary<(ushort skillId, CombatantSnapshot target), double> SingleHitDamages { get; } = new();
    public Dictionary<(CombatantSnapshot attacker, ushort skillId, CombatantSnapshot target), double> ActorSingleHitDamages { get; } = new();
    public double OffensiveBuffMultiplier { get; set; } = 1.40;
    public double DefenseDebuffMultiplier { get; set; } = 1.40;
    public bool IncludesAttackerStatusMultipliersInDamage { get; set; } = false;

    public double GetOffensiveBuffMultiplier() => OffensiveBuffMultiplier;
    public double GetDefenseDebuffMultiplier() => DefenseDebuffMultiplier;

    public SkillMetadata GetSkill(ushort skillId) =>
        Skills.TryGetValue(skillId, out var s)
            ? s
            : new SkillMetadata { SkillId = skillId, Name = SkillCatalog.GetSkillName(skillId), Element = ElementId.Passive };

    public int GetEffectiveCost(CombatantSnapshot actor, ushort skillId) => GetSkill(skillId).BaseCost;

    public bool CanUseSkill(CombatantSnapshot actor, ushort skillId)
    {
        if (actor.IsDead) return false;
        if (actor.HasSilenced) return skillId == 0;
        if (skillId == 0) return true;
        var s = GetSkill(skillId);
        return s.CostType switch
        {
            2 => actor.CurrentSp >= s.BaseCost,
            1 => actor.CurrentHp > (int)Math.Ceiling(actor.MaxHp * (s.BaseCost / 100.0)),
            _ => true
        };
    }

    public AffinityInfo QueryEffectiveAffinity(CombatantSnapshot actor, CombatantSnapshot target, ElementId element, bool respectAnalysis)
    {
        bool blockedByKarn =
            (element == ElementId.Phys && target.HasTetrakarn) ||
            (element != ElementId.Phys && element != ElementId.Almighty && (byte)element <= 7 && target.HasMakarakarn);
        bool blockedByShield = blockedByKarn || target.HasShieldOfJustice;
        bool wall = target.HasWallForElement(element);

        if (!Affinities.TryGetValue((target, element), out var raw))
        {
            raw = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        }

        if (respectAnalysis && !raw.IsRevealed && target.IsEnemy)
        {
            return new AffinityInfo
            {
                IsWeak = false,
                IsResist = wall || target.IsGuarding,
                IsNull = false,
                IsRepel = false,
                IsDrain = false,
                IsRevealed = false,
                BlockedByShield = blockedByShield,
                MultiplierPercent = blockedByShield ? 0 : (wall || target.IsGuarding ? 50 : 100)
            };
        }

        bool isWeak = raw.IsWeak && !wall && !target.IsGuarding && !blockedByShield;
        bool isResist = raw.IsResist || (wall && raw.CanDealDamage);
        int mult = blockedByShield || raw.IsNull || raw.IsRepel || raw.IsDrain
            ? 0
            : (wall ? 50 : (isWeak ? Math.Max(125, raw.MultiplierPercent) : raw.MultiplierPercent));

        return new AffinityInfo
        {
            IsWeak = isWeak,
            IsResist = isResist,
            IsNull = raw.IsNull,
            IsRepel = raw.IsRepel,
            IsDrain = raw.IsDrain,
            IsRevealed = raw.IsRevealed,
            BlockedByShield = blockedByShield,
            MultiplierPercent = mult
        };
    }

    public double EstimateSingleHitDamage(
        ushort skillId,
        CombatantSnapshot attacker,
        CombatantSnapshot target,
        AffinityInfo effectiveAffinity,
        bool assumeChargedOverride,
        bool assumeUnchargedOverride,
        bool respectAnalysis)
    {
        if (effectiveAffinity.IsBadHit)
            return 0.0;

        var skill = GetSkill(skillId);
        ElementId actualElem = skillId == 0 ? GetAttackElement(attacker) : skill.Element;
        double baseDmg = ActorSingleHitDamages.TryGetValue((attacker, skillId, target), out var ad)
            ? ad
            : (SingleHitDamages.TryGetValue((skillId, target), out var d) ? d : 0.0);

        if (baseDmg > 0.0)
        {
            if (respectAnalysis && !effectiveAffinity.IsRevealed && target.IsEnemy)
                baseDmg *= effectiveAffinity.MultiplierPercent / 100.0;
            else if (Affinities.TryGetValue((target, actualElem), out var raw) && effectiveAffinity.MultiplierPercent != raw.MultiplierPercent && raw.MultiplierPercent > 0)
                baseDmg *= (double)effectiveAffinity.MultiplierPercent / raw.MultiplierPercent;
        }

        bool usesPowerCharge = skillId == 0 || actualElem == ElementId.Phys;
        bool chargedNow = usesPowerCharge ? attacker.HasPowerCharge : attacker.HasMindCharge;
        if (assumeUnchargedOverride && chargedNow)
            baseDmg /= 2.5;
        else if (assumeChargedOverride && !chargedNow)
            baseDmg *= 2.5;

        return baseDmg;
    }

    public bool IsKnownWeakness(CombatantSnapshot target, ElementId element, AffinityInfo aff) =>
        aff.IsWeak && !aff.BlockedByShield && !target.IsGuarding && !target.HasWallForElement(element);

    public AffinityInfo QueryAilmentAffinity(CombatantSnapshot actor, CombatantSnapshot target, bool respectAnalysis)
    {
        if (!AilmentAffinities.TryGetValue(target, out var raw))
            return new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        if (respectAnalysis && !raw.IsRevealed && target.IsEnemy)
            return new AffinityInfo { IsRevealed = false, MultiplierPercent = 100 };

        return raw;
    }

    public Dictionary<int, ElementId> AttackElements { get; } = new();
    public List<SpRecoveryItemInfo> AvailableSpItems { get; } = new();
    public List<BattleConsumableInfo> AvailableConsumables { get; } = new();

    public ElementId GetAttackElement(CombatantSnapshot actor) =>
        AttackElements.TryGetValue(actor.SlotId, out var elem) ? elem : ElementId.Phys;

    public IReadOnlyList<SpRecoveryItemInfo> GetAvailableSpRecoveryItems(CombatantSnapshot beneficiary)
    {
        if (AvailableSpItems.Count > 0)
            return AvailableSpItems;

        var list = new List<SpRecoveryItemInfo>();
        foreach (var item in AvailableConsumables)
        {
            if (item.Kind != BattleConsumableKind.SpRestore)
                continue;
            list.Add(new SpRecoveryItemInfo
            {
                ItemId = item.ItemId,
                Name = item.Name,
                SkillId = item.SkillId,
                Quantity = item.Quantity,
                EstimatedSpGain = item.EstimatedSpGain,
                EstimatedHpGain = item.EstimatedHpGain,
                IsPartyWide = item.IsPartyWide,
                IsFullSp = item.IsFullRecovery && item.EstimatedSpGain > 0,
                IsFullHp = item.IsFullRecovery && item.EstimatedHpGain > 0
            });
        }
        return list;
    }

    public IReadOnlyList<BattleConsumableInfo> GetAvailableConsumables(CombatantSnapshot beneficiary) =>
        AvailableConsumables;
}

public sealed class SmartPartyAiTests
{
    private static Config DefaultConfig(bool respectAnalysis = true) => new()
    {
        RespectAnalysisState = respectAnalysis,
        EnableSmartFullAssault = true,
        EnableSmartConserveSp = true,
        EnableSmartHealSupport = true,
        EnableSmartActFreely = true,
        HealSupportHpThreshold = 60,
        ActFreelyEmergencyHpThreshold = 40,
        ConserveSpSafeHpPercent = 45,
        ConserveSpLowCostThreshold = 8,
        DebugLogging = false
    };

    private static MockBattleBridge CreateStandardSkillDatabase()
    {
        var b = new MockBattleBridge();
        b.Skills[0] = new SkillMetadata { SkillId = 0, Name = "Attack", Element = ElementId.Phys, CostType = 0, BaseCost = 0, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 15, BaseCrit = 3 };
        b.Skills[1] = new SkillMetadata { SkillId = 1, Name = "Agi", Element = ElementId.Fire, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 40 };
        b.Skills[3] = new SkillMetadata { SkillId = 3, Name = "Agidyne", Element = ElementId.Fire, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 160 };
        b.Skills[4] = new SkillMetadata { SkillId = 4, Name = "Maragi", Element = ElementId.Fire, CostType = 2, BaseCost = 10, Scope = 1, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 40 };
        b.Skills[48] = new SkillMetadata { SkillId = 48, Name = "Spirit Drain", Element = ElementId.Almighty, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 0, Mode = 5, Power = 0 };
        b.Skills[53] = new SkillMetadata { SkillId = 53, Name = "Megidolaon", Element = ElementId.Almighty, CostType = 2, BaseCost = 38, Scope = 1, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 210 };
        b.Skills[122] = new SkillMetadata { SkillId = 122, Name = "Power Slash", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 10, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 100, BaseCrit = 25 };
        b.Skills[133] = new SkillMetadata { SkillId = 133, Name = "Blade of Fury", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 18, Scope = 1, TargetSide = 2, MinHits = 2, MaxHits = 4, EffectType = 1, Power = 35, BaseCrit = 5 };
        b.Skills[138] = new SkillMetadata { SkillId = 138, Name = "God's Hand", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 25, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 230, BaseCrit = 15 };
        b.Skills[192] = new SkillMetadata { SkillId = 192, Name = "Dia", Element = ElementId.Recovery, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 1, EffectType = 2, Power = 50 };
        b.Skills[193] = new SkillMetadata { SkillId = 193, Name = "Diarama", Element = ElementId.Recovery, CostType = 2, BaseCost = 6, Scope = 0, TargetSide = 1, EffectType = 2, Power = 160 };
        b.Skills[194] = new SkillMetadata { SkillId = 194, Name = "Diarahan", Element = ElementId.Recovery, CostType = 2, BaseCost = 18, Scope = 0, TargetSide = 1, EffectType = 11, Power = 100 };
        b.Skills[196] = new SkillMetadata { SkillId = 196, Name = "Mediarama", Element = ElementId.Recovery, CostType = 2, BaseCost = 12, Scope = 1, TargetSide = 1, EffectType = 2, Power = 140 };
        b.Skills[197] = new SkillMetadata { SkillId = 197, Name = "Mediarahan", Element = ElementId.Recovery, CostType = 2, BaseCost = 30, Scope = 1, TargetSide = 1, EffectType = 11, Power = 100 };
        b.Skills[209] = new SkillMetadata { SkillId = 209, Name = "Recarm", Element = ElementId.Recovery, CostType = 2, BaseCost = 8, Scope = 0, TargetSide = 1, TargetCond = 4, EffectType = 11, Power = 50, AilmentMask = 0x80000 };
        b.Skills[213] = new SkillMetadata { SkillId = 213, Name = "Rakunda", Element = ElementId.Support, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 2, BuffMask = SkillCatalog.BuffRakunda | 0x200 };
        b.Skills[217] = new SkillMetadata { SkillId = 217, Name = "Dekunda", Element = ElementId.Support, CostType = 2, BaseCost = 10, Scope = 1, TargetSide = 1, BuffMask = SkillCatalog.BuffDekunda };
        b.Skills[219] = new SkillMetadata { SkillId = 219, Name = "Matarukaja", Element = ElementId.Support, CostType = 2, BaseCost = 24, Scope = 1, TargetSide = 1, BuffMask = SkillCatalog.BuffTarukaja | 0x4 };
        b.Skills[224] = new SkillMetadata { SkillId = 224, Name = "Dekaja", Element = ElementId.Support, CostType = 2, BaseCost = 10, Scope = 1, TargetSide = 2, BuffMask = SkillCatalog.BuffDekaja };
        b.Skills[227] = new SkillMetadata { SkillId = 227, Name = "Power Charge", Element = ElementId.Support, CostType = 2, BaseCost = 15, Scope = 0, TargetSide = 1, TargetCond = 1, BuffMask = SkillCatalog.BuffPowerCharge };
        b.Skills[236] = new SkillMetadata { SkillId = 236, Name = "Fire Break", Element = ElementId.Support, CostType = 2, BaseCost = 15, Scope = 0, TargetSide = 2, BuffMask = SkillCatalog.BuffFireBreak };
        b.Skills[240] = new SkillMetadata { SkillId = 240, Name = "Red Wall", Element = ElementId.Support, CostType = 2, BaseCost = 18, Scope = 0, TargetSide = 1, BuffMask = SkillCatalog.BuffRedWall };
        // Verified additional SKILL.TBL skills for comprehensive scenario coverage
        b.Skills[14] = new SkillMetadata { SkillId = 14, Name = "Garu", Element = ElementId.Wind, StatType = 2, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 98 };
        b.Skills[26] = new SkillMetadata { SkillId = 26, Name = "Bufu", Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 98 };
        b.Skills[29] = new SkillMetadata { SkillId = 29, Name = "Mabufu", Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 10, Scope = 1, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 95 };
        b.Skills[52] = new SkillMetadata { SkillId = 52, Name = "Megidola", Element = ElementId.Almighty, StatType = 2, CostType = 2, BaseCost = 24, Scope = 1, TargetSide = 2, EffectType = 1, Power = 150, Accuracy = 98 };
        b.Skills[61] = new SkillMetadata { SkillId = 61, Name = "Hama", Element = ElementId.Light, StatType = 2, CostType = 2, BaseCost = 8, Scope = 0, TargetSide = 2, EffectType = 0, Power = 1, Accuracy = 100, AilmentMask = 0x80000 };
        b.Skills[65] = new SkillMetadata { SkillId = 65, Name = "God's Judgement", Element = ElementId.Light, StatType = 2, CostType = 2, BaseCost = 54, Scope = 1, TargetSide = 2, EffectType = 8, Power = 50, Accuracy = 100, AilmentMask = 0x80000 };
        b.Skills[67] = new SkillMetadata { SkillId = 67, Name = "Samsara", Element = ElementId.Light, StatType = 2, CostType = 2, BaseCost = 44, Scope = 1, TargetSide = 2, EffectType = 0, Power = 1, Accuracy = 100, AilmentMask = 0x80000 };
        b.Skills[73] = new SkillMetadata { SkillId = 73, Name = "Mudoon", Element = ElementId.Dark, StatType = 2, CostType = 2, BaseCost = 15, Scope = 0, TargetSide = 2, EffectType = 0, Power = 1, Accuracy = 100, AilmentMask = 0x80000 };
        b.Skills[132] = new SkillMetadata { SkillId = 132, Name = "Brave Blade", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 18, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 190, Accuracy = 95, BaseCrit = 20 };
        b.Skills[172] = new SkillMetadata { SkillId = 172, Name = "Swift Strike", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 15, Scope = 1, TargetSide = 2, MinHits = 1, MaxHits = 3, EffectType = 1, Power = 30, Accuracy = 75, BaseCrit = 5 };
        b.Skills[198] = new SkillMetadata { SkillId = 198, Name = "Salvation", Element = ElementId.Recovery, CostType = 2, BaseCost = 40, Scope = 1, TargetSide = 1, EffectType = 11, Power = 100, AilmentMask = 0xFF };
        b.Skills[199] = new SkillMetadata { SkillId = 199, Name = "Patra", Element = ElementId.Recovery, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 1, EffectType = 0, Power = 15, AilmentMask = 0x16 };
        b.Skills[201] = new SkillMetadata { SkillId = 201, Name = "Re Patra", Element = ElementId.Recovery, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 1, TargetCond = 8, EffectType = 0, Power = 15, AilmentMask = 0x00100001 };
        b.Skills[206] = new SkillMetadata { SkillId = 206, Name = "Baisudi", Element = ElementId.Recovery, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 1, EffectType = 0, Power = 15, AilmentMask = 0xC0 };
        b.Skills[208] = new SkillMetadata { SkillId = 208, Name = "Amrita", Element = ElementId.Recovery, CostType = 2, BaseCost = 12, Scope = 1, TargetSide = 1, EffectType = 0, Power = 15, AilmentMask = 0xFF };
        b.Skills[210] = new SkillMetadata { SkillId = 210, Name = "Samarecarm", Element = ElementId.Recovery, CostType = 2, BaseCost = 18, Scope = 0, TargetSide = 1, TargetCond = 4, EffectType = 11, Power = 100, AilmentMask = 0x80000 };
        b.Skills[211] = new SkillMetadata { SkillId = 211, Name = "Tarunda", Element = ElementId.Support, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 2, BuffMask = SkillCatalog.BuffTarunda | SkillCatalog.BuffMakanda };
        b.Skills[214] = new SkillMetadata { SkillId = 214, Name = "Marakunda", Element = ElementId.Support, CostType = 2, BaseCost = 24, Scope = 1, TargetSide = 2, BuffMask = SkillCatalog.BuffRakunda | 0x200 };
        b.Skills[218] = new SkillMetadata { SkillId = 218, Name = "Tarukaja", Element = ElementId.Support, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 1, BuffMask = SkillCatalog.BuffTarukaja | SkillCatalog.BuffMakakaja };
        b.Skills[221] = new SkillMetadata { SkillId = 221, Name = "Marakukaja", Element = ElementId.Support, CostType = 2, BaseCost = 24, Scope = 1, TargetSide = 1, BuffMask = SkillCatalog.BuffMarakukaja };
        b.Skills[225] = new SkillMetadata { SkillId = 225, Name = "Heat Riser", Element = ElementId.Support, CostType = 2, BaseCost = 30, Scope = 0, TargetSide = 1, BuffMask = SkillCatalog.BuffHeatRiser };
        b.Skills[226] = new SkillMetadata { SkillId = 226, Name = "Debilitate", Element = ElementId.Support, CostType = 2, BaseCost = 30, Scope = 0, TargetSide = 2, BuffMask = SkillCatalog.BuffDebilitate };
        b.Skills[228] = new SkillMetadata { SkillId = 228, Name = "Mind Charge", Element = ElementId.Support, CostType = 2, BaseCost = 15, Scope = 0, TargetSide = 1, TargetCond = 1, BuffMask = SkillCatalog.BuffMindCharge };
        b.Skills[230] = new SkillMetadata { SkillId = 230, Name = "Makarakarn", Element = ElementId.Support, CostType = 2, BaseCost = 36, Scope = 0, TargetSide = 1, BuffMask = SkillCatalog.BuffMakarakarn };
        b.Skills[234] = new SkillMetadata { SkillId = 234, Name = "Rebellion", Element = ElementId.Support, CostType = 2, BaseCost = 5, Scope = 0, TargetSide = 1, BuffMask = SkillCatalog.BuffCritRateUp };
        b.Skills[235] = new SkillMetadata { SkillId = 235, Name = "Revolution", Element = ElementId.Support, CostType = 2, BaseCost = 5, Scope = 1, TargetSide = 3, BuffMask = SkillCatalog.BuffRevolutionCritUp };
        b.Skills[245] = new SkillMetadata { SkillId = 245, Name = "Kamui Miracle", Element = ElementId.Recovery, CostType = 2, BaseCost = 0, Scope = 1, TargetSide = 1, TargetCond = 2, EffectType = 11, Power = 100 };
        b.Skills[247] = new SkillMetadata { SkillId = 247, Name = "Youthful Wind", Element = ElementId.Recovery, CostType = 2, BaseCost = 25, Scope = 1, TargetSide = 1, EffectType = 2, Power = 130, BuffMask = SkillCatalog.BuffSukukaja | 0x100 };
        b.Skills[248] = new SkillMetadata { SkillId = 248, Name = "Dragon Hustle", Element = ElementId.Support, CostType = 2, BaseCost = 150, Scope = 1, TargetSide = 1, BuffMask = SkillCatalog.BuffHeatRiser };
        b.Skills[252] = new SkillMetadata { SkillId = 252, Name = "Shield of Justice", Element = ElementId.Support, CostType = 2, BaseCost = 160, Scope = 1, TargetSide = 1, BuffMask = SkillCatalog.BuffShieldOfJustice };
        // Verified Ailment & Ghastly Wail SKILL.TBL metadata
        b.Skills[83] = new SkillMetadata { SkillId = 83, Name = "Pulinpa", Element = ElementId.SpecialConfuse, CostType = 2, BaseCost = 5, Scope = 0, TargetSide = 2, EffectType = 0, BaseAilmentChance = 70, AilmentMask = 0x80 };
        b.Skills[84] = new SkillMetadata { SkillId = 84, Name = "Tentarafoo", Element = ElementId.SpecialConfuse, CostType = 2, BaseCost = 12, Scope = 1, TargetSide = 2, EffectType = 0, BaseAilmentChance = 40, AilmentMask = 0x80 };
        b.Skills[85] = new SkillMetadata { SkillId = 85, Name = "Evil Touch", Element = ElementId.SpecialFear, CostType = 2, BaseCost = 5, Scope = 0, TargetSide = 2, EffectType = 0, BaseAilmentChance = 70, AilmentMask = 0x04 };
        b.Skills[86] = new SkillMetadata { SkillId = 86, Name = "Evil Smile", Element = ElementId.SpecialFear, CostType = 2, BaseCost = 12, Scope = 1, TargetSide = 2, EffectType = 0, BaseAilmentChance = 40, AilmentMask = 0x04 };
        b.Skills[87] = new SkillMetadata { SkillId = 87, Name = "Ghastly Wail", Element = ElementId.Dark, CostType = 2, BaseCost = 15, Scope = 1, TargetSide = 2, EffectType = 0, Power = 1, AilmentMask = 0x80000 };
        b.Skills[90] = new SkillMetadata { SkillId = 90, Name = "Valiant Dance", Element = ElementId.SpecialRage, CostType = 2, BaseCost = 12, Scope = 1, TargetSide = 2, EffectType = 0, BaseAilmentChance = 40, AilmentMask = 0x02 };
        b.Skills[94] = new SkillMetadata { SkillId = 94, Name = "Poisma", Element = ElementId.SpecialPoison, CostType = 2, BaseCost = 5, Scope = 0, TargetSide = 2, EffectType = 0, BaseAilmentChance = 70, AilmentMask = 0x20 };
        b.Skills[95] = new SkillMetadata { SkillId = 95, Name = "Soul Break", Element = ElementId.SpecialExhaust, CostType = 2, BaseCost = 5, Scope = 0, TargetSide = 2, EffectType = 0, BaseAilmentChance = 70, AilmentMask = 0x40 };
        b.Skills[98] = new SkillMetadata { SkillId = 98, Name = "Makajam", Element = ElementId.SpecialSilence, CostType = 2, BaseCost = 5, Scope = 0, TargetSide = 2, EffectType = 0, BaseAilmentChance = 70, AilmentMask = 0x08 };
        return b;
    }

    private static CombatantSnapshot MakeParty(
        ushort unitId,
        TacticMode tactic,
        int hp,
        int maxHp,
        int sp,
        int maxSp,
        ushort[] skills,
        bool powerCharge = false,
        bool mindCharge = false,
        uint stateFlags = 0,
        bool redWall = false,
        int atkStage = 0,
        int magStage = 0,
        int defStage = 0,
        int hitEvaStage = 0,
        int critRateStage = 0,
        int revolutionCritStage = 0,
        int? atkStageTurns = null,
        int? magStageTurns = null,
        int? defStageTurns = null,
        int? hitEvaStageTurns = null,
        int? critRateStageTurns = null,
        int? revolutionCritStageTurns = null,
        int agility = 0,
        bool shieldOfJustice = false) => new()
    {
        UnitPtr = (nint)(0x1000 + unitId * 0x10),
        CombatantPtr = (nint)(0x2000 + unitId * 0x10),
        SlotId = unitId,
        IsPartyMember = true,
        UnitOrEnemyId = unitId,
        DisplayName = SkillCatalog.GetPartyMemberName(unitId),
        Tactic = tactic,
        CurrentHp = hp,
        MaxHp = maxHp,
        CurrentSp = sp,
        MaxSp = maxSp,
        Agility = agility,
        EquippedSkills = skills,
        ChargeFlags = (powerCharge ? 0x80000u : 0u) | (mindCharge ? 0x100000u : 0u),
        ShieldFlags = shieldOfJustice ? (ushort)1 : (ushort)0,
        StateFlags = stateFlags,
        HasRedWall = redWall,
        AtkStage = atkStage,
        MagStage = magStage,
        DefStage = defStage,
        HitEvaStage = hitEvaStage,
        CritRateStage = critRateStage,
        RevolutionCritStage = revolutionCritStage,
        AtkStageTurns = atkStageTurns,
        MagStageTurns = magStageTurns,
        DefStageTurns = defStageTurns,
        HitEvaStageTurns = hitEvaStageTurns,
        CritRateStageTurns = critRateStageTurns,
        RevolutionCritStageTurns = revolutionCritStageTurns
    };

    private static CombatantSnapshot MakeEnemy(
        ushort id,
        int slot,
        int hp,
        int maxHp,
        bool isDown = false,
        bool isGuarding = false,
        bool redWall = false,
        bool tetrakarn = false,
        bool makarakarn = false,
        bool shieldOfJustice = false,
        int atkStage = 0,
        int defStage = 0,
        int magStage = 0,
        int hitEvaStage = 0,
        ushort waitMessageId = 0,
        ushort lastSkillId = 0,
        ushort lastActionType = 0,
        bool mindCharge = false,
        bool powerCharge = false,
        bool enemyCounter = false,
        int revolutionCritStage = 0,
        uint stateFlags = 0,
        int? defStageTurns = null,
        int agility = 0) => new()
    {
        UnitPtr = (nint)(0x5000 + slot * 0x10),
        CombatantPtr = (nint)(0x6000 + slot * 0x10),
        SlotId = slot,
        IsEnemy = true,
        UnitOrEnemyId = id,
        DisplayName = $"Shadow#{id}",
        CurrentHp = hp,
        MaxHp = maxHp,
        CurrentSp = 200,
        MaxSp = 200,
        Agility = agility,
        StateFlags = stateFlags | (isDown ? 0x100000u : 0u),
        BaseFlags = (ushort)((isGuarding ? 0x200 : 0) | (enemyCounter ? 0x100 : 0)),
        HasRedWall = redWall,
        ChargeFlags = (tetrakarn ? 0x20000u : 0u) | (makarakarn ? 0x40000u : 0u) | (powerCharge ? 0x80000u : 0u) | (mindCharge ? 0x100000u : 0u),
        ShieldFlags = shieldOfJustice ? (ushort)1 : (ushort)0,
        AtkStage = atkStage,
        MagStage = magStage,
        DefStage = defStage,
        HitEvaStage = hitEvaStage,
        DefStageTurns = defStageTurns,
        RevolutionCritStage = revolutionCritStage,
        WaitMessageId = waitMessageId,
        LastSkillId = lastSkillId,
        LastActionType = lastActionType
    };

    [Fact]
    public void FullAssault_Fire50WeakPlusPhys200_PicksFireFirst_ThenPhysOnOneMore()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var actor = MakeParty(3, TacticMode.FullAssault, 400, 400, 100, 100, new ushort[] { 1, 122 });
        var enemy = MakeEnemy(10, 1, 400, 400);

        bridge.Affinities[(enemy, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(enemy, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(1, enemy)] = 50.0;    // Fire deals 50 dmg
        bridge.SingleHitDamages[(122, enemy)] = 200.0; // Power Slash deals 200 dmg
        bridge.SingleHitDamages[(0, enemy)] = 40.0;

        // Turn 1: Not yet on 1-More, enemy is standing -> Fire(50) + 1More Power Slash(200) = 250 > Direct(200)
        var turn1 = new BattleSnapshot
        {
            Actor = actor,
            IsActorOnOneMore = false,
            Allies = new List<CombatantSnapshot> { actor },
            Enemies = new List<CombatantSnapshot> { enemy }
        };
        var d1 = engine.DecideAction(turn1, cfg)!;
        Assert.Equal((ushort)1, d1.SkillId); // Agi!
        Assert.Same(enemy, d1.Target);

        // Turn 2: Now on 1-More and enemy is knocked Down -> follow up with 200 dmg Power Slash!
        var downedEnemy = MakeEnemy(10, 1, 350, 400, isDown: true);
        bridge.Affinities[(downedEnemy, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(downedEnemy, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(1, downedEnemy)] = 50.0;
        bridge.SingleHitDamages[(122, downedEnemy)] = 200.0;
        bridge.SingleHitDamages[(0, downedEnemy)] = 40.0;

        var turn2 = new BattleSnapshot
        {
            Actor = actor,
            IsActorOnOneMore = true,
            Allies = new List<CombatantSnapshot> { actor },
            Enemies = new List<CombatantSnapshot> { downedEnemy }
        };
        var d2 = engine.DecideAction(turn2, cfg)!;
        Assert.Equal((ushort)122, d2.SkillId); // Power Slash!
    }

    [Fact]
    public void FullAssault_MultiHitExpectedDamage_BeatsSingleHit()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var actor = MakeParty(3, TacticMode.FullAssault, 400, 400, 100, 100, new ushort[] { 122, 133 });
        var enemy = MakeEnemy(11, 1, 500, 500);
        bridge.Affinities[(enemy, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        // Power Slash: 1 hit * 130 = 130; Blade of Fury: 2-4 hits (expected 3.0) * 60 = 180!
        bridge.SingleHitDamages[(122, enemy)] = 130.0;
        bridge.SingleHitDamages[(133, enemy)] = 60.0;
        bridge.SingleHitDamages[(0, enemy)] = 30.0;

        var battle = new BattleSnapshot { Actor = actor, Allies = new List<CombatantSnapshot> { actor }, Enemies = new List<CombatantSnapshot> { enemy } };
        var decision = engine.DecideAction(battle, cfg)!;
        Assert.Equal((ushort)133, decision.SkillId);
    }

    [Fact]
    public void FullAssault_InnateWeaknessNegatedByRedWallOrMakarakarnOrGuard_NeverUsesFire()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var actor = MakeParty(4, TacticMode.FullAssault, 350, 350, 120, 120, new ushort[] { 3, 122 });

        // Case 1: Innately Fire Weak, negated by Red Wall -> Fire becomes 50% Resist (no 1-More), Phys 120 > Fire 40
        var enemyWall = MakeEnemy(12, 1, 400, 400, redWall: true);
        bridge.Affinities[(enemyWall, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(enemyWall, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(3, enemyWall)] = 80.0;   // scaled down by 50/125 = 32 when Wall active
        bridge.SingleHitDamages[(122, enemyWall)] = 110.0;
        bridge.SingleHitDamages[(0, enemyWall)] = 45.0;

        var dWall = engine.DecideAction(new BattleSnapshot { Actor = actor, Allies = new() { actor }, Enemies = new() { enemyWall } }, cfg)!;
        Assert.Equal((ushort)122, dWall.SkillId);

        // Case 2: Innately Fire Weak, blocked by Makarakarn -> Fire completely forbidden
        var enemyKarn = MakeEnemy(12, 1, 400, 400, makarakarn: true);
        bridge.Affinities[(enemyKarn, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(enemyKarn, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(3, enemyKarn)] = 180.0;
        bridge.SingleHitDamages[(122, enemyKarn)] = 110.0;
        bridge.SingleHitDamages[(0, enemyKarn)] = 45.0;

        var dKarn = engine.DecideAction(new BattleSnapshot { Actor = actor, Allies = new() { actor }, Enemies = new() { enemyKarn } }, cfg)!;
        Assert.Equal((ushort)122, dKarn.SkillId);

        // Case 3: Innately Phys Weak, blocked by Tetrakarn -> AI avoids Phys and picks Fire (Agidyne)
        var enemyTetra = MakeEnemy(12, 1, 400, 400, tetrakarn: true);
        bridge.Affinities[(enemyTetra, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(enemyTetra, ElementId.Phys)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(3, enemyTetra)] = 95.0;
        bridge.SingleHitDamages[(122, enemyTetra)] = 200.0;
        bridge.SingleHitDamages[(0, enemyTetra)] = 60.0;

        var dTetra = engine.DecideAction(new BattleSnapshot { Actor = actor, Allies = new() { actor }, Enemies = new() { enemyTetra } }, cfg)!;
        Assert.Equal((ushort)3, dTetra.SkillId);
    }

    [Fact]
    public void FullAssault_AoEMixedGroup_RejectsMaragiWhenEnemyDrainsFire()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var actor = MakeParty(4, TacticMode.FullAssault, 350, 350, 120, 120, new ushort[] { 1, 4, 122 });
        var weakEnemy = MakeEnemy(20, 1, 150, 150);
        var drainEnemy = MakeEnemy(21, 2, 200, 200);

        bridge.Affinities[(weakEnemy, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(weakEnemy, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(drainEnemy, ElementId.Fire)] = new AffinityInfo { IsDrain = true, IsRevealed = true, MultiplierPercent = 0 };
        bridge.Affinities[(drainEnemy, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        bridge.SingleHitDamages[(4, weakEnemy)] = 90.0;
        bridge.SingleHitDamages[(1, weakEnemy)] = 95.0;
        bridge.SingleHitDamages[(122, weakEnemy)] = 70.0;
        bridge.SingleHitDamages[(122, drainEnemy)] = 70.0;

        var battle = new BattleSnapshot { Actor = actor, Allies = new() { actor }, Enemies = new() { weakEnemy, drainEnemy } };
        var decision = engine.DecideAction(battle, cfg)!;

        // Must NOT pick Maragi (4); must pick Single-Target Agi (1) onto weakEnemy for 1-More!
        Assert.Equal((ushort)1, decision.SkillId);
        Assert.Same(weakEnemy, decision.Target);
    }

    [Fact]
    public void FullAssault_RespectAnalysisState_ControlsKnowledgeOfUnanalyzedAffinities()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);

        var actor = MakeParty(4, TacticMode.FullAssault, 350, 350, 120, 120, new ushort[] { 1, 122 });
        var hiddenEnemy = MakeEnemy(30, 1, 500, 500);
        bridge.Affinities[(hiddenEnemy, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = false, MultiplierPercent = 150 };
        bridge.Affinities[(hiddenEnemy, ElementId.Phys)] = new AffinityInfo { IsRevealed = false, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(1, hiddenEnemy)] = 60.0;    // base 40 * 1.5 = 60
        bridge.SingleHitDamages[(122, hiddenEnemy)] = 150.0; // neutral 150

        var battle = new BattleSnapshot { Actor = actor, Allies = new() { actor }, Enemies = new() { hiddenEnemy } };

        // RespectAnalysis = true (with probing disabled to isolate omniscience vs fair knowledge) -> AI does not know Fire weakness yet, picks Power Slash (150 vs 40)
        var fairCfg = DefaultConfig(respectAnalysis: true);
        fairCfg.ProbeUnrevealedElements = false;
        var fair = engine.DecideAction(battle, fairCfg)!;
        Assert.Equal((ushort)122, fair.SkillId);

        // RespectAnalysis = false -> AI sees Fire weakness (40*1.5=60 + 1-More 150 = 210 > 150), picks Agi!
        var omniscient = engine.DecideAction(battle, DefaultConfig(respectAnalysis: false))!;
        Assert.Equal((ushort)1, omniscient.SkillId);
    }

    [Fact]
    public void FullAssault_PowerChargeUsedOnBossWhenTwoTurnMathWins()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var kanji = MakeParty(6, TacticMode.FullAssault, 600, 600, 150, 150, new ushort[] { 227, 138 });
        var boss = MakeEnemy(99, 1, 2000, 2000);
        bridge.Affinities[(boss, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(138, boss)] = 300.0; // 0.52 * 750 = 390 > 300
        bridge.SingleHitDamages[(0, boss)] = 50.0;

        var battle = new BattleSnapshot { Actor = kanji, Allies = new() { kanji }, Enemies = new() { boss } };
        var d1 = engine.DecideAction(battle, cfg)!;
        Assert.Equal((ushort)227, d1.SkillId); // Power Charge!
    }

    [Fact]
    public void ConserveSp_UsesZeroSpOrLowCostOneMoreOrSpiritDrain()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Standard fight: has Agidyne (12 SP, 160 dmg) and God's Hand (0 SP/25% HP, 150 dmg) -> uses 0-SP Phys when HP safe!
        var chie = MakeParty(3, TacticMode.ConserveSp, 400, 400, 80, 100, new ushort[] { 3, 138 });
        var enemy = MakeEnemy(15, 1, 300, 300);
        bridge.Affinities[(enemy, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(enemy, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(3, enemy)] = 160.0;
        bridge.SingleHitDamages[(138, enemy)] = 150.0;
        bridge.SingleHitDamages[(0, enemy)] = 70.0;

        var dFree = engine.DecideAction(new BattleSnapshot { Actor = chie, Allies = new() { chie }, Enemies = new() { enemy } }, cfg)!;
        Assert.Equal((ushort)138, dFree.SkillId);

        // 2. Low SP (<35%) with Spirit Drain -> recovers SP!
        var naoto = MakeParty(7, TacticMode.ConserveSp, 350, 350, 20, 200, new ushort[] { 48, 53 });
        var dDrain = engine.DecideAction(new BattleSnapshot { Actor = naoto, Allies = new() { naoto }, Enemies = new() { enemy } }, cfg)!;
        Assert.Equal((ushort)48, dDrain.SkillId);

        // 3. Cheap Agi (4 SP, 50 dmg, Weak) -> 1-More 0-SP Attack (200 dmg) = 250 >= 1.25 * 200 -> picks cheap Agi!
        var yosuke = MakeParty(2, TacticMode.ConserveSp, 300, 300, 100, 100, new ushort[] { 1 });
        var fireWeak = MakeEnemy(16, 1, 300, 300);
        bridge.Affinities[(fireWeak, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(fireWeak, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(1, fireWeak)] = 50.0;
        bridge.SingleHitDamages[(0, fireWeak)] = 200.0;

        var dStarter = engine.DecideAction(new BattleSnapshot { Actor = yosuke, Allies = new() { yosuke }, Enemies = new() { fireWeak } }, cfg)!;
        Assert.Equal((ushort)1, dStarter.SkillId);
    }

    [Fact]
    public void HealSupport_UsesPercentHpHealing_EfficientTiers_Buffs_AndFallback()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var yukiko = MakeParty(4, TacticMode.HealSupport, 320, 320, 200, 200, new ushort[] { 192, 193, 196, 197, 209, 3 });
        var kanjiLow = MakeParty(6, TacticMode.FullAssault, 280, 700, 80, 80, Array.Empty<ushort>()); // 40% HP! (420 missing)
        var boss = MakeEnemy(99, 1, 1200, 1200);
        bridge.Affinities[(boss, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(3, boss)] = 150.0;

        // 1. Kanji is at 280/700 HP (40% < 60%): must heal Kanji, and must NOT waste SP on Dia (50*1.45=72) vs Diarama (160*1.45=232) -> wait actually missing 420 -> Diarama/Mediarama!
        var dHeal = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko, kanjiLow },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Same(kanjiLow, dHeal.Target);
        Assert.NotEqual((ushort)192, dHeal.SkillId); // Not tiny Dia!

        // 2. Both Yukiko (150/320) & Kanji (280/700) injured -> AoE Mediarama/Mediarahan!
        var yukikoLow = MakeParty(4, TacticMode.HealSupport, 150, 320, 200, 200, new ushort[] { 192, 193, 196, 197 });
        var dAoeHeal = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoLow,
            Allies = new() { yukikoLow, kanjiLow },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.True(dAoeHeal.SkillId is 196 or 197);

        // 3. Party healthy, enemy has 2 buffs -> Dekaja!
        var kanjiFull = MakeParty(6, TacticMode.FullAssault, 700, 700, 80, 80, Array.Empty<ushort>());
        var teddie = MakeParty(8, TacticMode.HealSupport, 380, 380, 200, 200, new ushort[] { 224, 213, 196, 3 });
        var buffedBoss = MakeEnemy(99, 1, 1200, 1200, atkStage: 1, defStage: 1);
        var dDekaja = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, kanjiFull },
            Enemies = new() { buffedBoss }
        }, cfg)!;
        Assert.Equal((ushort)224, dDekaja.SkillId);

        // 4. Healthy, unbuffed, already debuffed boss -> falls back to Agidyne smart offense!
        var debuffedBoss = MakeEnemy(99, 1, 1200, 1200, atkStage: -1, defStage: -1);
        var healerAttacking = MakeParty(4, TacticMode.HealSupport, 320, 320, 200, 200, new ushort[] { 193, 197, 3 });
        bridge.Affinities[(debuffedBoss, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(3, debuffedBoss)] = 180.0;
        var dFallback = engine.DecideAction(new BattleSnapshot
        {
            Actor = healerAttacking,
            Allies = new() { healerAttacking, kanjiFull },
            Enemies = new() { debuffedBoss }
        }, cfg)!;
        Assert.Equal((ushort)3, dFallback.SkillId);
    }

    [Fact]
    public void ActFreely_IgnoresChipDamage_HealsCritical_PrioritizesOffenseAndBossBuff()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var teddie = MakeParty(8, TacticMode.ActFreely, 400, 400, 200, 200, new ushort[] { 193, 1, 219 });
        var chipAlly = MakeParty(6, TacticMode.FullAssault, 450, 700, 80, 80, Array.Empty<ushort>()); // 64% HP (above 40% emergency threshold)
        var weakTrash = MakeEnemy(10, 1, 180, 180);
        bridge.Affinities[(weakTrash, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(1, weakTrash)] = 90.0;

        // Above 40% emergency threshold on trash mob -> chooses Agi offense, doesn't waste turn healing 64% chip!
        var dOff = engine.DecideAction(new BattleSnapshot { Actor = teddie, Allies = new() { teddie, chipAlly }, Enemies = new() { weakTrash } }, cfg)!;
        Assert.Equal((ushort)1, dOff.SkillId);

        // Critical ally at 25% HP (< 40%) -> interrupts offense to heal immediately!
        var critAlly = MakeParty(6, TacticMode.FullAssault, 175, 700, 80, 80, Array.Empty<ushort>());
        var dEmerg = engine.DecideAction(new BattleSnapshot { Actor = teddie, Allies = new() { teddie, critAlly }, Enemies = new() { weakTrash } }, cfg)!;
        Assert.Equal((ushort)193, dEmerg.SkillId);
        Assert.Same(critAlly, dEmerg.Target);
    }

    [Fact]
    public void FullAssault_ShieldOfJustice_And_MixedAoeNullMath()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // Shield of Justice blocks Phys & Fire -> actor falls back safely to Guard without crashing
        var actor = MakeParty(4, TacticMode.FullAssault, 350, 350, 120, 120, new ushort[] { 1, 122 });
        var shielded = MakeEnemy(50, 1, 500, 500, shieldOfJustice: true);
        bridge.Affinities[(shielded, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(shielded, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(1, shielded)] = 50.0;
        bridge.SingleHitDamages[(122, shielded)] = 100.0;
        bridge.SingleHitDamages[(0, shielded)] = 30.0;

        var dShield = engine.DecideAction(new BattleSnapshot { Actor = actor, Allies = new() { actor }, Enemies = new() { shielded } }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dShield.ActionKind);

        // Mixed group: Enemy1 & Enemy2 take 90 dmg each from Maragi (180 total), Enemy3 Nulls Fire -> Agidyne single target (160) vs Maragi (180)
        //Wait: 180 > 160 so Maragi wins cleanly when Enemy3 only Nulls (doesn't Repel/Drain); vs if only 1 Enemy takes 90 and Enemy3 Nulls, Agidyne (160) beats Maragi (90)!
        var yukiko = MakeParty(4, TacticMode.FullAssault, 350, 350, 120, 120, new ushort[] { 3, 4 });
        var e1 = MakeEnemy(51, 1, 300, 300);
        var e3Null = MakeEnemy(52, 2, 300, 300);
        bridge.Affinities[(e1, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(e3Null, ElementId.Fire)] = new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };
        bridge.Affinities[(e1, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(e3Null, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(4, e1)] = 75.0;     // Maragi on 1 active target = 75
        bridge.SingleHitDamages[(3, e1)] = 160.0;    // Agidyne on 1 active target = 160 > 75
        bridge.SingleHitDamages[(0, e1)] = 30.0;
        bridge.SingleHitDamages[(0, e3Null)] = 30.0;

        var dMixed = engine.DecideAction(new BattleSnapshot { Actor = yukiko, Allies = new() { yukiko }, Enemies = new() { e1, e3Null } }, cfg)!;
        Assert.Equal((ushort)3, dMixed.SkillId);
        Assert.Same(e1, dMixed.Target);
    }

    [Fact]
    public void Config_SaveAndLoad_RoundTripsCleanly()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"p4g64_smartai_test_{Guid.NewGuid():N}");
        var cfgPath = Path.Combine(tmpDir, "Config.json");
        try
        {
            var loaded = Configurable<Config>.Load(cfgPath);
            loaded.RespectAnalysisState = false;
            loaded.HealSupportHpThreshold = 75;
            loaded.Save!();

            var reloaded = Configurable<Config>.Load(cfgPath);
            Assert.False(reloaded.RespectAnalysisState);
            Assert.Equal(75, reloaded.HealSupportHpThreshold);
            Assert.DoesNotContain("\"Save\"", File.ReadAllText(cfgPath));
        }
        finally
        {
            if (Directory.Exists(tmpDir))
                Directory.Delete(tmpDir, recursive: true);
        }
    }

    [Fact]
    public void Telegraph_ShadowYukikoPassionateStare_ChieGuardsFireWeakness()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var shadowYukiko = MakeEnemy(258, 1, 1500, 1500, waitMessageId: 202);
        bridge.Affinities[(shadowYukiko, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        // Chie on Act Freely (weak to Fire) -> Guards on "Shadow Yukiko has a passionate stare"
        var chieFreely = MakeParty(3, TacticMode.ActFreely, 280, 280, 80, 80, new ushort[] { 122 });
        bridge.Affinities[(chieFreely, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(122, shadowYukiko)] = 95.0;

        var dFreely = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieFreely,
            Allies = new() { chieFreely },
            Enemies = new() { shadowYukiko },
            ActiveBattleBits = new HashSet<int> { 451 }
        }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dFreely.ActionKind);

        // Chie on Full Assault -> also Guards to protect weakness & deny 1 More when Yukiko isn't 1-shot
        var chieAssault = MakeParty(3, TacticMode.FullAssault, 280, 280, 80, 80, new ushort[] { 122 });
        bridge.Affinities[(chieAssault, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };

        var dAssault = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieAssault,
            Allies = new() { chieAssault },
            Enemies = new() { shadowYukiko },
            ActiveBattleBits = new HashSet<int> { 451 }
        }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dAssault.ActionKind);
    }

    [Fact]
    public void Telegraph_ShadowYukikoPassionateStare_ChieWithRedWallAttacksNormally()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var shadowYukiko = MakeEnemy(258, 1, 1500, 1500, waitMessageId: 202);
        bridge.Affinities[(shadowYukiko, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        var chieProtected = MakeParty(3, TacticMode.ActFreely, 280, 280, 80, 80, new ushort[] { 122 }, redWall: true);
        bridge.Affinities[(chieProtected, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(122, shadowYukiko)] = 95.0;

        var decision = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieProtected,
            Allies = new() { chieProtected },
            Enemies = new() { shadowYukiko },
            ActiveBattleBits = new HashSet<int> { 451 }
        }, cfg)!;

        Assert.Equal(AiActionKind.Skill, decision.ActionKind);
        Assert.Equal((ushort)122, decision.SkillId);
    }

    [Fact]
    public void Telegraph_ShadowTeddieNihilHand_PartyGuards()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var shadowTeddie = MakeEnemy(261, 1, 3000, 3000, waitMessageId: 169);
        bridge.Affinities[(shadowTeddie, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        var yukiko = MakeParty(4, TacticMode.ActFreely, 360, 360, 150, 150, new ushort[] { 3, 193 });
        bridge.SingleHitDamages[(3, shadowTeddie)] = 160.0;

        var decision = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko },
            Enemies = new() { shadowTeddie },
            ActiveBattleBits = new HashSet<int> { 458 }
        }, cfg)!;

        Assert.Equal(AiActionKind.Guard, decision.ActionKind);
    }

    [Fact]
    public void ActFreely_SingleEnemyNeutral_PrefersBufuOverMabufuAtEqualDamage()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[26] = new SkillMetadata { SkillId = 26, Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 98 };
        bridge.Skills[29] = new SkillMetadata { SkillId = 29, Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 10, Scope = 1, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 95 };

        var chie = MakeParty(3, TacticMode.ActFreely, 166, 226, 106, 106, new ushort[] { 26, 29 });
        var enemy = MakeEnemy(10, 1, 300, 300);

        bridge.SingleHitDamages[(0, enemy)] = 25;
        bridge.SingleHitDamages[(26, enemy)] = 78;
        bridge.SingleHitDamages[(29, enemy)] = 78;

        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();
        cfg.EnableSpRecoveryItems = false;

        var decision = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie },
            Enemies = new() { enemy }
        }, cfg)!;

        Assert.Equal(AiActionKind.Skill, decision.ActionKind);
        Assert.Equal((ushort)26, decision.SkillId);
    }

    [Fact]
    public void ActFreely_SingleLowHpEnemy_StandardAttackBeatsLethalGaruAndHealthyGaru()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[0] = new SkillMetadata { SkillId = 0, Element = ElementId.Phys, StatType = 1, CostType = 0, BaseCost = 0, Scope = 0, TargetSide = 2, EffectType = 1, Power = 15, Accuracy = 90 };
        bridge.Skills[14] = new SkillMetadata { SkillId = 14, Element = ElementId.Wind, StatType = 2, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 98 };

        var yosuke = MakeParty(2, TacticMode.ActFreely, 238, 251, 108, 146, new ushort[] { 14 });
        var lowEnemy = MakeEnemy(219, 1, 70, 200);
        var healthyEnemy = MakeEnemy(219, 2, 250, 250);

        bridge.SingleHitDamages[(0, lowEnemy)] = 130;
        bridge.SingleHitDamages[(14, lowEnemy)] = 78;
        bridge.SingleHitDamages[(0, healthyEnemy)] = 130;
        bridge.SingleHitDamages[(14, healthyEnemy)] = 78;

        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();
        cfg.EnableSpRecoveryItems = false;

        var lowDecision = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke },
            Enemies = new() { lowEnemy }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, lowDecision.ActionKind);

        var healthyDecision = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke },
            Enemies = new() { healthyEnemy }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, healthyDecision.ActionKind);
    }

    [Fact]
    public void SpRecoveryItem_UsesWellSizedPartialItemAndProtectsRareSomaByDefault()
    {
        var bridge = CreateStandardSkillDatabase();
        var yukiko = MakeParty(4, TacticMode.ActFreely, 200, 200, 15, 80, new ushort[] { 1 });
        var enemy = MakeEnemy(10, 1, 450, 450);

        bridge.SingleHitDamages[(0, enemy)] = 35;
        bridge.SingleHitDamages[(1, enemy)] = 95;

        bridge.AvailableSpItems.AddRange(new[]
        {
            new SpRecoveryItemInfo { ItemId = 779, Name = "Soul Drop", SkillId = 298, Quantity = 3, EstimatedSpGain = 10 },
            new SpRecoveryItemInfo { ItemId = 780, Name = "Snuff Soul", SkillId = 299, Quantity = 2, EstimatedSpGain = 50 },
            new SpRecoveryItemInfo { ItemId = 781, Name = "Chewing Soul", SkillId = 300, Quantity = 1, EstimatedSpGain = 100 },
            new SpRecoveryItemInfo { ItemId = 782, Name = "Soul Food", SkillId = 301, Quantity = 1, EstimatedSpGain = 200, IsFullSp = true },
            new SpRecoveryItemInfo { ItemId = 792, Name = "Soma", SkillId = 311, Quantity = 1, EstimatedSpGain = 200, EstimatedHpGain = 500, IsPartyWide = true, IsFullSp = true, IsFullHp = true }
        });

        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();
        cfg.EnableSpRecoveryItems = true;
        cfg.SpRecoveryItemThresholdPercent = 20;
        cfg.AllowRareFullSpItems = false;

        // 15/80 SP (missing 65) -> chooses Snuff Soul (+50), preserving Chewing Soul / Soul Food / Soma
        var decision = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko },
            Enemies = new() { enemy }
        }, cfg)!;

        Assert.Equal(AiActionKind.Item, decision.ActionKind);
        Assert.Equal((ushort)780, decision.ItemId);
        Assert.Equal((ushort)299, decision.SkillId);
        Assert.Equal(yukiko, decision.Target);

        // Late-game Yukiko at 10/180 SP (missing 170) -> chooses Chewing Soul (+100) while still reserving Soma/Soul Food
        var yukikoLate = MakeParty(4, TacticMode.HealSupport, 250, 250, 10, 180, new ushort[] { 1 });
        bridge.SingleHitDamages[(0, enemy)] = 40;
        var lateDecision = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoLate,
            Allies = new() { yukikoLate },
            Enemies = new() { enemy }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, lateDecision.ActionKind);
        Assert.Equal((ushort)781, lateDecision.ItemId);

        // Does NOT burn SP items when remaining enemy HP is tiny and fight is nearly over
        var weakEnemy = MakeEnemy(11, 2, 30, 200);
        bridge.SingleHitDamages[(0, weakEnemy)] = 35;
        var finishDecision = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko },
            Enemies = new() { weakEnemy }
        }, cfg)!;
        Assert.NotEqual(AiActionKind.Item, finishDecision.ActionKind);
    }

    [Fact]
    public void HealSupport_UsesSpItemOnLowSpProtagonistWhenAllySupportEnabled()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.AvailableSpItems.Add(new SpRecoveryItemInfo
        {
            ItemId = 780, Name = "Snuff Soul", SkillId = 299, Quantity = 2, EstimatedSpGain = 50
        });

        var protag = MakeParty(1, TacticMode.ActFreely, 260, 260, 12, 140, Array.Empty<ushort>());
        var yukiko = MakeParty(4, TacticMode.HealSupport, 240, 240, 150, 180, new ushort[] { 193 });
        var boss = MakeEnemy(10, 1, 800, 800);
        bridge.SingleHitDamages[(0, boss)] = 40;

        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();
        cfg.EnableSpRecoveryItems = true;
        cfg.AllowAllySpItemSupport = true;
        cfg.SpRecoveryItemThresholdPercent = 20;

        var decision = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { protag, yukiko },
            Enemies = new() { boss }
        }, cfg)!;

        Assert.Equal(AiActionKind.Item, decision.ActionKind);
        Assert.Equal((ushort)780, decision.ItemId);
        Assert.Equal(protag, decision.Target);
    }

    // =========================================================================
    // REGRESSION & COMPREHENSIVE SCENARIO SUITE
    // =========================================================================

    [Fact]
    public void NativeStatStageDecoding_HandlesNegativeDebuffStagesAndSignedNibbles()
    {
        // P4G 0x1400d0908 zero-extends AL into EAX: -1 => 255, -2 => 254, -7 => 249
        Assert.Equal((sbyte)0, SkillCatalog.DecodeSignedStatStageByte(0));
        Assert.Equal((sbyte)3, SkillCatalog.DecodeSignedStatStageByte(3));
        Assert.Equal((sbyte)-1, SkillCatalog.DecodeSignedStatStageByte(255));
        Assert.Equal((sbyte)-2, SkillCatalog.DecodeSignedStatStageByte(254));
        Assert.Equal((sbyte)-7, SkillCatalog.DecodeSignedStatStageByte(249));

        // P4G 0x1400d08fc signed nibble encoding: 0..7 positive, 8..14 => nib - 15 (0xE = -1)
        Assert.Equal((sbyte)-1, SkillCatalog.UnpackSignedNibble(0x3E, highNibble: false));
        Assert.Equal((sbyte)3, SkillCatalog.UnpackSignedNibble(0x3E, highNibble: true));
        Assert.Equal((sbyte)-2, SkillCatalog.UnpackSignedNibble(0xD0, highNibble: true));
    }

    [Fact]
    public void HealSupport_DebuffedBossNotSpammedWithDebilitate_DebuffedPartyGetsDekunda()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var bossDebuffed = MakeEnemy(99, 1, 1800, 1800, atkStage: -1, defStage: -1, magStage: -1, hitEvaStage: -1);
        bridge.Affinities[(bossDebuffed, ElementId.Almighty)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(52, bossDebuffed)] = 180.0;

        var naoto = MakeParty(7, TacticMode.HealSupport, 350, 350, 180, 180, new ushort[] { 224, 226, 217, 52 });
        var kanji = MakeParty(6, TacticMode.FullAssault, 650, 650, 80, 80, Array.Empty<ushort>());

        // Already debilitated boss -> must NOT spam Debilitate (226) or false Dekaja (224); must attack with Megidola (52)
        var dAttack = engine.DecideAction(new BattleSnapshot
        {
            Actor = naoto,
            Allies = new() { naoto, kanji },
            Enemies = new() { bossDebuffed }
        }, cfg)!;
        Assert.Equal((ushort)52, dAttack.SkillId);

        // Party has >= 2 negative debuffs -> casts Dekunda (217)
        var debuffedKanji = MakeParty(6, TacticMode.FullAssault, 650, 650, 80, 80, Array.Empty<ushort>(), atkStage: -1, defStage: -1);
        var dDekunda = engine.DecideAction(new BattleSnapshot
        {
            Actor = naoto,
            Allies = new() { naoto, debuffedKanji },
            Enemies = new() { bossDebuffed }
        }, cfg)!;
        Assert.Equal((ushort)217, dDekunda.SkillId);

        // Chie at 38.75% SP (155/400 SP: Dragon Hustle IS affordable at 150 SP, but below 50% threshold)
        // must pick Matarukaja (219, 24 SP) cleanly even when Dragon Hustle (248) sits in an earlier skill slot!
        var downedStatKanji = MakeParty(6, TacticMode.FullAssault, 650, 650, 80, 80, Array.Empty<ushort>(), atkStage: -1);
        var chieLowSp = MakeParty(3, TacticMode.HealSupport, 400, 400, 155, 400, new ushort[] { 248, 219, 138 });
        var dMataru = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieLowSp,
            Allies = new() { chieLowSp, downedStatKanji },
            Enemies = new() { MakeEnemy(99, 1, 1200, 1200) }
        }, cfg)!;
        Assert.Equal((ushort)219, dMataru.SkillId);
    }

    [Fact]
    public void HealSupport_KamuiMiracleNeverPickedAsHeal_YouthfulWindAndSalvationSynergize()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Teddie with Kamui Miracle (245, 0 SP) + Mediarama (196, 12 SP) must NEVER pick Kamui Miracle
        var teddie = MakeParty(8, TacticMode.HealSupport, 160, 350, 150, 150, new ushort[] { 245, 196, 193 });
        var protagLow = MakeParty(1, TacticMode.ActFreely, 150, 360, 100, 100, Array.Empty<ushort>());
        var boss = MakeEnemy(99, 1, 1500, 1500);

        var dTeddie = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { protagLow, teddie },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.NotEqual((ushort)245, dTeddie.SkillId);
        Assert.Equal((ushort)196, dTeddie.SkillId);

        // 2. Yosuke with Mediarama (196) + Youthful Wind (247) when party lacks Hit/Eva buff -> Youthful Wind (heal + Masukukaja)!
        var yukiko = MakeParty(4, TacticMode.HealSupport, 180, 360, 180, 200, new ushort[] { 196, 247, 197, 198 });
        var dYouthful = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { protagLow, yukiko },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)247, dYouthful.SkillId);

        // Same healing scenario, party ALREADY has +1 Hit/Eva -> chooses cheaper Mediarama (196)
        var yukikoHitUp = MakeParty(4, TacticMode.HealSupport, 180, 360, 180, 200, new ushort[] { 196, 247, 197, 198 }, hitEvaStage: 1);
        var protagHitUp = MakeParty(1, TacticMode.ActFreely, 150, 360, 100, 100, Array.Empty<ushort>(), hitEvaStage: 1);
        var dMediarama = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoHitUp,
            Allies = new() { protagHitUp, yukikoHitUp },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)196, dMediarama.SkillId);

        // 3. Party critically hurt (<30% HP) + an ally Confused (0x80) -> chooses Salvation (198) over plain Mediarahan (197)
        var yukikoCrit = MakeParty(4, TacticMode.HealSupport, 80, 400, 180, 200, new ushort[] { 196, 197, 198 }, defStage: 1);
        var protagConfused = MakeParty(1, TacticMode.ActFreely, 80, 450, 100, 100, Array.Empty<ushort>(), stateFlags: 0x80, defStage: 1);
        var dSalvation = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoCrit,
            Allies = new() { protagConfused, yukikoCrit },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)198, dSalvation.SkillId);

        // 4. Critical single ally missing 350 HP when Yukiko only has Dia (192) + Mediarama (196) -> upgrades to Mediarama instead of tiny Dia
        var yukikoDiaMed = MakeParty(4, TacticMode.HealSupport, 320, 320, 120, 120, new ushort[] { 192, 196 });
        var kanjiCrit = MakeParty(6, TacticMode.FullAssault, 50, 450, 80, 80, Array.Empty<ushort>());
        var dUpgrade = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoDiaMed,
            Allies = new() { yukikoDiaMed, kanjiCrit },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)196, dUpgrade.SkillId);
    }

    [Fact]
    public void InstantKill_HamaMudoNeverGrantsFakeOneMore_NeverCastOnBosses_ButUsedOnWeakMob()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Enemy1 Down + Enemy2 Weak to Light AND Wind: Hama (61) does NOT knock down; Garu (14) grants 1 More + AOA!
        var naoto = MakeParty(7, TacticMode.FullAssault, 320, 320, 150, 150, new ushort[] { 61, 14 });
        var protag = MakeParty(1, TacticMode.DirectCommands, 320, 320, 100, 100, Array.Empty<ushort>());
        var downed = MakeEnemy(10, 1, 200, 200, isDown: true);
        var standing = MakeEnemy(11, 2, 220, 220);

        bridge.Affinities[(standing, ElementId.Light)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(standing, ElementId.Wind)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(14, standing)] = 65.0;
        bridge.SingleHitDamages[(0, standing)] = 30.0;

        var dNoFakeAoa = engine.DecideAction(new BattleSnapshot
        {
            Actor = naoto,
            Allies = new() { protag, naoto },
            Enemies = new() { downed, standing }
        }, cfg)!;
        Assert.Equal((ushort)14, dNoFakeAoa.SkillId);

        // 2. Boss encounter with unanalyzed Dark affinity -> never casts Mudoon (73)
        var boss = MakeEnemy(276, 1, 2500, 2500);
        bridge.Affinities[(boss, ElementId.Dark)] = new AffinityInfo { IsRevealed = false, MultiplierPercent = 100 };
        bridge.Affinities[(boss, ElementId.Almighty)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(52, boss)] = 140.0;
        var naotoDark = MakeParty(7, TacticMode.FullAssault, 320, 320, 150, 150, new ushort[] { 73, 52 });
        var dBoss = engine.DecideAction(new BattleSnapshot
        {
            Actor = naotoDark,
            Allies = new() { protag, naotoDark },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)52, dBoss.SkillId);

        // 3. Tanky non-boss mob (400 HP) Weak to Light, resists Phys -> Samsara (67) or Hamaon wins cleanly by EV
        var mob = MakeEnemy(40, 1, 400, 400);
        bridge.Affinities[(mob, ElementId.Light)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(mob, ElementId.Phys)] = new AffinityInfo { IsResist = true, IsRevealed = true, MultiplierPercent = 50 };
        bridge.SingleHitDamages[(0, mob)] = 25.0;
        var dMob = engine.DecideAction(new BattleSnapshot
        {
            Actor = naoto,
            Allies = new() { naoto },
            Enemies = new() { mob }
        }, cfg)!;
        Assert.Equal((ushort)61, dMob.SkillId);
    }

    [Fact]
    public void AilmentCure_ExhaustionDetected_CriticalPriority_RePatraTargetsDownOnly()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var boss = MakeEnemy(99, 1, 1000, 1000);
        var exhaustedAlly = MakeParty(6, TacticMode.FullAssault, 500, 500, 80, 80, Array.Empty<ushort>(), stateFlags: 0x20); // Exhaustion (0.7x dmg!)
        var yukiko = MakeParty(4, TacticMode.HealSupport, 350, 350, 120, 120, new ushort[] { 208, 3 });

        var dExhaust = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko, exhaustedAlly },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)208, dExhaust.SkillId); // Amrita cures Exhaustion!

        // Poison (0x01) + Down (0x100000) with Re Patra (201, TargetCond=8): Re Patra must target Down ally, NOT poisoned ally!
        var poisonedAlly = MakeParty(2, TacticMode.ActFreely, 300, 300, 80, 80, Array.Empty<ushort>(), stateFlags: 0x01);
        var downedAlly = MakeParty(3, TacticMode.ActFreely, 300, 300, 80, 80, Array.Empty<ushort>(), stateFlags: 0x100000);
        var healer = MakeParty(4, TacticMode.HealSupport, 350, 350, 120, 120, new ushort[] { 201, 3 });
        var dRePatra = engine.DecideAction(new BattleSnapshot
        {
            Actor = healer,
            Allies = new() { healer, poisonedAlly, downedAlly },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)201, dRePatra.SkillId);
        Assert.Same(downedAlly, dRePatra.Target);

        // AoE Amrita covers both Confuse and Poison (cover == 2 > 1); with single-target Baisudi + Patra, Confuse (critical) wins
        var confusedAlly = MakeParty(1, TacticMode.DirectCommands, 350, 350, 80, 80, Array.Empty<ushort>(), stateFlags: 0x80);
        var curedAll = MakeParty(4, TacticMode.HealSupport, 350, 350, 120, 120, new ushort[] { 206, 208 });
        var dAmrita = engine.DecideAction(new BattleSnapshot
        {
            Actor = curedAll,
            Allies = new() { confusedAlly, poisonedAlly, curedAll },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)208, dAmrita.SkillId);

        var singleCure = MakeParty(4, TacticMode.HealSupport, 350, 350, 120, 120, new ushort[] { 199, 206 });
        var exhaustedOnly = MakeParty(2, TacticMode.ActFreely, 300, 300, 80, 80, Array.Empty<ushort>(), stateFlags: 0x02);
        var dCritFirst = engine.DecideAction(new BattleSnapshot
        {
            Actor = singleCure,
            Allies = new() { exhaustedOnly, confusedAlly, singleCure },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)206, dCritFirst.SkillId);
        Assert.Same(confusedAlly, dCritFirst.Target);
    }

    [Fact]
    public void ElementalWeaponBasicAttack_ExploitsWeaknessWithZeroSpAndTriggersOneMore()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.AttackElements[2] = ElementId.Ice; // Yosuke has an Ice elemental weapon!
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var yosuke = MakeParty(2, TacticMode.ConserveSp, 320, 320, 90, 90, new ushort[] { 14 });
        var pyroJack = MakeEnemy(45, 1, 250, 250);
        bridge.Affinities[(pyroJack, ElementId.Ice)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(pyroJack, ElementId.Wind)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(0, pyroJack)] = 65.0;
        bridge.SingleHitDamages[(14, pyroJack)] = 85.0;

        var d = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke },
            Enemies = new() { pyroJack }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, d.ActionKind);
        Assert.Same(pyroJack, d.Target);
    }

    [Fact]
    public void ConserveSp_AffordableLethalFinisherEndsFightInsteadOfBurningSpItem_AndRespectsSafeHpFloor()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[26] = new SkillMetadata { SkillId = 26, Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 98 };
        bridge.AvailableSpItems.Add(new SpRecoveryItemInfo { ItemId = 780, Name = "Snuff Soul", SkillId = 299, Quantity = 2, EstimatedSpGain = 50 });

        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Chie at 6/100 SP (6%), Attack does 35, Bufu (4 SP) does 135 (lethal on 125 HP enemy): finishes fight instead of wasting Snuff Soul!
        var chie = MakeParty(3, TacticMode.ConserveSp, 280, 280, 6, 100, new ushort[] { 26 });
        var mob = MakeEnemy(20, 1, 125, 200);
        bridge.Affinities[(mob, ElementId.Ice)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(0, mob)] = 35.0;
        bridge.SingleHitDamages[(26, mob)] = 135.0;

        var dFinisher = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie },
            Enemies = new() { mob }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dFinisher.ActionKind);
        Assert.Equal((ushort)26, dFinisher.SkillId);

        // 2. Chie at 38% HP (< ConserveSpSafeHpPercent 45%): refuses HP-cost Power Slash (122) and uses safe Normal Attack
        var lowHpChie = MakeParty(3, TacticMode.ConserveSp, 114, 300, 60, 100, new ushort[] { 122 });
        var healthy = MakeEnemy(21, 1, 400, 400);
        bridge.SingleHitDamages[(122, healthy)] = 160.0;
        bridge.SingleHitDamages[(0, healthy)] = 45.0;

        var dSafeHp = engine.DecideAction(new BattleSnapshot
        {
            Actor = lowHpChie,
            Allies = new() { lowHpChie },
            Enemies = new() { healthy }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, dSafeHp.ActionKind);
    }

    [Fact]
    public void FullAssault_AccuracyWeightAppliedUniformly_SingleHighAccBeatsLowAccAoEOnOneTarget()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // Brave Blade (Single, 175 dmg, acc=95 -> weight 1.0 = 175) vs Swift Strike (AoE, 90 * 2.0 = 180 raw, acc=75 -> weight 0.80 = 144)
        var chie = MakeParty(3, TacticMode.FullAssault, 400, 400, 100, 100, new ushort[] { 132, 172 });
        var enemy = MakeEnemy(15, 1, 600, 600);
        bridge.SingleHitDamages[(132, enemy)] = 175.0;
        bridge.SingleHitDamages[(172, enemy)] = 90.0;
        bridge.SingleHitDamages[(0, enemy)] = 40.0;

        var decision = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie },
            Enemies = new() { enemy }
        }, cfg)!;
        Assert.Equal((ushort)132, decision.SkillId);
    }

    [Fact]
    public void MultiEnemy_OneMoreChainsAcrossDifferentWeaknessesIntoAoa_AndBreakFallbackWorks()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // Chie + Protag: EnemyA Weak Fire (150 HP), EnemyB Weak Ice (150 HP). Agi(70) -> A + 1-More Bufu(70) -> B (+120 chain)!
        bridge.Skills[26] = new SkillMetadata { SkillId = 26, Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 98 };
        var protag = MakeParty(1, TacticMode.DirectCommands, 300, 300, 100, 100, Array.Empty<ushort>());
        var actor = MakeParty(4, TacticMode.FullAssault, 300, 300, 100, 100, new ushort[] { 1, 26, 3 });
        var eA = MakeEnemy(31, 1, 220, 220);
        var eB = MakeEnemy(32, 2, 220, 220);

        bridge.Affinities[(eA, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(eB, ElementId.Ice)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(1, eA)] = 70.0;
        bridge.SingleHitDamages[(26, eB)] = 70.0;
        bridge.SingleHitDamages[(3, eB)] = 110.0; // Agidyne on eB is non-weak 110, whereas Agi->eA chains to Bufu->eB

        var dChain = engine.DecideAction(new BattleSnapshot
        {
            Actor = actor,
            Allies = new() { protag, actor },
            Enemies = new() { eA, eB }
        }, cfg)!;
        Assert.Contains(dChain.SkillId, new ushort[] { 1, 26 });

        // Break Fallback: enemy Nulls Fire & Tetrakarn blocks Phys -> Yukiko casts Fire Break (236)!
        var yukiko = MakeParty(4, TacticMode.FullAssault, 300, 300, 100, 100, new ushort[] { 3, 236 });
        var nullEnemy = MakeEnemy(33, 1, 400, 400, tetrakarn: true);
        bridge.Affinities[(nullEnemy, ElementId.Fire)] = new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };
        var dBreak = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko },
            Enemies = new() { nullEnemy }
        }, cfg)!;
        Assert.Equal((ushort)236, dBreak.SkillId);
    }

    [Fact]
    public void Telegraphs_BossMatrixCoversMitsuoNaotoKuninoAmenoAndAllyWallSupport()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Shadow Yukiko passionate stare: Yosuke isn't Fire-weak, Chie IS Fire-weak -> Yosuke casts Red Wall (240) to protect Chie!
        var shadowYukiko = MakeEnemy(258, 1, 1500, 1500, waitMessageId: 202);
        var chieWeak = MakeParty(3, TacticMode.ActFreely, 280, 280, 80, 80, Array.Empty<ushort>());
        var yosukeSupport = MakeParty(2, TacticMode.ActFreely, 280, 280, 80, 80, new ushort[] { 14, 240 });
        bridge.Affinities[(chieWeak, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(14, shadowYukiko)] = 95.0;

        var dWall = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeSupport,
            Allies = new() { yosukeSupport, chieWeak },
            Enemies = new() { shadowYukiko },
            ActiveBattleBits = new HashSet<int> { 451 }
        }, cfg)!;
        Assert.Equal((ushort)240, dWall.SkillId);
        Assert.Same(chieWeak, dWall.Target);

        // 2. Shadow Mitsuo Whisper (181) / Ameno-sagiri active Wait telegraph -> Mandatory Guard
        var mitsuo = MakeEnemy(262, 1, 2200, 2200, waitMessageId: 181);
        var yukiko = MakeParty(4, TacticMode.FullAssault, 360, 360, 150, 150, new ushort[] { 3 });
        bridge.SingleHitDamages[(3, mitsuo)] = 150.0;
        var dMitsuo = engine.DecideAction(new BattleSnapshot { Actor = yukiko, Allies = new() { yukiko }, Enemies = new() { mitsuo } }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dMitsuo.ActionKind);

        var ameno = MakeEnemy(266, 1, 3000, 3000, waitMessageId: 202, lastActionType: 7);
        bridge.SingleHitDamages[(3, ameno)] = 150.0;
        var dAmeno = engine.DecideAction(new BattleSnapshot { Actor = yukiko, Allies = new() { yukiko }, Enemies = new() { ameno } }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dAmeno.ActionKind);

        // 3. Kunino-sagiri Quad Converge Fire (bit 453) -> Yukiko Guards? No, Yukiko nulls/neutral Fire, Chie (weak Fire) Guards!
        var kunino = MakeEnemy(264, 1, 3200, 3200);
        bridge.SingleHitDamages[(14, kunino)] = 90.0;
        var chieIce = MakeParty(3, TacticMode.ActFreely, 360, 360, 100, 100, new ushort[] { 14 });
        bridge.Affinities[(chieIce, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        var dKunino = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieIce,
            Allies = new() { chieIce },
            Enemies = new() { kunino },
            ActiveBattleBits = new HashSet<int> { 453 }
        }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dKunino.ActionKind);
    }

    [Fact]
    public void Soma_UsedInBossPartyEmergency_AndDisabledTacticsReturnNull()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.AvailableSpItems.Add(new SpRecoveryItemInfo
        {
            ItemId = 792, Name = "Soma", SkillId = 311, Quantity = 1, EstimatedSpGain = 200, EstimatedHpGain = 500, IsPartyWide = true, IsFullSp = true, IsFullHp = true
        });
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // Boss fight emergency (>=2 allies critical & actor critically low SP <= 15%) -> allows rare Soma cleanly
        var yukiko = MakeParty(4, TacticMode.HealSupport, 380, 400, 15, 200, new ushort[] { 193 });
        var protagCrit = MakeParty(1, TacticMode.DirectCommands, 120, 400, 10, 200, Array.Empty<ushort>());
        var kanjiCrit = MakeParty(6, TacticMode.FullAssault, 150, 600, 10, 120, Array.Empty<ushort>());
        var boss = MakeEnemy(99, 1, 2500, 2500);
        cfg.AllowRareFullSpItems = false;

        // First note HealSupport checks Heal before SP item; put Yukiko on ActFreely above emergency (protag at 35%? No, keep protag at 42% <= 45%)
        var protag42 = MakeParty(1, TacticMode.DirectCommands, 168, 400, 10, 200, Array.Empty<ushort>());
        var kanji42 = MakeParty(6, TacticMode.FullAssault, 252, 600, 10, 120, Array.Empty<ushort>());
        var yosuke = MakeParty(2, TacticMode.ActFreely, 350, 350, 15, 180, new ushort[] { 14 });
        bridge.SingleHitDamages[(14, boss)] = 90.0;
        var dSoma = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { protag42, yosuke, kanji42 },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dSoma.ActionKind);
        Assert.Equal((ushort)792, dSoma.ItemId);

        // Disabled tactic preset returns null so vanilla fallback runs
        cfg.EnableSmartFullAssault = false;
        var disabledActor = MakeParty(3, TacticMode.FullAssault, 300, 300, 100, 100, new ushort[] { 122 });
        Assert.Null(engine.DecideAction(new BattleSnapshot
        {
            Actor = disabledActor,
            Allies = new() { disabledActor },
            Enemies = new() { boss }
        }, cfg));
    }

    [Fact]
    public void FullAssault_MindChargeAndBarrierGuard_And_MakarakarnAllyProtection()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Yukiko with Mind Charge (228) + Agidyne (3) vs 1800 HP boss: chooses Mind Charge on Turn 1, then unleashes Agidyne on Turn 2
        var boss = MakeEnemy(99, 1, 1800, 1800);
        bridge.Affinities[(boss, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(3, boss)] = 200.0;

        var yukikoUncharged = MakeParty(4, TacticMode.FullAssault, 350, 350, 150, 150, new ushort[] { 228, 3 });
        var dCharge = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoUncharged,
            Allies = new() { yukikoUncharged },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)228, dCharge.SkillId);

        var yukikoCharged = MakeParty(4, TacticMode.FullAssault, 350, 350, 135, 150, new ushort[] { 228, 3 }, mindCharge: true);
        bridge.SingleHitDamages[(3, boss)] = 500.0; // Mind Charge 2.5x native output
        var dCast = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoCharged,
            Allies = new() { yukikoCharged },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)3, dCast.SkillId);

        // 2. Shadow Yukiko telegraph when actor lacks Red Wall but has Makarakarn (230): casts Makarakarn on Fire-weak ally!
        var shadowYukiko = MakeEnemy(258, 1, 1500, 1500, waitMessageId: 202);
        var chieWeak = MakeParty(3, TacticMode.ActFreely, 280, 280, 80, 80, Array.Empty<ushort>());
        var naotoKarns = MakeParty(7, TacticMode.ActFreely, 320, 320, 120, 120, new ushort[] { 52, 230 });
        bridge.Affinities[(chieWeak, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(52, shadowYukiko)] = 100.0;
        var dMakara = engine.DecideAction(new BattleSnapshot
        {
            Actor = naotoKarns,
            Allies = new() { naotoKarns, chieWeak },
            Enemies = new() { shadowYukiko },
            ActiveBattleBits = new HashSet<int> { 451 }
        }, cfg)!;
        Assert.Equal((ushort)230, dMakara.SkillId);
        Assert.Same(chieWeak, dMakara.Target);

        // 3. Enemy has BOTH Tetrakarn & Makarakarn up: party member with Phys + Agidyne safely Guards cleanly instead of self-reflecting
        var doubleKarn = MakeEnemy(50, 1, 500, 500, tetrakarn: true, makarakarn: true);
        bridge.SingleHitDamages[(3, doubleKarn)] = 160.0;
        bridge.SingleHitDamages[(122, doubleKarn)] = 120.0;
        bridge.SingleHitDamages[(0, doubleKarn)] = 40.0;
        var doomedAttacker = MakeParty(4, TacticMode.FullAssault, 320, 320, 120, 120, new ushort[] { 3, 122 });
        var dGuard = engine.DecideAction(new BattleSnapshot
        {
            Actor = doomedAttacker,
            Allies = new() { doomedAttacker },
            Enemies = new() { doubleKarn }
        }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dGuard.ActionKind);
    }

    [Fact]
    public void ValueOfSetup_3MemberBossVsShortFight_SetupWinsLongBossAndSkipsShortKill()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[3] = new SkillMetadata { SkillId = 3, Name = "Agidyne", Element = ElementId.Fire, StatType = 2, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 2, EffectType = 1, Power = 160 };
        bridge.Skills[27] = new SkillMetadata { SkillId = 27, Name = "Bufula", Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 8, Scope = 0, TargetSide = 2, EffectType = 1, Power = 100 };
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var teddie = MakeParty(8, TacticMode.FullAssault, 400, 400, 200, 200, new ushort[] { 27, 219, 213 });
        var protag = MakeParty(1, TacticMode.DirectCommands, 450, 450, 200, 200, new ushort[] { 3 });
        var kanjiCarry = MakeParty(6, TacticMode.FullAssault, 600, 600, 100, 100, new ushort[] { 138 });

        // 1. 8000 HP Boss (team unbuffed = 600 + 800 + 1600 = 3000/round -> ~2.67 rounds): Teddie sacrifices 600 to multiply team by +40%!
        var bigBoss = MakeEnemy(99, 1, 8000, 8000);
        bridge.ActorSingleHitDamages[(teddie, 27, bigBoss)] = 600.0;
        bridge.ActorSingleHitDamages[(protag, 3, bigBoss)] = 800.0;
        bridge.ActorSingleHitDamages[(kanjiCarry, 138, bigBoss)] = 1600.0;

        var dBoss = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, protag, kanjiCarry },
            Enemies = new() { bigBoss }
        }, cfg)!;
        Assert.True(dBoss.SkillId is 219 or 213); // Matarukaja or Rakunda!

        // Respects AllowOffensiveBuffsInFullAssault = false
        var cfgNoBuff = DefaultConfig();
        cfgNoBuff.AllowOffensiveBuffsInFullAssault = false;
        var dDirect = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, protag, kanjiCarry },
            Enemies = new() { bigBoss }
        }, cfgNoBuff)!;
        Assert.Equal((ushort)27, dDirect.SkillId);

        // 2. Short fight (2500 HP enemy with team dealing 3000/round -> dies in < 1 round): Act Freely skips Matarukaja and attacks!
        var shortMob = MakeEnemy(50, 1, 2500, 2500);
        bridge.ActorSingleHitDamages[(teddie, 27, shortMob)] = 600.0;
        bridge.ActorSingleHitDamages[(protag, 3, shortMob)] = 800.0;
        bridge.ActorSingleHitDamages[(kanjiCarry, 138, shortMob)] = 1600.0;

        var teddieFreely = MakeParty(8, TacticMode.ActFreely, 400, 400, 200, 200, new ushort[] { 27, 219, 213 });
        bridge.ActorSingleHitDamages[(teddieFreely, 27, shortMob)] = 600.0;

        var dShort = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddieFreely,
            Allies = new() { teddieFreely, protag, kanjiCarry },
            Enemies = new() { shortMob }
        }, cfg)!;
        Assert.Equal((ushort)27, dShort.SkillId);
    }

    [Fact]
    public void PowerCharge_DefersBuffToSupportAlly_NeverDelaysChargedAttack_And_TargetsStrongestCarry()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[27] = new SkillMetadata { SkillId = 27, Name = "Bufula", Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 8, Scope = 0, TargetSide = 2, EffectType = 1, Power = 100 };
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var boss = MakeEnemy(99, 1, 7000, 7000);
        var kanji = MakeParty(6, TacticMode.FullAssault, 600, 600, 150, 150, new ushort[] { 227, 219, 138 });
        var teddieSupport = MakeParty(8, TacticMode.ActFreely, 400, 400, 200, 200, new ushort[] { 219, 27 });
        var yukiko = MakeParty(4, TacticMode.ActFreely, 360, 360, 200, 200, new ushort[] { 3 });

        bridge.ActorSingleHitDamages[(kanji, 138, boss)] = 900.0;
        bridge.ActorSingleHitDamages[(teddieSupport, 27, boss)] = 350.0;
        bridge.ActorSingleHitDamages[(yukiko, 3, boss)] = 650.0;

        // 1. Uncharged Kanji (has Power Charge + Matarukaja) sees lower-DPS AI Teddie also has Matarukaja -> defers buff and Charges Turn 1!
        var dKanjiCharge = engine.DecideAction(new BattleSnapshot
        {
            Actor = kanji,
            Allies = new() { kanji, teddieSupport, yukiko },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)227, dKanjiCharge.SkillId);

        // 2. Teddie (lower DPS support) does cast Matarukaja Turn 1 while Kanji prepares Charge!
        var dTeddieBuff = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddieSupport,
            Allies = new() { kanji, teddieSupport, yukiko },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)219, dTeddieBuff.SkillId);

        // 3. Already-Charged Kanji (on Act Freely OR Full Assault) NEVER delays his 2.5x Charged God's Hand to cast a buff!
        var kanjiCharged = MakeParty(6, TacticMode.ActFreely, 600, 600, 135, 150, new ushort[] { 227, 219, 138 }, powerCharge: true);
        bridge.ActorSingleHitDamages[(kanjiCharged, 138, boss)] = 2250.0;
        var dUnleash = engine.DecideAction(new BattleSnapshot
        {
            Actor = kanjiCharged,
            Allies = new() { kanjiCharged, teddieSupport, yukiko },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)138, dUnleash.SkillId);

        // 4. Single-target Tarukaja targets strongest unbuffed DPS carry (Charged Kanji 2250 > weak Protag 200)
        var weakProtag = MakeParty(1, TacticMode.DirectCommands, 350, 350, 100, 100, Array.Empty<ushort>());
        var healerTarukaja = MakeParty(4, TacticMode.HealSupport, 360, 360, 180, 180, new ushort[] { 218, 3 });
        bridge.ActorSingleHitDamages[(weakProtag, 0, boss)] = 200.0;
        bridge.ActorSingleHitDamages[(healerTarukaja, 3, boss)] = 350.0;

        var dTarukaja = engine.DecideAction(new BattleSnapshot
        {
            Actor = healerTarukaja,
            Allies = new() { weakProtag, kanjiCharged, healerTarukaja },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)218, dTarukaja.SkillId);
        Assert.Same(kanjiCharged, dTarukaja.Target);
    }

    [Fact]
    public void ValueOfSetup_MarakundaSelectedOnMultipleToughEnemies()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var bossA = MakeEnemy(99, 1, 3200, 3200);
        var bossB = MakeEnemy(98, 2, 2800, 2800);
        var naoto = MakeParty(7, TacticMode.FullAssault, 360, 360, 220, 220, new ushort[] { 53 });
        var kanji = MakeParty(6, TacticMode.FullAssault, 600, 600, 120, 120, new ushort[] { 133 });
        var teddie = MakeParty(8, TacticMode.ActFreely, 380, 380, 180, 180, new ushort[] { 213, 214, 1 });

        bridge.ActorSingleHitDamages[(naoto, 53, bossA)] = 450.0;
        bridge.ActorSingleHitDamages[(naoto, 53, bossB)] = 450.0;
        bridge.ActorSingleHitDamages[(kanji, 133, bossA)] = 220.0; // Blade of Fury avg 3 hits = 660
        bridge.ActorSingleHitDamages[(kanji, 133, bossB)] = 220.0;
        bridge.ActorSingleHitDamages[(teddie, 1, bossA)] = 200.0;

        var dMarakunda = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { naoto, kanji, teddie },
            Enemies = new() { bossA, bossB }
        }, cfg)!;
        Assert.Equal((ushort)214, dMarakunda.SkillId); // Marakunda AoE beats single-target Rakunda!
    }

    [Fact]
    public void UnanalyzedEnemies_ProbesUnrevealedElementOncePhysRevealed_SkipsBossAndChargedTurns()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[26] = new SkillMetadata { SkillId = 26, Name = "Bufu", Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 40 };
        bridge.Skills[28] = new SkillMetadata { SkillId = 28, Name = "Bufudyne", Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 2, EffectType = 1, Power = 160 };
        bridge.Skills[41] = new SkillMetadata { SkillId = 41, Name = "Ziodyne", Element = ElementId.Elec, StatType = 2, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 2, EffectType = 1, Power = 160 };
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig(respectAnalysis: true);

        // 1. Regular 450 HP shadow: Elec (41) already revealed Neutral (150), Ice (28) unrevealed (145) vs Megidolaon (53, 165).
        // Naoto probes unrevealed Ice (28) instead of spamming revealed Ziodyne or burning Megidolaon!
        var shadow = MakeEnemy(15, 1, 450, 450);
        bridge.Affinities[(shadow, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(shadow, ElementId.Elec)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(shadow, ElementId.Ice)] = new AffinityInfo { IsRevealed = false, MultiplierPercent = 100 };
        bridge.Affinities[(shadow, ElementId.Almighty)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(41, shadow)] = 150.0;
        bridge.SingleHitDamages[(28, shadow)] = 145.0;
        bridge.SingleHitDamages[(53, shadow)] = 165.0;
        bridge.SingleHitDamages[(0, shadow)] = 40.0;

        var naoto = MakeParty(7, TacticMode.FullAssault, 340, 340, 200, 200, new ushort[] { 41, 28, 53 });
        var dProbe = engine.DecideAction(new BattleSnapshot
        {
            Actor = naoto,
            Allies = new() { naoto },
            Enemies = new() { shadow }
        }, cfg)!;
        Assert.Equal((ushort)28, dProbe.SkillId);

        // Respects ProbeUnrevealedElements = false toggle: FullAssault picks highest raw damage (Megidolaon 53) regardless of SP/overkill;
        // ActFreely respects AoE SP economy on single targets and chooses revealed Ziodyne (41).
        var cfgNoProbe = DefaultConfig(respectAnalysis: true);
        cfgNoProbe.ProbeUnrevealedElements = false;
        var dNoProbe = engine.DecideAction(new BattleSnapshot
        {
            Actor = naoto,
            Allies = new() { naoto },
            Enemies = new() { shadow }
        }, cfgNoProbe)!;
        Assert.Equal((ushort)53, dNoProbe.SkillId);

        var naotoActFreely = MakeParty(7, TacticMode.ActFreely, 340, 340, 200, 200, new ushort[] { 41, 28, 53 });
        var dActFreelyNoProbe = engine.DecideAction(new BattleSnapshot
        {
            Actor = naotoActFreely,
            Allies = new() { naotoActFreely },
            Enemies = new() { shadow }
        }, cfgNoProbe)!;
        Assert.Equal((ushort)41, dActFreelyNoProbe.SkillId);

        // Once Mind Charged, Naoto must NOT risk 2.5x Charge on unrevealed Bufudyne (28); picks guaranteed revealed skill!
        var naotoCharged = MakeParty(7, TacticMode.FullAssault, 340, 340, 200, 200, new ushort[] { 41, 28, 53 }, mindCharge: true);
        bridge.SingleHitDamages[(41, shadow)] = 375.0;
        bridge.SingleHitDamages[(28, shadow)] = 362.5;
        bridge.SingleHitDamages[(53, shadow)] = 412.5;
        var dCharged = engine.DecideAction(new BattleSnapshot
        {
            Actor = naotoCharged,
            Allies = new() { naotoCharged },
            Enemies = new() { shadow }
        }, cfg)!;
        Assert.Equal((ushort)53, dCharged.SkillId);

        // Against a 3000 HP Boss: does NOT downgrade to unrevealed weak spell solely to fish for weakness
        var boss = MakeEnemy(99, 1, 3000, 3000);
        bridge.Affinities[(boss, ElementId.Elec)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(boss, ElementId.Ice)] = new AffinityInfo { IsRevealed = false, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(41, boss)] = 150.0;
        bridge.SingleHitDamages[(28, boss)] = 145.0;
        var naotoBoss = MakeParty(7, TacticMode.FullAssault, 340, 340, 200, 200, new ushort[] { 41, 28 });
        var dBoss = engine.DecideAction(new BattleSnapshot
        {
            Actor = naotoBoss,
            Allies = new() { naotoBoss },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)41, dBoss.SkillId);

        // 2. Conserve SP on healthy 70% SP probes unrevealed cheap Agi (1, 4 SP) on regular shadow when Normal Attack is revealed Neutral
        var regShadow = MakeEnemy(16, 1, 320, 320);
        bridge.Affinities[(regShadow, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(regShadow, ElementId.Fire)] = new AffinityInfo { IsRevealed = false, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(0, regShadow)] = 65.0;
        bridge.SingleHitDamages[(1, regShadow)] = 60.0;
        var yukikoConserve = MakeParty(4, TacticMode.ConserveSp, 300, 300, 140, 200, new ushort[] { 1 });

        var dConserveProbe = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoConserve,
            Allies = new() { yukikoConserve },
            Enemies = new() { regShadow }
        }, cfg)!;
        Assert.Equal((ushort)1, dConserveProbe.SkillId);

        // Once Fire IS revealed Neutral (not weak), Conserve SP returns to 0-SP Normal Attack
        bridge.Affinities[(regShadow, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        var dConserveAfter = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoConserve,
            Allies = new() { yukikoConserve },
            Enemies = new() { regShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, dConserveAfter.ActionKind);
    }

    [Fact]
    public void ActFreely_SelectiveAilmentCure_IgnoresLowValue_CuresLethalSabotageChargedCarryAndMcAoaBlocker()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[208] = new SkillMetadata
        {
            SkillId = 208,
            Name = "Amrita",
            Element = ElementId.Recovery,
            CostType = 2,
            BaseCost = 12,
            Scope = 1,
            TargetSide = 1,
            EffectType = 11,
            AilmentMask = 0x1FF
        };
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var boss = MakeEnemy(99, 1, 900, 900);
        bridge.Affinities[(boss, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(boss, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        // 1. Healthy Chie with Poison (0x20, 260/320 HP = 81%): ActFreely Yukiko does NOT waste turn curing minor Poison -> casts Agidyne!
        // Also confirms StateFlags = 0x20 is Poison (HasPoison == true, HasSilenced == false), while 0x08 is Silence!
        var chieHealthyPoison = MakeParty(3, TacticMode.FullAssault, 260, 320, 100, 100, new ushort[] { 122 }, stateFlags: 0x20);
        Assert.True(chieHealthyPoison.HasPoison);
        Assert.False(chieHealthyPoison.HasSilenced);
        var yukiko = MakeParty(4, TacticMode.ActFreely, 320, 320, 180, 180, new ushort[] { 3, 208 });
        bridge.ActorSingleHitDamages[(yukiko, 3, boss)] = 280.0;
        bridge.ActorSingleHitDamages[(chieHealthyPoison, 122, boss)] = 160.0;

        var dHealthyPoison = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko, chieHealthyPoison },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chieHealthyPoison, boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dHealthyPoison.ActionKind);
        Assert.Equal((ushort)3, dHealthyPoison.SkillId);

        // 2. Low-HP Poison (40/320 HP <= lethal threshold): imminent 1 HP Poison bleed -> Yukiko cures with Amrita!
        var chieDyingPoison = MakeParty(3, TacticMode.FullAssault, 40, 320, 100, 100, new ushort[] { 122 }, stateFlags: 0x20);
        var dLethalPoison = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko, chieDyingPoison },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chieDyingPoison, boss }
        }, cfg)!;
        Assert.Equal((ushort)208, dLethalPoison.SkillId);

        // 3. Confuse (0x80) on carry who still acts this round -> lifesaving cure against self/boss sabotage!
        var kanjiConfused = MakeParty(6, TacticMode.FullAssault, 500, 500, 100, 100, new ushort[] { 138 }, stateFlags: 0x80);
        var dConfuse = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko, kanjiConfused },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiConfused, boss }
        }, cfg)!;
        Assert.Equal((ushort)208, dConfuse.SkillId);

        // 4. Silenced (0x08) Power-Charged Kanji sitting NEXT in UpcomingRoundTurnOrder -> Yukiko cures so Charged God's Hand fires!
        //    When Kanji ALREADY acted this round (not in UpcomingRoundTurnOrder), discounted future gain loses to Yukiko's Agidyne!
        var kanjiSilencedCharged = MakeParty(6, TacticMode.FullAssault, 550, 550, 120, 120, new ushort[] { 138 }, stateFlags: 0x08, powerCharge: true);
        Assert.True(kanjiSilencedCharged.HasSilenced);
        bridge.ActorSingleHitDamages[(kanjiSilencedCharged, 138, boss)] = 350.0;
        bridge.ActorSingleHitDamages[(yukiko, 3, boss)] = 150.0;

        var dBeforeTurn = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko, kanjiSilencedCharged },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiSilencedCharged, boss }
        }, cfg)!;
        Assert.Equal((ushort)208, dBeforeTurn.SkillId);

        var dAlreadyActed = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko, kanjiSilencedCharged },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { boss }
        }, cfg)!;
        Assert.Equal((ushort)3, dAlreadyActed.SkillId);

        // 5. All enemies are Down, but Protagonist is Down (0x100000) or Enraged (0x02) blocking AOA -> Yukiko cures MC to unlock AOA!
        bridge.Skills[204] = new SkillMetadata
        {
            SkillId = 204,
            Name = "Re Patra",
            Element = ElementId.Recovery,
            CostType = 2,
            BaseCost = 3,
            Scope = 0,
            TargetSide = 1,
            TargetCond = 8,
            AilmentMask = 0x140001
        };
        var yukikoWithRePatra = MakeParty(4, TacticMode.ActFreely, 320, 320, 180, 180, new ushort[] { 3, 208, 204 });
        var protagDown = MakeParty(1, TacticMode.DirectCommands, 300, 350, 100, 100, Array.Empty<ushort>(), stateFlags: 0x100000);
        var downedShadow = MakeEnemy(20, 1, 220, 350, isDown: true);
        bridge.ActorSingleHitDamages[(yukikoWithRePatra, 3, downedShadow)] = 110.0;

        var dMcDown = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoWithRePatra,
            Allies = new() { protagDown, yukikoWithRePatra },
            Enemies = new() { downedShadow }
        }, cfg)!;
        Assert.Equal((ushort)204, dMcDown.SkillId);
        Assert.Same(protagDown, dMcDown.Target);

        var protagEnraged = MakeParty(1, TacticMode.DirectCommands, 300, 350, 100, 100, Array.Empty<ushort>(), stateFlags: 0x02);
        var dMcEnraged = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoWithRePatra,
            Allies = new() { protagEnraged, yukikoWithRePatra },
            Enemies = new() { downedShadow }
        }, cfg)!;
        Assert.Equal((ushort)208, dMcEnraged.SkillId);
    }

    [Fact]
    public void Consumables_DamageItemsUnlockOneMoreAndAoa_BypassSilence_AndRespectRarityAndProbing()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig(respectAnalysis: true);

        var pinwheelSkill = new SkillMetadata
        {
            SkillId = 401,
            Name = "Pinwheel",
            Element = ElementId.Wind,
            StatType = 0,
            CostType = 0,
            Scope = 0,
            TargetSide = 2,
            EffectType = 4, // Fixed-power item damage verified in P4G.exe CalcSkillDamage
            Power = 50,
            Accuracy = 100
        };
        var bombItem = new BattleConsumableInfo
        {
            ItemId = 601,
            Name = "Pinwheel",
            SkillId = 401,
            Quantity = 3,
            Kind = BattleConsumableKind.Damage,
            Rarity = ConsumableRarityTier.Common,
            Skill = pinwheelSkill
        };
        var rareMegidoGem = new BattleConsumableInfo
        {
            ItemId = 699,
            Name = "Bead of Judgement",
            SkillId = 499,
            Quantity = 1,
            Kind = BattleConsumableKind.Damage,
            Rarity = ConsumableRarityTier.Rare,
            Skill = new SkillMetadata
            {
                SkillId = 499,
                Name = "Megidolaon Gem",
                Element = ElementId.Almighty,
                CostType = 0,
                Scope = 1,
                TargetSide = 2,
                EffectType = 4,
                Power = 400
            }
        };
        bridge.Skills[401] = pinwheelSkill;
        bridge.Skills[499] = rareMegidoGem.Skill;
        bridge.AvailableConsumables.AddRange(new[] { bombItem, rareMegidoGem });

        // 1. Wind-weak standing enemy + already-Down enemy: Fire-only Yukiko throws Pinwheel (601) to knock down & trigger AOA!
        var protag = MakeParty(1, TacticMode.DirectCommands, 300, 300, 100, 100, Array.Empty<ushort>());
        var yukiko = MakeParty(4, TacticMode.ActFreely, 300, 300, 150, 150, new ushort[] { 3 });
        var downed = MakeEnemy(10, 1, 200, 250, isDown: true);
        var windWeak = MakeEnemy(11, 2, 260, 260);
        bridge.Affinities[(windWeak, ElementId.Wind)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(windWeak, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(401, windWeak)] = 65.0;
        bridge.SingleHitDamages[(3, windWeak)] = 130.0;
        bridge.SingleHitDamages[(3, downed)] = 130.0;
        bridge.SingleHitDamages[(499, windWeak)] = 400.0;
        bridge.SingleHitDamages[(499, downed)] = 400.0;

        var dAoaItem = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { protag, yukiko },
            Enemies = new() { downed, windWeak }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dAoaItem.ActionKind);
        Assert.Equal((ushort)601, dAoaItem.ItemId); // Common Pinwheel wins; Rare item blocked!

        // 2. Solo ActFreely Yukiko: Pinwheel(65 Weak) -> 1-More Agidyne(140) = 205 > solo Agidyne(140) -> throws Pinwheel!
        //    When Yukiko ALSO knows Garu (14, 70 dmg Weak), repeatable skill beats finite Pinwheel!
        var soloShadow = MakeEnemy(12, 1, 320, 320);
        bridge.Affinities[(soloShadow, ElementId.Wind)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(soloShadow, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(401, soloShadow)] = 65.0;
        bridge.SingleHitDamages[(3, soloShadow)] = 140.0;
        bridge.SingleHitDamages[(14, soloShadow)] = 70.0;

        var dCombo = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko },
            Enemies = new() { soloShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dCombo.ActionKind);
        Assert.Equal((ushort)601, dCombo.ItemId);

        var yukikoWithGaru = MakeParty(4, TacticMode.ActFreely, 300, 300, 150, 150, new ushort[] { 3, 14 });
        var dSkillWins = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoWithGaru,
            Allies = new() { yukikoWithGaru },
            Enemies = new() { soloShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dSkillWins.ActionKind);
        Assert.Equal((ushort)14, dSkillWins.SkillId);

        // 3. Unrevealed Wind affinity: AllowDamageItemProbing = false prevents blind item probe!
        var hiddenShadow = MakeEnemy(13, 1, 320, 320);
        bridge.Affinities[(hiddenShadow, ElementId.Wind)] = new AffinityInfo { IsRevealed = false, MultiplierPercent = 125, IsWeak = true };
        bridge.Affinities[(hiddenShadow, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.SingleHitDamages[(401, hiddenShadow)] = 65.0;
        bridge.SingleHitDamages[(3, hiddenShadow)] = 140.0;

        var dNoItemProbe = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko },
            Enemies = new() { hiddenShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dNoItemProbe.ActionKind);
        Assert.Equal((ushort)3, dNoItemProbe.SkillId);

        // 4. MinConsumableReserveCount = 3 protects a 3-count stack completely!
        var cfgReserve = DefaultConfig(respectAnalysis: true);
        cfgReserve.MinConsumableReserveCount = 3;
        var dReserved = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko },
            Enemies = new() { soloShadow }
        }, cfgReserve)!;
        Assert.Equal(AiActionKind.Skill, dReserved.ActionKind);

        // 5. Silenced (0x08) Yukiko cannot cast Mediarama or Agidyne -> uses Medicine/Pinwheel via Item bypass!
        var medSkill = new SkillMetadata
        {
            SkillId = 305,
            Name = "Medicine",
            Element = ElementId.Recovery,
            CostType = 0,
            Scope = 0,
            TargetSide = 1,
            EffectType = 2,
            Power = 120
        };
        bridge.Skills[305] = medSkill;
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 650,
            Name = "Medicine",
            SkillId = 305,
            Quantity = 5,
            Kind = BattleConsumableKind.Heal,
            Rarity = ConsumableRarityTier.Common,
            Skill = medSkill,
            EstimatedHpGain = 120
        });

        var yukikoSilenced = MakeParty(4, TacticMode.HealSupport, 280, 320, 150, 150, new ushort[] { 196, 3 }, stateFlags: 0x08);
        var injuredAlly = MakeParty(2, TacticMode.ActFreely, 80, 320, 80, 80, Array.Empty<ushort>());
        var dSilenceHeal = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoSilenced,
            Allies = new() { yukikoSilenced, injuredAlly },
            Enemies = new() { soloShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dSilenceHeal.ActionKind);
        Assert.Equal((ushort)650, dSilenceHeal.ItemId);
        Assert.Same(injuredAlly, dSilenceHeal.Target);

        var dSilenceBomb = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoSilenced,
            Allies = new() { yukikoSilenced },
            Enemies = new() { soloShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dSilenceBomb.ActionKind);
        Assert.Equal((ushort)601, dSilenceBomb.ItemId);
    }

    [Fact]
    public void ActFreely_LeastTurnsPlanner_FinishBeatsRevive_TurnOrderScalesBuffs_AndChargedDefersHeal()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[27] = new SkillMetadata { SkillId = 27, Name = "Bufula", Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 8, Scope = 0, TargetSide = 2, EffectType = 1, Power = 100 };
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. PreferLeastTurnsWin = true finishes the last 80-HP enemy immediately instead of spending a turn reviving KO ally;
        //    toggling PreferLeastTurnsWin = false preserves EXP by reviving first!
        var koAlly = MakeParty(2, TacticMode.ActFreely, 0, 300, 50, 100, Array.Empty<ushort>(), stateFlags: 0x80000);
        var yukiko = MakeParty(4, TacticMode.ActFreely, 320, 320, 150, 150, new ushort[] { 3, 209 });
        var nearDeadBoss = MakeEnemy(50, 1, 80, 1000);
        bridge.SingleHitDamages[(3, nearDeadBoss)] = 150.0;

        var dFinishFirst = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko, koAlly },
            Enemies = new() { nearDeadBoss }
        }, cfg)!;
        Assert.Equal((ushort)3, dFinishFirst.SkillId);

        var cfgExpSafe = DefaultConfig();
        cfgExpSafe.PreferLeastTurnsWin = false;
        var dReviveFirst = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko, koAlly },
            Enemies = new() { nearDeadBoss }
        }, cfgExpSafe)!;
        Assert.Equal((ushort)209, dReviveFirst.SkillId);

        // 2. Turn-order buff timing on medium boss (2600 HP; team unbuffed = 2200/round -> ~1.18 rounds):
        //    When carries Kanji & Chie STILL act later this round -> Matarukaja (219) boosts both THIS round!
        //    When carries ALREADY acted this round -> < 1 full round left -> Teddie skips buff and casts Bufula (27)!
        var boss = MakeEnemy(99, 1, 2600, 2600);
        var teddie = MakeParty(8, TacticMode.ActFreely, 360, 360, 160, 160, new ushort[] { 27, 219 });
        var chie = MakeParty(3, TacticMode.ActFreely, 400, 400, 120, 120, new ushort[] { 138 });
        var kanji = MakeParty(6, TacticMode.FullAssault, 500, 500, 120, 120, new ushort[] { 138 });
        bridge.ActorSingleHitDamages[(teddie, 27, boss)] = 300.0;
        bridge.ActorSingleHitDamages[(chie, 138, boss)] = 900.0;
        bridge.ActorSingleHitDamages[(kanji, 138, boss)] = 1000.0;

        var dBuffEarlyRound = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, chie, kanji },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chie, kanji, boss }
        }, cfg)!;
        Assert.Equal((ushort)219, dBuffEarlyRound.SkillId);

        var dAttackLateRound = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, chie, kanji },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { boss }
        }, cfg)!;
        Assert.Equal((ushort)27, dAttackLateRound.SkillId);

        // 3. Charged carry defers non-lethal emergency heal (28% HP ally; <40% emergency, >=20% lethal floor)
        //    when HealSupport Yukiko acts BEFORE boss in UpcomingRoundTurnOrder -> Kanji unleashes God's Hand (138)!
        //    When boss acts BEFORE Yukiko -> Kanji heals safely!
        var kanjiCharged = MakeParty(6, TacticMode.ActFreely, 500, 500, 120, 120, new ushort[] { 138 }, powerCharge: true);
        var yosukeLow = MakeParty(2, TacticMode.ActFreely, 90, 300, 80, 100, Array.Empty<ushort>()); // 30% HP
        var yukikoHealer = MakeParty(4, TacticMode.HealSupport, 320, 320, 160, 160, new ushort[] { 193, 196 });
        bridge.ActorSingleHitDamages[(kanjiCharged, 138, boss)] = 1400.0;

        // Give kanjiCharged an emergency heal skill or item so we prove he actively defers healing
        var medSkill = bridge.Skills[305] = new SkillMetadata { SkillId = 305, Name = "Medicine", Element = ElementId.Recovery, Scope = 0, TargetSide = 1, EffectType = 2, Power = 120 };
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 650,
            Name = "Medicine",
            SkillId = 305,
            Quantity = 4,
            Kind = BattleConsumableKind.Heal,
            Rarity = ConsumableRarityTier.Common,
            Skill = medSkill,
            EstimatedHpGain = 120
        });

        var dDeferToYukiko = engine.DecideAction(new BattleSnapshot
        {
            Actor = kanjiCharged,
            Allies = new() { kanjiCharged, yosukeLow, yukikoHealer },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { yukikoHealer, boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dDeferToYukiko.ActionKind);
        Assert.Equal((ushort)138, dDeferToYukiko.SkillId);

        var dBossBeforeYukiko = engine.DecideAction(new BattleSnapshot
        {
            Actor = kanjiCharged,
            Allies = new() { kanjiCharged, yosukeLow, yukikoHealer },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { boss, yukikoHealer }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dBossBeforeYukiko.ActionKind);
        Assert.Equal((ushort)650, dBossBeforeYukiko.ItemId);
        Assert.Same(yosukeLow, dBossBeforeYukiko.Target);
    }

    [Fact]
    public void Bathhouse7F_DaringGigas_CounterFlagAndStaleBit466_DoesNotBrickIntoGuard()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // Daring Gigas (311, Bathhouse 7F mini-boss) carries Counter -> BaseFlags & 0x100 (HasEnemyCounter) is set,
        // and idle BattleBit 466 (Kanji AOA bit) or non-Marie WaitMessageId 209 must never brick the party into Guard.
        var gigas = MakeEnemy(311, 0, 928, 928, enemyCounter: true, waitMessageId: 209);
        var chie = MakeParty(3, TacticMode.ActFreely, 240, 240, 90, 90, new ushort[] { 122 });
        var yukiko = MakeParty(4, TacticMode.ActFreely, 210, 210, 140, 140, new ushort[] { 3, 193 });
        var yosuke = MakeParty(2, TacticMode.ActFreely, 230, 230, 110, 110, new ushort[] { 14, 122 });

        foreach (var elem in new[] { ElementId.Phys, ElementId.Fire, ElementId.Wind })
            bridge.Affinities[(gigas, elem)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        bridge.ActorSingleHitDamages[(chie, 122, gigas)] = 150.0;
        bridge.ActorSingleHitDamages[(chie, 0, gigas)] = 65.0;
        bridge.ActorSingleHitDamages[(yukiko, 3, gigas)] = 190.0;
        bridge.ActorSingleHitDamages[(yosuke, 14, gigas)] = 150.0;
        bridge.ActorSingleHitDamages[(yosuke, 122, gigas)] = 150.0;

        var battleBits = new HashSet<int> { 466 };

        var dChie = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie, yukiko, yosuke },
            Enemies = new() { gigas },
            ActiveBattleBits = battleBits
        }, cfg)!;
        Assert.NotEqual(AiActionKind.Guard, dChie.ActionKind);
        Assert.Equal((ushort)122, dChie.SkillId); // Phys-only attacker still strikes normally

        var dYukiko = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { chie, yukiko, yosuke },
            Enemies = new() { gigas },
            ActiveBattleBits = battleBits
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dYukiko.ActionKind);
        Assert.Equal((ushort)3, dYukiko.SkillId);

        // Hybrid attacker with equal Magic vs Phys prefers Magic over Counter's 10% Phys counter risk
        var dYosuke = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { chie, yukiko, yosuke },
            Enemies = new() { gigas },
            ActiveBattleBits = battleBits
        }, cfg)!;
        Assert.Equal((ushort)14, dYosuke.SkillId);

        // Actual Kusumi-no-Okami (277) WaitMessageId 209 telegraph must still trigger Guard!
        var marie = MakeEnemy(277, 0, 2000, 3000, waitMessageId: 209);
        bridge.Affinities[(marie, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.ActorSingleHitDamages[(chie, 122, marie)] = 150.0;
        var dMarieGuard = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie, yukiko, yosuke },
            Enemies = new() { marie }
        }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dMarieGuard.ActionKind);
    }

    [Fact]
    public void Rebellion_BuffsMultiHitPhysCarryForCritOneMore_SkipsWhenActiveOrShortFight()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var shadow1 = MakeEnemy(10, 0, 420, 420); // <500 => eligible for crit knockdown + 1 More
        var shadow2 = MakeEnemy(11, 1, 420, 420);
        foreach (var e in new[] { shadow1, shadow2 })
        {
            bridge.Affinities[(e, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
            bridge.Affinities[(e, ElementId.Ice)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        }

        // Atk/Def already in place or support only has Rebellion + single-target Ice; Chie uses 3-hit Blade of Fury
        var teddie = MakeParty(7, TacticMode.ActFreely, 320, 320, 120, 120, new ushort[] { 234, 26 });
        var chie = MakeParty(3, TacticMode.ActFreely, 320, 320, 100, 100, new ushort[] { 133 }, atkStage: 1);
        var kanji = MakeParty(6, TacticMode.ActFreely, 360, 360, 100, 100, new ushort[] { 138 }, atkStage: 1);

        bridge.ActorSingleHitDamages[(teddie, 26, shadow1)] = 55.0;
        bridge.ActorSingleHitDamages[(teddie, 26, shadow2)] = 55.0;
        bridge.ActorSingleHitDamages[(chie, 133, shadow1)] = 45.0; // *3 avg hits = 135/enemy
        bridge.ActorSingleHitDamages[(chie, 133, shadow2)] = 45.0;
        bridge.ActorSingleHitDamages[(kanji, 138, shadow1)] = 150.0;
        bridge.ActorSingleHitDamages[(kanji, 138, shadow2)] = 150.0;

        var dRebellion = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, chie, kanji },
            Enemies = new() { shadow1, shadow2 },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chie, kanji, shadow1, shadow2 }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dRebellion.ActionKind);
        Assert.Equal((ushort)234, dRebellion.SkillId);
        Assert.Same(chie, dRebellion.Target);

        // Once Chie has CritRateStage > 0, don't recast Rebellion when Kanji gain is tiny (< opportunity cost)
        var chieBuffed = MakeParty(3, TacticMode.ActFreely, 320, 320, 100, 100, new ushort[] { 133 }, atkStage: 1, critRateStage: 1);
        var dSkipRecast = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, chieBuffed, kanji },
            Enemies = new() { shadow1, shadow2 },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chieBuffed, kanji, shadow1, shadow2 }
        }, cfg)!;
        Assert.Equal((ushort)26, dSkipRecast.SkillId);

        // Short 1-turn lethal fight: never waste turn on Rebellion
        var lowShadow = MakeEnemy(10, 0, 50, 420);
        bridge.Affinities[(lowShadow, ElementId.Ice)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.ActorSingleHitDamages[(teddie, 26, lowShadow)] = 55.0;
        var dFinish = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, chie, kanji },
            Enemies = new() { lowShadow }
        }, cfg)!;
        Assert.Equal((ushort)26, dFinish.SkillId);
    }

    [Fact]
    public void Revolution_CastsForPhysPartyWhenSafe_VetoesAgainstPowerChargedEnemy()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var shadow1 = MakeEnemy(12, 0, 450, 450);
        var shadow2 = MakeEnemy(13, 1, 450, 450);
        foreach (var e in new[] { shadow1, shadow2 })
        {
            bridge.Affinities[(e, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
            bridge.Affinities[(e, ElementId.Ice)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        }

        // Healthy Phys-heavy party -> Teddie casts Revolution (235) for +15% crit & knockdown chains across Chie + Kanji + Yu
        var teddie = MakeParty(7, TacticMode.ActFreely, 350, 350, 120, 120, new ushort[] { 235, 26 });
        var chie = MakeParty(3, TacticMode.ActFreely, 350, 350, 100, 100, new ushort[] { 133 });
        var kanji = MakeParty(6, TacticMode.ActFreely, 380, 380, 100, 100, new ushort[] { 138 });
        var protag = MakeParty(1, TacticMode.DirectCommands, 360, 360, 120, 120, new ushort[] { 132 });

        bridge.ActorSingleHitDamages[(teddie, 26, shadow1)] = 55.0;
        bridge.ActorSingleHitDamages[(teddie, 26, shadow2)] = 55.0;
        bridge.ActorSingleHitDamages[(chie, 133, shadow1)] = 45.0;
        bridge.ActorSingleHitDamages[(chie, 133, shadow2)] = 45.0;
        bridge.ActorSingleHitDamages[(kanji, 138, shadow1)] = 160.0;
        bridge.ActorSingleHitDamages[(protag, 132, shadow1)] = 150.0;

        var dRev = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, chie, kanji, protag },
            Enemies = new() { shadow1, shadow2 },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chie, kanji, protag, shadow1, shadow2 }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dRev.ActionKind);
        Assert.Equal((ushort)235, dRev.SkillId);

        // If any standing enemy holds Power Charge while party lacks Tetrakarn/ShieldOfJustice, Revolution must be vetoed!
        var chargedBoss = MakeEnemy(200, 0, 2200, 2200, powerCharge: true);
        bridge.Affinities[(chargedBoss, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(chargedBoss, ElementId.Ice)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.ActorSingleHitDamages[(teddie, 26, chargedBoss)] = 55.0;
        bridge.ActorSingleHitDamages[(chie, 133, chargedBoss)] = 140.0;
        bridge.ActorSingleHitDamages[(kanji, 138, chargedBoss)] = 350.0;
        bridge.ActorSingleHitDamages[(protag, 132, chargedBoss)] = 320.0;

        var dVeto = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, chie, kanji, protag },
            Enemies = new() { chargedBoss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chie, kanji, protag, chargedBoss }
        }, cfg)!;
        Assert.NotEqual((ushort)235, dVeto.SkillId);
    }

    [Fact]
    public void BossGuardFix_StaleTelegraphsRealBossIdsAndHealthyMindCharge_DoNotLockPartyIntoGuard()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Ameno-sagiri (266) with leftover LastSkillId = 384 (Bewildering Fog) and WaitMessageId = 0 must NOT Guard
        var amenoFog = MakeEnemy(266, 1, 3000, 3000, lastSkillId: 384, lastActionType: 2, waitMessageId: 0);
        var yukiko = MakeParty(4, TacticMode.ActFreely, 360, 360, 150, 150, new ushort[] { 3 });
        bridge.SingleHitDamages[(3, amenoFog)] = 150.0;
        var dAmeno = engine.DecideAction(new BattleSnapshot { Actor = yukiko, Allies = new() { yukiko }, Enemies = new() { amenoFog } }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dAmeno.ActionKind);
        Assert.Equal((ushort)3, dAmeno.SkillId);

        // 2. Avenger Knight (310, Counter flag statically set) & Contrarian King (290) do NOT permanently block elements
        var avengerKnight = MakeEnemy(310, 1, 1400, 1400, enemyCounter: true);
        var chie = MakeParty(3, TacticMode.ActFreely, 320, 320, 90, 90, new ushort[] { 26, 122 });
        bridge.SingleHitDamages[(26, avengerKnight)] = 95.0;
        var dAvenger = engine.DecideAction(new BattleSnapshot { Actor = chie, Allies = new() { chie }, Enemies = new() { avengerKnight } }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dAvenger.ActionKind);
        Assert.Equal((ushort)26, dAvenger.SkillId);

        // 3. Stale WaitMessageId after enemy already acted (lastActionType == 2) or while enemy is Down does NOT force Guard
        var shadowTeddieStale = MakeEnemy(261, 1, 2500, 2500, waitMessageId: 169, lastActionType: 2);
        bridge.SingleHitDamages[(3, shadowTeddieStale)] = 150.0;
        var dTeddieStale = engine.DecideAction(new BattleSnapshot { Actor = yukiko, Allies = new() { yukiko }, Enemies = new() { shadowTeddieStale } }, cfg)!;
        Assert.Equal((ushort)3, dTeddieStale.SkillId);

        var shadowKanjiDown = MakeEnemy(259, 1, 1800, 1800, isDown: true, lastSkillId: 358, lastActionType: 7);
        bridge.SingleHitDamages[(3, shadowKanjiDown)] = 150.0;
        var dKanjiDown = engine.DecideAction(new BattleSnapshot { Actor = yukiko, Allies = new() { yukiko }, Enemies = new() { shadowKanjiDown } }, cfg)!;
        Assert.Equal((ushort)3, dKanjiDown.SkillId);

        // 4. Generic Mind-Charged boss when weak ally is at 100% HP: healthy ally attacks instead of permanent Guard loop;
        //    at critical HP (<= 50%), ally Guards as expected.
        var genericCharged = MakeEnemy(99, 1, 2000, 2000, mindCharge: true);
        bridge.Affinities[(chie, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.SingleHitDamages[(122, genericCharged)] = 110.0;

        var dHealthyWeak = engine.DecideAction(new BattleSnapshot { Actor = chie, Allies = new() { chie }, Enemies = new() { genericCharged } }, cfg)!;
        Assert.Equal((ushort)122, dHealthyWeak.SkillId);

        var chieLowHp = MakeParty(3, TacticMode.ActFreely, 110, 320, 90, 90, new ushort[] { 122 });
        bridge.Affinities[(chieLowHp, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        var dLowWeak = engine.DecideAction(new BattleSnapshot { Actor = chieLowHp, Allies = new() { chieLowHp }, Enemies = new() { genericCharged } }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dLowWeak.ActionKind);
    }

    [Fact]
    public void BuffDuration_SkipsActiveMultiTurnBuffs_AndRefreshesExpiringOneTurnBuffs()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var boss = MakeEnemy(99, 1, 2400, 2400);
        bridge.Affinities[(boss, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(boss, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        // Carry already has Atk+1 for 3 more turns and Crit+1 for 3 turns -> Yosuke must NOT waste turn on Matarukaja (219) or Rebellion (234)
        var yosukeFresh = MakeParty(2, TacticMode.ActFreely, 360, 360, 150, 150, new ushort[] { 219, 234, 14 }, atkStage: 1, critRateStage: 1, atkStageTurns: 3, critRateStageTurns: 3);
        var kanjiFresh = MakeParty(6, TacticMode.FullAssault, 450, 450, 120, 120, new ushort[] { 138 }, atkStage: 1, critRateStage: 1, atkStageTurns: 3, critRateStageTurns: 3);
        var chieFresh = MakeParty(3, TacticMode.ActFreely, 380, 380, 120, 120, new ushort[] { 133 }, atkStage: 1, critRateStage: 1, atkStageTurns: 3, critRateStageTurns: 3);
        bridge.ActorSingleHitDamages[(yosukeFresh, 14, boss)] = 65.0;
        bridge.ActorSingleHitDamages[(kanjiFresh, 138, boss)] = 280.0;
        bridge.ActorSingleHitDamages[(chieFresh, 133, boss)] = 160.0;

        var dSkip = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeFresh,
            Allies = new() { yosukeFresh, kanjiFresh, chieFresh },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiFresh, chieFresh, boss }
        }, cfg)!;
        Assert.Equal((ushort)14, dSkip.SkillId);

        // Expiring Atk+1 (Turns == 1) in a multi-round boss fight -> proactively refreshes Matarukaja (219)!
        var yosukeExpiring = MakeParty(2, TacticMode.ActFreely, 360, 360, 150, 150, new ushort[] { 219, 14 }, atkStage: 1, atkStageTurns: 1);
        var kanjiExpiring = MakeParty(6, TacticMode.FullAssault, 450, 450, 120, 120, new ushort[] { 138 }, atkStage: 1, atkStageTurns: 1);
        var chieExpiring = MakeParty(3, TacticMode.ActFreely, 380, 380, 120, 120, new ushort[] { 133 }, atkStage: 1, atkStageTurns: 1);
        bridge.ActorSingleHitDamages[(yosukeExpiring, 14, boss)] = 65.0;
        bridge.ActorSingleHitDamages[(kanjiExpiring, 138, boss)] = 280.0;
        bridge.ActorSingleHitDamages[(chieExpiring, 133, boss)] = 160.0;

        var dRefresh = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeExpiring,
            Allies = new() { yosukeExpiring, kanjiExpiring, chieExpiring },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiExpiring, chieExpiring, boss }
        }, cfg)!;
        Assert.Equal((ushort)219, dRefresh.SkillId);
    }

    [Fact]
    public void ActFreely_PicksCheapestWinOnTies_AndSkipsUnnecessaryDefensiveBuffsInShortFights()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Tying 1-round kill: Normal Attack (0, 90 dmg), Power Slash (122, 110 dmg, 10% HP), Agidyne (3, 140 dmg, 12 SP)
        //    against 80 HP enemy -> Act Freely finishes in 1 turn while saving both HP and SP!
        var trash = MakeEnemy(10, 1, 80, 80);
        var protagStandin = MakeParty(2, TacticMode.ActFreely, 300, 300, 100, 100, new ushort[] { 122, 3 });
        bridge.SingleHitDamages[(0, trash)] = 90.0;
        bridge.SingleHitDamages[(122, trash)] = 110.0;
        bridge.SingleHitDamages[(3, trash)] = 140.0;

        var dCheapWin = engine.DecideAction(new BattleSnapshot
        {
            Actor = protagStandin,
            Allies = new() { protagStandin },
            Enemies = new() { trash }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, dCheapWin.ActionKind);
        Assert.Equal((ushort)0, dCheapWin.SkillId);

        // 2. Short 1.5-round tough fight (580 HP, ends quickly) -> does NOT waste a carry's turn on Marakukaja (221)
        var midShadow = MakeEnemy(30, 1, 580, 580);
        var teddie = MakeParty(5, TacticMode.ActFreely, 360, 360, 140, 140, new ushort[] { 221, 26 });
        var kanji = MakeParty(6, TacticMode.FullAssault, 450, 450, 100, 100, new ushort[] { 138 });
        bridge.ActorSingleHitDamages[(teddie, 26, midShadow)] = 130.0;
        bridge.ActorSingleHitDamages[(kanji, 138, midShadow)] = 260.0;

        var dShortBoss = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddie,
            Allies = new() { teddie, kanji },
            Enemies = new() { midShadow },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanji, midShadow }
        }, cfg)!;
        Assert.Equal((ushort)26, dShortBoss.SkillId);
    }

    [Fact]
    public void FullAssault_MaximizesUncappedDamage_SkipsSpItem_UsesDamageItem_AndRageBoost()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.AvailableSpItems.Add(new SpRecoveryItemInfo
        {
            ItemId = 790, Name = "Snuff Soul", SkillId = 309, Quantity = 5, EstimatedSpGain = 50
        });
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Overkill & SP-agnostic max damage + skip SP-recovery item turn-burn
        var lowShadow = MakeEnemy(15, 1, 100, 100);
        var yukikoLowSp = MakeParty(4, TacticMode.FullAssault, 320, 320, 25, 200, new ushort[] { 1, 3 });
        bridge.SingleHitDamages[(1, lowShadow)] = 110.0; // Agi (4 SP)
        bridge.SingleHitDamages[(3, lowShadow)] = 260.0; // Agidyne (12 SP)
        var dMaxDmg = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoLowSp,
            Allies = new() { yukikoLowSp },
            Enemies = new() { lowShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dMaxDmg.ActionKind);
        Assert.Equal((ushort)3, dMaxDmg.SkillId);

        // 2. Uses Elemental Damage Item when stronger than neutral skills
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 501,
            Name = "Pinwheel",
            Kind = BattleConsumableKind.Damage,
            Quantity = 3,
            Rarity = ConsumableRarityTier.Common,
            SkillId = 900,
            Skill = new SkillMetadata { SkillId = 900, Name = "Pinwheel", Element = ElementId.Wind, Scope = 0, TargetSide = 2, EffectType = 4, Power = 150 }
        });
        var windWeakShadow = MakeEnemy(16, 1, 1200, 1200);
        var yosukeNeutral = MakeParty(2, TacticMode.FullAssault, 320, 320, 80, 80, new ushort[] { 122 });
        bridge.SingleHitDamages[(122, windWeakShadow)] = 70.0;
        bridge.SingleHitDamages[(0, windWeakShadow)] = 35.0;
        bridge.SingleHitDamages[(900, windWeakShadow)] = 200.0;
        var dItem = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeNeutral,
            Allies = new() { yosukeNeutral },
            Enemies = new() { windWeakShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dItem.ActionKind);
        Assert.Equal((ushort)501, dItem.ItemId);

        // 3. Uses Valiant Dance (90, Rage = 2x dmg) when it multiplies team damage above actor attack, never Confuse Pulinpa (83)
        bridge.AvailableConsumables.Clear();
        var bulkyNonBoss = MakeEnemy(20, 1, 480, 480);
        var bulkyNonBoss2 = MakeEnemy(21, 2, 480, 480);
        var teddieSupport = MakeParty(5, TacticMode.FullAssault, 350, 350, 120, 120, new ushort[] { 83, 90, 26 });
        var kanjiCarry = MakeParty(6, TacticMode.FullAssault, 450, 450, 120, 120, new ushort[] { 138 });
        var chieCarry = MakeParty(3, TacticMode.FullAssault, 400, 400, 120, 120, new ushort[] { 132 });
        bridge.ActorSingleHitDamages[(teddieSupport, 26, bulkyNonBoss)] = 55.0;
        bridge.ActorSingleHitDamages[(teddieSupport, 26, bulkyNonBoss2)] = 55.0;
        bridge.ActorSingleHitDamages[(kanjiCarry, 138, bulkyNonBoss)] = 140.0;
        bridge.ActorSingleHitDamages[(chieCarry, 132, bulkyNonBoss)] = 110.0;
        bridge.AilmentAffinities[bulkyNonBoss] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.AilmentAffinities[bulkyNonBoss2] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        var dRage = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddieSupport,
            Allies = new() { teddieSupport, kanjiCarry, chieCarry },
            Enemies = new() { bulkyNonBoss, bulkyNonBoss2 },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiCarry, chieCarry, bulkyNonBoss, bulkyNonBoss2 }
        }, cfg)!;
        Assert.Equal((ushort)90, dRage.SkillId);

        // Without Rage, FullAssault must NEVER cast pure Confuse Pulinpa (83)
        var teddieConfuseOnly = MakeParty(5, TacticMode.FullAssault, 350, 350, 120, 120, new ushort[] { 83, 26 });
        bridge.ActorSingleHitDamages[(teddieConfuseOnly, 26, bulkyNonBoss)] = 55.0;
        var dNoConfuse = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddieConfuseOnly,
            Allies = new() { teddieConfuseOnly, kanjiCarry, chieCarry },
            Enemies = new() { bulkyNonBoss, bulkyNonBoss2 },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiCarry, chieCarry, bulkyNonBoss, bulkyNonBoss2 }
        }, cfg)!;
        Assert.Equal((ushort)26, dNoConfuse.SkillId);
    }

    [Fact]
    public void ConserveSp_AllowsEmergencyDiaAndHighRoiCheapBuffsInBoss_SkipsInShortFights()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Long boss fight -> ConserveSp uses Rakunda (213, 12 SP) & Tarukaja (218, 12 SP) cleanly when ROI clears threshold
        var boss = MakeEnemy(99, 1, 2500, 2500);
        var yosukeSp = MakeParty(2, TacticMode.ConserveSp, 340, 340, 120, 120, new ushort[] { 213, 218, 52 });
        var kanjiCarry = MakeParty(6, TacticMode.FullAssault, 450, 450, 120, 120, new ushort[] { 138 });
        var chieCarry = MakeParty(3, TacticMode.FullAssault, 400, 400, 120, 120, new ushort[] { 132 });
        bridge.ActorSingleHitDamages[(yosukeSp, 0, boss)] = 45.0;
        bridge.ActorSingleHitDamages[(yosukeSp, 52, boss)] = 110.0;
        bridge.ActorSingleHitDamages[(kanjiCarry, 138, boss)] = 280.0;
        bridge.ActorSingleHitDamages[(chieCarry, 132, boss)] = 160.0;

        var dRakunda = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeSp,
            Allies = new() { yosukeSp, kanjiCarry, chieCarry },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiCarry, chieCarry, boss }
        }, cfg)!;
        Assert.Equal((ushort)213, dRakunda.SkillId);

        // Once boss already has Def-1 -> uses Tarukaja (218, 12 SP) on unbuffed heavy carry!
        var debuffedBoss = MakeEnemy(99, 1, 2500, 2500, defStage: -1, defStageTurns: 3);
        bridge.ActorSingleHitDamages[(yosukeSp, 0, debuffedBoss)] = 45.0;
        bridge.ActorSingleHitDamages[(kanjiCarry, 138, debuffedBoss)] = 280.0;
        bridge.ActorSingleHitDamages[(chieCarry, 132, debuffedBoss)] = 160.0;
        var dTarukaja = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeSp,
            Allies = new() { yosukeSp, kanjiCarry, chieCarry },
            Enemies = new() { debuffedBoss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiCarry, chieCarry, debuffedBoss }
        }, cfg)!;
        Assert.Equal((ushort)218, dTarukaja.SkillId);

        // And in a long standing pack with a multi-hit Phys carry, ConserveSp uses cheap Rebellion (234, 5 SP)!
        var s1 = MakeEnemy(10, 0, 480, 480);
        var s2 = MakeEnemy(11, 1, 480, 480);
        var teddieCritSp = MakeParty(5, TacticMode.ConserveSp, 340, 340, 120, 120, new ushort[] { 234 });
        var chieMulti = MakeParty(3, TacticMode.FullAssault, 400, 400, 120, 120, new ushort[] { 133 }, atkStage: 1);
        bridge.ActorSingleHitDamages[(teddieCritSp, 0, s1)] = 30.0;
        bridge.ActorSingleHitDamages[(chieMulti, 133, s1)] = 40.0;
        bridge.ActorSingleHitDamages[(chieMulti, 133, s2)] = 40.0;
        var dRebellion = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddieCritSp,
            Allies = new() { teddieCritSp, chieMulti },
            Enemies = new() { s1, s2 },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chieMulti, s1, s2 }
        }, cfg)!;
        Assert.Equal((ushort)234, dRebellion.SkillId);

        // 2. Short 180 HP shadow -> skips buffs completely, attacks for 0 SP
        var trash = MakeEnemy(10, 1, 180, 180);
        bridge.ActorSingleHitDamages[(yosukeSp, 0, trash)] = 50.0;
        var dTrash = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeSp,
            Allies = new() { yosukeSp, kanjiCarry },
            Enemies = new() { trash }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, dTrash.ActionKind);

        // 3. Emergency heal <= 35% HP in multi-round boss uses cheap Dia (192, 3 SP), skips Mediarama (196, 12 SP)
        var yukikoSp = MakeParty(4, TacticMode.ConserveSp, 320, 320, 100, 100, new ushort[] { 192, 196, 3 });
        var criticalKanji = MakeParty(6, TacticMode.FullAssault, 120, 450, 100, 100, new ushort[] { 138 });
        bridge.ActorSingleHitDamages[(yukikoSp, 0, boss)] = 40.0;
        bridge.ActorSingleHitDamages[(criticalKanji, 138, boss)] = 250.0;
        var dEmergencyDia = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoSp,
            Allies = new() { yukikoSp, criticalKanji },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)192, dEmergencyDia.SkillId);
    }

    [Fact]
    public void HealSupport_CastsDisablingAilmentsOnTrashPack_SkipsOnBoss_AndSkipsLateHealBeforeFinish()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. Multi-round standing non-boss shadows, party healthy/buffed -> HealSupport disables pack with Tentarafoo (84)!
        var s1 = MakeEnemy(14, 0, 450, 450, atkStage: -1, defStage: -1);
        var s2 = MakeEnemy(15, 1, 450, 450, atkStage: -1, defStage: -1);
        var teddieSupport = MakeParty(5, TacticMode.HealSupport, 360, 360, 140, 140, new ushort[] { 193, 84, 26 }, atkStage: 1, defStage: 1, hitEvaStage: 1);
        var chie = MakeParty(3, TacticMode.ActFreely, 360, 360, 100, 100, new ushort[] { 122 }, atkStage: 1, defStage: 1, hitEvaStage: 1);
        bridge.ActorSingleHitDamages[(teddieSupport, 26, s1)] = 70.0;
        bridge.ActorSingleHitDamages[(chie, 122, s1)] = 110.0;
        bridge.AilmentAffinities[s1] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.AilmentAffinities[s2] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        var dAilment = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddieSupport,
            Allies = new() { teddieSupport, chie },
            Enemies = new() { s1, s2 }
        }, cfg)!;
        Assert.Equal((ushort)84, dAilment.SkillId);

        // 2. Same setup against already-Confused enemies or a boss -> skips ailment and attacks!
        var confused1 = MakeEnemy(14, 0, 450, 450, atkStage: -1, defStage: -1, stateFlags: 0x80);
        var confused2 = MakeEnemy(15, 1, 450, 450, atkStage: -1, defStage: -1, stateFlags: 0x80);
        bridge.ActorSingleHitDamages[(teddieSupport, 26, confused1)] = 70.0;
        bridge.ActorSingleHitDamages[(chie, 122, confused1)] = 110.0;
        var dSkipAfflicted = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddieSupport,
            Allies = new() { teddieSupport, chie },
            Enemies = new() { confused1, confused2 }
        }, cfg)!;
        Assert.Equal((ushort)26, dSkipAfflicted.SkillId);

        var boss = MakeEnemy(259, 0, 1800, 1800, atkStage: -1, defStage: -1);
        bridge.ActorSingleHitDamages[(teddieSupport, 26, boss)] = 70.0;
        bridge.ActorSingleHitDamages[(chie, 122, boss)] = 110.0;
        bridge.AilmentAffinities[boss] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        var dSkipBoss = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddieSupport,
            Allies = new() { teddieSupport, chie },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)26, dSkipBoss.SkillId);

        // 3. Battle ends this round (EstimatedRoundsRemaining <= 1.0), injured ally is >= 45% (53% < 60% threshold):
        //    skips unnecessary non-emergency Diarama heal and attacks!
        var nearlyDead = MakeEnemy(14, 0, 160, 400);
        var chieMidHp = MakeParty(3, TacticMode.ActFreely, 160, 300, 100, 100, new ushort[] { 122 });
        bridge.ActorSingleHitDamages[(teddieSupport, 26, nearlyDead)] = 85.0;
        bridge.ActorSingleHitDamages[(chieMidHp, 122, nearlyDead)] = 120.0;
        var dFinishInsteadOfHeal = engine.DecideAction(new BattleSnapshot
        {
            Actor = teddieSupport,
            Allies = new() { teddieSupport, chieMidHp },
            Enemies = new() { nearlyDead },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chieMidHp, nearlyDead }
        }, cfg)!;
        Assert.Equal((ushort)26, dFinishInsteadOfHeal.SkillId);
    }

    [Fact]
    public void ShadowKanji_ElecTelegraphGating_And_ChargedBossGuardBeforeRevive()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var kanjiP100 = MakeEnemy(259, 0, 1500, 1800, lastSkillId: 358, lastActionType: 2);
        var niceGuy = MakeEnemy(272, 1, 350, 400);
        var yosuke = MakeParty(2, TacticMode.ActFreely, 250, 259, 105, 152, new ushort[] { 14, 192 });
        var chie = MakeParty(3, TacticMode.ActFreely, 220, 226, 80, 106, new ushort[] { 26, 218 });
        bridge.Affinities[(yosuke, ElementId.Elec)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.ActorSingleHitDamages[(yosuke, 14, kanjiP100)] = 90.0;
        bridge.ActorSingleHitDamages[(yosuke, 0, kanjiP100)] = 70.0;

        // 1a. Phase 100 (guards alive or HP > 50%), !anyAllyDown, !PowerCharge -> Yosuke Guards against Mazio/Zio follow-up
        var dGuardElec = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, chie },
            Enemies = new() { kanjiP100, niceGuy }
        }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dGuardElec.ActionKind);

        // 1b. If an ally is Down, AI_IS_ENEMY_DOWN preempts with Wall/SpiritDrain -> Yosuke does NOT falsely Guard
        var chieDown = MakeParty(3, TacticMode.ActFreely, 120, 226, 80, 106, new ushort[] { 26 }, stateFlags: 0x100000);
        var dSkipWhenAllyDown = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, chieDown },
            Enemies = new() { kanjiP100, niceGuy }
        }, cfg)!;
        Assert.NotEqual(AiActionKind.Guard, dSkipWhenAllyDown.ActionKind);

        // 1c. Both guards dead and HP <= 50% (AI_kanji_P4G_50) -> 358/359 no longer forces Elec Guard
        var kanjiP50Solo = MakeEnemy(259, 0, 850, 1800, lastSkillId: 359, lastActionType: 2);
        bridge.ActorSingleHitDamages[(yosuke, 14, kanjiP50Solo)] = 90.0;
        var dSkipP50 = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, chie },
            Enemies = new() { kanjiP50Solo }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dSkipP50.ActionKind);
        Assert.Equal((ushort)14, dSkipP50.SkillId);

        // 2. Boss holds PowerCharge + ally dead -> survivor at 82% HP Guards BEFORE risking partial-HP Revival Bead (50%)
        var revivalBeadSkill = new SkillMetadata
        {
            SkillId = 297,
            Name = "Revival Bead",
            Element = ElementId.Recovery,
            TargetSide = 1,
            TargetCond = 4,
            EffectType = 11,
            Power = 50,
            AilmentMask = 0x80000
        };
        bridge.Skills[297] = revivalBeadSkill;
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 783,
            Name = "Revival Bead",
            SkillId = 297,
            Quantity = 10,
            Kind = BattleConsumableKind.Revive,
            Rarity = ConsumableRarityTier.Common,
            Skill = revivalBeadSkill,
            EstimatedHpGain = 50
        });

        var kanjiCharged = MakeEnemy(259, 0, 700, 1800, powerCharge: true);
        var yosuke82 = MakeParty(2, TacticMode.ActFreely, 212, 259, 105, 152, new ushort[] { 14 });
        var chieDead = MakeParty(3, TacticMode.ActFreely, 0, 226, 28, 106, new ushort[] { 26 }, stateFlags: 0x80000);
        bridge.ActorSingleHitDamages[(yosuke82, 14, kanjiCharged)] = 85.0;

        var dGuardBeforeRevive = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke82,
            Allies = new() { yosuke82, chieDead },
            Enemies = new() { kanjiCharged }
        }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dGuardBeforeRevive.ActionKind);

        // Even at 100% HP, survivor must NOT burn partial-HP Revival Bead straight into a Power-Charged boss
        var yosukeFull = MakeParty(2, TacticMode.ActFreely, 259, 259, 105, 152, new ushort[] { 14 });
        bridge.ActorSingleHitDamages[(yosukeFull, 14, kanjiCharged)] = 85.0;
        var dNoSuicideRevive = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeFull,
            Allies = new() { yosukeFull, chieDead },
            Enemies = new() { kanjiCharged }
        }, cfg)!;
        Assert.NotEqual(AiActionKind.Item, dNoSuicideRevive.ActionKind);

        // Once boss consumes PowerCharge, Yosuke safely revives Chie on the next turn
        var kanjiSpent = MakeEnemy(259, 0, 700, 1800, powerCharge: false);
        var dSafeRevive = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke82,
            Allies = new() { yosuke82, chieDead },
            Enemies = new() { kanjiSpent }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dSafeRevive.ActionKind);
        Assert.Equal((ushort)783, dSafeRevive.ItemId);
        Assert.Same(chieDead, dSafeRevive.Target);

        // With all allies alive, 50% HP ally also reactively Guards against Power-Charged boss (<= 70% cap)
        var chie50 = MakeParty(3, TacticMode.ActFreely, 113, 226, 60, 106, new ushort[] { 122 });
        bridge.ActorSingleHitDamages[(chie50, 122, kanjiCharged)] = 75.0;
        var dGuardAt50 = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie50,
            Allies = new() { yosuke82, chie50 },
            Enemies = new() { kanjiCharged }
        }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dGuardAt50.ActionKind);
    }

    [Fact]
    public void FixedHpHealingConsumables_EffectType5_BeatMultiTurnDiaSpamInOneTurn()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // Actual SKILL.TBL EffectType = 5 fixed-HP healing consumables
        var valMedSkill = bridge.Skills[294] = new SkillMetadata { SkillId = 294, Name = "Value Medicine", Element = ElementId.Recovery, Scope = 1, TargetSide = 1, EffectType = 5, Power = 100 };
        var medKitSkill = bridge.Skills[295] = new SkillMetadata { SkillId = 295, Name = "Medical Kit", Element = ElementId.Recovery, Scope = 1, TargetSide = 1, EffectType = 5, Power = 200 };
        Assert.True(valMedSkill.IsHeal);
        Assert.True(medKitSkill.IsHeal);

        bridge.AvailableConsumables.AddRange(new[]
        {
            new BattleConsumableInfo { ItemId = 775, Name = "Value Medicine", SkillId = 294, Quantity = 5, Kind = BattleConsumableKind.Heal, Rarity = ConsumableRarityTier.Mid, Skill = valMedSkill, EstimatedHpGain = 100 },
            new BattleConsumableInfo { ItemId = 776, Name = "Medical Kit", SkillId = 295, Quantity = 3, Kind = BattleConsumableKind.Heal, Rarity = ConsumableRarityTier.Mid, Skill = medKitSkill, EstimatedHpGain = 200 }
        });

        var boss = MakeEnemy(259, 0, 1400, 1800);

        // 1. Two critical allies (60/192, 74/226) + actor only knows ST Dia -> picks sufficient 1-turn Value Medicine (775, 100 AoE) while saving Medical Kit
        var yosuke = MakeParty(2, TacticMode.ActFreely, 150, 259, 114, 152, new ushort[] { 192, 14 });
        var yukikoCrit = MakeParty(4, TacticMode.ActFreely, 60, 192, 150, 206, new ushort[] { 192, 3 });
        var chieCrit = MakeParty(3, TacticMode.ActFreely, 74, 226, 84, 106, new ushort[] { 26 });

        var dAoEItem = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, yukikoCrit, chieCrit },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dAoEItem.ActionKind);
        Assert.Equal((ushort)775, dAoEItem.ItemId);
        Assert.NotNull(dAoEItem.Targets);
        Assert.Equal(3, dAoEItem.Targets!.Count);

        // 2. Single 1% HP critical ally (deficit 191 vs Dia ~72) while party is bruised (< 70%) -> Medical Kit rescues all in 1 turn
        var yosuke47 = MakeParty(2, TacticMode.ActFreely, 123, 259, 111, 152, new ushort[] { 192, 14 });
        var yukiko1Hp = MakeParty(4, TacticMode.ActFreely, 1, 192, 149, 206, new ushort[] { 192, 3 });
        var chie61 = MakeParty(3, TacticMode.ActFreely, 139, 226, 84, 106, new ushort[] { 26 });

        var dRescueAll = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke47,
            Allies = new() { yosuke47, yukiko1Hp, chie61 },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dRescueAll.ActionKind);
        Assert.Equal((ushort)776, dRescueAll.ItemId);

        // 3. Moderate HealSupport injury (105/192 = 54.7% < 60%, deficit 87 vs Dia ~72) -> Dia covers it cleanly without burning items
        var yukikoMid = MakeParty(4, TacticMode.HealSupport, 105, 192, 149, 206, new ushort[] { 192, 3 });
        var yosukeHealSupport = MakeParty(2, TacticMode.HealSupport, 259, 259, 111, 152, new ushort[] { 192, 14 });
        var dDiaCoversIt = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeHealSupport,
            Allies = new() { yosukeHealSupport, yukikoMid },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dDiaCoversIt.ActionKind);
        Assert.Equal((ushort)192, dDiaCoversIt.SkillId);

        // 4. Single critical ally (1/192) when others are healthy and actor only knows Dia + has ST EffectType=5 200-HP Potion -> 1-turn save with Potion!
        var stPotionSkill = bridge.Skills[290] = new SkillMetadata { SkillId = 290, Name = "Bead-tier Potion", Element = ElementId.Recovery, Scope = 0, TargetSide = 1, EffectType = 5, Power = 200 };
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 771, Name = "Medicine 200", SkillId = 290, Quantity = 3, Kind = BattleConsumableKind.Heal, Rarity = ConsumableRarityTier.Mid, Skill = stPotionSkill, EstimatedHpGain = 200
        });
        var yosukeHealthy = MakeParty(2, TacticMode.ActFreely, 259, 259, 111, 152, new ushort[] { 192, 14 });
        var dStItemSave = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeHealthy,
            Allies = new() { yosukeHealthy, yukiko1Hp },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dStItemSave.ActionKind);
        Assert.Equal((ushort)771, dStItemSave.ItemId);
        Assert.Same(yukiko1Hp, dStItemSave.Target);
    }

    [Fact]
    public void ChieTarukaja_SkipsSpammedMcRefresh_HitsWeakGuard_AndBuffsOtherAlliesOrSelf()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[26] = new SkillMetadata { SkillId = 26, Name = "Bufu", Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 98 };
        bridge.Skills[29] = new SkillMetadata { SkillId = 29, Name = "Mabufu", Element = ElementId.Ice, StatType = 2, CostType = 2, BaseCost = 10, Scope = 1, TargetSide = 2, EffectType = 1, Power = 40, Accuracy = 95 };
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // 1. MC already has Tarukaja (Turns = 1), Nice Guy (272) is standing & Ice-weak -> Chie takes Bufu/Mabufu 1-More over marginal recast!
        var kanji = MakeEnemy(259, 0, 1200, 1800);
        var niceGuy = MakeEnemy(272, 1, 220, 400);
        bridge.Affinities[(niceGuy, ElementId.Ice)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(kanji, ElementId.Ice)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        var chie = MakeParty(3, TacticMode.ActFreely, 213, 226, 40, 106, new ushort[] { 26, 29, 218 });
        var protagBuffed = MakeParty(1, TacticMode.DirectCommands, 250, 260, 120, 150, new ushort[] { 122 }, atkStage: 1, atkStageTurns: 1);
        var yukikoSmall = MakeParty(4, TacticMode.ActFreely, 180, 192, 140, 206, new ushort[] { 3 });

        bridge.ActorSingleHitDamages[(chie, 26, niceGuy)] = 78.0;
        bridge.ActorSingleHitDamages[(chie, 29, niceGuy)] = 78.0;
        bridge.ActorSingleHitDamages[(chie, 29, kanji)] = 42.0;
        bridge.ActorSingleHitDamages[(chie, 0, kanji)] = 58.0;
        bridge.ActorSingleHitDamages[(protagBuffed, 122, kanji)] = 110.0;
        bridge.ActorSingleHitDamages[(yukikoSmall, 3, kanji)] = 75.0;

        var dHitWeakness = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { protagBuffed, chie, yukikoSmall },
            Enemies = new() { kanji, niceGuy },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { protagBuffed, yukikoSmall, kanji, niceGuy }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dHitWeakness.ActionKind);
        Assert.True(dHitWeakness.SkillId is 26 or 29);

        // 2. Solo boss, MC unbuffed marginal gain (~68 vs 58 attack cost) at 40/106 SP -> Chie attacks instead of spamming Tarukaja!
        var protagLowMarg = MakeParty(1, TacticMode.DirectCommands, 250, 260, 120, 150, new ushort[] { 122 });
        bridge.ActorSingleHitDamages[(protagLowMarg, 122, kanji)] = 70.0;
        var dSkipLowRoiBuff = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { protagLowMarg, chie },
            Enemies = new() { kanji },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { protagLowMarg, kanji }
        }, cfg)!;
        Assert.NotEqual((ushort)218, dSkipLowRoiBuff.SkillId);

        // 3. MC already buffed (Turns = 1), unbuffed Yukiko has strong Fire boss DPS -> Chie casts Tarukaja on Yukiko!
        var yukikoCarry = MakeParty(4, TacticMode.ActFreely, 192, 192, 180, 206, new ushort[] { 3 });
        bridge.ActorSingleHitDamages[(yukikoCarry, 3, kanji)] = 190.0;
        var chieHealthySp = MakeParty(3, TacticMode.ActFreely, 213, 226, 90, 106, new ushort[] { 26, 218 });
        bridge.ActorSingleHitDamages[(chieHealthySp, 0, kanji)] = 58.0;
        bridge.ActorSingleHitDamages[(chieHealthySp, 26, kanji)] = 45.0;

        var dBuffYukiko = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieHealthySp,
            Allies = new() { protagBuffed, chieHealthySp, yukikoCarry },
            Enemies = new() { kanji },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { protagBuffed, yukikoCarry, kanji }
        }, cfg)!;
        Assert.Equal((ushort)218, dBuffYukiko.SkillId);
        Assert.Same(yukikoCarry, dBuffYukiko.Target);

        // 4. HealSupport heuristic fallback & unbuffed self-selection when other allies are already Atk-buffed -> Chie buffs herself!
        var chieSelfSupport = MakeParty(3, TacticMode.HealSupport, 226, 226, 90, 106, new ushort[] { 218, 122 });
        var protagFullBuff = MakeParty(1, TacticMode.DirectCommands, 260, 260, 120, 150, Array.Empty<ushort>(), atkStage: 1, atkStageTurns: 3);
        var yukikoFullBuff = MakeParty(4, TacticMode.ActFreely, 192, 192, 150, 206, Array.Empty<ushort>(), atkStage: 1, atkStageTurns: 3);
        var dSelfBuff = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieSelfSupport,
            Allies = new() { protagFullBuff, chieSelfSupport, yukikoFullBuff },
            Enemies = new() { MakeEnemy(99, 0, 1800, 1800) }
        }, cfg)!;
        Assert.Equal((ushort)218, dSelfBuff.SkillId);
        Assert.Same(chieSelfSupport, dSelfBuff.Target);
    }

    [Fact]
    public void Revive_TurnOrderSorting_PicksYukikoBeforeChie_AndTier0RawQueueFirst()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var boss = MakeEnemy(50, 1, 1500, 1500, agility: 18);
        var yosuke = MakeParty(2, TacticMode.ActFreely, 250, 260, 120, 150, new ushort[] { 209, 14 }, agility: 30);
        // Chie has lower SlotId (3) than Yukiko (4), but Yukiko has higher Agility (25 > 20) -> Yukiko acts sooner after Yosuke!
        var chieDead = MakeParty(3, TacticMode.ActFreely, 0, 230, 80, 100, new ushort[] { 26 }, stateFlags: 0x80000, agility: 20);
        var yukikoDead = MakeParty(4, TacticMode.HealSupport, 0, 200, 150, 200, new ushort[] { 3, 193 }, stateFlags: 0x80000, agility: 25);

        // 1. Tier 1 fallback (RawRoundTurnOrder empty, both <= actor.Agility=30): Yukiko (Agi 25) beats Chie (Agi 20)
        var dTier1 = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, chieDead, yukikoDead },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)209, dTier1.SkillId);
        Assert.Same(yukikoDead, dTier1.Target);

        // 2. Tier 1 (Agi <= actor) beats Tier 2 (Agi > actor, already acted this round)
        var naotoDeadFast = MakeParty(7, TacticMode.ActFreely, 0, 220, 140, 180, new ushort[] { 53 }, stateFlags: 0x80000, agility: 40);
        var dTier1OverTier2 = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, naotoDeadFast, yukikoDead },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Same(yukikoDead, dTier1OverTier2.Target);

        // 3. Tier 0 RawRoundTurnOrder (both actor-absent and actor-present-at-0 mocks): explicit raw queue slot beats Agility
        var dTier0AbsentActor = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, chieDead, yukikoDead },
            Enemies = new() { boss },
            RawRoundTurnOrder = new List<CombatantSnapshot> { chieDead, yukikoDead, boss }
        }, cfg)!;
        Assert.Same(chieDead, dTier0AbsentActor.Target);

        var dTier0WithActor = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, chieDead, yukikoDead },
            Enemies = new() { boss },
            RawRoundTurnOrder = new List<CombatantSnapshot> { yosuke, chieDead, yukikoDead, boss }
        }, cfg)!;
        Assert.Same(chieDead, dTier0WithActor.Target);
    }

    [Fact]
    public void Revive_AntiWipeTriage_CriticalSelfHealsFirst_BossStabilizesThreeSurvivors_TelegraphWeakBlocked_AndChargedFullReviveDeferred()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var boss = MakeEnemy(259, 0, 1500, 1800, agility: 22);

        // 1. Critical self (< 40%) or Protagonist (< 30%) survival healing runs BEFORE EvaluateRevive
        var yukikoCrit = MakeParty(4, TacticMode.ActFreely, 50, 200, 140, 200, new ushort[] { 193, 209, 3 }, agility: 25);
        var chieDead = MakeParty(3, TacticMode.ActFreely, 0, 230, 80, 100, new ushort[] { 26 }, stateFlags: 0x80000, agility: 20);
        var dSelfHealFirst = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoCrit,
            Allies = new() { yukikoCrit, chieDead },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)193, dSelfHealFirst.SkillId);

        var mcCrit = MakeParty(1, TacticMode.DirectCommands, 70, 300, 100, 150, Array.Empty<ushort>(), agility: 28);
        var yukikoHealthyConserve = MakeParty(4, TacticMode.ConserveSp, 200, 200, 140, 200, new ushort[] { 193, 209 }, agility: 25);
        var dMcHealFirst = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoHealthyConserve,
            Allies = new() { mcCrit, yukikoHealthyConserve, chieDead },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)193, dMcHealFirst.SkillId);
        Assert.Same(mcCrit, dMcHealFirst.Target);

        // 2. Boss fight with >= 3 living allies and a survivor at <= 55% HP suppresses partial-HP Recarm (50%) and stabilizes survivor first
        var mcHealthy = MakeParty(1, TacticMode.DirectCommands, 260, 300, 100, 150, Array.Empty<ushort>(), agility: 28);
        var yosukeHalf = MakeParty(2, TacticMode.ActFreely, 130, 260, 100, 150, new ushort[] { 14 }, agility: 30); // 50% <= 55%
        var yukikoPartial = MakeParty(4, TacticMode.ActFreely, 190, 200, 140, 200, new ushort[] { 193, 209, 3 }, agility: 25);
        var dStabilizeBeforePartial = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoPartial,
            Allies = new() { mcHealthy, yosukeHalf, yukikoPartial, chieDead },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)193, dStabilizeBeforePartial.SkillId);
        Assert.Same(yosukeHalf, dStabilizeBeforePartial.Target);

        // 3. Telegraph weakness filter: Shadow Yukiko telegraphs Fire (WaitMessageId=202) -> skips Fire-weak Chie, revives neutral Kanji (or returns null if only Chie dead)
        var shadowYukikoTelegraph = MakeEnemy(258, 0, 1200, 1500, waitMessageId: 202);
        var kanjiDead = MakeParty(6, TacticMode.ActFreely, 0, 340, 80, 100, new ushort[] { 138 }, stateFlags: 0x80000, agility: 18);
        var yosukeReviver = MakeParty(2, TacticMode.ActFreely, 250, 260, 120, 150, new ushort[] { 209, 14 }, agility: 30);
        bridge.Affinities[(chieDead, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.ActorSingleHitDamages[(yosukeReviver, 14, shadowYukikoTelegraph)] = 60.0;

        var dSkipFireWeak = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeReviver,
            Allies = new() { yosukeReviver, chieDead, kanjiDead },
            Enemies = new() { shadowYukikoTelegraph }
        }, cfg)!;
        Assert.Equal((ushort)209, dSkipFireWeak.SkillId);
        Assert.Same(kanjiDead, dSkipFireWeak.Target);

        var dOnlyWeakDead = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeReviver,
            Allies = new() { yosukeReviver, chieDead },
            Enemies = new() { shadowYukikoTelegraph }
        }, cfg)!;
        Assert.NotEqual((ushort)209, dOnlyWeakDead.SkillId);

        // 4. Charged boss full-HP revive deferral:
        //    - When actor <= 85% HP and Charged boss acts ahead of dead ally -> Guards instead of Samarecarm
        //    - When actor > 85% HP with Samarecarm and dead ally is not weak -> Samarecarm is allowed
        var bossCharged = MakeEnemy(99, 0, 1800, 1800, powerCharge: true, agility: 26);
        var yukiko80FullRevive = MakeParty(4, TacticMode.HealSupport, 160, 200, 150, 200, new ushort[] { 210, 3 }, agility: 30);
        var dGuardChargedAhead = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko80FullRevive,
            Allies = new() { yukiko80FullRevive, kanjiDead },
            Enemies = new() { bossCharged },
            RawRoundTurnOrder = new List<CombatantSnapshot> { bossCharged, kanjiDead }
        }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dGuardChargedAhead.ActionKind);

        var yukiko100FullRevive = MakeParty(4, TacticMode.HealSupport, 200, 200, 150, 200, new ushort[] { 210, 3 }, agility: 30);
        var dFullReviveAt100 = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko100FullRevive,
            Allies = new() { yukiko100FullRevive, kanjiDead },
            Enemies = new() { bossCharged },
            RawRoundTurnOrder = new List<CombatantSnapshot> { kanjiDead, bossCharged }
        }, cfg)!;
        Assert.Equal((ushort)210, dFullReviveAt100.SkillId);
        Assert.Same(kanjiDead, dFullReviveAt100.Target);
    }

    [Fact]
    public void ElementProbing_ShadowKanjiAdds272And273_ProbesBufuAndMabufu_PreservesSingleTargetBufu_AndSkipsMajorBoss()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig(respectAnalysis: true);

        // Real UNIT.TBL HP: Kanji (259, 2000 HP revealed), Nice Guy (272, 800 HP unrevealed Ice weak), Tough Guy (273, 2500 HP unrevealed Ice null)
        var kanji = MakeEnemy(259, 0, 1800, 2000);
        var niceGuy = MakeEnemy(272, 1, 800, 800);
        var toughGuy = MakeEnemy(273, 2, 2500, 2500);

        bridge.Affinities[(kanji, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(kanji, ElementId.Ice)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(niceGuy, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(niceGuy, ElementId.Ice)] = new AffinityInfo { IsWeak = true, IsRevealed = false, MultiplierPercent = 125 };
        bridge.Affinities[(toughGuy, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(toughGuy, ElementId.Ice)] = new AffinityInfo { IsNull = true, IsRevealed = false, MultiplierPercent = 0 };
        bridge.Affinities[(toughGuy, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = false, MultiplierPercent = 125 };

        var chie = MakeParty(3, TacticMode.ActFreely, 220, 226, 96, 106, new ushort[] { 26, 29 });
        foreach (var e in new[] { kanji, niceGuy, toughGuy })
        {
            bridge.ActorSingleHitDamages[(chie, 0, e)] = 200.0;
            bridge.ActorSingleHitDamages[(chie, 26, e)] = 65.0;
            bridge.ActorSingleHitDamages[(chie, 29, e)] = 60.0;
        }

        // 1. Against Kanji + 272 + 273 (unrevealed), Chie picks Mabufu (29) to probe both 272 & 273 at once!
        var dMabufuProbe = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie },
            Enemies = new() { kanji, niceGuy, toughGuy }
        }, cfg)!;
        Assert.Equal((ushort)29, dMabufuProbe.SkillId);

        // 2. Without Mabufu, against Nice Guy (272, 800 HP) vs 200-dmg Attack, Chie picks Bufu (26) to probe Ice!
        var chieStOnly = MakeParty(3, TacticMode.ActFreely, 220, 226, 96, 106, new ushort[] { 26 });
        bridge.ActorSingleHitDamages[(chieStOnly, 0, niceGuy)] = 200.0;
        bridge.ActorSingleHitDamages[(chieStOnly, 26, niceGuy)] = 65.0;
        var dBufuProbe = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieStOnly,
            Allies = new() { chieStOnly },
            Enemies = new() { niceGuy }
        }, cfg)!;
        Assert.Equal((ushort)26, dBufuProbe.SkillId);

        // 3. Once Tough Guy (273) already has Fire weakness revealed, Chie does NOT waste SP probing Ice on 273 -> uses Attack (200)
        bridge.Affinities[(toughGuy, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.ActorSingleHitDamages[(chieStOnly, 0, toughGuy)] = 200.0;
        bridge.ActorSingleHitDamages[(chieStOnly, 26, toughGuy)] = 65.0;
        var dSkipWhenWeakRevealed = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieStOnly,
            Allies = new() { chieStOnly },
            Enemies = new() { toughGuy }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, dSkipWhenWeakRevealed.ActionKind);

        // 4. Single neutral unrevealed enemy in FullAssault (IgnoreResourceCosts=true) still prefers ST Bufu (26) over Mabufu (29)
        var chieFullAssault = MakeParty(3, TacticMode.FullAssault, 220, 226, 96, 106, new ushort[] { 26, 29 });
        var singleNeutral = MakeEnemy(15, 1, 500, 500);
        bridge.Affinities[(singleNeutral, ElementId.Ice)] = new AffinityInfo { IsRevealed = false, MultiplierPercent = 100 };
        bridge.ActorSingleHitDamages[(chieFullAssault, 26, singleNeutral)] = 65.0;
        bridge.ActorSingleHitDamages[(chieFullAssault, 29, singleNeutral)] = 65.0;
        var dSingleBufu = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieFullAssault,
            Allies = new() { chieFullAssault },
            Enemies = new() { singleNeutral }
        }, cfg)!;
        Assert.Equal((ushort)26, dSingleBufu.SkillId);

        // 5. 3000-HP major boss still skips element probing
        var majorBoss = MakeEnemy(99, 1, 3000, 3000);
        bridge.Affinities[(majorBoss, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(majorBoss, ElementId.Ice)] = new AffinityInfo { IsRevealed = false, MultiplierPercent = 100 };
        bridge.ActorSingleHitDamages[(chieStOnly, 0, majorBoss)] = 200.0;
        bridge.ActorSingleHitDamages[(chieStOnly, 26, majorBoss)] = 65.0;
        var dMajorBoss = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieStOnly,
            Allies = new() { chieStOnly },
            Enemies = new() { majorBoss }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, dMajorBoss.ActionKind);
    }

    [Fact]
    public void AilmentBitmasks_HybridCureItems_AndActFreelyCuring_UseRealSkillTblMasks()
    {
        // 1. Verify exact SKILL.TBL StateFlags bitmasks
        Assert.True(MakeParty(2, TacticMode.ActFreely, 200, 200, 50, 50, Array.Empty<ushort>(), stateFlags: 0x02).HasLossOfControl); // Rage
        Assert.True(MakeParty(2, TacticMode.ActFreely, 200, 200, 50, 50, Array.Empty<ushort>(), stateFlags: 0x04).HasLossOfControl); // Fear
        Assert.True(MakeParty(2, TacticMode.ActFreely, 200, 200, 50, 50, Array.Empty<ushort>(), stateFlags: 0x10).HasLossOfControl); // Confuse
        Assert.False(MakeParty(2, TacticMode.ActFreely, 200, 200, 50, 50, Array.Empty<ushort>(), stateFlags: 0x40).HasLossOfControl); // Exhaust is NOT turn loss
        Assert.True(MakeParty(2, TacticMode.ActFreely, 200, 200, 50, 50, Array.Empty<ushort>(), stateFlags: 0x02).HasCriticalCuringAilment);
        Assert.True(MakeParty(2, TacticMode.ActFreely, 200, 200, 50, 50, Array.Empty<ushort>(), stateFlags: 0x10).HasCriticalCuringAilment);
        Assert.False(MakeParty(2, TacticMode.ActFreely, 200, 200, 50, 50, Array.Empty<ushort>(), stateFlags: 0x08).HasCriticalCuringAilment); // Silence

        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // Hybrid consumables #795 (Muscle Drink: Rage 0x02), #797 (Rancid Gravy: Silence 0x08), plus #788 (Sedative: 0x16), #793 (Amrita Soda: Rare)
        var skMuscle = bridge.Skills[314] = new SkillMetadata { SkillId = 314, Name = "Muscle Drink", Element = ElementId.Recovery, TargetSide = 1, EffectType = 5, Power = 100, AilmentMask = 0x02 };
        var skGravy = bridge.Skills[316] = new SkillMetadata { SkillId = 316, Name = "Rancid Gravy", Element = ElementId.Recovery, TargetSide = 1, EffectType = 5, Power = 100, AilmentMask = 0x08 };
        var skSedative = bridge.Skills[310] = new SkillMetadata { SkillId = 310, Name = "Sedative", Element = ElementId.Recovery, TargetSide = 1, EffectType = 0, Power = 15, AilmentMask = 0x16 };
        var skAmritaSoda = bridge.Skills[312] = new SkillMetadata { SkillId = 312, Name = "Amrita Soda", Element = ElementId.Recovery, Scope = 1, TargetSide = 1, EffectType = 0, Power = 15, AilmentMask = 0xFF };

        var muscleDrink = new BattleConsumableInfo { ItemId = 795, Name = "Muscle Drink", SkillId = 314, Quantity = 5, Kind = BattleConsumableKind.Heal, Rarity = ConsumableRarityTier.Common, Skill = skMuscle, EstimatedHpGain = 100 };
        var rancidGravy = new BattleConsumableInfo { ItemId = 797, Name = "Rancid Gravy", SkillId = 316, Quantity = 5, Kind = BattleConsumableKind.Heal, Rarity = ConsumableRarityTier.Common, Skill = skGravy, EstimatedHpGain = 100 };
        var sedative = new BattleConsumableInfo { ItemId = 788, Name = "Sedative", SkillId = 310, Quantity = 5, Kind = BattleConsumableKind.CureAilment, Rarity = ConsumableRarityTier.Common, Skill = skSedative };
        var amritaSoda = new BattleConsumableInfo { ItemId = 793, Name = "Amrita Soda", SkillId = 312, Quantity = 3, Kind = BattleConsumableKind.CureAilment, Rarity = ConsumableRarityTier.Rare, Skill = skAmritaSoda };

        Assert.True(muscleDrink.CanCureAilment);
        Assert.True(rancidGravy.CanCureAilment);
        Assert.True(amritaSoda.CanCureAilment);
        Assert.Equal(ConsumableRarityTier.Rare, amritaSoda.Rarity);

        bridge.AvailableConsumables.AddRange(new[] { muscleDrink, rancidGravy, sedative, amritaSoda });

        var boss = MakeEnemy(259, 0, 1200, 1800);
        bridge.Affinities[(boss, ElementId.Wind)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(boss, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        // 2. Real Confuse (0x10) before turn -> Sedative (788) cures sabotage without double ActorOpportunityCost blocking it
        var yosuke = MakeParty(2, TacticMode.ActFreely, 250, 260, 120, 150, new ushort[] { 14 });
        var kanjiConfusedReal = MakeParty(6, TacticMode.FullAssault, 450, 500, 100, 100, new ushort[] { 138 }, stateFlags: 0x10);
        bridge.ActorSingleHitDamages[(yosuke, 14, boss)] = 95.0;
        bridge.ActorSingleHitDamages[(kanjiConfusedReal, 138, boss)] = 250.0;

        var dConfuseReal = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, kanjiConfusedReal },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiConfusedReal, boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dConfuseReal.ActionKind);
        Assert.Equal((ushort)788, dConfuseReal.ItemId);

        // 3. Real Rage (0x02) -> hybrid Muscle Drink (795, Common) cures Rage (and heals) in ActFreely!
        bridge.AvailableConsumables.Clear();
        bridge.AvailableConsumables.Add(muscleDrink);
        var kanjiEnraged = MakeParty(6, TacticMode.FullAssault, 320, 500, 100, 100, new ushort[] { 138 }, stateFlags: 0x02);
        bridge.ActorSingleHitDamages[(kanjiEnraged, 138, boss)] = 220.0;
        var dMuscleRage = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, kanjiEnraged },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { kanjiEnraged, boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dMuscleRage.ActionKind);
        Assert.Equal((ushort)795, dMuscleRage.ItemId);

        // 4. Healer Silence (0x08) when teammate is injured (< 65% HP) -> hybrid Rancid Gravy (797, Common) cures Yukiko!
        bridge.AvailableConsumables.Clear();
        bridge.AvailableConsumables.Add(rancidGravy);
        var yukikoSilenced = MakeParty(4, TacticMode.HealSupport, 180, 200, 150, 200, new ushort[] { 196, 3 }, stateFlags: 0x08);
        var chieInjured = MakeParty(3, TacticMode.ActFreely, 120, 226, 80, 100, new ushort[] { 122 });
        bridge.ActorSingleHitDamages[(yukikoSilenced, 3, boss)] = 110.0;
        bridge.ActorSingleHitDamages[(chieInjured, 122, boss)] = 95.0;
        var dSilenceHealer = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke,
            Allies = new() { yosuke, yukikoSilenced, chieInjured },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { yukikoSilenced, chieInjured, boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dSilenceHealer.ActionKind);
        Assert.Equal((ushort)797, dSilenceHealer.ItemId);
        Assert.Same(yukikoSilenced, dSilenceHealer.Target);

        // 5. Mid-tier Hiranya (794, penalty 30) cures Exhaust (0x40) in multi-round boss fight & Dizzy (0x40000) with Re Patra (201)
        var skHiranya = bridge.Skills[313] = new SkillMetadata { SkillId = 313, Name = "Hiranya", Element = ElementId.Recovery, TargetSide = 1, EffectType = 0, Power = 15, AilmentMask = 0xFF };
        var hiranya = new BattleConsumableInfo { ItemId = 794, Name = "Hiranya", SkillId = 313, Quantity = 4, Kind = BattleConsumableKind.CureAilment, Rarity = ConsumableRarityTier.Mid, Skill = skHiranya };
        bridge.AvailableConsumables.Clear();
        bridge.AvailableConsumables.Add(hiranya);

        var yukikoExhausted = MakeParty(4, TacticMode.HealSupport, 120, 200, 180, 200, new ushort[] { 3 }, stateFlags: 0x40);
        var yosukeSupport = MakeParty(2, TacticMode.ActFreely, 250, 260, 120, 150, new ushort[] { 14 });
        bridge.ActorSingleHitDamages[(yosukeSupport, 14, boss)] = 60.0;
        bridge.ActorSingleHitDamages[(yukikoExhausted, 3, boss)] = 80.0;
        var dExhaustMidItem = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeSupport,
            Allies = new() { yosukeSupport, yukikoExhausted },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { yukikoExhausted, boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dExhaustMidItem.ActionKind);
        Assert.Equal((ushort)794, dExhaustMidItem.ItemId);

        bridge.AvailableConsumables.Clear();
        var yosukeRePatra = MakeParty(2, TacticMode.ActFreely, 250, 260, 120, 150, new ushort[] { 14, 201 });
        var chieDizzy = MakeParty(3, TacticMode.FullAssault, 200, 226, 80, 100, new ushort[] { 122 }, stateFlags: 0x40000);
        bridge.ActorSingleHitDamages[(yosukeRePatra, 14, boss)] = 95.0;
        bridge.ActorSingleHitDamages[(chieDizzy, 122, boss)] = 150.0;
        var dDizzyCure = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeRePatra,
            Allies = new() { yosukeRePatra, chieDizzy },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { chieDizzy, boss }
        }, cfg)!;
        Assert.Equal((ushort)201, dDizzyCure.SkillId);
        Assert.Same(chieDizzy, dDizzyCure.Target);
    }

    [Fact]
    public void ChargedBossWithDeadAlly_NonReviversAndPartialOnlyHealthySurvivorsDoNotFalselyGuard()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var revivalBeadSkill = bridge.Skills[297] = new SkillMetadata
        {
            SkillId = 297, Name = "Revival Bead", Element = ElementId.Recovery,
            TargetSide = 1, TargetCond = 4, EffectType = 11, Power = 50, AilmentMask = 0x80000
        };
        var balmSkill = bridge.Skills[298] = new SkillMetadata
        {
            SkillId = 298, Name = "Balm of Life", Element = ElementId.Recovery,
            TargetSide = 1, TargetCond = 4, EffectType = 11, Power = 100, AilmentMask = 0x80000
        };

        var bossPowerCharged = MakeEnemy(259, 0, 1400, 1800, powerCharge: true, agility: 22);
        var kanjiDead = MakeParty(6, TacticMode.ActFreely, 0, 340, 80, 100, new ushort[] { 138 }, stateFlags: 0x80000, agility: 18);

        // 1. 100% HP non-reviver (no revive skill, no revive items) attacks instead of Guarding
        var yosukeNoRevive = MakeParty(2, TacticMode.ActFreely, 260, 260, 120, 150, new ushort[] { 14 }, agility: 30);
        bridge.ActorSingleHitDamages[(yosukeNoRevive, 14, bossPowerCharged)] = 85.0;
        var dNonReviver100 = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosukeNoRevive,
            Allies = new() { yosukeNoRevive, kanjiDead },
            Enemies = new() { bossPowerCharged }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dNonReviver100.ActionKind);
        Assert.Equal((ushort)14, dNonReviver100.SkillId);

        // 2. 80% HP non-reviver heals critical (< 30%) Protagonist instead of falsely Guarding before non-existent revive
        var mcCrit = MakeParty(1, TacticMode.DirectCommands, 60, 300, 100, 150, Array.Empty<ushort>(), agility: 28);
        var yosuke80Healer = MakeParty(2, TacticMode.ActFreely, 208, 260, 120, 150, new ushort[] { 192, 14 }, agility: 30);
        bridge.ActorSingleHitDamages[(yosuke80Healer, 14, bossPowerCharged)] = 85.0;
        var dNonReviver80SavesMc = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke80Healer,
            Allies = new() { mcCrit, yosuke80Healer, kanjiDead },
            Enemies = new() { bossPowerCharged }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dNonReviver80SavesMc.ActionKind);
        Assert.Equal((ushort)192, dNonReviver80SavesMc.SkillId);
        Assert.Same(mcCrit, dNonReviver80SavesMc.Target);

        // 3. Generic Mind-Charged boss + dead ally: 100% HP Fire-weak non-reviver attacks instead of Guard loop
        var genericMindCharged = MakeEnemy(99, 0, 2000, 2000, mindCharge: true, agility: 20);
        var chie100Weak = MakeParty(3, TacticMode.ActFreely, 320, 320, 90, 90, new ushort[] { 122 }, agility: 25);
        bridge.Affinities[(chie100Weak, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.ActorSingleHitDamages[(chie100Weak, 122, genericMindCharged)] = 110.0;
        var dMindChargeNonReviver = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie100Weak,
            Allies = new() { chie100Weak, kanjiDead },
            Enemies = new() { genericMindCharged }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dMindChargeNonReviver.ActionKind);
        Assert.Equal((ushort)122, dMindChargeNonReviver.SkillId);

        // 4. > 85% HP actor with ONLY suppressed partial revive (Recarm) attacks instead of Guarding or casting Recarm
        var yukiko100Partial = MakeParty(4, TacticMode.ActFreely, 200, 200, 150, 200, new ushort[] { 209, 3 }, agility: 25);
        bridge.ActorSingleHitDamages[(yukiko100Partial, 3, bossPowerCharged)] = 120.0;
        var dPartial100 = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko100Partial,
            Allies = new() { yukiko100Partial, kanjiDead },
            Enemies = new() { bossPowerCharged }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dPartial100.ActionKind);
        Assert.Equal((ushort)3, dPartial100.SkillId);

        // 5. ConserveSp (Samarecarm 18 SP > 8 SP cap, Balm of Life blocked by commonItemsOnly/AllowRareConsumables=false)
        //    takes 0-SP AOA/kill or attacks without falsely Guarding; also respects EnableSupportItemsInAi=false
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 784, Name = "Balm of Life", SkillId = 298, Quantity = 3,
            Kind = BattleConsumableKind.Revive, Rarity = ConsumableRarityTier.Rare, Skill = balmSkill
        });
        var lowAdd = MakeEnemy(15, 1, 40, 200, agility: 10);
        var yukikoConserve = MakeParty(4, TacticMode.ConserveSp, 160, 200, 150, 200, new ushort[] { 210, 3 }, agility: 25);
        bridge.ActorSingleHitDamages[(yukikoConserve, 0, lowAdd)] = 60.0;
        bridge.ActorSingleHitDamages[(yukikoConserve, 0, bossPowerCharged)] = 30.0;
        var dConserveNoRevive = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoConserve,
            Allies = new() { yukikoConserve, kanjiDead },
            Enemies = new() { bossPowerCharged, lowAdd }
        }, cfg)!;
        Assert.Equal(AiActionKind.NormalAttack, dConserveNoRevive.ActionKind);
        Assert.Same(lowAdd, dConserveNoRevive.Target);

        // With EnableSupportItemsInAi=false and Revival Bead only in inventory, 80% HP actor does not falsely Guard
        bridge.AvailableConsumables.Clear();
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 783, Name = "Revival Bead", SkillId = 297, Quantity = 5,
            Kind = BattleConsumableKind.Revive, Rarity = ConsumableRarityTier.Common, Skill = revivalBeadSkill
        });
        var cfgNoItems = DefaultConfig();
        cfgNoItems.EnableSupportItemsInAi = false;
        var yosuke80NoItems = MakeParty(2, TacticMode.ActFreely, 208, 260, 120, 150, new ushort[] { 14 }, agility: 30);
        bridge.ActorSingleHitDamages[(yosuke80NoItems, 14, bossPowerCharged)] = 85.0;
        var dNoItemsNoGuard = engine.DecideAction(new BattleSnapshot
        {
            Actor = yosuke80NoItems,
            Allies = new() { yosuke80NoItems, kanjiDead },
            Enemies = new() { bossPowerCharged }
        }, cfgNoItems)!;
        Assert.Equal(AiActionKind.Skill, dNoItemsNoGuard.ActionKind);
        Assert.Equal((ushort)14, dNoItemsNoGuard.SkillId);
    }

    [Fact]
    public void ChargedBossRevive_SkipsChargeWeakDeadAllyForGenericBosses_AndHandlesUpcomingOnlyBossActedFallback()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var chieFireWeakDead = MakeParty(3, TacticMode.ActFreely, 0, 230, 80, 100, new ushort[] { 26 }, stateFlags: 0x80000, agility: 25);
        var kanjiSafeDead = MakeParty(6, TacticMode.ActFreely, 0, 340, 80, 100, new ushort[] { 138 }, stateFlags: 0x80000, agility: 18);
        var yukikoFull = MakeParty(4, TacticMode.HealSupport, 200, 200, 150, 200, new ushort[] { 210, 3 }, agility: 30);

        bridge.Affinities[(chieFireWeakDead, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(chieFireWeakDead, ElementId.Phys)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };

        // 1. Generic Mind-Charged boss (empty TelegraphedElements): skips Fire-weak Chie (Agi 25) and revives neutral Kanji (Agi 18)
        var genericMindBoss = MakeEnemy(99, 0, 2000, 2000, mindCharge: true, agility: 20);
        var dMindReviveSafe = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoFull,
            Allies = new() { yukikoFull, chieFireWeakDead, kanjiSafeDead },
            Enemies = new() { genericMindBoss }
        }, cfg)!;
        Assert.Equal((ushort)210, dMindReviveSafe.SkillId);
        Assert.Same(kanjiSafeDead, dMindReviveSafe.Target);

        // 2. Generic Power-Charged boss: skips Phys-weak Chie and revives neutral Kanji
        var genericPowerBoss = MakeEnemy(99, 0, 2000, 2000, powerCharge: true, agility: 20);
        var dPowerReviveSafe = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoFull,
            Allies = new() { yukikoFull, chieFireWeakDead, kanjiSafeDead },
            Enemies = new() { genericPowerBoss }
        }, cfg)!;
        Assert.Equal((ushort)210, dPowerReviveSafe.SkillId);
        Assert.Same(kanjiSafeDead, dPowerReviveSafe.Target);

        // 3. If ONLY charge-weak Chie is dead, 100% HP non-weak Yukiko neither revives Chie nor falsely Guards -> attacks
        bridge.ActorSingleHitDamages[(yukikoFull, 3, genericMindBoss)] = 120.0;
        var dOnlyChargeWeakDead = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoFull,
            Allies = new() { yukikoFull, chieFireWeakDead },
            Enemies = new() { genericMindBoss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dOnlyChargeWeakDead.ActionKind);
        Assert.Equal((ushort)3, dOnlyChargeWeakDead.SkillId);

        // 4. UpcomingRoundTurnOrder-only fallback when Charged boss already acted this round (bossIdx < 0)
        var bossFastActed = MakeEnemy(99, 0, 2000, 2000, powerCharge: true, agility: 30);
        var yosukeInQueue = MakeParty(2, TacticMode.ActFreely, 250, 260, 120, 150, new ushort[] { 14 }, agility: 15);
        var yukiko80 = MakeParty(4, TacticMode.HealSupport, 160, 200, 150, 200, new ushort[] { 210, 3 }, agility: 20);
        var allyNextRoundSlower = MakeParty(3, TacticMode.ActFreely, 0, 230, 80, 100, new ushort[] { 26 }, stateFlags: 0x80000, agility: 25);
        var allyThisRound = MakeParty(6, TacticMode.ActFreely, 0, 340, 80, 100, new ushort[] { 138 }, stateFlags: 0x80000, agility: 12);
        var allyNextRoundFaster = MakeParty(7, TacticMode.ActFreely, 0, 220, 140, 180, new ushort[] { 53 }, stateFlags: 0x80000, agility: 40);

        var snapUpcomingOnly = new BattleSnapshot
        {
            Actor = yukiko80,
            Allies = new() { yukiko80, yosukeInQueue, allyNextRoundSlower, allyThisRound, allyNextRoundFaster },
            Enemies = new() { bossFastActed },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { yosukeInQueue }
        };
        Assert.True(SupportAndRecoveryEvaluator.DoesChargedBossActAheadOfAlly(snapUpcomingOnly, new() { bossFastActed }, allyNextRoundSlower));
        Assert.False(SupportAndRecoveryEvaluator.DoesChargedBossActAheadOfAlly(snapUpcomingOnly, new() { bossFastActed }, allyThisRound));
        Assert.False(SupportAndRecoveryEvaluator.DoesChargedBossActAheadOfAlly(snapUpcomingOnly, new() { bossFastActed }, allyNextRoundFaster));
    }

    [Fact]
    public void ActFreely_CustomEmergencyHpThresholdAbove45_HealsBeforeReviveAndInNormalTriage()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var boss = MakeEnemy(259, 0, 1500, 1800, agility: 22);

        var cfgCustom55 = DefaultConfig();
        cfgCustom55.ActFreelyEmergencyHpThreshold = 55;
        var cfgDefault40 = DefaultConfig();

        // Case A: Pre-revive self-heal at 50% HP with ActFreelyEmergencyHpThreshold = 55 (Step 3)
        var yukiko50 = MakeParty(4, TacticMode.ActFreely, 100, 200, 140, 200, new ushort[] { 193, 209, 3 }, agility: 25);
        var chieDead = MakeParty(3, TacticMode.ActFreely, 0, 230, 80, 100, new ushort[] { 26 }, stateFlags: 0x80000, agility: 20);
        bridge.SingleHitDamages[(3, boss)] = 150.0;

        var dSelfHeal55 = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko50,
            Allies = new() { yukiko50, chieDead },
            Enemies = new() { boss }
        }, cfgCustom55)!;
        Assert.Equal(AiActionKind.Skill, dSelfHeal55.ActionKind);
        Assert.Equal((ushort)193, dSelfHeal55.SkillId);
        Assert.Same(yukiko50, dSelfHeal55.Target);

        // Case B: Step 5 emergency heal for 50% ally when no one is dead
        var yukikoFull = MakeParty(4, TacticMode.ActFreely, 200, 200, 140, 200, new ushort[] { 193, 3 }, agility: 25);
        var chie50 = MakeParty(3, TacticMode.ActFreely, 115, 230, 80, 100, new ushort[] { 26 }, agility: 20);

        var dAllyHeal55 = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoFull,
            Allies = new() { yukikoFull, chie50 },
            Enemies = new() { boss }
        }, cfgCustom55)!;
        Assert.Equal(AiActionKind.Skill, dAllyHeal55.ActionKind);
        Assert.Equal((ushort)193, dAllyHeal55.SkillId);
        Assert.Same(chie50, dAllyHeal55.Target);

        // Default threshold (40%) keeps attacking at 50% ally HP
        var dDefaultAttack = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoFull,
            Allies = new() { yukikoFull, chie50 },
            Enemies = new() { boss }
        }, cfgDefault40)!;
        Assert.Equal(AiActionKind.Skill, dDefaultAttack.ActionKind);
        Assert.Equal((ushort)3, dDefaultAttack.SkillId);
    }

    [Fact]
    public void BossStabilize_SingleInjuredSurvivor_FallsBackToAoEHealItemOnlyWhenNoSingleTargetItemExists()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var revivalBeadSkill = bridge.Skills[297] = new SkillMetadata
        {
            SkillId = 297, Name = "Revival Bead", Element = ElementId.Recovery,
            TargetSide = 1, TargetCond = 4, EffectType = 11, Power = 50, AilmentMask = 0x80000
        };
        var valMedSkill = bridge.Skills[294] = new SkillMetadata
        {
            SkillId = 294, Name = "Value Medicine", Element = ElementId.Recovery,
            Scope = 1, TargetSide = 1, EffectType = 5, Power = 100
        };
        var medSkill = bridge.Skills[305] = new SkillMetadata
        {
            SkillId = 305, Name = "Medicine", Element = ElementId.Recovery,
            Scope = 0, TargetSide = 1, EffectType = 2, Power = 120
        };
        var beadChainSkill = bridge.Skills[296] = new SkillMetadata
        {
            SkillId = 296, Name = "Bead Chain", Element = ElementId.Recovery,
            Scope = 1, TargetSide = 1, EffectType = 11, Power = 100
        };

        var revivalBead = new BattleConsumableInfo
        {
            ItemId = 783, Name = "Revival Bead", SkillId = 297, Quantity = 5,
            Kind = BattleConsumableKind.Revive, Rarity = ConsumableRarityTier.Common, Skill = revivalBeadSkill
        };
        var valueMed = new BattleConsumableInfo
        {
            ItemId = 775, Name = "Value Medicine", SkillId = 294, Quantity = 5,
            Kind = BattleConsumableKind.Heal, Rarity = ConsumableRarityTier.Mid, Skill = valMedSkill, EstimatedHpGain = 100
        };
        var medicine = new BattleConsumableInfo
        {
            ItemId = 650, Name = "Medicine", SkillId = 305, Quantity = 5,
            Kind = BattleConsumableKind.Heal, Rarity = ConsumableRarityTier.Common, Skill = medSkill, EstimatedHpGain = 120
        };
        var beadChain = new BattleConsumableInfo
        {
            ItemId = 777, Name = "Bead Chain", SkillId = 296, Quantity = 3,
            Kind = BattleConsumableKind.Heal, Rarity = ConsumableRarityTier.Rare, Skill = beadChainSkill
        };

        var boss = MakeEnemy(259, 0, 1400, 1800);
        var mc = MakeParty(1, TacticMode.DirectCommands, 280, 300, 100, 150, Array.Empty<ushort>());
        var yosuke50 = MakeParty(2, TacticMode.ActFreely, 130, 260, 100, 150, new ushort[] { 14 });
        var chieActor = MakeParty(3, TacticMode.ActFreely, 250, 250, 100, 100, new ushort[] { 14 });
        var kanjiDead = MakeParty(6, TacticMode.ActFreely, 0, 340, 80, 100, new ushort[] { 138 }, stateFlags: 0x80000);
        bridge.SingleHitDamages[(14, boss)] = 90.0;

        // Case A: Single 50% survivor with only Value Medicine + Revival Bead -> stabilizes via Value Medicine
        bridge.AvailableConsumables.AddRange(new[] { revivalBead, valueMed });
        var dAoEFallback = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieActor,
            Allies = new() { mc, yosuke50, chieActor, kanjiDead },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dAoEFallback.ActionKind);
        Assert.Equal((ushort)775, dAoEFallback.ItemId);
        Assert.NotNull(dAoEFallback.Targets);
        Assert.Equal(3, dAoEFallback.Targets!.Count);

        // Case B: Single-target Medicine (650) available alongside Value Medicine (775) -> uses Medicine, preserves AoE item
        bridge.AvailableConsumables.Add(medicine);
        var dSinglePreferred = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieActor,
            Allies = new() { mc, yosuke50, chieActor, kanjiDead },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Item, dSinglePreferred.ActionKind);
        Assert.Equal((ushort)650, dSinglePreferred.ItemId);
        Assert.Same(yosuke50, dSinglePreferred.Target);

        // Case C: Rare Bead Chain only with AllowRareConsumables = false -> protected, falls back to Garu (14)
        bridge.AvailableConsumables.Clear();
        bridge.AvailableConsumables.Add(beadChain);
        var dRareProtected = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieActor,
            Allies = new() { mc, yosuke50, chieActor, kanjiDead },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dRareProtected.ActionKind);
        Assert.Equal((ushort)14, dRareProtected.SkillId);
    }

    [Fact]
    public void FullAssault_SingleTarget_PrefersBufuOverMabufuRegardlessOfSlotOrderAndFollowup()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig(respectAnalysis: true);

        // EquippedSkills has Mabufu (29) BEFORE Bufu (26)
        var chie = MakeParty(3, TacticMode.FullAssault, 220, 226, 96, 106, new ushort[] { 29, 26 });

        // 1. Single revealed neutral enemy -> Bufu (26) beats Mabufu (29)
        var neutralShadow = MakeEnemy(15, 0, 500, 500);
        bridge.Affinities[(neutralShadow, ElementId.Ice)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.ActorSingleHitDamages[(chie, 26, neutralShadow)] = 65.0;
        bridge.ActorSingleHitDamages[(chie, 29, neutralShadow)] = 65.0;

        var dNeutral = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie },
            Enemies = new() { neutralShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dNeutral.ActionKind);
        Assert.Equal((ushort)26, dNeutral.SkillId);

        // 2. Single revealed Ice-weak enemy -> initial turn uses Bufu (26) with Bufu followup (not Mabufu),
        //    and actual 1-More turn also uses Bufu (26)
        var iceWeakShadow = MakeEnemy(16, 0, 500, 500);
        bridge.Affinities[(iceWeakShadow, ElementId.Ice)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.ActorSingleHitDamages[(chie, 26, iceWeakShadow)] = 81.0;
        bridge.ActorSingleHitDamages[(chie, 29, iceWeakShadow)] = 81.0;

        var dWeakInitial = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie },
            Enemies = new() { iceWeakShadow },
            IsActorOnOneMore = false
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dWeakInitial.ActionKind);
        Assert.Equal((ushort)26, dWeakInitial.SkillId);
        Assert.Contains("Bufu ->", dWeakInitial.Reason);
        Assert.DoesNotContain("Mabufu", dWeakInitial.Reason);

        var iceWeakDown = MakeEnemy(16, 0, 419, 500, isDown: true);
        bridge.Affinities[(iceWeakDown, ElementId.Ice)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.ActorSingleHitDamages[(chie, 26, iceWeakDown)] = 81.0;
        bridge.ActorSingleHitDamages[(chie, 29, iceWeakDown)] = 81.0;

        var dWeakOneMore = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie },
            Enemies = new() { iceWeakDown },
            IsActorOnOneMore = true
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dWeakOneMore.ActionKind);
        Assert.Equal((ushort)26, dWeakOneMore.SkillId);

        // 3. Two enemies where one Nulls Ice (enemiesDamaged == 1) -> Bufu (26) on the non-Null enemy
        var iceNullShadow = MakeEnemy(17, 1, 500, 500);
        bridge.Affinities[(iceNullShadow, ElementId.Ice)] = new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };
        bridge.ActorSingleHitDamages[(chie, 26, iceNullShadow)] = 0.0;
        bridge.ActorSingleHitDamages[(chie, 29, iceNullShadow)] = 0.0;

        var dOneNull = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie },
            Enemies = new() { neutralShadow, iceNullShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dOneNull.ActionKind);
        Assert.Equal((ushort)26, dOneNull.SkillId);
        Assert.Same(neutralShadow, dOneNull.Target);

        // 4. Two revealed neutral enemies (enemiesDamaged == 2) -> Mabufu (29) wins multi-target
        var neutralShadow2 = MakeEnemy(18, 1, 500, 500);
        bridge.Affinities[(neutralShadow2, ElementId.Ice)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.ActorSingleHitDamages[(chie, 26, neutralShadow2)] = 65.0;
        bridge.ActorSingleHitDamages[(chie, 29, neutralShadow2)] = 65.0;

        var dTwoNeutral = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { chie },
            Enemies = new() { neutralShadow, neutralShadow2 }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dTwoNeutral.ActionKind);
        Assert.Equal((ushort)29, dTwoNeutral.SkillId);

        // 5. Single revealed neutral enemy with ONLY Mabufu (29) vs equal-damage Common damage item -> reusable Mabufu beats item
        var dryIceSkill = bridge.Skills[402] = new SkillMetadata
        {
            SkillId = 402, Name = "Dry Ice", Element = ElementId.Ice, CostType = 0,
            Scope = 0, TargetSide = 2, EffectType = 4, Power = 65, Accuracy = 100
        };
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 602, Name = "Dry Ice", SkillId = 402, Quantity = 3,
            Kind = BattleConsumableKind.Damage, Rarity = ConsumableRarityTier.Common, Skill = dryIceSkill
        });
        var chieMabufuOnly = MakeParty(3, TacticMode.FullAssault, 220, 226, 96, 106, new ushort[] { 29 });
        bridge.ActorSingleHitDamages[(chieMabufuOnly, 29, neutralShadow)] = 65.0;
        bridge.ActorSingleHitDamages[(chieMabufuOnly, 402, neutralShadow)] = 65.0;

        var dMabufuBeatsItem = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieMabufuOnly,
            Allies = new() { chieMabufuOnly },
            Enemies = new() { neutralShadow }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dMabufuBeatsItem.ActionKind);
        Assert.Equal((ushort)29, dMabufuBeatsItem.SkillId);
    }

    [Fact]
    public void BreakFallback_TargetsActualResistantEnemy_NotFirstSlot()
    {
        var bridge = new MockBattleBridge();
        bridge.Skills[0] = new SkillMetadata { SkillId = 0, Name = "Attack", Element = ElementId.Phys, CostType = 0, Scope = 0, TargetSide = 2, EffectType = 1, Power = 15, Accuracy = 100 };
        bridge.Skills[1] = new SkillMetadata { SkillId = 1, Name = "Agi", Element = ElementId.Fire, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 80, Accuracy = 100 };
        bridge.Skills[236] = new SkillMetadata { SkillId = 236, Name = "Fire Break", Element = ElementId.Support, CostType = 2, BaseCost = 15, Scope = 0, TargetSide = 2, BuffMask = SkillCatalog.BuffFireBreak };

        var yukiko = MakeParty(4, TacticMode.FullAssault, 220, 220, 100, 120, new ushort[] { 1, 236, 0, 0, 0, 0, 0, 0 });
        var alreadyBroken = new CombatantSnapshot
        {
            SlotId = 0, IsEnemy = true, UnitOrEnemyId = 10, DisplayName = "Enemy0_AlreadyBroken",
            CurrentHp = 400, MaxHp = 400, HasFireBreak = true
        };
        var needsBreak = MakeEnemy(11, 1, 400, 400);

        // Both block all direct attacks (Phys Null, Fire Null on alreadyBroken via Makarakarn / Fire Null on needsBreak)
        bridge.Affinities[(alreadyBroken, ElementId.Phys)] = new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };
        bridge.Affinities[(alreadyBroken, ElementId.Fire)] = new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };
        bridge.Affinities[(needsBreak, ElementId.Phys)] = new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };
        bridge.Affinities[(needsBreak, ElementId.Fire)] = new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };

        var engine = new SmartPartyAiEngine(bridge);
        var decision = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko },
            Enemies = new() { alreadyBroken, needsBreak }
        }, new Config { RespectAnalysisState = true, EnableDamageItemsInAi = false })!;

        Assert.Equal(AiActionKind.Skill, decision.ActionKind);
        Assert.Equal((ushort)236, decision.SkillId);
        Assert.Same(needsBreak, decision.Target);
    }

    [Fact]
    public void HeuristicRebellion_IgnoresEmptySkillSlots_PrefersPhysicalCarry()
    {
        var bridge = new MockBattleBridge();
        bridge.Skills[0] = new SkillMetadata { SkillId = 0, Name = "Attack", Element = ElementId.Phys, CostType = 0, Scope = 0, TargetSide = 2, EffectType = 1, Power = 15, Accuracy = 100 };
        bridge.Skills[1] = new SkillMetadata { SkillId = 1, Name = "Agi", Element = ElementId.Fire, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, EffectType = 1, Power = 80, Accuracy = 100 };
        bridge.Skills[120] = new SkillMetadata { SkillId = 120, Name = "Bash", Element = ElementId.Phys, CostType = 1, BaseCost = 10, Scope = 0, TargetSide = 2, EffectType = 1, Power = 90, Accuracy = 100 };
        bridge.Skills[121] = new SkillMetadata { SkillId = 121, Name = "Cleave", Element = ElementId.Phys, CostType = 1, BaseCost = 12, Scope = 0, TargetSide = 2, EffectType = 1, Power = 100, Accuracy = 100 };
        bridge.Skills[192] = new SkillMetadata { SkillId = 192, Name = "Dia", Element = ElementId.Recovery, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 1, EffectType = 2, Power = 50 };
        bridge.Skills[234] = new SkillMetadata { SkillId = 234, Name = "Rebellion", Element = ElementId.Support, CostType = 2, BaseCost = 5, Scope = 0, TargetSide = 1, BuffMask = SkillCatalog.BuffCritRateUp };

        // Yukiko has 6 empty skill slots (0); Chie has 2 real Phys skills and 0 empty slots.
        // No damage model configured so EvaluateHealSupport falls through to heuristic Rebellion selection.
        var yukiko = MakeParty(4, TacticMode.HealSupport, 220, 220, 120, 120, new ushort[] { 1, 192, 0, 0, 0, 0, 0, 0 }, atkStage: 1);
        var chie = MakeParty(3, TacticMode.HealSupport, 240, 240, 90, 90, new ushort[] { 120, 121, 234, 1, 1, 1, 1, 1 }, atkStage: 1);
        var boss = MakeEnemy(259, 0, 1500, 1500);

        var engine = new SmartPartyAiEngine(bridge);
        var decision = engine.DecideAction(new BattleSnapshot
        {
            Actor = chie,
            Allies = new() { yukiko, chie },
            Enemies = new() { boss }
        }, new Config { EnableTelegraphGuard = false })!;

        Assert.Equal(AiActionKind.Skill, decision.ActionKind);
        Assert.Equal((ushort)234, decision.SkillId);
        Assert.Same(chie, decision.Target);
    }

    [Fact]
    public void SkillCatalog_IncludesVerifiedCureSkillNames()
    {
        Assert.Equal("Re Patra", SkillCatalog.GetSkillName(201));
        Assert.Equal("Posumudi", SkillCatalog.GetSkillName(202));
        Assert.Equal("Mutudi", SkillCatalog.GetSkillName(203));
        Assert.Equal("Enradi", SkillCatalog.GetSkillName(204));
        Assert.Equal("Tirundi", SkillCatalog.GetSkillName(205));
        Assert.Equal("Nervundi", SkillCatalog.GetSkillName(206));
        Assert.Equal("Energy Shower", SkillCatalog.GetSkillName(207));
    }

    [Fact]
    public void OneMoreFollowup_ActFreelyAndConserveSp_NeverHallucinateBlockedNeutralChipItems()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var kanji = MakeEnemy(259, 0, 2000, 2000);
        var niceGuy = MakeEnemy(272, 1, 700, 800);
        var toughDown = MakeEnemy(273, 2, 2200, 2500, isDown: true);

        bridge.Affinities[(niceGuy, ElementId.Ice)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(niceGuy, ElementId.Fire)] = new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };
        bridge.Affinities[(toughDown, ElementId.Ice)] = new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };
        bridge.Affinities[(toughDown, ElementId.Fire)] = new AffinityInfo { IsWeak = true, IsRevealed = true, MultiplierPercent = 125 };
        bridge.Affinities[(kanji, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(kanji, ElementId.Ice)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        var dryIceSkill = new SkillMetadata { SkillId = 320, Name = "Dry Ice", Element = ElementId.Ice, Scope = 0, TargetSide = 2, EffectType = 4, Power = 50 };
        var aoeChipSkill = new SkillMetadata { SkillId = 321, Name = "Mega Bomb", Element = ElementId.Almighty, Scope = 1, TargetSide = 2, EffectType = 4, Power = 100 };
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 808, Name = "Dry Ice", SkillId = 320, Quantity = 3,
            Kind = BattleConsumableKind.Damage, Rarity = ConsumableRarityTier.Common, Skill = dryIceSkill
        });
        bridge.AvailableConsumables.Add(new BattleConsumableInfo
        {
            ItemId = 814, Name = "Mega Bomb", SkillId = 321, Quantity = 3,
            Kind = BattleConsumableKind.Damage, Rarity = ConsumableRarityTier.Mid, Skill = aoeChipSkill
        });

        // 1. ActFreely: Dry Ice (50) must NOT hallucinate neutral-chip Mega Bomb (100x3 - 55 = 245) as its 1-More follow-up;
        //    only legal skills (Maragi / Attack) can be projected on the 1-More turn.
        var yukiko = MakeParty(4, TacticMode.ActFreely, 192, 192, 162, 206, new ushort[] { 4 });
        bridge.SingleHitDamages[(320, niceGuy)] = 50.0;
        bridge.SingleHitDamages[(321, kanji)] = 100.0;
        bridge.SingleHitDamages[(321, niceGuy)] = 100.0;
        bridge.SingleHitDamages[(321, toughDown)] = 100.0;
        bridge.SingleHitDamages[(0, toughDown)] = 151.0;
        bridge.SingleHitDamages[(0, kanji)] = 60.0;
        bridge.SingleHitDamages[(0, niceGuy)] = 60.0;
        bridge.SingleHitDamages[(4, kanji)] = 80.0;
        bridge.SingleHitDamages[(4, toughDown)] = 120.0;

        var dActFreely = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { yukiko },
            Enemies = new() { kanji, niceGuy, toughDown }
        }, cfg)!;
        Assert.DoesNotContain("Mega Bomb", dActFreely.Reason);

        // 2. FullAssault (AllowDamageItemOutrank = true) DOES allow neutral-chip Mega Bomb follow-up when it outranks skills
        var yukikoAssault = MakeParty(4, TacticMode.FullAssault, 192, 192, 162, 206, new ushort[] { 4 });
        var dFullAssault = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoAssault,
            Allies = new() { yukikoAssault },
            Enemies = new() { kanji, niceGuy, toughDown }
        }, cfg)!;
        Assert.Contains("Mega Bomb", dFullAssault.Reason);

        // 3. Conserve SP never uses damage items in 1-More follow-up projections either
        var chieConserve = MakeParty(3, TacticMode.ConserveSp, 226, 226, 96, 106, new ushort[] { 26 }); // Bufu (4 SP)
        bridge.SingleHitDamages[(26, niceGuy)] = 40.0;
        bridge.SingleHitDamages[(0, toughDown)] = 140.0;
        var dConserve = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieConserve,
            Allies = new() { chieConserve },
            Enemies = new() { kanji, niceGuy, toughDown }
        }, cfg)!;
        Assert.DoesNotContain("Mega Bomb", dConserve.Reason);
    }

    [Fact]
    public void ActFreely_PoisonAndRageCureMath_RespectsPhysOnlyPoison_AndMultiRoundBossBleed()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.IncludesAttackerStatusMultipliersInDamage = true;
        bridge.Skills[202] = new SkillMetadata
        {
            SkillId = 202, Name = "Posumudi", Element = ElementId.Recovery,
            CostType = 2, BaseCost = 5, Scope = 0, TargetSide = 1, EffectType = 0, AilmentMask = 0x20
        };
        bridge.Skills[208] = new SkillMetadata
        {
            SkillId = 208, Name = "Amrita", Element = ElementId.Recovery,
            CostType = 2, BaseCost = 12, Scope = 1, TargetSide = 1, EffectType = 0, AilmentMask = 0xFF
        };

        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var boss = MakeEnemy(259, 0, 2000, 2000);
        bridge.Affinities[(boss, ElementId.Fire)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };
        bridge.Affinities[(boss, ElementId.Phys)] = new AffinityInfo { IsRevealed = true, MultiplierPercent = 100 };

        // 1. Healthy Poisoned Magic caster (Yukiko, 95% HP, Agidyne=300): Poison does NOT reduce Magic damage in P4G,
        //    so Chie (Attack=160) with Posumudi must NOT waste a turn curing healthy Magic-only Yukiko!
        var yukikoMagicPoisoned = MakeParty(4, TacticMode.FullAssault, 190, 200, 150, 150, new ushort[] { 3 }, stateFlags: 0x20);
        var supportActor = MakeParty(8, TacticMode.ActFreely, 300, 300, 150, 150, new ushort[] { 202, 122 });
        bridge.ActorSingleHitDamages[(yukikoMagicPoisoned, 3, boss)] = 300.0;
        bridge.ActorSingleHitDamages[(supportActor, 122, boss)] = 160.0;

        var dHealthyMagic = engine.DecideAction(new BattleSnapshot
        {
            Actor = supportActor,
            Allies = new() { supportActor, yukikoMagicPoisoned },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { yukikoMagicPoisoned, boss }
        }, cfg)!;
        Assert.Equal((ushort)122, dHealthyMagic.SkillId);

        // 2. Multi-round boss fight where BOTH MC and Yosuke (Phys) are Poisoned at ~55% HP:
        //    AoE Amrita recovers Phys penalty + multi-round HP bleed for both allies, outscoring a 160-dmg attack!
        var mcPoisoned = MakeParty(1, TacticMode.DirectCommands, 150, 280, 120, 120, new ushort[] { 122 }, stateFlags: 0x20);
        var yosukePoisoned = MakeParty(2, TacticMode.ActFreely, 140, 260, 100, 150, new ushort[] { 122 }, stateFlags: 0x20);
        var amritaSupport = MakeParty(4, TacticMode.ActFreely, 220, 220, 150, 180, new ushort[] { 208, 1 });
        // Native bridge already includes 0.70x Phys Poison penalty on MC & Yosuke (e.g. 140 raw -> 200 cured)
        bridge.ActorSingleHitDamages[(mcPoisoned, 122, boss)] = 140.0;
        bridge.ActorSingleHitDamages[(yosukePoisoned, 122, boss)] = 140.0;
        bridge.ActorSingleHitDamages[(amritaSupport, 1, boss)] = 150.0;

        var dMultiPoison = engine.DecideAction(new BattleSnapshot
        {
            Actor = amritaSupport,
            Allies = new() { mcPoisoned, yosukePoisoned, amritaSupport },
            Enemies = new() { boss },
            UpcomingRoundTurnOrder = new List<CombatantSnapshot> { mcPoisoned, yosukePoisoned, boss }
        }, cfg)!;
        Assert.Equal(AiActionKind.Skill, dMultiPoison.ActionKind);
        Assert.Equal((ushort)208, dMultiPoison.SkillId);
    }

    [Fact]
    public void ChargeAction_SkipsFixedPowerAndPercentHpSkills()
    {
        var bridge = CreateStandardSkillDatabase();
        bridge.Skills[66] = new SkillMetadata
        {
            SkillId = 66, Name = "Divine Judgement", Element = ElementId.Light,
            CostType = 2, BaseCost = 48, Scope = 0, TargetSide = 2, EffectType = 8, Power = 50, AilmentMask = 0x80000
        };
        bridge.Skills[330] = new SkillMetadata
        {
            SkillId = 330, Name = "Fixed Blast", Element = ElementId.Almighty,
            CostType = 2, BaseCost = 10, Scope = 0, TargetSide = 2, EffectType = 4, Power = 250
        };

        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();
        var boss = MakeEnemy(260, 0, 2000, 2000);
        var naoto = MakeParty(7, TacticMode.FullAssault, 340, 340, 200, 200, new ushort[] { 228, 66, 330 });
        bridge.SingleHitDamages[(66, boss)] = 1000.0;
        bridge.SingleHitDamages[(330, boss)] = 250.0;

        var d = engine.DecideAction(new BattleSnapshot
        {
            Actor = naoto,
            Allies = new() { naoto },
            Enemies = new() { boss }
        }, cfg)!;

        // Mind Charge (228) does NOT multiply EffectType 8 (HP-halving) or EffectType 4 (fixed damage)
        Assert.NotEqual((ushort)228, d.SkillId);
        Assert.Equal((ushort)66, d.SkillId);
    }

    [Fact]
    public void TelegraphAndChargeGuard_SkipsGuardingWhenActorHasShieldOfJustice()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        // Shadow Teddie telegraphing Nihil Hand (MustGuardUltimate = true) while Chie already holds Shield of Justice
        var shadowTeddie = MakeEnemy(261, 0, 3000, 3000, lastActionType: 7, waitMessageId: 169);
        var chieShielded = MakeParty(3, TacticMode.ActFreely, 120, 300, 100, 100, new ushort[] { 138 }, shieldOfJustice: true);
        bridge.SingleHitDamages[(138, shadowTeddie)] = 350.0;

        var dUltimate = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieShielded,
            Allies = new() { chieShielded },
            Enemies = new() { shadowTeddie }
        }, cfg)!;
        Assert.NotEqual(AiActionKind.Guard, dUltimate.ActionKind);
        Assert.Equal((ushort)138, dUltimate.SkillId);

        // Power-Charged boss with wounded actor who already holds Shield of Justice -> attacks instead of wasting Shield guarding
        var chargedBoss = MakeEnemy(259, 0, 2000, 2000, powerCharge: true);
        bridge.SingleHitDamages[(138, chargedBoss)] = 350.0;
        var dCharged = engine.DecideAction(new BattleSnapshot
        {
            Actor = chieShielded,
            Allies = new() { chieShielded },
            Enemies = new() { chargedBoss }
        }, cfg)!;
        Assert.NotEqual(AiActionKind.Guard, dCharged.ActionKind);
        Assert.Equal((ushort)138, dCharged.SkillId);
    }

    [Fact]
    public void OneMoreTurn_SkipsChargeAndBuffs_WhileNormalTurn1AllowsSetup()
    {
        var bridge = CreateStandardSkillDatabase();
        var engine = new SmartPartyAiEngine(bridge);
        var cfg = DefaultConfig();

        var boss = MakeEnemy(259, 0, 2000, 2000);
        var kanji = MakeParty(6, TacticMode.ActFreely, 400, 400, 120, 120, new ushort[] { 227, 219, 138 });
        bridge.SingleHitDamages[(138, boss)] = 300.0;

        // Normal Turn 1 (including Player Advantage): IsActorOnOneMore == false -> Power Charge (227) is allowed
        var dNormal = engine.DecideAction(new BattleSnapshot
        {
            Actor = kanji,
            IsActorOnOneMore = false,
            Allies = new() { kanji },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)227, dNormal.SkillId);

        // Actual 1-More bonus turn: IsActorOnOneMore == true -> skips Power Charge / Matarukaja and attacks directly!
        var dOneMore = engine.DecideAction(new BattleSnapshot
        {
            Actor = kanji,
            IsActorOnOneMore = true,
            Allies = new() { kanji },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)138, dOneMore.SkillId);
    }
}
