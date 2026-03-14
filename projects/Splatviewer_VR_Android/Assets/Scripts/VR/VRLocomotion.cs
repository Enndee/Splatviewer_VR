// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Smooth locomotion and snap-turn for the VR Gaussian Splatting viewer.
///
/// Controls (VR):
///   Left stick         → smooth move (relative to HMD facing direction, XZ plane)
///   Right stick X      → snap turn (configurable degrees per click)
///   Right stick Y      → fly up / down (useful for inspecting splats from above)
///
/// Keyboard fallback (editor / desktop, no HMD):
///   W / A / S / D      → move forward / left / back / right
///   Q / E              → move down / up
///   Mouse drag (RMB)   → look
/// </summary>
[RequireComponent(typeof(VRRig))]
public class VRLocomotion : MonoBehaviour
{
    [Header("Smooth Movement")]
    [Tooltip("Horizontal move speed in metres per second.")]
    public float moveSpeed = 2.5f;

    [Tooltip("Vertical fly speed in metres per second (right stick Y).")]
    public float flySpeed = 1.5f;

    [Tooltip("Analogue stick dead-zone radius (0–1).")]
    [Range(0f, 0.5f)]
    public float stickDeadzone = 0.2f;

    [Header("Snap Turn")]
    [Tooltip("Rotation applied per snap-turn click, in degrees.")]
    public float snapAngle = 45f;

    [Header("Mouse Look (desktop fallback)")]
    [Tooltip("Mouse look sensitivity when using the keyboard fallback.")]
    public float mouseSensitivity = 2f;

    [Tooltip("Hide and lock the cursor while using desktop mode.")]
    public bool lockCursorInDesktopMode = true;

    // ── Private state ─────────────────────────────────────────────────────────

    VRRig  _rig;
    bool   _snapTurnReady = true;
    float  _mousePitch;   // accumulated vertical mouse look (desktop only)
    VRFileBrowser _browser;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        _rig = GetComponent<VRRig>();
        _browser = FindAnyObjectByType<VRFileBrowser>();

