using EFT;
using HarmonyLib;
using RootMotion.FinalIK;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>Keep the support arm away from V in the first person right-shoulder high ready.</summary>
internal static class HighReadyClearance
{
    private static readonly AccessTools.FieldRef<Player, LimbIK[]> Limbs =
        AccessTools.FieldRefAccess<Player, LimbIK[]>("_limbs");
    private static readonly AccessTools.FieldRef<Player, Transform[]> Markers =
        AccessTools.FieldRefAccess<Player, Transform[]>("_markers");
    private static readonly AccessTools.FieldRef<Player, Transform[]> Grips =
        AccessTools.FieldRefAccess<Player, Transform[]>("_ikTargets");

    private static IKSolverLimb _leftArm;
    private static Transform _camera;
    private static float _weight;
    private static int _frame = -2;
    private static int _solvedFrame = -2;
    private static float _elbowAngle;
    private static float _forward;
    internal static float ForwardCm => _frame == Time.frameCount ? 100f * _forward : 0f;

    internal static void Install(Harmony harmony)
    {
        var method = AccessTools.Method(typeof(IKSolverLimb), nameof(IKSolverLimb.OnUpdateVirtual));
        if (method == null)
        {
            FreeAimPlugin.Log.LogWarning("FreeAim: left arm IK hook not found; high ready elbow clearance disabled.");
            return;
        }
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(HighReadyClearance), nameof(AfterBendNormal)));
        FreeAimPlugin.Log.LogInfo("FreeAim: patched left arm IK for right-shoulder high ready");
    }

    // Called just after ShiftWeaponRoot, with this frame's camera and weapon pose, before IkProcess.
    internal static void ApplyBeforeHandIk(Player player, Transform root, Transform cam, float weight)
    {
        LimbIK[] limbs = Limbs(player);
        if (limbs == null || limbs.Length < 2 || limbs[0] == null || limbs[1] == null
            || player.LeftHandInteractionTarget != null)
        {
            return;
        }
        IKSolverLimb left = limbs[0].solver;
        if (left != _leftArm || Time.frameCount - _frame > 1)
        {
            _elbowAngle = 0f;
        }
        _leftArm = left;
        _camera = cam;
        _weight = weight;
        _frame = Time.frameCount;
        _forward = 0f;

        float move = FreeAimPlugin.HighReadyForwardCm.Value * 0.01f * weight;
        if (move <= 0f)
        {
            return;
        }
        Vector3 forward = cam.forward;
        Transform[] markers = Markers(player);
        Transform[] grips = Grips(player);
        for (int i = 0; i < 2; i++)
        {
            var solver = limbs[i].solver;
            var shoulder = solver?.bone1?.transform;
            var elbow = solver?.bone2?.transform;
            var wrist = solver?.bone3?.transform;
            if (shoulder == null || elbow == null || wrist == null)
            {
                return;
            }
            // Grenade and interaction targets are excluded. Both endpoints of the game's marker/grip
            // interpolation must remain reachable; a sphere of reachable targets is convex.
            float reach = Vector3.Distance(shoulder.position, elbow.position)
                          + Vector3.Distance(elbow.position, wrist.position);
            Transform marker = i < (markers?.Length ?? 0) ? markers[i] : null;
            Transform grip = i < (grips?.Length ?? 0) ? grips[i] : null;
            move = LimitReach(move, marker, root, shoulder.position, forward, reach);
            move = LimitReach(move, grip, root, shoulder.position, forward, reach);
        }
        _forward = move;
        root.position += forward * move;
    }

    private static float LimitReach(float move, Transform grip, Transform root, Vector3 shoulder, Vector3 forward, float reach)
    {
        if (grip == null || !grip.IsChildOf(root))
        {
            return move;
        }
        Vector3 offset = grip.position - shoulder;
        float along = Vector3.Dot(offset, forward);
        float lateralSq = Mathf.Max(0f, offset.sqrMagnitude - along * along);
        reach *= 0.96f; // keep a little bend instead of reaching full extension
        float roomSq = reach * reach - lateralSq;
        if (roomSq <= 0f)
        {
            return 0f;
        }
        float allowance = Mathf.Sqrt(roomSq) - along;
        return Mathf.Clamp(allowance, 0f, move);
    }

    // The native solver has picked its bend plane. It will restore its original normal after solving.
    // Changing only this normal preserves IKPosition and IKRotation, so the hand stays on its grip.
    private static void AfterBendNormal(IKSolverLimb __instance)
    {
        if (__instance != _leftArm || _frame != Time.frameCount || _camera == null || __instance.IKPositionWeight < 0.95f)
        {
            return;
        }
        var shoulderBone = __instance.bone1.transform;
        var elbowBone = __instance.bone2.transform;
        var wristBone = __instance.bone3.transform;
        Vector3 shoulder = _camera.InverseTransformPoint(shoulderBone.position);
        Vector3 grip = _camera.InverseTransformPoint(__instance.IKPosition);
        Vector3 axis = grip - shoulder;
        float length = axis.magnitude;
        if (length < 0.01f)
        {
            return;
        }
        axis /= length;
        float upperSq = (elbowBone.position - shoulderBone.position).sqrMagnitude;
        float lowerSq = (wristBone.position - elbowBone.position).sqrMagnitude;
        float along = (length * length + upperSq - lowerSq) * (0.5f / length);
        float bendRadius = Mathf.Sqrt(Mathf.Max(0f, upperSq - along * along));
        Vector3 normal = _camera.InverseTransformDirection(__instance.bendNormal);
        Vector3 bend = Vector3.Cross(axis, normal).normalized;
        if (bendRadius < 0.01f || bend == Vector3.zero)
        {
            return;
        }

        // The elbow can move around the shoulder-to-grip axis with the same bone lengths and grip.
        // Score projected arm thickness against the central cone; favour small, stable adjustments.
        if (_solvedFrame != Time.frameCount)
        {
            _solvedFrame = Time.frameCount;
            Vector3 centre = shoulder + axis * along;
            float margin = Mathf.Tan(Mathf.Max(5f, FreeAimPlugin.ClearAngle.Value) * Mathf.Deg2Rad);
            float best = 0f;
            float bestCost = ElbowCost(shoulder, centre + bend * bendRadius, grip, margin);
            for (int angle = -100; angle <= 100; angle += 10)
            {
                if (angle == 0)
                {
                    continue;
                }
                Vector3 elbow = centre + Quaternion.AngleAxis(angle, axis) * bend * bendRadius;
                float cost = ElbowCost(shoulder, elbow, grip, margin) + 0.00018f * Mathf.Abs(angle);
                cost += 0.00008f * (Mathf.Abs(angle - _elbowAngle) - Mathf.Abs(_elbowAngle));
                if (cost < bestCost)
                {
                    best = angle;
                    bestCost = cost;
                }
            }
            _elbowAngle = Mathf.MoveTowards(_elbowAngle, best, 360f * Time.deltaTime);
        }
        __instance.bendNormal = Quaternion.AngleAxis(_elbowAngle * _weight, _camera.TransformDirection(axis))
                                * __instance.bendNormal;
    }

    private static float ElbowCost(Vector3 shoulder, Vector3 elbow, Vector3 grip, float margin)
    {
        // Extra cost for an elbow near the lens and horizontally in front of the eye.
        float nearEye = Mathf.Max(0f, 0.14f - Mathf.Abs(elbow.z)) * Mathf.Clamp01(1f - Mathf.Abs(elbow.x) / 0.2f);
        return Occlusion(elbow, grip, 0.06f, margin) + 0.55f * Occlusion(shoulder, elbow, 0.07f, margin)
               + 0.3f * nearEye;
    }

    private static float Occlusion(Vector3 a, Vector3 b, float radius, float margin)
    {
        const float near = 0.03f;
        if (a.z < near && b.z < near)
        {
            return 0f;
        }
        if (a.z < near)
        {
            a = Vector3.Lerp(a, b, (near - a.z) / (b.z - a.z));
        }
        if (b.z < near)
        {
            b = Vector3.Lerp(b, a, (near - b.z) / (a.z - b.z));
        }
        Vector2 p = new(a.x, a.y);
        Vector2 delta = new(b.x - a.x, b.y - a.y);
        float t = Mathf.Clamp01(-Vector2.Dot(p, delta) / Mathf.Max(delta.sqrMagnitude, 1e-8f));
        return Mathf.Max(0f, radius + Mathf.Lerp(a.z, b.z, t) * margin - (p + delta * t).magnitude);
    }
}
