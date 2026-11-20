using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Native;

/// <summary>
/// Live bridge reading P4G battle memory and calling native damage/affinity calculators safely.
/// </summary>
public sealed unsafe class NativeBattleBridge : IBattleEngineBridge
{
    private readonly record struct CachedConsumableTemplate(
        ushort ItemId,
        string Name,
        ushort SkillId,
        BattleConsumableKind Kind,
        ConsumableRarityTier Rarity,
        SkillMetadata Skill,
        byte SpMode,
        ushort SpPwr,
        byte HpMode,
        ushort HpPwr);

    private readonly P4GNative _native;
    private nint _cachedItemTblPtr;
    private nint _cachedConsumableSkillSec1Ptr;
    private int _cachedItemCount;
    private List<CachedConsumableTemplate>? _cachedConsumables;
    private nint _cachedSkillSec0Ptr;
    private nint _cachedSkillSec1Ptr;
    private SkillMetadata?[]? _skillCache;

    // Per-turn read-only caches cleared on CaptureBattleSnapshot and at end of OnBtlAiStateUpdate.
    private readonly Dictionary<(nint targetComb, ElementId elem), uint> _turnTrueAffinityCache = new(64);
    private readonly Dictionary<(nint targetComb, ElementId elem, bool respectAnalysis), AffinityInfo> _turnAffinityCache = new(64);
    private readonly Dictionary<(nint targetComb, bool respectAnalysis), AffinityInfo> _turnAilmentAffinityCache = new(16);
    private readonly Dictionary<(ushort skillId, nint attackerComb, nint targetComb), int> _turnRawDamageCache = new(128);
    private readonly Dictionary<(nint comb, ushort skillId), int> _turnEffectiveCostCache = new(32);
    private readonly Dictionary<(nint unit, ushort skillId), bool> _turnCanUseSkillCache = new(32);
    private readonly Dictionary<nint, ElementId> _turnAttackElemCache = new(8);
    private readonly Dictionary<(int maxHp, int maxSp), IReadOnlyList<BattleConsumableInfo>> _turnConsumablesCache = new(4);
    private readonly Dictionary<(int maxHp, int maxSp), IReadOnlyList<SpRecoveryItemInfo>> _turnSpItemsCache = new(4);
    private double _turnOffBuffMult = double.NaN;
    private double _turnDefDebuffMult = double.NaN;

    public NativeBattleBridge(P4GNative native)
    {
        _native = native;
    }

    public void ClearTurnCache()
    {
        _turnTrueAffinityCache.Clear();
        _turnAffinityCache.Clear();
        _turnAilmentAffinityCache.Clear();
        _turnRawDamageCache.Clear();
        _turnEffectiveCostCache.Clear();
        _turnCanUseSkillCache.Clear();
        _turnAttackElemCache.Clear();
        _turnConsumablesCache.Clear();
        _turnSpItemsCache.Clear();
        _turnOffBuffMult = double.NaN;
        _turnDefDebuffMult = double.NaN;
    }

