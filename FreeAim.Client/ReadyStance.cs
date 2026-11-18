using System;
using System.ComponentModel;
using System.Linq;
using BepInEx.Configuration;
using EFT;
using EFT.Animations;
using EFT.Communications;
using HarmonyLib;
using UnityEngine;

namespace FreeAim.Client;

public enum StanceKeyMode
{
    [Description("切换")]
    Toggle,
    [Description("按住")]
    Hold,
}

internal enum ReadyPose
{
    None,
    Braced,
    Low,
    High,
}

/// <summary>
/// Braced tactical ready stance (CAR-style): the gun is pulled to the centre line, canted around the
/// bore, and the free-aim offset collapses so the crosshair is back at screen centre (vanilla hip fire).
/// Holding it costs arm stamina through a game-native <see cref="Physical.Consumption"/>.
/// </summary>
internal static class ReadyStance
{
    /// <summary>Player wants the stance (key state).</summary>
    private static ReadyPose _requested;

    internal static ReadyPose Current { get; private set; }
    internal static bool Engaged => Current != ReadyPose.None;
    internal static bool Braced => Current == ReadyPose.Braced;

    // Separate blends allow a direct high-to-low transition without a step in pitch.
    private static float _low;
    private static float _high;
    private static float _lowBodyFollow;
    private static int _stanceFrame = -2;
    private static Stamina _recoveringHands;
    private static float LowPitchBlend => Mathf.SmoothStep(0f, 1f, _low);
    internal static float ReadyPitch => LowPitchBlend * FreeAimPlugin.LowReadyPitch.Value
        - _aimFade * Mathf.SmoothStep(0f, 1f, _high) * FreeAimPlugin.HighReadyPitch.Value;
    internal static float LowReadyAdsLimit { get; private set; } = 1f;
    internal static float BodyFollowMultiplier => Mathf.Lerp(1f, FreeAimPlugin.ReadyBodyFollow.Value,
        Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(_lowBodyFollow + _high)) * _aimFade);

    internal static float ScaleArmRecovery(float rate, Stamina stamina) =>
        stamina == _recoveringHands && _requested == ReadyPose.Low && Time.frameCount - _stanceFrame <= 1
        && FreeAimPlugin.StanceEnabled.Value && PointedReadyEnabled
            ? rate * FreeAimPlugin.LowReadyRecovery.Value
            : rate;

    internal static bool LowReadyActive => Current == ReadyPose.Low && _requested == ReadyPose.Low && !Suspended
        && Time.frameCount - _stanceFrame <= 1 && FreeAimPlugin.StanceEnabled.Value && PointedReadyEnabled;

    private static bool PointedReadyEnabled => FreeAimPlugin.Enabled.Value && FreeAimPlugin.Mode.Value == AimMode.Deadzone;

    /// <summary>Engaged, but the hands are busy (reload etc.): pose eased out, no drain, resumes afterwards.</summary>
    internal static bool Suspended;

    /// <summary>Visual pose weight 0..1 (raise blend, faded out in ADS).</summary>
    internal static float PoseWeight;

    /// <summary>Arms ran out; stance blocked until stamina recovers to the re-entry threshold.</summary>
    internal static bool Exhausted;

    private static float _raise;     // 0..1 raise progress
    private static float _aimFade;   // 0..1, 1 = not aiming
    private static float _drainRate; // stamina/s, read by the consumption delegate

    // animator LEFT_STANCE_CURVE, fed every frame by Player (0 = right shoulder, 1 = left shoulder).
    // Note: the public PWA.LeftStanceCurrentCurveValue getter does NOT return this field.
    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, float> LeftStanceCurve =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, float>("_leftStanceCurrentCurveValue");

    /// <summary>Lateral correction applied this frame (m, + = right), for the overlay.</summary>
    internal static float CentreShift;

    /// <summary>Left-shoulder blend 0..1 seen this frame, for the overlay.</summary>
    internal static float LeftBlend;

    /// <summary>Centred variant (right shoulder): bore slid onto the screen's vertical centre line (CAR centre hold).</summary>
    internal static bool Centred;

    /// <summary>Left-shoulder pose weight applied in the body pass this frame, for the overlay.</summary>
    internal static float LeftPoseWeight;

    private static bool _wasBraced;
    private static float _baseWeight; // raise x ADS fade, before the shoulder split

    /// <summary>
    /// Body bracing right now: fraction of the body sway (breath, steps, motion) suppressed before it reaches
    /// the arm model (AimModel). Stability x stance weight.
    /// </summary>
    internal static float Stabilization => FreeAimPlugin.StanceStability.Value * _baseWeight;

    /// <summary>Stance weight 0..1 (raise x ADS fade), used by the arm model (stiffness, integral).</summary>
    internal static float StanceWeight => _baseWeight;

    private static Physical.Consumption _consumption;
    private static Action _stopConsumption;
    private static PhysicalBase _consumptionOwner;
    private static int _playerId;

    // ---------- input (called from plugin Update) ----------

    internal static void UpdateInput()
    {
        if (!FreeAimPlugin.StanceEnabled.Value)
        {
            _requested = ReadyPose.None;
            return;
        }

        var key = FreeAimPlugin.StanceKey.Value;
        bool pressed = IsPressedThisFrame(key);
        ReadyPose pose = PoseForModifiers(key);
        if (FreeAimPlugin.StanceMode.Value == StanceKeyMode.Hold)
        {
            if (!IsHeld(key))
            {
                _requested = ReadyPose.None;
                return;
            }
            if (pose == ReadyPose.Braced && !CanEnter())
            {
                if (pressed)
                {
                    RejectEntry();
                }
                pose = ReadyPose.None;
            }
            _requested = pose;
        }
        else if (pressed && pose != ReadyPose.None)
        {
            if (_requested == pose)
            {
                _requested = ReadyPose.None;
            }
            else if (pose != ReadyPose.Braced || CanEnter())
            {
                _requested = pose;
            }
            else
            {
                RejectEntry();
            }
        }
    }

    private static ReadyPose PoseForModifiers(KeyboardShortcut key)
    {
        // Required modifiers of a custom shortcut belong to its base (braced) pose.
        bool ctrl = ExtraModifierHeld(key, KeyCode.LeftControl, KeyCode.RightControl);
        bool alt = ExtraModifierHeld(key, KeyCode.LeftAlt, KeyCode.RightAlt);
        if ((ctrl && alt) || ((ctrl || alt) && !PointedReadyEnabled))
        {
            return ReadyPose.None;
        }
        return ctrl ? ReadyPose.Low : alt ? ReadyPose.High : ReadyPose.Braced;
    }

    private static bool ExtraModifierHeld(KeyboardShortcut key, KeyCode left, KeyCode right) =>
        !key.Modifiers.Contains(left) && !key.Modifiers.Contains(right) && (Input.GetKey(left) || Input.GetKey(right));

    /// <summary>Entry is blocked while the arms are (or just became) exhausted.</summary>
    private static bool CanEnter()
    {
        if (Exhausted)
        {
            return false;
        }
        // stamina may already be at 0 (ADS, StaminaZero health effect) without us having latched yet
        var hands = FreeAimPlugin.GetLocalPlayer()?.Physical?.HandsStamina;
        if (hands != null && hands.Current <= 1f)
        {
            Exhausted = true;
            return false;
        }
        return true;
    }

    private static void RejectEntry()
    {
        var hands = FreeAimPlugin.GetLocalPlayer()?.Physical?.HandsStamina;
        string now = hands != null ? $"{hands.Current:0}" : "?";
        string need = hands != null ? $"{ReentryThreshold(hands):0}" : "?";
        Notify($"手臂耐力不足，无法据枪（{now}/{need}）");
        FreeAimPlugin.Log.LogInfo($"FreeAim: stance entry rejected, arm stamina {now} < re-entry {need}");
    }

    /// <summary>
    /// Re-entry threshold, capped at half of the current capacity so a reduced capacity (skills, buffs)
    /// can never lock the stance out permanently.
    /// </summary>
    private static float ReentryThreshold(Stamina hands)
    {
        float capacity = 100f;
        try
        {
            capacity = hands.TotalCapacity;
        }
        catch
        {
            // keep default
        }
        return Mathf.Min(FreeAimPlugin.StanceReentryStamina.Value, capacity * 0.5f);
    }

    private static void Notify(string message)
    {
        if (!FreeAimPlugin.StanceNotifications.Value)
        {
            return;
        }
        try
        {
            NotificationManager.DisplayWarningNotification(message, ENotificationDurationType.Default);
        }
        catch (Exception e)
        {
            FreeAimPlugin.Log.LogWarning($"FreeAim: notification failed: {e.Message}");
        }
    }

    // BepInEx KeyboardShortcut.IsDown() fails while any other key is held (e.g. WASD while moving),
    // so test only the main key and its own modifiers.
    internal static bool IsPressedThisFrame(KeyboardShortcut shortcut)
        => shortcut.MainKey != KeyCode.None && Input.GetKeyDown(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);

    internal static bool IsHeld(KeyboardShortcut shortcut)
        => shortcut.MainKey != KeyCode.None && Input.GetKey(shortcut.MainKey) && shortcut.Modifiers.All(Input.GetKey);

    // ---------- per frame (called from the weapon postfix, before free-aim sampling) ----------

    internal static void Step(Player player, ProceduralWeaponAnimation pwa, bool holdingGun, bool sprinting, float dt)
    {
        int id = player.GetInstanceID();
        if (id != _playerId)
        {
            _playerId = id;
            _requested = ReadyPose.None;
            Exhausted = false;
            _raise = _low = _high = _lowBodyFollow = 0f;
            _aimFade = 1f;
            _wasBraced = false;
            _baseWeight = 0f;
            Centred = false;
            StopDrain();
        }

        var hands = player.Physical?.HandsStamina;
        if (hands != null)
        {
            if (!Exhausted && _requested == ReadyPose.Braced && hands.Current <= 1f)
            {
                Exhausted = true; // arms gave out: drop the stance
                _requested = ReadyPose.None;
                Notify("手臂耐力耗尽，退出据枪");
                FreeAimPlugin.Log.LogInfo("FreeAim: stance dropped, arm stamina exhausted");
            }
            else if (Exhausted && hands.Current >= ReentryThreshold(hands))
            {
                Exhausted = false;
                FreeAimPlugin.Log.LogInfo($"FreeAim: arm stamina recovered to {hands.Current:0}, stance available");
            }
        }
        if (Exhausted && _requested == ReadyPose.Braced)
        {
            _requested = ReadyPose.None;
        }

        bool pointed = _requested is ReadyPose.Low or ReadyPose.High;
        bool canPose = FreeAimPlugin.StanceEnabled.Value && holdingGun && !sprinting;
        var state = player.CurrentManagedState;
        bool canPoint = canPose && PointedReadyEnabled && state != null && AimModel.IsPlainState(state.Name)
            && player.BtrState == EPlayerBtrState.Outside;
        Current = (pointed ? canPoint : canPose) ? _requested : ReadyPose.None;
        bool aiming = pwa.IsAiming;
        bool busy = (Engaged || (canPoint && _low > 0f))
            && player.HandsController is Player.FirearmController busyFc && HandsBusy(busyFc);
        // Keep the request while the hands are busy or a high/low stance is interrupted by ADS.
        Suspended = Engaged && ((pointed && aiming) || busy);
        HandleShoulder();

        float raiseTime = Mathf.Max(0.05f, FreeAimPlugin.StanceRaiseTime.Value * WeaponProfile.RaiseScale);
        float step = dt / raiseTime;
        _raise = Mathf.MoveTowards(_raise, Braced && !Suspended ? 1f : 0f, step);
        bool lowReady = Current == ReadyPose.Low && !Suspended;
        float lowTarget = lowReady ? 1f : 0f;
        bool raisingLow = _low > 0f && !lowReady && canPoint && !busy;
        // Keep the standard timing for lowering, body follow, and interruptions such as sprint/reload.
        float lowStep = raisingLow
            ? dt / Mathf.Max(0.05f, FreeAimPlugin.LowReadyRaiseTime.Value * WeaponProfile.RaiseScale)
            : step;
        _low = Mathf.MoveTowards(_low, lowTarget, lowStep);
        _lowBodyFollow = Mathf.MoveTowards(_lowBodyFollow, lowTarget, step);
        _high = Mathf.MoveTowards(_high, Current == ReadyPose.High && !Suspended ? 1f : 0f, step);
        LowReadyAdsLimit = raisingLow && aiming ? 1f - LowPitchBlend : 1f;
        _aimFade = Mathf.MoveTowards(_aimFade, aiming ? 0f : 1f, dt / 0.12f);
        _stanceFrame = Time.frameCount;
        _recoveringHands = Current == ReadyPose.Low && !Suspended ? hands : null;
        // The PWA-pass pose is built on the right-shoulder weapon frame. While the game's left-shoulder
        // animation is blended in, PlayerBones.ShiftWeaponRoot remaps the weapon into the third-person
        // frame, so the PWA pose fades out by the shoulder blend and the left-shoulder pose (body pass,
        // ApplyBodyPass) fades in by it. This curve value is last frame's; the body pass uses the current one.
        LeftBlend = Mathf.Clamp01(LeftStanceCurve(pwa));
        _baseWeight = Mathf.SmoothStep(0f, 1f, _raise) * _aimFade;
        PoseWeight = _baseWeight * (1f - LeftBlend);

        // ADS already drains through the game's own Aim consumption; don't double-charge.
        bool drain = Braced && !Suspended && !pwa.IsAiming && FreeAimPlugin.StanceDrainMultiplier.Value > 0f;
        if (drain && player.HandsController is Player.FirearmController fc)
        {
            _drainRate = ComputeDrainRate(player, fc) * FreeAimPlugin.StanceDrainMultiplier.Value;
            StartDrain(player.Physical);
        }
        else
        {
            _drainRate = 0f;
            StopDrain();
        }
    }

    /// <summary>
    /// The support hand is off the gun or the hands are working the weapon: magazine reloads, chambering
    /// and single-round loading (IsInReloadOperation), malfunction handling, unloading, stock folding,
    /// attachment changes, launcher toggling, and left-hand actions (looting, gestures, watch, compass).
    /// Bolt/pump cycling after a shot (RechamberOperation) is deliberately not included.
    /// </summary>
    private static bool HandsBusy(Player.FirearmController fc)
    {
        try
        {
            if (fc.IsInReloadOperation() || fc.IsInInteraction())
            {
                return true;
            }
            var op = fc.CurrentOperation;
            return op is Player.FirearmController.MalfunctionOperation
                || op is Player.FirearmController.DischargeOperation
                || op is Player.FirearmController.DischargeAmmoFromCamoraOperation
                || op is Player.FirearmController.FoldStockOperation
                || op is Player.FirearmController.AddModOperation
                || op is Player.FirearmController.RemoveModOperation
                || op is Player.FirearmController.ToggleLauncherOperation;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The stance works on both shoulders and never switches shoulder itself; the game's shoulder key
    /// keeps working inside the stance and the pose follows the shoulder blend. On the right shoulder
    /// the variant (normal/centred) is picked on entry.
    /// </summary>
    private static void HandleShoulder()
    {
        bool braced = _requested == ReadyPose.Braced;
        if (braced && !_wasBraced)
        {
            Centred = FreeAimPlugin.StanceDefaultCentred.Value;
        }
        _wasBraced = braced;
    }

    /// <summary>Same formula the game uses for ADS (Physical, EConsumptionType.Aim), before our multiplier.</summary>
    private static float ComputeDrainRate(Player player, Player.FirearmController fc)
    {
        try
        {
            var mc = player.MovementContext;
            float armFactor = mc.PhysicalConditionIs(EPhysicalCondition.OnPainkillers) ? 1f
                : mc.PhysicalConditionIs(EPhysicalCondition.LeftArmDamaged | EPhysicalCondition.RightArmDamaged) ? 2f
                : mc.PhysicalConditionContainsAny(EPhysicalCondition.LeftArmDamaged | EPhysicalCondition.RightArmDamaged) ? 1.5f
                : 1f;

            float poseFactor = 1f;
            float hydration = 1f;
            if (player.Physical is Physical phys)
            {
                poseFactor = phys._aimMultiplierByPose[(int)phys._pose];
                hydration = phys.HydrationFactor;
            }
            float strength = 1f - (float)player.Skills.StrengthBuffAimFatigue;
            return Mathf.Sqrt(Mathf.Max(0f, fc.ErgonomicWeight)) * player.Physical.StaminaParameters.AimDrainRate
                   * armFactor * poseFactor * strength * hydration;
        }
        catch (Exception e)
        {
            FreeAimPlugin.Log.LogWarning($"FreeAim: stance drain formula failed, using fallback: {e.Message}");
            return 3f;
        }
    }

    private static void StartDrain(PhysicalBase physical)
    {
        if (physical == null)
        {
            return;
        }
        if (_consumptionOwner != physical)
        {
            StopDrain();
            _consumption = null;
            _consumptionOwner = physical;
        }
        if (_stopConsumption != null)
        {
            return; // already attached; delegate reads _drainRate every frame
        }
        _consumption ??= new Physical.Consumption(Physical.EConsumptionType.Aim)
        {
            Delta = new Compute<float>(() => _drainRate), // Compute re-evaluates every read
            Requires = Physical.EConsumptionTarget.None,
            AllowsRestoration = false,   // like ADS: no regen while holding the stance
            Downtime = 0.5f,
            PrimaryTarget = Physical.EConsumptionTarget.Hands,
        };
        _stopConsumption = physical.HandsStamina.AddConsumption(_consumption);
    }

    private static void StopDrain()
    {
        _stopConsumption?.Invoke();
        _stopConsumption = null;
    }

    // ---------- visual pose (called at the end of the weapon postfix, right-shoulder frame) ----------

    internal static void ApplyPose(ProceduralWeaponAnimation pwa, Player.FirearmController fc)
    {
        _poseFrame = Time.frameCount; // holding a gun this frame: the body pass may apply its part
        float w = PoseWeight;
        if (w <= 0f)
        {
            return;
        }
        var hands = pwa.HandsContainer;
        Transform root = hands.WeaponRootAnim;
        // Only the camera's orientation is used here (cant axis, offset directions); positional
        // measurement against the camera happens in ApplyCentring.
        Transform cam = hands.CameraTransform;
        Transform fireport = fc.CurrentFireport?.Original;
        if (root == null || cam == null || fireport == null)
        {
            return;
        }

        float centre = Centred ? FreeAimPlugin.StanceCentreCentred.Value : FreeAimPlugin.StanceCentreNormal.Value;

        // Cant around the bore line (axis through the muzzle along the shot direction):
        // muzzle position and shot direction are unchanged, so bullets still go to screen centre.
        float cant = (Centred ? FreeAimPlugin.StanceCantCentred.Value : FreeAimPlugin.StanceCant.Value) * w;
        if (Mathf.Abs(cant) > 0.01f)
        {
            Vector3 axis = fireport.TransformDirection(pwa.ShotDirection).normalized;
            Vector3 pivot = fireport.position;
            // Unity is left-handed: +angle about the forward axis turns the top of the gun to the left.
            Quaternion roll = Quaternion.AngleAxis(cant, axis);
            root.SetPositionAndRotation(pivot + roll * (root.position - pivot), roll * root.rotation);
        }

        // Pull the gun toward the centre line / up / in (camera space, metres). Translation only, so the
        // shot direction is unchanged; only the muzzle origin shifts by a few cm.
        Vector3 offset = FreeAimPlugin.StanceOffset.Value;
        offset.x *= 1f - centre; // the sideways shift is superseded by centring
        offset *= w;
        if (offset != Vector3.zero)
        {
            root.position += cam.right * offset.x + cam.up * offset.y + cam.forward * offset.z;
        }

        // The sideways centring is applied later, in the body pass (ApplyBodyPass), once the arms rig
        // has been moved onto this frame's ribcage. See ApplyBodyPass for why.
    }

    private static int _poseFrame = -1;

    /// <summary>
    /// Body-pass part of the pose, called from a postfix on PlayerBones.ShiftWeaponRoot (after MouseLook
    /// placed the arms rig on this frame's ribcage, after the game's left-shoulder remap, before hand IK).
    /// Left shoulder: the whole stance pose (cant, view-space offset) is built here, because the game
    /// rebuilds the weapon from the third-person frame in ShiftWeaponRoot and would remap a PWA-pass pose
    /// (the 0.0.7 failure). The game stores its left-shoulder smoothing state before returning, so this
    /// postfix does not feed back into the next frame.
    /// Right shoulder high ready: lower the grips and move the raised gun right before hand IK.
    /// Both shoulders: the sideways centring slide. During the PWA pass the arms rig still sits where
    /// last frame's MouseLook put it, so a correction measured against the camera there is off by one
    /// frame of player movement and jitters while strafing (the 0.0.9 failure).
    /// Measured against HandsContainer.CameraTransform ("Cam"), never the render camera: the render
    /// camera copies Cam only later in the frame (FirstPersonCameraState.ManualLateUpdate), which is the
    /// likely cause of the 0.0.8 jitter. On the left shoulder the game's camera pass runs after this
    /// (LateTransformations), so Cam's local transform is last frame's, but its parent was already moved
    /// this frame and the hip-fire camera target does not depend on the weapon.
    /// </summary>
    internal static void ApplyBodyPass(Player player, float leftStanceCurve)
    {
        CentreShift = 0f;
        LeftPoseWeight = 0f;
        if (_poseFrame != Time.frameCount)
        {
            return; // no gun in hands this frame
        }
        _poseFrame = -1;
        float baseWeight = _baseWeight;
        if (player.HandsController is not Player.FirearmController fc)
        {
            StanceClearance.Reset();
            return;
        }
        var pwa = player.ProceduralWeaponAnimation;
        var hands = pwa.HandsContainer;
        Transform root = hands.WeaponRootAnim;
        Transform cam = hands.CameraTransform;
        if (root == null || cam == null)
        {
            return;
        }

        float left = Mathf.Clamp01(leftStanceCurve);
        float highWeight = PointedReadyEnabled && FreeAimPlugin.StanceEnabled.Value
            ? Mathf.SmoothStep(0f, 1f, _high) * _aimFade * (1f - left)
            : 0f;
        if (highWeight > 0f)
        {
            HighReadyPlacement.Apply(player, root, cam, highWeight);
        }
        if (baseWeight <= 0f)
        {
            StanceClearance.Reset();
            return;
        }
        Transform fireport = fc.CurrentFireport?.Original;
        if (fireport == null)
        {
            return;
        }

        float wl = baseWeight * left;
        LeftPoseWeight = wl;
        float centreLeft = FreeAimPlugin.StanceCentreLeft.Value;

        if (wl > 0f)
        {
            // Same clockwise cant as on the right shoulder (top of the gun to the left), about the bore.
            float cant = FreeAimPlugin.StanceCant.Value * wl;
            if (Mathf.Abs(cant) > 0.01f)
            {
                Vector3 bore = fireport.TransformDirection(pwa.ShotDirection).normalized;
                Vector3 pivot = fireport.position;
                Quaternion roll = Quaternion.AngleAxis(cant, bore);
                root.SetPositionAndRotation(pivot + roll * (root.position - pivot), roll * root.rotation);
            }

            // Pose offset with x mirrored: "toward the centre line" is to the right on this shoulder.
            Vector3 offset = FreeAimPlugin.StanceOffset.Value;
            offset.x = -offset.x * (1f - centreLeft);
            offset *= wl;
            if (offset != Vector3.zero)
            {
                root.position += cam.right * offset.x + cam.up * offset.y + cam.forward * offset.z;
            }
        }

        float centreRight = Centred ? FreeAimPlugin.StanceCentreCentred.Value : FreeAimPlugin.StanceCentreNormal.Value;
        float weight = centreRight * baseWeight * (1f - left) + centreLeft * wl;
        if (weight > 0f)
        {
            ApplyCentring(pwa, fc, root, cam, fireport, weight);
        }

        // last: lower the gun until the screen centre is clear (measures the final pose of this frame)
        StanceClearance.Apply(pwa, root, cam, baseWeight, Time.deltaTime);
    }

    private static void ApplyCentring(ProceduralWeaponAnimation pwa, Player.FirearmController fc, Transform root, Transform cam,
        Transform fireport, float weight)
    {
        var hands = pwa.HandsContainer;

        // Measure where the bore line crosses the depth of the game's rotation pivot (stable under the
        // game's rotational sway, which pivots there) and slide the gun sideways by that amount. The
        // game rebuilds the weapon transform every frame, so this is measured fresh without feedback.
        Vector3 axis = fireport.TransformDirection(pwa.ShotDirection).normalized;
        Vector3 pivotPoint = root.TransformPoint(hands.RotationCenter);
        float along = Vector3.Dot(axis, cam.forward);
        Vector3 borePoint = fireport.position;
        if (along > 0.2f)
        {
            float depthGap = Vector3.Dot(pivotPoint - fireport.position, cam.forward);
            borePoint += axis * (depthGap / along);
        }
        float lateral = Vector3.Dot(borePoint - cam.position, cam.right);
        float maxShift = FreeAimPlugin.StanceCentreMaxShift.Value;
        CentreShift = Mathf.Clamp(-lateral, -maxShift, maxShift) * weight;
        root.position += cam.right * CentreShift;
    }
}
