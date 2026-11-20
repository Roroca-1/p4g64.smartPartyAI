using System.Diagnostics;
using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions.X64;
using Reloaded.Hooks.ReloadedII.Interfaces;

namespace p4g64.smartPartyAI.Native;

/// <summary>
/// P4G PC 64-bit native offsets (relative to main module BaseAddress, reference ImageBase = 0x140000000),
/// structs, and function wrappers.
/// </summary>
public sealed unsafe class P4GNative
{
    public const int MaxSkillTableId = 0x1D8; // 472 entries in SKILL.TBL Sec 1

    // Verified Global Pointer RVAs (VA - 0x140000000)
    public const long RvaBattleManagerPtr = 0x0EC08F0; // QWORD* -> BattleManager
    public const long RvaSkillSec1Ptr = 0x0EC0910;     // QWORD* -> SKILL.TBL Section 1 (0x2C bytes/skill)
    public const long RvaSkillSec0Ptr = 0x0EC0940;     // QWORD* -> SKILL.TBL Section 0 (2 bytes/skill)
    public const long RvaEffectStageTablePtr = 0x0EC09C0; // QWORD* -> EFFECT.TBL stage multiplier percentages
    public const long RvaInventoryPtr = 0x1165930;     // QWORD* -> Item count byte array (byte[itemId])
    public const long RvaItemNamePtr = 0x1165938;      // QWORD* -> Item name ASCII table (24 bytes/item)
    public const long RvaItemCountVal = 0x1165940;     // uint -> Loaded itemtbl entry count (2560 vanilla)
    public const long RvaItemTblPtr = 0x11A5940;       // QWORD* -> Loaded itemtbl records (0x44 bytes/item)
    public const long RvaBitflagArrayPtr = 0x51FF7A0;  // QWORD* -> global bitflag array (battle bits at 5120 + idx)

    // Verified Native Function RVAs (VA - 0x140000000)
    public const long RvaBtlAiStateUpdate = 0x04B0A0;
    public const long RvaCanUnitUseSkill = 0x0639F0;
    public const long RvaIsUnitOn1More = 0x0620B0;
    public const long RvaResolveAiActionTargets = 0x064F50;
    public const long RvaSetAiAction = 0x0697F0;
    public const long RvaIsAffinityRevealed = 0x0A8AD0;
    public const long RvaGetMaxHp = 0x0CF510;
    public const long RvaGetMaxSp = 0x0CF790;
    public const long RvaGetCombatantStat = 0x0D00C0; // int(combatant, ushort statIdx: 3 = Agility)
    public const long RvaGetStatStage = 0x0D08C0;
    public const long RvaCalcSkillDamage = 0x0D1910;
    public const long RvaGetEffectiveAffinity = 0x0D35B0;
    public const long RvaGetSkillElement = 0x0D3E80;
    public const long RvaGetEffectiveSkillCost = 0x0D3F40;
    public const long RvaGetPartyPersonaData = 0x0D6E80;
    public const long RvaGetRand = 0x520960;

    // Struct field offsets verified via P4G.exe disassembly
    public const int OffBattleMgrOneMoreSide = 0x1E;   // ushort: 1 = Party 1 More, 2 = Enemy 1 More
    public const int OffBattleMgrUnitListHead = 0x1C0; // BattleUnit*
    public const int OffBattleMgrTurnQueue = 0x398;    // BattleUnit*[12] current round execution order
    public const int BattleTurnQueueCapacity = 12;
    public const int OffBattleMgrOneMoreUnit = 0x3F8;  // BattleUnit* currently acting on 1 More

