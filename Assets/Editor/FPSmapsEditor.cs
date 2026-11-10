using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// inspector buttons for baseMap
[CustomEditor(typeof(baseMap))]
public class BaseMapEditor : Editor
{
    public override void OnInspectorGUI()
    {
        // normal fields first, extra info and buttons below them
        DrawDefaultInspector();
        var map = (baseMap)target;

        EditorGUILayout.Space();
        if (map.Layout != null)
        {
            // summary box turns into a warning if any room can't be reached
            var layout = map.Layout;
            EditorGUILayout.HelpBox(
                $"Rooms: {layout.rooms.Count}   Partitions: {layout.leaves.Count}   Corridors: {layout.corridors.Count / 2}\n" +
                $"Reachable: {layout.reachableRooms}/{layout.rooms.Count}   Hash: {layout.Hash:X8}",
                layout.AllRoomsReachable ? MessageType.None : MessageType.Warning);
        }

        // one row of three buttons
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate"))
                map.Generate();
            if (GUILayout.Button("New Random Seed"))
            {
                // record first so Ctrl+Z brings back the old seed
                Undo.RecordObject(map, "Randomize Seed");
                map.RandomizeSeed();
                EditorUtility.SetDirty(map);
            }
            if (GUILayout.Button("Verify Determinism"))
                map.VerifyDeterminism();
        }

        // gizmos only redraw when the Scene view repaints, so nudge it after any change
        SceneView.RepaintAll();
    }
}

// Unity reverts Play mode changes, so carry each map's seed back into the edit-mode scene
[InitializeOnLoad]
public static class PlayModeSeedKeeper
{
    const string Key = "FPSmaps.PlayModeSeeds";

    // InitializeOnLoad runs this whenever scripts reload, so the hook is always registered
    static PlayModeSeedKeeper()
    {
        EditorApplication.playModeStateChanged += OnPlayModeChanged;
    }

    static void OnPlayModeChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingPlayMode)
        {
            // saved as "path=seed" lines, keyed by hierarchy path since play mode objects are copies
            // SessionState survives the switch back to edit mode, regular fields don't
            var lines = new System.Text.StringBuilder();
            foreach (var map in Object.FindObjectsByType<baseMap>())
            {
                if (map.keepPlayModeSeed)
                    lines.Append(HierarchyPath(map.transform)).Append('=').Append(map.seed).Append('\n');
            }
            SessionState.SetString(Key, lines.ToString());
        }
        else if (state == PlayModeStateChange.EnteredEditMode)
        {
            // read once and clear, so an old seed can't be applied twice
            string saved = SessionState.GetString(Key, "");
            SessionState.EraseString(Key);
            if (saved.Length == 0) return;

            // match each edit-mode map to its saved line by hierarchy path
            foreach (var map in Object.FindObjectsByType<baseMap>())
            {
                string path = HierarchyPath(map.transform) + "=";
                foreach (var line in saved.Split('\n'))
                {
                    if (!line.StartsWith(path) || !int.TryParse(line.Substring(path.Length), out int seed)) continue;

                    // seed didn't change during play, nothing to do
                    if (seed == map.seed) break;

                    // undoable, and marks the scene as changed so it can be saved
                    Undo.RecordObject(map, "Keep Play Mode Seed");
                    map.Generate(seed);
                    EditorUtility.SetDirty(map);
                    EditorSceneManager.MarkSceneDirty(map.gameObject.scene);
                    Debug.Log($"[FPSmaps] Kept seed {seed} from Play mode on {map.name}.");
                    break;
                }
            }
        }
    }

    // BSP Map walks up the parents and prepends each name
    static string HierarchyPath(Transform t)
    {
        string path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}

// FPSmaps > Build Test Scene: hand-built cube room, FPS player, and the BSP map object
public static class FPSmapsSceneSetup
{
    const string MaterialFolder = "Assets/Materials";

    [MenuItem("FPSmaps/Build Test Scene")]
    public static void BuildTestScene()
    {
        // remove anything left from a previous run so the menu can be used again safely
        foreach (var name in new[] { "Test Room", "Player", "BSP Map" })
        {
            var existing = GameObject.Find(name);
            if (existing != null) Undo.DestroyObjectImmediate(existing);
        }

        // flat colors for now, textures can be swapped into these assets later
        var floorMat = GetOrCreateMaterial("Floor", new Color(0.45f, 0.45f, 0.48f));
        var wallMat = GetOrCreateMaterial("Wall", new Color(0.75f, 0.73f, 0.7f));
        var propMat = GetOrCreateMaterial("Prop", new Color(0.85f, 0.5f, 0.2f));

        BuildRoom(floorMat, wallMat, propMat);
        BuildPlayer(new Vector3(0f, 0.05f, -6f));

        // map sits off to the side so it doesn't overlap the test room
        var map = new GameObject("BSP Map");
        map.transform.position = new Vector3(20f, 0f, -32f);
        var generator = map.AddComponent<baseMap>();
        generator.floorMaterial = floorMat;
        generator.wallMaterial = wallMat;
        generator.ceilingMaterial = wallMat;
        Undo.RegisterCreatedObjectUndo(map, "Create BSP Map");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[FPSmaps] Test scene built. Press Play: WASD move, Shift sprint, Space jump, M toggles the BSP map.");
    }

