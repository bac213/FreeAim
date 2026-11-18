using EFT;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>
/// Measurement only (debug overlay on): splits the weapon deviation into a slow part (exponential
/// average, ~= the steady offset an integral term would trim) and a fast part (RMS around that average,
/// = bounce/breath rhythm). Angles in degrees: pitch + = muzzle down, yaw + = right.
///
/// Source: the game's own sway input (dynamic HandsRotation + turn sway, as applied in
/// ApplyComplexRotation), read from the springs before our stance removal.
/// Muzzle: where the muzzle actually points relative to the render camera, minus our free-aim offset.
/// </summary>
internal static class SwayProbe
{
    internal const float SlowTau = 1.5f; // s

    internal sealed class Stat
    {
        internal Vector2 Now;
        internal Vector2 Slow;
        internal Vector2 FastRms;
        private Vector2 _sq;
        private bool _init;

        internal void Add(Vector2 v, float dt)
        {
            Now = v;
            if (!_init || dt <= 0f)
            {
                Slow = v;
                _sq = Vector2.zero;
                _init = true;
            }
            else
            {
                float a = 1f - Mathf.Exp(-dt / SlowTau);
                Slow += (v - Slow) * a;
                Vector2 d = v - Slow;
                _sq += (new Vector2(d.x * d.x, d.y * d.y) - _sq) * a;
            }
            FastRms = new Vector2(Mathf.Sqrt(_sq.x), Mathf.Sqrt(_sq.y));
        }

        internal void Reset() => _init = false;

        internal string Format() =>
            $"now P{Now.x,5:0.00} Y{Now.y,5:0.00}  slow P{Slow.x,5:0.00} Y{Slow.y,5:0.00}  fast±P{FastRms.x:0.00} Y{FastRms.y:0.00}";
    }

    internal static readonly Stat Source = new();
    internal static readonly Stat Muzzle = new();

    private static float _nextLog;

    private static readonly AccessTools.FieldRef<ProceduralWeaponAnimation, float> DisplacementStr =
        AccessTools.FieldRefAccess<ProceduralWeaponAnimation, float>("_displacementStr");

    /// <summary>Called from the weapon postfix before RemoveSway (springs are unaffected by our edits anyway).</summary>
    internal static void SampleSource(ProceduralWeaponAnimation pwa, float dt)
    {
        if (!FreeAimPlugin.ShowOverlay.Value)
        {
            return;
        }
        var hands = pwa.HandsContainer;
        Vector3 sway = hands.SwaySpring.Value;
        float displacement = DisplacementStr(pwa) * (pwa.IsAiming ? pwa.AimingDisplacementStr : 1f);
        Vector3 dyn = hands.HandsRotation.Current + displacement * new Vector3(sway.x, 0f, sway.z) + sway;
        // hands frame: x = pitch (+ down), y = yaw (+ right)
        Source.Add(new Vector2(dyn.x, dyn.y), dt);
    }

    /// <summary>Called from OnGUI (repaint), after the whole frame has been posed.</summary>
    internal static void SampleMuzzle(Player player, Camera cam, Vector3 muzzleDir)
    {
        Vector2 applied = WeaponOffsetPatch.Applied; // x yaw +right, y pitch +down
        Vector2 offset = new(applied.y, applied.x);   // as (pitch, yaw)
        float dt = Time.deltaTime;
        Muzzle.Add(Angles(cam.transform.InverseTransformDirection(muzzleDir)) - offset, dt);

        // Split muzzle-vs-screen into weapon and camera parts, both against the camera's parent
        // (CameraContainer, the un-animated view direction): muzzle ~= weapon - camera.
        Transform camT = player.ProceduralWeaponAnimation.HandsContainer.CameraTransform;
        Transform view = camT != null ? camT.parent : null;
        if (view != null)
        {
            Weapon.Add(Angles(view.InverseTransformDirection(muzzleDir)) - offset, dt);
            CameraPart.Add(Angles(view.InverseTransformDirection(camT.forward)), dt);
            Vector3 v = view.InverseTransformDirection(player.Velocity);
            _fwd = v.z;
            _right = v.x;
        }

        if (Time.time >= _nextLog)
        {
            _nextLog = Time.time + 1f;
            var pwa = player.ProceduralWeaponAnimation;
            string stance = ReadyStance.Suspended ? "suspended" : ReadyStance.Engaged ? "stance" : "hip";
            int id = player.GetInstanceID();
            if (id != _loggedPlayer)
            {
                _loggedPlayer = id;
                Write($"--- raid start {System.DateTime.Now:yyyy-MM-dd HH:mm:ss} ---");
            }
            Write($"{System.DateTime.Now:HH:mm:ss} fwd {_fwd,5:0.00} right {_right,5:0.00} pose {player.Pose} {stance}{(pwa.IsAiming ? " ADS" : "")} " +
                  $"left {ReadyStance.LeftBlend:0.0} arm {player.Physical.HandsStamina.Current:0} " +
                  $"| source {Source.Format()} | muzzle {Muzzle.Format()} | weapon {Weapon.Format()} | camera {CameraPart.Format()} | model {AimModel.Format()} | {RecoilFeedback.Format()} | {WeaponProfile.Format()}");
        }
    }

    internal static readonly Stat Weapon = new();
    internal static readonly Stat CameraPart = new();
    private static float _fwd, _right;

    /// <summary>(pitch + down, yaw + right) in degrees of a direction in some local frame.</summary>
    internal static Vector2 Angles(Vector3 local)
    {
        float yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
        float pitch = -Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
        return new Vector2(pitch, yaw);
    }

    private static int _loggedPlayer;
    private static System.IO.StreamWriter _writer;
    private static bool _writeFailed;

    /// <summary>
    /// Own append-only file (BepInEx/FreeAim_sway.log), flushed per line: LogOutput.log is overwritten on
    /// every game start (AppendLog = false), which lost the first test run.
    /// </summary>
    private static void Write(string line)
    {
        if (_writeFailed)
        {
            return;
        }
        try
        {
            if (_writer == null)
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "FreeAim_sway.log");
                _writer = new System.IO.StreamWriter(path, append: true) { AutoFlush = true };
            }
            _writer.WriteLine(line);
        }
        catch (System.Exception e)
        {
            _writeFailed = true;
            FreeAimPlugin.Log.LogWarning($"FreeAim: sway log disabled: {e.Message}");
        }
    }

    internal static void Reset()
    {
        Source.Reset();
        Muzzle.Reset();
        Weapon.Reset();
        CameraPart.Reset();
    }
}
