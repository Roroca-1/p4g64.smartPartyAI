using System.Drawing;
using p4g64.smartPartyAI.Ai;
using p4g64.smartPartyAI.Core;
using p4g64.smartPartyAI.Native;
using Reloaded.Hooks.Definitions;
using Reloaded.Mod.Interfaces;
using Reloaded.Mod.Interfaces.Internal;
using IReloadedHooks = Reloaded.Hooks.ReloadedII.Interfaces.IReloadedHooks;

namespace p4g64.smartPartyAI;

/// <summary>
/// Reloaded-II mod entry point for p4g64.smartPartyAI.
/// </summary>
public unsafe class Mod : IModV2
{
    private IModLoaderV1 _modLoader = null!;
    private ILogger _logger = null!;
    private IReloadedHooks _hooks = null!;
    private Config _config = new();

    private P4GNative _native = null!;
    private NativeBattleBridge _bridge = null!;
    private SmartPartyAiEngine _aiEngine = null!;
    private IHook<P4GNative.BtlAiStateUpdateDel>? _aiStateHook;
    private IHook<P4GNative.GetRandDel>? _randHook;

    public void Start(IModLoaderV1 loader)
    {
        // IModV2 uses StartEx when available.
    }

    public void StartEx(IModLoaderV1 loader, IModConfigV1 modConfig)
    {
        _modLoader = loader;
        _logger = (ILogger)loader.GetLogger();

        var configDir = (loader as IModLoaderV3)?.GetModConfigDirectory(modConfig.ModId)
                        ?? (loader as IModLoaderV2)?.GetDirectoryForModId(modConfig.ModId)
                        ?? AppContext.BaseDirectory;
        var configPath = Path.Combine(configDir, "Config.json");
        _config = Configurable<Config>.Load(configPath);
        _config.ConfigurationUpdated += updated =>
        {
            if (updated is Config newCfg)
            {
                _config = newCfg;
                Log("Configuration reloaded.");
            }
        };

        var hooksController = loader.GetController<IReloadedHooks>();
        if (hooksController == null || !hooksController.TryGetTarget(out _hooks!))
        {
            _logger.WriteLine("[SmartPartyAI] ERROR: Could not resolve IReloadedHooks controller. Check reloaded.sharedlib.hooks dependency.", Color.Red);
            return;
        }

        _native = new P4GNative(_hooks);
        _bridge = new NativeBattleBridge(_native);
        _aiEngine = new SmartPartyAiEngine(_bridge);

        long hookAddr = _native.BaseAddress + P4GNative.RvaBtlAiStateUpdate;
        _aiStateHook = _hooks.CreateHook<P4GNative.BtlAiStateUpdateDel>(OnBtlAiStateUpdate, hookAddr).Activate();
        _randHook = _hooks.CreateHook<P4GNative.GetRandDel>(OnGetRand, _native.BaseAddress + P4GNative.RvaGetRand).Activate();

        Log($"Initialized cleanly! Base=0x{_native.BaseAddress:X}, AI Hook=0x{hookAddr:X}, RespectAnalysis={_config.RespectAnalysisState}");
    }

    private uint OnGetRand()
    {
        if (NativeBattleBridge.DeterministicMidpointRand)
            return 5u; // (5 % 11) + 95 = 100 -> 1.00x exact expected damage without advancing P4G RNG

        return _randHook!.OriginalFunction();
    }

