using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace D3ModTool;

// ============================================================================================================================
// UI layout MODEL for the visual designer: a .ui file (see UiFile) as typed definitions, the template/style-expanded instance
// tree and the rectangles the 2.8.0.99920 client computes. Everything marked "verified" was read from the exe (RVAs given) or
// checked against the running game; "guessed" comes from statistics over the 258 stock 2.8 layouts only.
// Full write-up: D3Sniff\reports\ui_layout_2.8.0.md.
// ============================================================================================================================

/// <summary>Value kind of a data-element id, from the exe's per-id descriptor table (16-byte {u64 kind, char* default} entries at
/// RVA 0xFE72F0, 284 ids 0..0x11B; verified). Strings live in the record's style array (+1576), Int/Color/Bool in the int array
/// (+1600), Float in the float array (+1624). Styles and properties share ONE id space ("style 140" = id 0x8C).</summary>
public enum UiValueKind { String = 0, Int = 1, Color = 3, Bool = 4, Float = 5 }

/// <summary>How sure a name/meaning is: Verified = read from the exe code or checked in game; Statistics = consistent across all
/// stock layouts (e.g. every value is a "UI:" texture); Guessed = plausible, unconfirmed.</summary>
public enum UiCertainty { Verified, Statistics, Guessed, Unknown }

public sealed record UiPropertyInfo(int Id, UiValueKind Kind, string Default, string Name, string Meaning, UiCertainty Certainty);

/// <summary>Data-element ids (styles and properties) with names. Kind and default of every id come from the exe table
/// (verified); names/meanings carry their own certainty.</summary>
public static class UiProps
{
    // positioning (loader 0x1EC0F0 / SetPosSize 0x1ECE50 / UpdateRect 0x1ED390; all verified)
    public const int TemplateMarker = 0x01, Left = 0x03, Top = 0x04, Right = 0x05, Bottom = 0x06, X = 0x07, Y = 0x08, Width = 0x09, Height = 0x0A,
        MaxWidth = 0x0B, MaxHeight = 0x0C, MinWidth = 0x0D, MinHeight = 0x0E, Alpha = 0x0F,
        Texture = 0x10, TextureHover = 0x11, TexturePressed = 0x12, TextureDisabled = 0x13,
        IconTexture = 0x14, IconHover = 0x15, IconPressed = 0x16, IconDisabled = 0x17, Anim2D = 0x1F, Texture2 = 0x20,
        OnClick = 0x29, OnDoubleClick = 0x2B, DrawHook = 0x2C,
        TextColor = 0x33, TextShadowColor = 0x34, Font = 0x3A, FontSize = 0x3B, TextHAlign = 0x3D, TextVAlign = 0x3E, Text = 0x41,
        OnOpen = 0x60, OnClose = 0x61, OnUpdate = 0x63, OnTooltip = 0x69, TooltipText = 0x6E, BlendShader = 0x8B,
        HAnchor = 0x8C, VAnchor = 0x8D, StartsHidden = 0x97, StackHorizontal = 0x99, StackReverse = 0x9A, StackSpacing = 0x9B,
        DockEdges = 0x9C, NamedStyle = 0x9D, TemplateRef = 0x9E, ContextMenu = 0x9F, StackPadding = 0xA4, StackCenterCross = 0xA6,
        ClickSound = 0xAF, MouseOverSound = 0xB1, OpenSound = 0xB2, CloseSound = 0xB3, ListItemTemplate = 0xBB,
        AutoAlign = 0xD1, Rotation = 0xD2, FlipX = 0xD3, FlipY = 0xD4;

