using System;
using System.ComponentModel;
using EFT;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;

namespace FreeAim.Client;

public enum AimMode
{
    /// <summary>Four-point model: view, body, gun target, muzzle (the name is kept for config compatibility).</summary>
    [Description("四点模型")]
    Deadzone,
    /// <summary>Stage 0 spike: constant offset from config (verification only).</summary>
    [Description("固定偏移（测试用）")]
    Fixed,
}

/// <summary>
/// Four-point aim model. All angles are degrees relative to the view V (x = yaw, + right; y = pitch, + down),
/// the same convention as WeaponOffsetPatch.ApplyOffset.
/// <list type="bullet">
/// <item>V (view, head): the mouse, in exact game degrees. Its yaw stays within the neck range of the body;
/// beyond it the view can only turn as fast as the body. Excess input is dropped.</item>
/// <item>A (body): yaw follows V with a limited turn rate (weapon inertia, pose, overweight, legs); pitch = V.
/// Body sway D (the game's breath/step/motion/shake spring, rerouted) moves A.</item>
/// <item>T (gun target) = V + lead O. Turning adds (lead gain - 1) x the view turn to O, so the gun leads the
/// view. Part of O fades quickly, the rest stays; after the idle delay without mouse input it recentres.
/// The ready stance, ADS and sprint pull O to 0.</item>
/// <item>R (muzzle) = arm path + sway path. Arm: T is smoothed first (time tau_s: gentle start and stop), then a
/// critically damped spring (omega per weapon) with partial target-velocity feed-forward k = 1 - 1/(2 omega tau_s).
/// All poles are real and k is chosen so a turn that stops never overshoots: no wobble, heavy guns just trail.
/// Plus gun swing (the gun's own inertia: a critically damped mode driven by the arm's acceleration, so a turn that
/// stops swings past once; gain grows and return slows with inertia), gravity sag and the integral (ready stance and
/// ADS; integrates only while the view, the gun and the swing are still, so turns never wind it up). Sway: body sway filtered by the arm (slow part corrected) and the gun's inertia (fast part
/// smoothed); this path never amplifies.</item>
/// </list>
/// The model never measures the muzzle, so recoil (separate game springs, applied on top) is never corrected.
/// </summary>
internal static class AimModel
{
    // ---- outputs / state, relative to V ----
    internal static float BodyYaw;        // A - V (yaw); pitch of A = V
    internal static float BodyRate;       // body yaw rate, deg/s (world)
    internal static float BodyTurnLimit;  // current max body turn rate, deg/s
    internal static Vector2 Gun;          // arm path: G - V
    internal static Vector2 Sway;         // sway path at the muzzle
    internal static Vector2 Swing;        // gun swing (own inertia) on top of the arm path
    internal static Vector2 Hold;         // stance integral (shift of the arm target)
    internal static Vector2 HoldFrozen;   // 1 = integral paused on that axis this frame
    internal static float Sag;            // gravity sag of the arm target (+ down)
    internal static Vector2 Output;       // applied this frame
    internal static float Weight;         // 0..1 (fades with ADS / inactive)
    internal static float SinceInput;     // s since the last mouse input
    internal static float ArmHz;
    internal static float ArmFeedForward;
    internal static float ArmSmooth;
    internal static float Kappa;
    internal static float Carry;          // share of the view motion the shoulder carries at once
    internal static bool Active;          // model running (local player, gun in hands, plain movement state)
    internal static Vector2 Lead => _leadKeep + _leadFast;
    /// <summary>ADS blend 0..1 following the game's own camera-to-scope weight (model output fades with it).</summary>
    internal static float Ads => _adsBlend;

    private static Vector2 _leadKeep;
    private static Vector2 _leadFast;
    private static Vector2 _gunVel;       // world, deg/s, the part not carried by the shoulder
    private static Vector2 _viewVelLp;    // low-passed view velocity (carry acceleration for the swing)
    private static Vector2 _swingVel;     // deg/s
    private static Vector2 _bodyVel;      // world, deg/s (yaw: body, pitch: view)
    private static Vector2 _smooth;       // smoothed arm target, relative to V, world-anchored like Gun
    private static Vector2 _lastView;
    private static bool _hasView;
    private static int _playerId;
    private static bool _leadAllowed;
    private static float _ads;
    private static float _adsBlend;
    private static float _activeFade;
    private static float _lastSample = -10f;
    private static Vector2 _swayLow;
    private static Vector2 _swayInertia;

