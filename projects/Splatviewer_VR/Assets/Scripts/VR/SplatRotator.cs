// SPDX-License-Identifier: MIT
using UnityEngine;

/// <summary>
/// Runtime rotation control for the GaussianSplat GameObject.
///
/// VR rotation is now handled entirely by WorldGrabManipulator.
/// This script only handles desktop keyboard controls and explicit actions
/// (flip, reset) that can be triggered from code or context menus.
///
/// Desktop controls:
///   Q / E                → rotate around Y axis
///   Arrow Left / Right   → rotate around Y axis
///   Arrow Up / Down      → rotate around X axis
///   , / .                → rotate around Z axis
///   Home                 → reset to original rotation
///   End                  → flip upside down
/// </summary>
[DefaultExecutionOrder(-200)]
public class SplatRotator : MonoBehaviour
{
    [Header("Rotation Speed")]
    [Tooltip("Degrees per second when holding a key.")]
    public float rotationSpeed = 45f;

    [Header("Saved Rotation")]
    [Tooltip("Rotation applied at startup. Set this in the Inspector to bake a corrected orientation.")]
    public Vector3 startEuler = new Vector3(180f, 0f, 0f);

    // ── Private ───────────────────────────────────────────────────────────────

    Quaternion _originalRotation;
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
        // VR rotation is handled by WorldGrabManipulator — skip entirely in VR
        if (UnityEngine.XR.XRSettings.isDeviceActive)
            return;

        KeyboardRotate();
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
}