    public BattleSnapshot? CaptureBattleSnapshot(nint actorUnitPtr)
    {
        ClearTurnCache();
        nint battleMgr = _native.BattleManagerPtr;
        if (battleMgr == 0 || actorUnitPtr == 0)
            return null;

        var actor = ReadCombatantSnapshot(actorUnitPtr);
        if (actor == null || !actor.IsPartyMember)
            return null;

        var allies = new List<CombatantSnapshot>(4);
        var enemies = new List<CombatantSnapshot>(6);
        var unitByPtr = new Dictionary<nint, CombatantSnapshot>(10)
        {
            [actorUnitPtr] = actor
        };

        nint cur = *(nint*)(battleMgr + P4GNative.OffBattleMgrUnitListHead);
        int guard = 0;
        while (cur != 0 && guard++ < 64)
        {
            uint flags = *(uint*)(cur + P4GNative.OffUnitFlags);
            nint unitData = *(nint*)(cur + P4GNative.OffUnitDataPtr);
            if ((flags & 0x9) == 0x9 && unitData != 0)
            {
                byte unitType = *(byte*)(unitData + P4GNative.OffUnitDataType);
                if (unitType == 0)
                {
                    var snap = cur == actorUnitPtr ? actor : ReadCombatantSnapshot(cur);
                    if (snap != null)
                    {
                        allies.Add(snap);
                        unitByPtr[cur] = snap;
                    }
                }
                else if (unitType == 1)
                {
                    var snap = ReadCombatantSnapshot(cur);
                    if (snap != null)
                    {
                        enemies.Add(snap);
                        unitByPtr[cur] = snap;
                    }
                }
            }
            cur = *(nint*)(cur + P4GNative.OffUnitNext);
        }

        if (enemies.Count == 0)
            return null;

        bool isOneMore = _native.IsUnitOn1More(actorUnitPtr) != 0;
        var rawOrder = new List<CombatantSnapshot>(P4GNative.BattleTurnQueueCapacity);
        var upcomingOrder = new List<CombatantSnapshot>(P4GNative.BattleTurnQueueCapacity);
        nint* queueArr = (nint*)(battleMgr + P4GNative.OffBattleMgrTurnQueue);
        for (int i = 0; i < P4GNative.BattleTurnQueueCapacity; i++)
        {
            nint qUnit = queueArr[i];
            if (qUnit == 0)
                break;
            if (!isOneMore && i == 0 && qUnit == actorUnitPtr)
                continue;
            if (unitByPtr.TryGetValue(qUnit, out var qSnap))
            {
                rawOrder.Add(qSnap);
                if (!qSnap.IsDead)
                    upcomingOrder.Add(qSnap);
            }
        }

        var activeBattleBits = new HashSet<int>();
        for (int bit = 448; bit <= 472; bit++)
        {
            if (_native.IsBattleBitSet(bit))
                activeBattleBits.Add(bit);
        }

        return new BattleSnapshot
        {
            Actor = actor,
            IsActorOnOneMore = isOneMore,
            Allies = allies,
            Enemies = enemies,
            ActiveBattleBits = activeBattleBits,
            UpcomingRoundTurnOrder = upcomingOrder,
            RawRoundTurnOrder = rawOrder
        };
    }