    private const float BodyRateSmoothing = 0.06f; // s
    private const float FadeTime = 0.15f;          // s
    private const float AdsFadeTime = 0.12f;       // s
    private const float HoldRelease = 0.3f;        // s
    private const float CarryAccelSmoothing = 0.03f; // s; mouse noise must not kick the gun swing
    private const float SwingStill = 0.3f;         // deg; integral waits until the gun swing has settled

    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, float> AimingWeight =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, float>("_aimingWeight");

    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, float> DisplacementStr =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, float>("_displacementStr");

    private static bool Fresh => Time.time - _lastSample < 0.25f;

    /// <summary>States that use the default MovementState.Rotate; everything else keeps the game's own rotation.</summary>
    internal static bool IsPlainState(EPlayerState s) => s is EPlayerState.Idle or EPlayerState.Run or EPlayerState.Sprint
        or EPlayerState.Sidestep or EPlayerState.Jump or EPlayerState.FallDown or EPlayerState.JumpLanding
        or EPlayerState.Transition;

    // ------------------------------------------------------------------ input

    /// <summary>
    /// Prefix on Player.Rotate(Vector2, bool). Converts the raw mouse delta to game degrees with the state's
    /// own ClampRotation (sensitivity, ADS sensitivity, state factor, per-call cap), applies the neck limit,
    /// feeds the lead, adds the ADS camera-to-gun transfer, and hands the degrees on with ignoreClamp = true.
    /// </summary>
    public static void RotatePrefix(Player __instance, ref Vector2 deltaRotation, ref bool ignoreClamp)
    {
        if (!__instance.IsYourPlayer || ignoreClamp || !Active || !Fresh)
        {
            return;
        }
        if (float.IsNaN(deltaRotation.x) || float.IsNaN(deltaRotation.y)
            || float.IsInfinity(deltaRotation.x) || float.IsInfinity(deltaRotation.y))
        {
            return;
        }
        var state = __instance.CurrentManagedState;
        if (state == null || !IsPlainState(state.Name))
        {
            return;
        }

        bool input = deltaRotation.sqrMagnitude > 1e-10f;
        Vector3 d3 = state.ClampRotation(deltaRotation);
        Vector2 deg = new(d3.x, d3.y);

        // neck: the view stays within the neck range of the body; past it, only as fast as the body turns
        float neck = Mathf.Lerp(FreeAimPlugin.NeckYaw.Value, FreeAimPlugin.AdsNeckYaw.Value, _ads);
        float rel = -BodyYaw; // view relative to body
        float next = rel + deg.x;
        float limit = Mathf.Max(neck, Mathf.Abs(rel));
        if (Mathf.Abs(next) > limit)
        {
            deg.x = Mathf.Sign(next) * limit - rel;
        }

        if (input && _leadAllowed)
        {
            Vector2 lead = deg * (FreeAimPlugin.LeadGain.Value - 1f);
            float keep = FreeAimPlugin.LeadKeep.Value;
            _leadKeep += lead * keep;
            _leadFast += lead * (1f - keep);
            Vector2 cone = new(FreeAimPlugin.ConeYaw.Value, FreeAimPlugin.ConePitch.Value);
            Vector2 total = _leadKeep + _leadFast;
            float norm = new Vector2(total.x / cone.x, total.y / cone.y).magnitude;
            if (norm > 1f)
            {
                _leadKeep /= norm;
                _leadFast /= norm;
            }
        }

        if (input)
        {
            SinceInput = 0f;
        }
        deltaRotation = deg;
        ignoreClamp = true;
    }

    // ------------------------------------------------------------------ per frame

    /// <summary>Body sway input D: the dynamic part of the game's HandsRotation spring (yaw, pitch).</summary>
    internal static Vector2 ReadGameSway(ProceduralWeaponAnimation pwa)
    {
        Vector3 c = pwa.HandsContainer.HandsRotation.Current; // x = pitch (+ down), y = yaw (+ right)
        return new Vector2(c.y, c.x);
    }

