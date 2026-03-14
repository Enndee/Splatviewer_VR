// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Runtime rotation control for the GaussianSplat GameObject.
///
/// Attach this to the same GameObject that has GaussianSplatRenderer.
///
/// Desktop controls:
///   Q / E                → rotate around Y axis
///
/// Legacy desktop controls are still available:
///   Arrow Left / Right   → rotate around Y axis
///   Arrow Up / Down      → rotate around X axis
///   , / .                → rotate around Z axis
///   Home                 → reset to original rotation
///   End                  → flip upside down
///
/// VR controls (hold LEFT GRIP, then use RIGHT STICK):
///   Right stick X        → rotate around Y
///   Right stick Y        → rotate around X
///   Left  primary button (X) while holding left grip → flip upside down
///   Right primary button (A) while holding left grip → reset rotation
/// </summary>
public class SplatRotator : MonoBehaviour
{
    [Header("Rotation Speed")]
    [Tooltip("Degrees per second when holding a key or tilting the stick.")]
    public float rotationSpeed = 45f;

    [Header("Saved Rotation")]
    [Tooltip("Rotation applied at startup. Set this in the Inspector to bake a corrected orientation.")]
    public Vector3 startEuler = Vector3.zero;

    // ── Private ───────────────────────────────────────────────────────────────

    Quaternion _originalRotation;
    bool _flipButtonUsed;
    bool _resetButtonUsed;
    VRFileBrowser _browser;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        transform.localEulerAngles = startEuler;
        _originalRotation = transform.localRotation;
        _browser = FindAnyObjectByType<VRFileBrowser>();
    }

    void Update()
    {
        if (XRSettings.isDeviceActive)
            VRRotate();
        else
            KeyboardRotate();
    }

    // ── VR rotation ───────────────────────────────────────────────────────────

    void VRRotate()
    {
        if (_browser != null && _browser.IsOpen) return;

        // Require left grip held as a "modifier" to avoid clashing with locomotion
        float leftGrip = ReadAxis1D(XRNode.LeftHand, CommonUsages.grip);
        if (leftGrip < 0.5f)
        {
            _flipButtonUsed  = false;
            _resetButtonUsed = false;
            return;
        }

        Vector2 rightStick = ReadStick(XRNode.RightHand);

        // Rotate Y (yaw) and X (pitch) from right stick
        if (rightStick.sqrMagnitude > 0.04f)
        {
            transform.Rotate(Vector3.up,     rightStick.x * rotationSpeed * Time.deltaTime, Space.World);
            transform.Rotate(Vector3.right,  -rightStick.y * rotationSpeed * Time.deltaTime, Space.World);
        }

        // Left hand primary button (X on Quest) → flip upside down
        if (ReadButton(XRNode.LeftHand, CommonUsages.primaryButton) && !_flipButtonUsed)
        {
            FlipUpsideDown();
            _flipButtonUsed = true;
        }
        else if (!ReadButton(XRNode.LeftHand, CommonUsages.primaryButton))
        {
            _flipButtonUsed = false;
        }

        // Right hand primary button (A on Quest) → reset
        if (ReadButton(XRNode.RightHand, CommonUsages.primaryButton) && !_resetButtonUsed)
        {
            ResetRotation();
            _resetButtonUsed = true;
        }
        else if (!ReadButton(XRNode.RightHand, CommonUsages.primaryButton))
        {
            _resetButtonUsed = false;
        }
    }

    // ── Keyboard rotation ─────────────────────────────────────────────────────

    void KeyboardRotate()
    {
        if (_browser != null && _browser.IsOpen)
            return;

        float dt = rotationSpeed * Time.deltaTime;

        if (Input.GetKey(KeyCode.Q))          transform.Rotate(Vector3.up,    -dt, Space.World);
        if (Input.GetKey(KeyCode.E))          transform.Rotate(Vector3.up,     dt, Space.World);
        if (Input.GetKey(KeyCode.LeftArrow))  transform.Rotate(Vector3.up,    -dt, Space.World);
        if (Input.GetKey(KeyCode.RightArrow)) transform.Rotate(Vector3.up,     dt, Space.World);
        if (Input.GetKey(KeyCode.UpArrow))    transform.Rotate(Vector3.right,  -dt, Space.World);
        if (Input.GetKey(KeyCode.DownArrow))  transform.Rotate(Vector3.right,   dt, Space.World);
        if (Input.GetKey(KeyCode.Comma))      transform.Rotate(Vector3.forward, -dt, Space.World);
        if (Input.GetKey(KeyCode.Period))     transform.Rotate(Vector3.forward,  dt, Space.World);

        if (Input.GetKeyDown(KeyCode.End))  FlipUpsideDown();
        if (Input.GetKeyDown(KeyCode.Home)) ResetRotation();
    }

    // ── Actions ───────────────────────────────────────────────────────────────

    /// <summary>Rotates 180° around the world X axis — fixes most upside-down splats.</summary>
    [ContextMenu("Flip Upside Down")]
    public void FlipUpsideDown()
    {
        transform.Rotate(Vector3.right, 180f, Space.World);
        Debug.Log($"[SplatRotator] Flipped. Euler now: {transform.localEulerAngles}");
    }

    /// <summary>Resets to the rotation stored in startEuler.</summary>
    [ContextMenu("Reset Rotation")]
    public void ResetRotation()
    {
        transform.localRotation = _originalRotation;
        Debug.Log("[SplatRotator] Rotation reset.");
    }

    /// <summary>
    /// Saves the current rotation into startEuler so it persists across Play sessions.
    /// Call this from the Inspector context menu after dialling in the right orientation.
    /// </summary>
    [ContextMenu("Save Current Rotation as Default")]
    public void SaveCurrentRotation()
    {
        startEuler = transform.localEulerAngles;
        Debug.Log($"[SplatRotator] Saved rotation: {startEuler}");
    }

    // ── XR helpers ────────────────────────────────────────────────────────────

    static Vector2 ReadStick(XRNode node)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 &&
            devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v;
        return Vector2.zero;
    }

    static float ReadAxis1D(XRNode node, InputFeatureUsage<float> usage)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 && devices[0].TryGetFeatureValue(usage, out float v))
            return v;
        return 0f;
    }

    static bool ReadButton(XRNode node, InputFeatureUsage<bool> usage)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 && devices[0].TryGetFeatureValue(usage, out bool v))
            return v;
        return false;
    }
}