    private CombatantSnapshot? ReadCombatantSnapshot(nint unitPtr)
    {
        nint unitData = *(nint*)(unitPtr + P4GNative.OffUnitDataPtr);
        if (unitData == 0)
            return null;

        byte unitType = *(byte*)(unitData + P4GNative.OffUnitDataType);
        ushort unitOrEnemyId = *(ushort*)(unitData + P4GNative.OffUnitDataId);
        nint combPtr = *(nint*)(unitData + P4GNative.OffUnitDataCombatant);
        if (combPtr == 0)
            return null;

        int slotId = *(int*)(unitPtr + P4GNative.OffUnitSlotId);
        ushort lastActionType = *(ushort*)(unitPtr + P4GNative.OffUnitLastActionType);
        ushort lastSkillId = *(ushort*)(unitPtr + P4GNative.OffUnitLastSkillId);
        ushort waitMsgId = *(ushort*)(unitPtr + P4GNative.OffUnitWaitMsgId);
        ushort baseFlags = *(ushort*)(combPtr + P4GNative.OffCombBaseFlags);
        ushort personaOrId = *(ushort*)(combPtr + P4GNative.OffCombPersonaOrId);
        short hp = *(short*)(combPtr + P4GNative.OffCombHp);
        short sp = *(short*)(combPtr + P4GNative.OffCombSp);
        int maxHp = Math.Max(1, _native.GetMaxHp(combPtr));
        int maxSp = Math.Max(0, _native.GetMaxSp(combPtr));
        int agility = Math.Max(1, _native.GetCombatantStat(combPtr, 3));
        uint stateFlags = *(uint*)(combPtr + P4GNative.OffCombStateFlags);
        byte tacticByte = *(byte*)(combPtr + P4GNative.OffCombTactic);
        uint chargeFlags = *(uint*)(combPtr + P4GNative.OffCombChargeFlags);
        ushort shieldFlags = *(ushort*)(combPtr + P4GNative.OffCombShieldFlags);

        int atkStage = _native.GetSignedStatStage(combPtr, 0);
        int magStage = _native.GetSignedStatStage(combPtr, 1);
        int defStage = _native.GetSignedStatStage(combPtr, 2);
        int hitStage = _native.GetSignedStatStage(combPtr, 3);
        int critStage = _native.GetSignedStatStage(combPtr, 5);
        int revCritStage = _native.GetSignedStatStage(combPtr, 6);
        int atkTurns = _native.GetStatStageDuration(combPtr, 0);
        int magTurns = _native.GetStatStageDuration(combPtr, 1);
        int defTurns = _native.GetStatStageDuration(combPtr, 2);
        int hitTurns = _native.GetStatStageDuration(combPtr, 3);
        int critTurns = _native.GetStatStageDuration(combPtr, 5);
        int revCritTurns = _native.GetStatStageDuration(combPtr, 6);

        byte breaks0 = *(byte*)(combPtr + P4GNative.OffCombBreaksByte0);
        byte breaks1 = *(byte*)(combPtr + P4GNative.OffCombBreaksByte1);
        byte walls0 = *(byte*)(combPtr + P4GNative.OffCombWallsByte0);
        byte walls1 = *(byte*)(combPtr + P4GNative.OffCombWallsByte1);

        ushort[] skills = Array.Empty<ushort>();
        if (unitType == 0)
        {
            nint personaData = _native.GetPartyPersonaData(personaOrId);
            if (personaData != 0)
            {
                skills = new ushort[8];
                ushort* pSkills = (ushort*)(personaData + P4GNative.OffPersonaSkills);
                for (int i = 0; i < 8; i++)
                    skills[i] = pSkills[i];
            }
        }

        string name = unitType == 0
            ? SkillCatalog.GetPartyMemberName(unitOrEnemyId)
            : $"Enemy#{unitOrEnemyId}(s{slotId})";

        return new CombatantSnapshot
        {
            UnitPtr = unitPtr,
            CombatantPtr = combPtr,
            SlotId = slotId,
            IsPartyMember = unitType == 0,
            IsEnemy = unitType == 1,
            UnitOrEnemyId = unitOrEnemyId,
            PersonaOrEnemyId = personaOrId,
            DisplayName = name,
            Tactic = tacticByte <= 4 ? (TacticMode)tacticByte : TacticMode.DirectCommands,
            CurrentHp = hp,
            MaxHp = maxHp,
            CurrentSp = sp,
            MaxSp = maxSp,
            Agility = agility,
            StateFlags = stateFlags,
            ChargeFlags = chargeFlags,
            BaseFlags = baseFlags,
            ShieldFlags = shieldFlags,
            AtkStage = atkStage,
            MagStage = magStage,
            DefStage = defStage,
            HitEvaStage = hitStage,
            CritRateStage = critStage,
            RevolutionCritStage = revCritStage,
            AtkStageTurns = atkTurns,
            MagStageTurns = magTurns,
            DefStageTurns = defTurns,
            HitEvaStageTurns = hitTurns,
            CritRateStageTurns = critTurns,
            RevolutionCritStageTurns = revCritTurns,
            HasFireBreak = P4GNative.UnpackSignedNibble(breaks0, false) > 0,
            HasIceBreak = P4GNative.UnpackSignedNibble(breaks0, true) > 0,
            HasWindBreak = P4GNative.UnpackSignedNibble(breaks1, false) > 0,
            HasElecBreak = P4GNative.UnpackSignedNibble(breaks1, true) > 0,
            HasRedWall = P4GNative.UnpackSignedNibble(walls0, false) > 0,
            HasBlueWall = P4GNative.UnpackSignedNibble(walls0, true) > 0,
            HasGreenWall = P4GNative.UnpackSignedNibble(walls1, false) > 0,
            HasWhiteWall = P4GNative.UnpackSignedNibble(walls1, true) > 0,
            EquippedSkills = skills,
            LastActionType = lastActionType,
            LastSkillId = lastSkillId,
            WaitMessageId = waitMsgId
        };
    }

