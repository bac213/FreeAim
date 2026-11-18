using EFT;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace FreeAim.Client;

// Translation only, after shoulder remapping and before EFT solves the two hands.
internal static class ArmPlacement
{
    private static readonly AccessTools.FieldRef<Player, LimbIK[]> Limbs =
        AccessTools.FieldRefAccess<Player, LimbIK[]>("_limbs");
    private static readonly AccessTools.FieldRef<Player, Transform[]> Markers =
        AccessTools.FieldRefAccess<Player, Transform[]>("_markers");
    private static readonly AccessTools.FieldRef<Player, Transform[]> Grips =
        AccessTools.FieldRefAccess<Player, Transform[]>("_ikTargets");

    internal static float Wanted, Moved; // cumulative path length this frame (m)
    private static Vector2 _margin;
    private static int _frame;
    internal static void Begin()
    {
        _frame = Time.frameCount;
        Wanted = Moved = 0f;
        _margin = new Vector2(float.NaN, float.NaN);
    }

    internal static string Format() => _frame != Time.frameCount || Wanted <= 0f ? "placement: -" :
        $"placement actual/request: {Moved * 100f:0.0}/{Wanted * 100f:0.0}cm   reach margin L/R: {_margin.x:0.0}/{_margin.y:0.0}cm";

    internal static float Translate(Player player, Transform root, Vector3 delta)
    {
        float move = delta.magnitude;
        if (move < 0.00001f) return 1f;
        Wanted += move;
        var limbs = Limbs(player);
        if (player.LeftHandInteractionTarget != null || limbs == null || limbs.Length < 2) return 0f;
        var markers = Markers(player);
        var grips = Grips(player);
        Vector3 dir = delta / move;
        float allowed = move;
        for (int i = 0; i < 2; i++)
        {
            var arm = limbs[i]?.solver;
            var shoulder = arm?.bone1?.transform;
            var elbow = arm?.bone2?.transform;
            var wrist = arm?.bone3?.transform;
            if (!shoulder || !elbow || !wrist) return 0f;
            float reach = Vector3.Distance(shoulder.position, elbow.position) + Vector3.Distance(elbow.position, wrist.position);
            bool tracked = false;
            for (int j = 0; j < 2; j++)
            {
                var targets = j == 0 ? markers : grips;
                Transform t = i < (targets?.Length ?? 0) ? targets[i] : null;
                if (!t || !t.IsChildOf(root)) continue;
                tracked = true;
                Vector3 from = t.position - shoulder.position;
                allowed = Mathf.Min(allowed, MotionLimits.ReachTravel(from, dir, reach));
            }
            if (!tracked) return 0f; // no known moving endpoint for this hand
        }
        root.position += dir * allowed;
        Moved += allowed;
        if (FreeAimPlugin.ShowOverlay.Value)
        {
            for (int i = 0; i < 2; i++)
            {
                var arm = limbs[i].solver;
                Vector3 s = arm.bone1.transform.position, e = arm.bone2.transform.position;
                float length = Vector3.Distance(s, e) + Vector3.Distance(e, arm.bone3.transform.position);
                float distance = 0f;
                for (int j = 0; j < 2; j++)
                {
                    var targets = j == 0 ? markers : grips;
                    Transform t = i < (targets?.Length ?? 0) ? targets[i] : null;
                    if (t && t.IsChildOf(root)) distance = Mathf.Max(distance, Vector3.Distance(s, t.position));
                }
                _margin[i] = (length - distance) * 100f;
            }
        }
        return allowed / move;
    }
}
