using System;
using System.Collections.Generic;
using EFT;
using EFT.Animations;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>
/// Mass distribution of the gun in hands, from the game's own data (works for modded guns and parts):
/// every part placed on the weapon (the game's ContainerCollectionView: slot -> bone, item, item view) contributes
/// its own mass (item total weight minus the placed parts in its slots; a magazine keeps its cartridges) at its
/// centre (volume-weighted centre of the meshes that belong to it, not to a child part). The receiver is the
/// weapon's own mass at the centre of the meshes that belong to no part.
/// Measured in Weapon_root_anim space, so the gun's current pose does not matter. Pivot = the game's
/// RotationCenter (the point the gun is rotated about, by the game and by the aim model).
/// <list type="bullet">
/// <item>Inertia J = sum m r^2 about the pivot (kg m^2).</item>
/// <item>Moment M = sum m x, x = distance in front of the pivot along the bore (kg m): what gravity pulls on.</item>
/// </list>
/// </summary>
internal static class WeaponMass
{
    internal static bool Valid;
    internal static float Inertia;      // kg m^2
    internal static float Moment;       // kg m
    internal static float CentreAhead;  // m, centre of mass in front of the pivot
    internal static int Parts;

    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, ContainerCollectionView> Ccv =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, ContainerCollectionView>("_ccv");

    private struct Part
    {
        public float Mass;
        public Vector3 Sum;   // volume-weighted centre sum (root space)
        public float Volume;
        public Vector3 Origin;
    }

    private static readonly Dictionary<Transform, Item> _viewOwner = new();
    private static readonly Dictionary<Item, Part> _parts = new();

    internal static void Measure(ProceduralWeaponAnimation pwa, Player.FirearmController fc)
    {
        Valid = false;
        try
        {
            var ccv = Ccv(pwa);
            var hands = pwa.HandsContainer;
            Transform root = hands.WeaponRootAnim;
            Transform weaponView = hands.Weapon;
            Transform fireport = fc.CurrentFireport?.Original;
            if (ccv == null || root == null || weaponView == null || fireport == null)
            {
                return;
            }
            Weapon weapon = fc.Item;

            _viewOwner.Clear();
            _parts.Clear();
            foreach (var view in ccv.ContainerBones.Values)
            {
                if (view?.Item != null && view.ItemView != null && !ReferenceEquals(view.Item, weapon))
                {
                    _viewOwner[view.ItemView] = view.Item;
                }
            }

            // own masses: total minus the placed parts directly in its slots
            AddPart(weapon, root.InverseTransformPoint(weaponView.position));
            foreach (var pair in _viewOwner)
            {
                AddPart(pair.Value, root.InverseTransformPoint(pair.Key.position));
            }

            // mesh centres: each renderer belongs to the nearest part view above it, else to the receiver
            foreach (Renderer r in weaponView.GetComponentsInChildren<Renderer>(false))
            {
                if (r is not MeshRenderer && r is not SkinnedMeshRenderer)
                {
                    continue;
                }
                Bounds b = r.bounds;
                float volume = b.size.x * b.size.y * b.size.z;
                if (volume <= 1e-9f || b.size.magnitude > 1.5f)
                {
                    continue;
                }
                Item owner = weapon;
                for (Transform t = r.transform; t != null && t != weaponView; t = t.parent)
                {
                    if (_viewOwner.TryGetValue(t, out Item item))
                    {
                        owner = item;
                        break;
                    }
                }
                if (_parts.TryGetValue(owner, out Part part))
                {
                    part.Sum += root.InverseTransformPoint(b.center) * volume;
                    part.Volume += volume;
                    _parts[owner] = part;
                }
            }

            Vector3 pivot = hands.RotationCenter;
            Vector3 bore = root.InverseTransformDirection(fireport.TransformDirection(pwa.ShotDirection)).normalized;
            float j = 0f, moment = 0f, mass = 0f;
            foreach (var part in _parts.Values)
            {
                if (part.Mass <= 0f)
                {
                    continue;
                }
                Vector3 centre = part.Volume > 0f ? part.Sum / part.Volume : part.Origin;
                Vector3 r = centre - pivot;
                j += part.Mass * r.sqrMagnitude;
                moment += part.Mass * Vector3.Dot(r, bore);
                mass += part.Mass;
            }
            if (mass <= 0.05f || j <= 1e-4f)
            {
                return;
            }
            Inertia = j;
            Moment = Mathf.Max(0f, moment);
            CentreAhead = moment / mass;
            Parts = _parts.Count;
            Valid = true;
        }
        catch (Exception e)
        {
            Valid = false;
            FreeAimPlugin.Log.LogWarning($"FreeAim: weapon mass distribution unavailable: {e.Message}");
        }
    }

    private static void AddPart(Item item, Vector3 origin)
    {
        float own = item.TotalWeight;
        if (item is CompoundItem compound)
        {
            foreach (Slot slot in compound.Slots)
            {
                Item child = slot.ContainedItem;
                if (child != null && _viewOwner.ContainsValue(child))
                {
                    own -= child.TotalWeight;
                }
            }
        }
        _parts[item] = new Part { Mass = Mathf.Max(0f, own), Origin = origin };
    }
}
