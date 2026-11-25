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
    [Range(2f, 6f)] public float wallHeight = 3f;
    public bool buildCeiling = false;
    [Tooltip("Solid-looking wall tops and outer faces, only visible from the designer view.")]
    public bool buildWallBlocks = true;
    [Tooltip("Texture repeats per world unit.")]
    public float uvScale = 0.5f;
    public Material floorMaterial;
    public Material wallMaterial;
    public Material ceilingMaterial;
    [Tooltip("How strongly spawn, mid and site floors are tinted with their role color.")]
    [Range(0f, 1f)] public float roleTint = 0.4f;

    [Header("Surfaces")]
    [Tooltip("Assigned: the materials above as they are. Fixed: the noise textures chosen below. Random: picked from the seed, together with any extra materials.")]
    public SurfaceMode surfaceMode = SurfaceMode.RandomFromSeed;
    public SurfaceKind floorSurface = SurfaceKind.SandstoneTiles;
    public SurfaceKind wallSurface = SurfaceKind.Sandstone;
    [Tooltip("Your own materials, added to the random pick alongside the noise textures.")]
    public Material[] extraFloorMaterials;
    public Material[] extraWallMaterials;
    [Tooltip("Move the player into the attacker spawn (or the first room) on Play and on a new seed.")]
    public bool spawnPlayerInMap = true;

    [Header("Scene View (Gizmos)")]
    public bool drawPartitions = true;
    public bool drawRooms = true;
    public bool drawCorridors = true;

    [Header("Play Mode Overlay")]
    public bool showOverlay = true;
    [Min(64)] public int overlaySize = 320;

    const string GeneratedRootName = "Generated Map";

    // floor submeshes, one material each: plain room, corridor, then the four roles
    const int FloorRoom = 0, FloorCorridor = 1, FloorGroupCount = 6;

    // the solved map: layout plus room roles and the constraint report
    public MapPlan Plan { get; private set; }
    public BSPLayout Layout => Plan?.layout;

    Texture2D overlayTexture;
    bool overlayDirty = true;   // set when the layout changes so the minimap redraws once
    string statusMessage;
    float statusUntil;
    GUIStyle rightAligned;

    Mesh floorMesh, wallMesh, ceilingMesh, wallBlockMesh;
    Material[] floorMaterials;      // tinted copies made at runtime, never saved
    Material wallTopMaterial;
    Material floorSource, wallSource;                        // the floor and wall this build uses
    readonly List<Material> generatedSurfaces = new List<Material>();   // noise-textured copies, never saved

    // what the current map is surfaced with, e.g. "Sandstone floor, Blue-grey concrete walls"
    public string SurfaceSummary { get; private set; } = "";
    FirstPersonController player;
    MapUI ui;
    DesignerCamera designer;

    // runs in edit mode too (ExecuteAlways), so the map appears as soon as the scene opens
    void OnEnable()
    {
        Generate();
    }

    // meshes and tinted materials aren't saved with the scene, so free them when this component goes away
    void OnDisable()
    {
        DestroyMesh(ref floorMesh);
        DestroyMesh(ref wallMesh);
        DestroyMesh(ref ceilingMesh);
        DestroyMesh(ref wallBlockMesh);
        DestroyMaterials();
    }

    void Start()
    {
        // Start also fires in edit mode because of ExecuteAlways, only spawn during Play
        if (!Application.isPlaying) return;
        player = FindAnyObjectByType<FirstPersonController>();
        SpawnPlayer();

        // the in-game panel and designer camera, added here so older scenes get them without any setup
        ui = GetComponent<MapUI>();
        if (ui == null) ui = gameObject.AddComponent<MapUI>();
        designer = GetComponent<DesignerCamera>();
        if (designer == null) designer = gameObject.AddComponent<DesignerCamera>();
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
        if (PanelOpen) return;

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

    // VIEW STATE

    public bool PanelOpen => ui != null && ui.IsOpen;
    public bool DesignerActive => designer != null && designer.IsActive;

    // the player only gets the mouse and keyboard when no panel or designer view is using them
    public void RefreshPlayerInput()
    {
        if (player != null) player.SetInputEnabled(!PanelOpen && !DesignerActive);
    }

    public void ToggleDesignerView()
    {
        if (designer != null) designer.SetActive(!designer.IsActive);
    }

    public Vector3 MapCenterWorld => CellToWorld(Layout.width / 2f, Layout.height / 2f);
    public float MapSizeWorld => Mathf.Max(Layout.width, Layout.height) * cellSize;

    // 3D BUILD FROM THE 2D LAYOUT

    // rebuilds only the meshes, e.g. after the wall height changes, without solving the map again
    public void RebuildGeometry()
    {
        if (Layout != null) BuildGeometry();
    }

    void BuildGeometry()
    {
        // all generated parts live under one child so they're easy to find, hide or replace
        var root = GetOrCreateChild(transform, GeneratedRootName);
        root.gameObject.SetActive(build3D);
        if (!build3D) return;
        RefreshMaterials();

        // fill the meshes from the grid, then hook them up to renderers and colliders
        var groups = FloorGroups();
        MapMeshBuilder.BuildFloor(Layout, cellSize, uvScale, EnsureMesh(ref floorMesh, "Map Floor"),
            cell => groups[Layout.Index(cell.x, cell.y)], FloorGroupCount);
        MapMeshBuilder.BuildWalls(Layout, cellSize, wallHeight, uvScale, EnsureMesh(ref wallMesh, "Map Walls"));
        SetupPart(root, "Floor", floorMesh, floorMaterials, true);
        SetupPart(root, "Walls", wallMesh, new[] { wallSource }, true);

        // optional parts are hidden rather than deleted so turning them back on is cheap
        if (buildWallBlocks)
        {
            MapMeshBuilder.BuildWallBlocks(Layout, cellSize, wallHeight, uvScale, EnsureMesh(ref wallBlockMesh, "Map Wall Blocks"));
            SetupPart(root, "Wall Blocks", wallBlockMesh, new[] { wallTopMaterial, wallSource }, false).SetActive(true);
        }
        else HidePart(root, "Wall Blocks");

        // the ceiling matches the walls unless the assigned materials are in use
        if (buildCeiling)
        {
            var ceiling = surfaceMode == SurfaceMode.Assigned ? ceilingMaterial : wallSource;
            MapMeshBuilder.BuildCeiling(Layout, cellSize, wallHeight, uvScale, EnsureMesh(ref ceilingMesh, "Map Ceiling"));
            SetupPart(root, "Ceiling", ceilingMesh, new[] { ceiling }, true).SetActive(true);
        }
        else HidePart(root, "Ceiling");
    }

    // floor submesh for every cell: corridors and plain rooms first, then rooms with a role on top
    int[] FloorGroups()
    {
        var groups = new int[Layout.cells.Length];
        for (int i = 0; i < groups.Length; i++)
            groups[i] = Layout.cells[i] == CellType.Corridor ? FloorCorridor : FloorRoom;

        if (Plan.roles == null) return groups;
        for (int r = 0; r < Layout.rooms.Count; r++)
        {
            if (Plan.roles[r] == RoomRole.None) continue;

            // role order matches the material slots: AttackerSpawn = 2, DefenderSpawn = 3, Mid = 4, Site = 5
            int group = FloorCorridor + (int)Plan.roles[r];
            var rect = Layout.rooms[r];
            for (int y = rect.yMin; y < rect.yMax; y++)
                for (int x = rect.xMin; x < rect.xMax; x++)
                    groups[Layout.Index(x, y)] = group;
        }
        return groups;
    }

    // tinted copies of the chosen surfaces, so the original assets are never changed
    // copying keeps the shader, which a build needs (Shader.Find can miss shaders nothing references)
    // remade on every build so they pick up any change to the source materials (e.g. new textures)
    void RefreshMaterials()
    {
        DestroyMaterials();
        PickSurfaces();
        floorMaterials = new Material[FloorGroupCount];
        for (int i = 1; i < FloorGroupCount; i++) floorMaterials[i] = Copy(floorSource);

        // slot 0 is the plain room floor, the source itself
        floorMaterials[FloorRoom] = floorSource;
        Tint(floorMaterials[FloorCorridor], floorSource, Color.black, 0.2f);
        Tint(floorMaterials[2], floorSource, MapStyle.Attacker, roleTint);
        Tint(floorMaterials[3], floorSource, MapStyle.Defender, roleTint);
        Tint(floorMaterials[4], floorSource, MapStyle.Mid, roleTint);
        Tint(floorMaterials[5], floorSource, MapStyle.Site, roleTint);

        // wall tops a bit darker than the walls, so outlines read clearly from above
        wallTopMaterial = Copy(wallSource);
        Tint(wallTopMaterial, wallSource, Color.black, 0.45f);
    }

    // picks this build's floor and wall: the assigned materials, the fixed noise textures,
    // or a pick from the seed out of the noise textures plus any extra materials
    void PickSurfaces()
    {
        floorSource = floorMaterial;
        wallSource = wallMaterial;
        SurfaceSummary = "Assigned materials";
        if (surfaceMode == SurfaceMode.Assigned) return;

        // its own rng off the seed, so the pick is repeatable and never changes the layout
        var rng = new System.Random(unchecked(seed ^ 0x5F7A3));
        var floor = surfaceMode == SurfaceMode.Fixed
            ? new SurfaceChoice(floorSurface)
            : SurfaceChoice.Pick(rng, SurfaceTextures.FloorKinds, extraFloorMaterials);
        var wall = surfaceMode == SurfaceMode.Fixed
            ? new SurfaceChoice(wallSurface)
            : SurfaceChoice.Pick(rng, SurfaceTextures.WallKinds, extraWallMaterials);

        floorSource = SurfaceMaterial(floor, floorMaterial);
        wallSource = SurfaceMaterial(wall, wallMaterial);
        SurfaceSummary = $"{floor.Name} floor, {wall.Name} walls";
    }

    // a passed-in material is used as it is, plain keeps the template, and a noise kind gets a copy
    // of the template with its texture (white base color, so the texture shows its real colors
    // and role tints still blend over it)
    Material SurfaceMaterial(SurfaceChoice choice, Material template)
    {
        if (choice.plain) return template;
        if (choice.material != null) return choice.material;
        if (template == null) return null;

        var m = new Material(template) { name = SurfaceTextures.Name(choice.kind), hideFlags = HideFlags.DontSave };
        var tex = SurfaceTextures.Get(choice.kind, choice.variant);
        float scale = SurfaceTextures.TileScale(choice.kind);
        foreach (var prop in new[] { "_BaseMap", "_MainTex" })
        {
            if (!m.HasProperty(prop)) continue;
            m.SetTexture(prop, tex);
            m.SetTextureScale(prop, new Vector2(scale, scale));
        }
        foreach (var prop in new[] { "_BaseColor", "_Color" })
            if (m.HasProperty(prop)) m.SetColor(prop, Color.white);

        // stone and concrete are matte
        if (m.HasProperty("_Smoothness")) m.SetFloat("_Smoothness", 0.12f);
        generatedSurfaces.Add(m);
        return m;
    }

    static Material Copy(Material source)
    {
        if (source == null) return null;
        return new Material(source) { name = source.name + " (Tinted)", hideFlags = HideFlags.DontSave };
    }

    // sets target's color to source's color blended toward tint
    // URP Lit uses _BaseColor, the built-in pipeline uses _Color, so handle both
    static void Tint(Material target, Material source, Color tint, float amount)
    {
        if (target == null || source == null) return;
        foreach (var prop in new[] { "_BaseColor", "_Color" })
        {
            if (!source.HasProperty(prop)) continue;
            var c = Color.Lerp(source.GetColor(prop), tint, amount);
            target.SetColor(prop, new Color(c.r, c.g, c.b, 1f));
        }
    }

    void DestroyMaterials()
    {
        if (floorMaterials != null)
        {
            // slot 0 is the original asset, never destroy that one
            for (int i = 1; i < floorMaterials.Length; i++)
                if (floorMaterials[i] != null) SafeDestroy(floorMaterials[i]);
            floorMaterials = null;
        }
        if (wallTopMaterial != null) SafeDestroy(wallTopMaterial);
        wallTopMaterial = null;

        // the textures themselves stay cached in SurfaceTextures, only the material copies go
        foreach (var m in generatedSurfaces)
            if (m != null) SafeDestroy(m);
        generatedSurfaces.Clear();
    }

    // meshes are rebuilt from the seed every time, so they are never saved into the scene
    static Mesh EnsureMesh(ref Mesh mesh, string name)
    {
        if (mesh == null) mesh = new Mesh { name = name, hideFlags = HideFlags.DontSave };
        return mesh;
    }

    // makes sure a child has a filter and renderer (plus a collider if needed) all pointing at the given mesh
    static GameObject SetupPart(Transform root, string name, Mesh mesh, Material[] materials, bool collide)
    {
        var go = GetOrCreateChild(root, name).gameObject;
        GetOrAdd<MeshFilter>(go).sharedMesh = mesh;
        GetOrAdd<MeshRenderer>(go).sharedMaterials = materials;
        if (!collide) return go;

        // clear first so the collider re-cooks the changed mesh
        var collider = GetOrAdd<MeshCollider>(go);
        collider.sharedMesh = null;
        collider.sharedMesh = mesh;
        return go;
    }

    static void HidePart(Transform root, string name)
    {
        var part = root.Find(name);
        if (part != null) part.gameObject.SetActive(false);
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

    // world position to grid coordinates, in cells (fractional), relative to the map object
    Vector2 WorldToCell(Vector3 worldPos)
    {
        Vector3 local = transform.InverseTransformPoint(worldPos) / cellSize;
        return new Vector2(local.x, local.z);
    }

    // what a world position is standing in: a role name, "Room", "Corridor", or null outside the map
    public string AreaNameAt(Vector3 worldPos)
    {
        if (Layout == null) return null;
        var cell = Vector2Int.FloorToInt(WorldToCell(worldPos));
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
        for (int i = 0; i < pixels.Length; i++) pixels[i] = MapStyle.MapBackground;

        // outline each leaf partition, then paint floor cells over them, then tint rooms by role
        foreach (var leaf in Layout.leaves) PaintOutline(pixels, w, leaf.bounds, MapStyle.Partition);

        // the cell grid and pixel array share the same y * width + x indexing, so copy straight across
        for (int i = 0; i < pixels.Length; i++)
        {
            if (Layout.cells[i] == CellType.Room) pixels[i] = MapStyle.Room;
            else if (Layout.cells[i] == CellType.Corridor) pixels[i] = MapStyle.Corridor;
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
        return Plan.roles == null ? MapStyle.Room : MapStyle.RoleColor(Plan.roles[room]);
    }

    void OnGUI()
    {
        if (!Application.isPlaying || !showOverlay || Layout == null) return;
        MapStyle.Ensure();

        // only rebuild the texture when the layout changed, not every frame
        if (overlayDirty || overlayTexture == null) RebuildOverlayTexture();

        // fit the longer side to overlaySize and pin to the top-right corner, inside a dark frame
        float scale = overlaySize / (float)Mathf.Max(Layout.width, Layout.height);
        float w = Layout.width * scale, h = Layout.height * scale;
        const float margin = 16f, frame = 6f;
        var mapRect = new Rect(Screen.width - w - margin, margin, w, h);
        GUI.Box(new Rect(mapRect.x - frame, mapRect.y - frame, w + frame * 2f, h + frame * 2f), GUIContent.none, MapStyle.PanelBox);

        GUI.DrawTexture(mapRect, overlayTexture);
        DrawRoleLabels(mapRect, scale);
        DrawPlayerMarker(mapRect, scale);
        DrawInfoBox(new Rect(mapRect.x - frame, mapRect.yMax + frame + 6f, w + frame * 2f, 112f));
    }

    // info box under the map: seed/hash, room counts, constraint result, legend, key hints,
    // then the temporary status line
    void DrawInfoBox(Rect box)
    {
        GUI.Box(box, GUIContent.none, MapStyle.PanelBox);
        var line = new Rect(box.x + 10f, box.y + 8f, box.width - 20f, 20f);

        GUI.Label(line, $"Seed {seed}", MapStyle.Label);
        GUI.Label(line, $"Hash {Plan.Hash:X8}", RightAligned(MapStyle.Small));
        line.y += 20f;
        GUI.Label(line, $"{Layout.rooms.Count} rooms, {Layout.reachableRooms} reachable", MapStyle.Small);
        line.y += 18f;
        GUI.Label(line, ConstraintSummary(), MapStyle.OkLabel);
        line.y += 24f;
        DrawLegend(line);
        line.y += 22f;
        GUI.Label(line, "[Tab] panel  [V] designer  [M] map  [N] new  [R] redo", MapStyle.Small);

        // status from N or R, hidden once its time runs out
        if (statusMessage != null && Time.unscaledTime < statusUntil)
        {
            var statusRect = new Rect(box.x, box.yMax + 6f, box.width, 30f);
            GUI.Box(statusRect, GUIContent.none, MapStyle.PanelBox);
            GUI.Label(new Rect(statusRect.x + 10f, statusRect.y + 5f, statusRect.width - 20f, 20f), statusMessage, MapStyle.Small);
        }
    }

    // color chip and name for each kind of area, spread evenly across one row
    void DrawLegend(Rect row)
    {
        var items = new (string name, Color color)[]
        {
            ("ATK", MapStyle.Attacker), ("DEF", MapStyle.Defender), ("MID", MapStyle.Mid),
            ("Site", MapStyle.Site), ("Room", MapStyle.Room), ("Hall", MapStyle.Corridor)
        };
        float slot = row.width / items.Length;
        for (int i = 0; i < items.Length; i++)
        {
            float x = row.x + i * slot;
            MapStyle.Fill(new Rect(x, row.y + 4f, 10f, 10f), items[i].color);
            GUI.Label(new Rect(x + 13f, row.y, slot - 13f, 18f), items[i].name, MapStyle.Small);
        }
    }

    // right-aligned copy of a style, built once on first use
    GUIStyle RightAligned(GUIStyle source) => rightAligned ??= new GUIStyle(source) { alignment = TextAnchor.UpperRight };

    // one line for the minimap, e.g. "Constraints on" or "Constraints on (attempt 3)"
    // broken rules are only used by the solver to pick between attempts, they aren't shown
    public string ConstraintSummary()
    {
        if (!constraints.enabled) return "Constraints off";
        return Plan.attempt == 0 ? "Constraints on" : $"Constraints on (attempt {Plan.attempt})";
    }

    // ATK, DEF, MID and site letters drawn over their rooms
    void DrawRoleLabels(Rect mapRect, float scale)
    {
        for (int r = 0; r < Layout.rooms.Count; r++)
        {
            string label = Plan.Label(r);
            if (label == null) continue;

            var p = CellToMinimap(mapRect, scale, Layout.rooms[r].center);
            GUI.Label(new Rect(p.x - 16f, p.y - 10f, 32f, 20f), label, MapStyle.MapLabel);
        }
    }

    // grid coordinates to a screen point on the minimap
    // map cells go up in +Z, GUI rows go down, so flip the vertical axis
    static Vector2 CellToMinimap(Rect mapRect, float scale, Vector2 cell)
    {
        return new Vector2(mapRect.x + cell.x * scale, mapRect.yMax - cell.y * scale);
    }

    void DrawPlayerMarker(Rect mapRect, float scale)
    {
        if (player == null) return;

        var p = CellToMinimap(mapRect, scale, WorldToCell(player.transform.position));

        // don't draw the dot when the player is outside the map
        if (!mapRect.Contains(p)) return;

        // dark outline first, then the dot, so it shows up on every floor color
        const float size = 7f;
        MapStyle.Fill(new Rect(p.x - size / 2f - 1f, p.y - size / 2f - 1f, size + 2f, size + 2f), MapStyle.MapBackground);
        MapStyle.Fill(new Rect(p.x - size / 2f, p.y - size / 2f, size, size), MapStyle.Player);
    }

    // SCENE VIEW

    // grid cell (x, y) to a world position on the map's floor plane
    public Vector3 CellToWorld(float x, float y)
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
                Gizmos.color = Color.Lerp(Color.white, MapStyle.Partition, t);
                DrawRectGizmo(node.bounds, 0f, false);
            }
        }

        // small height offsets stack corridors above partitions and rooms above corridors
        if (drawCorridors) DrawFilledGizmos(Layout.corridors, MapStyle.Corridor, 0.02f);
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
