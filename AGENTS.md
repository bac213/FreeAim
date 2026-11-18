# FreeAim (SPT client mod) — working notes

Client-only BepInEx plugin for SPT 4.1.6, targeting net472. No server part.

Docs:
- `docs/DESIGN.md` (Chinese): the four-point aim model, every design decision agreed with the user, the arm maths, per-weapon formulas, measurements, to-do list. **Read it before changing the aim model.**
- `docs/CHANGELOG.md`: version history 0.0.2–0.0.22, what was verified, what failed.

## Goal
Realistic gun handling (realism, not nerfs):
- The mouse turns the view V with vanilla feel. The body A follows the view with a limited turn speed and limits it beyond the neck range.
- The gun target T = V + lead (the gun leads turns, part of the lead stays, recentres after 5 s without mouse input).
- The arm carries the muzzle R toward T with the gun's inertia (weight × length², ergonomics). Body sway (breath, steps) moves the arm's base and is filtered by the arm.
- Bullets follow the muzzle. Recoil is never reduced.
- Braced stance (Mouse4): braced body, centred target, stiffer arm, integral hold, costs arm stamina. Low ready (Ctrl+Mouse4) and high ready (Alt+Mouse4) offset T in pitch and speed up A following V. Low ready also increases native arm recovery.

## Status (2026-11-17)
`FreeAim.Client` **0.1.1** is deployed to `BepInEx/plugins/` (0 build warnings, handling balance pending in-game feedback). The first release package is `dist/FreeAim-0.1.0.zip`. 0.0.22–0.0.31 (= 0.1.0) are **not yet verified in game** (0.0.27: stance keeps the screen centre clear, `StanceClearance.cs`) (0.0.26: ADS is vanilla except the body/neck limit) (0.0.25: recoil feedback into the view, `RecoilFeedback.cs`; recoil sim `C:/Users/Administrator/scratch/sim_recoil2.py`) (0.0.24: integral anti-windup + gun swing, sim in `C:/Users/Administrator/scratch/sim_024*.py`); balance comes after the feel is right.

Verified earlier and still in place: ready stance on both shoulders without jitter (0.0.10–0.0.12), suspend while hands are busy (0.0.13), sway probe (0.0.16).

Things to check in game for 0.0.22 (overlay on; `model` line shows body yaw, turn rate/limit, lead, gun, sway, sag, hold, arm Hz, smoothing, feed-forward, kappa, weight, idle):
1. No overshoot or wobble when a turn stops, for every gun; starts and stops are gentle (`1b / Arm smoothing`).
2. The view turns with vanilla feel; flicks within 45° are unlimited; 180° turns are capped by the body turn speed.
3. Turning: the grey dot (target) leads the view; light guns follow it, heavy/long guns trail. 5 s without mouse input: the kept lead returns to the centre.
4. ADS: the gun swings onto the screen centre (R follows V), no jumps on entry or exit; sway goes through the model.
5. Stance: centred, braced, `hold P` ≈ −sag. Recoil identical with `1d` on and off.
6. Prone, mounting, stationary weapons, vaulting, BTR: `(inactive)`, the game's own rotation applies.
7. Left shoulder: known risk that PWA-pass rotations are remapped into the third-person frame.

Open issues and to-do: see `docs/DESIGN.md` section 7.