    private void OnBtlAiStateUpdate(nint unitPtr)
    {
        bool wasPartyAiCandidate = false;
        nint unitData = 0;
        nint combatant = 0;

        if (unitPtr != 0)
        {
            unitData = *(nint*)(unitPtr + P4GNative.OffUnitDataPtr);
            if (unitData != 0 && *(byte*)(unitData + P4GNative.OffUnitDataType) == 0)
            {
                ushort unitId = *(ushort*)(unitData + P4GNative.OffUnitDataId);
                byte wasRunning = *(byte*)(unitPtr + P4GNative.OffUnitAiRunning);
                ushort prevState = *(ushort*)(unitPtr + P4GNative.OffUnitState);
                combatant = *(nint*)(unitData + P4GNative.OffUnitDataCombatant);

                // Party members (Yosuke=2, Chie=3, Yukiko=4, Kanji=6, Naoto=7, Teddie=8) waiting for AI completion
                if (unitId is 2 or 3 or 4 or 6 or 7 or 8 && combatant != 0 && (wasRunning != 0 || prevState != 0x11))
                    wasPartyAiCandidate = true;
            }
        }

        _aiStateHook!.OriginalFunction(unitPtr);

        if (!wasPartyAiCandidate)
            return;

        // Only override on the exact frame the AI state machine finishes and transitions into 0x11 (Execute Action)
        if (*(ushort*)(unitPtr + P4GNative.OffUnitState) != 0x11)
            return;

        byte tacticByte = *(byte*)(combatant + P4GNative.OffCombTactic);
        if (tacticByte == (byte)TacticMode.DirectCommands)
            return;

        uint stateFlags = *(uint*)(combatant + P4GNative.OffCombStateFlags);
        // Do not override loss-of-control ailments (Dead=0x80000, Charm=0x100, Enervate=0x80, Confuse=0x10, Fear=0x04, Rage=0x02)
        if ((stateFlags & 0x80196) != 0)
            return;

        ushort vanillaActionType = *(ushort*)(unitPtr + P4GNative.OffUnitActionType);
        // Preserve special scripted Items (3) or Escape (6)
        if (vanillaActionType is 3 or 6)
            return;

        try
        {
            var battle = _bridge.CaptureBattleSnapshot(unitPtr);
            if (battle == null)
                return;

            var decision = _aiEngine.DecideAction(battle, _config);
            if (decision == null)
                return;

            ApplyDecision(unitPtr, decision);

            if (_config.DebugLogging)
            {
                Log($"{battle.Actor.DisplayName} [{battle.Actor.Tactic}, 1More={battle.IsActorOnOneMore}, HP={battle.Actor.CurrentHp}/{battle.Actor.MaxHp}, SP={battle.Actor.CurrentSp}/{battle.Actor.MaxSp}] -> {decision.Reason}");
            }
        }
        catch (Exception ex)
        {
            _logger.WriteLine($"[SmartPartyAI] Exception during AI override: {ex}", Color.OrangeRed);
        }
        finally
        {
            _bridge.ClearTurnCache();
        }
    }

