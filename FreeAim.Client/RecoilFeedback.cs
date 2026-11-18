using System;
using EFT;
using EFT.Animations;
using EFT.InventoryLogic;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>
/// Recoil feedback into the view (hip fire and ready stance; ADS keeps the game's own camera-to-gun follow).
/// The body is moved by the shot's momentum through the stock, not by the muzzle flip: a pistol flips a lot at
/// the wrist but barely moves the body; a stocked pistol puts a small 9x18 impulse into the shoulder; a 7.62x51
/// rifle puts a big one. So every shot (InitiateShot postfix) gives an impulse
/// p = bullet mass x projectiles x velocity x (1 + velocity^2 / 1.5e6) (powder gas, see GasFactor),
/// relative to 5.45x39 7N6, times the stock transfer (shouldered 1, no stock / folded = small) and the non-stock
/// mods' recoil (half weight). Two parts, both injected through Player.Rotate:
/// <list type="bullet">
/// <item>View kick (main): head and view snap up and come back (critically damped, 6 Hz). The muzzle is
/// compensated by the same amount, so the bullets go where vanilla puts them.</item>
/// <item>Body push: upper body, view and arms pushed together (critically damped, upper body Hz), leaving a
/// residual that drifts back. This one moves the muzzle too, so it adds to recoil.</item>
/// </list>
/// The game's own muzzle recoil is untouched. The aim model is told about the injected view change, so it is
/// not treated as a mouse turn.
/// </summary>
internal static class RecoilFeedback
{
    internal static Vector2 Recoil;      // game muzzle recoil, view axes (deg), overlay only
    internal static Vector2 Follow;      // view kick currently in the view
    internal static float Push;          // push part currently in the view (pitch)
    internal static float Residual;      // push residual set point (pitch)
    internal static float LastImpulse;   // last shot, relative to the reference cartridge (before stock / mods)
    internal static float LastTransfer;  // stock x mods factor of the last shot

    /// <summary>Muzzle offset (relative to V) that keeps the view kick out of the bullets' direction.</summary>
    internal static Vector2 Compensation => -Follow;

    private static Vector2 _followVel;
    private static float _pushVel;
    private static float _shots;         // impulse queued since the last step (reference shots)
    private static float _shotYaw;       // signed sideways share queued
    private static Vector2 _pending;     // injected since the aim model last read the view
    private static int _playerId;

    private const float KickHz = 6f;
    private static readonly float ReferenceImpulse = 0.00342f * 880f * GasFactor(880f); // 5.45x39 7N6, N·s

    /// <summary>
    /// 1 + gas momentum / bullet momentum. Charge mass from energy: muzzle energy 1/2 m v^2 is ~30% of the powder
    /// energy (smokeless ~3.7 MJ/kg), so m_charge = m v^2 / 2.22e6; the gas leaves at ~1.5 v. Ratio = 1.5 v^2 / 2.22e6.
    /// Only bullet mass and velocity are needed, so any calibre (modded too) works.
    /// </summary>
    private static float GasFactor(float v) => 1f + v * v / 1.5e6f;
    private const float SidewaysShare = 0.3f;

    /// <summary>View change injected since the last call (the aim model subtracts it from the view delta).</summary>
    internal static Vector2 ConsumeInjected()
    {
        Vector2 v = _pending;
        _pending = Vector2.zero;
        return v;
    }

    /// <summary>Postfix on Player.FirearmController.InitiateShot (ClientFirearmController calls the base).</summary>
    public static void ShotPostfix(Player.FirearmController __instance, Ammo ammo)
    {
        try
        {
            var player = FreeAimPlugin.GetLocalPlayer();
            if (player == null || !ReferenceEquals(player.HandsController, __instance) || ammo == null)
            {
                return;
            }
            float v = Mathf.Max(0f, ammo.InitialSpeed);
            float p = ammo.BulletMassGram * 0.001f * Mathf.Max(1, ammo.ProjectileCount) * v * GasFactor(v);
            float rel = Mathf.Clamp(p / ReferenceImpulse, 0.05f, 5f);
            LastImpulse = rel;
            LastTransfer = WeaponProfile.ShotTransfer;
            float shot = rel * LastTransfer;
            _shots += shot;
            _shotYaw += shot * UnityEngine.Random.Range(-1f, 1f) * SidewaysShare;
        }
        catch (Exception)
        {
            // feedback is cosmetic; never break the shot
        }
    }