    public const int OffUnitSlotId = 0x08;      // int
    public const int OffUnitState = 0x0C;       // ushort (0x11 = AI ready to execute)
    public const int OffUnitFlags = 0x20;       // uint ((flags & 0x9) == 0x9 active/targetable)
    public const int OffUnitDataPtr = 0x38;     // UnitData*
    public const int OffUnitTargets = 0x48;     // BattleUnit*[...]
    public const int OffUnitAiRunning = 0xA8;   // byte
    public const int OffUnitTargetCount = 0xAA; // ushort
    public const int OffUnitActionType = 0xAC;     // ushort (1=Attack, 2=Skill, 3=Item, 6=Escape, 7=Wait/Guard, 8=Wait)
    public const int OffUnitSkillId = 0xAE;        // ushort
    public const int OffUnitItemId = 0xB0;         // ushort (inventory item ID when ActionType == 3)
    public const int OffUnitLastActionType = 0xBC; // ushort
    public const int OffUnitLastSkillId = 0xBE;    // ushort
    public const int OffUnitWaitMsgId = 0x188;     // ushort (set by COMM 0x2121 telegraph wait)
    public const int OffUnitNext = 0x520;          // BattleUnit*

    public const int OffUnitDataType = 0xA2;       // byte (0=Party, 1=Enemy, 2=Support/Rise)
    public const int OffUnitDataId = 0xA4;         // ushort (Party ID 1..8 or Enemy ID)
    public const int OffUnitDataCombatant = 0xCF0; // Combatant*

    public const int OffCombBaseFlags = 0x00;    // ushort (0x100 = Unbreakable Shield, 0x200 = Guard)
    public const int OffCombPersonaOrId = 0x02;  // ushort (Persona ID for party)
    public const int OffCombEnemyTblId = 0x04;   // ushort
    public const int OffCombHp = 0x08;           // short
    public const int OffCombSp = 0x0A;           // short
    public const int OffCombStateFlags = 0x0C;   // uint (0x80000=Dead, 0x100000=Down, 0x40000=Dizzy)
    public const int OffCombTactic = 0x10;       // byte (0=ActFreely, 1=FullAssault, 2=ConserveSP, 3=HealSupport, 4=Direct)
    public const int OffCombChargeFlags = 0x14;  // uint (bit17=Tetrakarn, bit18=Makarakarn, bit19=PowerCharge, bit20=MindCharge)
    public const int OffCombShieldFlags = 0x16;  // ushort (bit0=Shield of Justice)
    public const int OffCombTempScratch = 0x18;  // uint (must zero on attacker & target after CalcSkillDamage)
    public const int OffCombBreaksByte0 = 0x20;  // byte (lo=Fire Break, hi=Ice Break)
    public const int OffCombBreaksByte1 = 0x21;  // byte (lo=Wind Break, hi=Elec Break)
    public const int OffCombWallsByte0 = 0x22;   // byte (lo=Red Wall, hi=Blue Wall)
    public const int OffCombWallsByte1 = 0x23;   // byte (lo=Green Wall, hi=White Wall)
    public const int OffCombStatTurns = 0x25;    // byte[8] (4-bit remaining-turn nibbles for stat stages 0..15)

    public const int OffPersonaSkills = 0x0C;    // ushort[8]

    // Delegates (x64 MS ABI via Reloaded.Hooks)
    [Function(CallingConventions.Microsoft)]
    public delegate void BtlAiStateUpdateDel(nint unit);

    [Function(CallingConventions.Microsoft)]
    public delegate int CanUnitUseSkillDel(nint unit, ushort skillId);

    [Function(CallingConventions.Microsoft)]
    public delegate int IsUnitOn1MoreDel(nint unit);

    [Function(CallingConventions.Microsoft)]
    public delegate int ResolveAiActionTargetsDel(nint unit, int arg2);

    [Function(CallingConventions.Microsoft)]
    public delegate void SetAiActionDel(nint unit, nint aiActionPtr, ushort actionCode);

    [Function(CallingConventions.Microsoft)]
    public delegate int IsAffinityRevealedDel(ushort enemyId, ushort element);

    [Function(CallingConventions.Microsoft)]
    public delegate int GetMaxHpDel(nint combatant);

    [Function(CallingConventions.Microsoft)]
    public delegate int GetMaxSpDel(nint combatant);