## Code map (`FreeAim.Client/`)
| File | Role |
|---|---|
| `FreeAimPlugin.cs` | Entry point. Config (F12, sections below), Harmony patching, F8 toggle, `ShiftWeaponRoot` postfix wrapper, debug overlay (`OnGUI`). |
| `AimModel.cs` | Four-point model. `RotatePrefix` (on `Player.Rotate`): raw mouse → game degrees via `CurrentManagedState.ClampRotation`, neck limit, lead accumulation, then `ignoreClamp = true`. Only in plain states (`IsPlainState`) outside the BTR. `Step` (per frame): body yaw with turn limit; lead fade / idle recentre / collapse in stance, ADS, sprint; arm = first-order target smoothing (tau_s) + critically damped spring (omega) + feed-forward k = 1 − 1/(2·omega·tau_s), arm reach clamp, integral in stance/ADS; sway path (gain ≤ 1, kappa). `RemoveGameSway` takes `HandsRotation` yaw/pitch (rerouted body sway) and all of `SwaySpring` (turn sway) out of the weapon. Never measures the muzzle, so recoil is never corrected. Angles: degrees relative to V, x yaw + right, y pitch + down. |
| `WeaponProfile.cs` | Per-weapon values from `Item.TotalWeight`, `WeaponLn`, `TotalErgonomics` (refresh 0.5 s or on controller change): inertia ratio J, ergo factor, arm Hz, smoothing time, body turn factor, kappa, sag scale, hold tau (with arm fatigue). Profile off = base config values. |
| `WeaponOffsetPatch.cs` | Postfix on `ProceduralWeaponAnimation.ApplyComplexRotation`. Order: conditions → `WeaponProfile.Update` → `ReadyStance.Step` → `AimModel.Step` (Fixed mode: `Deactivate` + constant offset) → probe → `AimModel.RemoveGameSway` → rotate the weapon by the model output around `RotationCenter` → `ReadyStance.ApplyPose`. |
| `ReadyStance.cs` | Stance input (own key test), stamina gating, native `Physical.Consumption` drain, hands-busy suspend, shoulder handling. `Stabilization` (body bracing) and `StanceWeight` feed the model. `ApplyPose` (PWA pass, right shoulder: cant about the bore, view-space offset); `ApplyBodyPass` (from the `ShiftWeaponRoot` postfix: whole left-shoulder pose plus the centring slide on both shoulders). |
| `HighReadyPlacement.cs` | Right-shoulder high ready: move the gun and grips right and down within arm reach before the native hand IK solve. |
| `LowReadyWalkPatch.cs` | Multiplies the local player’s normal walking displacement during active low ready before terrain projection and collision. Checks ADS and sprint at the movement hook. |
| `ArmRecoveryPatch.cs` | Scales only the local player’s natural hands stamina recovery rate while low ready is active, inside the game’s existing cooldown and capacity checks. |
| `ConfigText.cs` | F12 localisation (0.0.23): `ConfigText.Bind` adds a `ConfigurationManagerAttributes` tag (Chinese `Category`, `DispName`, decreasing `Order` to keep code order) and a Chinese description. ConfigurationManager 18.4 reads the tag by class name and public field names via reflection. Sections and keys stay English, so the .cfg and saved values are unchanged. Enum values show Chinese through `[Description]` (`AimMode`, `StanceKeyMode`). |
| `SwayProbe.cs` | Debug only (overlay on): game sway source, muzzle deviation, weapon/camera split, body-frame velocity; slow average and fast RMS; overlay rows and one line per second (incl. the model and weapon profile) in append-only `BepInEx/FreeAim_sway.log`. |

Harmony patches (local player only, via `GameWorld.MainPlayer` + `IsYourPlayer`):
- `ProceduralWeaponAnimation.ApplyComplexRotation`: postfix (`WeaponOffsetPatch`).
- `Player.Rotate(Vector2,bool)`: prefix (`AimModel.RotatePrefix`).
- `PlayerBones.ShiftWeaponRoot`: postfix (left-shoulder stance pose, centring slide).