    static void BuildRoom(Material floorMat, Material wallMat, Material propMat)
    {
        var room = new GameObject("Test Room");
        Undo.RegisterCreatedObjectUndo(room, "Create Test Room");
        var t = room.transform;

        // 20 x 20 room, 4 tall walls, 2 wide doorway (same as a 2-cell corridor)
        const float size = 20f, half = size / 2f, wallH = 4f, wallT = 0.5f;
        const float doorW = 2f, doorH = 2.5f;

        // floor top sits at y = 0, so it's moved down by half its thickness
        Cube("Floor", t, new Vector3(0f, -0.1f, 0f), new Vector3(size, 0.2f, size), floorMat);

        // south, east, west walls are solid
        Cube("Wall South", t, new Vector3(0f, wallH / 2f, -half), new Vector3(size, wallH, wallT), wallMat);
        Cube("Wall East", t, new Vector3(half, wallH / 2f, 0f), new Vector3(wallT, wallH, size), wallMat);
        Cube("Wall West", t, new Vector3(-half, wallH / 2f, 0f), new Vector3(wallT, wallH, size), wallMat);

        // north wall has a doorway to test corridor-sized openings
        // two side pieces plus a lintel above the gap
        float segW = (size - doorW) / 2f;
        Cube("Wall North L", t, new Vector3(-(doorW + segW) / 2f, wallH / 2f, half), new Vector3(segW, wallH, wallT), wallMat);
        Cube("Wall North R", t, new Vector3((doorW + segW) / 2f, wallH / 2f, half), new Vector3(segW, wallH, wallT), wallMat);
        Cube("Door Lintel", t, new Vector3(0f, (doorH + wallH) / 2f, half), new Vector3(doorW, wallH - doorH, wallT), wallMat);

        // cover crates
        // crate 3 sits on top of crate 2 to test jumping onto a stack
        Cube("Crate 1", t, new Vector3(3f, 0.5f, 3f), Vector3.one, propMat);
        Cube("Crate 2", t, new Vector3(-4f, 0.75f, 2f), Vector3.one * 1.5f, propMat);
        Cube("Crate 3", t, new Vector3(-4f, 1.75f, 2f), Vector3.one * 0.5f, propMat);

        // stairs (each step under the CharacterController's 0.3 step offset)
        // every step is 0.25 taller than the last and one unit further back
        for (int i = 0; i < 4; i++)
        {
            float h = 0.25f * (i + 1);
            Cube($"Step {i + 1}", t, new Vector3(-6f, h / 2f, -3f - i), new Vector3(3f, h, 1f), propMat);
        }

        // ramp to test slopes
        // tilted 20 degrees, under the CharacterController's default 45 degree slope limit
        var ramp = Cube("Ramp", t, new Vector3(6f, 0.85f, -5f), new Vector3(2f, 0.2f, 5f), propMat);
        ramp.transform.rotation = Quaternion.Euler(-20f, 0f, 0f);
    }

    static void BuildPlayer(Vector3 position)
    {
        var player = new GameObject("Player");
        Undo.RegisterCreatedObjectUndo(player, "Create Player");
        player.transform.position = position;

        // capsule about the size of a person, with its bottom at the object's origin
        var cc = player.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.35f;
        cc.center = new Vector3(0f, 0.9f, 0f);
        cc.stepOffset = 0.3f;

        // reuse the scene's Main Camera so its URP settings and AudioListener are kept
        var cam = Camera.main;
        if (cam == null)
        {
            var camGO = new GameObject("Main Camera") { tag = "MainCamera" };
            Undo.RegisterCreatedObjectUndo(camGO, "Create Camera");
            cam = camGO.AddComponent<Camera>();
            camGO.AddComponent<AudioListener>();
        }

        // camera at eye height, with a small near plane so walls don't clip when you hug them
        Undo.SetTransformParent(cam.transform, player.transform, "Parent Camera");
        cam.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        cam.transform.localRotation = Quaternion.identity;
        cam.nearClipPlane = 0.05f;

        var fps = player.AddComponent<FirstPersonController>();
        fps.cameraPivot = cam.transform;
    }

    // creates a primitive cube with its collider, positioned and scaled in the parent's space
    static GameObject Cube(string name, Transform parent, Vector3 position, Vector3 scale, Material mat)
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = name;
        go.transform.SetParent(parent, false);
        go.transform.localPosition = position;
        go.transform.localScale = scale;
        go.GetComponent<MeshRenderer>().sharedMaterial = mat;
        return go;
    }

    // loads the material if it already exists, otherwise creates and saves it
    static Material GetOrCreateMaterial(string name, Color color)
    {
        string path = $"{MaterialFolder}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        if (!AssetDatabase.IsValidFolder(MaterialFolder))
            AssetDatabase.CreateFolder("Assets", "Materials");

        // URP Lit in this project, Standard as a backup for the built-in pipeline
        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        mat = new Material(shader);

        // URP reads _BaseColor, the built-in pipeline reads color, so set both
        mat.SetColor("_BaseColor", color);
        mat.color = color;
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