    public SkillMetadata GetSkill(ushort skillId)
    {
        if (skillId >= P4GNative.MaxSkillTableId)
        {
            return new SkillMetadata { SkillId = skillId, Name = SkillCatalog.GetSkillName(skillId), Element = ElementId.Passive };
        }

        byte* sec0 = _native.SkillSec0Ptr;
        byte* sec1 = _native.SkillSec1Ptr;
        if (sec0 == null || sec1 == null)
        {
            return new SkillMetadata
            {
                SkillId = skillId,
                Name = SkillCatalog.GetSkillName(skillId),
                Element = ElementId.Phys,
                BaseCrit = skillId == 0 ? (byte)3 : (byte)0
            };
        }

        if (_skillCache == null || _cachedSkillSec0Ptr != (nint)sec0 || _cachedSkillSec1Ptr != (nint)sec1)
        {
            _skillCache = new SkillMetadata?[P4GNative.MaxSkillTableId];
            _cachedSkillSec0Ptr = (nint)sec0;
            _cachedSkillSec1Ptr = (nint)sec1;
        }

        var cached = _skillCache[skillId];
        if (cached != null)
            return cached;

        byte elem = sec0[skillId * 2];
        byte typ = sec0[skillId * 2 + 1];
        byte* e = sec1 + skillId * 0x2C;

        var meta = new SkillMetadata
        {
            SkillId = skillId,
            Name = SkillCatalog.GetSkillName(skillId),
            Element = (ElementId)elem,
            SkillType = typ,
            StatType = e[0x05],
            CostType = e[0x06],
            BaseCost = *(ushort*)(e + 0x08),
            Scope = e[0x0C],
            TargetSide = e[0x0D],
            TargetCond = e[0x0E],
            Accuracy = e[0x12] == 0 ? (byte)100 : e[0x12],
            BaseCrit = e[0x29],
            BaseAilmentChance = e[0x1D],
            MinHits = e[0x13] == 0 ? (byte)1 : e[0x13],
            MaxHits = e[0x14] == 0 ? (byte)1 : e[0x14],
            EffectType = e[0x15],
            Power = *(ushort*)(e + 0x16),
            Mode = e[0x18],
            AilmentMask = *(uint*)(e + 0x20),
            BuffMask = *(uint*)(e + 0x24)
        };
        _skillCache[skillId] = meta;
        return meta;
    }

    public int GetEffectiveCost(CombatantSnapshot actor, ushort skillId)
    {
        if (skillId == 0)
            return 0;

        var key = (actor.CombatantPtr, skillId);
        if (_turnEffectiveCostCache.TryGetValue(key, out int cached))
            return cached;

        int cost = _native.GetEffectiveSkillCost(actor.CombatantPtr, skillId);
        _turnEffectiveCostCache[key] = cost;
        return cost;
    }

    public bool CanUseSkill(CombatantSnapshot actor, ushort skillId)
    {
        if (skillId == 0)
            return !actor.IsDead;

        var key = (actor.UnitPtr, skillId);
        if (_turnCanUseSkillCache.TryGetValue(key, out bool cached))
            return cached;

        bool canUse = _native.CanUnitUseSkill(actor.UnitPtr, skillId) != 0;
        _turnCanUseSkillCache[key] = canUse;
        return canUse;
    }

    [ThreadStatic]
    public static bool DeterministicMidpointRand;

    public ElementId GetAttackElement(CombatantSnapshot actor)
    {
        if (actor.CombatantPtr == 0)
            return ElementId.Phys;

        if (_turnAttackElemCache.TryGetValue(actor.CombatantPtr, out var cached))
            return cached;

        int rawElem = _native.GetSkillElement(actor.CombatantPtr, 0);
        ElementId elem = (uint)rawElem <= (uint)ElementId.PassSpecial ? (ElementId)rawElem : ElementId.Phys;
        _turnAttackElemCache[actor.CombatantPtr] = elem;
        return elem;
    }

    private uint GetCachedTrueRawAffinity(nint targetComb, ElementId element)
    {
        var key = (targetComb, element);
        if (_turnTrueAffinityCache.TryGetValue(key, out uint cached))
            return cached;

        uint raw = _native.GetEffectiveAffinity(targetComb, (short)element);
        _turnTrueAffinityCache[key] = raw;
        return raw;
    }