        if (XRSettings.isDeviceActive)
            SyncDesktopPitchFromCamera();
        else
            ResetDesktopLook();
    }

    void SyncDesktopPitchFromCamera()
    {
        Camera cam = _rig != null ? _rig.xrCamera : Camera.main;
        if (cam != null)
        {
            float pitch = cam.transform.localEulerAngles.x;
            if (pitch > 180f)
                pitch -= 360f;
            _mousePitch = pitch;
        }
    }

    public void ResetDesktopLook()
    {
        if (XRSettings.isDeviceActive)
            return;

        Camera cam = _rig != null ? _rig.xrCamera : Camera.main;
        if (cam != null)
        {
            cam.transform.localRotation = Quaternion.identity;
        }

        _mousePitch = 0f;
    }

    void Update()
    {
        if (XRSettings.isDeviceActive)
        {
            VRMove();
            VRSnapTurn();
        }
        else
        {
            KeyboardMouseFallback();
        }
    }

    // ── VR movement ───────────────────────────────────────────────────────────

    void VRMove()
    {
        if (_browser != null && _browser.IsOpen) return;

        Vector2 leftStick = ReadStick(XRNode.LeftHand);
        if (leftStick.magnitude <= stickDeadzone)
            return;

        // Use the camera's world-space axes so direction is always correct regardless
        // of XROrigin rotation (snap turns, spawn orientation, etc.)
        Camera cam = _rig != null ? _rig.xrCamera : Camera.main;
        if (cam == null) return;

        Vector3 headForward = cam.transform.forward;
        headForward.y = 0f;
        if (headForward.sqrMagnitude < 0.001f) return;
        headForward.Normalize();

        Vector3 headRight = cam.transform.right;
        headRight.y = 0f;
        headRight.Normalize();

        Vector3 move = (headForward * leftStick.y + headRight * leftStick.x)
                       * moveSpeed * Time.deltaTime;
        transform.position += move;
    }

    void VRSnapTurn()
    {
        // Right stick is used by file browser when open
        if (_browser != null && _browser.IsOpen) return;

        Vector2 rightStick = ReadStick(XRNode.RightHand);

        // Horizontal axis → snap rotate the XR Origin
        if (Mathf.Abs(rightStick.x) > 0.7f && _snapTurnReady)
        {
            transform.Rotate(0f, snapAngle * Mathf.Sign(rightStick.x), 0f);
            _snapTurnReady = false;
        }
        else if (Mathf.Abs(rightStick.x) < 0.3f)
        {
            _snapTurnReady = true;
        }

        // Vertical axis → fly up / down
        if (Mathf.Abs(rightStick.y) > stickDeadzone)
            transform.position += Vector3.up * (rightStick.y * flySpeed * Time.deltaTime);
    }

    // ── Keyboard / mouse fallback ─────────────────────────────────────────────

    void KeyboardMouseFallback()
    {
        if (_browser != null && (_browser.IsOpen || _browser.WasOpenThisFrame))
            return;

        UpdateDesktopCursorState();

        // Translation
        float h = (Input.GetKey(KeyCode.D) ? 1f : 0f) - (Input.GetKey(KeyCode.A) ? 1f : 0f);
        float v = (Input.GetKey(KeyCode.W) ? 1f : 0f) - (Input.GetKey(KeyCode.S) ? 1f : 0f);
        float y = (Input.GetKey(KeyCode.Space) ? 1f : 0f) - (Input.GetKey(KeyCode.C) ? 1f : 0f);

        Vector3 forward = transform.forward;
        forward.y = 0f;
        if (forward.sqrMagnitude < 0.001f)
            forward = Vector3.forward;
        forward.Normalize();

        Vector3 right = transform.right;
        right.y = 0f;
        if (right.sqrMagnitude < 0.001f)
            right = Vector3.right;
        right.Normalize();

        Vector3 move = (right * h + forward * v + Vector3.up * y);
        if (move.sqrMagnitude > 0.01f)
            transform.position += move.normalized * moveSpeed * Time.deltaTime;

        bool allowMouseLook = !lockCursorInDesktopMode || Cursor.lockState == CursorLockMode.Locked;
        if (allowMouseLook)
        {
            Camera cam = _rig != null ? _rig.xrCamera : Camera.main;
            float mouseX = Input.GetAxis("Mouse X") * mouseSensitivity;
            float mouseY = Input.GetAxis("Mouse Y") * mouseSensitivity;

            transform.Rotate(0f, mouseX, 0f, Space.World);

            _mousePitch -= mouseY;
            _mousePitch  = Mathf.Clamp(_mousePitch, -80f, 80f);

            if (cam != null)
            {
                Vector3 euler = cam.transform.localEulerAngles;
                euler.x = _mousePitch;
                cam.transform.localEulerAngles = euler;
            }
        }
    }

    void UpdateDesktopCursorState()
    {
        if (!lockCursorInDesktopMode)
            return;

        if (Input.GetKeyDown(KeyCode.Escape))
        {
            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
            return;
        }

        if (Cursor.lockState != CursorLockMode.Locked && Input.GetMouseButtonDown(0))
        {
            Cursor.lockState = CursorLockMode.Locked;
            Cursor.visible = false;
        }
        else if (Cursor.lockState == CursorLockMode.Locked)
        {
            Cursor.visible = false;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>Reads the primary 2D axis (thumbstick) from an XR controller node.</summary>
    static Vector2 ReadStick(XRNode node)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 &&
            devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v;
        return Vector2.zero;
    }

    /// <summary>Returns the HMD's look direction projected flat onto the XZ plane.</summary>
    static Vector3 GetHMDForwardFlat()
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(XRNode.Head, devices);
        if (devices.Count > 0 &&
            devices[0].TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
        {
            Vector3 fwd = rot * Vector3.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude > 0.001f)
                return fwd.normalized;
        }
        return Vector3.forward;
    }
}