    /// <summary>Fixed mode or no model: make sure the input prefix passes everything through.</summary>
    internal static void Deactivate()
    {
        Active = false;
        Weight = 0f;
        _activeFade = 0f;
        Output = Vector2.zero;
    }

    /// <summary>Called from the weapon postfix every frame, before the weapon is posed.</summary>
    internal static void Step(Player player, ProceduralWeaponAnimation pwa, float dt, bool holdingGun, bool sprinting, Vector2 swayIn)
    {
        int id = player.GetInstanceID();
        if (id != _playerId)
        {
            _playerId = id;
            ResetAll();
        }
        _lastSample = Time.time;

        var mc = player.MovementContext;
        var state = player.CurrentManagedState;
        bool plain = state != null && IsPlainState(state.Name) && player.BtrState == EPlayerBtrState.Outside;
        bool aiming = pwa.IsAiming;
        bool stance = ReadyStance.Engaged;
        Active = FreeAimPlugin.Enabled.Value && holdingGun && plain;
        _ads = Mathf.MoveTowards(_ads, aiming ? 1f : 0f, dt / AdsFadeTime);
        // ADS is vanilla: the gun, its sway and recoil belong to the game while the camera sits on the scope.
        // Fade with the game's own aim weight so the gun reaches the centre as the camera reaches the scope.
        float aimWeight;
        try
        {
            aimWeight = AimingWeight(pwa);
        }
        catch
        {
            aimWeight = _ads;
        }
        _adsBlend = Mathf.Clamp01(aimWeight / 0.95f);
        _leadAllowed = Active && !aiming && !sprinting && !stance;
        SinceInput += dt;

        // view change this frame; world-anchored body and gun stay put while the view turns
        Vector2 view = mc.Rotation;
        Vector2 dV = _hasView ? new Vector2(Mathf.DeltaAngle(_lastView.x, view.x), Mathf.DeltaAngle(_lastView.y, view.y)) : Vector2.zero;
        _lastView = view;
        _hasView = true;
        dV -= RecoilFeedback.ConsumeInjected(); // recoil feedback moved the view, not the mouse
        bool jump = dt <= 0f || dV.magnitude > 45f; // vault, respawn, mount: keep relative state, drop velocities
        if (jump)
        {
            dV = Vector2.zero;
            _gunVel = _bodyVel = _swingVel = Vector2.zero;
        }
        Vector2 viewVel = dt > 0f ? dV / dt : Vector2.zero;
        // shoulder carry: the torso and shoulder turn with the head from the first instant (same motor command),
        // so this share of the view motion moves the gun at once; the arm spring only has to chase the rest
        float carry = FreeAimPlugin.Inertia.Value
            ? Mathf.Clamp(FreeAimPlugin.ShoulderCarry.Value / Mathf.Sqrt(Mathf.Max(0.1f, WeaponProfile.SwingScale)), 0f, 0.85f)
            : 0f;
        Carry = carry;
        Vector2 lastViewVelLp = _viewVelLp;
        _viewVelLp += (viewVel - _viewVelLp) * (1f - Mathf.Exp(-dt / CarryAccelSmoothing));
        Vector2 carryAccel = dt > 0f ? carry * (_viewVelLp - lastViewVelLp) / dt : Vector2.zero;
        BodyYaw -= dV.x;
        Gun -= dV * (1f - carry);
        _smooth -= dV * (1f - carry);

        float recentre = 1f - Mathf.Exp(-FreeAimPlugin.RecenterSpeed.Value * dt);
        Vector2 swayEff = swayIn * (1f - ReadyStance.Stabilization); // stance: the body is braced

        if (!Active)
        {
            BodyYaw = 0f;
            BodyRate = viewVel.x;
            _bodyVel = viewVel;
            _gunVel = viewVel * (1f - carry);
            _leadKeep -= _leadKeep * recentre;
            _leadFast -= _leadFast * recentre;
            Hold = Vector2.zero;
            float decay = Mathf.Exp(-dt / FadeTime);
            Gun *= decay;
            Sway *= decay;
            Swing *= decay;
            _swingVel = Vector2.zero;
            _smooth = Gun;
            _swayLow = swayEff;
            _swayInertia = Vector2.zero;
            _activeFade = Mathf.MoveTowards(_activeFade, 0f, dt / FadeTime);
            Weight = _activeFade;
            Output = (Gun + Swing + Sway) * Weight;
            return;
        }

        // ---- body (A): yaw follows the view with a limited turn rate
        BodyTurnLimit = TurnLimit(player, mc);
        float rel = -BodyYaw;
        float desired = Mathf.Clamp(rel / Mathf.Max(0.02f, FreeAimPlugin.BodyFollowTime.Value), -BodyTurnLimit, BodyTurnLimit);
        BodyRate += (desired - BodyRate) * (1f - Mathf.Exp(-dt / BodyRateSmoothing));
        float bodyStep = BodyRate * dt;
        if (Mathf.Abs(bodyStep) > Mathf.Abs(rel) && Mathf.Sign(bodyStep) == Mathf.Sign(rel))
        {
            bodyStep = rel; // don't swing past the view
        }
        BodyYaw += bodyStep;
        Vector2 bodyVel = new(dt > 0f ? bodyStep / dt : 0f, viewVel.y);

        // ---- lead (O)
        if (!_leadAllowed)
        {
            _leadKeep -= _leadKeep * recentre;
            _leadFast -= _leadFast * recentre;
        }
        else
        {
            _leadFast *= Mathf.Exp(-dt / Mathf.Max(0.05f, FreeAimPlugin.LeadFadeTime.Value));
            if (SinceInput > FreeAimPlugin.IdleDelay.Value)
            {
                _leadKeep *= Mathf.Exp(-dt * FreeAimPlugin.IdleRate.Value);
            }
        }

        // ---- arm: target T (+ gravity sag, + stance integral)
        float stanceW = ReadyStance.StanceWeight;
        Sag = FreeAimPlugin.HipSag.Value * WeaponProfile.SagScale
              * (1f + FreeAimPlugin.SagFatigue.Value * (1f - WeaponProfile.ArmFraction));
        Vector2 aim = Lead;
        Vector2 target = aim + new Vector2(0f, Sag) + Hold;

        // tensed arms in the ready stance and in ADS (cheek weld)
        float aimW = Mathf.Max(stanceW, _ads);
        ArmHz = WeaponProfile.SpringHz * Mathf.Lerp(1f, FreeAimPlugin.StanceArmStiffness.Value, aimW);
        float omega = 2f * Mathf.PI * ArmHz;
        float tauS = Mathf.Max(0.01f, WeaponProfile.SmoothTime);
        // feed-forward share that still guarantees no overshoot when a turn stops (forcing never changes sign)
        float k = Mathf.Clamp01(1f - 1f / (2f * omega * tauS));
        ArmSmooth = tauS;
        ArmFeedForward = k;
        Kappa = WeaponProfile.Kappa;
        _bodyVel = bodyVel;

        float swingScale = WeaponProfile.SwingScale;
        // swing peak ~ gain / omega: grows with scale^1.5 (inertia^0.75), heavy guns swing far and return slowly
        float swingGain = FreeAimPlugin.SwingGain.Value * Mathf.Sqrt(swingScale);
        float swingOmega = 2f * Mathf.PI * FreeAimPlugin.SwingHz.Value / swingScale;
        if (!FreeAimPlugin.Inertia.Value)
        {
            Gun = target;
            _smooth = target;
            _gunVel = bodyVel;
            Swing = _swingVel = Vector2.zero;
        }
        else
        {
            const float maxStep = 1f / 300f;
            int steps = Mathf.Clamp(Mathf.CeilToInt(dt / maxStep), 1, 32);
            float h = dt / steps;
            for (int i = 0; i < steps; i++)
            {
                // first-order target smoothing: finite acceleration at the start and end of every move
                Vector2 smoothVel = (target - _smooth) / tauS;
                _smooth += smoothVel * h;
                // critically damped spring with partial velocity feed-forward
                Vector2 accel = omega * omega * (_smooth - Gun) + 2f * omega * (k * smoothVel - _gunVel);
                _gunVel += accel * h;
                Gun += _gunVel * h;
                // gun swing: the gun's own inertia against the arm's acceleration (critically damped, one swing)
                Vector2 swingAccel = -swingGain * (accel + carryAccel) - swingOmega * swingOmega * Swing - 2f * swingOmega * _swingVel;
                _swingVel += swingAccel * h;
                Swing += _swingVel * h;
            }
            float swingMax = FreeAimPlugin.SwingMax.Value;
            for (int axis = 0; axis < 2; axis++)
            {
                if (Mathf.Abs(Swing[axis]) > swingMax)
                {
                    Swing[axis] = Mathf.Sign(Swing[axis]) * swingMax;
                    if (_swingVel[axis] * Swing[axis] > 0f)
                    {
                        _swingVel[axis] = 0f;
                    }
                }
            }
        }

        // arm reach relative to the body
        Vector2 reach = new(FreeAimPlugin.ArmYaw.Value, FreeAimPlugin.ArmPitch.Value);
        Vector2 body = new(BodyYaw, 0f);
        for (int axis = 0; axis < 2; axis++)
        {
            float off = Gun[axis] - body[axis];
            if (Mathf.Abs(off) > reach[axis])
            {
                Gun[axis] = body[axis] + Mathf.Sign(off) * reach[axis];
                if ((_gunVel[axis] + carry * viewVel[axis] - bodyVel[axis]) * off > 0f)
                {
                    _gunVel[axis] = bodyVel[axis] - carry * viewVel[axis];
                }
            }
        }

        // ---- stance integral: muscles hold the muzzle on the aim against sag and steady drift
        bool integrate = FreeAimPlugin.HoldEnabled.Value
                         && stance && !ReadyStance.Suspended && stanceW > 0.95f;
        HoldFrozen = Vector2.zero;
        if (integrate)
        {
            float tau = Mathf.Max(0.05f, WeaponProfile.HoldTau);
            float gain = Mathf.Min(1f, dt / tau);
            float max = FreeAimPlugin.HoldMax.Value;
            float rate = FreeAimPlugin.HoldFreezeRate.Value;
            float maxErr = FreeAimPlugin.HoldFreezeError.Value;
            Vector2 muzzle = Gun + Swing;
            for (int axis = 0; axis < 2; axis++)
            {
                // anti-windup: only steady, small errors (sag, steady drift); never while turning or swinging
                bool moving = Mathf.Abs(viewVel[axis]) > rate || Mathf.Abs(_gunVel[axis] + carry * viewVel[axis]) > rate
                              || Mathf.Abs(target[axis] - muzzle[axis]) > maxErr
                              || Mathf.Abs(Swing[axis]) > SwingStill;
                if (moving)
                {
                    HoldFrozen[axis] = 1f;
                    continue;
                }
                Hold[axis] = Mathf.Clamp(Hold[axis] + (aim[axis] - muzzle[axis]) * gain, -max, max);
            }
        }
        else
        {
            Hold *= Mathf.Exp(-dt / HoldRelease);
        }

        // ---- sway path: arm corrects the slow part, gun inertia smooths the fast part; gain <= 1
        if (!FreeAimPlugin.Inertia.Value)
        {
            Sway = swayEff;
        }
        else
        {
            float a = 1f - Mathf.Exp(-omega * dt);
            _swayLow += (swayEff - _swayLow) * a;
            Vector2 high = swayEff - _swayLow;
            _swayInertia += (high - _swayInertia) * a;
            Sway = (1f - Kappa) * high + Kappa * _swayInertia;
        }

        // fully in ADS: the arm sits on the aim (the game owns the gun), so leaving ADS starts from the centre
        if (_adsBlend >= 1f)
        {
            Gun = _smooth = aim;
            _gunVel = viewVel * (1f - carry);
            Swing = _swingVel = Vector2.zero;
            Sway = _swayInertia = Vector2.zero;
            _swayLow = swayEff;
        }

        _activeFade = Mathf.MoveTowards(_activeFade, 1f, dt / FadeTime);
        // ADS fades the model out (vanilla gun, sway and recoil); the body still limits the view (neck, turn rate)
        Weight = _activeFade * (1f - _adsBlend);
        Output = (Gun + Swing + Sway) * Weight;
    }

