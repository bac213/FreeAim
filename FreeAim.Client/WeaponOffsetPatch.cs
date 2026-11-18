using EFT;
using EFT.Animations;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>
/// Postfix on ProceduralWeaponAnimation.ApplyComplexRotation (runs inside
/// FirstPersonStrategy.ApplyTransformations, before AvoidObstacles and before the camera pass).
/// Steps the aim model, takes the game's rerouted sway out of the weapon, then rotates Weapon_root_anim
/// by the model's muzzle offset around the game's own RotationCenter pivot. Bullets are fired along
/// CurrentFireport (child of the weapon), so they follow this rotation without touching ballistics.
/// The hip-fire camera target is CameraOffset-based (PWA.CalculateCameraPosition), so the view
/// does not move with the gun; in ADS the camera goes to the scope bone, so the offset fades to 0.
/// </summary>
internal static class WeaponOffsetPatch
{
    /// <summary>Fixed-mode fade weight 0..1 (read by the overlay).</summary>
    internal static float Blend;

    /// <summary>Offset actually applied this frame (x yaw +right, y pitch +down), for the overlay.</summary>
    internal static Vector2 Applied;

    public static void Postfix(ProceduralWeaponAnimation __instance, float dt)
    {
        var player = FreeAimPlugin.GetLocalPlayer();
        if (player == null || player.ProceduralWeaponAnimation != __instance)
        {
            return;
        }

        bool holdingGun = player.HandsController is Player.FirearmController
                          && !__instance.IsMountedState
                          && Mathf.Abs(__instance.BlindfireBlender.Value) < 0.01f;
        bool sprinting = player.MovementContext.IsSprintEnabled;
        bool enabled = FreeAimPlugin.Enabled.Value;

        WeaponProfile.Update(player);

        // ready stance: centred crosshair (lead collapses), braced body, own pose and arm-stamina drain
        ReadyStance.Step(player, __instance, holdingGun, sprinting, dt);

        Vector2 offset;
        bool model = FreeAimPlugin.Mode.Value == AimMode.Deadzone;
        if (!model)
        {
            AimModel.Deactivate();
            bool aiming = __instance.IsAiming && !FreeAimPlugin.ApplyWhenAiming.Value;
            bool allowed = enabled && holdingGun && !aiming && !ReadyStance.Engaged;
            Blend = Mathf.MoveTowards(Blend, allowed ? 1f : 0f, FreeAimPlugin.BlendSpeed.Value * dt);
            offset = new Vector2(FreeAimPlugin.YawDeg.Value, -FreeAimPlugin.PitchDeg.Value) * Blend;
        }
        else
        {
            Blend = 0f;
            Vector2 sway = holdingGun ? AimModel.ReadGameSway(__instance) : Vector2.zero;
            AimModel.Step(player, __instance, dt, holdingGun, sprinting, sway);
            // the view already carries last frame's recoil follow: keep the muzzle's world direction
            Vector2 compensation = RecoilFeedback.Compensation;
            RecoilFeedback.Step(player, __instance, dt, AimModel.Ads, ReadyStance.StanceWeight);
            offset = AimModel.Output + compensation;
        }

        Applied = offset;

        if (holdingGun)
        {
            SwayProbe.SampleSource(__instance, dt);
            if (model)
            {
                // the game's sway is rerouted through the model (body sway) or replaced (turn sway)
                AimModel.RemoveGameSway(__instance, AimModel.Weight);
            }
        }

        if (offset != Vector2.zero)
        {
            ApplyOffset(__instance, offset);
        }

        if (holdingGun && player.HandsController is Player.FirearmController fc)
        {
            ReadyStance.ApplyPose(__instance, fc);
        }
    }

    private static void ApplyOffset(ProceduralWeaponAnimation pwa, Vector2 offset)
    {
        var hands = pwa.HandsContainer;
        Transform root = hands.WeaponRootAnim;
        Transform cam = hands.CameraTransform;
        if (root == null || cam == null)
        {
            return;
        }

        // Unity is left-handed: +angle about up = right, +angle about right = down (matches Rotate deltas).
        Quaternion rot = Quaternion.AngleAxis(offset.x, cam.up) * Quaternion.AngleAxis(offset.y, cam.right);
        Vector3 pivot = root.TransformPoint(hands.RotationCenter);
        root.SetPositionAndRotation(pivot + rot * (root.position - pivot), rot * root.rotation);
    }
}