    private void ApplyDecision(nint unitPtr, AiDecision decision)
    {
        nint aiActionPtr = unitPtr + P4GNative.OffUnitTargets; // unit + 0x48

        if (decision.ActionKind == AiActionKind.NormalAttack)
        {
            _native.SetAiAction(unitPtr, aiActionPtr, 0x8000);
            if (decision.Target != null && decision.Target.UnitPtr != 0)
                WriteSingleTarget(unitPtr, decision.Target.UnitPtr);
            return;
        }

        if (decision.ActionKind == AiActionKind.Guard)
        {
            *(ushort*)(unitPtr + P4GNative.OffUnitWaitMsgId) = 0;
            nint unitData = *(nint*)(unitPtr + P4GNative.OffUnitDataPtr);
            if (unitData != 0)
            {
                nint comb = *(nint*)(unitData + P4GNative.OffUnitDataCombatant);
                if (comb != 0)
                    *(ushort*)(comb + P4GNative.OffCombBaseFlags) = (ushort)(*(ushort*)(comb + P4GNative.OffCombBaseFlags) & ~0x200);
            }
            ClearTargets(unitPtr);
            _native.SetAiAction(unitPtr, aiActionPtr, 0x8002);
            *(ushort*)(unitPtr + P4GNative.OffUnitSkillId) = 0;
            return;
        }

        if (decision.ActionKind == AiActionKind.Wait)
        {
            ClearTargets(unitPtr);
            _native.SetAiAction(unitPtr, aiActionPtr, 0x800A);
            *(ushort*)(unitPtr + P4GNative.OffUnitSkillId) = 0;
            return;
        }

        if (decision.ActionKind == AiActionKind.Item)
        {
            var itemSkill = _bridge.GetSkill(decision.SkillId);
            _native.SetAiAction(unitPtr, aiActionPtr, decision.SkillId);
            *(ushort*)(unitPtr + P4GNative.OffUnitActionType) = 3; // ActionType 3 = Consumable Item
            *(ushort*)(unitPtr + P4GNative.OffUnitSkillId) = decision.SkillId;
            *(ushort*)(unitPtr + P4GNative.OffUnitItemId) = decision.ItemId;

            if (itemSkill.IsAoE && decision.Targets != null && decision.Targets.Count > 0)
            {
                WriteTargetList(unitPtr, decision.Targets);
                if (*(ushort*)(unitPtr + P4GNative.OffUnitTargetCount) == 0)
                    _native.ResolveAiActionTargets(unitPtr, 0);
            }
            else
            {
                AssignTargetOrResolve(unitPtr, itemSkill, decision.Target);
            }
            return;
        }

        var skill = _bridge.GetSkill(decision.SkillId);
        _native.SetAiAction(unitPtr, aiActionPtr, decision.SkillId);
        AssignTargetOrResolve(unitPtr, skill, decision.Target);
    }

    private void AssignTargetOrResolve(nint unitPtr, SkillMetadata skill, CombatantSnapshot? target)
    {
        if (skill.IsAoE || skill.IsSelfOnly || skill.Scope == 2)
        {
            // Populates targets[] and targetCount for all valid living/dead AoE/Self targets
            _native.ResolveAiActionTargets(unitPtr, 0);
        }
        else if (target != null && target.UnitPtr != 0)
        {
            WriteSingleTarget(unitPtr, target.UnitPtr);
        }
        else
        {
            _native.ResolveAiActionTargets(unitPtr, 0);
        }
    }

    private static void WriteSingleTarget(nint unitPtr, nint targetUnitPtr)
    {
        ClearTargets(unitPtr);
        *(nint*)(unitPtr + P4GNative.OffUnitTargets) = targetUnitPtr;
        *(ushort*)(unitPtr + P4GNative.OffUnitTargetCount) = 1;
    }

    private static void WriteTargetList(nint unitPtr, IReadOnlyList<CombatantSnapshot> targets)
    {
        ClearTargets(unitPtr);
        nint* targetArr = (nint*)(unitPtr + P4GNative.OffUnitTargets);
        int count = 0;
        foreach (var t in targets)
        {
            if (t.UnitPtr != 0 && count < 12)
                targetArr[count++] = t.UnitPtr;
        }
        *(ushort*)(unitPtr + P4GNative.OffUnitTargetCount) = (ushort)count;
    }

    private static void ClearTargets(nint unitPtr)
    {
        nint* targetArr = (nint*)(unitPtr + P4GNative.OffUnitTargets);
        for (int i = 0; i < 12; i++)
            targetArr[i] = 0;
        *(ushort*)(unitPtr + P4GNative.OffUnitTargetCount) = 0;
    }

    private void Log(string msg) => _logger.WriteLine($"[SmartPartyAI] {msg}", Color.LightSkyBlue);

    public Action Disposing { get; } = () => { };

    public void Suspend()
    {
        _aiStateHook?.Disable();
        _randHook?.Disable();
    }

    public void Resume()
    {
        _aiStateHook?.Enable();
        _randHook?.Enable();
    }

    public void Unload()
    {
        Disposing();
        _aiStateHook?.Disable();
        _randHook?.Disable();
    }
    public bool CanUnload() => true;
    public bool CanSuspend() => true;
}
