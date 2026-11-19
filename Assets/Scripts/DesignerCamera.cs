using UnityEngine;
using UnityEngine.InputSystem;

// designer view: an overhead orbit camera for looking over the whole 3D map
// V toggles it; WASD pans, Q/E rotates, scroll zooms, right-drag orbits, F reframes the whole map
// the player camera is switched off while it's active and turned back on when it closes
[RequireComponent(typeof(baseMap))]
public class DesignerCamera : MonoBehaviour
{
    [Tooltip("Starting tilt in degrees, 90 is straight down.")]
    [Range(20f, 90f)] public float startPitch = 60f;
    public float panSpeed = 1f;
    public float rotateSpeed = 90f;
    [Tooltip("Degrees of orbit per pixel of right-drag.")]
    public float orbitSensitivity = 0.25f;

    baseMap map;
    Camera cam;
    Camera playerCam;
    bool active;

    // the camera orbits a point on the floor at a distance, from these angles
    Vector3 focus;
    float yaw, pitch, distance;

    public bool IsActive => active;

    void Awake()
    {
        map = GetComponent<baseMap>();
    }

    void OnDestroy()
    {
        if (cam != null) Destroy(cam.gameObject);
    }

    void Update()
    {
        // V would otherwise fire while typing a seed into the panel
        var kb = Keyboard.current;
        if (kb == null || map.PanelOpen) return;

        if (kb.vKey.wasPressedThisFrame) SetActive(!active);
        if (!active) return;

        HandleInput(kb, Mouse.current);
        PlaceCamera();
    }

    public void SetActive(bool value)
    {
        if (value == active || map.Layout == null) return;
        active = value;

        if (active)
        {
            EnsureCamera();
            Frame();
            PlaceCamera();
        }

        // only one camera renders at a time
        if (cam != null) cam.gameObject.SetActive(active);
        if (playerCam != null) playerCam.enabled = !active;
        map.RefreshPlayerInput();
    }

    // centers on the map and backs off far enough to see all of it
    public void Frame()
    {
        focus = map.MapCenterWorld;
        yaw = 0f;
        pitch = startPitch;

        // fits the map's longest side into the vertical field of view, with a little margin
        float halfFov = cam.fieldOfView * 0.5f * Mathf.Deg2Rad;
        distance = map.MapSizeWorld * 0.6f / Mathf.Tan(halfFov);
    }

    void EnsureCamera()
    {
        if (playerCam == null)
        {
            var player = FindAnyObjectByType<FirstPersonController>();
            if (player != null) playerCam = player.GetComponentInChildren<Camera>(true);
        }
        if (cam != null) return;

        // not saved and not parented, so the map's transform can't tilt it
        var go = new GameObject("Designer Camera") { hideFlags = HideFlags.DontSave };
        cam = go.AddComponent<Camera>();
        if (playerCam != null) cam.CopyFrom(playerCam);
        cam.nearClipPlane = 0.3f;
        cam.farClipPlane = 2000f;
    }

    void HandleInput(Keyboard kb, Mouse mouse)
    {
        float dt = Time.unscaledDeltaTime;

        // pan along the ground relative to where the camera faces, faster when zoomed out
        var flatRot = Quaternion.Euler(0f, yaw, 0f);
        Vector3 move = Vector3.zero;
        if (kb.wKey.isPressed) move += flatRot * Vector3.forward;
        if (kb.sKey.isPressed) move -= flatRot * Vector3.forward;
        if (kb.dKey.isPressed) move += flatRot * Vector3.right;
        if (kb.aKey.isPressed) move -= flatRot * Vector3.right;
        float boost = kb.leftShiftKey.isPressed ? 2.5f : 1f;
        focus += move.normalized * panSpeed * distance * boost * dt;

        if (kb.qKey.isPressed) yaw += rotateSpeed * dt;
        if (kb.eKey.isPressed) yaw -= rotateSpeed * dt;
        if (kb.fKey.wasPressedThisFrame) Frame();
        if (mouse == null) return;

        // one zoom step per scroll notch, whatever the platform's scroll units are
        float scroll = mouse.scroll.ReadValue().y;
        if (scroll != 0f) distance *= scroll > 0f ? 0.88f : 1.12f;
        distance = Mathf.Clamp(distance, 3f, map.MapSizeWorld * 3f);

        // right-drag orbits: sideways turns, up and down tilts
        if (mouse.rightButton.isPressed)
        {
            var delta = mouse.delta.ReadValue();
            yaw += delta.x * orbitSensitivity;
            pitch = Mathf.Clamp(pitch - delta.y * orbitSensitivity, 20f, 89f);
        }
    }

    // camera sits back from the focus point along its view direction
    void PlaceCamera()
    {
        var rot = Quaternion.Euler(pitch, yaw, 0f);
        cam.transform.SetPositionAndRotation(focus - rot * Vector3.forward * distance, rot);
    }
}