    static readonly (byte Kind, string Default)[] Table =
    {
        (1,""), (0,"0"), (4,"1"), (1,"0"), (1,"0"), (1,"0"), (1,"0"), (1,"0"),
        (1,"0"), (1,"0"), (1,"0"), (1,"-1"), (1,"-1"), (1,"-1"), (1,"-1"), (1,"255"),
        (0,""), (0,""), (0,""), (0,""), (0,""), (0,""), (0,""), (0,""),
        (0,""), (0,""), (0,""), (0,""), (0,""), (0,""), (0,""), (0,"-1"),
        (0,""), (1,"0"), (1,"0"), (1,"0"), (1,"0"), (0,"-1"), (0,"-1"), (0,"-1"),
        (0,"-1"), (0,"-1"), (0,"-1"), (0,"-1"), (0,"-1"), (5,"0"), (5,"0"), (5,"0"),
        (5,"0"), (5,"0"), (5,"0"), (3,"0xFFCD9D51"), (3,"0xFF000000"), (3,"0xFF7F7F7F"), (3,"0xFFFFFFFF"), (3,"0XFFFF691E"),
        (3,"0XFFFFFFFF"), (3,"0XFFFF691E"), (0,"ExocetLight"), (1,"24"), (1,"0"), (1,""), (1,""), (5,"1"),
        (4,"0"), (0,""), (1,"0"), (0,"-1"), (0,"-1"), (1,"-1"), (1,"-1"), (0,"-1"),
        (0,"-1"), (4,"0"), (4,"0"), (4,"0"), (4,"0"), (0,"-1"), (0,"-1"), (5,"0"),
        (4,"0"), (1,"0"), (3,"0XFF888888"), (5,"0"), (5,"0"), (4,"0"), (4,"0"), (0,"-1"),
        (0,"-1"), (0,"-1"), (0,"-1"), (0,""), (0,""), (0,"-1"), (0,"-1"), (0,"-1"),
        (0,"-1"), (0,"-1"), (0,"-1"), (0,"-1"), (0,"-1"), (0,"-1"), (1,"-1"), (4,"0"),
        (1,"-1"), (0,"UI_TIP_NULL"), (1,"0"), (1,"0"), (1,"-1"), (1,"-1"), (0,""), (1,"Center"),
        (0,"-1"), (0,"-1"), (4,"0"), (4,"0"), (1,"-1"), (4,"0"), (1,"-1"), (4,"0"),
        (4,"0"), (4,"0"), (1,"0"), (0,"-1"), (0,"-1"), (0,"-1"), (4,"0"), (4,"1"),
        (0,"-1"), (1,"3"), (1,"3"), (0,"-1"), (0,"-1"), (0,"-1"), (1,"-1"), (1,"5"),
        (0,""), (0,"-1"), (1,"-1"), (0,"-1"), (0,""), (0,""), (4,"0"), (4,"0"),
        (1,"0"), (4,"0"), (0,""), (0,""), (0,""), (0,""), (1,"-1"), (4,"0"),
        (4,"0"), (4,"0"), (4,"0"), (1,"0"), (0,"LT"), (0,""), (0,""), (0,""),
        (0,""), (0,"-1"), (4,"0"), (4,"0"), (0,"0,0,0,0"), (4,"0"), (4,"0"), (4,"0"),
        (4,"0"), (1,"0"), (1,"0"), (4,"0"), (0,"-1"), (0,"-1"), (0,"-1"), (0,"-1"),
        (0,"-1"), (0,"-1"), (0,"-1"), (0,"-1"), (1,"0"), (4,"0"), (4,"0"), (4,"0"),
        (5,"0"), (0,"-1"), (0,"-1"), (0,""), (4,"0"), (4,"0"), (1,"0"), (0,"-1"),
        (1,"0"), (1,"0"), (4,"1"), (0,""), (0,""), (4,"0"), (1,"0"), (1,"-1"),
        (4,"0"), (5,"0"), (5,"0"), (5,"1.0"), (0,""), (3,"0xFF7F7F7F"), (3,"0xFF000000"), (1,"512"),
        (1,"-1"), (1,"0"), (5,"0"), (4,"0"), (4,"0"), (1,"0"), (1,"0"), (0,"UI_CONSOLE_NULL"),
        (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"),
        (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"),
        (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"),
        (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"),
        (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (0,"UI_CONSOLE_NULL"), (4,"1"), (5,"2"), (5,"2"),
        (5,"1"), (4,"0"), (4,"0"), (4,"0"), (4,"0"), (4,"0"), (1,"200"), (4,"0"),
        (4,"0"), (5,"0"), (5,"0"), (5,"0"), (5,"0"), (0,""), (0,""), (4,"0"),
        (1,"PC"), (4,"1"), (4,"1"), (1,"0"), (1,"0"), (4,"0"), (4,"1"), (0,""),
        (1,"0"), (4,"0"), (1,"0"), (4,"0"),
    };

    const UiCertainty V = UiCertainty.Verified, S = UiCertainty.Statistics, G = UiCertainty.Guessed;

    static readonly Dictionary<int, (string Name, string Meaning, UiCertainty C)> Names = new()
    {
        [0x01] = ("TemplateMarker", "\"1\" = template definition: the factory 0x1E4BF0 does not build it as an ordinary child; never inherited", V),
        [0x02] = ("Flag02", "bool (default 1) stored by the base loader 0x1E6820 (+0x440); stock writes 0", G),
        [0x03] = ("Left", "ABSOLUTE layout x (old LTRB form). If 0x03 or 0x04 is present, x=0x03, y=0x04, w=0x05-0x03 (else 0x09), h=0x06-0x04 (else 0x0A), then made parent-relative by subtracting the parent's absolute design origin (0x1F0560/0x1F05F0)", V),
        [0x04] = ("Top", "ABSOLUTE layout y (LTRB form, see 0x03)", V),
        [0x05] = ("Right", "ABSOLUTE right edge (LTRB form)", V),
        [0x06] = ("Bottom", "ABSOLUTE bottom edge (LTRB form)", V),
        [0x07] = ("X", "x relative to the parent's top-left (used when neither 0x03 nor 0x04 is present)", V),
        [0x08] = ("Y", "y relative to the parent's top-left", V),
        [0x09] = ("Width", "width (both forms unless 0x05 given)", V),
        [0x0A] = ("Height", "height (both forms unless 0x06 given)", V),
        [0x0B] = ("MaxWidth", "if > 0: width = min(width, this) (SetPosSize 0x1ECE50)", V),
        [0x0C] = ("MaxHeight", "if > 0: height = min(height, this)", V),
        [0x0D] = ("MinWidth", "if > 0: width = max(width, this)", V),
        [0x0E] = ("MinHeight", "if > 0: height = max(height, this)", V),
        [0x0F] = ("Alpha", "opacity 0..255 (byte at def+0x4A8, default 255)", V),
        [0x10] = ("Texture", "background / normal-state texture (texture getter 0x1D6250 -> 0x3A67E0), e.g. \"UI:BattleNetLightBox\"", V),
        [0x11] = ("TextureHover", "mouse-over texture of buttons/list items", S),
        [0x12] = ("TexturePressed", "pressed texture", S),
        [0x13] = ("TextureDisabled", "disabled texture", S),
        [0x14] = ("IconTexture", "button icon, normal", S),
        [0x15] = ("IconHover", "button icon, hover", S),
        [0x16] = ("IconPressed", "button icon, pressed", S),
        [0x17] = ("IconDisabled", "button icon, disabled (texture getter in the button loader 0x22A360)", S),
        [0x18] = ("CheckedTexture", "check box (type 18), checked up", S),
        [0x19] = ("CheckedHover", "check box checked, hover", S),
        [0x1A] = ("CheckedDisabled", "check box checked, disabled", S),
        [0x1B] = ("UncheckedTexture", "check box unchecked, up", S),
        [0x1C] = ("UncheckedHover", "check box unchecked, hover", S),
        [0x1D] = ("UncheckedDisabled", "check box unchecked, disabled", S),
        [0x1E] = ("GlowTexture", "hotbar glow (\"UI:GlowRound\")", S),
        [0x1F] = ("Anim2D", "Anim2D SNO name (SNO lookup 0x6DEB80 with group 7), e.g. \"ProgressSpinner\"", V),
        [0x20] = ("Texture2", "second texture (def+0x4C0; frame/overlay), e.g. \"UI:BattleNetExtendedExperience_Frame\"", V),
        [0x21] = ("IconWidth", "button icon width", G),
        [0x22] = ("IconHeight", "button icon height", G),
        [0x29] = ("OnClick", "click handler name (LookupUIHandler)", V),
        [0x2A] = ("ClickPredicate", "click predicate handler", S),
        [0x2B] = ("OnDoubleClick", "double-click handler", V),
        [0x2C] = ("DrawHook", "type-17 draw hook name (9-entry table 0x105D8B0)", V),
        [0x2D] = ("Float2D", "float (text/combo)", G),
        [0x2E] = ("Float2E", "float (text/combo)", G),
        [0x2F] = ("TextInsetLeft", "text inset/offset floats 0x2F..0x32 (left, top, right, bottom); read by the text loader 0x1FA720", G),
        [0x30] = ("TextInsetTop", "see 0x2F", G),
        [0x31] = ("TextInsetRight", "see 0x2F", G),
        [0x32] = ("TextInsetBottom", "see 0x2F", G),
        [0x33] = ("TextColor", "text colour ARGB (default 0xFFCD9D51, the D3 gold)", V),
        [0x34] = ("TextShadowColor", "text shadow/outline colour ARGB (default 0xFF000000)", G),
        [0x35] = ("TextColorDisabled", "button/radio text colour (default 0xFF7F7F7F)", G),
        [0x36] = ("TextColorHover", "button text colour (default white)", G),
        [0x37] = ("TextColorPressed", "button text colour (default 0xFFFF691E)", G),
        [0x38] = ("TextColor38", "button text colour (default white)", G),
        [0x39] = ("TextColorSelected", "button/check text colour (default 0xFFFF691E)", G),
        [0x3A] = ("Font", "font name: Serif, ExocetLight (default), SansSerif, Script", V),
        [0x3B] = ("FontSize", "font size in UI units (default 24)", V),
        [0x3C] = ("MinFontSize", "second size (+0xA20 of text); probably shrink-to-fit minimum", G),
        [0x3D] = ("TextHAlign", "horizontal text alignment 1 left, 2 center, 3 right (2/3 checked in game)", V),
        [0x3E] = ("TextVAlign", "vertical text alignment/flags (values 1,2,3,4,6; 6 most common)", G),
        [0x3F] = ("LineSpacing", "float, default 1", G),
        [0x40] = ("WordWrap", "text bool", G),
        [0x41] = ("Text", "text string key \"StringList:Label\" (e.g. \"Bnet_Strings:_ReplacedByData\")", S),
        [0x42] = ("TextFit", "text int 1,2,8,...", G),
        [0x45] = ("HotkeyId", "key binding id (type 6 hotkey controls, buttons)", G),
        [0x47] = ("OnHotkey", "hotkey handler (type 6)", S),
        [0x48] = ("OnConsoleHotkey", "console hotkey handler (type 6)", S),
        [0x49] = ("Flag49", "bool -> control flag 0x8", V),
        [0x4A] = ("Flag4A", "bool -> control flag 0x2 (800 stock uses, mostly 1)", V),
        [0x4B] = ("Flag4B", "bool -> control flag 0x10", V),
        [0x4C] = ("Flag4C", "bool -> control flag 0x20", V),
        [0x4D] = ("OnDragStart", "drag start handler", V),
        [0x4E] = ("OnDrag", "drag handler", V),
        [0x52] = ("Color52", "colour (default 0xFF888888; stock -1)", G),
        [0x53] = ("ProgressMin", "progress bar float", G),
        [0x54] = ("ProgressMax", "progress bar float (100)", G),
        [0x58] = ("ProgressEndCap", "progress bar end cap name", S),
        [0x59] = ("ProgressFlash", "progress bar flash control", S),
        [0x5A] = ("ProgressTarget", "progress bar target control", S),
        [0x5B] = ("ProgressFill", "progress fill texture", V),
        [0x5C] = ("ProgressBack", "progress bar texture", V),
        [0x5D] = ("SelectedSprite", "dropdown selected sprite", S),
        [0x5F] = ("OnInit", "init handler (base loader +0x468)", V),
        [0x60] = ("OnOpen", "open/loaded handler (+0x448)", V),
        [0x61] = ("OnClose", "close/unloaded handler (+0x450)", V),
        [0x62] = ("Handler62", "handler (+0x460)", V),
        [0x63] = ("OnUpdate", "per-frame update handler (+0x458)", V),
        [0x64] = ("OnValueChanged", "scroll bar/edit box value changed", S),
        [0x65] = ("OnSelectionChanged", "list/dropdown/radio group selection changed", S),
        [0x66] = ("Int66", "int (def+0x4D8, -1 -> 0), values 0,1,3", G),
        [0x67] = ("Flag67", "bool -> control flag bit 12", V),
        [0x68] = ("Int68", "int (def+0x9F0)", V),
        [0x69] = ("OnTooltip", "tooltip handler (called every frame while hovered)", V),
        [0x6A] = ("TooltipOffsetX", "int -> float, tooltip parameter block (+0x5C8)", G),
        [0x6B] = ("TooltipOffsetY", "int -> float (+0x5CC)", G),
        [0x6C] = ("TooltipAnchor", "int (+0x5D0): values 5,7,8", G),
        [0x6D] = ("TooltipAnchor2", "int (+0x5D4): values 0,2,3,7", G),
        [0x6E] = ("TooltipText", "tooltip string key (128 chars)", S),
        [0x6F] = ("TooltipPlacement", "int default \"Center\" (+0x5C0)", G),
        [0x70] = ("OnRightClick", "right-click handler", V),
        [0x76] = ("TooltipWidth", "int (+0x5C4), values 400/500", G),
        [0x7B] = ("OnEnter", "edit box enter handler", S),
        [0x7C] = ("OnGainFocus", "edit box focus handler", S),
        [0x80] = ("Handler80", "handler (+0x978)", V),
        [0x83] = ("RadioGroup", "radio button group name/path", S),
        [0x84] = ("RadioIndex", "radio value 0..3", S),
        [0x87] = ("DropdownRows", "dropdown visible rows (default 5)", V),
        [0x88] = ("DropdownPlaceholder", "dropdown placeholder text key", S),
        [0x8A] = ("Int8A", "int (def+0x4D4)", G),
        [0x8B] = ("BlendShader", "draw shader name (SNO group 37), e.g. sprite_additive", V),
        [0x8C] = ("HAnchor", "LEFT=1, RIGHT=2, STRETCH=3 (case-insensitive), else centre=0; see UiLayoutEngine", V),
        [0x8D] = ("VAnchor", "TOP=1, BOTTOM=2, else centre=0", V),
        [0x90] = ("RadioValue", "radio button value", G),
        [0x92] = ("RadioSelectedTexture", "radio selected up", S),
        [0x93] = ("RadioSelectedHover", "radio selected over", S),
        [0x94] = ("RadioSelectedPressed", "radio selected down", S),
        [0x97] = ("StartsHidden", "bool -> flag 0x40: the control starts hidden; code shows it", V),
        [0x99] = ("StackHorizontal", "stack panel: 1 = horizontal, 0 = vertical", V),
        [0x9A] = ("StackReverse", "stack panel: lay children out last-to-first", V),
        [0x9B] = ("StackSpacing", "stack panel: gap between children", V),
        [0x9C] = ("DockEdges", "letters L,T,R,B (default \"LT\"): which edges stay put when the control is resized later (R: keep right edge, B: keep bottom)", V),
        [0x9D] = ("NamedStyle", "named style reference \"Layout:StyleName\" or \"StyleName\" (header styles @56); supplies values the control lacks", V),
        [0x9E] = ("TemplateRef", "template reference \"Layout:Full.Path\" or \"Full.Path\": instantiates that control's children under this one; its values are the base of this control's", V),
        [0x9F] = ("ContextMenu", "context menu template ref (512 chars)", S),
        [0xA0] = ("StringA0", "second 512-char reference", G),
        [0xA1] = ("OnCreateContextMenu", "context menu create handler", V),
        [0xA4] = ("StackPadding", "stack panel padding \"top,right,bottom,left\" (CSS order)", V),
        [0xA6] = ("StackCenterCross", "bool -> flag 0x100: centred on the cross axis inside a stack", V),
        [0xAC] = ("ActorName", "3D actor (type 21)", S),
        [0xAD] = ("ObserverName", "3D observer (type 21)", S),
        [0xAE] = ("LightName", "3D light actor (type 21)", S),
        [0xAF] = ("ClickSound", "sound on click", S),
        [0xB0] = ("RightClickSound", "sound on right click", S),
        [0xB1] = ("MouseOverSound", "sound on mouse over", S),
        [0xB2] = ("OpenSound", "sound when opened (base loader +0x470)", V),
        [0xB3] = ("CloseSound", "sound when closed (+0x474)", V),
        [0xB8] = ("StoryboardDuration", "storyboard (type 45) length in seconds; key times are fractions 0..1", V),
        [0xBA] = ("OnStoryboardFinished", "storyboard finished handler", S),
        [0xBB] = ("ListItemTemplate", "list box row template \"Layout_main.Template\"", S),
        [0xBF] = ("TreeScrollbar", "tree scroll bar name", S),
        [0xC3] = ("ChatContextMenu", "chat context menu ref", S),
        [0xC4] = ("OnChatContextMenu", "chat context menu handler", S),
        [0xCC] = ("EditPlaceholder", "edit box placeholder text key", S),
        [0xCF] = ("EditMaxLength", "edit box max length (default 512)", G),
        [0xD1] = ("AutoAlign", "position inside the parent, re-applied on every resize: 1 centre x, 2 centre y, 3 both, 4 right-align", V),
        [0xD2] = ("Rotation", "float degrees (converted to radians, wrapped to [0, 2pi))", V),
        [0xD3] = ("FlipX", "bool (def+0x9C8)", G),
        [0xD4] = ("FlipY", "bool (def+0x9CC)", G),
        [0x10D] = ("ScrollGradientTop", "scroll box gradient texture", S),
        [0x10E] = ("ScrollGradientBottom", "scroll box gradient texture", S),
        [0x117] = ("ItemName", "item preview (type 30) item name", S),
    };

    public static int Count => Table.Length;

    public static UiPropertyInfo Info(int id)
    {
        var (kind, def) = id >= 0 && id < Table.Length ? Table[id] : ((byte)0, "");
        if (Names.TryGetValue(id, out var n)) return new UiPropertyInfo(id, (UiValueKind)kind, def, n.Name, n.Meaning, n.C);
        if (id >= 0xD7 && id <= 0xFC) return new UiPropertyInfo(id, (UiValueKind)kind, def, $"ConsoleHandler{id:X2}", "console/gamepad button handler", UiCertainty.Statistics);
        return new UiPropertyInfo(id, (UiValueKind)kind, def, $"Id{id:X2}", "", UiCertainty.Unknown);
    }

    public static IEnumerable<UiPropertyInfo> All() => Enumerable.Range(0, Table.Length).Select(Info);

    /// <summary>The exe's default for an int/bool/colour id (hex accepted).</summary>
    public static int DefaultInt(int id)
    {
        var d = id >= 0 && id < Table.Length ? Table[id].Default : "";
        if (d.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return unchecked((int)uint.Parse(d[2..], NumberStyles.HexNumber));
        return int.TryParse(d, out var v) ? v : 0;
    }

    public static float DefaultFloat(int id) =>
        float.TryParse(id >= 0 && id < Table.Length ? Table[id].Default : "", NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    public static string DefaultString(int id) => id >= 0 && id < Table.Length ? Table[id].Default : "";
}

/// <summary>Control type (record +1048) -> the CreationParams class the factory 0x1E4BF0 uses (switch on type-1, jump table
/// 0x1E63BC; verified) and what it is. Types 51, 52, 54 build nothing.</summary>
public static class UiControlTypes
{
    static readonly Dictionary<int, (string Class, string What)> Map = new()
    {
        [-1] = ("-", "no control (style holder / placeholder; the factory rejects it)"),
        [1] = ("Control", "plain UIControl"),
        [2] = ("Window", "panel/window: texture, children (most common)"),
        [3] = ("Button", "button: state textures 0x10..0x13, icon 0x14..0x17, label text, sounds"),
        [4] = ("Text", "static text label"),
        [5] = ("EditBox", "text input"),
        [6] = ("Hotkey", "invisible key binding handler (0x45 key, 0x47 handler)"),
        [7] = ("ChatEditBox", "chat input line"),
        [8] = ("ChatList", "chat message list"),
        [9] = ("EditBox", "console input"),
        [10] = ("Window(map)", "map / minimap"),
        [11] = ("Scrollbar", "scroll bar"),
        [12] = ("ProgressBar", "progress bar (fill 0x5B, back 0x5C)"),
        [13] = ("ListBox", "list box: rows created at runtime from template 0xBB"),
        [14] = ("ListItem", "list row template root (with style 1 \"1\")"),
        [15] = ("Window(15)", "window subclass"),
        [16] = ("Window(tutorial)", "tutorial"),
        [17] = ("DrawHook", "custom draw (0x2C)"),
        [18] = ("CheckBox", "check box / toggle button"),
        [19] = ("AutoScrollingText", "auto-scrolling text (conversation)"),
        [20] = ("ComboBox", "dropdown"),
        [21] = ("RActor", "3D actor view (0xAC actor, 0xAD observer, 0xAE light)"),
        [22] = ("RadioButton", "radio button / tab"),
        [23] = ("RadioButtonGroup", "radio button group (invisible)"),
        [24] = ("Text(balloon)", "chat balloon"),
        [25] = ("ScrollableText", "multi-line scrollable text"),
        [26] = ("Window(26)", "window subclass"),
        [27] = ("Button(27)", "button subclass"),
        [28] = ("Blinker", "blinking/animated sprite (0xC9..0xCB)"),
        [29] = ("Window(video)", "video"),
        [30] = ("ItemButton", "item slot (inventory/paperdoll)"),
        [31] = ("Button(31)", "button subclass"),
        [32] = ("Button(32)", "button subclass"),
        [33] = ("HotbarButton", "skill hotbar button"),
        [34] = ("RecipeListRadioButton", "crafting recipe button"),
        [35] = ("ShopButton", "shop button"),
        [36] = ("KeyMappingButton", "key binding button (options)"),
        [37] = ("Window(37)", "window subclass"),
        [38] = ("StackPanel", "stack panel: arranges visible children in a row/column and sizes itself to them"),
        [39] = ("WrapPanel", "wrap panel"),
        [40] = ("DialogBox", "dialog box"),
        [41] = ("ContextMenu", "context menu"),
        [42] = ("ContextMenuItem", "context menu item"),
        [43] = ("Type43", ""), [44] = ("Type44", ""),
        [45] = ("Storyboard", "animation (tracks +1784, keys +1712/+1736/+1760); not drawn"),
        [46] = ("Window(46)", ""), [47] = ("Tree", "tree view"), [48] = ("TreeFolder", "tree folder"),
        [49] = ("Window(49)", ""), [50] = ("TileListBox", "tile/grid list box (template 0xBB)"),
        [53] = ("ConsoleScrollBox", "scroll box"), [55] = ("ColorPicker", "colour picker"), [56] = ("Type56", "browser container"),
    };

    public static string ClassOf(int type) => Map.TryGetValue(type, out var m) ? m.Class : $"Type{type}";
    public static string Describe(int type) => Map.TryGetValue(type, out var m) ? m.What : "";
    public static bool IsStack(int type) => type == 38;
}

/// <summary>One set of values: styles (strings), ints and floats - the same shape on a control record, a child spec and a named style.</summary>
public sealed class UiDataSet
{
    public List<(int Id, string Value)> Strings { get; } = new();
    public List<(int Id, int Value)> Ints { get; } = new();
    public List<(int Id, float Value)> Floats { get; } = new();

    public bool Has(int id) => Strings.Any(s => s.Id == id) || Ints.Any(s => s.Id == id) || Floats.Any(s => s.Id == id);
    public string? String(int id) { foreach (var s in Strings) if (s.Id == id) return s.Value; return null; }
    public int? Int(int id) { foreach (var s in Ints) if (s.Id == id) return s.Value; return null; }
    public float? Float(int id) { foreach (var s in Floats) if (s.Id == id) return s.Value; return null; }

    /// <summary>Reads the three {count, offset(+16), bytes} arrays at <paramref name="at"/>, +24 and +48 (styles 264 B, ints 16 B, floats 16 B).</summary>
    internal static UiDataSet Read(byte[] b, int at)
    {
        var d = new UiDataSet();
        int n = Bin.I32(b, at), off = Bin.I32(b, at + 4);
        for (int k = 0; k < n; k++) { int e = 16 + off + k * 264; d.Strings.Add((Bin.I32(b, e), Bin.CStr(b, e + 8, 256))); }
        n = Bin.I32(b, at + 24); off = Bin.I32(b, at + 28);
        for (int k = 0; k < n; k++) { int e = 16 + off + k * 16; d.Ints.Add((Bin.I32(b, e), Bin.I32(b, e + 8))); }
        n = Bin.I32(b, at + 48); off = Bin.I32(b, at + 52);
        for (int k = 0; k < n; k++) { int e = 16 + off + k * 16; d.Floats.Add((Bin.I32(b, e), BitConverter.Int32BitsToSingle(Bin.I32(b, e + 8)))); }
        return d;
    }
}

/// <summary>UIStyleChildSpec (608 B; record +1648 array, also inside a named style @592): name[512] = a descendant's path
/// RELATIVE to the owner (24 stock specs are dotted, e.g. "Text.Label"), then styles @512, ints @536, floats @560 (never used in
/// stock), @584 an unused array slot. Every stock owner (193) has a template (0x9E) or named style (0x9D): the spec overrides values
/// of that descendant of the instance (runtime list at data+0x738, searched before the control's own values; verified 0x1D8350).</summary>
public sealed record UiChildSpec(string Name, UiDataSet Data);

/// <summary>UIStyle (header @56, 656 B entries): u64 FNV-1a64(lowercase name) @0 (93/93 verified), name[512] @8, styles @520,
/// ints @544, floats @568, child specs @592 (1 stock use), @616/@640 unused. Referenced by property 0x9D "Layout:Name".</summary>
public sealed record UiNamedStyle(ulong Hash, string Name, UiDataSet Data, IReadOnlyList<UiChildSpec> ChildSpecs);

public enum UiTrackKind { Int = 0, Float = 1, Vector2 = 2 }

/// <summary>What a binding animates (binding @512; verified by matching 529 stock bindings to their tracks' values).</summary>
public enum UiAnimTarget { X = 0, Y = 1, Width = 2, Height = 3, Position = 4, Size = 5, Alpha = 6 }

/// <summary>One key: time (fraction 0..1 of the storyboard's 0xB8 seconds) and value (Int: Value; Vector2: Value, Value2).</summary>
public readonly record struct UiAnimKey(float Time, float Value, float Value2);

/// <summary>UIAnimationDescription (536 B, record +1784 of a type-45 storyboard): u64 FNV-1a64(lowercase name) @0 (529/529),
/// name[512] @8, kind @520 (0 int keys +1712 {f32 t, i32 v}; 1 float keys +1736 {f32 t, f32 v}; 2 vector keys +1760 {f32 t, f32 x, f32 y}),
/// key range [@524, @528) into that array.</summary>
public sealed record UiAnimTrack(ulong Hash, string Name, UiTrackKind Kind, IReadOnlyList<UiAnimKey> Keys);

/// <summary>UIControlAnimationBinding (520 B, record +1808 on the animated control): "^Layout_main.Storyboard.Track" @0,
/// target @512 (UiAnimTarget), f32 @516 (always 1.0; weight).</summary>
public sealed record UiAnimBinding(string TrackPath, UiAnimTarget Target, float Weight);

/// <summary>A control definition: one 1856-byte record of the file, decoded. Raw bytes stay available for the serializer.</summary>
public sealed class UiDefinition
{
    public required UiLayout Layout { get; init; }
    public required int Index { get; init; }
    /// <summary>+0: creation order (parents first; also the order of children in stack panels).</summary>
    public required int Order { get; init; }
    public required string Name { get; init; }
    public required string ParentPath { get; init; }
    public string FullPath => ParentPath.Length == 0 ? Name : ParentPath + "." + Name;
    /// <summary>+1048 control type, see UiControlTypes.</summary>
    public required int Type { get; init; }
    /// <summary>+1064 class name ("$anon$&lt;name&gt;" for every stock control).</summary>
    public required string ClassName { get; init; }
    public required UiDataSet Data { get; init; }
    public required IReadOnlyList<UiChildSpec> ChildSpecs { get; init; }
    public required IReadOnlyList<UiAnimTrack> Tracks { get; init; }
    public required IReadOnlyList<UiAnimBinding> Bindings { get; init; }
    /// <summary>The whole record (1856 B) as read; +1672..+1711 and +1832..+1855 are zero in every stock record (unknown).</summary>
    public required byte[] RawRecord { get; init; }

    public UiDefinition? Parent { get; internal set; }
    public List<UiDefinition> Children { get; } = new();

    public bool IsTemplate => Data.String(UiProps.TemplateMarker) == "1";
    public override string ToString() => $"{FullPath} type={Type}";
}

/// <summary>A decoded .ui file: definitions (tree by +528 parent hash = path), named styles and layout-level data elements.</summary>
public sealed class UiLayout
{
    public string Name { get; }
    public UiFile File { get; }
    public List<UiDefinition> Definitions { get; } = new();
    public List<UiNamedStyle> Styles { get; } = new();
    /// <summary>Header @80 / @104 / @128: layout-level data elements {u64 hash, value} (string 264 B / int 16 B / float 16 B).
    /// The names are not stored (only hashes); 3 / 36 / 1 stock files have them. Purpose unknown.</summary>
    public List<(ulong Hash, string Value)> HeaderStrings { get; } = new();
    public List<(ulong Hash, int Value)> HeaderInts { get; } = new();
    public List<(ulong Hash, float Value)> HeaderFloats { get; } = new();
    readonly Dictionary<string, UiDefinition> byPath = new(StringComparer.OrdinalIgnoreCase);

    public UiLayout(UiFile file, string name)
    {
        File = file; Name = name;
        var b = file.Bytes;
        foreach (var c in file.Controls())
        {
            int r = UiFile.ControlsAt + c.Index * UiFile.ControlSize;
            var specs = new List<UiChildSpec>();
            int n = Bin.I32(b, r + 1648), off = Bin.I32(b, r + 1652);
            for (int k = 0; k < n; k++) { int e = 16 + off + k * 608; specs.Add(new UiChildSpec(Bin.CStr(b, e, 512), UiDataSet.Read(b, e + 512))); }
            var tracks = new List<UiAnimTrack>();
            n = Bin.I32(b, r + 1784); off = Bin.I32(b, r + 1788);
            for (int k = 0; k < n; k++)
            {
                int e = 16 + off + k * 536;
                var kind = (UiTrackKind)Bin.I32(b, e + 520);
                int first = Bin.I32(b, e + 524), end = Bin.I32(b, e + 528);
                int field = kind switch { UiTrackKind.Int => 1712, UiTrackKind.Float => 1736, _ => 1760 }, size = kind == UiTrackKind.Vector2 ? 12 : 8;
                int ko = Bin.I32(b, r + field + 4), kn = Bin.I32(b, r + field);
                var keys = new List<UiAnimKey>();
                for (int q = first; q < end && q < kn; q++)
                {
                    int ke = 16 + ko + q * size;
                    float t = BitConverter.Int32BitsToSingle(Bin.I32(b, ke));
                    keys.Add(kind switch
                    {
                        UiTrackKind.Int => new UiAnimKey(t, Bin.I32(b, ke + 4), 0),
                        UiTrackKind.Float => new UiAnimKey(t, BitConverter.Int32BitsToSingle(Bin.I32(b, ke + 4)), 0),
                        _ => new UiAnimKey(t, BitConverter.Int32BitsToSingle(Bin.I32(b, ke + 4)), BitConverter.Int32BitsToSingle(Bin.I32(b, ke + 8))),
                    });
                }
                tracks.Add(new UiAnimTrack(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(e)), Bin.CStr(b, e + 8, 512), kind, keys));
            }
            var binds = new List<UiAnimBinding>();
            n = Bin.I32(b, r + 1808); off = Bin.I32(b, r + 1812);
            for (int k = 0; k < n; k++) { int e = 16 + off + k * 520; binds.Add(new UiAnimBinding(Bin.CStr(b, e, 512), (UiAnimTarget)Bin.I32(b, e + 512), BitConverter.Int32BitsToSingle(Bin.I32(b, e + 516)))); }
            var d = new UiDefinition
            {
                Layout = this, Index = c.Index, Order = Bin.I32(b, r), Name = c.Name, ParentPath = c.Path, Type = Bin.I32(b, r + 1048),
                ClassName = Bin.CStr(b, r + UiFile.ClassNameAt, 512), Data = UiDataSet.Read(b, r + 1576), ChildSpecs = specs,
                Tracks = tracks, Bindings = binds, RawRecord = b.AsSpan(r, UiFile.ControlSize).ToArray(),
            };
            Definitions.Add(d);
            byPath[d.FullPath] = d;
        }
        foreach (var d in Definitions)
            if (d.ParentPath.Length > 0 && byPath.TryGetValue(d.ParentPath, out var p)) { d.Parent = p; p.Children.Add(d); }
        foreach (var d in Definitions) d.Children.Sort((x, y) => x.Order.CompareTo(y.Order));

        int sn = Bin.I32(b, 56), so = Bin.I32(b, 60);
        for (int k = 0; k < sn; k++)
        {
            int e = 16 + so + k * 656;
            var specs = new List<UiChildSpec>();
            int cn = Bin.I32(b, e + 592), co = Bin.I32(b, e + 596);
            for (int q = 0; q < cn; q++) { int ce = 16 + co + q * 608; specs.Add(new UiChildSpec(Bin.CStr(b, ce, 512), UiDataSet.Read(b, ce + 512))); }
            Styles.Add(new UiNamedStyle(BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(e)), Bin.CStr(b, e + 8, 512), UiDataSet.Read(b, e + 520), specs));
        }
        int hn = Bin.I32(b, 80), ho = Bin.I32(b, 84);
        for (int k = 0; k < hn; k++) { int e = 16 + ho + k * 264; HeaderStrings.Add((BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(e)), Bin.CStr(b, e + 8, 256))); }
        hn = Bin.I32(b, 104); ho = Bin.I32(b, 108);
        for (int k = 0; k < hn; k++) { int e = 16 + ho + k * 16; HeaderInts.Add((BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(e)), Bin.I32(b, e + 8))); }
        hn = Bin.I32(b, 128); ho = Bin.I32(b, 132);
        for (int k = 0; k < hn; k++) { int e = 16 + ho + k * 16; HeaderFloats.Add((BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(e)), BitConverter.Int32BitsToSingle(Bin.I32(b, e + 8)))); }
    }

    public static UiLayout Load(string path) => new(new UiFile(System.IO.File.ReadAllBytes(path)), Path.GetFileNameWithoutExtension(path));

    public UiDefinition? Find(string fullPath) => byPath.GetValueOrDefault(fullPath);
    public UiNamedStyle? FindStyle(string name) => Styles.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Top-level definitions that are built when the layout is created: roots without the template marker.</summary>
    public IEnumerable<UiDefinition> Roots => Definitions.Where(d => d.Parent == null && !d.IsTemplate).OrderBy(d => d.Order);
}

/// <summary>Finds other layouts by name (file name without .ui), for cross-file template (0x9E) and style (0x9D) references.</summary>
public sealed class UiLayoutSet
{
    readonly Dictionary<string, UiLayout> loaded = new(StringComparer.OrdinalIgnoreCase);
    readonly List<string> dirs;
    public UiLayoutSet(params string[] directories) => dirs = directories.ToList();
    public void Add(UiLayout l) => loaded[l.Name] = l;

    public UiLayout? Get(string name)
    {
        if (loaded.TryGetValue(name, out var l)) return l;
        foreach (var d in dirs)
        {
            var hit = Directory.EnumerateFiles(d, name + ".ui", SearchOption.AllDirectories).FirstOrDefault();
            if (hit != null) return loaded[name] = UiLayout.Load(hit);
        }
        return null;
    }

    /// <summary>"Layout:Path" -> (layout, path); a bare "Path" stays in <paramref name="current"/>. A leading '^' is dropped.</summary>
    public (UiLayout? Layout, string Path) Split(string reference, UiLayout current)
    {
        reference = reference.TrimStart('^');
        int colon = reference.IndexOf(':');
        return colon < 0 ? (current, reference) : (Get(reference[..colon]), reference[(colon + 1)..]);
    }
}

public readonly record struct UiRect(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public override string ToString() => FormattableString.Invariant($"{Left:0.#},{Top:0.#},{Right:0.#},{Bottom:0.#}");
}

/// <summary>Screen in UI units. The UI is a 1600x1200 design space scaled by s = min(W/1600, H/1200) and centred
/// (1920x1080: x' = 240 + 0.9x). Wider screens show (W/s - 1600)/2 extra units on each side (266.7 at 16:9), which LEFT/RIGHT
/// anchored controls move into; taller screens likewise for TOP/BOTTOM (verified: anchor offset 0x1EB7A0, screenshot check).</summary>
public readonly record struct UiScreen(float PixelWidth, float PixelHeight)
{
    public static readonly UiScreen Wide16x9 = new(1920, 1080);
    public float Scale => Math.Min(PixelWidth / 1600f, PixelHeight / 1200f);
    public float MarginX => (PixelWidth / Scale - 1600f) / 2f;
    public float MarginY => (PixelHeight / Scale - 1200f) / 2f;
    /// <summary>The whole window in UI units (what a STRETCH control gets).</summary>
    public UiRect Full => new(-MarginX, -MarginY, 1600 + MarginX, 1200 + MarginY);
    public (float X, float Y) ToPixels(float x, float y) => ((x + MarginX) * Scale, (y + MarginY) * Scale);
}

public enum UiHAnchor { Center = 0, Left = 1, Right = 2, Stretch = 3 }
public enum UiVAnchor { Center = 0, Top = 1, Bottom = 2 }

/// <summary>Where a resolved value came from (highest priority first).</summary>
public enum UiValueSource { ChildSpec, Own, NamedStyle, Template, Default }

/// <summary>
/// A built control: a definition placed in the tree with templates expanded. Value lookup follows the exe (getters 0x1D5F30 /
/// 0x1D5FE0 / 0x1D6190 and 0x1D8350; verified): child-spec overrides from the instance site, the control's own values, its named
/// style (0x9D; not for id 0x9D itself), then the template base chain (the 0x9E target; ids 0x9E and 0x01 are never inherited),
/// then the exe default.
/// </summary>
public sealed class UiNode
{
    public required UiDefinition Definition { get; init; }
    public required string Path { get; init; }
    public UiNode? Parent { get; init; }
    public List<UiNode> Children { get; } = new();
    /// <summary>Overrides from child specs of ancestors (outermost instance site first; it wins - order is a guess).</summary>
    public List<UiDataSet> Overrides { get; } = new();
    public UiNamedStyle? Style { get; internal set; }
    /// <summary>The 0x9E target this control's values fall back to (and whose children it got).</summary>
    public UiNode? TemplateBase { get; internal set; }
    public bool FromTemplate { get; init; }

    /// <summary>Position relative to the parent's design rect and size, after min/max, AutoAlign and stack arrangement.</summary>
    public float X, Y, W, H;
    /// <summary>Absolute rect in the 1600x1200 design space (exe +0x4EC), before anchors.</summary>
    public UiRect DesignRect;
    /// <summary>Final rect in UI units, as the host's control_rect reports (exe +0x498).</summary>
    public UiRect Rect;
    public UiHAnchor HAnchor, EffectiveHAnchor;
    public UiVAnchor VAnchor, EffectiveVAnchor;

    public int Type => Definition.Type;
    public bool StartsHidden => Bool(UiProps.StartsHidden);

    public bool Has(int id) => Lookup(id, out _, out _);
    public string? String(int id) => Lookup(id, out var v, out _) ? v as string : null;
    public int Int(int id) => Lookup(id, out var v, out _) && v is int i ? i : UiProps.DefaultInt(id);
    public float Float(int id) => Lookup(id, out var v, out _) && v is float f ? f : UiProps.DefaultFloat(id);
    public bool Bool(int id) => Int(id) != 0;
    public string StringOrDefault(int id) => String(id) ?? UiProps.DefaultString(id);

    public UiValueSource SourceOf(int id) => Lookup(id, out _, out var s) ? s : UiValueSource.Default;

    public bool Lookup(int id, out object? value, out UiValueSource source)
    {
        foreach (var o in Overrides)
            if (Get(o, id, out value)) { source = UiValueSource.ChildSpec; return true; }
        return LookupBase(id, out value, out source);
    }

    bool LookupBase(int id, out object? value, out UiValueSource source)
    {
        source = UiValueSource.Own;
        if (Get(Definition.Data, id, out value)) return true;
        source = UiValueSource.NamedStyle;
        if (id != UiProps.NamedStyle && Style != null && Get(Style.Data, id, out value)) return true;
        source = UiValueSource.Template;
        if (id != UiProps.TemplateRef && id != UiProps.TemplateMarker && TemplateBase != null && TemplateBase.LookupBase(id, out value, out _)) return true;
        source = UiValueSource.Default; value = null;
        return false;
    }

    static bool Get(UiDataSet d, int id, out object? v)
    {
        var s = d.String(id); if (s != null) { v = s; return true; }
        var i = d.Int(id); if (i != null) { v = i.Value; return true; }
        var f = d.Float(id); if (f != null) { v = f.Value; return true; }
        v = null; return false;
    }

    public IEnumerable<UiNode> Descendants() { yield return this; foreach (var c in Children) foreach (var d in c.Descendants()) yield return d; }
    public override string ToString() => $"{Path} {Rect}";
}

/// <summary>
/// Builds the instance tree of a layout and computes every control's rectangle like the 2.8 client (verified in the exe:
/// loader 0x1EC0F0, init 0x1EFA80, SetPosSize 0x1ECE50, UpdateRect 0x1ED390, anchor inheritance 0x1ED640, anchor offset 0x1EB7A0,
/// stack panel 0x50FDB0/0x510190; checked against the in-game AH stash grid rect 164,309,585,929 and a 16:9 screenshot).
/// <code>
/// size/pos:  LTRB form if 0x03 or 0x04 present: abs (0x03,0x04), w = 0x05-0x03 | 0x09, h = 0x06-0x04 | 0x0A, rel = abs - parent design origin
///            else rel = (0x07, 0x08), size = (0x09, 0x0A)          [missing values are 0; values inherit through style/template]
///            w = min(w, 0x0B) if 0x0B > 0, h likewise 0x0C; w = max(w, 0x0D) if > 0, h 0x0E
///            0xD1: 1 x = (parentW-w)/2, 2 y = (parentH-h)/2, 3 both, 4 x = parentW-w
/// stack:     type 38 places its visible children (creation order, reversed if 0x9A) at a cursor starting at (padLeft, padTop),
///            advancing by w (0x99 = 1) or h (0x99 = 0) + spacing 0x9B; the other axis keeps the child's own value (centred if 0xA6);
///            then the stack's size = children's extent + (padRight, padBottom).
/// design:    DesignRect = parent.DesignRect.origin + (x, y), size (w, h); a STRETCH control's design rect is 0,0,1600,1200.
/// anchors:   effective = own 0x8C/0x8D, else that of the nearest ancestor with a non-zero one; STRETCH found that way counts as centre.
/// final:     STRETCH (own 0x8C) -> the whole screen; else DesignRect + (LEFT -marginX | RIGHT +marginX, TOP -marginY | BOTTOM +marginY).
/// </code>
/// Not modelled: sizes set by code at runtime (text auto-size with 0x9C dock edges, list box rows, controls moved by handlers).
/// </summary>
public sealed class UiLayoutEngine
{
    public UiLayoutSet Set { get; }
    public UiScreen Screen { get; }
    public List<string> Warnings { get; } = new();

    public UiLayoutEngine(UiLayoutSet set, UiScreen? screen = null) { Set = set; Screen = screen ?? UiScreen.Wide16x9; }

    /// <summary>Builds and lays out every root of the layout (template definitions are skipped like the game does).</summary>
    public List<UiNode> Build(UiLayout layout)
    {
        Set.Add(layout);
        var roots = layout.Roots.Select(r => Instantiate(r, null, r.FullPath, false, new List<(string, UiDataSet)>(), 0)).ToList();
        foreach (var r in roots) Layout(r);
        return roots;
    }

    /// <summary>Builds one definition subtree on its own, even a template (for previewing list rows and templates).</summary>
    public UiNode BuildSubtree(UiDefinition def)
    {
        Set.Add(def.Layout);
        var n = Instantiate(def, null, def.FullPath, false, new List<(string, UiDataSet)>(), 0);
        Layout(n);
        return n;
    }

    UiNode Instantiate(UiDefinition def, UiNode? parent, string path, bool fromTemplate, List<(string Rel, UiDataSet Data)> pending, int depth)
    {
        var node = new UiNode { Definition = def, Path = path, Parent = parent, FromTemplate = fromTemplate };
        // child-spec overrides that target this node (relative names were resolved to absolute paths by the caller)
        foreach (var (rel, data) in pending) if (rel.Equals(path, StringComparison.OrdinalIgnoreCase)) node.Overrides.Add(data);

        var styleRef = def.Data.String(UiProps.NamedStyle);
        if (styleRef != null)
        {
            var (sl, sname) = Set.Split(styleRef, def.Layout);
            node.Style = sl?.FindStyle(sname);
            if (node.Style == null) Warnings.Add($"{path}: named style {styleRef} not found");
        }
        // specs of this control (and of its named style) apply to its descendants
        var specs = new List<(string, UiDataSet)>(pending);
        foreach (var s in def.ChildSpecs) specs.Add((path + "." + s.Name, s.Data));
        if (node.Style != null) foreach (var s in node.Style.ChildSpecs) specs.Add((path + "." + s.Name, s.Data));

        foreach (var c in def.Children)
            if (!c.IsTemplate) node.Children.Add(Instantiate(c, node, path + "." + c.Name, fromTemplate, specs, depth + 1));

        var tref = def.Data.String(UiProps.TemplateRef);
        if (tref != null && depth < 32)
        {
            var (tl, tpath) = Set.Split(tref, def.Layout);
            var target = tl?.Find(tpath);
            if (target == null) Warnings.Add($"{path}: template {tref} not found");
            else
            {
                // the target is this control's value base; its children become this control's children
                node.TemplateBase = Instantiate(target, null, target.FullPath, true, new List<(string, UiDataSet)>(), depth + 1);
                foreach (var c in target.Children)
                    if (!c.IsTemplate) node.Children.Add(Instantiate(c, node, path + "." + c.Name, true, specs, depth + 1));
            }
        }
        return node;
    }

    public void Layout(UiNode root)
    {
        Measure(root, null);
        Place(root, null);
        Anchor(root);
    }

    /// <summary>Pass 1 (post-order for stacks): relative position and size of every node.</summary>
    void Measure(UiNode n, UiNode? parent)
    {
        n.HAnchor = ParseH(n.String(UiProps.HAnchor));
        n.VAnchor = ParseV(n.String(UiProps.VAnchor));
        if (n.HAnchor == UiHAnchor.Stretch) { n.X = 0; n.Y = 0; n.W = 1600; n.H = 1200; }
        else
        {
            if (n.Has(UiProps.Left) || n.Has(UiProps.Top))
            {
                float ax = n.Int(UiProps.Left), ay = n.Int(UiProps.Top);
                n.W = n.Has(UiProps.Right) ? n.Int(UiProps.Right) - ax : n.Int(UiProps.Width);
                n.H = n.Has(UiProps.Bottom) ? n.Int(UiProps.Bottom) - ay : n.Int(UiProps.Height);
                var (px, py) = parent == null ? (0f, 0f) : AbsOrigin(parent);
                n.X = ax - px; n.Y = ay - py;
            }
            else { n.X = n.Int(UiProps.X); n.Y = n.Int(UiProps.Y); n.W = n.Int(UiProps.Width); n.H = n.Int(UiProps.Height); }
            ClampSize(n);
        }
        foreach (var c in n.Children) Measure(c, n);
        if (UiControlTypes.IsStack(n.Type)) ArrangeStack(n);
        if (parent != null && n.HAnchor != UiHAnchor.Stretch) AutoAlign(n, parent);
    }

    /// <summary>Absolute design origin of a node during Measure (parents are measured before children).</summary>
    static (float, float) AbsOrigin(UiNode n)
    {
        float x = 0, y = 0;
        for (var p = n; p != null; p = p.Parent) { if (p.HAnchor == UiHAnchor.Stretch) break; x += p.X; y += p.Y; }
        return (x, y);
    }

    static void ClampSize(UiNode n)
    {
        int maxW = n.Int(UiProps.MaxWidth), maxH = n.Int(UiProps.MaxHeight), minW = n.Int(UiProps.MinWidth), minH = n.Int(UiProps.MinHeight);
        if (maxW > 0) n.W = Math.Min(n.W, maxW);
        if (maxH > 0) n.H = Math.Min(n.H, maxH);
        if (minW > 0) n.W = Math.Max(n.W, minW);
        if (minH > 0) n.H = Math.Max(n.H, minH);
    }

    static void AutoAlign(UiNode n, UiNode parent)
    {
        switch (n.Int(UiProps.AutoAlign))
        {
            case 1: n.X = (parent.W - n.W) * 0.5f; break;
            case 2: n.Y = (parent.H - n.H) * 0.5f; break;
            case 3: n.X = (parent.W - n.W) * 0.5f; n.Y = (parent.H - n.H) * 0.5f; break;
            case 4: n.X = parent.W - n.W; break;
        }
    }

    /// <summary>Stack panel arrangement (0x50FDB0 / 0x510190). Hidden children (0x97) take no space.</summary>
    static void ArrangeStack(UiNode s)
    {
        bool horizontal = s.Bool(UiProps.StackHorizontal), reverse = s.Bool(UiProps.StackReverse);
        float spacing = s.Int(UiProps.StackSpacing);
        var pad = ParsePadding(s.StringOrDefault(UiProps.StackPadding));   // top, right, bottom, left
        var kids = s.Children.Where(c => !c.StartsHidden).ToList();
        if (reverse) kids.Reverse();
        float cross = kids.Count == 0 ? 0 : kids.Max(c => horizontal ? c.H : c.W);
        float cx = pad[3], cy = pad[0], maxX = 0, maxY = 0;
        foreach (var c in kids)
        {
            bool centre = c.Bool(UiProps.StackCenterCross);
            if (horizontal) { c.X = cx; if (centre) c.Y = (cross + pad[0] + pad[2] - c.H) * 0.5f; cx += c.W + spacing; }
            else { c.Y = cy; if (centre) c.X = (cross + pad[3] + pad[1] - c.W) * 0.5f; cy += c.H + spacing; }
            maxX = Math.Max(maxX, c.X + c.W); maxY = Math.Max(maxY, c.Y + c.H);
        }
        s.W = maxX + pad[1]; s.H = maxY + pad[2];
        ClampSize(s);
    }

    static float[] ParsePadding(string s)
    {
        var p = s.Split(',').Select(x => float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).ToList();
        while (p.Count < 4) p.Add(0);
        return p.Take(4).ToArray();
    }

    /// <summary>Pass 2: absolute design rects (parent's design origin + own relative position).</summary>
    static void Place(UiNode n, UiNode? parent)
    {
        if (n.HAnchor == UiHAnchor.Stretch) n.DesignRect = new UiRect(0, 0, 1600, 1200);
        else
        {
            float ox = parent?.DesignRect.Left ?? 0, oy = parent?.DesignRect.Top ?? 0;
            n.DesignRect = new UiRect(ox + n.X, oy + n.Y, ox + n.X + n.W, oy + n.Y + n.H);
        }
        foreach (var c in n.Children) Place(c, n);
    }

    /// <summary>Pass 3: anchor inheritance (0x1ED640) and offsets (0x1EB7A0 / 0x1ED390).</summary>
    void Anchor(UiNode n)
    {
        var h = n.HAnchor; var v = n.VAnchor;
        for (var p = n.Parent; p != null && (h == UiHAnchor.Center || v == UiVAnchor.Center); p = p.Parent)
        {
            if (h == UiHAnchor.Center) h = p.HAnchor;
            if (v == UiVAnchor.Center) v = p.VAnchor;
        }
        n.EffectiveHAnchor = h == UiHAnchor.Stretch ? UiHAnchor.Center : h;
        n.EffectiveVAnchor = v;
        if (n.HAnchor == UiHAnchor.Stretch) n.Rect = Screen.Full;
        else
        {
            float dx = n.EffectiveHAnchor switch { UiHAnchor.Left => -Screen.MarginX, UiHAnchor.Right => Screen.MarginX, _ => 0 };
            float dy = n.EffectiveVAnchor switch { UiVAnchor.Top => -Screen.MarginY, UiVAnchor.Bottom => Screen.MarginY, _ => 0 };
            var d = n.DesignRect;
            n.Rect = new UiRect(d.Left + dx, d.Top + dy, d.Right + dx, d.Bottom + dy);
        }
        foreach (var c in n.Children) Anchor(c);
    }

    public static UiHAnchor ParseH(string? s) => s?.ToUpperInvariant() switch { "LEFT" => UiHAnchor.Left, "RIGHT" => UiHAnchor.Right, "STRETCH" => UiHAnchor.Stretch, _ => UiHAnchor.Center };
    public static UiVAnchor ParseV(string? s) => s?.ToUpperInvariant() switch { "TOP" => UiVAnchor.Top, "BOTTOM" => UiVAnchor.Bottom, _ => UiVAnchor.Center };
}

// ---------------------------------------------------------------------------------------------------------------- JSON DTOs

public sealed record UiRectDto(float Left, float Top, float Right, float Bottom)
{
    public static UiRectDto From(UiRect r) => new(r.Left, r.Top, r.Right, r.Bottom);
}

/// <summary>One resolved value: id, name, kind, value as text, and where it came from.</summary>
public sealed record UiValueDto(int Id, string Name, string Kind, string Value, string Source);

/// <summary>What the designer draws for one control. Definition = the record to edit (layout file + index); FromTemplate nodes
/// live in the template's file.</summary>
public sealed record UiNodeDto(
    string Path, string Name, string? ParentPath, int Type, string TypeClass, int Order,
    string DefinitionLayout, int DefinitionIndex, bool FromTemplate, string? TemplateRef, string? NamedStyle,
    UiRectDto Rect, UiRectDto DesignRect, string HAnchor, string VAnchor, bool StartsHidden, int Alpha,
    string? Texture, string? TextureHover, string? TexturePressed, string? TextureDisabled, string? Texture2,
    string? Text, string? Font, int? FontSize, uint? TextColor, int? TextHAlign, int? TextVAlign,
    IReadOnlyList<UiValueDto> Values);

public sealed record UiLayoutDto(string Name, int Sno, uint Version, float ScreenWidth, float ScreenHeight, float MarginX, float MarginY,
    IReadOnlyList<UiNodeDto> Nodes, IReadOnlyList<string> Warnings);

public static class UiLayoutDtos
{
    public static UiLayoutDto ToDto(UiLayout layout, IEnumerable<UiNode> roots, UiLayoutEngine engine) =>
        new(layout.Name, layout.File.SnoId, layout.File.Version, engine.Screen.PixelWidth, engine.Screen.PixelHeight, engine.Screen.MarginX, engine.Screen.MarginY,
            roots.SelectMany(r => r.Descendants()).Select(ToDto).ToList(), engine.Warnings.ToList());

    public static UiNodeDto ToDto(UiNode n)
    {
        var d = n.Definition;
        string? S(int id) => n.String(id);
        int? I(int id) => n.Has(id) ? n.Int(id) : null;
        return new UiNodeDto(n.Path, d.Name, n.Parent?.Path, d.Type, UiControlTypes.ClassOf(d.Type), d.Order,
            d.Layout.Name, d.Index, n.FromTemplate, d.Data.String(UiProps.TemplateRef), d.Data.String(UiProps.NamedStyle),
            UiRectDto.From(n.Rect), UiRectDto.From(n.DesignRect), n.HAnchor.ToString(), n.VAnchor.ToString(), n.StartsHidden, n.Int(UiProps.Alpha),
            S(UiProps.Texture), S(UiProps.TextureHover), S(UiProps.TexturePressed), S(UiProps.TextureDisabled), S(UiProps.Texture2),
            S(UiProps.Text), S(UiProps.Font), I(UiProps.FontSize), n.Has(UiProps.TextColor) ? unchecked((uint)n.Int(UiProps.TextColor)) : null,
            I(UiProps.TextHAlign), I(UiProps.TextVAlign), Values(n));
    }

    /// <summary>Every id that has a value on this node (own, override, style or template), resolved.</summary>
    public static List<UiValueDto> Values(UiNode n)
    {
        var ids = new SortedSet<int>();
        void Collect(UiDataSet d) { foreach (var s in d.Strings) ids.Add(s.Id); foreach (var s in d.Ints) ids.Add(s.Id); foreach (var s in d.Floats) ids.Add(s.Id); }
        foreach (var o in n.Overrides) Collect(o);
        Collect(n.Definition.Data);
        if (n.Style != null) Collect(n.Style.Data);
        for (var t = n.TemplateBase; t != null; t = t.TemplateBase) { Collect(t.Definition.Data); if (t.Style != null) Collect(t.Style.Data); }
        var list = new List<UiValueDto>();
        foreach (var id in ids)
        {
            if (!n.Lookup(id, out var v, out var src)) continue;
            var info = UiProps.Info(id);
            string text = v switch
            {
                float f => f.ToString(CultureInfo.InvariantCulture),
                int i when info.Kind == UiValueKind.Color => $"0x{unchecked((uint)i):X8}",
                _ => v?.ToString() ?? "",
            };
            list.Add(new UiValueDto(id, info.Name, info.Kind.ToString(), text, src.ToString()));
        }
        return list;
    }
}

static class Bin
{
    public static int I32(byte[] b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(at));
    public static string CStr(byte[] b, int at, int max)
    {
        int n = b.AsSpan(at, max).IndexOf((byte)0);
        return Encoding.ASCII.GetString(b, at, n < 0 ? max : n);
    }
}
