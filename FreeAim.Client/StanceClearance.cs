using System;
using System.Collections.Generic;
using EFT.Animations;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>
/// Keeps the screen centre clear of the gun in the ready stance. Every visible mesh of the weapon (receiver,
/// optic, rail parts, mounts...) is a small oriented box (its local mesh bounds). Each frame the boxes are
/// projected into Cam space; every box that covers the centre column (within the clear angle) needs the gun
/// lowered until its top sits below the clear angle. The gun is translated down by the largest need, so a
/// tall optic or a high mount is lowered more than an iron-sight rifle. Translation only: the bore stays
/// parallel and the muzzle origin drops by a few cm.
/// Measured on the rest pose (the aim model's rotation undone) every frame in the body pass (the game rebuilds the weapon transform each frame), so the
/// shift never feeds back into its own measurement.
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
    private static readonly Vector3[] _corner = new Vector3[8];
    private static Transform _weapon;
    private static float _nextRefresh;
    private static float _smoothed;
    private const float RefreshInterval = 0.5f; // s: attachments and magazines change rarely
    private const float MaxPartSize = 1.5f;     // m: skip light beams and other effect meshes

    internal static void Reset()
    {
        _smoothed = Need = Applied = 0f;
    }

    /// <summary>Called at the end of ReadyStance.ApplyBodyPass with the stance weight.</summary>
    internal static void Apply(ProceduralWeaponAnimation pwa, Transform root, Transform cam, float weight, float dt)
    {
        Need = Applied = 0f;
        if (!FreeAimPlugin.ClearEnabled.Value || weight <= 0f)
        {
            _smoothed = 0f;
            return;
        }
        Transform weapon = pwa.HandsContainer.Weapon;
        if (weapon == null)
        {
            return;
        }
        Refresh(weapon);

        // Measure the rest pose: undo this frame's aim-model rotation (lag, swing, sway, sag), so turning and
        // sway do not slide parts in and out of the centre column (the gun would bob up and down).
        Vector2 off = WeaponOffsetPatch.Applied;
        Quaternion undo = Quaternion.Inverse(Quaternion.AngleAxis(off.x, cam.up) * Quaternion.AngleAxis(off.y, cam.right));
        Vector3 pivot = root.TransformPoint(pwa.HandsContainer.RotationCenter);
        bool rotated = off.sqrMagnitude > 1e-8f;

        float tanM = Mathf.Tan(FreeAimPlugin.ClearAngle.Value * Mathf.Deg2Rad);
        float need = 0f;
        Boxes = 0;
        for (int i = 0; i < _boxes.Count; i++)
        {
            Box b = _boxes[i];
            if (b.Renderer == null || b.Space == null || !b.Renderer.enabled || !b.Renderer.isVisible)
            {
                continue;
            }
            Boxes++;
            Vector3 c = b.Bounds.center;
            Vector3 e = b.Bounds.extents;
            for (int k = 0; k < 8; k++)
            {
                Vector3 local = c + new Vector3((k & 1) == 0 ? -e.x : e.x, (k & 2) == 0 ? -e.y : e.y, (k & 4) == 0 ? -e.z : e.z);
                Vector3 world = b.Space.TransformPoint(local);
                if (rotated)
                {
                    world = pivot + undo * (world - pivot);
                }
                _corner[k] = cam.InverseTransformPoint(world);
            }
            // horizontal extent of the box on screen (tangent space), corners in front of the eye only
            float xMin = float.MaxValue, xMax = float.MinValue, boxNeed = float.MinValue;
            int front = 0;
            for (int k = 0; k < 8; k++)
            {
                Vector3 p = _corner[k];
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
            if (front == 0 || xMax < -tanM || xMin > tanM)
            {
                continue;
            }
            need = Mathf.Max(need, boxNeed);
        }

        need = Mathf.Clamp(need, 0f, FreeAimPlugin.ClearMax.Value);
        Need = need;
        // quick to clear, slow to come back up (no bobbing with every bit of sway)
        float tau = need > _smoothed ? 0.12f : 0.4f;
        _smoothed += (need - _smoothed) * (1f - Mathf.Exp(-dt / tau));
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
