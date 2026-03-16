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
/// When rotation trigger is released, the world continues spinning with inertia
/// and gradually decelerates.
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
    [Tooltip("Rotation smoothing speed. Higher = snappier. 0 = instant (no smoothing).")]
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

    // Smoothing targets (used when rotationSmoothing > 0)
    Quaternion _targetRotation;
    Vector3 _targetPosition;

    // ── Inertia state ─────────────────────────────────────────────────────────

    Vector3 _angularVelocity;        // axis * degrees/s
    Vector3 _linearVelocity;         // world units/s
    Quaternion _prevHandRot;
    Vector3 _prevHandPos;
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
                _prevHandRot = handRot;
                _prevHandPos = handPos;
                _angularVelocity = Vector3.zero;
                _linearVelocity = Vector3.zero;
            }

            // Delta rotation of the hand since start
            Quaternion deltaRot = handRot * Quaternion.Inverse(_rotateHandStartRot);

            // Hand position delta (translation while rotating)
            Vector3 handPosDelta = handPos - _rotatePivotStart;

            // Compute target rotation and position around the hand's initial pivot point,
            // plus controller translation so the world follows hand movement
            _targetRotation = deltaRot * _rotateWorldStartRot;
            _targetPosition = _rotatePivotStart + deltaRot * (_rotateWorldStartPos - _rotatePivotStart) + handPosDelta;

            // Track angular/linear velocity for inertia
            if (Time.deltaTime > 0f)
            {
                Quaternion frameDelta = handRot * Quaternion.Inverse(_prevHandRot);
                frameDelta.ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f) angle -= 360f;
                if (axis.sqrMagnitude > 0.001f)
                    _angularVelocity = Vector3.Lerp(_angularVelocity, axis.normalized * (angle / Time.deltaTime), 0.3f);

                _linearVelocity = Vector3.Lerp(_linearVelocity, (handPos - _prevHandPos) / Time.deltaTime, 0.3f);
            }
            _prevHandRot = handRot;
            _prevHandPos = handPos;

            if (rotationSmoothing <= 0f)
            {
                // Instant — no smoothing
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
