using UnityEngine;
using UnityEngine.InputSystem;

// in-game generator panel: seed field, Generate button, sliders for the main settings and the constraint report
// Tab opens and closes it, player input pauses while it's open so the mouse can use the controls
// also draws the crosshair and shows which area the player is standing in at the bottom of the screen
[RequireComponent(typeof(baseMap))]
public class MapUI : MonoBehaviour
{
    public bool startOpen = false;
    [Min(240)] public int panelWidth = 360;

    baseMap map;
    FirstPersonController player;
    string seedText;
    Vector2 panelScroll;
    bool open;

    public bool IsOpen => open;

    void Awake()
    {
        map = GetComponent<baseMap>();
    }

    void Start()
    {
        player = FindAnyObjectByType<FirstPersonController>();
        seedText = map.seed.ToString();
        SetOpen(startOpen);
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb != null && kb.tabKey.wasPressedThisFrame) SetOpen(!open);
    }

    void SetOpen(bool value)
    {
        open = value;

        // hand the mouse to the panel, or back to the player (unless the designer view still has it)
        map.RefreshPlayerInput();

        // show the real seed again next time, in case the text was a word
        if (open) seedText = map.seed.ToString();
    }

    // builds a new map from the panel's values and moves the player into it
    void Apply()
    {
        map.Generate(ParseSeed(seedText));
        map.SpawnPlayer();
    }

    // numbers are used as-is, anything else (e.g. "dust") is hashed into a seed so words work too
    static int ParseSeed(string text)
    {
        text = text.Trim();
        if (int.TryParse(text, out int value)) return value;

        // FNV-1a by hand, string.GetHashCode can differ between runs and platforms
        unchecked
        {
            int h = (int)2166136261;
            foreach (char ch in text) h = (h ^ ch) * 16777619;
            return h & int.MaxValue;   // keep it positive so it reads nicely
        }
    }

    // PANEL

    void OnGUI()
    {
        if (map.Plan == null) return;
        MapStyle.Ensure();
        DrawHud();
        if (!open) return;

        // the whole panel scrolls, so it still fits on short screens
        GUILayout.BeginArea(new Rect(16f, 16f, panelWidth, Screen.height - 32f), MapStyle.PanelBox);
        panelScroll = GUILayout.BeginScrollView(panelScroll);
        GUILayout.Label("Map Generator", MapStyle.Title);
        GUILayout.Label("Changes apply when you press Generate", MapStyle.Small);
        DrawSeedRow();
        DrawLayoutSliders();
        DrawConstraintSliders();
        GUILayout.Space(10f);
        if (GUILayout.Button("Generate", MapStyle.AccentButton, GUILayout.Height(34f))) Apply();
        DrawViewSection();
        DrawReport();
        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    void DrawSeedRow()
    {
        GUILayout.Label("SEED", MapStyle.Header);
        GUILayout.BeginHorizontal();
        seedText = GUILayout.TextField(seedText, MapStyle.TextField, GUILayout.Height(26f));
        if (GUILayout.Button("Random", MapStyle.Button, GUILayout.Width(80f), GUILayout.Height(26f)))
        {
            seedText = Random.Range(0, int.MaxValue).ToString();
            Apply();
        }
        GUILayout.EndHorizontal();

        // a word seed shows the number it turns into, so it can be shared either way
        if (!int.TryParse(seedText.Trim(), out _))
            GUILayout.Label($"= seed {ParseSeed(seedText)}", MapStyle.Small);
    }

    void DrawLayoutSliders()
    {
        var s = map.settings;
        GUILayout.Label("LAYOUT", MapStyle.Header);

        // the map stays square, one slider sets both sides
        int size = IntSlider("Map size", s.width, 32, 128);
        s.width = size;
        s.height = size;
        s.maxDepth = IntSlider("Max depth", s.maxDepth, 2, 8);
        s.minRoomSize = IntSlider("Min room size", s.minRoomSize, 3, 12);
        s.corridorWidth = IntSlider("Corridor width", s.corridorWidth, 1, 4);
    }

    void DrawConstraintSliders()
    {
        var c = map.constraints;
        GUILayout.Label("CONSTRAINTS", MapStyle.Header);
        c.enabled = GUILayout.Toggle(c.enabled, " Use attack/defense constraints", MapStyle.Toggle, GUILayout.Height(26f));
        if (!c.enabled) return;

        // the rules themselves are fixed, only how many sites the map has is a choice
        c.siteCount = IntSlider("Sites", c.siteCount, 2, 3);
    }

    // settings that only change how the map looks, so they apply right away without a new layout
    void DrawViewSection()
    {
        GUILayout.Label("VIEW", MapStyle.Header);

        string label = map.DesignerActive ? "Back to Player View  [V]" : "Designer View  [V]";
        if (GUILayout.Button(label, MapStyle.Button, GUILayout.Height(28f))) map.ToggleDesignerView();

        // only rebuild the meshes when the value actually moved
        float height = FloatSlider("Wall height", map.wallHeight, 2f, 6f);
        if (!Mathf.Approximately(height, map.wallHeight))
        {
            map.wallHeight = height;
            map.RebuildGeometry();
        }
        map.showOverlay = GUILayout.Toggle(map.showOverlay, " Show minimap  [M]", MapStyle.Toggle, GUILayout.Height(26f));
    }

    // summary, then the distance and entrance report
    void DrawReport()
    {
        GUILayout.Label("REPORT", MapStyle.Header);
        GUILayout.Label(map.ConstraintSummary(), MapStyle.OkLabel);
        foreach (var line in map.Plan.report) GUILayout.Label(line, MapStyle.Small);
    }

    // HUD

    // crosshair and area name in player view, plus a hint line when the panel is closed
    void DrawHud()
    {
        bool playerView = !map.DesignerActive && !open;
        if (playerView) DrawCrosshair();
        if (playerView) DrawAreaName();

        if (!open)
        {
            string hint = map.DesignerActive
                ? "Designer view: WASD pan   Q/E rotate   scroll zoom   right-drag orbit   F frame   V exit"
                : "[Tab] map generator   [V] designer view";
            MapStyle.HaloLabel(new Rect(16f, Screen.height - 30f, Screen.width - 32f, 20f), hint, MapStyle.Small);
        }
    }

    // small light dot with a dark rim, so it reads against light walls and dark floors alike
    static void DrawCrosshair()
    {
        float cx = Screen.width / 2f, cy = Screen.height / 2f;
        MapStyle.Fill(new Rect(cx - 3f, cy - 3f, 6f, 6f), MapStyle.Halo);
        MapStyle.Fill(new Rect(cx - 2f, cy - 2f, 4f, 4f), MapStyle.Ink);
    }

    // name of the area under the player, centered near the bottom of the screen
    void DrawAreaName()
    {
        if (player == null) return;
        string area = map.AreaNameAt(player.transform.position);
        if (area == null) return;
        MapStyle.HaloLabel(new Rect(0f, Screen.height - 76f, Screen.width, 32f), area, MapStyle.Area);
    }

    // CONTROLS

    // whole-number slider, rounded so it snaps to integers
    int IntSlider(string label, int value, int min, int max)
    {
        return Mathf.RoundToInt(SliderRow(label, value, min, max, "0"));
    }

    float FloatSlider(string label, float value, float min, float max)
    {
        return SliderRow(label, value, min, max, "0.00");
    }

    // label, slider and current value on one row, the value shown in the given number format
    float SliderRow(string label, float value, float min, float max, string format)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, MapStyle.Label, GUILayout.Width(140f));
        value = GUILayout.HorizontalSlider(value, min, max, MapStyle.Slider, MapStyle.SliderThumb, GUILayout.ExpandWidth(true));
        GUILayout.Label(value.ToString(format), MapStyle.Small, GUILayout.Width(36f));
        GUILayout.EndHorizontal();
        return value;
    }
}