    public AffinityInfo QueryEffectiveAffinity(CombatantSnapshot actor, CombatantSnapshot target, ElementId element, bool respectAnalysis)
    {
        var cacheKey = (target.CombatantPtr, element, respectAnalysis);
        if (_turnAffinityCache.TryGetValue(cacheKey, out var cachedAff))
            return cachedAff;

        uint raw = GetCachedTrueRawAffinity(target.CombatantPtr, element);

        bool isNull = (raw & 0x01000000) != 0;
        bool isRepel = (raw & 0x02000000) != 0;
        bool isDrain = (raw & 0x04000000) != 0;
        bool isWeak = (raw & 0x08000000) != 0;
        bool isResist = (raw & 0x10000000) != 0;
        int mult = (int)(raw & 0xFF);

        // Visible barriers that GetEffectiveAffinity does NOT check:
        bool blockedByKarn =
            (element == ElementId.Phys && target.HasTetrakarn) ||
            (element != ElementId.Phys && element != ElementId.Almighty && (byte)element <= 7 && target.HasMakarakarn);
        bool blockedByShield = blockedByKarn || target.HasShieldOfJustice;

        // Visible Guard / Wall overrides
        bool hasVisibleWall = target.HasWallForElement(element);
        if (target.IsGuarding || hasVisibleWall || blockedByShield)
            isWeak = false;

        bool revealed = true;
        if (target.IsEnemy && (byte)element <= 7 && element != ElementId.Almighty)
            revealed = _native.IsAffinityRevealed(target.UnitOrEnemyId, (ushort)element) != 0;

        // When RespectAnalysisState == true and base affinity has NOT been analyzed yet:
        // Hide innate Weak/Resist/Null/Repel/Drain so AI plays fairly, EXCEPT always respect visible in-battle states (Karn/Shield/Wall/Guard/Break)!
        AffinityInfo result;
        if (respectAnalysis && !revealed && target.IsEnemy)
        {
            result = new AffinityInfo
            {
                IsWeak = false,
                IsResist = hasVisibleWall || target.IsGuarding,
                IsNull = false,
                IsRepel = false,
                IsDrain = false,
                IsRevealed = false,
                BlockedByShield = blockedByShield,
                MultiplierPercent = blockedByShield ? 0 : (hasVisibleWall || target.IsGuarding ? 50 : 100)
            };
        }
        else
        {
            result = new AffinityInfo
            {
                IsWeak = isWeak,
                IsResist = isResist,
                IsNull = isNull,
                IsRepel = isRepel,
                IsDrain = isDrain,
                IsRevealed = revealed,
                BlockedByShield = blockedByShield,
                MultiplierPercent = (isNull || isRepel || isDrain || blockedByShield) ? 0 : (mult > 0 ? mult : 100)
            };
        }

        _turnAffinityCache[cacheKey] = result;
        return result;
    }

