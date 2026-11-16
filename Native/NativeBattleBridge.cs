using p4g64.smartPartyAI.Core;

namespace p4g64.smartPartyAI.Native;

/// <summary>
/// Live bridge reading P4G battle memory and calling native damage/affinity calculators safely.
/// </summary>
public sealed unsafe class NativeBattleBridge : IBattleEngineBridge
{
    private readonly record struct CachedSpItemTemplate(
        ushort ItemId,
        string Name,
        ushort SkillId,
        bool IsPartyWide,
        byte SpMode,
        ushort SpPwr,
        byte HpMode,
        ushort HpPwr);

    private readonly P4GNative _native;
    private nint _cachedItemTblPtr;
    private nint _cachedSkillSec1Ptr;
    private int _cachedItemCount;
    private List<CachedSpItemTemplate>? _cachedSpItems;

    public NativeBattleBridge(P4GNative native)
    {
        _native = native;
    }

    public BattleSnapshot? CaptureBattleSnapshot(nint actorUnitPtr)
    {
        nint battleMgr = _native.BattleManagerPtr;
        if (battleMgr == 0 || actorUnitPtr == 0)
            return null;

        var actor = ReadCombatantSnapshot(actorUnitPtr);
        if (actor == null || !actor.IsPartyMember)
            return null;

        var allies = new List<CombatantSnapshot>(4);
        var enemies = new List<CombatantSnapshot>(6);

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
                    var snap = ReadCombatantSnapshot(cur);
                    if (snap != null)
                        allies.Add(snap);
                }
                else if (unitType == 1)
                {
                    var snap = ReadCombatantSnapshot(cur);
                    if (snap != null)
                        enemies.Add(snap);
                }
            }
            cur = *(nint*)(cur + P4GNative.OffUnitNext);
        }

        if (enemies.Count == 0)
            return null;

        bool isOneMore = _native.IsUnitOn1More(actorUnitPtr) != 0;
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
            ActiveBattleBits = activeBattleBits
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
        uint stateFlags = *(uint*)(combPtr + P4GNative.OffCombStateFlags);
        byte tacticByte = *(byte*)(combPtr + P4GNative.OffCombTactic);
        uint chargeFlags = *(uint*)(combPtr + P4GNative.OffCombChargeFlags);
        ushort shieldFlags = *(ushort*)(combPtr + P4GNative.OffCombShieldFlags);

        int atkStage = _native.GetSignedStatStage(combPtr, 0);
        int magStage = _native.GetSignedStatStage(combPtr, 1);
        int defStage = _native.GetSignedStatStage(combPtr, 2);
        int hitStage = _native.GetSignedStatStage(combPtr, 3);

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
            StateFlags = stateFlags,
            ChargeFlags = chargeFlags,
            BaseFlags = baseFlags,
            ShieldFlags = shieldFlags,
            AtkStage = atkStage,
            MagStage = magStage,
            DefStage = defStage,
            HitEvaStage = hitStage,
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
            return new SkillMetadata { SkillId = skillId, Name = SkillCatalog.GetSkillName(skillId), Element = ElementId.Phys };
        }

        byte elem = sec0[skillId * 2];
        byte typ = sec0[skillId * 2 + 1];
        byte* e = sec1 + skillId * 0x2C;

        return new SkillMetadata
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
            MinHits = e[0x13] == 0 ? (byte)1 : e[0x13],
            MaxHits = e[0x14] == 0 ? (byte)1 : e[0x14],
            EffectType = e[0x15],
            Power = *(ushort*)(e + 0x16),
            Mode = e[0x18],
            AilmentMask = *(uint*)(e + 0x20),
            BuffMask = *(uint*)(e + 0x24)
        };
    }

    public int GetEffectiveCost(CombatantSnapshot actor, ushort skillId)
    {
        if (skillId == 0)
            return 0;
        return _native.GetEffectiveSkillCost(actor.CombatantPtr, skillId);
    }

    public bool CanUseSkill(CombatantSnapshot actor, ushort skillId)
    {
        if (skillId == 0)
            return !actor.IsDead;
        return _native.CanUnitUseSkill(actor.UnitPtr, skillId) != 0;
    }

    [ThreadStatic]
    public static bool DeterministicMidpointRand;

    public ElementId GetAttackElement(CombatantSnapshot actor)
    {
        if (actor.CombatantPtr == 0)
            return ElementId.Phys;

        int rawElem = _native.GetSkillElement(actor.CombatantPtr, 0);
        return Enum.IsDefined(typeof(ElementId), (byte)rawElem) ? (ElementId)(byte)rawElem : ElementId.Phys;
    }

    public AffinityInfo QueryEffectiveAffinity(CombatantSnapshot actor, CombatantSnapshot target, ElementId element, bool respectAnalysis)
    {
        // Note: Normal Attack uses the weapon's actual element when skillId==0; callers with skillId==0 should resolve via GetSkillElement if needed,
        // wait: P4G weapon attacks are Phys (elem 0), except rare edge cases. We'll use element directly here.
        uint raw = _native.GetEffectiveAffinity(target.CombatantPtr, (short)element);

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
        bool blockedByShield = blockedByKarn || target.HasShieldOfJustice || target.HasUnbreakableShield;

        // Visible Guard / Wall overrides
        bool hasVisibleWall = target.HasWallForElement(element);
        if (target.IsGuarding || hasVisibleWall || blockedByShield)
            isWeak = false;

        bool revealed = true;
        if (target.IsEnemy && (byte)element <= 7 && element != ElementId.Almighty)
            revealed = _native.IsAffinityRevealed(target.UnitOrEnemyId, (ushort)element) != 0;

        // When RespectAnalysisState == true and base affinity has NOT been analyzed yet:
        // Hide innate Weak/Resist/Null/Repel/Drain so AI plays fairly, EXCEPT always respect visible in-battle states (Karn/Shield/Wall/Guard/Break)!
        if (respectAnalysis && !revealed && target.IsEnemy)
        {
            return new AffinityInfo
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

        return new AffinityInfo
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

        int raw = CallNativeCalcDamageClean(skillId, attacker.CombatantPtr, target.CombatantPtr);
        var skill = GetSkill(skillId);
        ElementId actualElement = skillId == 0 ? GetAttackElement(attacker) : skill.Element;

        // If unanalyzed and RespectAnalysisState is true, normalize away hidden innate Null/Repel/Drain/Weakness/Resist multipliers
        double dmg;
        if (respectAnalysis && !effectiveAffinity.IsRevealed && target.IsEnemy && (byte)actualElement <= 7 && actualElement != ElementId.Almighty)
        {
            uint trueRawAff = _native.GetEffectiveAffinity(target.CombatantPtr, (short)actualElement);
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
                dmg = EstimateBaselineNeutralDamage(skill, actualElement, attacker, target, effectiveAffinity.MultiplierPercent);
            }
        }
        else
        {
            dmg = raw < 0 ? -raw : 0.0;
        }

        // Adjust Charge multiplier delta cleanly when simulating 2nd-step 1-More followups (Normal Attack always uses Power Charge)
        bool usesPowerCharge = skillId == 0 || actualElement == ElementId.Phys;
        bool currentlyCharged = usesPowerCharge ? attacker.HasPowerCharge : attacker.HasMindCharge;
        if (assumeUnchargedOverride && currentlyCharged && dmg > 0)
            dmg /= 2.5;
        else if (assumeChargedOverride && !currentlyCharged && dmg > 0)
            dmg *= 2.5;

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
        var results = new List<SpRecoveryItemInfo>();
        byte* inv = _native.InventoryPtr;
        byte* itemTbl = _native.ItemTblPtr;
        byte* sec1 = _native.SkillSec1Ptr;
        if (inv == null || itemTbl == null || sec1 == null)
            return results;

        int itemCount = _native.ItemTblCount;
        if (itemCount <= 0 || itemCount > 4096)
            itemCount = 2560;

        var catalog = GetOrBuildSpItemCatalog(itemTbl, sec1, _native.ItemNamePtr, itemCount);
        foreach (var tpl in catalog)
        {
            byte qty = inv[tpl.ItemId];
            if (qty == 0)
                continue;

            int estSp = ResolveRecoveryValue(tpl.SpMode, tpl.SpPwr, beneficiary.MaxSp);
            if (estSp <= 0)
                continue;

            int estHp = ResolveRecoveryValue(tpl.HpMode, tpl.HpPwr, beneficiary.MaxHp);
            results.Add(new SpRecoveryItemInfo
            {
                ItemId = tpl.ItemId,
                Name = tpl.Name,
                SkillId = tpl.SkillId,
                Quantity = qty,
                IsPartyWide = tpl.IsPartyWide,
                EstimatedSpGain = estSp,
                EstimatedHpGain = estHp,
                IsFullSp = tpl.SpMode == 11 && tpl.SpPwr >= 100,
                IsFullHp = tpl.HpMode == 11 && tpl.HpPwr >= 100
            });
        }

        return results;
    }

    private List<CachedSpItemTemplate> GetOrBuildSpItemCatalog(byte* itemTbl, byte* sec1, byte* itemNames, int itemCount)
    {
        if (_cachedSpItems != null &&
            _cachedItemTblPtr == (nint)itemTbl &&
            _cachedSkillSec1Ptr == (nint)sec1 &&
            _cachedItemCount == itemCount)
        {
            return _cachedSpItems;
        }

        var list = new List<CachedSpItemTemplate>(24);
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
            if (sk[0x0D] != 1 || sk[0x0E] != 0) // Ally target, non-revive
                continue;

            byte spMode = sk[0x18];
            ushort spPwr = *(ushort*)(sk + 0x1A);
            if ((spMode != 5 && spMode != 11) || spPwr == 0)
                continue;

            list.Add(new CachedSpItemTemplate(
                (ushort)id,
                ReadItemName(itemNames, id),
                skillId,
                sk[0x0C] == 1,
                spMode,
                spPwr,
                sk[0x15],
                *(ushort*)(sk + 0x16)));
        }

        _cachedItemTblPtr = (nint)itemTbl;
        _cachedSkillSec1Ptr = (nint)sec1;
        _cachedItemCount = itemCount;
        _cachedSpItems = list;
        return list;
    }

    private static int ResolveRecoveryValue(byte mode, ushort power, int maxStat) => mode switch
    {
        5 => power,
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

    private static double EstimateBaselineNeutralDamage(
        SkillMetadata skill,
        ElementId actualElement,
        CombatantSnapshot attacker,
        CombatantSnapshot target,
        int visMultPct)
    {
        int pwr = skill.SkillId == 0 ? 15 : skill.Power;
        double est = Math.Sqrt(Math.Max(1, pwr)) * 8.5 * (visMultPct / 100.0);
        if (target.DefStage < 0) est *= 1.3;
        if (target.DefStage > 0) est *= 0.75;
        int atkStage = skill.StatType == 1 ? attacker.AtkStage : attacker.MagStage;
        if (atkStage > 0) est *= 1.3;
        if (atkStage < 0) est *= 0.75;
        bool usesPowerCharge = skill.SkillId == 0 || actualElement == ElementId.Phys;
        if ((usesPowerCharge && attacker.HasPowerCharge) || (!usesPowerCharge && attacker.HasMindCharge))
            est *= 2.5;
        return est;
    }
}
