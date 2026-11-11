namespace D3Casc;

/// <summary>
/// SNO group ids, folder names and file extensions, copied from the emulator
/// (D3-GameServer/Core/Types/SNO/SNOGroup.cs and Core/MPQ/FileExtensions.cs) so that the paths this
/// tool builds (<c>Base\Actor\Barbarian_Male.acr</c>) match the paths in the 2.7.4 MPQ pack
/// (<c>Actor\Barbarian_Male.acr</c>). Groups 67-69 have no emulator extension; those come from CASCLib.
/// </summary>
public static class SnoGroups
{
    public readonly record struct Info(int Id, string Name, string Ext);

    public static readonly Info[] Table =
    {
        new(1, "Actor", ".acr"), new(2, "Adventure", ".adv"), new(3, "AiBehavior", ".aib"), new(4, "AiState", ".ais"),
        new(5, "AmbientSound", ".ams"), new(6, "Anim", ".ani"), new(7, "Animation2D", ".an2"), new(8, "AnimSet", ".ans"),
        new(9, "Appearance", ".app"), new(10, "Hero", ".hro"), new(11, "Cloth", ".clt"), new(12, "Conversation", ".cnv"),
        new(13, "ConversationList", ".cnl"), new(14, "EffectGroup", ".efg"), new(15, "Encounter", ".enc"),
        new(17, "Explosion", ".xpl"), new(18, "FlagSet", ".flg"), new(19, "Font", ".fnt"), new(20, "GameBalance", ".gam"),
        new(21, "Globals", ".glo"), new(22, "LevelArea", ".lvl"), new(23, "Light", ".lit"), new(24, "MarkerSet", ".mrk"),
        new(25, "Monster", ".mon"), new(26, "Observer", ".obs"), new(27, "Particle", ".prt"), new(28, "Physics", ".phy"),
        new(29, "Power", ".pow"), new(31, "Quest", ".qst"), new(32, "Rope", ".rop"), new(33, "Scene", ".scn"),
        new(34, "SceneGroup", ".scg"), new(35, "Script", ".scr"), new(36, "ShaderMap", ".shm"), new(37, "Shaders", ".shd"),
        new(38, "Shakes", ".shk"), new(39, "SkillKit", ".skl"), new(40, "Sound", ".snd"), new(41, "SoundBank", ".sbk"),
        new(42, "StringList", ".stl"), new(43, "Surface", ".srf"), new(44, "Textures", ".tex"), new(45, "Trail", ".trl"),
        new(46, "UI", ".ui"), new(47, "Weather", ".wth"), new(48, "Worlds", ".wrl"), new(49, "Recipe", ".rcp"),
        new(51, "Condition", ".cnd"), new(52, "TreasureClass", ".trs"), new(53, "Account", ".acc"), new(54, "Conductor", ".con"),
        new(55, "TimedEvent", ".tme"), new(56, "Act", ".act"), new(57, "Material", ".mat"), new(58, "QuestRange", ".qsr"),
        new(59, "Lore", ".lor"), new(60, "Reverb", ".rev"), new(61, "PhysMesh", ".phm"), new(62, "Music", ".mus"),
        new(63, "Tutorial", ".tut"), new(64, "BossEncounter", ".bos"), new(65, "ControlScheme", ".ctr"), new(66, "Accolade", ".aco"),
        new(67, "AnimTree", ".ant"), new(68, "Vibrations", ""), new(69, "DungeonFinders", ""),
    };

    static readonly Dictionary<int, Info> ById = Table.ToDictionary(t => t.Id);
    static readonly Dictionary<string, Info> ByName = Table.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);

    public static string Name(int id) => ById.TryGetValue(id, out var i) ? i.Name : $"Group{id}";
    public static string Ext(int id) => ById.TryGetValue(id, out var i) ? i.Ext : ".xxx";

    /// <summary>Accepts a group name (case-insensitive) or a numeric id.</summary>
    public static bool TryParse(string text, out int id)
    {
        if (int.TryParse(text, out id)) return true;
        if (ByName.TryGetValue(text, out var i)) { id = i.Id; return true; }
        id = -1;
        return false;
    }
}
