using System;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>
/// Per-weapon handling derived from the gun in hands: weight, length (base to muzzle, incl. suppressor)
/// and total ergonomics (incl. skills; 0 with an injured arm).
/// <list type="bullet">
/// <item>Gun follow spring (PD): omega (response) falls and zeta (damping) drops below 1 as the
/// rotational inertia m·L² grows, so heavy/long guns respond slower and swing past the aim point.</item>
/// <item>Stance hold (I): its time constant grows with inertia, poor ergonomics and tired arms.</item>
/// </list>
/// Everything is relative to a reference gun (config), which gets exactly the base values from
/// "1b. Gun inertia" and "1d. Stance hold".
/// </summary>
internal static class WeaponProfile
{
    private const float ErgoLow = 20f;
    private const float ErgoHigh = 80f;
    private const float RefreshInterval = 0.5f; // s; attachments and magazine weight change rarely

    internal static float Mass;         // kg
    internal static float Length;       // m
    internal static float Ergonomics;   // total
    internal static float InertiaRatio = 1f; // J / J0 (measured mass distribution, else m L^2 / (m0 L0^2))
    internal static float SimpleRatio = 1f;  // m L^2 / (m0 L0^2), for comparison
    internal static float ErgoFactor = 1f;   // response multiplier from ergonomics

    /// <summary>Gun follow spring frequency (Hz) and damping ratio for the gun in hands.</summary>
    internal static float SpringHz => Active ? _springHz : FreeAimPlugin.SpringHz.Value;
    /// <summary>Arm target smoothing time (s): heavier / less ergonomic guns start and stop more gently.</summary>
    internal static float SmoothTime => Active ? _smoothTime : FreeAimPlugin.ArmSmoothing.Value;

    /// <summary>Stance hold time constant (s) right now, incl. arm fatigue.</summary>
    internal static float HoldTau
    {
        get
        {
            float tau = FreeAimPlugin.HoldTau.Value;
            if (!Active)
            {
                return tau;
            }
            return tau * _holdFactor * (1f + FreeAimPlugin.ProfileFatigueHold.Value * (1f - _armFraction));
        }
    }

    /// <summary>Body turn-rate multiplier: heavy/long guns turn the body slower (inertia^-exponent, clamped).</summary>
    internal static float TurnFactor => Active ? _turnFactor : 1f;

    /// <summary>Share of fast body sway the gun's inertia resists: J / (J + reference). Reference gun = 0.5 at reference 1.</summary>
    internal static float Kappa => Active ? _kappa : 0.5f;

    /// <summary>Stance raise time multiplier: sqrt(inertia^exponent / ergonomics factor), clamped.</summary>
    internal static float RaiseScale => Active ? Mathf.Clamp(Mathf.Sqrt(_holdFactor), 0.6f, 1.8f) : 1f;

    /// <summary>
    /// Share of a shot's impulse that reaches the body through the gun: shouldered stock 1, no stock or folded
    /// "No stock transfer" (wrists and arms absorb it); times the non-stock mods' recoil change at half weight
    /// (muzzle brakes, compensators), 0.5..1.2. Integral stocks (no stock slot, not a pistol) count as shouldered.
    /// </summary>
    internal static float ShotTransfer => _valid
        ? (_shouldered ? 1f : FreeAimPlugin.RecoilNoStock.Value) * _modsRecoil
        : 1f;

    internal static bool Shouldered => _shouldered;

    /// <summary>Gun swing scale: inertia^exponent (gain up, return frequency down for heavy/long guns).</summary>
    internal static float SwingScale => Active ? _swingScale : 1f;

    /// <summary>Gravity sag scale: weight x length (moment about the shoulder) over the reference gun.</summary>
    internal static float SagScale => Active ? _sagScale : 1f;

    /// <summary>Arm stamina 0..1.</summary>
    internal static float ArmFraction => _armFraction;

    private static bool Active => FreeAimPlugin.ProfileEnabled.Value && _valid;
    private static float _turnFactor = 1f;
    private static float _kappa = 0.5f;
    private static float _sagScale = 1f;
    private static float _swingScale = 1f;
    private static bool _shouldered = true;
    private static float _modsRecoil = 1f;

    private static bool _valid;
    private static float _springHz = 4f;
    private static float _smoothTime = 0.1f;
    private static float _holdFactor = 1f;
    private static float _armFraction = 1f;
    private static object _controller;
    private static float _nextRefresh;

    /// <summary>Called once per frame from the weapon postfix, before the gun spring step.</summary>
    internal static void Update(Player player)
    {
        try
        {
            _armFraction = Mathf.Clamp01(player.Physical?.HandsStamina?.NormalValue ?? 1f);
        }
        catch
        {
            _armFraction = 1f;
        }

        if (player.HandsController is not Player.FirearmController fc)
        {
            _valid = false;
            _controller = null;
            return;
        }
        if (ReferenceEquals(fc, _controller) && Time.time < _nextRefresh)
        {
            Recompute(); // config may have changed (F12); inputs are cached
            return;
        }
        _controller = fc;
        _nextRefresh = Time.time + RefreshInterval;
        try
        {
            Mass = Mathf.Max(0.1f, fc.Item.TotalWeight);
            Length = fc.WeaponLn > 0.05f ? fc.WeaponLn : 0.6f; // WeaponLn is 0 until the weapon bones exist
            Ergonomics = Mathf.Max(0f, fc.TotalErgonomics);
            ReadStock(fc.Item);
            WeaponMass.Measure(player.ProceduralWeaponAnimation, fc);
            _valid = true;
        }
        catch (Exception e)
        {
            _valid = false;
            FreeAimPlugin.Log.LogWarning($"FreeAim: weapon profile unavailable: {e.Message}");
            return;
        }
        Recompute();
    }

