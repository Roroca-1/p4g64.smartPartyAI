using System.Collections.Frozen;

namespace p4g64.smartPartyAI.Core;

/// <summary>
/// Human-readable party/skill names and buff mask helpers verified against P4G SKILL.TBL.
/// </summary>
public static class SkillCatalog
{
    // Verified SKILL.TBL Sec 1 BuffMask (+0x24) bits
    public const uint BuffTarukaja = 0x00000001;
    public const uint BuffTarunda = 0x00000002;
    public const uint BuffMakakaja = 0x00000004;
    public const uint BuffMakanda = 0x00000008;
    public const uint BuffRakukaja = 0x00000010;
    public const uint BuffRakunda = 0x00000020;
    public const uint BuffSukukaja = 0x00000040;
    public const uint BuffSukunda = 0x00000080;
    public const uint BuffMarakukaja = 0x00000110;
    public const uint BuffHeatRiser = 0x00000155;
    public const uint BuffDebilitate = 0x000002AA;
    public const uint BuffDekaja = 0x00001000;
    public const uint BuffDekunda = 0x00002000;
    public const uint BuffCritRateUp = 0x00004000;
    public const uint BuffPowerCharge = 0x00000400;
    public const uint BuffMindCharge = 0x00000800;
    public const uint BuffTetrakarn = 0x00010000;
    public const uint BuffMakarakarn = 0x00020000;
    public const uint BuffFireBreak = 0x00040000;
    public const uint BuffIceBreak = 0x00080000;
    public const uint BuffWindBreak = 0x00100000;
    public const uint BuffElecBreak = 0x00200000;
    public const uint BuffRedWall = 0x08000000;
    public const uint BuffBlueWall = 0x10000000;
    public const uint BuffGreenWall = 0x20000000;
    public const uint BuffWhiteWall = 0x40000000;
    public const uint BuffShieldOfJustice = 0x80000000;

    public static uint GetWallMaskForElement(ElementId element) => element switch
    {
        ElementId.Fire => BuffRedWall,
        ElementId.Ice => BuffBlueWall,
        ElementId.Wind => BuffGreenWall,
        ElementId.Elec => BuffWhiteWall,
        _ => 0
    };

    public static ElementId? GetBreakElement(uint buffMask) => buffMask switch
    {
        BuffFireBreak => ElementId.Fire,
        BuffIceBreak => ElementId.Ice,
        BuffWindBreak => ElementId.Wind,
        BuffElecBreak => ElementId.Elec,
        _ => null
    };

    public static ElementId? GetWallElement(uint buffMask) => buffMask switch
    {
        BuffRedWall => ElementId.Fire,
        BuffBlueWall => ElementId.Ice,
        BuffGreenWall => ElementId.Wind,
        BuffWhiteWall => ElementId.Elec,
        _ => null
    };

    public static uint GetCuredStateMask(SkillMetadata skill)
    {
        if (skill.TargetsDownOnly)
            return 0x140000u; // Re Patra cures Down (0x100000) / Dizzy (0x40000)

        uint low = skill.AilmentMask & 0xFFu;
        return low == 0xFFu ? 0x1FFu : low;
    }

    public static bool IsKnownBossUnitId(ushort enemyId) =>
        enemyId is 266 or 268 or 271 or 276 or 279 or 282 or 284 or 286;

    /// <summary>
    /// Sign-extends the 8-bit stat stage returned in AL by P4G GetStatStage (0x1400d08c0),
    /// where movzbl zero-extends negative sbyte stages (-1..-7) to 255..249.
    /// </summary>
    public static sbyte DecodeSignedStatStageByte(int rawNativeReturn) =>
        unchecked((sbyte)(byte)rawNativeReturn);

    /// <summary>
    /// Unpacks a P4G 4-bit signed nibble matching 0x1400d08fc (0..7 => positive, 8..15 => nib - 15).
    /// </summary>
    public static sbyte UnpackSignedNibble(byte packedByte, bool highNibble)
    {
        byte nib = highNibble ? (byte)(packedByte >> 4) : (byte)(packedByte & 0x0F);
        return nib <= 7 ? (sbyte)nib : (sbyte)(nib - 15);
    }

    public static string GetPartyMemberName(ushort unitId) => unitId switch
    {
        1 => "Protagonist",
        2 => "Yosuke",
        3 => "Chie",
        4 => "Yukiko",
        5 => "Rise",
        6 => "Kanji",
        7 => "Naoto",
        8 => "Teddie",
        _ => $"Unit#{unitId}"
    };

