// SPDX-License-Identifier: MIT
using System.Collections.Generic;
using GaussianSplatting.Runtime;
using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// Manages the XR camera rig for Gaussian Splatting VR viewing.
///
/// Hierarchy:
///   XROrigin [this script + VRLocomotion]
///     └── CameraOffset
///           └── Main Camera  (tag: MainCamera)
///
/// Works with Quest 3 (Meta Link / Air Link) and Virtual Desktop (SteamVR OpenXR runtime).
/// Requires: XR Plug-in Management + OpenXR provider enabled in Project Settings.
/// </summary>
public class VRRig : MonoBehaviour
{
    [Header("Rig References")]
    [Tooltip("Child GameObject that provides the camera height offset. Should be named 'Camera Offset'.")]
    public Transform cameraOffset;

    [Tooltip("The Main Camera inside the rig (child of Camera Offset).")]
    public Camera xrCamera;

    [Header("Standing Height")]
    [Tooltip("Eye height in meters used when the XR runtime does not provide floor-level tracking.")]
    public float eyeHeight = 1.65f;

    [Header("Spawn Point")]
    [Tooltip("Optional: assign an empty GameObject in the scene to define the VR starting position and heading. " +
             "XROrigin will be placed at this Transform on startup so the camera appears at exactly that viewpoint. " +
             "Leave empty to keep XROrigin's current scene position.")]
    public Transform spawnPoint;

    // ── Lifecycle ─────────────────────────────────────────────────────────────

    void Awake()
    {
        AutoResolveReferences();
        ApplySpawnPoint();
        ApplyCameraOffset();
    }

    void OnEnable()
    {
        InputDevices.deviceConnected    += OnDeviceConnected;
        InputDevices.deviceDisconnected += OnDeviceDisconnected;
    }

    void OnDisable()
    {
        InputDevices.deviceConnected    -= OnDeviceConnected;
        InputDevices.deviceDisconnected -= OnDeviceDisconnected;
    }

    // ── Public ────────────────────────────────────────────────────────────────

    /// <summary>True when an XR device (HMD) is active and tracking.</summary>
    public bool IsVRActive => XRSettings.isDeviceActive;

    /// <summary>World-space position of the HMD (or main camera position as fallback).</summary>
    public Vector3 HeadPosition
    {
        get
        {
            var devices = new List<InputDevice>();
            InputDevices.GetDevicesAtXRNode(XRNode.Head, devices);
            if (devices.Count > 0 && devices[0].TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos))
                return transform.TransformPoint(pos);
            return xrCamera != null ? xrCamera.transform.position : transform.position;
        }
    }

    // ── Private ───────────────────────────────────────────────────────────────

    void AutoResolveReferences()
    {
        if (cameraOffset == null)
        {
            cameraOffset = transform.Find("Camera Offset");
            if (cameraOffset == null)
                cameraOffset = transform.Find("CameraOffset");
        }

        if (xrCamera == null)
            xrCamera = GetComponentInChildren<Camera>();
    }

    void ApplySpawnPoint()
    {
        // Default target is world origin — SHARP splats always place the scene so the
        // capture camera is at (0,0,0), matching splatapult's identity default view.
        Vector3 targetPos = GetSpawnEyePosition();
        float yaw = spawnPoint != null ? spawnPoint.eulerAngles.y : 0f;

        // Place the XROrigin floor so the camera eye ends up at targetPos.
        transform.position = targetPos - Vector3.up * eyeHeight;
        transform.rotation = Quaternion.Euler(0f, yaw, 0f);
    }

    /// <summary>Resets the rig to its spawn point position and heading.</summary>
    public void ResetToSpawnPoint(GaussianSplatRenderer renderer = null)
    {
        ApplySpawnPoint();
        AlignToRenderer(renderer);

        var locomotion = GetComponent<VRLocomotion>();
        if (locomotion != null)
            locomotion.ResetDesktopLook();
    }

    Vector3 GetSpawnEyePosition()
    {
        return spawnPoint != null ? spawnPoint.position : Vector3.zero;
    }

    void AlignToRenderer(GaussianSplatRenderer renderer)
    {
        if (renderer == null || renderer.m_Asset == null)
            return;

        Vector3 boundsCenterLocal = (renderer.m_Asset.boundsMin + renderer.m_Asset.boundsMax) * 0.5f;
        Vector3 boundsCenterWorld = renderer.transform.TransformPoint(boundsCenterLocal);
        Vector3 lookDirection = boundsCenterWorld - GetSpawnEyePosition();
        lookDirection.y = 0f;
        if (lookDirection.sqrMagnitude < 0.0001f)
            return;

        transform.rotation = Quaternion.LookRotation(lookDirection.normalized, Vector3.up);
    }

    void ApplyCameraOffset()
    {
        // Only apply a manual eye-height offset when the XR runtime uses seated/stationary
        // tracking. When using floor-level tracking (Quest 3 default) the runtime already
        // positions the camera at the correct height.
        //
        // We set it anyway at startup; if the runtime overrides it that's fine.
        if (cameraOffset != null)
            cameraOffset.localPosition = new Vector3(0f, eyeHeight, 0f);
    }

    void OnDeviceConnected(InputDevice device)
    {
        if ((device.characteristics & InputDeviceCharacteristics.HeadMounted) != 0)
            Debug.Log($"[VRRig] HMD connected: {device.name}");
        if ((device.characteristics & InputDeviceCharacteristics.Controller) != 0)
            Debug.Log($"[VRRig] Controller connected: {device.name}");
    }

    void OnDeviceDisconnected(InputDevice device)
    {
        if ((device.characteristics & InputDeviceCharacteristics.HeadMounted) != 0)
            Debug.LogWarning($"[VRRig] HMD disconnected: {device.name}");
    }
}