    public AffinityInfo QueryAilmentAffinity(CombatantSnapshot actor, CombatantSnapshot target, bool respectAnalysis)
    {
        if (target.IsEnemy && (target.MaxHp >= 500 || SkillCatalog.IsKnownBossUnitId(target.UnitOrEnemyId)))
            return new AffinityInfo { IsNull = true, IsRevealed = true, MultiplierPercent = 0 };

        var cacheKey = (target.CombatantPtr, respectAnalysis);
        if (_turnAilmentAffinityCache.TryGetValue(cacheKey, out var cachedAff))
            return cachedAff;

        uint raw = _native.GetEffectiveAffinity(target.CombatantPtr, 12);
        bool immune = (raw & 0x27000000) != 0;
        int mult = (int)(raw & 0xFF);
        bool revealed = !target.IsEnemy || _native.IsAffinityRevealed(target.UnitOrEnemyId, 12) != 0;

        AffinityInfo result = (respectAnalysis && !revealed && target.IsEnemy)
            ? new AffinityInfo { IsRevealed = false, MultiplierPercent = 100 }
            : new AffinityInfo
            {
                IsNull = immune,
                IsResist = (raw & 0x10000000) != 0,
                IsWeak = (raw & 0x08000000) != 0,
                IsRevealed = revealed,
                MultiplierPercent = immune ? 0 : (mult > 0 ? mult : 100)
            };

        _turnAilmentAffinityCache[cacheKey] = result;
        return result;
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

        var dmgKey = (skillId, attacker.CombatantPtr, target.CombatantPtr);
        if (!_turnRawDamageCache.TryGetValue(dmgKey, out int raw))
        {
            raw = CallNativeCalcDamageClean(skillId, attacker.CombatantPtr, target.CombatantPtr);
            _turnRawDamageCache[dmgKey] = raw;
        }

        var skill = GetSkill(skillId);
        ElementId actualElement = skillId == 0 ? GetAttackElement(attacker) : skill.Element;

        // If unanalyzed and RespectAnalysisState is true, normalize away hidden innate Null/Repel/Drain/Weakness/Resist multipliers
        double dmg;
        if (respectAnalysis && !effectiveAffinity.IsRevealed && target.IsEnemy && (byte)actualElement <= 7 && actualElement != ElementId.Almighty)
        {
            uint trueRawAff = GetCachedTrueRawAffinity(target.CombatantPtr, actualElement);
            bool trueBad = (trueRawAff & 0x07000000) != 0;
            int trueMult = (int)(trueRawAff & 0xFF);

            if (raw < 0 && !trueBad && trueMult > 0)
            {
                // Native CalcSkillDamage already applies Guard (0.4x); normalize only innate affinity vs visible Elemental Wall.
                int visibleNonGuardMult = effectiveAffinity.BlockedByShield ? 0 : (target.HasWallForElement(actualElement) ? 50 : 100);
                dmg = (-raw) * ((double)visibleNonGuardMult / trueMult);
            }
            else
            {
                // Target secretly Nulled/Repelled/Drained an unanalyzed element; estimate fair baseline power so AI doesn't peek
                dmg = EstimateBaselineNeutralDamage(_native, skill, actualElement, attacker, target, effectiveAffinity.MultiplierPercent);
            }
        }
        else
        {
            dmg = raw < 0 ? -raw : 0.0;
        }

        // Adjust Charge multiplier delta cleanly when simulating 2nd-step 1-More followups (fixed-power EffectType 4/13/18 ignores Charge)
        if (!skill.IsFixedPowerDamage)
        {
            bool usesPowerCharge = skillId == 0 || actualElement == ElementId.Phys;
            bool currentlyCharged = usesPowerCharge ? attacker.HasPowerCharge : attacker.HasMindCharge;
            if (assumeUnchargedOverride && currentlyCharged && dmg > 0)
                dmg /= 2.5;
            else if (assumeChargedOverride && !currentlyCharged && dmg > 0)
                dmg *= 2.5;
        }

        return dmg;
    }

    public bool IsKnownWeakness(CombatantSnapshot target, ElementId element, AffinityInfo aff) =>
        aff.IsWeak && !aff.BlockedByShield && !target.IsGuarding && !target.HasWallForElement(element);

    private int CallNativeCalcDamageClean(ushort skillId, nint attackerComb, nint targetComb)
    {
        DeterministicMidpointRand = true;
        try
        {
            return _native.CalcSkillDamage(skillId, attackerComb, targetComb, 0, 1, 1, 0, 1);
        }
        finally
        {
            DeterministicMidpointRand = false;
            // CRITICAL: Reset temporary calc scratch fields on both combatants (mirrors 0x1400687ff)
            *(uint*)(attackerComb + P4GNative.OffCombTempScratch) = 0;
            *(uint*)(targetComb + P4GNative.OffCombTempScratch) = 0;
        }
    }