Config sections (English in the .cfg, Chinese in F12; add new entries with `t.Bind(section, key, default, cCategory, "中文名", "中文说明", range)`; `BepInEx/config/com.spt.freeaim.client.cfg`; defaults in code, the user's file keeps saved values, so edit both when retuning):
- `0. General`: Enabled, F8 toggle, Mode (Deadzone = the model; Fixed = spike).
- `1. Lead`: lead gain 1.3, kept 0.5, fade 0.5 s, max lead 12°/6°, idle delay 5 s, idle speed 0.8/s, recenter speed 10/s.
- `1a. View & body`: neck 45° (ADS 45°), body turn 300°/s, follow 0.12 s, crouch ×0.8, overweight ×0.6, injured leg ×0.7.
- `1b. Gun inertia`: response 1.6 Hz, arm smoothing 0.12 s, arm reach 30°/15°, stance/ADS stiffness ×1.5, gravity sag 0.3°, tired-arm sag ×1.
- `1c. Ready stance`: key Mouse4, raise 0.25 s, cant 15°, pose offset, stability (bracing) 0.5, drain ×0.6 ADS, re-entry 20, centring, notifications.
- `1d. Stance hold`: integral on, tau 1.2 s, max 3°.
- `1e. Weapon handling`: reference 3.5 kg / 0.8 m, inertia exponent 0.5, extra weight emphasis 0.08, ergo effect 0.2, tired-arm hold slowdown 1.5, shake inertia reference 1.
- `2. Fixed offset (spike)`, `3. Debug` (overlay currently **on** in the user's cfg).

Default keys: F8 free aim on/off; Mouse4 (`KeyCode.Mouse3`) braced stance; Ctrl+Mouse4 low ready; Alt+Mouse4 high ready.
Overlay dots: white = screen centre, red = muzzle direction, grey = gun target (lead).

## Build / deploy
```bash
cd E:/SPT/free-aim-main
dotnet build -c Release FreeAim.Client/FreeAim.Client.csproj     # expect 0 warnings / 0 errors
# the game locks the DLL while running: check first
tasklist | grep -i EscapeFromTarkov || cp FreeAim.Client/bin/Release/FreeAim.Client.dll E:/SPT/BepInEx/plugins/
```
- Record routine tuning and development changes in Git and leave the plugin version unchanged. Note user-facing changes as Unreleased in `docs/CHANGELOG.md`.
- Bump `PluginVersion` in `FreeAimPlugin.cs` and `<Version>` in the csproj together only when the user requests a release.
- Restart the client after deploying. `BepInEx/LogOutput.log` should show `FreeAim.Client <ver> loaded` and three `patched` lines.
- Config file: `BepInEx/config/com.spt.freeaim.client.cfg`. BepInEx keeps saved values when defaults change; remove obsolete keys and update retuned ones in the user's file too.
- Deploy while the game runs: a background loop `while tasklist | grep -qi EscapeFromTarkov; do sleep 3; done; cp ...` waits for the game to close.
- Before a big rewrite, back up the deployed DLL, source and cfg to `C:/Users/Administrator/scratch/backup/`.
- Git Bash heredocs break on long multi-line Python with quotes (an escaped `\n` inside a C# string can end up as a real newline). Write the script to `C:/Users/Administrator/scratch/` and run `python3 -I <file>`.

## Decompiled source
Full decompile of `Assembly-CSharp.dll` (8620 files): `C:/Users/Administrator/decomp/Assembly-CSharp/`. Search with `rg -n`.
Regenerate with:
```bash
"$HOME/.dotnet/tools/ilspycmd.cmd" -p -o C:/Users/Administrator/decomp/Assembly-CSharp E:/SPT/EscapeFromTarkov_Data/Managed/Assembly-CSharp.dll
```
Line numbers below are for this decompile (SPT 4.1.6). Paths are relative to that folder.

### Shooting: bullets follow the weapon bone
| What | Where |
|---|---|
| Bullet direction `WeaponDirection = CurrentFireport.Original.TransformDirection(LocalShotDirection)` | `EFT/Player.cs:12160` |
| `Shot()` uses it, then `AdjustShotVectors` | `EFT/Player.cs:14322`, `:14286` |
| `LocalShotDirection` = `PWA.ShotDirection` = `_shotDirection`, default `Vector3.down` (muzzle-local forward) | `EFT.Animations/ProceduralWeaponAnimation.cs:331`, `:528` |
| Iron-sight / collimator calibrated shot direction | PWA `method_14` `:1607` → `EFT.InventoryLogic/Weapon.cs:1265` |
| Bots shoot along `LookDirection` instead, so they are unaffected | `AIFirearmController.cs:6` |
| Weapon length `WeaponLn` (base → fireport) | `EFT/Player.cs:11987`, computed `:12866` |
| `ErgonomicWeight` (passed to `Physical.Aim`) | `EFT/Player.cs:12134` |

### Procedural weapon animation (PWA)
| What | Where |
|---|---|
| `HandsContainer` (`PlayerSpring`), `Shootingg` (`ShotEffector`) | PWA `:212`, `:227` |
| `PlayerSpring` fields: `HandsRotation` spring, `SwaySpring` (`BetterSpring`), `WeaponRootAnim`, `CameraTransform`, `RotationCenter` | `EFT.Animations/PlayerSpring.cs:22`, `:28`, `:32`, `:34`, `:46` |
| First-person order: `ProcessEffectors` → `ApplyTransformations` (ZeroAdjustments, UpdateAimWeight, BlendAnimatorPose, ApplyPosition, **ApplyComplexRotation**, TacticalReload, AvoidObstacles) → camera pass | `FirstPersonStrategy.cs:21`, `:67`; PWA `ProcessEffectors` `:2300` |
| `ApplyComplexRotation`: `HandsRotation.Get()` + displacement×sway + sway, applied as `Euler(x,0,z)*Euler(0,y,0)` in the parent frame around `RotationCenter`, then recoil around `RecoilPivot`, then aiming alignment | PWA `:1760` |
| `_displacementStr` (private), `AimingDisplacementStr` | PWA `:473`, `:465` |
| `AvoidObstacles` (TurnAway; wall push-back) | PWA `:1864` |
| Hip-fire camera target `CameraOffset + _cameraByFOVOffset + TurnAway.CameraShift`, independent of the weapon. ADS target = scope bone | PWA `CalculateCameraPosition` `:2027` |
| `Spring.FixedUpdate`: `Current` = dynamic part, `Get()` = `Zero + Current` | `EFT.Animations/Spring.cs:65` |
| Recoil goes to separate recoil springs (`HandRotationRecoilEffect`), not `HandsRotation` | `ShotEffector.cs:52` |
| `Spring.FixedUpdate` is P+D only, no I: `Velocity += accel; Velocity -= (Current-Zero)*ReturnSpeed; Velocity *= Damping`. A sustained push `a` leaves a steady offset `Current = a / ReturnSpeed`, which is why the muzzle rests off the aim point | `EFT.Animations/Spring.cs:65` |
| HandsRotation components in the parent (hands controller) frame: x = pitch (+ = muzzle down), y = yaw, z = roll (Unity left-handed) | PWA `ApplyComplexRotation` `:1760` |
| Breath: one-sided pitch-down push `max(0,-YRandom) * (1 - staminaLevel) * 2` (staminaLevel = arm stamina, or min(arm, body) if `StaminaExhaustionCausesJiggle`); plus random yaw/roll. Scaled by `Breath.Intensity = IntensityByPoseLevel * IntensityByAiming` | `EFT.Animations/BreathEffector.cs` `Process`; PWA `:2284` |
| Motion: `movementProcessors` push HandsRotation from smoothed local velocity/acceleration (steady walking = steady push = steady tilt); `mouseProcessors` push `SwaySpring` from turn rate, scaled by `SwayFactors` | `MotionEffector.cs` `FixedTracking`/`Process` |
| Game's own weight scaling: `_swayStrength = SWAY_STRENGTH_PER_KG(ErgonomicWeight * (1+Overweight))`, `_displacementStr = DISPLACEMENT_STRENGTH_PER_KG(...)` (turn sway and its displacement). Per-weapon inertia must account for this to avoid double counting | PWA `:2295` |
| Walk step curves (`WalkEffector`) and the idle rest pose (`CustomEffector.RestValues`, after `RestDelay` 10 s idle) also drive HandsRotation; their curve data lives in prefabs, not code | `WalkEffector.cs`, `CustomEffector.cs` |
| `AnimatorPose` / `ActiveBlends`: one pose at a time, overwritten by the movement state, disables `CustomEffector.Aim` (`IsAiming && ActiveBlends.Count < 1`) | PWA `BlendAnimatorPose` `:1684`, `AssignAnimatorPose` `:2495` |
| `BlindfireBlender` (blind fire / side fire) | PWA `:439` |

### Frame pipeline: arms pass vs body pass
| What | Where |
|---|---|
| `Player.VisualPass()` | `EFT/Player.cs:26122` |
| Arms pass (`_armsupdated`): `PWA.ProcessEffectors`, then `PlayerBones.Offset/DeltaRotation = WeaponRootAnim.localPosition/localRotation` | `EFT/Player.cs:26137` |
| Body pass (`_bodyupdated`): sets the left-stance curve | `:26153` |
| Body pass: `PlayerBones.ShiftWeaponRoot` | `:26192` |
| Body pass: then hand IK | — |
| `PWA.LateTransformations` | `:26228` |

**The arms rig is stale during the arms pass.** `MouseLook()` (body pass) → `MovementContext.DefaultRotationFunction` places `HandsController.ControllerGameObject` (parent of `Weapon_root`/`Weapon_root_anim`) at `Ribcage.Original.position` and sets `CameraContainer.rotation` (`EFT/MovementContext.cs:561`). So inside the PWA pass the gun still sits where last frame's MouseLook left it. Anything that measures the gun's **position** against the camera there is off by one frame of player movement. That error is sideways while strafing (the 0.0.9 centred-variant jitter). Relative changes (cant, view-space offset, rotations) are fine in the PWA pass.
- On the right shoulder the camera pass runs inside `ProcessEffectors`, so by `ShiftWeaponRoot` (after MouseLook, before hand IK) both rig and camera are final for the frame. On the left shoulder the camera pass runs later, in `LateTransformations`.
- The weapon root's `localPosition` is reset each frame in `ApplyPosition` (PWA `:1716`), so late shifts don't feed back. The left-shoulder branch of `ShiftWeaponRoot` stores its smoothing state (`targetLSPosition`, `_lastWeaponRotationInLeftStance`) before returning, so a postfix doesn't feed back there either.
- **The render camera is stale in the body pass.** `FirstPersonCameraState.ManualLateUpdate` copies `Player.CameraPosition` (`Cam`, same as `HandsContainer.CameraTransform`) to the render camera late in the frame. Always measure against `HandsContainer.CameraTransform`, never `CameraManager.Instance.Camera`. This is the likely real cause of the 0.0.8 jitter.

### Left shoulder
| What | Where |
|---|---|
| `LeftStanceController.ToggleLeftStance()` (respects body-action blocks) | `EFT/LeftStanceController.cs:51`; accessor `MovementContext.LeftStanceController` `EFT/MovementContext.cs:664` |
| Shoulder blend 0..1 is the private field `PWA._leftStanceCurrentCurveValue`. The public `PWA.LeftStanceCurrentCurveValue` returns `FarPlane.Depth`, which is not the blend, so don't use it | PWA `:287`, `:526`, setter `:2321` |
| `PlayerBones.ShiftWeaponRoot`: on the left shoulder (curve ≥ 1) it rebuilds `Weapon_Root_Anim` as `Weapon_Root_Third.position + Weapon_Root_Third.rotation * Offset` and `Weapon_Root_Third.rotation * DeltaRotation` (keeps only local x/z euler). This **remaps anything done in PWA into the third-person frame** | `PlayerBones.cs:342` |

### Input, camera, body rotation
| What | Where |
|---|---|
| Mouse look → `Player.Rotate(deltaRotation)`. Only local input and bots call it; recoil does not | `EFT/MoveInputTranslator.cs:48`, `EFT/Player.cs:25734` |
| `MovementState.Rotate` adds the delta to `MovementContext.Rotation` (x = yaw, y = pitch) | `EFT/MovementState.cs:193`, `EFT/MovementContext.cs:1044` |
| **`Player.Rotate` receives raw mouse axes, not degrees.** `MovementState.Rotate` → `ClampRotation` (public): `delta * Player.GetRotationMultiplier()` (hip: `GetSensitivity() * (1 + _mouseSensitivityModifier)`; ADS: `HandsController.AimingSensitivity`) `* StateSensitivity`, then capped at `RotationSpeedClamp` per call; `ignoreClamp = true` skips all of it. User settings: hip 0.7, aiming 0.5. Up to 0.0.19 the deadzone treated raw deltas as degrees (gun moved ~1.4x the camera's speed for the same input) and mixed them with `MovementContext.Rotation` degrees | `EFT/MovementState.cs:193`, `:203`; `EFT/Player.cs:25498`; `EFT/MovementContext.cs:2781` |
| States with their own `Rotate`: prone and prone-move (collision check `IsAbleToRotateProne`), `StationaryPlayerState`, `IdleWeaponMountingState`, vault, climb, pick-up (no rotation), roll. Sprint scales sensitivity and clamp by `Physical.SprintSensitivity` (`UpdateStateValues`). State name: `CurrentManagedState.Name` (`EPlayerState`: Idle, Run, Sprint, Sidestep, Jump, FallDown, JumpLanding, Transition, BlindFire, ProneIdle, ProneMove, Stationary, IdleWeaponMounting, ClimbOver, ClimbUp, Roll, Pickup, Loot, ...) | `EFT/PronePlayerState.cs:99`, `EFT/MovementContext.cs:4160`, `EPlayerState.cs`, `EFT/Player.cs:24498` |
| Free look is `Player.Look` (separate path, `MOUSE_LOOK_*_LIMIT`), not `Rotate`. BTR: `Player.BtrState != Outside` switches to a custom hand rotator | `EFT/MoveInputTranslator.cs:41`, `EFT/Player.cs:25278` |
| Body-turn inputs: `Physical.Overweight`, `MovementContext.PoseLevel`, `IsInPronePose`, `EPhysicalCondition.LeftLegDamaged/RightLegDamaged` | `PhysicalBase.cs:325`, `EFT/MovementContext.cs:1024` |
| Body yaw clamp `HANDS_TO_BODY_MAX_ANGLE = 45`. Hard clamp in the `Rotation` setter; smooth follow in `ProcessUpperbodyRotation`; `HandsToBodyAngle` → animator `SetDeltaRotation` | `EFTHardSettings.cs:369`, `EFT/MovementContext.cs:1044`, `EFT/MovementState.cs:120`, `EFT/MovementContext.cs:2089` |
| `Player.HandsRotation` / `ICustomRotator` (BTR uses it to decouple hands from the camera) | `EFT/Player.cs:24632`, `:24386`, `:25295`; `EFT/ICustomRotator.cs:5` |
| Sprint flag `MovementContext.IsSprintEnabled` | `EFT/MovementContext.cs:1175` |
| Local player `GameWorld.MainPlayer`, `Player.IsYourPlayer` | `EFT/GameWorld.cs:586`, `EFT/Player.cs:25237` |
| Render camera `CameraManager.Instance.Camera` | `EFT.CameraControl/CameraManager.cs:323` |

### Stamina
| What | Where |
|---|---|
| Hands busy: `FirearmController.IsInReloadOperation()` (`EFT/Player.cs:14578`), `IsInInteraction()` = animator use-left-hand flag (`:13658`), `CurrentOperation` (`:12188`) is a nested `FirearmController.*Operation`. Weapon inspect is only an animator trigger (`LookTrigger`) during `Idling`, so it is not detectable from the operation | |
| `PhysicalBase.HandsStamina` (`Stamina`), `Exhausted` = `Current < 15` | `PhysicalBase.cs:357`, `Stamina.cs:107` |
| `Stamina.AddConsumption` (returns an unsubscribe `Action`), `Process` (sums `Delta` per second; any consumption with `AllowsRestoration = false` blocks regen) | `Stamina.cs:281`, `:304` |
| Native ADS drain formula `sqrt(_aimMass) * AimDrainRate * armDamage * _aimMultiplierByPose[_pose] * (1 - StrengthBuffAimFatigue) * HydrationFactor` | `Physical.cs:708` (`_aimMass` `:434`, `_aimMultiplierByPose` `:437`); `AimDrainRate` `EFT/GlobalConfiguration.cs:908` |
| `StaminaZero` health effect sets `HandsStamina.Current = 0` | `EFT.HealthSystem/ActiveHealthController.cs:2265` |
| In-game notice `NotificationManager.DisplayWarningNotification` | `EFT.Communications/NotificationManager.cs:530` |

### Per-weapon data
| What | Where |
|---|---|
| `WeaponTemplate.CameraToWeaponAngleSpeedRange` (per weapon; read by PWA) | `EFT.InventoryLogic/WeaponTemplate.cs:67` |
| `GlobalConfiguration.InertiaSettings` (movement inertia curves) | `EFT/GlobalConfiguration.cs:1128` |
| `WeaponLn`, `ErgonomicWeight` | see Shooting |

Outside Assembly-CSharp:
- `BepInEx.Configuration.KeyboardShortcut.IsDown()` returns false while **any** other key is held (`ModifierKeyTest` checks every supported key). Use `ReadyStance.IsPressedThisFrame`/`IsHeld` instead.
- ConfigurationManager is installed (`BepInEx/plugins/spt/ConfigurationManager`), so all config is editable with F12.

## Sway measurements (0.0.16 probe, 2026-11-17, right shoulder, flat ground, full arm stamina)
Raw logs and scripts: `C:/Users/Administrator/scratch/sway/` (`run3.txt`, `analyse2.py` = per-condition mean ± std of the 1 Hz samples, sprint/ADS/transitions dropped). Degrees, P+ = muzzle down, Y+ = right. "Muzzle" = muzzle vs screen centre minus the free-aim offset.

| Movement | Hip muzzle mean | Stance muzzle mean | Note |
|---|---|---|---|
| still | P 0.4, wanders ±0.5..1 slowly | P 0.07 ±0.26, Y ±0.67 | hip wander is not in the springs (likely the breath hip-random offset in the recoil channel) |
| W forward | P 1.03 ±0.63 | P 0.85 ±0.67 | **stance barely reduces it**: most of the forward-walk pitch is not in HandsRotation/SwaySpring (source P ~0.8..1.1), so spring-based `RemoveSway` cannot catch it |
| S back | P −0.50 | P +0.24 | few samples, noisy |
| A / D strafe | Y +2.44 / −2.15 (trails the movement) | Y +1.19 / −1.16 | stance halves it, as expected |

- The camera's own animation adds only P ≈ −0.15 (looks up) while moving and no yaw. The deviation is almost all weapon.
- Tired arms (run 1, arm < 20): source pitch offset 2..4x, fast pitch shake ~4x.
- Conclusion: slow offsets are as large as or larger than the fast bounce in every moving case. (0.0.17 corrected the measured error; since 0.0.20 the body sway is rerouted through the arm model instead.)

## Approaches that failed (don't retry)
- **0.0.7: stance pose in PWA plus mirroring on the left shoulder.** `ShiftWeaponRoot` remaps the PWA result into the third-person frame, so the centring went the wrong way.
- **0.0.8: stance pose in a `PlayerBones.ShiftWeaponRoot` postfix, measured against the render camera.** The render camera is copied from `Cam` only later in the frame, so it is stale there. The postfix location itself is fine (0.0.10+ use it with `HandsContainer.CameraTransform`). Mirroring the cant was also wrong: the user wants the same clockwise cant everywhere.
- **Using the game's `AnimatorPose` system for the stance.** See the PWA table.
- **0.0.9: centring slide measured against the camera in the PWA pass.** Jittered when strafing, because the arms rig is stale there.
- **0.0.9/0.0.10: switching to the right shoulder on stance entry.** Worked, but the user wants the stance to stay on the left shoulder.
- **Up to 0.0.19: feeding raw `Player.Rotate` deltas into the aim model as degrees.** They are raw mouse axes; convert with `ClampRotation` first.
- **0.0.17: integral on the measured muzzle-vs-screen error, frozen while shooting.** Superseded by the model's own integral, which never sees recoil.
- **0.0.20–0.0.21: underdamped arm spring (zeta 0.45–0.85) with the body velocity carried rigidly into the gun.** Wobbled like jelly; the user rejected it. Keep the arm critically damped with the no-overshoot feed-forward; add heavy-gun momentum only as a single, torque-limited overshoot.
- **ADS camera-to-gun transfer (view swings onto the gun).** Rejected by the user: in ADS the gun must swing onto the view.

## User preferences
- Replies in Chinese.
- Feel matters most. Visible animation jitter and wobble are unacceptable.
- Realism, not nerfs: a realistic result that is stronger than vanilla is fine (e.g. hip fire steadier for slow drift). Costs such as arm stamina are preferred over free buffs. Recoil must never be reduced.
- The stance is clockwise canted (top of the gun to the left) in all variants and on both shoulders.
- Discuss the logic first when the user says "先别动手"; implement only after agreement. Functionality first, balance later.
- The user tests in game and reports back with screenshots. When a test seems to show the fix failing, first check that the deployed DLL matches the current build and the client was restarted. Routine tuning builds can share the same version in `LogOutput.log`.
