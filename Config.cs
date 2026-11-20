using System.ComponentModel;

namespace p4g64.smartPartyAI;

/// <summary>
/// User-facing configuration options for Smart Party AI Overhaul.
/// </summary>
public class Config : Configurable<Config>
{
    [Category("Affinity & Knowledge")]
    [DisplayName("Respect Analysis State")]
    [Description("When enabled, the AI only exploits enemy weaknesses and blocks that have been revealed in the Analyze panel, plus always-visible in-battle states (Tetrakarn, Makarakarn, Red/Blue/Green/White Walls, Breaks, Guard, Shield of Justice). Disable for Turn-1 omniscience.")]
    [DefaultValue(true)]
    public bool RespectAnalysisState { get; set; } = true;

    [Category("Affinity & Knowledge")]
    [DisplayName("Probe Unrevealed Elements")]
    [Description("When Respect Analysis State is enabled, allows AI party members on non-boss enemies to test unrevealed single-target elemental affinities instead of only spamming Physical/highest-power skills.")]
    [DefaultValue(true)]
    public bool ProbeUnrevealedElements { get; set; } = true;

    [Category("Affinity & Knowledge")]
    [DisplayName("Smart Telegraph & Weakness Guard")]
    [Description("When enabled, party members automatically Guard (or cast an Elemental Wall) when a boss telegraphs an elemental attack targeting their weakness (e.g. Shadow Yukiko's passionate stare) or a party-wipe ultimate (e.g. Shadow Teddie's Nihil Hand).")]
    [DefaultValue(true)]
    public bool EnableTelegraphGuard { get; set; } = true;

    [Category("Tactics Presets")]
    [DisplayName("Enable Smart Full Assault")]
    [Description("Replaces Full Assault with expected-damage + 1-More 2-step lookahead + AoE group math + barrier avoidance.")]
    [DefaultValue(true)]
    public bool EnableSmartFullAssault { get; set; } = true;

    [Category("Tactics Presets")]
    [DisplayName("Full Assault Offensive Setups")]
    [Description("Allows Full Assault to cast pure offensive buffs/debuffs (Matarukaja, Rakunda, Marakunda, Debilitate, carry Tarukaja) on tough fights strictly when multi-turn team DPS math predicts higher total damage.")]
    [DefaultValue(true)]
    public bool AllowOffensiveBuffsInFullAssault { get; set; } = true;

    [Category("Tactics Presets")]
    [DisplayName("Enable Smart Conserve SP")]
    [Description("Replaces Conserve SP with zero-SP Phys/Attack optimization, safe HP gating, Spirit Drain recovery, and low-cost 1-More/All-Out Attack starters.")]
    [DefaultValue(true)]
    public bool EnableSmartConserveSp { get; set; } = true;

    [Category("Tactics Presets")]
    [DisplayName("Conserve SP Unanalyzed Probing")]
    [Description("When SP is healthy (>= 35%), allows Conserve SP to spend a cheap spell (within Low-Cost Starter Max SP) to test an unrevealed affinity on standing non-boss shadows if zero-SP attacks cannot finish the battle cleanly.")]
    [DefaultValue(true)]
    public bool ConserveSpProbeUnrevealed { get; set; } = true;

    [Category("Tactics Presets")]
    [DisplayName("Enable Smart Heal / Support")]
    [Description("Replaces Heal/Support with percentage-based HP healing, revive/ailment triage, Dekaja/Dekunda, boss buffs/debuffs, Elemental Walls, and smart 1-More offense when healthy.")]
    [DefaultValue(true)]
    public bool EnableSmartHealSupport { get; set; } = true;

    [Category("Tactics Presets")]
    [DisplayName("Enable Smart Act Freely")]
    [Description("Replaces Act Freely with a Balanced All-Rounder (emergency healing, All-Out Attack priority, key boss buffs/debuffs, and Full Assault offense).")]
    [DefaultValue(true)]
    public bool EnableSmartActFreely { get; set; } = true;

    [Category("Thresholds")]
    [DisplayName("Heal/Support HP Threshold (%)")]
    [Description("Percentage of max HP below which Heal/Support prioritizes healing an ally.")]
    [DefaultValue(60)]
    public int HealSupportHpThreshold { get; set; } = 60;

    [Category("Thresholds")]
    [DisplayName("Act Freely Emergency HP Threshold (%)")]
    [Description("Percentage of max HP below which Act Freely interrupts offense to heal a critically low ally.")]
    [DefaultValue(40)]
    public int ActFreelyEmergencyHpThreshold { get; set; } = 40;

    [Category("Thresholds")]
    [DisplayName("Conserve SP Safe HP Floor (%)")]
    [Description("Minimum attacker HP% required before Conserve SP spends HP on Physical skills.")]
    [DefaultValue(45)]
    public int ConserveSpSafeHpPercent { get; set; } = 45;