    private static float TurnLimit(Player player, MovementContext mc)
    {
        float limit = FreeAimPlugin.BodyTurnSpeed.Value * WeaponProfile.TurnFactor;
        try
        {
            limit *= Mathf.Lerp(FreeAimPlugin.CrouchTurn.Value, 1f, Mathf.Clamp01(mc.PoseLevel));
            limit *= Mathf.Lerp(1f, FreeAimPlugin.OverweightTurn.Value, Mathf.Clamp01(player.Physical.Overweight));
            if (mc.PhysicalConditionContainsAny(EPhysicalCondition.LeftLegDamaged | EPhysicalCondition.RightLegDamaged))
            {
                limit *= FreeAimPlugin.LegInjuryTurn.Value;
            }
        }
        catch
        {
            // keep the weapon-scaled limit
        }
        return Mathf.Max(30f, limit);
    }

    private static void ResetAll()
    {
        BodyYaw = BodyRate = 0f;
        Gun = Sway = Swing = Hold = Output = Vector2.zero;
        _leadKeep = _leadFast = _gunVel = _viewVelLp = _swingVel = _bodyVel = _swayLow = _swayInertia = _smooth = Vector2.zero;
        _hasView = false;
        _activeFade = 0f;
        _ads = _adsBlend = 0f;
        SinceInput = 0f;
    }

