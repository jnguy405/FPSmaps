using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
#if UNITY_EDITOR
using UnityEditor;
#endif

// owns the seed + BSP settings, draws the layout in 2D and builds it as walkable 3D geometry
// 2D: gizmos in the Scene view, and a minimap overlay in Play mode
// 3D: floor, wall and optional ceiling meshes under a "Generated Map" child, rebuilt whenever the layout changes
[ExecuteAlways]
public class baseMap : MonoBehaviour
{
    [Header("Generation")]
    public int seed = 12345;
    public BSPSettings settings = new BSPSettings();
    [Tooltip("When leaving Play mode, keep the seed you were on instead of reverting it.")]
    public bool keepPlayModeSeed = true;

    [Header("Map Constraints")]
    public MapConstraints constraints = new MapConstraints();

    [Header("3D Build")]
    public bool build3D = true;
    [Tooltip("World size of one grid cell.")]
    public float cellSize = 1f;
    public float wallHeight = 4f;
    public bool buildCeiling = false;
    [Tooltip("Texture repeats per world unit.")]
    public float uvScale = 0.5f;
    public Material floorMaterial;
    public Material wallMaterial;
    public Material ceilingMaterial;
    [Tooltip("Move the player into the attacker spawn (or the first room) on Play and on a new seed.")]
    public bool spawnPlayerInMap = true;

    [Header("Scene View (Gizmos)")]
    public bool drawPartitions = true;
    public bool drawRooms = true;
    public bool drawCorridors = true;

    [Header("Play Mode Overlay")]
    public bool showOverlay = true;
    [Min(64)] public int overlaySize = 320;

    static readonly Color BackgroundColor = new Color(0.08f, 0.08f, 0.1f, 0.9f);
    static readonly Color PartitionColor = new Color(0.35f, 0.55f, 0.9f, 1f);
    static readonly Color RoomColor = new Color(0.95f, 0.75f, 0.3f, 1f);
    static readonly Color CorridorColor = new Color(0.55f, 0.85f, 0.75f, 1f);
    static readonly Color PlayerColor = new Color(0.95f, 0.3f, 0.3f, 1f);

    // role colors differ in lightness as well as hue so they stay readable on the minimap
    static readonly Color AttackerColor = new Color(0.95f, 0.45f, 0.2f, 1f);
    static readonly Color DefenderColor = new Color(0.25f, 0.45f, 0.95f, 1f);
    static readonly Color MidColor = new Color(0.75f, 0.55f, 0.95f, 1f);
    static readonly Color SiteColor = new Color(0.35f, 0.85f, 0.4f, 1f);

    const string GeneratedRootName = "Generated Map";

    // the solved map: layout plus room roles and the constraint report
    public MapPlan Plan { get; private set; }
    public BSPLayout Layout => Plan?.layout;

    Texture2D overlayTexture;
    bool overlayDirty = true;   // set when the layout changes so the minimap redraws once
    string statusMessage;
    float statusUntil;
    GUIStyle centeredLabel;

    Mesh floorMesh, wallMesh, ceilingMesh;
    FirstPersonController player;
    MapUI ui;

    // runs in edit mode too (ExecuteAlways), so the map appears as soon as the scene opens
    void OnEnable()
    {
        Generate();
    }

    // meshes aren't saved with the scene, so free them when this component goes away
    void OnDisable()
    {
        DestroyMesh(ref floorMesh);
        DestroyMesh(ref wallMesh);
        DestroyMesh(ref ceilingMesh);
    }

    void Start()
    {
        // Start also fires in edit mode because of ExecuteAlways, only spawn during Play
        if (!Application.isPlaying) return;
        player = FindAnyObjectByType<FirstPersonController>();
        SpawnPlayer();

        // the in-game generator panel, added here so older scenes get it without any setup
        ui = GetComponent<MapUI>();
        if (ui == null) ui = gameObject.AddComponent<MapUI>();
    }

    // called whenever a value changes in the inspector
    void OnValidate()
    {
        settings.Validate();
#if UNITY_EDITOR
        AssignDefaultMaterials();
        // Unity does not allow creating objects inside OnValidate, so rebuild on the next editor tick
        // remove first so dragging a slider queues one rebuild, not dozens
        EditorApplication.delayCall -= DelayedGenerate;
        EditorApplication.delayCall += DelayedGenerate;
#endif
    }

#if UNITY_EDITOR
    void DelayedGenerate()
    {
        // the object may have been deleted or disabled before the delayed call ran
        if (this != null && isActiveAndEnabled) Generate();
    }