    [Category("Thresholds")]
    [DisplayName("Conserve SP Low-Cost Starter Max SP")]
    [Description("Maximum effective SP cost allowed in Conserve SP to cast an elemental spell for a 1-More or All-Out Attack setup.")]
    [DefaultValue(8)]
    public int ConserveSpLowCostThreshold { get; set; } = 8;

    [Category("Items & SP Recovery")]
    [DisplayName("Use SP Recovery Items")]
    [Description("Allow AI party members to use SP-restoring consumables (Soul Drop, Snuff Soul, Chewing Soul, etc.) from shared inventory when SP runs low.")]
    [DefaultValue(true)]
    public bool EnableSpRecoveryItems { get; set; } = true;

    [Category("Items & SP Recovery")]
    [DisplayName("SP Item Threshold (%)")]
    [Description("Trigger SP item recovery when a party member's SP drops at or below this percentage of Max SP.")]
    [DefaultValue(20)]
    public int SpRecoveryItemThresholdPercent { get; set; } = 20;

    [Category("Items & SP Recovery")]
    [DisplayName("Allow Ally SP Item Support")]
    [Description("Allow Act Freely and Heal/Support party members to use SP items on low-SP allies (including the Protagonist), not only themselves.")]
    [DefaultValue(true)]
    public bool AllowAllySpItemSupport { get; set; } = true;

    [Category("Items & SP Recovery")]
    [DisplayName("Allow Full-SP / Rare Items (Soul Food / Soma)")]
    [Description("When false, AI preserves rare full-SP consumables (Soul Food, Soma) outside critical high-HP boss fights and only uses partial SP items.")]
    [DefaultValue(false)]
    public bool AllowRareFullSpItems { get; set; } = false;

    [Category("Items & SP Recovery")]
    [DisplayName("Use Elemental & Fixed-Damage Items")]
    [Description("Allow AI (especially Act Freely) to throw elemental damage items (e.g., Firecracker, Dry Ice, Pinwheel) to hit revealed enemy weaknesses for free 1 More or All-Out Attacks, bypass immunities, or finish low-HP targets.")]
    [DefaultValue(true)]
    public bool EnableDamageItemsInAi { get; set; } = true;

    [Category("Items & SP Recovery")]
    [DisplayName("Use Healing / Cure / Revive Items")]
    [Description("Allow Act Freely and Heal/Support party members to use HP, Ailment Cure, and Revive consumables when they lack the needed spell or are Silenced.")]
    [DefaultValue(true)]
    public bool EnableSupportItemsInAi { get; set; } = true;

    [Category("Items & SP Recovery")]
    [DisplayName("Allow Rare Consumables (Bead Chain / Balm / Rare Gems)")]
    [Description("When false, protects irreplaceable or full-party rare consumables (Balm of Life, Bead Chain, Amrita Soda, top-tier attack items) from automatic AI use.")]
    [DefaultValue(false)]
    public bool AllowRareConsumables { get; set; } = false;

    [Category("Items & SP Recovery")]
    [DisplayName("Probe Unrevealed Enemies With Items")]
    [Description("When false, the AI never spends finite elemental damage items just to probe unanalyzed affinities (only uses them on revealed weaknesses, kills, or immune targets).")]
    [DefaultValue(false)]
    public bool AllowDamageItemProbing { get; set; } = false;

    [Category("Items & SP Recovery")]
    [DisplayName("Minimum Item Reserve Stock")]
    [Description("Minimum quantity of each consumable stack to keep untouched in inventory before the AI is allowed to use it (0 allows using down to the last item).")]
    [DefaultValue(0)]
    public int MinConsumableReserveCount { get; set; } = 0;

    [Category("Act Freely Optimization")]
    [DisplayName("Smart Selective Ailment Curing")]
    [Description("In Act Freely, only spends a turn curing ailments when it prevents imminent death (e.g. Poison lethal tick, Charm/Confuse sabotage, MC AOA block) or unlocks a higher-output carry turn right before they act.")]
    [DefaultValue(true)]
    public bool ActFreelySmartAilmentCure { get; set; } = true;

    [Category("Act Freely Optimization")]
    [DisplayName("Prioritize Least-Turns Immediate Victory")]
    [Description("When Act Freely can finish the entire fight on the current turn (kills or All-Out Attack), take the guaranteed finish immediately rather than reviving or healing first.")]
    [DefaultValue(true)]
    public bool PreferLeastTurnsWin { get; set; } = true;

    [Category("Diagnostics")]
    [DisplayName("Debug Logging")]
    [Description("Logs AI decision breakdowns, expected damage comparisons, 1-More lookahead lines, and blocked-element skips to the Reloaded-II console.")]
    [DefaultValue(true)]
    public bool DebugLogging { get; set; } = true;
}
