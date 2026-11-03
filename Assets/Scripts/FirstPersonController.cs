using UnityEngine;
using UnityEngine.InputSystem;

// Basic FPS movement: WASD, Shift sprint, Space jump, mouse look
// Click to lock the cursor, Esc to release it
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
    Vector3 velocity;
    float pitch;
    Vector3 spawnPosition;
    Quaternion spawnRotation;

    void Awake()
    {
        controller = GetComponent<CharacterController>();
        if (cameraPivot == null)
        {
            var cam = GetComponentInChildren<Camera>();
            if (cam != null) cameraPivot = cam.transform;
        }
        spawnPosition = transform.position;
        spawnRotation = transform.rotation;
    }

    void Start()
    {
        SetCursorLocked(true);
    }

    void Update()
    {
        var kb = Keyboard.current;
        var mouse = Mouse.current;

        UpdateCursorLock(kb, mouse);
        if (Cursor.lockState == CursorLockMode.Locked && mouse != null)
            Look(mouse.delta.ReadValue());
        Move(kb);

        if (transform.position.y < killHeight)
            Teleport(spawnPosition, spawnRotation);
    }

    void UpdateCursorLock(Keyboard kb, Mouse mouse)
    {
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
        // Mouse delta is already per-frame, so no deltaTime here
        transform.Rotate(Vector3.up, delta.x * mouseSensitivity, Space.Self);
        pitch = Mathf.Clamp(pitch - delta.y * mouseSensitivity, -maxPitch, maxPitch);
        if (cameraPivot != null)
            cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
    }

    void Move(Keyboard kb)
    {
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
        input = Vector2.ClampMagnitude(input, 1f);

        bool grounded = controller.isGrounded;
        if (grounded && velocity.y < 0f) velocity.y = -2f;   // keep snapped to the ground
        if (grounded && jump) velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        velocity.y += gravity * Time.deltaTime;

        float speed = sprint ? sprintSpeed : walkSpeed;
        Vector3 horizontal = (transform.right * input.x + transform.forward * input.y) * speed;
        controller.Move((horizontal + Vector3.up * velocity.y) * Time.deltaTime);
    }

    // Moves the player instantly (the CharacterController must be disabled to set position)
    public void Teleport(Vector3 position, Quaternion rotation)
    {
        controller.enabled = false;
        transform.SetPositionAndRotation(position, rotation);
        controller.enabled = true;
        velocity = Vector3.zero;
    }

    public void SetSpawn(Vector3 position, Quaternion rotation)
    {
        spawnPosition = position;
        spawnRotation = rotation;
    }
}
