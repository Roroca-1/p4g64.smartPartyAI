#!/usr/bin/env python3
"""
Builds GitHub and GameBanana Reloaded-II release packages for p4g64.smartPartyAI.
Verifies xxHash64 against Reloaded-II reference DLL hashes and validates archive payloads.
"""

from __future__ import annotations

import hashlib
import json
import os
import shutil
import struct
import subprocess
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

ROOT = Path("/mnt/Data/Dev/p4g64.smartPartyAI")
RELEASE_BIN = ROOT / "bin" / "Release"
RELOADED_MOD_DIR = Path(
    "/mnt/RD/SteamLibrary/steamapps/compatdata/1113000/pfx/drive_c/Reloaded-II/Mods/p4g64.smartPartyAI"
)

MOD_ID = "p4g64.smartPartyAI"
VERSION = json.loads((ROOT / "ModConfig.json").read_text(encoding="utf-8"))["ModVersion"]

# Known-good xxHash64 (seed=0) hashes from Reloaded-II Sewer56.Update.Metadata.json files
REFERENCE_XXH64 = {
    "Reloaded.Hooks.Definitions.dll": 3707225781942902320,
    "Reloaded.Hooks.ReloadedII.Interfaces.dll": 4073583542395463031,
}

PAYLOAD_BINARIES = [
    "p4g64.smartPartyAI.dll",
    "p4g64.smartPartyAI.deps.json",
    "Reloaded.Hooks.Definitions.dll",
    "Reloaded.Hooks.ReloadedII.Interfaces.dll",
]


def xxh64(data: bytes, seed: int = 0) -> int:
    prime1 = 0x9E3779B185EBCA87
    prime2 = 0xC2B2AE3D27D4EB4F
    prime3 = 0x165667B19E3779F9
    prime4 = 0x85EBCA77C2B2AE63
    prime5 = 0x27D4EB2F165667C5
    mask64 = 0xFFFFFFFFFFFFFFFF

    def rotl64(x: int, r: int) -> int:
        return ((x << r) | (x >> (64 - r))) & mask64

    n = len(data)
    p = 0
    if n >= 32:
        v1 = (seed + prime1 + prime2) & mask64
        v2 = (seed + prime2) & mask64
        v3 = seed & mask64
        v4 = (seed - prime1) & mask64
        limit = n - 32
        while p <= limit:
            k1, k2, k3, k4 = struct.unpack_from("<QQQQ", data, p)
            v1 = (rotl64((v1 + k1 * prime2) & mask64, 31) * prime1) & mask64
            v2 = (rotl64((v2 + k2 * prime2) & mask64, 31) * prime1) & mask64
            v3 = (rotl64((v3 + k3 * prime2) & mask64, 31) * prime1) & mask64
            v4 = (rotl64((v4 + k4 * prime2) & mask64, 31) * prime1) & mask64
            p += 32
        h = (rotl64(v1, 1) + rotl64(v2, 7) + rotl64(v3, 12) + rotl64(v4, 18)) & mask64
        for v in (v1, v2, v3, v4):
            v = (rotl64((v * prime2) & mask64, 31) * prime1) & mask64
            h ^= v
            h = (h * prime1 + prime4) & mask64
    else:
        h = (seed + prime5) & mask64

    h = (h + n) & mask64
    while p + 8 <= n:
        k1 = struct.unpack_from("<Q", data, p)[0]
        k1 = (rotl64((k1 * prime2) & mask64, 31) * prime1) & mask64
        h ^= k1
        h = (rotl64(h, 27) * prime1 + prime4) & mask64
        p += 8
    while p + 4 <= n:
        k1 = struct.unpack_from("<I", data, p)[0]
        h ^= (k1 * prime1) & mask64
        h = (rotl64(h, 23) * prime2 + prime3) & mask64
        p += 4
    while p < n:
        h ^= (data[p] * prime5) & mask64
        h = (rotl64(h, 11) * prime1) & mask64
        p += 1

    h ^= h >> 33
    h = (h * prime2) & mask64
    h ^= h >> 29
    h = (h * prime3) & mask64
    h ^= h >> 32
    return h


def load_font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = [
        "/usr/share/fonts/TTF/DejaVuSans-Bold.ttf" if bold else "/usr/share/fonts/TTF/DejaVuSans.ttf",
        "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf" if bold else "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
        "/usr/share/fonts/liberation/LiberationSans-Bold.ttf" if bold else "/usr/share/fonts/liberation/LiberationSans-Regular.ttf",
        "/usr/share/fonts/noto/NotoSans-Bold.ttf" if bold else "/usr/share/fonts/noto/NotoSans-Regular.ttf",
    ]
    for path in candidates:
        if os.path.exists(path):
            return ImageFont.truetype(path, size)
    return ImageFont.load_default()


