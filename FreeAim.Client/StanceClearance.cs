using System;
using System.Collections.Generic;
using EFT.Animations;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>
/// Keeps the screen centre clear of the gun in the ready stance. Every visible mesh of the weapon (receiver,
/// optic, rail parts, mounts...) is a small oriented box (its local mesh bounds). The boxes are projected
/// from a stable reference pose, and the tallest part in the centre column sets the drop. The drop is a
/// translation on top of the live pose, so it preserves the game's visible movement and muzzle direction.
/// </summary>
internal static class StanceClearance
{
    internal static float Need;     // m, measured this frame (before smoothing)
    internal static float Applied;  // m, applied this frame
    internal static int Boxes;      // meshes considered

    private struct Box
    {
        public Renderer Renderer;
        public Transform Space;
        public Bounds Bounds;
    }

    private static readonly List<Box> _boxes = new();
    private static Transform _weapon;
    private static float _nextRefresh;
    private static float _smoothed;
    private static bool _hasReference;
    private static Vector3 _referencePosition;
    private static Quaternion _referenceRotation;
    private const float RefreshInterval = 0.5f; // s: attachments and magazines change rarely
    private const float MaxPartSize = 1.5f;      // m: skip light beams and other effect meshes
    private const float ReferenceTime = 0.55f; // s: average residual animation before measuring silhouettes
    private const float EdgeFadeDegrees = 1f;

    internal static void Reset()
    {
        _hasReference = false;
        _smoothed = Need = Applied = 0f;
        Boxes = 0;
    }

    /// <summary>Called at the end of ReadyStance.ApplyBodyPass with the stance weight.</summary>
    internal static void Apply(ProceduralWeaponAnimation pwa, Transform root, Transform cam, float weight,
        float proceduralPositionWeight, float dt)
    {
        var hands = pwa.HandsContainer;
        Transform weapon = hands.Weapon;
        if (!FreeAimPlugin.ClearEnabled.Value || weight <= 0f || weapon == null)
        {
            Reset();
            return;
        }
        if (weapon != _weapon)
        {
            Reset();
        }
        Refresh(weapon);

        // Remove aim-model rotation and the native position springs from the sample, not the live pose.
        // ShiftWeaponRoot can replace part of the procedural position with the third-person shoulder pose.
        Vector2 off = WeaponOffsetPatch.Applied;
        Quaternion undo = Quaternion.Inverse(Quaternion.AngleAxis(off.x, cam.up) * Quaternion.AngleAxis(off.y, cam.right));
        Vector3 pivot = root.TransformPoint(hands.RotationCenter);
        Vector3 handMotion = root.parent != null
            ? root.parent.TransformVector(hands.HandsPosition.GetRelative()) * proceduralPositionWeight
            : Vector3.zero;
        Vector3 eyeMotion = cam.parent != null
            ? cam.parent.TransformVector(hands.CameraPosition.GetRelative())
            : Vector3.zero;
        Vector3 position = cam.InverseTransformPoint(pivot + undo * (root.position - pivot) - handMotion + eyeMotion);
        Quaternion relative = Quaternion.Inverse(cam.rotation) * root.rotation;
        Quaternion rotation = Quaternion.Inverse(cam.rotation) * undo * root.rotation;
        float track = _hasReference ? 1f - Mathf.Exp(-dt / ReferenceTime) : 1f;
        _referencePosition = Vector3.Lerp(_referencePosition, position, track);
        _referenceRotation = _hasReference ? Quaternion.Slerp(_referenceRotation, rotation, track) : rotation;
        _hasReference = true;

        Matrix4x4 measure = Matrix4x4.TRS(_referencePosition, _referenceRotation * Quaternion.Inverse(relative), Vector3.one)
            * Matrix4x4.Translate(-cam.InverseTransformPoint(root.position)) * cam.worldToLocalMatrix;
        float angle = FreeAimPlugin.ClearAngle.Value;
        float tanM = Mathf.Tan(angle * Mathf.Deg2Rad);
        float edgeWidth = Mathf.Tan((angle + EdgeFadeDegrees) * Mathf.Deg2Rad) - tanM;
        float need = 0f;
        Boxes = 0;
        for (int i = 0; i < _boxes.Count; i++)
        {
            Box b = _boxes[i];
            if (b.Renderer == null || b.Space == null || !b.Renderer.enabled || !b.Renderer.gameObject.activeInHierarchy)
            {
                continue;
            }
            Boxes++;
            Vector3 c = b.Bounds.center;
            Vector3 e = b.Bounds.extents;
            Matrix4x4 project = measure * b.Space.localToWorldMatrix;
            // Horizontal extent in tangent space, using corners in front of the eye.
            float xMin = float.MaxValue, xMax = float.MinValue, boxNeed = float.MinValue;
            int front = 0;
            for (int k = 0; k < 8; k++)
            {
                Vector3 local = c + new Vector3((k & 1) == 0 ? -e.x : e.x, (k & 2) == 0 ? -e.y : e.y, (k & 4) == 0 ? -e.z : e.z);
                Vector3 p = project.MultiplyPoint3x4(local);
                if (p.z < 0.03f)
                {
                    continue;
                }
                front++;
                float x = p.x / p.z;
                xMin = Mathf.Min(xMin, x);
                xMax = Mathf.Max(xMax, x);
                boxNeed = Mathf.Max(boxNeed, p.y + p.z * tanM); // drop that puts this corner below the clear angle
            }
            if (front != 0 && boxNeed > 0f)
            {
                // Full clearance inside the centre band; fade just outside it so a tall part
                // grazing the side of the band cannot toggle the whole drop in one frame.
                float gap = Mathf.Max(xMin, -xMax) - tanM;
                float coverage = Mathf.SmoothStep(1f, 0f, gap / edgeWidth);
                need = Mathf.Max(need, boxNeed * coverage);
            }
        }

        need = Mathf.Clamp(need, 0f, FreeAimPlugin.ClearMax.Value);
        Need = need;
        // Use the same speed in both directions so small oscillations cannot bias the drop downward.
        _smoothed += (need - _smoothed) * (1f - Mathf.Exp(-dt / 0.16f));
        Applied = _smoothed * weight;
        if (Applied > 1e-4f)
        {
            root.position -= cam.up * Applied;
        }
    }

