using UnityEngine;

// shared look for every on-screen element: one palette and one set of GUI styles
// role colors live here too, so the minimap, the 3D floor tints and the legend always match
public static class MapStyle
{
    // UI PALETTE

    public static readonly Color PanelColor = new Color(0.07f, 0.08f, 0.1f, 0.9f);
    public static readonly Color TextColor = new Color(0.91f, 0.92f, 0.94f);
    public static readonly Color MutedColor = new Color(0.6f, 0.63f, 0.68f);
    public static readonly Color AccentColor = new Color(0.98f, 0.76f, 0.3f);
    public static readonly Color OkColor = new Color(0.45f, 0.88f, 0.5f);
    public static readonly Color FailColor = new Color(1f, 0.52f, 0.44f);
    static readonly Color ButtonColor = new Color(0.17f, 0.19f, 0.23f, 1f);
    static readonly Color ButtonHoverColor = new Color(0.24f, 0.27f, 0.32f, 1f);
    static readonly Color ButtonActiveColor = new Color(0.12f, 0.13f, 0.16f, 1f);

    // MAP PALETTE

    // role colors differ in lightness as well as hue so they stay readable on the minimap
    public static readonly Color MapBackground = new Color(0.08f, 0.08f, 0.1f, 0.9f);
    public static readonly Color Partition = new Color(0.22f, 0.3f, 0.45f, 1f);
    public static readonly Color Room = new Color(0.78f, 0.74f, 0.62f, 1f);
    public static readonly Color Corridor = new Color(0.5f, 0.55f, 0.58f, 1f);
    public static readonly Color Player = new Color(1f, 0.95f, 0.3f, 1f);
    public static readonly Color Attacker = new Color(0.95f, 0.45f, 0.2f, 1f);
    public static readonly Color Defender = new Color(0.25f, 0.45f, 0.95f, 1f);
    public static readonly Color Mid = new Color(0.75f, 0.55f, 0.95f, 1f);
    public static readonly Color Site = new Color(0.35f, 0.85f, 0.4f, 1f);

    // color for a room role, plain rooms get the room color
    public static Color RoleColor(RoomRole role)
    {
        switch (role)
        {
            case RoomRole.AttackerSpawn: return Attacker;
            case RoomRole.DefenderSpawn: return Defender;
            case RoomRole.Mid: return Mid;
            case RoomRole.Site: return Site;
            default: return Room;
        }
    }

    // STYLES

    public static GUIStyle PanelBox, Title, Header, Label, Small, OkLabel, FailLabel;
    public static GUIStyle Button, AccentButton, MapLabel, Area;

    static Texture2D panelTex, buttonTex, buttonHoverTex, buttonActiveTex, accentTex, accentHoverTex;

    // GUI styles can only be built inside OnGUI, so every OnGUI calls this first
    // textures can be destroyed when scenes change, so a missing one triggers a rebuild
    public static void Ensure()
    {
        if (Title != null && panelTex != null) return;

        panelTex = Solid(PanelColor);
        buttonTex = Solid(ButtonColor);
        buttonHoverTex = Solid(ButtonHoverColor);
        buttonActiveTex = Solid(ButtonActiveColor);
        accentTex = Solid(AccentColor);
        accentHoverTex = Solid(Color.Lerp(AccentColor, Color.white, 0.25f));

        PanelBox = new GUIStyle(GUI.skin.box)
        {
            normal = { background = panelTex },
            border = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(14, 14, 12, 12)
        };

        Label = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, normal = { textColor = TextColor } };
        Small = new GUIStyle(Label) { fontSize = 11, normal = { textColor = MutedColor } };
        Title = new GUIStyle(Label) { fontSize = 18, fontStyle = FontStyle.Bold, wordWrap = false, normal = { textColor = AccentColor } };
        Header = new GUIStyle(Label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            margin = new RectOffset(4, 4, 12, 4),
            normal = { textColor = MutedColor }
        };
        OkLabel = new GUIStyle(Label) { fontStyle = FontStyle.Bold, normal = { textColor = OkColor } };
        FailLabel = new GUIStyle(Label) { normal = { textColor = FailColor } };

        Button = new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            border = new RectOffset(0, 0, 0, 0),
            padding = new RectOffset(10, 10, 6, 6),
            normal = { background = buttonTex, textColor = TextColor },
            hover = { background = buttonHoverTex, textColor = Color.white },
            active = { background = buttonActiveTex, textColor = AccentColor }
        };

        // dark text on the accent, the main action stands out from the other buttons
        var dark = new Color(0.08f, 0.08f, 0.1f);
        AccentButton = new GUIStyle(Button)
        {
            normal = { background = accentTex, textColor = dark },
            hover = { background = accentHoverTex, textColor = dark },
            active = { background = buttonActiveTex, textColor = AccentColor }
        };

        MapLabel = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            normal = { textColor = dark }
        };
        Area = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 22,
            fontStyle = FontStyle.Bold,
            normal = { textColor = TextColor }
        };
    }

    // DRAWING HELPERS

    // text with a soft dark shadow underneath, readable over any part of the 3D view
    public static void ShadowLabel(Rect r, string text, GUIStyle style)
    {
        var color = style.normal.textColor;
        style.normal.textColor = new Color(0f, 0f, 0f, 0.7f);
        GUI.Label(new Rect(r.x + 1f, r.y + 2f, r.width, r.height), text, style);
        style.normal.textColor = color;
        GUI.Label(r, text, style);
    }

    // flat colored rectangle, for legend chips, markers and the crosshair
    public static void Fill(Rect r, Color color)
    {
        var old = GUI.color;
        GUI.color = color;
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = old;
    }

    // 1x1 texture of one color, used as a style background
    static Texture2D Solid(Color color)
    {
        var tex = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
        tex.SetPixel(0, 0, color);
        tex.Apply();
        return tex;
    }
}
