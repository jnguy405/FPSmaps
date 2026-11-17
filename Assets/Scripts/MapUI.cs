using UnityEngine;
using UnityEngine.InputSystem;

// in-game generator panel: seed field, Generate button, sliders for the main settings and the constraint report
// Tab opens and closes it, player input pauses while it's open so the mouse can use the controls
// also shows which area the player is standing in at the bottom of the screen
[RequireComponent(typeof(baseMap))]
public class MapUI : MonoBehaviour
{
    public bool startOpen = false;
    [Min(240)] public int panelWidth = 360;

    baseMap map;
    FirstPersonController player;
    string seedText;
    Vector2 reportScroll;
    bool open;

    // built on first use, GUI styles can only be created inside OnGUI
    GUIStyle titleStyle, wrapStyle, failStyle, areaStyle;

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

        // hand the mouse to the panel, or back to the player
        if (player != null) player.SetInputEnabled(!open);

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
        EnsureStyles();
        DrawAreaName();

        if (!open)
        {
            GUI.Label(new Rect(10f, Screen.height - 30f, 300f, 20f), "[Tab] map generator");
            return;
        }

        GUILayout.BeginArea(new Rect(10f, 10f, panelWidth, Screen.height - 20f), GUI.skin.box);
        GUILayout.Label("Map Generator", titleStyle);
        DrawSeedRow();
        GUILayout.Space(8f);
        DrawLayoutSliders();
        GUILayout.Space(8f);
        DrawConstraintSliders();
        GUILayout.Space(8f);
        if (GUILayout.Button("Generate", GUILayout.Height(32f))) Apply();
        GUILayout.Space(8f);
        DrawReport();
        GUILayout.EndArea();
    }

    void DrawSeedRow()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label("Seed", GUILayout.Width(40f));
        seedText = GUILayout.TextField(seedText);
        if (GUILayout.Button("Random", GUILayout.Width(70f)))
        {
            seedText = Random.Range(0, int.MaxValue).ToString();
            Apply();
        }
        GUILayout.EndHorizontal();

        // a word seed shows the number it turns into, so it can be shared either way
        if (!int.TryParse(seedText.Trim(), out _))
            GUILayout.Label($"= seed {ParseSeed(seedText)}", wrapStyle);
    }

    void DrawLayoutSliders()
    {
        var s = map.settings;
        GUILayout.Label("Layout", titleStyle);

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
        GUILayout.Label("Constraints", titleStyle);
        c.enabled = GUILayout.Toggle(c.enabled, " Use attack/defense constraints");
        if (!c.enabled) return;

        c.siteCount = IntSlider("Sites", c.siteCount, 2, 3);
        c.minSiteArea = IntSlider("Min site area", c.minSiteArea, 16, 144);
        c.siteBalance = FloatSlider("Site balance", c.siteBalance, 0.05f, 0.6f);
        c.defenderAdvantage = FloatSlider("Defender advantage", c.defenderAdvantage, 0.5f, 1f);
        c.preferredEntrancesPerSide = IntSlider("Entrances per side", c.preferredEntrancesPerSide, 1, 3);
    }

    // summary, every broken rule, then the distance report, scrolling if it runs long
    void DrawReport()
    {
        var plan = map.Plan;
        GUILayout.Label(map.ConstraintSummary(), titleStyle);
        reportScroll = GUILayout.BeginScrollView(reportScroll);
        foreach (var f in plan.failures) GUILayout.Label("FAILED: " + f, failStyle);
        foreach (var line in plan.report) GUILayout.Label(line, wrapStyle);
        GUILayout.EndScrollView();
    }

    // name of the area under the player, centered near the bottom of the screen
    void DrawAreaName()
    {
        if (player == null) return;
        string area = map.AreaNameAt(player.transform.position);
        if (area == null) return;
        GUI.Label(new Rect(0f, Screen.height - 60f, Screen.width, 30f), area, areaStyle);
    }

    // CONTROLS

    // label, slider and current value on one row
    int IntSlider(string label, int value, int min, int max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(130f));
        value = Mathf.RoundToInt(GUILayout.HorizontalSlider(value, min, max));
        GUILayout.Label(value.ToString(), GUILayout.Width(36f));
        GUILayout.EndHorizontal();
        return value;
    }

    float FloatSlider(string label, float value, float min, float max)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Label(label, GUILayout.Width(130f));
        value = GUILayout.HorizontalSlider(value, min, max);
        GUILayout.Label(value.ToString("0.00"), GUILayout.Width(36f));
        GUILayout.EndHorizontal();
        return value;
    }

    void EnsureStyles()
    {
        if (titleStyle != null) return;
        titleStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
        wrapStyle = new GUIStyle(GUI.skin.label) { wordWrap = true };
        failStyle = new GUIStyle(wrapStyle) { normal = { textColor = new Color(1f, 0.55f, 0.45f) } };
        areaStyle = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 20,
            fontStyle = FontStyle.Bold
        };
    }
}