def generate_preview_png(out_path: Path) -> None:
    size = 512
    img = Image.new("RGBA", (size, size), (16, 19, 26, 255))
    draw = ImageDraw.Draw(img)

    # Diagonal Persona 4 Golden yellow & charcoal TV bars in the background
    yellow = (255, 224, 24, 255)
    amber = (245, 178, 12, 255)
    slate = (28, 33, 46, 255)
    dark = (14, 17, 24, 255)
    white = (248, 250, 252, 255)
    red_badge = (224, 52, 52, 255)

    draw.polygon([(0, 0), (size, 0), (size, 150), (0, 210)], fill=slate)
    draw.polygon([(0, 405), (size, 345), (size, size), (0, size)], fill=yellow)
    draw.polygon([(0, 392), (size, 332), (size, 352), (0, 412)], fill=amber)

    # Subtle outer frame + inner CRT border
    draw.rectangle([16, 16, size - 16, size - 16], outline=yellow, width=6)
    draw.rectangle([28, 28, size - 28, size - 28], outline=(70, 80, 102, 255), width=2)

    # Top "P4G64 // RELOADED-II" pill & "1 MORE!" badge sized via actual textbbox
    f_small = load_font(18, bold=True)
    f_badge = load_font(21, bold=True)
    f_title = load_font(58, bold=True)
    f_chip = load_font(17, bold=True)
    f_bot = load_font(22, bold=True)
    f_sub = load_font(16, bold=True)

    tag_text = "P4G64 // RELOADED-II"
    tb = draw.textbbox((0, 0), tag_text, font=f_small)
    tag_w = (tb[2] - tb[0]) + 28
    draw.rounded_rectangle([40, 44, 40 + tag_w, 84], radius=8, fill=yellow)
    draw.text((54, 53), tag_text, font=f_small, fill=dark)

    badge_text = "1 MORE!"
    bb = draw.textbbox((0, 0), badge_text, font=f_badge)
    badge_w = (bb[2] - bb[0]) + 28
    badge_x0 = size - 40 - badge_w
    draw.rounded_rectangle([badge_x0, 44, size - 40, 84], radius=9, fill=red_badge)
    draw.text((badge_x0 + 14, 52), badge_text, font=f_badge, fill=white)

    # Main title
    draw.text((42, 128), "SMART", font=f_title, fill=yellow)
    draw.text((42, 194), "PARTY AI", font=f_title, fill=white)

    # Feature chips
    chips = [
        "1-More Lookahead",
        "SKILL.TBL Math",
        "Wall & Karn Guard",
        "Boss Telegraphs",
    ]
    y = 282
    x = 42
    for chip in chips:
        bbox = draw.textbbox((0, 0), chip, font=f_chip)
        tw = bbox[2] - bbox[0]
        cw = tw + 24
        if x + cw > size - 40:
            x = 42
            y += 38
        draw.rounded_rectangle([x, y, x + cw, y + 30], radius=6, fill=(36, 44, 62, 255), outline=amber, width=2)
        draw.text((x + 12, y + 6), chip, font=f_chip, fill=white)
        x += cw + 12

    # Bottom yellow banner text (safely inside inner border [28..484])
    draw.text((42, 412), f"v{VERSION}  •  All 4 Tactics Overhauled", font=f_bot, fill=dark)
    draw.text((42, 446), "Full Assault | Conserve SP | Heal | Act Freely", font=f_sub, fill=(35, 38, 48, 255))

    img.convert("RGB").quantize(
        colors=64,
        method=Image.Quantize.MEDIANCUT,
        dither=Image.Dither.NONE,
    ).save(out_path, "PNG", optimize=True)


def verify_xxhash_reference() -> None:
    for fname, expected in REFERENCE_XXH64.items():
        fpath = RELEASE_BIN / fname
        got = xxh64(fpath.read_bytes())
        if got != expected:
            raise RuntimeError(f"xxHash64 verification failed for {fname}: got {got}, expected {expected}")
        print(f"[verify] xxHash64 OK: {fname} -> {got}")


def compute_file_hashes(payload_dir: Path) -> list[dict]:
    entries = []
    for path in sorted(payload_dir.rglob("*")):
        if not path.is_file():
            continue
        rel = str(path.relative_to(payload_dir)).replace("/", "\\")
        if rel == "Sewer56.Update.Metadata.json":
            continue
        entries.append({"RelativePath": rel, "Hash": xxh64(path.read_bytes())})
    return entries