    public IReadOnlyList<SpRecoveryItemInfo> GetAvailableSpRecoveryItems(CombatantSnapshot beneficiary)
    {
        var cacheKey = (beneficiary.MaxHp, beneficiary.MaxSp);
        if (_turnSpItemsCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var results = new List<SpRecoveryItemInfo>();
        foreach (var item in GetAvailableConsumables(beneficiary))
        {
            if (item.Kind != BattleConsumableKind.SpRestore || item.EstimatedSpGain <= 0)
                continue;

            bool fullSp = (item.Skill.Mode == 11 && item.EstimatedSpGain >= beneficiary.MaxSp) || item.IsFullRecovery;
            results.Add(new SpRecoveryItemInfo
            {
                ItemId = item.ItemId,
                Name = item.Name,
                SkillId = item.SkillId,
                Quantity = item.Quantity,
                IsPartyWide = item.IsPartyWide,
                EstimatedSpGain = item.EstimatedSpGain,
                EstimatedHpGain = item.EstimatedHpGain,
                IsFullSp = fullSp,
                IsFullHp = item.Skill.EffectType == 11 && item.Skill.Power >= 100
            });
        }

        _turnSpItemsCache[cacheKey] = results;
        return results;
    }

    public IReadOnlyList<BattleConsumableInfo> GetAvailableConsumables(CombatantSnapshot actor)
    {
        var cacheKey = (actor.MaxHp, actor.MaxSp);
        if (_turnConsumablesCache.TryGetValue(cacheKey, out var cached))
            return cached;

        var results = new List<BattleConsumableInfo>();
        byte* inv = _native.InventoryPtr;
        byte* itemTbl = _native.ItemTblPtr;
        byte* sec1 = _native.SkillSec1Ptr;
        if (inv == null || itemTbl == null || sec1 == null)
            return results;

        int itemCount = _native.ItemTblCount;
        if (itemCount <= 0 || itemCount > 4096)
            itemCount = 2560;

        var catalog = GetOrBuildConsumableCatalog(itemTbl, sec1, _native.ItemNamePtr, itemCount);
        foreach (var tpl in catalog)
        {
            byte qty = inv[tpl.ItemId];
            if (qty == 0)
                continue;

            int estSp = ResolveRecoveryValue(tpl.SpMode, tpl.SpPwr, actor.MaxSp);
            int estHp = ResolveRecoveryValue(tpl.HpMode, tpl.HpPwr, actor.MaxHp);
            results.Add(new BattleConsumableInfo
            {
                ItemId = tpl.ItemId,
                Name = tpl.Name,
                SkillId = tpl.SkillId,
                Quantity = qty,
                Kind = tpl.Kind,
                Rarity = tpl.Rarity,
                Skill = tpl.Skill,
                EstimatedHpGain = estHp,
                EstimatedSpGain = estSp,
                IsFullRecovery = (tpl.SpMode == 11 && tpl.SpPwr >= 100) || (tpl.HpMode == 11 && tpl.HpPwr >= 100)
            });
        }

        _turnConsumablesCache[cacheKey] = results;
        return results;
    }

    private List<CachedConsumableTemplate> GetOrBuildConsumableCatalog(byte* itemTbl, byte* sec1, byte* itemNames, int itemCount)
    {
        if (_cachedConsumables != null &&
            _cachedItemTblPtr == (nint)itemTbl &&
            _cachedConsumableSkillSec1Ptr == (nint)sec1 &&
            _cachedItemCount == itemCount)
        {
            return _cachedConsumables;
        }

        var list = new List<CachedConsumableTemplate>(64);
        for (int id = 1; id < itemCount; id++)
        {
            byte* rec = itemTbl + id * 0x44;
            if (*(uint*)rec != 3) // Consumable items only
                continue;

            ushort skillId = *(ushort*)(rec + 0x24);
            if (skillId == 0 || skillId >= P4GNative.MaxSkillTableId)
                continue;

            byte* sk = sec1 + skillId * 0x2C;
            if ((sk[0x04] & 0x02) == 0) // Must be battle-usable (matches 0x1400d0021)
                continue;

            var skill = GetSkill(skillId);
            byte spMode = sk[0x18];
            ushort spPwr = *(ushort*)(sk + 0x1A);
            byte hpMode = sk[0x15];
            ushort hpPwr = *(ushort*)(sk + 0x16);

            BattleConsumableKind? kind = null;
            ConsumableRarityTier rarity = ConsumableRarityTier.Common;

            if (skill.TargetsEnemies && skill.IsDamaging)
            {
                kind = BattleConsumableKind.Damage;
                bool rareDmg = (skill.IsAoE && skill.Power >= 120) ||
                               (!skill.IsFixedPowerDamage && skill.Power >= 200) ||
                               (skill.Element == ElementId.Almighty && skill.Power >= 150);
                rarity = rareDmg
                    ? ConsumableRarityTier.Rare
                    : (skill.IsAoE || skill.Power >= 100 ? ConsumableRarityTier.Mid : ConsumableRarityTier.Common);
            }
            else if (skill.TargetsAllies && skill.IsRevive)
            {
                kind = BattleConsumableKind.Revive;
                rarity = skill.Power >= 100 ? ConsumableRarityTier.Rare : ConsumableRarityTier.Common;
            }
            else if (skill.TargetsAllies && !skill.TargetsDeadOnly && (spMode is 5 or 11) && spPwr > 0)
            {
                kind = BattleConsumableKind.SpRestore;
                bool fullSp = spMode == 11 && spPwr >= 100;
                rarity = fullSp || skill.IsAoE
                    ? ConsumableRarityTier.Rare
                    : (spPwr >= 90 ? ConsumableRarityTier.Mid : ConsumableRarityTier.Common);
            }
            else if (skill.TargetsAllies && !skill.TargetsDeadOnly && skill.IsHeal)
            {
                kind = BattleConsumableKind.Heal;
                rarity = skill.IsFullHeal && skill.IsAoE
                    ? ConsumableRarityTier.Rare
                    : (skill.IsFullHeal || skill.IsAoE || skill.Power >= 150 ? ConsumableRarityTier.Mid : ConsumableRarityTier.Common);
            }
            else if (skill.TargetsAllies && !skill.TargetsDeadOnly && skill.IsAilmentCure)
            {
                kind = BattleConsumableKind.CureAilment;
                bool fullMask = (skill.AilmentMask & 0xFF) == 0xFF;
                rarity = skill.IsAoE && fullMask
                    ? ConsumableRarityTier.Rare
                    : (skill.IsAoE || fullMask ? ConsumableRarityTier.Mid : ConsumableRarityTier.Common);
            }

            if (kind == null)
                continue;

            list.Add(new CachedConsumableTemplate(
                (ushort)id,
                ReadItemName(itemNames, id),
                skillId,
                kind.Value,
                rarity,
                skill,
                spMode,
                spPwr,
                hpMode,
                hpPwr));
        }

        _cachedItemTblPtr = (nint)itemTbl;
        _cachedConsumableSkillSec1Ptr = (nint)sec1;
        _cachedItemCount = itemCount;
        _cachedConsumables = list;
        return list;
    }

    private static int ResolveRecoveryValue(byte mode, ushort power, int maxStat) => mode switch
    {
        2 or 5 => power,
        11 => (int)Math.Round(maxStat * (power / 100.0)),
        _ => 0
    };

    private static string ReadItemName(byte* itemNames, int itemId)
    {
        if (itemNames == null)
            return $"Item#{itemId}";

        byte* p = itemNames + itemId * 24;
        Span<byte> buf = stackalloc byte[24];
        int len = 0;
        for (; len < 24; len++)
        {
            byte b = p[len];
            if (b == 0)
                break;
            if (b < 0x20 || b > 0x7E)
                return $"Item#{itemId}";
            buf[len] = b;
        }
        return len > 0 ? System.Text.Encoding.ASCII.GetString(buf[..len]) : $"Item#{itemId}";
    }

    public double GetOffensiveBuffMultiplier()
    {
        if (double.IsNaN(_turnOffBuffMult))
            _turnOffBuffMult = Math.Max(1.05, _native.GetStageMultiplier(isEnemy: false, isDefense: false, stage: 1));
        return _turnOffBuffMult;
    }

    public double GetDefenseDebuffMultiplier()
    {
        if (double.IsNaN(_turnDefDebuffMult))
            _turnDefDebuffMult = Math.Max(1.05, _native.GetStageMultiplier(isEnemy: true, isDefense: true, stage: -1));
        return _turnDefDebuffMult;
    }

    private static double EstimateBaselineNeutralDamage(
        P4GNative native,
        SkillMetadata skill,
        ElementId actualElement,
        CombatantSnapshot attacker,
        CombatantSnapshot target,
        int visMultPct)
    {
        int pwr = skill.SkillId == 0 ? 15 : skill.Power;
        double est = Math.Sqrt(Math.Max(1, pwr)) * 8.5 * (visMultPct / 100.0);
        if (target.DefStage != 0)
            est *= native.GetStageMultiplier(target.IsEnemy, isDefense: true, target.DefStage);
        int atkStage = skill.StatType == 1 ? attacker.AtkStage : attacker.MagStage;
        if (atkStage != 0)
            est *= native.GetStageMultiplier(attacker.IsEnemy, isDefense: false, atkStage);
        bool usesPowerCharge = skill.SkillId == 0 || actualElement == ElementId.Phys;
        if ((usesPowerCharge && attacker.HasPowerCharge) || (!usesPowerCharge && attacker.HasMindCharge))
            est *= 2.5;
        return est;
    }
}
