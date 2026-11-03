using UnityEngine;
using UnityEngine.InputSystem;

// Owns the seed + BSP settings and draws the generated layout in 2D:
// gizmos in the Scene view, and a minimap overlay in Play mode
public class baseMap : MonoBehaviour
{
    [Header("Generation")]
    public int seed = 12345;
    public BSPSettings settings = new BSPSettings();

    [Header("Scene View (Gizmos)")]
    public float cellSize = 1f;
    public bool drawPartitions = true;
    public bool drawRooms = true;

    [Header("Play Mode Overlay")]
    public bool showOverlay = true;
    [Min(64)] public int overlaySize = 320;

    static readonly Color BackgroundColor = new Color(0.08f, 0.08f, 0.1f, 0.9f);
    static readonly Color PartitionColor = new Color(0.35f, 0.55f, 0.9f, 1f);
    static readonly Color RoomColor = new Color(0.95f, 0.75f, 0.3f, 1f);

    public BSPLayout Layout { get; private set; }

    Texture2D overlayTexture;
    bool overlayDirty = true;
    string statusMessage;
    float statusUntil;

    void Awake()
    {
        Generate();
    }

    void OnValidate()
    {
        settings.Validate();
        Generate();
    }

    public void Generate()
    {
        Layout = BSPGenerator.Generate(seed, settings);
        overlayDirty = true;
    }

    public void Generate(int newSeed)
    {
        seed = newSeed;
        Generate();
    }

    public void RandomizeSeed()
    {
        Generate(Random.Range(0, int.MaxValue));
    }

    // Generates the current seed twice from scratch and checks both layouts match
    public bool VerifyDeterminism()
    {
        int a = BSPGenerator.Generate(seed, settings).Hash;
        int b = BSPGenerator.Generate(seed, settings).Hash;
        bool match = a == b && (Layout == null || Layout.Hash == a);
        if (match)
            Debug.Log($"[baseMap] Seed {seed} is deterministic (hash {a:X8}).");
        else
            Debug.LogError($"[baseMap] Seed {seed} gave different layouts ({a:X8} vs {b:X8}).");
        return match;
    }

    void Update()
    {
        var kb = Keyboard.current;
        if (kb == null) return;

        if (kb.mKey.wasPressedThisFrame) showOverlay = !showOverlay;
        if (kb.nKey.wasPressedThisFrame)
        {
            RandomizeSeed();
            SetStatus($"New seed {seed} -> hash {Layout.Hash:X8}");
        }
        if (kb.rKey.wasPressedThisFrame)
        {
            int before = Layout.Hash;
            Generate();
            SetStatus(before == Layout.Hash
                ? $"Regenerated seed {seed}: identical layout (hash {Layout.Hash:X8})"
                : $"Regenerated seed {seed}: MISMATCH {before:X8} vs {Layout.Hash:X8}");
        }
    }

    void SetStatus(string message)
    {
        statusMessage = message;
        statusUntil = Time.unscaledTime + 3f;
    }

    void OnDestroy()
    {
        if (overlayTexture != null) Destroy(overlayTexture);
    }

    // 2D OVERLAY FOR MAP VIEW IN PLAY MODE

    void RebuildOverlayTexture()
    {
        int w = settings.width, h = settings.height;
        if (overlayTexture == null || overlayTexture.width != w || overlayTexture.height != h)
        {
            if (overlayTexture != null) Destroy(overlayTexture);
            overlayTexture = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
        }

        var pixels = new Color[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = BackgroundColor;

        foreach (var leaf in Layout.leaves)
        {
            var b = leaf.bounds;
            for (int x = b.xMin; x < b.xMax; x++)
            {
                pixels[b.yMin * w + x] = PartitionColor;
                pixels[(b.yMax - 1) * w + x] = PartitionColor;
            }
            for (int y = b.yMin; y < b.yMax; y++)
            {
                pixels[y * w + b.xMin] = PartitionColor;
                pixels[y * w + b.xMax - 1] = PartitionColor;
            }
        }

        foreach (var r in Layout.rooms)
            for (int y = r.yMin; y < r.yMax; y++)
                for (int x = r.xMin; x < r.xMax; x++)
                    pixels[y * w + x] = RoomColor;

        overlayTexture.SetPixels(pixels);
        overlayTexture.Apply();
        overlayDirty = false;
    }

    void OnGUI()
    {
        if (!showOverlay || Layout == null) return;
        if (overlayDirty || overlayTexture == null) RebuildOverlayTexture();

        float scale = overlaySize / (float)Mathf.Max(settings.width, settings.height);
        float w = settings.width * scale, h = settings.height * scale;
        const float margin = 10f;
        var mapRect = new Rect(Screen.width - w - margin, margin, w, h);

        GUI.DrawTexture(mapRect, overlayTexture);
        var infoRect = new Rect(mapRect.x, mapRect.yMax + 4f, w, 44f);
        GUI.Box(infoRect, GUIContent.none);
        GUI.Label(new Rect(infoRect.x + 6f, infoRect.y + 2f, w - 12f, 20f),
            $"Seed {seed}   Rooms {Layout.rooms.Count}   Hash {Layout.Hash:X8}");
        GUI.Label(new Rect(infoRect.x + 6f, infoRect.y + 22f, w - 12f, 20f),
            "[M] map   [N] new seed   [R] regenerate");

        if (statusMessage != null && Time.unscaledTime < statusUntil)
        {
            var statusRect = new Rect(mapRect.x, infoRect.yMax + 4f, w, 24f);
            GUI.Box(statusRect, GUIContent.none);
            GUI.Label(new Rect(statusRect.x + 6f, statusRect.y + 2f, w - 12f, 20f), statusMessage);
        }
    }

    // ---------- Scene view ----------

    Vector3 CellToWorld(float x, float y)
    {
        return transform.position + new Vector3(x * cellSize, 0f, y * cellSize);
    }

    void DrawRectGizmo(RectInt r, float yOffset, bool solid)
    {
        Vector3 center = CellToWorld(r.center.x, r.center.y) + Vector3.up * yOffset;
        Vector3 size = new Vector3(r.width * cellSize, 0.01f, r.height * cellSize);
        if (solid) Gizmos.DrawCube(center, size);
        else Gizmos.DrawWireCube(center, size);
    }

    void OnDrawGizmos()
    {
        if (Layout == null) return;

        if (drawPartitions)
        {
            foreach (var node in Layout.nodes)
            {
                // Shallow cuts are brighter so the split hierarchy is readable
                float t = settings.maxDepth > 0 ? node.depth / (float)settings.maxDepth : 0f;
                Gizmos.color = Color.Lerp(Color.white, PartitionColor, t);
                DrawRectGizmo(node.bounds, 0f, false);
            }
        }

        if (drawRooms)
        {
            Gizmos.color = new Color(RoomColor.r, RoomColor.g, RoomColor.b, 0.6f);
            foreach (var r in Layout.rooms)
                DrawRectGizmo(r, 0.02f, true);
        }
    }
}