    // falls back to the materials made by FPSmaps > Build Test Scene
    void AssignDefaultMaterials()
    {
        if (floorMaterial == null) floorMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Floor.mat");
        if (wallMaterial == null) wallMaterial = AssetDatabase.LoadAssetAtPath<Material>("Assets/Materials/Wall.mat");
        if (ceilingMaterial == null) ceilingMaterial = wallMaterial;
    }
#endif

    // rebuilds everything from the current seed: layout and roles, then minimap, then 3D
    public void Generate()
    {
        Plan = MapConstraintSolver.Solve(seed, settings, constraints);
        overlayDirty = true;
        BuildGeometry();
    }

    public void Generate(int newSeed)
    {
        seed = newSeed;
        Generate();
    }

    // UnityEngine.Random is fine here, picking a seed doesn't need to be repeatable
    public void RandomizeSeed()
    {
        Generate(Random.Range(0, int.MaxValue));
    }

    // solves the current seed twice from scratch and checks both plans match, roles included
    public bool VerifyDeterminism()
    {
        int a = MapConstraintSolver.Solve(seed, settings, constraints).Hash;
        int b = MapConstraintSolver.Solve(seed, settings, constraints).Hash;

        // also compare against the plan already on screen
        bool match = a == b && (Plan == null || Plan.Hash == a);
        if (match)
            Debug.Log($"[baseMap] Seed {seed} is deterministic (hash {a:X8}).");
        else
            Debug.LogError($"[baseMap] Seed {seed} gave different layouts ({a:X8} vs {b:X8}).");
        return match;
    }

    void Update()
    {
        // keyboard shortcuts only make sense while playing
        if (!Application.isPlaying) return;
        var kb = Keyboard.current;
        if (kb == null) return;

        // while the panel is open, letters belong to its text field
        if (ui != null && ui.IsOpen) return;

        // M toggles the minimap, N rolls a new map, R rebuilds the current one
        if (kb.mKey.wasPressedThisFrame) showOverlay = !showOverlay;
        if (kb.nKey.wasPressedThisFrame) NewMap();
        if (kb.rKey.wasPressedThisFrame) RegenerateAndCheck();
    }

    // moves the player into the new map too, since the old position may now be inside a wall
    void NewMap()
    {
        RandomizeSeed();
        SpawnPlayer();
        SetStatus($"New seed {seed} -> hash {Plan.Hash:X8}");
    }

    // rebuilds the same seed, the hash should not change
    void RegenerateAndCheck()
    {
        int before = Plan.Hash;
        Generate();
        SetStatus(before == Plan.Hash
            ? $"Regenerated seed {seed}: identical layout (hash {Plan.Hash:X8})"
            : $"Regenerated seed {seed}: MISMATCH {before:X8} vs {Plan.Hash:X8}");
    }

    // shows a message under the minimap for 3 seconds
    void SetStatus(string message)
    {
        statusMessage = message;
        statusUntil = Time.unscaledTime + 3f;
    }

    void OnDestroy()
    {
        if (overlayTexture != null) SafeDestroy(overlayTexture);
    }

    // 3D BUILD FROM THE 2D LAYOUT

    void BuildGeometry()
    {
        // all generated parts live under one child so they're easy to find, hide or replace
        var root = GetOrCreateChild(transform, GeneratedRootName);
        root.gameObject.SetActive(build3D);
        if (!build3D) return;

        // fill the meshes from the grid, then hook them up to renderers and colliders
        MapMeshBuilder.BuildFloor(Layout, cellSize, uvScale, EnsureMesh(ref floorMesh, "Map Floor"));
        MapMeshBuilder.BuildWalls(Layout, cellSize, wallHeight, uvScale, EnsureMesh(ref wallMesh, "Map Walls"));
        SetupPart(root, "Floor", floorMesh, floorMaterial);
        SetupPart(root, "Walls", wallMesh, wallMaterial);

        if (buildCeiling)
        {
            MapMeshBuilder.BuildCeiling(Layout, cellSize, wallHeight, uvScale, EnsureMesh(ref ceilingMesh, "Map Ceiling"));
            SetupPart(root, "Ceiling", ceilingMesh, ceilingMaterial).SetActive(true);
        }
        else
        {
            // hide rather than delete so turning the ceiling back on is cheap
            var ceiling = root.Find("Ceiling");
            if (ceiling != null) ceiling.gameObject.SetActive(false);
        }
    }

