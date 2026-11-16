# Smart Party AI Overhaul (`p4g64.smartPartyAI`)

A native C# [Reloaded-II](https://reloaded-project.github.io/Reloaded-II/) mod for **Persona 4 Golden (PC 64-bit)** that replaces the vanilla party AI tactics engine with data-driven combat math, 2-step `1 More` lookahead, AoE group optimization, strict barrier/wall avoidance, and automatic boss telegraph & weakness guarding.

[![Buy Me a Coffee](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://buymeacoffee.com/kanasara)

## What It Changes

### Core Combat Evaluation
- **True `SKILL.TBL` Damage & Multi-Hit Math**: Evaluates skills using the game's actual deterministic single-hit formula (`0x1400d1910`) multiplied by expected hit count `(min_hits + max_hits) / 2.0`, AoE group totals across living enemies, active buffs/debuffs, Charges, and Boost/Amp passives—ignoring misleading flavor text.
- **2-Step `1 More` Lookahead**: Compares direct single-turn damage against hitting an active weakness first (`Skill_1`) → knocking down → following up on the `1 More` (`Skill_2`, plus All-Out Attack opportunities). For example, if Fire deals 50 dmg (Weak) and Physical deals 200 dmg, the AI chooses Fire first (`50 + 200 = 250 > 200`) and then unleashes the 200 dmg Physical skill on the `1 More`.
- **Strict Barrier & Negated-Weakness Avoidance**: Avoids elements blocked or negated by `Red/Blue/Green/White Wall`, `Guard`, `Tetrakarn`, `Makarakarn`, `Shield of Justice`, or dynamic Null/Repel/Drain. Rejects AoE spells if any living enemy Repels, Drains, or Karn-reflects that element.
- **Smart Boss Telegraph & Weakness Guarding**: Monitors boss telegraph messages, `LastSkillId`, `Mind Charge` / `Power Charge`, and battle bitflags to automatically **Guard** (or cast a matching Elemental Wall / `Makarakarn` on `Act Freely` & `Heal/Support`) when a boss telegraphs an attack threatening an uncovered weakness (e.g., Chie guarding against Shadow Yukiko's *"passionate stare"* → *Burn to Ashes*) or a fatal ultimate (e.g., Shadow Teddie's *Nihil Hand*), unless a lethal finish is available immediately.
- **Configurable Knowledge Mode**: `RespectAnalysisState` (default `true`) restricts weakness/immunity exploitation to affinities revealed in the Analyze panel while always respecting visible in-battle barriers/walls; toggle `false` for Turn-1 omniscience.

### Tactics Presets
1. **Full Assault**: Pure maximum damage, `1 More` chains, All-Out Attack setups, `Power/Mind Charge` 2-turn math, and self-preservation guarding against telegraphed weakness/ultimate wipes when not finishing the boss.
2. **Conserve SP**: Prioritizes 0-SP Normal Attacks and safe HP-cost Physical skills (`>= 45%` HP), `Spirit Drain` below 35% SP, and low-cost Tier-1 spells only when unlocking an All-Out Attack or high-value `1 More` combo; guards against telegraphed weakness/ultimate attacks and never blindly bashes Phys Null/Repel enemies.
3. **Heal / Support**: Percentage-based Max HP healing (`< 60%` default), revives (`Recarm`/`Samarecarm`), ailment triage, `Dekaja`/`Dekunda`, boss buffs/debuffs (`Matarukaja`, `Debilitate`, `Rakunda`, `Heat Riser`, `Shield of Justice`), Elemental Walls for exposed party weaknesses, telegraph defense, and smart `1 More` offense when healthy.
4. **Act Freely**: Balanced All-Rounder combining telegraphed weakness/ultimate guarding, critical emergency healing (`< 40%`), immediate All-Out Attack priority, reactive ally Wall support, key boss buffs/debuffs, and Full Assault damage logic.

## Configuration (Reloaded-II Mod Config)

All options can be configured directly inside the Reloaded-II launcher under **Configure Mod**:
- **Respect Analysis State** (`true`): Only exploit enemy affinities revealed in the Analyze panel while always honoring visible in-battle barriers/walls/Karns.
- **Smart Telegraph & Weakness Guard** (`true`): Automatically Guard or shield against telegraphed boss weakness hits and ultimate attacks.
- **Enable Smart Full Assault / Conserve SP / Heal / Support / Act Freely** (`true`): Toggle individual tactics presets on or off.
- **Heal/Support HP Threshold (%)** (`60`), **Act Freely Emergency HP Threshold (%)** (`40`), **Conserve SP Safe HP Floor (%)** (`45`), **Conserve SP Low-Cost Starter Max SP** (`8`).
- **Use SP Recovery Items** (`true`), **SP Item Threshold (%)** (`20`), **Allow Ally SP Item Support** (`true`), **Allow Full-SP / Rare Items (Soul Food / Soma)** (`false`): Recover SP cleanly using partial SP consumables from shared inventory without wasting rare full-restores unless enabled.
- **Debug Logging** (`true`): Prints AI damage comparisons and decision breakdowns in the Reloaded-II console.

## Installation

### Option A: GameBanana 1-Click Install
Click the **Reloaded-II 1-Click Install** button on GameBanana. Reloaded-II will automatically download the mod and its `reloaded.sharedlib.hooks` dependency.

### Option B: Manual Install (GitHub / Archive)
1. Install [**Reloaded.SharedLib.Hooks**](https://github.com/Sewer56/Reloaded.SharedLib.Hooks.ReloadedII) if not already installed (or let Reloaded-II auto-download dependencies).
2. Download `p4g64.smartPartyAI1.0.0.zip` (or `.7z`) and drag it into the Reloaded-II launcher (or extract into `<Reloaded-II>/Mods/p4g64.smartPartyAI/`).
3. Enable **Smart Party AI Overhaul** for **Persona 4 Golden** (`p4g.exe`) and launch through Reloaded-II.

## Build & Tests

```bash
dotnet test Tests/p4g64.smartPartyAI.Tests.csproj
dotnet build p4g64.smartPartyAI.csproj -c Release
```
