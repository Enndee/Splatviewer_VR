// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Manipulates a WorldRoot transform using VR controllers.
/// Attach to XROrigin; assign the WorldRoot (e.g. GaussianSplat GameObject) as target.
///
/// Controls (right controller):
///   Grip   → translate world 1:1 with hand delta
///   Trigger → rotate world around the controller pivot
///
/// Hand input is filtered through an EMA (exponential moving average) to reduce
/// jitter during rotation. When the trigger is released, inertia continues the
/// motion with gradual deceleration.
///
/// Other scripts query IsManipulating to disable themselves during manipulation.
/// Call ResetWorld() before every scene load to return WorldRoot to identity.
/// </summary>
public class WorldGrabManipulator : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The root transform to manipulate (e.g. GaussianSplat GameObject).")]
    public Transform worldRoot;

    [Header("Thresholds")]
    [Tooltip("Grip value above which translation begins.")]
    [Range(0f, 1f)] public float gripThreshold = 0.5f;

    [Tooltip("Trigger value above which rotation begins.")]
    [Range(0f, 1f)] public float triggerThreshold = 0.5f;

    [Header("Smoothing")]
    [Tooltip("Scales hand rotation before applying. Lower = slower/heavier feel.")]
    [Range(0.05f, 1f)] public float rotationScale = 0.35f;

    [Tooltip("Hand input filter strength. Higher = smoother but more latent. 0 = no filtering.")]
    [Range(0f, 50f)] public float inputSmoothing = 15f;

    [Tooltip("Output smoothing speed. Higher = snappier. 0 = instant.")]
    [Range(0f, 50f)] public float rotationSmoothing = 10f;

    [Header("Inertia")]
    [Tooltip("How quickly rotation inertia decays after release. Higher = stops faster.")]
    [Range(0.5f, 20f)] public float inertiaDrag = 4f;

    [Tooltip("Angular speed (deg/s) below which inertia stops completely.")]
    [Range(0.01f, 5f)] public float inertiaMinSpeed = 0.5f;

    // ── State flags ──────────────────────────────────────────────────────────

    /// <summary>True while right grip is held and world is being translated.</summary>
    public bool IsGrabbing { get; private set; }

    /// <summary>True while right trigger is held and world is being rotated.</summary>
    public bool IsRotating { get; private set; }

    /// <summary>True when either grab or rotate is active.</summary>
    public bool IsManipulating => IsGrabbing || IsRotating;

    // ── Saved neutral state ──────────────────────────────────────────────────

    Vector3 _neutralPosition;
    Quaternion _neutralRotation;

    // ── Grab (translation) state ─────────────────────────────────────────────

    Vector3 _grabHandStartPos;
    Vector3 _grabWorldStartPos;

    // ── Rotate state ─────────────────────────────────────────────────────────

    Quaternion _rotateHandStartRot;
    Vector3 _rotatePivotStart;
    Vector3 _rotateWorldStartPos;
    Quaternion _rotateWorldStartRot;

    // Filtered hand input (EMA)
    Quaternion _filteredHandRot;
    Vector3 _filteredHandPos;

    // Smoothing targets (used when rotationSmoothing > 0)
    Quaternion _targetRotation;
    Vector3 _targetPosition;

    // ── Inertia state ─────────────────────────────────────────────────────────

    Vector3 _angularVelocity;        // axis * degrees/s
    Vector3 _linearVelocity;         // world units/s
    Quaternion _prevFilteredRot;
    Vector3 _prevFilteredPos;
    bool _hasInertia;

    // ── Cache ────────────────────────────────────────────────────────────────

    VRFileBrowser _browser;

    // ── Lifecycle ────────────────────────────────────────────────────────────

    void Awake()
    {
        _browser = FindAnyObjectByType<VRFileBrowser>();

        if (worldRoot != null)
        {
            _neutralPosition = worldRoot.localPosition;
            _neutralRotation = worldRoot.localRotation;
        }
    }

    void Update()
    {
        if (worldRoot == null) return;
        if (!XRSettings.isDeviceActive) return;
        if (_browser != null && _browser.IsOpen) return;

        float grip = ReadAxis(XRNode.RightHand, CommonUsages.grip);
        float trigger = ReadAxis(XRNode.RightHand, CommonUsages.trigger);

        // ── Translation (right grip) ────────────────────────────────────────
        if (grip >= gripThreshold)
        {
            _hasInertia = false; // cancel inertia when grabbing
            Vector3 handPos = GetControllerWorldPosition(XRNode.RightHand);

            if (!IsGrabbing)
            {
                // Begin grab
                IsGrabbing = true;
                _grabHandStartPos = handPos;
                _grabWorldStartPos = worldRoot.position;
            }

            // Delta between current hand position and start, applied to world
            Vector3 handDelta = handPos - _grabHandStartPos;
            worldRoot.position = _grabWorldStartPos + handDelta;
        }
        else
        {
            IsGrabbing = false;
        }

        // ── Rotation (right trigger) ────────────────────────────────────────
        if (trigger >= triggerThreshold && !IsGrabbing)
        {
            _hasInertia = false; // cancel inertia while actively rotating
            Vector3 handPos = GetControllerWorldPosition(XRNode.RightHand);
            Quaternion handRot = GetControllerWorldRotation(XRNode.RightHand);

            if (!IsRotating)
            {
                // Begin rotation
                IsRotating = true;
                _rotateHandStartRot = handRot;
                _rotatePivotStart = handPos;
                _rotateWorldStartPos = worldRoot.position;
                _rotateWorldStartRot = worldRoot.rotation;
                _filteredHandRot = handRot;
                _filteredHandPos = handPos;
                _prevFilteredRot = handRot;
                _prevFilteredPos = handPos;
                _angularVelocity = Vector3.zero;
                _linearVelocity = Vector3.zero;
            }

            // ── Filter hand input (EMA) to reduce jitter ──────────────────
            if (inputSmoothing > 0f)
            {
                float f = 1f - Mathf.Exp(-inputSmoothing * Time.deltaTime);
                _filteredHandRot = Quaternion.Slerp(_filteredHandRot, handRot, f);
                _filteredHandPos = Vector3.Lerp(_filteredHandPos, handPos, f);
            }
            else
            {
                _filteredHandRot = handRot;
                _filteredHandPos = handPos;
            }

            // Delta rotation of the filtered hand since start, scaled down for heavier feel
            Quaternion fullDelta = _filteredHandRot * Quaternion.Inverse(_rotateHandStartRot);
            Quaternion deltaRot = Quaternion.Slerp(Quaternion.identity, fullDelta, rotationScale);

            // Filtered hand position delta
            Vector3 handPosDelta = _filteredHandPos - _rotatePivotStart;

            // Compute target rotation and position around the hand's initial pivot point,
            // plus controller translation so the world follows hand movement
            _targetRotation = deltaRot * _rotateWorldStartRot;
            _targetPosition = _rotatePivotStart + deltaRot * (_rotateWorldStartPos - _rotatePivotStart) + handPosDelta;

            // Track angular/linear velocity from filtered input for inertia
            if (Time.deltaTime > 0f)
            {
                Quaternion frameDelta = _filteredHandRot * Quaternion.Inverse(_prevFilteredRot);
                frameDelta.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f) angle -= 360f;
                if (axis.sqrMagnitude > 0.001f)
                    _angularVelocity = Vector3.Lerp(_angularVelocity, axis.normalized * (angle / Time.deltaTime), 0.3f);

                _linearVelocity = Vector3.Lerp(_linearVelocity, (_filteredHandPos - _prevFilteredPos) / Time.deltaTime, 0.3f);
            }
            _prevFilteredRot = _filteredHandRot;
            _prevFilteredPos = _filteredHandPos;

            if (rotationSmoothing <= 0f)
            {
                // Instant — no output smoothing
                worldRoot.rotation = _targetRotation;
                worldRoot.position = _targetPosition;
            }
            else
            {
                float t = 1f - Mathf.Exp(-rotationSmoothing * Time.deltaTime);
                worldRoot.rotation = Quaternion.Slerp(worldRoot.rotation, _targetRotation, t);
                worldRoot.position = Vector3.Lerp(worldRoot.position, _targetPosition, t);
            }
        }
        else
        {
            if (IsRotating)
            {
                // Just released — start inertia
                IsRotating = false;
                _hasInertia = _angularVelocity.magnitude > inertiaMinSpeed;
            }
        }

        // ── Inertia (after rotation release) ────────────────────────────────
        if (_hasInertia)
        {
            float speed = _angularVelocity.magnitude;
            if (speed < inertiaMinSpeed)
            {
                _hasInertia = false;
            }
            else
            {
                float dt = Time.deltaTime;

                // Apply angular velocity as rotation around world center
                Quaternion inertiaRot = Quaternion.AngleAxis(speed * dt, _angularVelocity.normalized);
                worldRoot.rotation = inertiaRot * worldRoot.rotation;

                // Also apply linear velocity
                worldRoot.position += _linearVelocity * dt;

                // Decay
                float decay = Mathf.Exp(-inertiaDrag * dt);
                _angularVelocity *= decay;
                _linearVelocity *= decay;
            }
        }
    }

    // ── Public API ───────────────────────────────────────────────────────────

    /// <summary>
    /// Resets WorldRoot to its neutral (identity) state.
    /// Call this before every scene/file load.
    /// </summary>
    public void ResetWorld()
    {
        if (worldRoot == null) return;

        worldRoot.localPosition = _neutralPosition;
        worldRoot.localRotation = _neutralRotation;

        IsGrabbing = false;
        IsRotating = false;
        _hasInertia = false;
        _angularVelocity = Vector3.zero;
        _linearVelocity = Vector3.zero;
    }

    // ── XR Helpers ───────────────────────────────────────────────────────────

    static float ReadAxis(XRNode node, InputFeatureUsage<float> usage)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 && devices[0].TryGetFeatureValue(usage, out float v))
            return v;
        return 0f;
    }

    Vector3 GetControllerWorldPosition(XRNode node)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 && devices[0].TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos))
            return transform.TransformPoint(pos);
        return transform.position;
    }

    Quaternion GetControllerWorldRotation(XRNode node)
    {
        var devices = new List<InputDevice>();
        InputDevices.GetDevicesAtXRNode(node, devices);
        if (devices.Count > 0 && devices[0].TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
            return transform.rotation * rot;
        return transform.rotation;
    }
}
