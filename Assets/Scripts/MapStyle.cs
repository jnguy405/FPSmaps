using UnityEngine;

// shared look for every on-screen element: one dark navy palette and one set of GUI styles
// role colors live here too, so the minimap, the 3D floor tints and the legend always match
public static class MapStyle
{
    // UI PALETTE

    public static readonly Color PanelColor = Hex("#0B121C", 0.95f);   // panel fill, near black with a blue cast
    static readonly Color Line = Hex("#22364F");                       // panel and control outlines
    static readonly Color Control = Hex("#111C2B");                    // button and field fill
    static readonly Color ControlHover = Hex("#182941");
    static readonly Color Pressed = Hex("#0E2E52");                    // pressed buttons, toggles while on
    static readonly Color Track = Hex("#1C2D44");                      // slider track
    public static readonly Color Accent = Hex("#4DA3FF");              // section headers, main button, focus
    public static readonly Color Ink = Hex("#DCE7F5");                 // main text
    public static readonly Color LabelInk = Hex("#7E93AD");            // small labels
    public static readonly Color Halo = Hex("#05080D", 0.8f);          // dark outline behind text over the 3D view

    // MAP PALETTE

    // one bright color per role, apart in hue and lightness so they stay readable on the dark minimap
    public static readonly Color MapBackground = Hex("#070B11", 0.95f);
    public static readonly Color Partition = Hex("#1A2738");
    public static readonly Color Room = Hex("#2A3F5A");
    public static readonly Color Corridor = Hex("#1B2C42");
    public static readonly Color Player = Hex("#F5F8FC");
    public static readonly Color Attacker = Hex("#FF7A59");   // coral
    public static readonly Color Defender = Hex("#4DA3FF");   // electric blue
    public static readonly Color Mid = Hex("#A78BFA");        // violet
    public static readonly Color Site = Hex("#34D399");       // emerald

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

    public static GUIStyle PanelBox, Title, Header, Label, Small, OkLabel;
    public static GUIStyle Button, AccentButton, Toggle, TextField, Slider, SliderThumb, MapLabel, Area;

    static Texture2D panelTex;

    // GUI styles can only be built inside OnGUI, so every OnGUI calls this first
    // textures can be destroyed when scenes change, so a missing one triggers a rebuild
    public static void Ensure()
    {
        if (Title != null && panelTex != null) return;

        // boxes are 9-sliced, the border keeps the corners from stretching
        // small corner radius and 1px outlines keep every edge crisp
        panelTex = Rounded(16, 16, 0, 3f, 1f, PanelColor, Line);
        PanelBox = new GUIStyle(GUI.skin.box)
        {
            normal = { background = panelTex },
            border = new RectOffset(4, 4, 4, 4),
            padding = new RectOffset(16, 16, 14, 14)
        };

        Label = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true, normal = { textColor = Ink } };
        Small = new GUIStyle(Label) { fontSize = 11, normal = { textColor = LabelInk } };
        Title = new GUIStyle(Label) { fontSize = 18, fontStyle = FontStyle.Bold, wordWrap = false, normal = { textColor = Ink } };
        Header = new GUIStyle(Label)
        {
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            margin = new RectOffset(4, 4, 14, 4),
            normal = { textColor = Accent }
        };
        OkLabel = new GUIStyle(Label) { fontStyle = FontStyle.Bold, normal = { textColor = Accent } };

        // dark buttons with a thin outline, the outline lights up in the accent on hover
        var box = Rounded(16, 16, 0, 3f, 1f, Control, Line);
        var boxHover = Rounded(16, 16, 0, 3f, 1f, ControlHover, Accent);
        var boxPressed = Rounded(16, 16, 0, 3f, 1f, Pressed, Accent);
        Button = new GUIStyle(GUI.skin.button)
        {
            fontSize = 13,
            fontStyle = FontStyle.Bold,
            border = new RectOffset(4, 4, 4, 4),
            padding = new RectOffset(12, 12, 6, 6),
            normal = { background = box, textColor = Ink },
            hover = { background = boxHover, textColor = Color.white },
            active = { background = boxPressed, textColor = Color.white }
        };

        // solid accent with near-black text, so the main action stands out from the other buttons
        AccentButton = new GUIStyle(Button)
        {
            normal = { background = Rounded(16, 16, 0, 3f, 1f, Accent, Accent), textColor = PanelColor },
            hover = { background = Rounded(16, 16, 0, 3f, 1f, Hex("#73B7FF"), Hex("#73B7FF")), textColor = PanelColor },
            active = { background = boxPressed, textColor = Color.white }
        };