    private static void Refresh(Transform weapon)
    {
        if (ReferenceEquals(weapon, _weapon) && Time.time < _nextRefresh)
        {
            return;
        }
        _weapon = weapon;
        _nextRefresh = Time.time + RefreshInterval;
        _boxes.Clear();
        try
        {
            foreach (Renderer r in weapon.GetComponentsInChildren<Renderer>(false))
            {
                Bounds bounds;
                Transform space;
                if (r is MeshRenderer)
                {
                    var filter = r.GetComponent<MeshFilter>();
                    if (filter == null || filter.sharedMesh == null)
                    {
                        continue;
                    }
                    bounds = filter.sharedMesh.bounds;
                    space = r.transform;
                }
                else if (r is SkinnedMeshRenderer skinned)
                {
                    bounds = skinned.localBounds;
                    space = skinned.rootBone != null ? skinned.rootBone : r.transform;
                }
                else
                {
                    continue; // particles, lines, trails
                }
                Vector3 scale = space.lossyScale;
                Vector3 size = Vector3.Scale(bounds.size, new Vector3(Mathf.Abs(scale.x), Mathf.Abs(scale.y), Mathf.Abs(scale.z)));
                if (size.magnitude > MaxPartSize || size.sqrMagnitude < 1e-8f)
                {
                    continue;
                }
                string name = r.name.ToLowerInvariant();
                if (name.Contains("beam") || name.Contains("laser") || name.Contains("flash") || name.Contains("light_cone"))
                {
                    continue;
                }
                _boxes.Add(new Box { Renderer = r, Space = space, Bounds = bounds });
            }
        }
        catch (Exception e)
        {
            _boxes.Clear();
            FreeAimPlugin.Log.LogWarning($"FreeAim: stance clearance unavailable: {e.Message}");
        }
    }
}
