using EFT;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>Lower both grips and move the raised gun to the right.</summary>
internal static class HighReadyPlacement
{
    private static readonly AccessTools.FieldRef<Player, LimbIK[]> Limbs =
        AccessTools.FieldRefAccess<Player, LimbIK[]>("_limbs");
    private static readonly AccessTools.FieldRef<Player, Transform[]> Markers =
        AccessTools.FieldRefAccess<Player, Transform[]>("_markers");
    private static readonly AccessTools.FieldRef<Player, Transform[]> Grips =
        AccessTools.FieldRefAccess<Player, Transform[]>("_ikTargets");

    private static int _frame = -1;
    private static Vector2 _offsetCm;
    internal static Vector2 OffsetCm => _frame == Time.frameCount ? _offsetCm : Vector2.zero;

    // Shift the whole weapon before the native hand solve. Both hands keep their original grips
    // and both elbows use EFT's normal bend planes. The camera and weapon rotation stay put.
    internal static void Apply(Player player, Transform root, Transform cam, float weight)
    {
        LimbIK[] limbs = Limbs(player);
        if (limbs == null || limbs.Length < 2 || player.LeftHandInteractionTarget != null)
        {
            return;
        }

        float x = FreeAimPlugin.HighReadyRightCm.Value * 0.01f * weight;
        float y = -FreeAimPlugin.HighReadyDownCm.Value * 0.01f * weight;
        Vector3 offset = cam.right * x + cam.up * y;
        float move = offset.magnitude;
        if (move < 0.0001f)
        {
            return;
        }
        Vector3 direction = offset / move;
        float allowed = move;
        Transform[] markers = Markers(player);
        Transform[] grips = Grips(player);
        for (int i = 0; i < 2; i++)
        {
            var solver = limbs[i]?.solver;
            var shoulder = solver?.bone1?.transform;
            var elbow = solver?.bone2?.transform;
            var wrist = solver?.bone3?.transform;
            if (shoulder == null || elbow == null || wrist == null)
            {
                return;
            }

            // EFT blends between its marker and the assigned grip. Limit the same translation for
            // both hands against both endpoints, so neither hand has to leave the weapon to reach.
            float reach = Vector3.Distance(shoulder.position, elbow.position)
                          + Vector3.Distance(elbow.position, wrist.position);
            Transform marker = i < (markers?.Length ?? 0) ? markers[i] : null;
            Transform grip = i < (grips?.Length ?? 0) ? grips[i] : null;
            allowed = LimitReach(allowed, marker, root, shoulder.position, direction, reach);
            allowed = LimitReach(allowed, grip, root, shoulder.position, direction, reach);
        }

        root.position += direction * allowed;
        _offsetCm = new Vector2(x, y) * (100f * allowed / move);
        _frame = Time.frameCount;
    }

    private static float LimitReach(float move, Transform target, Transform root, Vector3 shoulder,
        Vector3 direction, float reach)
    {
        if (target == null || !target.IsChildOf(root))
        {
            return move;
        }
        Vector3 offset = target.position - shoulder;
        float along = Vector3.Dot(offset, direction);
        float radius = reach * 0.96f; // leave some elbow bend when the original pose allows it
        // If the original grip is already farther out, allow movement inward without extending it.
        float room = Mathf.Max(0f, radius * radius - offset.sqrMagnitude);
        return Mathf.Clamp(Mathf.Sqrt(along * along + room) - along, 0f, move);
    }
}