    /// <summary>Called from the weapon postfix after the aim model step.</summary>
    internal static void Step(Player player, ProceduralWeaponAnimation pwa, float dt, float ads, float stanceW)
    {
        int id = player.GetInstanceID();
        if (id != _playerId)
        {
            _playerId = id;
            Reset();
        }

        bool active = FreeAimPlugin.RecoilEnabled.Value && AimModel.Active && dt > 0f;
        Recoil = active ? ReadRecoil(player, pwa) : Vector2.zero;
        float hipW = 1f - ads; // ADS: the game's camera follows the gun already

        float shots = _shots;
        float shotYaw = _shotYaw;
        _shots = _shotYaw = 0f;
        float wk = 2f * Mathf.PI * KickHz;
        float wp = 2f * Mathf.PI * Mathf.Max(0.5f, FreeAimPlugin.RecoilBodyHz.Value);
        if (active && shots > 0f && hipW > 0f)
        {
            // critically damped mode: a velocity impulse v peaks at v / (w e), so v = peak * w * e
            float kick = Mathf.Lerp(FreeAimPlugin.RecoilKickHip.Value, FreeAimPlugin.RecoilKickStance.Value, stanceW) * hipW;
            float push = Mathf.Lerp(FreeAimPlugin.RecoilPush.Value, FreeAimPlugin.RecoilPushStance.Value, stanceW) * hipW;
            float e = (float)Math.E;
            _followVel += new Vector2(shotYaw, -shots) * (kick * wk * e); // pitch + = down
            _pushVel -= shots * push * wp * e;
            Residual -= shots * push * FreeAimPlugin.RecoilResidual.Value;
        }
        float tauR = Mathf.Max(0.05f, FreeAimPlugin.RecoilResidualReturn.Value);
        Residual *= Mathf.Exp(-dt / tauR);

        const float maxStep = 1f / 300f;
        int steps = Mathf.Clamp(Mathf.CeilToInt(dt / maxStep), 1, 32);
        float h = dt / steps;
        Vector2 followNext = Follow;
        float pushNext = Push;
        for (int i = 0; i < steps; i++)
        {
            Vector2 af = -wk * wk * followNext - 2f * wk * _followVel;
            _followVel += af * h;
            followNext += _followVel * h;
            float ap = wp * wp * (Residual - pushNext) - 2f * wp * _pushVel;
            _pushVel += ap * h;
            pushNext += _pushVel * h;
        }

        // ---- inject the change into the view
        Vector2 dFollow = followNext - Follow;
        float dPush = pushNext - Push;
        Vector2 want = dFollow + new Vector2(0f, dPush);
        if (want.sqrMagnitude < 1e-10f)
        {
            return;
        }
        var state = player.CurrentManagedState;
        if (state == null || !AimModel.IsPlainState(state.Name))
        {
            // can't rotate here: forget the feedback (the view keeps what it has)
            Reset();
            return;
        }
        var mc = player.MovementContext;
        Vector2 before = mc.Rotation;
        player.Rotate(want, true);
        Vector2 after = mc.Rotation;
        Vector2 got = new(Mathf.DeltaAngle(before.x, after.x), after.y - before.y);
        _pending += got;

        // share what actually happened (pitch limit) between the parts
        float sx = Mathf.Abs(want.x) > 1e-6f ? Mathf.Clamp01(got.x / want.x) : 1f;
        float sy = Mathf.Abs(want.y) > 1e-6f ? Mathf.Clamp01(got.y / want.y) : 1f;
        Follow += new Vector2(dFollow.x * sx, dFollow.y * sy);
        Push += dPush * sy;
        if (sy < 1f)
        {
            _pushVel = 0f;
            _followVel.y = 0f;
        }
    }

    /// <summary>Muzzle recoil rotation as view yaw / pitch (deg), for the overlay. The game rotates the weapon about
    /// world axes Euler(rootRotation * r); for these small angles that is the rotation vector rootRotation * r.</summary>
    private static Vector2 ReadRecoil(Player player, ProceduralWeaponAnimation pwa)
    {
        try
        {
            var fx = pwa.Shootingg?.CurrentRecoilEffect;
            if (fx == null)
            {
                return Vector2.zero;
            }
            var hand = fx.HandRotationRecoilEffect;
            Vector3 r = hand.Current - (hand.UseOffset ? hand.Offset : Vector3.zero);
            if (fx.WeaponRecoilEffect != null)
            {
                r += fx.WeaponRecoilEffect.GetRotationRecoil();
            }
            Transform root = pwa.HandsContainer.WeaponRootAnim;
            if (root == null)
            {
                return Vector2.zero;
            }
            Vector3 world = root.rotation * r;
            Vector2 view = player.MovementContext.Rotation;
            Quaternion look = Quaternion.Euler(view.y, view.x, 0f);
            float yaw = Vector3.Dot(world, look * Vector3.up);
            float pitch = Vector3.Dot(world, look * Vector3.right);
            Vector2 v = new(yaw, pitch);
            return float.IsNaN(v.x) || float.IsNaN(v.y) ? Vector2.zero : v;
        }
        catch (Exception)
        {
            return Vector2.zero;
        }
    }

    internal static void Reset()
    {
        Follow = _followVel = Vector2.zero;
        Push = _pushVel = Residual = _shots = _shotYaw = 0f;
    }

    internal static string Format() =>
        $"muzzle recoil Y{Recoil.x,5:0.00} P{Recoil.y,5:0.00}  view kick Y{Follow.x,5:0.00} P{Follow.y,5:0.00}  push {Push,5:0.00} residual {Residual,5:0.00}  " +
        $"last shot p x{LastImpulse:0.00} transfer x{LastTransfer:0.00}";
}