    [Function(CallingConventions.Microsoft)]
    public delegate int GetCombatantStatDel(nint combatant, ushort statIdx);

    [Function(CallingConventions.Microsoft)]
    public delegate int GetStatStageDel(nint combatant, byte index);

    [Function(CallingConventions.Microsoft)]
    public delegate int CalcSkillDamageDel(
        ushort skillId,
        nint attackerCombatant,
        nint targetCombatant,
        ushort arg4,
        ushort arg5,
        ushort arg6,
        int arg7,
        byte arg8);

    [Function(CallingConventions.Microsoft)]
    public delegate uint GetEffectiveAffinityDel(nint targetCombatant, short element);

    [Function(CallingConventions.Microsoft)]
    public delegate int GetSkillElementDel(nint attackerCombatant, ushort skillId);

    [Function(CallingConventions.Microsoft)]
    public delegate int GetEffectiveSkillCostDel(nint attackerCombatant, ushort skillId);

    [Function(CallingConventions.Microsoft)]
    public delegate nint GetPartyPersonaDataDel(ushort personaId);

    [Function(CallingConventions.Microsoft)]
    public delegate uint GetRandDel();

    public nint BaseAddress { get; }

    public CanUnitUseSkillDel CanUnitUseSkill { get; }
    public IsUnitOn1MoreDel IsUnitOn1More { get; }
    public ResolveAiActionTargetsDel ResolveAiActionTargets { get; }
    public SetAiActionDel SetAiAction { get; }
    public IsAffinityRevealedDel IsAffinityRevealed { get; }
    public GetMaxHpDel GetMaxHp { get; }
    public GetMaxSpDel GetMaxSp { get; }
    public GetCombatantStatDel GetCombatantStat { get; }
    public GetStatStageDel GetStatStage { get; }
    public CalcSkillDamageDel CalcSkillDamage { get; }
    public GetEffectiveAffinityDel GetEffectiveAffinity { get; }
    public GetSkillElementDel GetSkillElement { get; }
    public GetEffectiveSkillCostDel GetEffectiveSkillCost { get; }
    public GetPartyPersonaDataDel GetPartyPersonaData { get; }

    public P4GNative(IReloadedHooks hooks)
    {
        BaseAddress = Process.GetCurrentProcess().MainModule!.BaseAddress;

        CanUnitUseSkill = hooks.CreateWrapper<CanUnitUseSkillDel>(BaseAddress + RvaCanUnitUseSkill, out _);
        IsUnitOn1More = hooks.CreateWrapper<IsUnitOn1MoreDel>(BaseAddress + RvaIsUnitOn1More, out _);
        ResolveAiActionTargets = hooks.CreateWrapper<ResolveAiActionTargetsDel>(BaseAddress + RvaResolveAiActionTargets, out _);
        SetAiAction = hooks.CreateWrapper<SetAiActionDel>(BaseAddress + RvaSetAiAction, out _);
        IsAffinityRevealed = hooks.CreateWrapper<IsAffinityRevealedDel>(BaseAddress + RvaIsAffinityRevealed, out _);
        GetMaxHp = hooks.CreateWrapper<GetMaxHpDel>(BaseAddress + RvaGetMaxHp, out _);
        GetMaxSp = hooks.CreateWrapper<GetMaxSpDel>(BaseAddress + RvaGetMaxSp, out _);
        GetCombatantStat = hooks.CreateWrapper<GetCombatantStatDel>(BaseAddress + RvaGetCombatantStat, out _);
        GetStatStage = hooks.CreateWrapper<GetStatStageDel>(BaseAddress + RvaGetStatStage, out _);
        CalcSkillDamage = hooks.CreateWrapper<CalcSkillDamageDel>(BaseAddress + RvaCalcSkillDamage, out _);
        GetEffectiveAffinity = hooks.CreateWrapper<GetEffectiveAffinityDel>(BaseAddress + RvaGetEffectiveAffinity, out _);
        GetSkillElement = hooks.CreateWrapper<GetSkillElementDel>(BaseAddress + RvaGetSkillElement, out _);
        GetEffectiveSkillCost = hooks.CreateWrapper<GetEffectiveSkillCostDel>(BaseAddress + RvaGetEffectiveSkillCost, out _);
        GetPartyPersonaData = hooks.CreateWrapper<GetPartyPersonaDataDel>(BaseAddress + RvaGetPartyPersonaData, out _);
    }

