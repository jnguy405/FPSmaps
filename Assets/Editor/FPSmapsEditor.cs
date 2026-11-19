using System.Collections.Generic;
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
                $"Rooms: {layout.rooms.Count}   Partitions: {layout.leaves.Count}   Connections: {layout.connections.Count}\n" +
                $"Reachable: {layout.reachableRooms}/{layout.rooms.Count}   Hash: {map.Plan.Hash:X8}",
                layout.AllRoomsReachable ? MessageType.None : MessageType.Warning);

            // constraint result: distances and entrances for every role
            if (map.constraints.enabled)
            {
                var lines = new List<string> { map.ConstraintSummary() };
                lines.AddRange(map.Plan.report);
                EditorGUILayout.HelpBox(string.Join("\n", lines), MessageType.Info);
            }
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
        if (state == PlayModeStateChange.ExitingPlayMode) SaveSeeds();
        else if (state == PlayModeStateChange.EnteredEditMode) RestoreSeeds();
    }

    // saved as "path=seed" lines, keyed by hierarchy path since play mode objects are copies
    // SessionState survives the switch back to edit mode, regular fields don't
    static void SaveSeeds()
    {
        var lines = new System.Text.StringBuilder();
        foreach (var map in Object.FindObjectsByType<baseMap>())
        {
            if (map.keepPlayModeSeed)
                lines.Append(HierarchyPath(map.transform)).Append('=').Append(map.seed).Append('\n');
        }
        SessionState.SetString(Key, lines.ToString());
    }

    static void RestoreSeeds()
    {
        // read once and clear, so an old seed can't be applied twice
        var seeds = ParseSeeds(SessionState.GetString(Key, ""));
        SessionState.EraseString(Key);

        // match each edit-mode map to its saved seed by hierarchy path
        foreach (var map in Object.FindObjectsByType<baseMap>())
        {
            // skip maps with nothing saved, or whose seed didn't change during play
            if (!seeds.TryGetValue(HierarchyPath(map.transform), out int seed) || seed == map.seed) continue;
            ApplySeed(map, seed);
        }
    }

    // turns "path=seed" lines back into a lookup, skipping anything that doesn't parse
    static Dictionary<string, int> ParseSeeds(string saved)
    {
        var seeds = new Dictionary<string, int>();
        foreach (var line in saved.Split('\n'))
        {
            // split on the last '=' in case an object name contains one
            int eq = line.LastIndexOf('=');
            if (eq > 0 && int.TryParse(line.Substring(eq + 1), out int seed))
                seeds[line.Substring(0, eq)] = seed;
        }
        return seeds;
    }

    // undoable, and marks the scene as changed so it can be saved
    static void ApplySeed(baseMap map, int seed)
    {
        Undo.RecordObject(map, "Keep Play Mode Seed");
        map.Generate(seed);
        EditorUtility.SetDirty(map);
        EditorSceneManager.MarkSceneDirty(map.gameObject.scene);
        Debug.Log($"[FPSmaps] Kept seed {seed} from Play mode on {map.name}.");
    }

    // BSP Map walks up the parents and prepends each name
    static string HierarchyPath(Transform t)
    {
        string path = t.name;
        for (var p = t.parent; p != null; p = p.parent) path = p.name + "/" + path;
        return path;
    }
}

// FPSmaps > Build Test Scene: the FPS player and the BSP map object
public static class FPSmapsSceneSetup
{
    const string MaterialFolder = "Assets/Materials";

    [MenuItem("FPSmaps/Build Test Scene")]
    public static void BuildTestScene()
    {
        // remove anything left from a previous run so the menu can be used again safely
        // (Test Room is the old hand-built room, cleared out of scenes made before it was dropped)
        foreach (var name in new[] { "Test Room", "Player", "BSP Map" })
        {
            var existing = GameObject.Find(name);
            if (existing != null) Undo.DestroyObjectImmediate(existing);
        }

        // flat colors for now, textures can be swapped into these assets later
        var floorMat = GetOrCreateMaterial("Floor", new Color(0.45f, 0.45f, 0.48f));
        var wallMat = GetOrCreateMaterial("Wall", new Color(0.75f, 0.73f, 0.7f));

        // the map moves the player into the attacker spawn on Play, so the start position only matters in edit mode
        BuildPlayer(new Vector3(0f, 0.05f, 0f));

        var map = new GameObject("BSP Map");
        var generator = map.AddComponent<baseMap>();
        generator.floorMaterial = floorMat;
        generator.wallMaterial = wallMat;
        generator.ceilingMaterial = wallMat;
        Undo.RegisterCreatedObjectUndo(map, "Create BSP Map");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[FPSmaps] Test scene built. Press Play: WASD move, Shift sprint, Space jump, M toggles the BSP map.");
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