        // toggles look like buttons, tinted blue with an accent outline while on
        Toggle = new GUIStyle(Button)
        {
            fontSize = 12,
            alignment = TextAnchor.MiddleLeft,
            onNormal = { background = boxPressed, textColor = Color.white },
            onHover = { background = boxPressed, textColor = Color.white },
            onActive = { background = boxPressed, textColor = Color.white }
        };

        TextField = new GUIStyle(GUI.skin.textField)
        {
            fontSize = 13,
            border = new RectOffset(4, 4, 4, 4),
            padding = new RectOffset(8, 8, 5, 5),
            normal = { background = Rounded(16, 16, 0, 3f, 1f, MapBackground, Line), textColor = Ink },
            hover = { background = Rounded(16, 16, 0, 3f, 1f, MapBackground, Line), textColor = Ink },
            focused = { background = Rounded(16, 16, 0, 3f, 1f, MapBackground, Accent), textColor = Color.white }
        };

        // slim track and a narrow squared thumb, both 16 tall so the thumb sits centered on the track
        Slider = new GUIStyle(GUI.skin.horizontalSlider)
        {
            border = new RectOffset(2, 2, 0, 0),
            fixedHeight = 16,
            margin = new RectOffset(4, 4, 4, 4),
            normal = { background = Rounded(8, 16, 6, 1f, 0f, Track, Track) }
        };
        SliderThumb = new GUIStyle(GUI.skin.horizontalSliderThumb)
        {
            border = new RectOffset(0, 0, 0, 0),
            fixedWidth = 8,
            fixedHeight = 16,
            normal = { background = Rounded(8, 16, 1, 2f, 0f, Accent, Accent) },
            hover = { background = Rounded(8, 16, 1, 2f, 0f, Hex("#73B7FF"), Hex("#73B7FF")) },
            active = { background = Rounded(8, 16, 1, 2f, 0f, Color.white, Color.white) }
        };

        // dark text, since role labels sit on the bright role colors
        MapLabel = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 11,
            fontStyle = FontStyle.Bold,
            normal = { textColor = MapBackground }
        };
        Area = new GUIStyle(GUI.skin.label)
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 22,
            fontStyle = FontStyle.Bold,
            normal = { textColor = Ink }
        };
    }

    // DRAWING HELPERS

    // text with a dark outline around it, readable over any part of the 3D view
    public static void HaloLabel(Rect r, string text, GUIStyle style)
    {
        var color = style.normal.textColor;
        style.normal.textColor = Halo;
        for (int dx = -1; dx <= 1; dx++)
            for (int dy = -1; dy <= 1; dy++)
                if (dx != 0 || dy != 0) GUI.Label(new Rect(r.x + dx, r.y + dy, r.width, r.height), text, style);
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

    // "#RRGGBB" to a color, with an optional alpha
    static Color Hex(string hex, float alpha = 1f)
    {
        ColorUtility.TryParseHtmlString(hex, out var c);
        c.a = alpha;
        return c;
    }

    // w x h texture of a rounded box, inset from the top and bottom, with an outline and soft edges
    // used as a 9-sliced style background, so only the corners need to be drawn at full size
    static Texture2D Rounded(int w, int h, int inset, float radius, float outline, Color fill, Color line)
    {
        var tex = new Texture2D(w, h) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color[w * h];
        float halfW = w / 2f, halfH = h / 2f - inset;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                // signed distance to the box edge, negative inside
                float qx = Mathf.Abs(x + 0.5f - w / 2f) - (halfW - radius);
                float qy = Mathf.Abs(y + 0.5f - h / 2f) - (halfH - radius);
                float d = new Vector2(Mathf.Max(qx, 0f), Mathf.Max(qy, 0f)).magnitude + Mathf.Min(Mathf.Max(qx, qy), 0f) - radius;

                // a one pixel soft edge on the outside, and where the outline meets the fill
                var c = Color.Lerp(line, fill, Mathf.Clamp01(0.5f - d - outline));
                c.a *= Mathf.Clamp01(0.5f - d);
                pixels[y * w + x] = c;
            }
        }
        tex.SetPixels(pixels);
        tex.Apply();
        return tex;
    }
}
