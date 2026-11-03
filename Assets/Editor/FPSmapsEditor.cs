using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Inspector buttons for baseMap
[CustomEditor(typeof(baseMap))]
public class BaseMapEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();
        var map = (baseMap)target;

        EditorGUILayout.Space();
        if (map.Layout != null)
        {
            EditorGUILayout.HelpBox(
                $"Rooms: {map.Layout.rooms.Count}   Partitions: {map.Layout.leaves.Count}   Hash: {map.Layout.Hash:X8}",
                MessageType.None);
        }

        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Generate"))
                map.Generate();
            if (GUILayout.Button("New Random Seed"))
            {
                Undo.RecordObject(map, "Randomize Seed");
                map.RandomizeSeed();
                EditorUtility.SetDirty(map);
            }
            if (GUILayout.Button("Verify Determinism"))
                map.VerifyDeterminism();
        }
        SceneView.RepaintAll();
    }
}

// FPSmaps > Build Test Scene: hand-built cube room, FPS player, and the BSP map object
public static class FPSmapsSceneSetup
{
    const string MaterialFolder = "Assets/Materials";

    [MenuItem("FPSmaps/Build Test Scene")]
    public static void BuildTestScene()
    {
        foreach (var name in new[] { "Test Room", "Player", "BSP Map" })
        {
            var existing = GameObject.Find(name);
            if (existing != null) Undo.DestroyObjectImmediate(existing);
        }

        var floorMat = GetOrCreateMaterial("Floor", new Color(0.45f, 0.45f, 0.48f));
        var wallMat = GetOrCreateMaterial("Wall", new Color(0.75f, 0.73f, 0.7f));
        var propMat = GetOrCreateMaterial("Prop", new Color(0.85f, 0.5f, 0.2f));

        BuildRoom(floorMat, wallMat, propMat);
        BuildPlayer(new Vector3(0f, 0.05f, -6f));

        var map = new GameObject("BSP Map");
        map.transform.position = new Vector3(20f, 0f, -32f);
        map.AddComponent<baseMap>();
        Undo.RegisterCreatedObjectUndo(map, "Create BSP Map");

        EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());
        Debug.Log("[FPSmaps] Test scene built. Press Play: WASD move, Shift sprint, Space jump, M toggles the BSP map.");
    }

    static void BuildRoom(Material floorMat, Material wallMat, Material propMat)
    {
        var room = new GameObject("Test Room");
        Undo.RegisterCreatedObjectUndo(room, "Create Test Room");
        var t = room.transform;

        const float size = 20f, half = size / 2f, wallH = 4f, wallT = 0.5f;
        const float doorW = 2f, doorH = 2.5f;

        Cube("Floor", t, new Vector3(0f, -0.1f, 0f), new Vector3(size, 0.2f, size), floorMat);

        // South, east, west walls are solid
        Cube("Wall South", t, new Vector3(0f, wallH / 2f, -half), new Vector3(size, wallH, wallT), wallMat);
        Cube("Wall East", t, new Vector3(half, wallH / 2f, 0f), new Vector3(wallT, wallH, size), wallMat);
        Cube("Wall West", t, new Vector3(-half, wallH / 2f, 0f), new Vector3(wallT, wallH, size), wallMat);

        // North wall has a doorway to test corridor-sized openings
        float segW = (size - doorW) / 2f;
        Cube("Wall North L", t, new Vector3(-(doorW + segW) / 2f, wallH / 2f, half), new Vector3(segW, wallH, wallT), wallMat);
        Cube("Wall North R", t, new Vector3((doorW + segW) / 2f, wallH / 2f, half), new Vector3(segW, wallH, wallT), wallMat);
        Cube("Door Lintel", t, new Vector3(0f, (doorH + wallH) / 2f, half), new Vector3(doorW, wallH - doorH, wallT), wallMat);

        // Cover crates
        Cube("Crate 1", t, new Vector3(3f, 0.5f, 3f), Vector3.one, propMat);
        Cube("Crate 2", t, new Vector3(-4f, 0.75f, 2f), Vector3.one * 1.5f, propMat);
        Cube("Crate 3", t, new Vector3(-4f, 1.75f, 2f), Vector3.one * 0.5f, propMat);

        // Stairs (each step under the CharacterController's 0.3 step offset)
        for (int i = 0; i < 4; i++)
        {
            float h = 0.25f * (i + 1);
            Cube($"Step {i + 1}", t, new Vector3(-6f, h / 2f, -3f - i), new Vector3(3f, h, 1f), propMat);
        }

        // Ramp to test slopes
        var ramp = Cube("Ramp", t, new Vector3(6f, 0.85f, -5f), new Vector3(2f, 0.2f, 5f), propMat);
        ramp.transform.rotation = Quaternion.Euler(-20f, 0f, 0f);
    }

    static void BuildPlayer(Vector3 position)
    {
        var player = new GameObject("Player");
        Undo.RegisterCreatedObjectUndo(player, "Create Player");
        player.transform.position = position;

        var cc = player.AddComponent<CharacterController>();
        cc.height = 1.8f;
        cc.radius = 0.35f;
        cc.center = new Vector3(0f, 0.9f, 0f);
        cc.stepOffset = 0.3f;

        // Reuse the scene's Main Camera so its URP settings and AudioListener are kept
        var cam = Camera.main;
        if (cam == null)
        {
            var camGO = new GameObject("Main Camera") { tag = "MainCamera" };
            Undo.RegisterCreatedObjectUndo(camGO, "Create Camera");
            cam = camGO.AddComponent<Camera>();
            camGO.AddComponent<AudioListener>();
        }
        Undo.SetTransformParent(cam.transform, player.transform, "Parent Camera");
        cam.transform.localPosition = new Vector3(0f, 1.6f, 0f);
        cam.transform.localRotation = Quaternion.identity;
        cam.nearClipPlane = 0.05f;

        var fps = player.AddComponent<FirstPersonController>();
        fps.cameraPivot = cam.transform;
    }

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

    static Material GetOrCreateMaterial(string name, Color color)
    {
        string path = $"{MaterialFolder}/{name}.mat";
        var mat = AssetDatabase.LoadAssetAtPath<Material>(path);
        if (mat != null) return mat;

        if (!AssetDatabase.IsValidFolder(MaterialFolder))
            AssetDatabase.CreateFolder("Assets", "Materials");

        var shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
        mat = new Material(shader);
        mat.SetColor("_BaseColor", color);
        mat.color = color;
        AssetDatabase.CreateAsset(mat, path);
        return mat;
    }
}
