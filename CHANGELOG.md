# Changelog

## 1.1.0 (2026-11-20)
- **Performance & Native Caching**: Added static `SKILL.TBL` caching and per-turn snapshot caching in `NativeBattleBridge` (native `CalcSkillDamage`, affinities, skill usability/costs, attack element, consumables, and `EFFECT.TBL` stage multipliers), eliminated duplicate `TeamOffenseContext`, `EvaluateChargeAction`, and `AnalyzeTelegraphedThreats` passes per turn, removed reflection/boxing in `GetAttackElement`, and skipped empty `0` Persona skill slots across all evaluator loops.
- **Bug Fixes**:
  - Fixed `EvaluateBreakFallback` targeting `livingEnemies[0]` instead of the actual enemy that resists/nulls the Break element and lacks an active Break.
  - Fixed heuristic `Rebellion` targeting counting empty Persona skill slots (`0`) as Physical skills.
  - Fixed `StateFlags` bitmasks against `SKILL.TBL` / `P4G.exe` (`0x02` Rage, `0x04` Fear, `0x08` Silence, `0x10` Confuse, `0x20` Poison, `0x40` Exhaust, `0x80` Enervate), `Mod.cs` loss-of-control bypass (`0x80196`), and added verified cure skill names (`Re Patra`, `Posumudi`, `Mutudi`, `Enradi`, `Tirundi`, `Nervundi`, `Energy Shower`).
  - Fixed shifted boss unit IDs (`UNIT.TBL` / `ENCOUNT.TBL`) and boss guard-lock edge cases (`Ameno-sagiri` `LastSkillId`, `WaitMessageId` gating, Shadow Kanji Phase-100 `358`/`359` gating).
- **Turn-Order Reviving & Anti-Wipe Survival Triage**: Captures `RawRoundTurnOrder` (`BattleManager + 0x398`) and sorts revive targets by turn window / descending Agility (e.g., reviving Yukiko before Chie when Yosuke acts), filters telegraph/charge-weak revives, prioritizes critical self/Protagonist survival healing before partial-HP revives, stabilizes low-HP survivors before chaining `50%`-HP boss revives, and hardens Charged-boss survival guards.
- **Team Value-of-Setup (VoS), Buff Durations & Enemy Ailments**: Reads live `EFFECT.TBL` stage multipliers (`0x140ec09c0`) and 4-bit remaining buff/debuff turn durations (`Combatant + 0x25`) for proactive refreshes (`Matarukaja`, `Rakunda`, `Marakunda`, `Debilitate`, `Dragon Hustle`, `Rebellion`, `Revolution`), Charge deferral, and `AilmentEvaluator` (`Tentarafoo`, `Evil Smile`, `Valiant Dance`, `Ghastly Wail`).
- **Smart Element Probing & Consumables**: Decoupled elemental weakness probing from boss Hama/Mudo immunity so party members probe Shadow Kanji's guards (`272`/`273`) and tough regular shadows with `Bufu`/`Mabufu`/`Agi`/`Zio`/`Garu` while keeping `Bufu` ahead of `Mabufu` on single targets; added hybrid HP+Cure consumable support (`#795..#797`), fixed `ActFreely` ailment-cure opportunity-cost scoring, and expanded `ActFreelyRoutePlanner`.
- **Tests**: Expanded offline xUnit suite from 30 to 61 passing tests (`0` warnings, `0` errors).

## 1.0.0 (2026-11-16)
- Initial release for Persona 4 Golden (PC 64-bit, Reloaded-II).
- Replaced vanilla party AI tactics (`Full Assault`, `Conserve SP`, `Heal/Support`, `Act Freely`) with deterministic `SKILL.TBL` damage & multi-hit evaluation (`0x1400d1910` + midpoint RNG stabilization via `0x140520960` during simulation).
- Added 2-step `1 More` lookahead, AoE group damage summation, single-target SP-efficiency & lethal SP conservation (prevents AoE/spell waste over `Bufu` or stronger Normal Attacks), elemental weapon affinity lookup, and strict Wall/Guard/Tetrakarn/Makarakarn/Shield of Justice avoidance.
- Added shared-inventory SP recovery item support (`Soul Drop`, `Snuff Soul`, `Chewing Soul`, etc., with `Soma`/`Soul Food` protected by default) when party/actor SP runs low.
- Added configurable `RespectAnalysisState` (fair Analyze-aware mode vs Turn-1 omniscience).
- Added Smart Boss Telegraph & Weakness Guarding (`EnableTelegraphGuard`) across Shadow Chie, Shadow Yukiko (*passionate stare*), Shadow Kanji, Shadow Teddie (*Mind Charge* / *Nihil Hand*), Shadow Mitsuo, Shadow Naoto, Kunino-sagiri, Ameno-sagiri, Kusumi-no-Okami, and generic charged bosses.
- Added SP-recovery item support cleanly inside battle, `Youthful Wind` (`Heal + Marakukaja`) / `Salvation` turn-economy healing, fixed native `GetStatStage` signed debuff decoding (`0x1400d08c0`), excluded `Kamui Miracle` from reliable healing, removed false `1 More` bonuses from `Hama`/`Mudo` instant-kills, cached `itemtbl` SP consumable scanning, and modularized the AI engine under `Ai/`.
- Added comprehensive unit test suite (30 tests) covering native stat-stage/nibble decoding, combat math, barriers, SP conservation, SP item selection, healing/cure triage, buff/debuff maintenance, telegraph guarding, and config persistence.