def make_zip(src_dir: Path, out_zip: Path) -> None:
    if out_zip.exists():
        out_zip.unlink()
    subprocess.run(["zip", "-r", "-9", str(out_zip), "."], cwd=src_dir, check=True)


def make_7z(src_dir: Path, out_7z: Path) -> None:
    if out_7z.exists():
        out_7z.unlink()
    subprocess.run(["7z", "a", "-t7z", "-mx=9", str(out_7z), "."], cwd=src_dir, check=True)


def copy_verified(src: Path, dst: Path) -> None:
    dst.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(src, dst)
    if src.read_bytes() != dst.read_bytes():
        raise IOError(f"Copy mismatch between {src} and {dst}")
    digest = hashlib.md5(dst.read_bytes()).hexdigest()
    print(f"[sync] {dst} (md5={digest})")


def main() -> None:
    verify_xxhash_reference()

    preview_root = ROOT / "Preview.png"
    generate_preview_png(preview_root)
    print(f"[icon] Generated {preview_root} ({preview_root.stat().st_size} bytes)")

    releases_dir = ROOT / "releases"
    staging_dir = releases_dir / "_staging"
    github_dir = releases_dir / "github"
    gb_dir = releases_dir / "gamebanana"

    for d in (staging_dir, github_dir, gb_dir):
        if d.exists():
            shutil.rmtree(d)
        d.mkdir(parents=True, exist_ok=True)

    # 1. Stage clean mod files
    copy_verified(ROOT / "ModConfig.json", staging_dir / "ModConfig.json")
    copy_verified(preview_root, staging_dir / "Preview.png")
    for fname in PAYLOAD_BINARIES:
        copy_verified(RELEASE_BIN / fname, staging_dir / fname)

    # 2. Generate Sewer56.Update.Metadata.json
    mod_config = json.loads((ROOT / "ModConfig.json").read_text(encoding="utf-8"))
    readme_text = (ROOT / "README.md").read_text(encoding="utf-8")
    changelog_text = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")

    update_meta = {
        "ExtraData": None,
        "Type": 0,
        "Version": VERSION,
        "Hashes": {"Files": compute_file_hashes(staging_dir)},
        "IgnoreRegexes": [
            ".*\\.json",
            ".*\\.nuspec",
            f"{MOD_ID.replace('.', '\\.')}\\.ReleaseMetadata\\.json",
        ],
        "IncludeRegexes": [
            "ModConfig\\.json",
            "\\.deps\\.json",
            "\\.runtimeconfig\\.json",
        ],
        "DeltaData": None,
    }
    (staging_dir / "Sewer56.Update.Metadata.json").write_text(
        json.dumps(update_meta, separators=(",", ":")), encoding="utf-8"
    )

    # 3. Build GitHub release artifacts
    gh_zip_name = f"{MOD_ID}{VERSION}.zip"
    gh_7z_name = f"{MOD_ID}{VERSION}.7z"
    make_zip(staging_dir, github_dir / gh_zip_name)
    copy_verified(github_dir / gh_zip_name, github_dir / "Mod.zip")
    make_7z(staging_dir, github_dir / gh_7z_name)

    gh_release_meta = {
        "Releases": [
            {
                "ReleaseType": 0,
                "FileName": gh_zip_name,
                "Version": VERSION,
                "Delta": None,
            }
        ],
        "ExtraData": {
            "ModId": MOD_ID,
            "ModName": mod_config["ModName"],
            "ModDescription": mod_config["ModDescription"],
            "Changelog": changelog_text,
            "Readme": readme_text,
        },
    }
    (github_dir / f"{MOD_ID}.ReleaseMetadata.json").write_text(
        json.dumps(gh_release_meta, indent=2), encoding="utf-8"
    )

    (github_dir / "GITHUB_RELEASE_NOTES.md").write_text(
        f"""# Smart Party AI Overhaul (`{MOD_ID}`) v{VERSION}

[![Buy Me a Coffee](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://buymeacoffee.com/kanasara)

A native C# Reloaded-II overhaul for **Persona 4 Golden (PC 64-bit)** that replaces the vanilla party member AI tactics engine with deterministic `SKILL.TBL` combat math, 2-step `1 More` lookahead, team Value-of-Setup buffs/debuffs, turn-order reviving & anti-wipe triage, smart element probing, ailment curing, and boss telegraph guarding.

## What's New in v{VERSION}
- **Fixed Per-Ailment Affinity Slot & Out-of-Bounds Reveal Bug**: Resolves each ailment skill's true `UNIT.TBL` / `SKILL.TBL` slot (`8=Confuse` .. `15=Silence`) instead of hardcoding slot `12`, and removes out-of-bounds `IsAffinityRevealed(..., >= 8)` calls (verified at `P4G.exe` `0x0A8AD0`). Fixes party members spamming blocked ailments (e.g. `Tentarafoo`) into Golden Hands.
- **Fixed `Hama`/`Mudo` Instant-Kill Valuation**: Uses `SKILL.TBL` `BaseAilmentChance` (`Mudo = 25%`, `Mudoon = 60%`, etc.), zeros out known `Resist` targets, and keeps unrevealed Light/Dark casts conservative so low-odds `Mudo` doesn't spam over reliable attacks on unanalyzed high-HP/resistant targets like `Treasure Hand`.
- **Fixed Multi-Turn Boss Telegraph Guarding (`battle/enemy.bf` audit)**:
  - **Shadow Teddie (`#261`)**: No longer guards for 3 rounds straight during `Nullity Guidance` (`364`) and `Ultra Charge` (`363` / `bit 448`); guards only on the final `Wait 169` / `bit 458` telegraph before `Nihil Hand` (while normal `Mind Charge` still protects Ice weaknesses).
  - **Ameno-sagiri (`#266`)**: Suppresses premature Mind-Charge guards during Bewildering Fog buff turns (`bit 451 && !bit 455`) and triggers mandatory `MustGuardUltimate` on `bit 455` right before `Nebula Oculus`.
  - **Shadow Naoto (`#263`)**: Checks completion bits `456..459` alongside `452..455` so allies don't stay stuck Guarding after Mute Ray's follow-up already fired.
- **Verified**: `69/69` xUnit tests passing, `0` warnings / `0` errors on Release build.

## AI Assistance Disclosure
- **AI Coding Assistant**: C# coding, refactoring, and automated unit tests were heavily assisted by **Gemini 4 Neon** ([Google Gemini](https://gemini.google.com/) via Google Antigravity).

## Assets
- `{gh_zip_name}` (primary Reloaded-II release asset matching `ModConfig.json`)
- `Mod.zip` (compatibility alias)
- `{gh_7z_name}` (smaller 7z download)
- `{MOD_ID}.ReleaseMetadata.json` (enables Reloaded-II auto-updates via GitHub)
""",
        encoding="utf-8",
    )

    # 4. Build GameBanana release artifacts
    gb_7z_name = f"{MOD_ID}{VERSION}.7z"
    gb_zip_name = f"{MOD_ID}{VERSION}.zip"
    copy_verified(github_dir / gb_7z_name, gb_dir / gb_7z_name)
    copy_verified(github_dir / gh_zip_name, gb_dir / gb_zip_name)
    copy_verified(preview_root, gb_dir / "Preview.png")

    gb_meta_staging = releases_dir / "_gb_meta_staging"
    if gb_meta_staging.exists():
        shutil.rmtree(gb_meta_staging)
    gb_meta_staging.mkdir(parents=True)

    gb_release_meta = {
        "Releases": [
            {
                "ReleaseType": 0,
                "FileName": gb_7z_name,
                "Version": VERSION,
                "Delta": None,
            }
        ],
        "ExtraData": {
            "ModId": MOD_ID,
            "ModName": mod_config["ModName"],
            "ModDescription": mod_config["ModDescription"],
            "Changelog": changelog_text,
            "Readme": readme_text,
        },
    }
    (gb_meta_staging / f"{MOD_ID}.ReleaseMetadata.json").write_text(
        json.dumps(gb_release_meta, indent=2), encoding="utf-8"
    )
    (gb_meta_staging / ".disable_gb1click").write_text("", encoding="utf-8")

    make_7z(gb_meta_staging, gb_dir / f"{MOD_ID}.ReleaseMetadata.json.7z")
    shutil.rmtree(gb_meta_staging)

    (gb_dir / "GAMEBANANA_SUBMISSION.md").write_text(
        f"""# GameBanana Mod Submission Copy — Smart Party AI Overhaul

**Mod Name:** Smart Party AI Overhaul  
**Game:** Persona 4 Golden (PC)  
**Category:** Gameplay / AI Overhaul (Reloaded-II)  
**Dependency:** `reloaded.sharedlib.hooks` (auto-installed by Reloaded-II)  
**Source Code:** https://github.com/Roroca-1/p4g64.smartPartyAI

[![Buy Me a Coffee](https://www.buymeacoffee.com/assets/img/custom_images/orange_img.png)](https://buymeacoffee.com/kanasara)

---

## Files to Upload on GameBanana (Important!)

1. **Primary Download (1-Click Install enabled):** `{gb_7z_name}` *(recommended)*  
   - Optional Zip alternative: `{gb_zip_name}`
2. **Reloaded-II Auto-Update Metadata (Do NOT rename):** `{MOD_ID}.ReleaseMetadata.json.7z`  
   - Contains `{MOD_ID}.ReleaseMetadata.json` and `.disable_gb1click` so GameBanana shows the Reloaded-II 1-Click Install button on the main mod archive instead of the metadata file.
3. **Thumbnail / Mod Icon:** `Preview.png` (512x512)

> **Optional after publishing on GameBanana:** Copy the numeric Mod ID from your GameBanana URL (`https://gamebanana.com/mods/<ID>`), add `"GameBanana": {{ "ItemType": "Mod", "ItemId": <ID> }}` under `PluginData` in `ModConfig.json`, set `"ProjectUrl"`, and re-run `scripts/package_releases.py` if you want Reloaded-II's GameBanana auto-updater linked directly to the submission.

---

## Ready-to-Paste GameBanana Description

**Smart Party AI Overhaul** completely replaces Persona 4 Golden's vanilla AI tactics engine with deterministic `SKILL.TBL` combat math, 2-step `1 More` lookahead, AoE group evaluation, strict barrier/wall awareness, and boss telegraph guarding.

### Key Features
- **Real Attack Power & Multi-Hit Math:** Evaluates skills using the game's actual damage calculator (`SKILL.TBL` + active Attack/Magic stages + Charges + Boost/Amp passives) and `(min_hits + max_hits) / 2.0`, ignoring misleading in-game flavor text.
- **2-Step `1 More` Lookahead:** Compares direct damage against knocking down a standing weak enemy first and following up on the `1 More` (e.g., **Fire Weak 50 dmg → 1 More → Phys 200 dmg = 250 total > 200 direct**), plus All-Out Attack opportunities.
- **Strict Negated-Weakness & Barrier Avoidance:** Never wastes turns or kills your team on `Red/Blue/Green/White Wall`, `Guard`, `Tetrakarn`, `Makarakarn`, `Shield of Justice`, or dynamic Null/Repel/Drain affinities. Rejects AoE spells if any living enemy Repels/Drains/reflects.
- **Smart Boss Telegraph & Weakness Guarding:** Automatically **Guards** (or covers allies with Elemental Walls / `Makarakarn` on `Act Freely` & `Heal/Support`) when bosses telegraph an incoming weakness hit—like Chie blocking when *"Shadow Yukiko has a passionate stare"*—or fatal ultimates like Shadow Teddie's *Nihil Hand*.
- **Respects Analyze State (`RespectAnalysisState`):** Fair by default (only exploits weaknesses/immunities you've discovered while always honoring visible in-battle walls/shields), or toggle `false` in Mod Config for Turn-1 omniscience.

### Overhauled Tactics
- **Full Assault:** Maximum expected damage, 2-step `1 More` combos, All-Out Attack priority, `Power/Mind Charge` 2-turn math, and smart telegraph self-defense.
- **Conserve SP:** Zero-SP attacks and safe HP-cost Phys skills (`>= 45%` HP), `Spirit Drain` recovery below 35% SP, cheap knockdown starters only when triggering an All-Out Attack or high-value `1 More`, and strict Phys Null/Tetrakarn safety.
- **Heal/Support:** Percentage-based healing triage (`< 60%` HP), revives, ailment cures, `Dekaja`/`Dekunda`, boss buffs/debuffs, Elemental Walls for party weaknesses, and smart `1 More` offense when healthy.
- **Act Freely:** Balanced all-rounder combining telegraphed weakness guarding, critical emergency healing (`< 40%` HP), immediate All-Out Attacks, reactive Wall support, key boss buffs/debuffs, and Full Assault offense.
""",
        encoding="utf-8",
    )

    # 5. Sync live Reloaded-II mod directory cleanly as well
    if RELOADED_MOD_DIR.parent.exists():
        RELOADED_MOD_DIR.mkdir(parents=True, exist_ok=True)
        for src in staging_dir.iterdir():
            if src.is_file():
                copy_verified(src, RELOADED_MOD_DIR / src.name)
        pdb_src = RELEASE_BIN / f"{MOD_ID}.pdb"
        if pdb_src.exists():
            copy_verified(pdb_src, RELOADED_MOD_DIR / pdb_src.name)

    shutil.rmtree(staging_dir)
    print("[done] Packaged GitHub and GameBanana releases cleanly!")


if __name__ == "__main__":
    main()
