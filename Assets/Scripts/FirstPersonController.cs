using UnityEngine;
using UnityEngine.InputSystem;

// basic FPS movement: WASD, Shift sprint, Space jump, mouse look
// click to lock the cursor, Esc to release it
[RequireComponent(typeof(CharacterController))]
public class FirstPersonController : MonoBehaviour
{
    [Header("References")]
    public Transform cameraPivot;

    [Header("Movement")]
    public float walkSpeed = 5f;
    public float sprintSpeed = 8f;
    public float jumpHeight = 1.2f;
    public float gravity = -20f;

    [Header("Look")]
    [Tooltip("Degrees per pixel of mouse movement.")]
    public float mouseSensitivity = 0.1f;
    [Range(0f, 90f)] public float maxPitch = 85f;

    [Header("Safety")]
    [Tooltip("Respawn at the start position if the player falls below this height.")]
    public float killHeight = -20f;

    CharacterController controller;
    Vector3 velocity;   // only y is used, horizontal speed comes straight from input each frame
    float pitch;        // camera up/down angle, kept separately so it can be clamped
    Vector3 spawnPosition;
    Quaternion spawnRotation;

    void Awake()
    {
        controller = GetComponent<CharacterController>();

        // fall back to a child camera if no pivot was assigned in the inspector
        if (cameraPivot == null)
        {
            var cam = GetComponentInChildren<Camera>();
            if (cam != null) cameraPivot = cam.transform;
        }

        // wherever the player starts is the default respawn point
        spawnPosition = transform.position;
        spawnRotation = transform.rotation;
    }

    void Start()
    {
        SetCursorLocked(true);
    }

    void Update()
    {
        // either can be null if the device isn't connected
        var kb = Keyboard.current;
        var mouse = Mouse.current;

        UpdateCursorLock(kb, mouse);

        // only look around while the cursor is captured, so a free cursor can click the editor
        if (Cursor.lockState == CursorLockMode.Locked && mouse != null)
            Look(mouse.delta.ReadValue());
        Move(kb);

        // fell out of the map, put the player back at spawn
        if (transform.position.y < killHeight)
            Teleport(spawnPosition, spawnRotation);
    }

    void UpdateCursorLock(Keyboard kb, Mouse mouse)
    {
        // Esc releases, a left click recaptures
        if (kb != null && kb.escapeKey.wasPressedThisFrame)
            SetCursorLocked(false);
        else if (mouse != null && mouse.leftButton.wasPressedThisFrame && Cursor.lockState != CursorLockMode.Locked)
            SetCursorLocked(true);
    }

    void SetCursorLocked(bool locked)
    {
        Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !locked;
    }

    void Look(Vector2 delta)
    {
        // mouse delta is already per-frame, so no deltaTime here
        // yaw turns the whole body so movement follows where you face
        transform.Rotate(Vector3.up, delta.x * mouseSensitivity, Space.Self);

        // pitch only tilts the camera, clamped so you can't flip over backwards
        pitch = Mathf.Clamp(pitch - delta.y * mouseSensitivity, -maxPitch, maxPitch);
        if (cameraPivot != null)
            cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void Move(Keyboard kb)
    {
        // build a direction from WASD: x is strafe, y is forward/back
        Vector2 input = Vector2.zero;
        bool sprint = false, jump = false;
        if (kb != null)
        {
            if (kb.wKey.isPressed) input.y += 1f;
            if (kb.sKey.isPressed) input.y -= 1f;
            if (kb.dKey.isPressed) input.x += 1f;
            if (kb.aKey.isPressed) input.x -= 1f;
            sprint = kb.leftShiftKey.isPressed;
            jump = kb.spaceKey.wasPressedThisFrame;
        }

        // stops diagonals from being faster than straight movement
        input = Vector2.ClampMagnitude(input, 1f);

        bool grounded = controller.isGrounded;
        if (grounded && velocity.y < 0f) velocity.y = -2f;   // keep snapped to the ground

        // v = sqrt(2gh) gives exactly jumpHeight of rise (gravity is negative, hence the -2)
        if (grounded && jump) velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        velocity.y += gravity * Time.deltaTime;

        // input is relative to where the player faces
        float speed = sprint ? sprintSpeed : walkSpeed;
        Vector3 horizontal = (transform.right * input.x + transform.forward * input.y) * speed;

        // one Move call for walking and falling, so collisions are handled together
        controller.Move((horizontal + Vector3.up * velocity.y) * Time.deltaTime);
    }

    // moves the player instantly (the CharacterController must be disabled to set position)
    public void Teleport(Vector3 position, Quaternion rotation)
    {
        controller.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        controller.enabled = true;

        // drop any fall speed so the player doesn't slam into the floor on arrival
        velocity = Vector3.zero;
    }

    // where killHeight respawns send the player
    public void SetSpawn(Vector3 position, Quaternion rotation)
    {
        spawnPosition = position;
        spawnRotation = rotation;
    }
}