    // meshes are rebuilt from the seed every time, so they are never saved into the scene
    static Mesh EnsureMesh(ref Mesh mesh, string name)
    {
        if (mesh == null) mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        return mesh;
    }

    // makes sure a child has a filter, renderer and collider all pointing at the given mesh
    static GameObject SetupPart(Transform root, string name, Mesh mesh, Material material)
    {
        var go = GetOrCreateChild(root, name).gameObject;
        GetOrAdd<MeshFilter>(go).sharedMesh = mesh;
        GetOrAdd<MeshRenderer>(go).sharedMaterial = material;

        // clear first so the collider re-cooks the changed mesh
        var collider = GetOrAdd<MeshCollider>(go);
        collider.sharedMesh = null;
        collider.sharedMesh = mesh;
        return go;
    }

    // reuses an existing child by name so rebuilding doesn't pile up duplicates
    static Transform GetOrCreateChild(Transform parent, string name)
    {
        var child = parent.Find(name);
        if (child != null) return child;
        child = new GameObject(name).transform;
        child.SetParent(parent, false);   // false keeps it at the parent's origin
        return child;
    }

    static T GetOrAdd<T>(GameObject go) where T : Component
    {
        // explicit null check because Unity objects override == null
        var c = go.GetComponent<T>();
        return c != null ? c : go.AddComponent<T>();
    }

    static void DestroyMesh(ref Mesh mesh)
    {
        if (mesh == null) return;
        SafeDestroy(mesh);
        mesh = null;
    }

    // Destroy only works in Play mode, edit mode needs DestroyImmediate
    static void SafeDestroy(Object obj)
    {
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }

    // puts the player in the middle of the attacker spawn, or the first room if there are no roles
    public void SpawnPlayer()
    {
        if (!spawnPlayerInMap || !build3D || player == null || Layout == null || Layout.rooms.Count == 0) return;

        // lift slightly so the capsule starts just above the floor instead of inside it
        var r = Layout.rooms[Mathf.Max(0, Plan.attackerSpawn)];
        Vector3 pos = CellToWorld(r.center.x, r.center.y) + Vector3.up * 0.05f;
        player.Teleport(pos, player.transform.rotation);

        // falling out of the map now respawns here too
        player.SetSpawn(pos, player.transform.rotation);
    }

    // what a world position is standing in: a role name, "Room", "Corridor", or null outside the map
    public string AreaNameAt(Vector3 worldPos)
    {
        if (Layout == null) return null;
        Vector3 local = transform.InverseTransformPoint(worldPos) / cellSize;
        var cell = new Vector2Int(Mathf.FloorToInt(local.x), Mathf.FloorToInt(local.z));
        if (!Layout.IsFloor(cell.x, cell.y)) return null;

        // rooms win over corridors, since corridor cells inside a room are part of it
        for (int r = 0; r < Layout.rooms.Count; r++)
            if (Layout.rooms[r].Contains(cell)) return Plan.FullName(r) ?? "Room";
        return "Corridor";
    }

    // 2D OVERLAY FOR MAP VIEW IN PLAY MODE

