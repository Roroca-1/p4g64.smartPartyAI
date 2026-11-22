# Smart Party AI Overhaul (`p4g64.smartPartyAI`)

A native C# [Reloaded-II](https://reloaded-project.github.io/Reloaded-II/) mod for **Persona 4 Golden (PC 64-bit)** that replaces the vanilla party AI tactics engine with deterministic `SKILL.TBL` combat math, 2-step `1 More` lookahead, team Value-of-Setup buff/debuff evaluation, turn-order reviving & anti-wipe triage, smart element probing, ailment curing, and boss telegraph guarding.

[![Buy Me a Coffee](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://buymeacoffee.com/kanasara)

## What It Changes

### Core Combat Evaluation
- **True `SKILL.TBL` Damage & Multi-Hit Math**: Evaluates skills using the game's actual deterministic single-hit formula (`0x1400d1910`) multiplied by expected hit count `(min_hits + max_hits) / 2.0`, live `EFFECT.TBL` buff/debuff multipliers (`0x140ec09c0`), buff turn durations (`Combatant + 0x25`), Charges, and Boost/Amp passives—with static `SKILL.TBL` and per-turn snapshot caching for low frame overhead.
- **2-Step `1 More` Lookahead**: Compares direct single-turn damage against hitting an active weakness first (`Skill_1`) → knocking down → following up on the `1 More` (`Skill_2`, plus All-Out Attack opportunities).
- **Team Value-of-Setup (Buffs, Debuffs & Crit)**: Projects multi-turn team damage against kill horizon and Charge momentum so `Matarukaja`, `Rakunda`, `Marakunda`, `Debilitate`, `Dragon Hustle`, `Rebellion`, and `Revolution` are cast only when net team gain beats attacking immediately.
- **Turn-Order Reviving & Anti-Wipe Survival Triage**: Reads `BattleManager + 0x398` turn order and Agility so downed allies acting sooner are revived first, skips telegraph/charge-weak revives, prioritizes critical self/Protagonist healing over suicidal revives, and stabilizes wounded survivors before chaining partial-HP boss revives.
- **Smart Element Probing & Barrier Avoidance**: Strictly respects `Red/Blue/Green/White Wall`, `Guard`, `Tetrakarn`, `Makarakarn`, `Shield of Justice`, and Null/Repel/Drain. Under `RespectAnalysisState = true`, probes unrevealed elemental weaknesses on regular shadows and boss adds (such as Shadow Kanji's guards) while skipping standalone major bosses.
- **Smart Boss Telegraph & Weakness Guarding**: Monitors verified boss unit IDs, telegraph wait messages, `LastSkillId`, `Mind Charge` / `Power Charge`, and battle bitflags to automatically **Guard** (or cover exposed allies with Elemental Walls / `Makarakarn`) unless an immediate battle finish is available.
- **Smart Consumables & Ailment Curing**: Supports SP items, healing items, hybrid HP+Cure items, revive items, and elemental damage items with configurable rarity/reserve protection, verified `StateFlags` bitmasks, and selective `Act Freely` ailment curing.

### Tactics Presets
1. **Full Assault**: Maximum expected damage, 2-step `1 More` chains, All-Out Attack setups, `Power/Mind Charge` math, high-ROI offensive setups (`AllowOffensiveBuffsInFullAssault`), damage-amplifying ailments, and self-defense guarding against telegraphed wipes when not finishing the fight.
2. **Conserve SP**: Prioritizes 0-SP Normal Attacks and safe HP-cost Physical skills (`>= 45%` HP), emergency cheap recovery (`<= 8 SP` / common items), high-ROI cheap setups on long fights, cheap `1 More` / All-Out Attack starters, optional cheap unanalyzed probing, and `Spirit Drain` / SP items below threshold.
3. **Heal / Support**: Percentage-based HP healing (`< 60%` default, including `Youthful Wind` and `Salvation` turn economy), turn-order reviving, ailment curing, `Dekaja`/`Dekunda`, boss buffs/debuffs (`Matarukaja`, `Debilitate`, `Rakunda`, `Heat Riser`, `Shield of Justice`), Elemental Walls, disabling ailments, and smart `1 More` offense when healthy.
4. **Act Freely**: Turn-order-aware least-turns route planner combining immediate finishes, telegraph/Charge survival guards, critical survival healing, anti-wipe reviving, selective lifesaving/carry ailment curing, team setups, and Full Assault offense.

## Configuration (Reloaded-II Mod Config)

All options can be configured directly inside the Reloaded-II launcher under **Configure Mod**:
- **Affinity & Knowledge**: `RespectAnalysisState` (`true`), `ProbeUnrevealedElements` (`true`), `EnableTelegraphGuard` (`true`).
- **Tactics Presets**: Individual toggles for Smart Full Assault, Conserve SP, Heal/Support, Act Freely, plus `AllowOffensiveBuffsInFullAssault` (`true`) and `ConserveSpProbeUnrevealed` (`true`).
- **Thresholds**: `HealSupportHpThreshold` (`60%`), `ActFreelyEmergencyHpThreshold` (`40%`), `ConserveSpSafeHpPercent` (`45%`), `ConserveSpLowCostThreshold` (`8 SP`).
- **Items & SP Recovery**: `EnableSpRecoveryItems` (`true`), `SpRecoveryItemThresholdPercent` (`20%`), `AllowAllySpItemSupport` (`true`), `AllowRareFullSpItems` (`false`), `EnableDamageItemsInAi` (`true`), `EnableSupportItemsInAi` (`true`), `AllowRareConsumables` (`false`), `AllowDamageItemProbing` (`false`), `MinConsumableReserveCount` (`0`).
- **Act Freely Optimization**: `ActFreelySmartAilmentCure` (`true`), `PreferLeastTurnsWin` (`true`).
- **Diagnostics**: `DebugLogging` (`true`).

## Installation

### Option A: GameBanana 1-Click Install
Click the **Reloaded-II 1-Click Install** button on [GameBanana](https://gamebanana.com/mods/741767). Reloaded-II will automatically download the mod and its `reloaded.sharedlib.hooks` dependency.

### Option B: Manual Install (GitHub / Archive)
1. Install [**Reloaded.SharedLib.Hooks**](https://github.com/Sewer56/Reloaded.SharedLib.Hooks.ReloadedII) if not already installed (or let Reloaded-II auto-download dependencies).
2. Download `p4g64.smartPartyAI1.1.1.zip` (or `.7z`) from [GitHub Releases](https://github.com/Roroca-1/p4g64.smartPartyAI/releases) and drag it into the Reloaded-II launcher (or extract into `<Reloaded-II>/Mods/p4g64.smartPartyAI/`).
3. Enable **Smart Party AI Overhaul** for **Persona 4 Golden** (`p4g.exe`) and launch through Reloaded-II.

## Credits & AI Assistance Disclosure

- **Project Lead, Design & Live Testing**:  `Roroca-1` — tactical design direction, preset architecture decisions, live in-game P4G battle testing, log analysis, and verification.
- **AI Coding Assistants**: C# implementation, refactoring, and automated unit tests were heavily assisted by [Google Gemini](https://gemini.google.com/) (via Google Antigravity) and [OpenAI Codex](https://openai.com/codex/).

## Build & Tests

```bash
dotnet test Tests/p4g64.smartPartyAI.Tests.csproj
dotnet build p4g64.smartPartyAI.csproj -c Release
```