    private static void Recompute()
    {
        float m0 = Mathf.Max(0.1f, FreeAimPlugin.ProfileRefMass.Value);
        float l0 = Mathf.Max(0.05f, FreeAimPlugin.ProfileRefLength.Value);
        SimpleRatio = Mathf.Clamp(Mass * Length * Length / (m0 * l0 * l0), 0.02f, 50f);
        // reference gun = uniform rod of m0, l0 in front of the pivot: J0 = m0 l0^2 / 3, moment m0 l0 / 2
        bool measured = WeaponMass.Valid && FreeAimPlugin.ProfileMassDistribution.Value;
        InertiaRatio = measured ? Mathf.Clamp(WeaponMass.Inertia / (m0 * l0 * l0 / 3f), 0.02f, 50f) : SimpleRatio;

        float ergoT = Mathf.InverseLerp(ErgoLow, ErgoHigh, Ergonomics);
        float ergoEffect = FreeAimPlugin.ProfileErgoEffect.Value;
        ErgoFactor = Mathf.Lerp(1f - ergoEffect, 1f + ergoEffect, ergoT);

        float softness = FreeAimPlugin.ProfileSoftness.Value;
        // Boost total weight separately so length and attachment position keep their original exponents.
        float extraWeightExponent = softness * FreeAimPlugin.ProfileWeightEmphasis.Value;
        float inertiaScale = Mathf.Pow(InertiaRatio, softness) * Mathf.Pow(Mass / m0, extraWeightExponent);

        _springHz = Mathf.Clamp(FreeAimPlugin.SpringHz.Value / inertiaScale * ErgoFactor, 1.2f, 10f);

        _smoothTime = Mathf.Clamp(FreeAimPlugin.ArmSmoothing.Value * inertiaScale / ErgoFactor, 0.02f, 1f);

        _holdFactor = inertiaScale / ErgoFactor;
        _turnFactor = Mathf.Clamp(1f / inertiaScale, 0.4f, 1.5f);
        float kRef = Mathf.Max(0.05f, FreeAimPlugin.ProfileKappaRef.Value);
        _kappa = Mathf.Clamp01(InertiaRatio / (InertiaRatio + kRef));
        _sagScale = Mathf.Clamp(measured ? WeaponMass.Moment / (m0 * l0 / 2f) : Mass * Length / (m0 * l0), 0.1f, 6f);
        _swingScale = Mathf.Clamp(inertiaScale / ErgoFactor, 0.1f, 4f);
    }

    private static void ReadStock(Weapon weapon)
    {
        try
        {
            bool stockMod = false;
            float mods = 0f;
            foreach (Mod mod in weapon.Mods)
            {
                if (mod is Stock)
                {
                    stockMod = true;
                }
                else
                {
                    mods += mod.Template.Recoil;
                }
            }
            bool stockSlot = false;
            foreach (Slot slot in weapon.AllSlots)
            {
                if (slot.ID != null && slot.ID.StartsWith("mod_stock", StringComparison.OrdinalIgnoreCase))
                {
                    stockSlot = true;
                    break;
                }
            }
            bool pistol = string.Equals(weapon.WeapClass, "pistol", StringComparison.OrdinalIgnoreCase);
            bool integral = !stockSlot && !pistol;
            _shouldered = (stockMod || integral) && !weapon.Folded;
            _modsRecoil = Mathf.Clamp(1f + 0.5f * mods / 100f, 0.5f, 1.2f);
        }
        catch (Exception)
        {
            _shouldered = true;
            _modsRecoil = 1f;
        }
    }

    internal static string Format() =>
        Active
            ? $"weapon {Mass:0.00}kg L{Length:0.00}m ergo {Ergonomics:0}  inertia x{InertiaRatio:0.00} ({(WeaponMass.Valid && FreeAimPlugin.ProfileMassDistribution.Value ? $"{WeaponMass.Parts} parts, CoM {WeaponMass.CentreAhead * 100f:0}cm ahead" : "simple")}, m·L² x{SimpleRatio:0.00})  " +
              $"follow {_springHz:0.0}Hz smooth {_smoothTime:0.00}s  hold τ{HoldTau:0.00}s  κ{_kappa:0.00} turn x{_turnFactor:0.00} sag x{_sagScale:0.00} swing x{_swingScale:0.00} stock {(_shouldered ? "yes" : "no")} mods recoil x{_modsRecoil:0.00}"
            : $"weapon profile off  follow {FreeAimPlugin.SpringHz.Value:0.0}Hz smooth {FreeAimPlugin.ArmSmoothing.Value:0.00}s  hold τ{FreeAimPlugin.HoldTau.Value:0.00}s";
}
