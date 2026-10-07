using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

public class CameraFollow : MonoBehaviour
{
    public Transform target;
    public float lookSensitivity = 0.3f;
    public float lookResetDelay = 1.5f;
    public float lookResetSpeedDegPerSec = 180f;

    [Tooltip("How much each scroll tick changes zoom. Tune to taste - the new Input System's raw scroll delta varies by OS/mouse.")]
    public float zoomSpeed = 0.5f;

    [Tooltip("Furthest the camera can zoom out, as a multiple of whatever orthographic size it starts with.")]
    public float maxZoomOutMultiplier = 2f;

    private float rotationOffset = 0f;
    private float idleTimer = 0f;
    private bool dragging = false;
    private bool wasPressedLastFrame = false;

    private Camera cam;
    private float baseOrthographicSize;

    void Awake()
    {
        cam = GetComponent<Camera>();
        baseOrthographicSize = cam.orthographicSize;
    }

    void LateUpdate()
    {
        UpdateZoom();

        if (target == null) return;

        Vector3 pos = transform.position;
        pos.x = target.position.x;
        pos.y = target.position.y;
        transform.position = pos;

        UpdateLook();
    }

    void UpdateZoom()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        float scroll = mouse.scroll.ReadValue().y;
        if (Mathf.Abs(scroll) < 0.01f) return;
        if (IsOverUI(mouse.position.ReadValue())) return;

        float maxSize = baseOrthographicSize * maxZoomOutMultiplier;
        cam.orthographicSize = Mathf.Clamp(cam.orthographicSize - scroll * zoomSpeed, baseOrthographicSize, maxSize);
    }

    void UpdateLook()
    {
        Mouse mouse = Mouse.current;
        if (mouse == null) return;

        bool isPressed = mouse.leftButton.isPressed;
        if (isPressed && !wasPressedLastFrame)
        {
            dragging = !IsOverUI(mouse.position.ReadValue());
        }
        if (!isPressed)
        {
            dragging = false;
        }
        wasPressedLastFrame = isPressed;

        bool changed = false;
        if (dragging)
        {
            Vector2 delta = mouse.delta.ReadValue();
            if (Mathf.Abs(delta.x) > 0.01f)
            {
                rotationOffset -= delta.x * lookSensitivity;
                changed = true;
            }
        }

        if (changed)
        {
            idleTimer = 0f;
        }
        else
        {
            idleTimer += Time.deltaTime;
            if (idleTimer >= lookResetDelay)
            {
                rotationOffset = Mathf.MoveTowardsAngle(rotationOffset, 0f, lookResetSpeedDegPerSec * Time.deltaTime);
            }
        }

        transform.rotation = Quaternion.Euler(0f, 0f, rotationOffset);
    }

    private static bool IsOverUI(Vector2 screenPos)
    {
        if (EventSystem.current == null) return false;
        PointerEventData ped = new PointerEventData(EventSystem.current) { position = screenPos };
        List<RaycastResult> results = new List<RaycastResult>();
        EventSystem.current.RaycastAll(ped, results);
        return results.Count > 0;
    }
}