    void RebuildOverlayTexture()
    {
        int w = Layout.width, h = Layout.height;
        EnsureOverlayTexture(w, h);

        // start with a dark background everywhere
        var pixels = new Color[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = BackgroundColor;

        // outline each leaf partition, then paint floor cells over them, then tint rooms by role
        foreach (var leaf in Layout.leaves) PaintOutline(pixels, w, leaf.bounds, PartitionColor);

        // the cell grid and pixel array share the same y * width + x indexing, so copy straight across
        for (int i = 0; i < pixels.Length; i++)
        {
            if (Layout.cells[i] == CellType.Room) pixels[i] = RoomColor;
            else if (Layout.cells[i] == CellType.Corridor) pixels[i] = CorridorColor;
        }

        // repaint each room in its role color (rooms without a role keep the plain room color)
        for (int r = 0; r < Layout.rooms.Count; r++)
            PaintRect(pixels, w, Layout.rooms[r], RoomColorFor(r));

        overlayTexture.SetPixels(pixels);
        overlayTexture.Apply();   // uploads the pixels to the GPU
        overlayDirty = false;
    }

    // one texture pixel per grid cell, scaled up on screen with point filtering
    void EnsureOverlayTexture(int w, int h)
    {
        if (overlayTexture != null && overlayTexture.width == w && overlayTexture.height == h) return;

        // map size changed, so the old texture can't be reused
        if (overlayTexture != null) SafeDestroy(overlayTexture);
        overlayTexture = new Texture2D(w, h, TextureFormat.RGBA32, false)
        {
            filterMode = FilterMode.Point,   // crisp pixels instead of blur
            wrapMode = TextureWrapMode.Clamp
        };
    }

    // top and bottom rows, then left and right columns
    static void PaintOutline(Color[] pixels, int w, RectInt b, Color color)
    {
        for (int x = b.xMin; x < b.xMax; x++)
        {
            pixels[b.yMin * w + x] = color;
            pixels[(b.yMax - 1) * w + x] = color;
        }
        for (int y = b.yMin; y < b.yMax; y++)
        {
            pixels[y * w + b.xMin] = color;
            pixels[y * w + b.xMax - 1] = color;
        }
    }

    static void PaintRect(Color[] pixels, int w, RectInt r, Color color)
    {
        for (int y = r.yMin; y < r.yMax; y++)
            for (int x = r.xMin; x < r.xMax; x++)
                pixels[y * w + x] = color;
    }

    // color for a room's role, or the plain room color if it has none
    Color RoomColorFor(int room)
    {
        if (Plan.roles == null) return RoomColor;
        switch (Plan.roles[room])
        {
            case RoomRole.AttackerSpawn: return AttackerColor;
            case RoomRole.DefenderSpawn: return DefenderColor;
            case RoomRole.Mid: return MidColor;
            case RoomRole.Site: return SiteColor;
            default: return RoomColor;
        }
    }

    void OnGUI()
    {
        if (!Application.isPlaying || !showOverlay || Layout == null) return;

        // only rebuild the texture when the layout changed, not every frame
        if (overlayDirty || overlayTexture == null) RebuildOverlayTexture();

        // fit the longer side to overlaySize and pin to the top-right corner
        float scale = overlaySize / (float)Mathf.Max(Layout.width, Layout.height);
        float w = Layout.width * scale, h = Layout.height * scale;
        const float margin = 10f;
        var mapRect = new Rect(Screen.width - w - margin, margin, w, h);

        GUI.DrawTexture(mapRect, overlayTexture);
        DrawRoleLabels(mapRect, scale);
        DrawPlayerMarker(mapRect, scale);
        DrawInfoBox(mapRect);
    }

    // info box under the map: seed/hash, room counts, constraint result, key hints, then the temporary status line
    void DrawInfoBox(Rect mapRect)
    {
        var infoRect = new Rect(mapRect.x, mapRect.yMax + 4f, mapRect.width, 84f);
        GUI.Box(infoRect, GUIContent.none);
        InfoLine(infoRect, 0, $"Seed {seed}   Hash {Plan.Hash:X8}");
        InfoLine(infoRect, 1, $"Rooms {Layout.rooms.Count}   Reachable {Layout.reachableRooms}/{Layout.rooms.Count}");
        InfoLine(infoRect, 2, ConstraintSummary());
        InfoLine(infoRect, 3, "[M] map   [N] new seed   [R] regenerate");

        // status from N or R, hidden once its time runs out
        if (statusMessage != null && Time.unscaledTime < statusUntil)
        {
            var statusRect = new Rect(mapRect.x, infoRect.yMax + 4f, mapRect.width, 24f);
            GUI.Box(statusRect, GUIContent.none);
            InfoLine(statusRect, 0, statusMessage);
        }
    }

    // label on a given row of a box, 20 pixels per row with a small inset from the edges
    static void InfoLine(Rect box, int row, string text)
    {
        GUI.Label(new Rect(box.x + 6f, box.y + 2f + row * 20f, box.width - 12f, 20f), text);
    }

    // one line for the minimap, e.g. "Constraints OK (attempt 3)" or "Constraints: 2 failed"
    public string ConstraintSummary()
    {
        if (!constraints.enabled) return "Constraints off";
        if (!Plan.Solved) return $"Constraints: {Plan.failures.Count} failed";
        return Plan.attempt == 0 ? "Constraints OK" : $"Constraints OK (attempt {Plan.attempt})";
    }

    // ATK, DEF, MID and site letters drawn over their rooms
    void DrawRoleLabels(Rect mapRect, float scale)
    {
        for (int r = 0; r < Layout.rooms.Count; r++)
        {
            string label = Plan.Label(r);
            if (label == null) continue;

            // same flip as the player marker, cells go up in +Z and GUI rows go down
            var c = Layout.rooms[r].center;
            float px = mapRect.x + c.x * scale;
            float py = mapRect.yMax - c.y * scale;
            GUI.Label(new Rect(px - 16f, py - 10f, 32f, 20f), label, CenteredLabel);
        }
    }

    // built on first use, GUI styles can only be created inside OnGUI
    GUIStyle CenteredLabel => centeredLabel ??= new GUIStyle(GUI.skin.label)
    {
        alignment = TextAnchor.MiddleCenter,
        fontStyle = FontStyle.Bold,
        normal = { textColor = Color.black }
    };

    void DrawPlayerMarker(Rect mapRect, float scale)
    {
        if (player == null) return;

        // player position in grid cells, relative to the map object
        // map cells go up in +Z, GUI rows go down, so flip the vertical axis
        Vector3 local = transform.InverseTransformPoint(player.transform.position) / cellSize;
        float px = mapRect.x + local.x * scale;
        float py = mapRect.yMax - local.z * scale;

        // don't draw the dot when the player is outside the map (e.g. in the test room)
        if (!mapRect.Contains(new Vector2(px, py))) return;

        // tint a white square, then restore the GUI color for anything drawn after
        const float size = 6f;
        var old = GUI.color;
        GUI.color = PlayerColor;
        GUI.DrawTexture(new Rect(px - size / 2f, py - size / 2f, size, size), Texture2D.whiteTexture);
        GUI.color = old;
    }

    // SCENE VIEW

    // grid cell (x, y) to a world position on the map's floor plane
    Vector3 CellToWorld(float x, float y)
    {
        return transform.TransformPoint(new Vector3(x * cellSize, 0f, y * cellSize));
    }

    void DrawRectGizmo(RectInt r, float yOffset, bool solid)
    {
        // Gizmos.matrix is the map's transform, so this is in local space
        // flattened box, 0.01 tall, so it reads as a 2D rectangle
        Vector3 center = new Vector3(r.center.x * cellSize, yOffset, r.center.y * cellSize);
        Vector3 size = new Vector3(r.width * cellSize, 0.01f, r.height * cellSize);
        if (solid) Gizmos.DrawCube(center, size);
        else Gizmos.DrawWireCube(center, size);
    }

    void OnDrawGizmos()
    {
        if (Layout == null) return;
        Gizmos.matrix = transform.localToWorldMatrix;

        // every node in the tree, so you can see each level of cuts
        if (drawPartitions)
        {
            foreach (var node in Layout.nodes)
            {
                // shallow cuts are brighter so the split hierarchy is readable
                float t = settings.maxDepth > 0 ? node.depth / (float)settings.maxDepth : 0f;
                Gizmos.color = Color.Lerp(Color.white, PartitionColor, t);
                DrawRectGizmo(node.bounds, 0f, false);
            }
        }

        // small height offsets stack corridors above partitions and rooms above corridors
        if (drawCorridors) DrawFilledGizmos(Layout.corridors, CorridorColor, 0.02f);
        if (drawRooms) DrawRoomGizmos();
    }

    // rooms in their role colors, with the role written above each one in the editor
    void DrawRoomGizmos()
    {
        for (int r = 0; r < Layout.rooms.Count; r++)
        {
            var color = RoomColorFor(r);
            Gizmos.color = new Color(color.r, color.g, color.b, 0.6f);
            DrawRectGizmo(Layout.rooms[r], 0.03f, true);

#if UNITY_EDITOR
            // Handles draws text in world space, so convert the room center first
            string label = Plan.Label(r);
            var c = Layout.rooms[r].center;
            if (label != null) Handles.Label(CellToWorld(c.x, c.y) + Vector3.up * 0.5f, label);
#endif
        }
    }

    // solid, semi-transparent rects all in one color
    void DrawFilledGizmos(List<RectInt> rects, Color color, float yOffset)
    {
        Gizmos.color = new Color(color.r, color.g, color.b, 0.6f);
        foreach (var r in rects)
            DrawRectGizmo(r, yOffset, true);
    }
}