    public nint BattleManagerPtr => *(nint*)(BaseAddress + RvaBattleManagerPtr);
    public byte* SkillSec0Ptr => *(byte**)(BaseAddress + RvaSkillSec0Ptr);
    public byte* SkillSec1Ptr => *(byte**)(BaseAddress + RvaSkillSec1Ptr);
    public byte* InventoryPtr => *(byte**)(BaseAddress + RvaInventoryPtr);
    public byte* ItemNamePtr => *(byte**)(BaseAddress + RvaItemNamePtr);
    public byte* ItemTblPtr => *(byte**)(BaseAddress + RvaItemTblPtr);
    public int ItemTblCount => *(int*)(BaseAddress + RvaItemCountVal);

    /// <summary>
    /// Calls native GetStatStage (0x1400d08c0) and sign-extends AL cleanly, matching callers like 0x1400d0a0c
    /// (GetStatStage zero-extends AL into EAX via movzbl, so negative stages come back as 255, 254, etc.).
    /// </summary>
    public sbyte GetSignedStatStage(nint combatant, byte index) =>
        Core.SkillCatalog.DecodeSignedStatStageByte(GetStatStage(combatant, index));

    /// <summary>
    /// Reads remaining buff/debuff turns (0..15) stored as 4-bit nibbles at Combatant + 0x25..0x2C
    /// (verified in P4G.exe 0x1400d070b and 0x1400d0804).
    /// </summary>
    public int GetStatStageDuration(nint combatant, byte index)
    {
        if (combatant == 0 || index >= 16)
            return 0;

        byte packed = *(byte*)(combatant + OffCombStatTurns + (index >> 1));
        return (index & 1) != 0 ? (packed >> 4) & 0x0F : packed & 0x0F;
    }

    /// <summary>
    /// Reads live buff/debuff stage damage multiplier from P4G EFFECT.TBL (0x140ec09c0),
    /// matching GetStatStageMultiplier (0x1400d09a0). Falls back cleanly if unloaded.
    /// </summary>
    public double GetStageMultiplier(bool isEnemy, bool isDefense, int stage)
    {
        int clamped = Math.Clamp(stage, -1, 1);
        byte* tbl = *(byte**)(BaseAddress + RvaEffectStageTablePtr);
        if (tbl != null)
        {
            int idx = isDefense
                ? ((isEnemy ? 3 : 2) * 3) + (1 - clamped)
                : ((isEnemy ? 1 : 0) * 3) + (clamped + 1);
            byte raw = tbl[idx];
            if (raw is >= 25 and <= 250)
                return raw / 100.0;
        }

        if (isDefense)
            return clamped < 0 ? 1.40 : (clamped > 0 ? 0.70 : 1.0);
        return clamped > 0 ? 1.33 : (clamped < 0 ? 0.75 : 1.0);
    }

    public bool IsBattleBitSet(int battleBitIndex)
    {
        if (battleBitIndex < 0)
            return false;

        nint arrPtr = *(nint*)(BaseAddress + RvaBitflagArrayPtr);
        if (arrPtr == 0)
            return false;

        int fullBit = 5120 + battleBitIndex;
        uint word = *(uint*)(arrPtr + ((fullBit >> 5) * 4));
        return (word & (1u << (fullBit & 31))) != 0;
    }

    public static sbyte UnpackSignedNibble(byte b, bool highNibble) =>
        Core.SkillCatalog.UnpackSignedNibble(b, highNibble);
}
