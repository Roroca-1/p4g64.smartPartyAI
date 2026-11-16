using p4g64.smartPartyAI.Ai;
using p4g64.smartPartyAI.Core;
using Xunit;

namespace p4g64.smartPartyAI.Tests;

internal sealed class MockBattleBridge : IBattleEngineBridge
{
    public Dictionary<ushort, SkillMetadata> Skills { get; } = new();
    public Dictionary<(CombatantSnapshot target, ElementId elem), AffinityInfo> Affinities { get; } = new();
    public Dictionary<(ushort skillId, CombatantSnapshot target), double> SingleHitDamages { get; } = new();

    public SkillMetadata GetSkill(ushort skillId) =>
        Skills.TryGetValue(skillId, out var s)
            ? s
            : new SkillMetadata { SkillId = skillId, Name = SkillCatalog.GetSkillName(skillId), Element = ElementId.Passive };

    public int GetEffectiveCost(CombatantSnapshot actor, ushort skillId) => GetSkill(skillId).BaseCost;

    public bool CanUseSkill(CombatantSnapshot actor, ushort skillId)
    {
        if (skillId == 0) return !actor.IsDead;
        var s = GetSkill(skillId);
        if (actor.HasSilenced && s.CostType == 2) return false;
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
        bool blockedByShield = blockedByKarn || target.HasShieldOfJustice || target.HasUnbreakableShield;
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
        double baseDmg = SingleHitDamages.TryGetValue((skillId, target), out var d) ? d : 0.0;

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

    public Dictionary<int, ElementId> AttackElements { get; } = new();
    public List<SpRecoveryItemInfo> AvailableSpItems { get; } = new();

    public ElementId GetAttackElement(CombatantSnapshot actor) =>
        AttackElements.TryGetValue(actor.SlotId, out var elem) ? elem : ElementId.Phys;

    public IReadOnlyList<SpRecoveryItemInfo> GetAvailableSpRecoveryItems(CombatantSnapshot beneficiary) =>
        AvailableSpItems;
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
        b.Skills[0] = new SkillMetadata { SkillId = 0, Name = "Attack", Element = ElementId.Phys, CostType = 0, BaseCost = 0, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 15 };
        b.Skills[1] = new SkillMetadata { SkillId = 1, Name = "Agi", Element = ElementId.Fire, CostType = 2, BaseCost = 4, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 40 };
        b.Skills[3] = new SkillMetadata { SkillId = 3, Name = "Agidyne", Element = ElementId.Fire, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 160 };
        b.Skills[4] = new SkillMetadata { SkillId = 4, Name = "Maragi", Element = ElementId.Fire, CostType = 2, BaseCost = 10, Scope = 1, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 40 };
        b.Skills[48] = new SkillMetadata { SkillId = 48, Name = "Spirit Drain", Element = ElementId.Almighty, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 0, Mode = 5, Power = 0 };
        b.Skills[53] = new SkillMetadata { SkillId = 53, Name = "Megidolaon", Element = ElementId.Almighty, CostType = 2, BaseCost = 38, Scope = 1, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 210 };
        b.Skills[122] = new SkillMetadata { SkillId = 122, Name = "Power Slash", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 10, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 100 };
        b.Skills[133] = new SkillMetadata { SkillId = 133, Name = "Blade of Fury", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 18, Scope = 1, TargetSide = 2, MinHits = 2, MaxHits = 4, EffectType = 1, Power = 35 };
        b.Skills[138] = new SkillMetadata { SkillId = 138, Name = "God's Hand", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 25, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 230 };
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
        b.Skills[132] = new SkillMetadata { SkillId = 132, Name = "Brave Blade", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 18, Scope = 0, TargetSide = 2, MinHits = 1, MaxHits = 1, EffectType = 1, Power = 190, Accuracy = 95 };
        b.Skills[172] = new SkillMetadata { SkillId = 172, Name = "Swift Strike", Element = ElementId.Phys, StatType = 1, CostType = 1, BaseCost = 15, Scope = 1, TargetSide = 2, MinHits = 1, MaxHits = 3, EffectType = 1, Power = 30, Accuracy = 75 };
        b.Skills[198] = new SkillMetadata { SkillId = 198, Name = "Salvation", Element = ElementId.Recovery, CostType = 2, BaseCost = 40, Scope = 1, TargetSide = 1, EffectType = 11, Power = 100, AilmentMask = 0xFF };
        b.Skills[199] = new SkillMetadata { SkillId = 199, Name = "Patra", Element = ElementId.Recovery, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 1, EffectType = 0, Power = 15, AilmentMask = 0x16 };
        b.Skills[201] = new SkillMetadata { SkillId = 201, Name = "Re Patra", Element = ElementId.Recovery, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 1, TargetCond = 8, EffectType = 0, Power = 15, AilmentMask = 0x00100001 };
        b.Skills[206] = new SkillMetadata { SkillId = 206, Name = "Baisudi", Element = ElementId.Recovery, CostType = 2, BaseCost = 3, Scope = 0, TargetSide = 1, EffectType = 0, Power = 15, AilmentMask = 0xC0 };
        b.Skills[208] = new SkillMetadata { SkillId = 208, Name = "Amrita", Element = ElementId.Recovery, CostType = 2, BaseCost = 12, Scope = 1, TargetSide = 1, EffectType = 0, Power = 15, AilmentMask = 0xFF };
        b.Skills[210] = new SkillMetadata { SkillId = 210, Name = "Samarecarm", Element = ElementId.Recovery, CostType = 2, BaseCost = 18, Scope = 0, TargetSide = 1, TargetCond = 4, EffectType = 11, Power = 100, AilmentMask = 0x80000 };
        b.Skills[211] = new SkillMetadata { SkillId = 211, Name = "Tarunda", Element = ElementId.Support, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 2, BuffMask = SkillCatalog.BuffTarunda | SkillCatalog.BuffMakanda };
        b.Skills[218] = new SkillMetadata { SkillId = 218, Name = "Tarukaja", Element = ElementId.Support, CostType = 2, BaseCost = 12, Scope = 0, TargetSide = 1, BuffMask = SkillCatalog.BuffTarukaja | SkillCatalog.BuffMakakaja };
        b.Skills[221] = new SkillMetadata { SkillId = 221, Name = "Marakukaja", Element = ElementId.Support, CostType = 2, BaseCost = 24, Scope = 1, TargetSide = 1, BuffMask = SkillCatalog.BuffMarakukaja };
        b.Skills[225] = new SkillMetadata { SkillId = 225, Name = "Heat Riser", Element = ElementId.Support, CostType = 2, BaseCost = 30, Scope = 0, TargetSide = 1, BuffMask = SkillCatalog.BuffHeatRiser };
        b.Skills[226] = new SkillMetadata { SkillId = 226, Name = "Debilitate", Element = ElementId.Support, CostType = 2, BaseCost = 30, Scope = 0, TargetSide = 2, BuffMask = SkillCatalog.BuffDebilitate };
        b.Skills[228] = new SkillMetadata { SkillId = 228, Name = "Mind Charge", Element = ElementId.Support, CostType = 2, BaseCost = 15, Scope = 0, TargetSide = 1, TargetCond = 1, BuffMask = SkillCatalog.BuffMindCharge };
        b.Skills[230] = new SkillMetadata { SkillId = 230, Name = "Makarakarn", Element = ElementId.Support, CostType = 2, BaseCost = 36, Scope = 0, TargetSide = 1, BuffMask = SkillCatalog.BuffMakarakarn };
        b.Skills[245] = new SkillMetadata { SkillId = 245, Name = "Kamui Miracle", Element = ElementId.Recovery, CostType = 2, BaseCost = 0, Scope = 1, TargetSide = 1, TargetCond = 2, EffectType = 11, Power = 100 };
        b.Skills[247] = new SkillMetadata { SkillId = 247, Name = "Youthful Wind", Element = ElementId.Recovery, CostType = 2, BaseCost = 25, Scope = 1, TargetSide = 1, EffectType = 2, Power = 130, BuffMask = SkillCatalog.BuffMarakukaja };
        b.Skills[248] = new SkillMetadata { SkillId = 248, Name = "Dragon Hustle", Element = ElementId.Support, CostType = 2, BaseCost = 150, Scope = 1, TargetSide = 1, BuffMask = SkillCatalog.BuffHeatRiser };
        b.Skills[252] = new SkillMetadata { SkillId = 252, Name = "Shield of Justice", Element = ElementId.Support, CostType = 2, BaseCost = 160, Scope = 1, TargetSide = 1, BuffMask = SkillCatalog.BuffShieldOfJustice };
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
        int hitEvaStage = 0) => new()
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
        EquippedSkills = skills,
        ChargeFlags = (powerCharge ? 0x80000u : 0u) | (mindCharge ? 0x100000u : 0u),
        StateFlags = stateFlags,
        HasRedWall = redWall,
        AtkStage = atkStage,
        MagStage = magStage,
        DefStage = defStage,
        HitEvaStage = hitEvaStage
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
        bool mindCharge = false,
        bool powerCharge = false) => new()
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
        StateFlags = isDown ? 0x100000u : 0u,
        BaseFlags = isGuarding ? (ushort)0x200 : (ushort)0,
        HasRedWall = redWall,
        ChargeFlags = (tetrakarn ? 0x20000u : 0u) | (makarakarn ? 0x40000u : 0u) | (powerCharge ? 0x80000u : 0u) | (mindCharge ? 0x100000u : 0u),
        ShieldFlags = shieldOfJustice ? (ushort)1 : (ushort)0,
        AtkStage = atkStage,
        MagStage = magStage,
        DefStage = defStage,
        HitEvaStage = hitEvaStage,
        WaitMessageId = waitMessageId,
        LastSkillId = lastSkillId
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

        // RespectAnalysis = true -> AI does not know Fire weakness yet, picks Power Slash (150 vs 40)
        var fair = engine.DecideAction(battle, DefaultConfig(respectAnalysis: true))!;
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

        var shadowYukiko = MakeEnemy(268, 1, 1500, 1500, waitMessageId: 202);
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

        var shadowYukiko = MakeEnemy(268, 1, 1500, 1500, waitMessageId: 202);
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

        var shadowTeddie = MakeEnemy(276, 1, 3000, 3000, waitMessageId: 169);
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

        // 2. Yukiko with Mediarama (196) + Youthful Wind (247) when party lacks Def buff -> Youthful Wind (heal + Marakukaja)!
        var yukiko = MakeParty(4, TacticMode.HealSupport, 180, 360, 180, 200, new ushort[] { 196, 247, 197, 198 });
        var dYouthful = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukiko,
            Allies = new() { protagLow, yukiko },
            Enemies = new() { boss }
        }, cfg)!;
        Assert.Equal((ushort)247, dYouthful.SkillId);

        // Same healing scenario, party ALREADY has +1 Def -> chooses cheaper Mediarama (196)
        var yukikoDefUp = MakeParty(4, TacticMode.HealSupport, 180, 360, 180, 200, new ushort[] { 196, 247, 197, 198 }, defStage: 1);
        var protagDefUp = MakeParty(1, TacticMode.ActFreely, 150, 360, 100, 100, Array.Empty<ushort>(), defStage: 1);
        var dMediarama = engine.DecideAction(new BattleSnapshot
        {
            Actor = yukikoDefUp,
            Allies = new() { protagDefUp, yukikoDefUp },
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
        var shadowYukiko = MakeEnemy(268, 1, 1500, 1500, waitMessageId: 202);
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

        // 2. Shadow Mitsuo Whisper (181) / Ameno-sagiri Nebula Oculus (bit 455) / Global bit 466 -> Mandatory Guard
        var mitsuo = MakeEnemy(279, 1, 2200, 2200, waitMessageId: 181);
        var yukiko = MakeParty(4, TacticMode.FullAssault, 360, 360, 150, 150, new ushort[] { 3 });
        bridge.SingleHitDamages[(3, mitsuo)] = 150.0;
        var dMitsuo = engine.DecideAction(new BattleSnapshot { Actor = yukiko, Allies = new() { yukiko }, Enemies = new() { mitsuo } }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dMitsuo.ActionKind);

        var ameno = MakeEnemy(286, 1, 3000, 3000, lastSkillId: 384);
        bridge.SingleHitDamages[(3, ameno)] = 150.0;
        var dAmeno = engine.DecideAction(new BattleSnapshot { Actor = yukiko, Allies = new() { yukiko }, Enemies = new() { ameno } }, cfg)!;
        Assert.Equal(AiActionKind.Guard, dAmeno.ActionKind);

        // 3. Kunino-sagiri Quad Converge Fire (bit 453) -> Yukiko Guards? No, Yukiko nulls/neutral Fire, Chie (weak Fire) Guards!
        var kunino = MakeEnemy(284, 1, 3200, 3200);
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
        var shadowYukiko = MakeEnemy(268, 1, 1500, 1500, waitMessageId: 202);
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
}
