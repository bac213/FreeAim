using EFT;
using HarmonyLib;
using UnityEngine;

namespace FreeAim.Client;

internal static class LowReadyWalkPatch
{
    internal static void Install(Harmony harmony)
    {
        var applyMotion = AccessTools.Method(typeof(MovementState), nameof(MovementState.ApplyMotion));
        if (applyMotion == null)
        {
            FreeAimPlugin.Log.LogWarning("FreeAim: movement hook not found; low ready walk bonus disabled.");
            return;
        }
        harmony.Patch(applyMotion, prefix: new HarmonyMethod(typeof(LowReadyWalkPatch), nameof(BeforeApplyMotion)));
        FreeAimPlugin.Log.LogInfo("FreeAim: patched low ready walk speed");
    }

    private static void BeforeApplyMotion(MovementState __instance, ref Vector3 motion)
    {
        // EFT calls the normal walking state Run. Sprint has its own state and a separate entry flag.
        float blend = ReadyStance.LowBenefit;
        if (__instance.Name != EPlayerState.Run || blend <= 0f)
        {
            return;
        }
        var context = __instance.MovementContext;
        if (context.IsSprintEnabled || context.IsInPronePose || !context.IsGrounded || !context.CanWalk)
        {
            return;
        }
        var player = FreeAimPlugin.GetLocalPlayer();
        if (player == null || player.MovementContext != context || player.BtrState != EPlayerBtrState.Outside
            || player.HandsController is not Player.FirearmController firearm || firearm.IsAiming
            || player.ProceduralWeaponAnimation is not { IsAiming: false, IsMountedState: false }
            || player.Physical?.Sprinting == true)
        {
            return;
        }

        // The step comes from the selected speed and the game's movement penalties. Scale it after
        // the normalized speed clamp, before the game projects onto slopes and applies gravity/collision.
        float multiplier = Mathf.Lerp(1f, FreeAimPlugin.LowReadyWalkSpeed.Value, blend);
        motion.x *= multiplier;
        motion.z *= multiplier;
    }
}