    // ------------------------------------------------------------------ weapon transform

    /// <summary>
    /// Takes the game's rerouted sway out of the weapon (fraction k): the dynamic pitch/yaw of HandsRotation
    /// (now body sway, fed back through the sway path) and all of SwaySpring (turn sway; our arm inertia
    /// replaces it). Roll is left in place. Recoil lives in other springs and is untouched.
    /// The game applied L(r) = Euler(r.x,0,r.z) * Euler(0,r.y,0) in parent space around RotationCenter.
    /// </summary>
    internal static void RemoveGameSway(ProceduralWeaponAnimation pwa, float k)
    {
        var hands = pwa.HandsContainer;
        Transform root = hands.WeaponRootAnim;
        if (k <= 0f || root == null || root.parent == null)
        {
            return;
        }
        Vector3 sway = hands.SwaySpring.Value;
        float displacement = DisplacementStr(pwa) * (pwa.IsAiming ? pwa.AimingDisplacementStr : 1f);
        Vector3 swayPart = displacement * new Vector3(sway.x, 0f, sway.z) + sway;
        Vector3 current = hands.HandsRotation.Current;
        Vector3 removed = new Vector3(current.x, current.y, 0f) + swayPart;
        if (removed.sqrMagnitude < 1e-8f)
        {
            return;
        }
        Vector3 full = hands.HandsRotation.Get() + swayPart;
        Vector3 reduced = full - k * removed;

        Quaternion applied = Quaternion.Euler(full.x, 0f, full.z) * Quaternion.Euler(0f, full.y, 0f);
        Quaternion wanted = Quaternion.Euler(reduced.x, 0f, reduced.z) * Quaternion.Euler(0f, reduced.y, 0f);
        Quaternion parent = root.parent.rotation;
        Quaternion correction = parent * wanted * Quaternion.Inverse(applied) * Quaternion.Inverse(parent);

        Vector3 pivot = root.TransformPoint(hands.RotationCenter);
        root.SetPositionAndRotation(pivot + correction * (root.position - pivot), correction * root.rotation);
    }

    internal static string Format() =>
        $"body {BodyYaw,5:0.0}° turn {BodyRate,4:0}/{BodyTurnLimit:0}°/s  lead Y{Lead.x,5:0.0} P{Lead.y,5:0.0}  " +
        $"gun Y{Gun.x,5:0.00} P{Gun.y,5:0.00}  sway Y{Sway.x,5:0.00} P{Sway.y,5:0.00}  swing Y{Swing.x,5:0.00} P{Swing.y,5:0.00}  sag {Sag:0.00} hold Y{Hold.x:0.00}{(HoldFrozen.x > 0f ? "*" : "")} P{Hold.y:0.00}{(HoldFrozen.y > 0f ? "*" : "")}  " +
        $"arm {ArmHz:0.0}Hz carry {Carry:0.00} smooth {ArmSmooth:0.00}s ff {ArmFeedForward:0.00} κ{Kappa:0.00} w{Weight:0.00} idle {Mathf.Min(SinceInput, 99f):0}s{(Active ? "" : " (inactive)")}";
}
