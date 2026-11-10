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
    [Tooltip("Move the player into the first room on Play and on a new seed.")]
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

    const string GeneratedRootName = "Generated Map";

    public BSPLayout Layout { get; private set; }

    Texture2D overlayTexture;
    bool overlayDirty = true;   // set when the layout changes so the minimap redraws once
    string statusMessage;
    float statusUntil;

    Mesh floorMesh, wallMesh, ceilingMesh;
    FirstPersonController player;

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

    // rebuilds everything from the current seed: layout data, then minimap, then 3D
    public void Generate()
    {
        Layout = BSPGenerator.Generate(seed, settings);
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

    // generates the current seed twice from scratch and checks both layouts match
    public bool VerifyDeterminism()
    {
        int a = BSPGenerator.Generate(seed, settings).Hash;
        int b = BSPGenerator.Generate(seed, settings).Hash;

        // also compare against the layout already on screen
        bool match = a == b && (Layout == null || Layout.Hash == a);
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

        // M toggles the minimap
        if (kb.mKey.wasPressedThisFrame) showOverlay = !showOverlay;

        // N rolls a new map and moves the player into it, since the old position may now be inside a wall
        if (kb.nKey.wasPressedThisFrame)
        {
            RandomizeSeed();
            SpawnPlayer();
            SetStatus($"New seed {seed} -> hash {Layout.Hash:X8}");
        }

        // R rebuilds the same seed, the hash should not change
        if (kb.rKey.wasPressedThisFrame)
        {
            int before = Layout.Hash;
            Generate();
            SetStatus(before == Layout.Hash
                ? $"Regenerated seed {seed}: identical layout (hash {Layout.Hash:X8})"
                : $"Regenerated seed {seed}: MISMATCH {before:X8} vs {Layout.Hash:X8}");
        }
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

    // puts the player in the middle of the first room
    public void SpawnPlayer()
    {
        if (!spawnPlayerInMap || !build3D || player == null || Layout == null || Layout.rooms.Count == 0) return;

        // lift slightly so the capsule starts just above the floor instead of inside it
        var r = Layout.rooms[0];
        Vector3 pos = CellToWorld(r.center.x, r.center.y) + Vector3.up * 0.05f;
        player.Teleport(pos, player.transform.rotation);

        // falling out of the map now respawns here too
        player.SetSpawn(pos, player.transform.rotation);
    }

    // 2D OVERLAY FOR MAP VIEW IN PLAY MODE

    void RebuildOverlayTexture()
    {
        // one texture pixel per grid cell, scaled up on screen with point filtering
        int w = Layout.width, h = Layout.height;
        if (overlayTexture == null || overlayTexture.width != w || overlayTexture.height != h)
        {
            // map size changed, so the old texture can't be reused
            if (overlayTexture != null) SafeDestroy(overlayTexture);
            overlayTexture = new Texture2D(w, h, TextureFormat.RGBA32, false)
            {
                filterMode = FilterMode.Point,   // crisp pixels instead of blur
                wrapMode = TextureWrapMode.Clamp
            };
        }

        // start with a dark background everywhere
        var pixels = new Color[w * h];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = BackgroundColor;

        // outline each leaf partition: top and bottom rows, then left and right columns
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

        // the cell grid and pixel array share the same y * width + x indexing, so copy straight across
        for (int i = 0; i < pixels.Length; i++)
        {
            if (Layout.cells[i] == CellType.Room) pixels[i] = RoomColor;
            else if (Layout.cells[i] == CellType.Corridor) pixels[i] = CorridorColor;
        }

        overlayTexture.SetPixels(pixels);
        overlayTexture.Apply();   // uploads the pixels to the GPU
        overlayDirty = false;
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
        DrawPlayerMarker(mapRect, scale);

        // info box under the map: seed/hash, room counts, key hints
        var infoRect = new Rect(mapRect.x, mapRect.yMax + 4f, w, 64f);
        GUI.Box(infoRect, GUIContent.none);
        GUI.Label(new Rect(infoRect.x + 6f, infoRect.y + 2f, w - 12f, 20f),
            $"Seed {seed}   Hash {Layout.Hash:X8}");
        GUI.Label(new Rect(infoRect.x + 6f, infoRect.y + 22f, w - 12f, 20f),
            $"Rooms {Layout.rooms.Count}   Reachable {Layout.reachableRooms}/{Layout.rooms.Count}");
        GUI.Label(new Rect(infoRect.x + 6f, infoRect.y + 42f, w - 12f, 20f),
            "[M] map   [N] new seed   [R] regenerate");

        // temporary status line from N or R, hidden once its time runs out
        if (statusMessage != null && Time.unscaledTime < statusUntil)
        {
            var statusRect = new Rect(mapRect.x, infoRect.yMax + 4f, w, 24f);
            GUI.Box(statusRect, GUIContent.none);
            GUI.Label(new Rect(statusRect.x + 6f, statusRect.y + 2f, w - 12f, 20f), statusMessage);
        }
    }

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
        if (drawCorridors)
        {
            Gizmos.color = new Color(CorridorColor.r, CorridorColor.g, CorridorColor.b, 0.6f);
            foreach (var c in Layout.corridors)
                DrawRectGizmo(c, 0.02f, true);
        }

        if (drawRooms)
        {
            Gizmos.color = new Color(RoomColor.r, RoomColor.g, RoomColor.b, 0.6f);
            foreach (var r in Layout.rooms)
                DrawRectGizmo(r, 0.03f, true);
        }
    }
}
