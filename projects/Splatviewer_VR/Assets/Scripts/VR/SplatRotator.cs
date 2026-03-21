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
///   Mouse wheel          → uniform scale up/down
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
///   Both grips + move controllers closer/farther → uniform scale down/up
/// </summary>
public class SplatRotator : MonoBehaviour
{
    [Header("Rotation Speed")]
    [Tooltip("Degrees per second when holding a key or tilting the stick.")]
    public float rotationSpeed = 45f;

    [Header("VR Scaling")]
    [Tooltip("Grip amount required before a controller counts as 'held'.")]
    [Range(0.1f, 1f)]
    public float gripThreshold = 0.5f;

    [Tooltip("Minimum uniform scale allowed for the splat object.")]
    public float minUniformScale = 0.05f;

    [Tooltip("Maximum uniform scale allowed for the splat object.")]
    public float maxUniformScale = 20f;

    [Header("Desktop Scaling")]
    [Tooltip("Uniform scale multiplier applied per mouse wheel notch in desktop mode.")]
    public float mouseWheelScaleStep = 0.1f;

    [Header("Saved Rotation")]
    [Tooltip("Rotation applied at startup. Set this in the Inspector to bake a corrected orientation.")]
    public Vector3 startEuler = Vector3.zero;

    // ── Private ───────────────────────────────────────────────────────────────

    Quaternion _originalRotation;
    bool _flipButtonUsed;
    bool _resetButtonUsed;
    bool _isScaling;
    float _scaleStartDistance;
    Vector3 _scaleStartLocalScale;
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
        float rightGrip = ReadAxis1D(XRNode.RightHand, CommonUsages.grip);

        if (leftGrip >= gripThreshold && rightGrip >= gripThreshold)
        {
            HandleVRScaleGesture();
            _flipButtonUsed = false;
            _resetButtonUsed = false;
            return;
        }

        _isScaling = false;

        if (leftGrip < gripThreshold)
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

    void HandleVRScaleGesture()
    {
        if (!TryGetControllerDistance(out float controllerDistance))
        {
            _isScaling = false;
            return;
        }

        if (!_isScaling)
        {
            _isScaling = true;
            _scaleStartDistance = Mathf.Max(controllerDistance, 0.001f);
            _scaleStartLocalScale = transform.localScale;
            return;
        }

        float scaleRatio = controllerDistance / Mathf.Max(_scaleStartDistance, 0.001f);
        transform.localScale = ClampUniformScale(_scaleStartLocalScale * scaleRatio);
    }

    // ── Keyboard rotation ─────────────────────────────────────────────────────

    void KeyboardRotate()
    {
        if (_browser != null && _browser.IsOpen)
            return;

        HandleMouseWheelScale();

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

    void HandleMouseWheelScale()
    {
        float scroll = Input.mouseScrollDelta.y;
        if (Mathf.Abs(scroll) < 0.01f)
            return;

        float scaleMultiplier = 1f - scroll * mouseWheelScaleStep;
        if (scaleMultiplier <= 0.01f)
            scaleMultiplier = 0.01f;

        transform.localScale = ClampUniformScale(transform.localScale * scaleMultiplier);
    }

    Vector3 ClampUniformScale(Vector3 targetScale)
    {
        float maxComponent = Mathf.Max(targetScale.x, Mathf.Max(targetScale.y, targetScale.z));
        float minComponent = Mathf.Min(targetScale.x, Mathf.Min(targetScale.y, targetScale.z));

        if (maxComponent > maxUniformScale && maxComponent > 0f)
            targetScale *= maxUniformScale / maxComponent;
        if (minComponent < minUniformScale && minComponent > 0f)
            targetScale *= minUniformScale / minComponent;

        return targetScale;
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

    static readonly List<InputDevice> s_devices = new(2);

    static Vector2 ReadStick(XRNode node)
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, s_devices);
        if (s_devices.Count > 0 &&
            s_devices[0].TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 v))
            return v;
        return Vector2.zero;
    }

    static float ReadAxis1D(XRNode node, InputFeatureUsage<float> usage)
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, s_devices);
        if (s_devices.Count > 0 && s_devices[0].TryGetFeatureValue(usage, out float v))
            return v;
        return 0f;
    }

    static bool ReadButton(XRNode node, InputFeatureUsage<bool> usage)
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, s_devices);
        if (s_devices.Count > 0 && s_devices[0].TryGetFeatureValue(usage, out bool v))
            return v;
        return false;
    }

    static bool TryReadDevicePosition(XRNode node, out Vector3 position)
    {
        s_devices.Clear();
        InputDevices.GetDevicesAtXRNode(node, s_devices);
        if (s_devices.Count > 0 && s_devices[0].TryGetFeatureValue(CommonUsages.devicePosition, out position))
            return true;

        position = default;
        return false;
    }

    static bool TryGetControllerDistance(out float distance)
    {
        if (TryReadDevicePosition(XRNode.LeftHand, out Vector3 leftPos) &&
            TryReadDevicePosition(XRNode.RightHand, out Vector3 rightPos))
        {
            distance = Vector3.Distance(leftPos, rightPos);
            return true;
        }

        distance = 0f;
        return false;
    }
}
