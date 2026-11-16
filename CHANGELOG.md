# Changelog

## 1.0.0 (2026-11-16)
- Initial release for Persona 4 Golden (PC 64-bit, Reloaded-II).
- Replaced vanilla party AI tactics (`Full Assault`, `Conserve SP`, `Heal/Support`, `Act Freely`) with deterministic `SKILL.TBL` damage & multi-hit evaluation (`0x1400d1910` + midpoint RNG stabilization via `0x140520960` during simulation).
- Added 2-step `1 More` lookahead, AoE group damage summation, single-target SP-efficiency & lethal SP conservation (prevents AoE/spell waste over `Bufu` or stronger Normal Attacks), elemental weapon affinity lookup, and strict Wall/Guard/Tetrakarn/Makarakarn/Shield of Justice avoidance.
- Added shared-inventory SP recovery item support (`Soul Drop`, `Snuff Soul`, `Chewing Soul`, etc., with `Soma`/`Soul Food` protected by default) when party/actor SP runs low.
- Added configurable `RespectAnalysisState` (fair Analyze-aware mode vs Turn-1 omniscience).
- Added Smart Boss Telegraph & Weakness Guarding (`EnableTelegraphGuard`) across Shadow Chie, Shadow Yukiko (*passionate stare*), Shadow Kanji, Shadow Teddie (*Mind Charge* / *Nihil Hand*), Shadow Mitsuo, Shadow Naoto, Kunino-sagiri, Ameno-sagiri, Kusumi-no-Okami, and generic charged bosses.
- Added SP-recovery item support cleanly inside battle, `Youthful Wind` (`Heal + Marakukaja`) / `Salvation` turn-economy healing, fixed native `GetStatStage` signed debuff decoding (`0x1400d08c0`), excluded `Kamui Miracle` from reliable healing, removed false `1 More` bonuses from `Hama`/`Mudo` instant-kills, cached `itemtbl` SP consumable scanning, and modularized the AI engine under `Ai/`.
- Added comprehensive unit test suite (30 tests) covering native stat-stage/nibble decoding, combat math, barriers, SP conservation, SP item selection, healing/cure triage, buff/debuff maintenance, telegraph guarding, and config persistence.
