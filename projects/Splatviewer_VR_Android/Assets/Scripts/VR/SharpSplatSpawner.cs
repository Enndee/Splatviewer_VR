// SPDX-License-Identifier: MIT
using GaussianSplatting.Runtime;
using UnityEngine;

/// <summary>
/// Reads the camera embedded in a GaussianSplatAsset (imported from cameras.json or 
/// the SHARP extrinsic matrix) and repositions the XROrigin so the player spawns at
/// that viewpoint automatically.
///
/// Attach to the same GameObject as GaussianSplatRenderer, or assign the renderer
/// reference manually. Also assign the VRRig reference (the XROrigin GameObject).
/// </summary>
public class SharpSplatSpawner : MonoBehaviour
{
    [Header("References")]
    [Tooltip("The GaussianSplatRenderer whose asset contains the camera. Auto-resolved on this GameObject if left empty.")]
    public GaussianSplatRenderer splatRenderer;

    [Tooltip("The XROrigin (VRRig) GameObject to reposition at startup.")]
    public VRRig vrRig;

    [Header("Options")]
    [Tooltip("Index of the camera in the asset to use as spawn point. Usually 0.")]
    public int cameraIndex = 0;

    [Tooltip("Apply the spawn position from the embedded camera on Awake. Disable if you want manual placement.")]
    public bool applyOnAwake = true;

    void Awake()
    {
        if (!applyOnAwake) return;
        ApplySplatCamera();
    }

    /// <summary>Reads camera[cameraIndex] from the splat asset and moves the VR rig there.</summary>
    [ContextMenu("Apply Splat Camera Now")]
    public void ApplySplatCamera()
    {
        if (!ResolveReferences()) return;

        var asset = splatRenderer.asset;
        if (asset == null)
        {
            Debug.LogWarning("[SharpSplatSpawner] GaussianSplatRenderer has no asset assigned.");
            return;
        }

        var cameras = asset.cameras;
        if (cameras == null || cameras.Length == 0)
        {
            Debug.LogWarning("[SharpSplatSpawner] Asset has no embedded camera data. " +
                             "Re-import the PLY file to extract the SHARP extrinsic, " +
                             "or place a cameras.json next to the PLY before importing.");
            return;
        }

        int idx = Mathf.Clamp(cameraIndex, 0, cameras.Length - 1);
        var cam = cameras[idx];

        // Build forward direction from axisZ and compute yaw for XROrigin
        // The VRRig handles pitch via head tracking, so we only yaw-align the rig.
        Vector3 worldPos = cam.pos;
        Vector3 forward  = cam.axisZ;
        forward.y = 0f;

        Quaternion yaw = forward.sqrMagnitude > 0.001f
            ? Quaternion.LookRotation(forward.normalized, Vector3.up)
            : Quaternion.identity;

        Transform rigTransform = vrRig.transform;

        // Place rig floor at eye-height below the camera position
        rigTransform.position = worldPos - Vector3.up * vrRig.eyeHeight;
        rigTransform.rotation = yaw;

        Debug.Log($"[SharpSplatSpawner] Spawned at camera[{idx}]: pos={worldPos:F2}, fwd={forward:F2}");
    }

    bool ResolveReferences()
    {
        if (splatRenderer == null)
            splatRenderer = GetComponent<GaussianSplatRenderer>();
        if (splatRenderer == null)
        {
            Debug.LogWarning("[SharpSplatSpawner] No GaussianSplatRenderer found.");
            return false;
        }
        if (vrRig == null)
            vrRig = FindFirstObjectByType<VRRig>();
        if (vrRig == null)
        {
            Debug.LogWarning("[SharpSplatSpawner] No VRRig found in scene.");
            return false;
        }
        return true;
    }
}