    private static readonly FrozenDictionary<ushort, string> KnownSkillNames = new Dictionary<ushort, string>
    {
        [0] = "Attack",
        [1] = "Agi",
        [2] = "Agilao",
        [3] = "Agidyne",
        [4] = "Maragi",
        [5] = "Maragion",
        [6] = "Maragidyne",
        [7] = "Ragnarok",
        [8] = "Inferno",
        [9] = "Prominence",
        [10] = "Life Drain",
        [14] = "Garu",
        [15] = "Garula",
        [16] = "Garudyne",
        [17] = "Magaru",
        [18] = "Magarula",
        [19] = "Magarudyne",
        [20] = "Panta Rhei",
        [26] = "Bufu",
        [27] = "Bufula",
        [28] = "Bufudyne",
        [29] = "Mabufu",
        [30] = "Mabufula",
        [31] = "Mabufudyne",
        [32] = "Niflheim",
        [39] = "Zio",
        [40] = "Zionga",
        [41] = "Ziodyne",
        [42] = "Mazio",
        [43] = "Mazionga",
        [44] = "Maziodyne",
        [45] = "Thunder Reign",
        [48] = "Spirit Drain",
        [51] = "Megido",
        [52] = "Megidola",
        [53] = "Megidolaon",
        [55] = "Black Viper",
        [56] = "Morning Star",
        [61] = "Hama",
        [62] = "Hamaon",
        [63] = "Mahama",
        [64] = "Mahamaon",
        [65] = "God's Judgement",
        [66] = "Divine Judgement",
        [67] = "Samsara",
        [72] = "Mudo",
        [73] = "Mudoon",
        [74] = "Mamudo",
        [75] = "Mamudoon",
        [76] = "Demonic Decree",
        [78] = "Die for Me!",
        [120] = "Bash",
        [121] = "Cleave",
        [122] = "Power Slash",
        [123] = "Assault Dive",
        [124] = "Sonic Punch",
        [125] = "Double Fangs",
        [126] = "Kill Rush",
        [127] = "Twin Slash",
        [128] = "Fatal End",
        [129] = "Mighty Swing",
        [130] = "Torrent Shot",
        [131] = "Heat Wave",
        [132] = "Brave Blade",
        [133] = "Blade of Fury",
        [134] = "Vorpal Blade",
        [135] = "Gale Slash",
        [136] = "Gigantic Fist",
        [138] = "God's Hand",
        [139] = "Agneyastra",
        [140] = "Primal Force",
        [141] = "Pralaya",
        [168] = "Tempest Slash",
        [169] = "Myriad Arrows",
        [170] = "Hassou Tobi",
        [171] = "Rampage",
        [172] = "Swift Strike",
        [173] = "Vicious Strike",
        [174] = "Single Shot",
        [192] = "Dia",
        [193] = "Diarama",
        [194] = "Diarahan",
        [195] = "Media",
        [196] = "Mediarama",
        [197] = "Mediarahan",
        [198] = "Salvation",
        [199] = "Patra",
        [200] = "Me Patra",
        [208] = "Amrita",
        [209] = "Recarm",
        [210] = "Samarecarm",
        [211] = "Tarunda",
        [212] = "Matarunda",
        [213] = "Rakunda",
        [214] = "Marakunda",
        [215] = "Sukunda",
        [216] = "Masukunda",
        [217] = "Dekunda",
        [218] = "Tarukaja",
        [219] = "Matarukaja",
        [220] = "Rakukaja",
        [221] = "Marakukaja",
        [222] = "Sukukaja",
        [223] = "Masukukaja",
        [224] = "Dekaja",
        [225] = "Heat Riser",
        [226] = "Debilitate",
        [227] = "Power Charge",
        [228] = "Mind Charge",
        [229] = "Tetrakarn",
        [230] = "Makarakarn",
        [234] = "Rebellion",
        [235] = "Revolution",
        [236] = "Fire Break",
        [237] = "Ice Break",
        [238] = "Wind Break",
        [239] = "Elec Break",
        [240] = "Red Wall",
        [241] = "Blue Wall",
        [242] = "Green Wall",
        [243] = "White Wall",
        [245] = "Kamui Miracle",
        [247] = "Youthful Wind",
        [248] = "Dragon Hustle",
        [249] = "Burning Petals",
        [252] = "Shield of Justice"
    }.ToFrozenDictionary();

    public static string GetSkillName(ushort skillId) =>
        KnownSkillNames.TryGetValue(skillId, out var name) ? name : $"Skill#{skillId}";
}
