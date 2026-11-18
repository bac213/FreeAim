using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using EFT.CameraControl;
using HarmonyLib;
using UnityEngine;

namespace FreeAim.Client;

/// <summary>
/// Gun-led free aim. Deadzone mode: mouse moves the gun inside a cone, camera follows past its edge.
/// Fixed mode (Stage 0 spike): constant hip-fire offset to verify that
/// (1) bullets follow the muzzle, (2) the view does not move, (3) ADS stays aligned
/// once the offset fades out.
/// </summary>
[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class FreeAimPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.spt.freeaim.client";
    public const string PluginName = "FreeAim.Client";
    public const string PluginVersion = "0.1.1";

    internal static ManualLogSource Log;

    internal static ConfigEntry<bool> Enabled;
    internal static ConfigEntry<KeyboardShortcut> ToggleKey;
    internal static ConfigEntry<AimMode> Mode;
    internal static ConfigEntry<float> ConeYaw;
    internal static ConfigEntry<float> ConePitch;
    internal static ConfigEntry<float> RecenterSpeed;
    internal static ConfigEntry<float> LeadGain;
    internal static ConfigEntry<float> LeadKeep;
    internal static ConfigEntry<float> LeadFadeTime;
    internal static ConfigEntry<float> IdleDelay;
    internal static ConfigEntry<float> IdleRate;
    internal static ConfigEntry<float> NeckYaw;
    internal static ConfigEntry<float> AdsNeckYaw;
    internal static ConfigEntry<float> BodyTurnSpeed;
    internal static ConfigEntry<float> BodyFollowTime;
    internal static ConfigEntry<float> CrouchTurn;
    internal static ConfigEntry<float> OverweightTurn;
    internal static ConfigEntry<float> LegInjuryTurn;
    internal static ConfigEntry<bool> Inertia;
    internal static ConfigEntry<float> SpringHz;
    internal static ConfigEntry<float> ArmSmoothing;
    internal static ConfigEntry<float> ArmYaw;
    internal static ConfigEntry<float> ArmPitch;
    internal static ConfigEntry<float> StanceArmStiffness;
    internal static ConfigEntry<float> SwingGain;
    internal static ConfigEntry<float> ShoulderCarry;
    internal static ConfigEntry<float> SwingHz;
    internal static ConfigEntry<float> SwingMax;
    internal static ConfigEntry<float> HipSag;
    internal static ConfigEntry<float> SagFatigue;
    internal static ConfigEntry<bool> StanceEnabled;
    internal static ConfigEntry<KeyboardShortcut> StanceKey;
    internal static ConfigEntry<StanceKeyMode> StanceMode;
    internal static ConfigEntry<float> LowReadyPitch;
    internal static ConfigEntry<float> HighReadyPitch;
    internal static ConfigEntry<float> HighReadyLeftCm;
    internal static ConfigEntry<float> HighReadyDownCm;
    internal static ConfigEntry<float> ReadyBodyFollow;
    internal static ConfigEntry<float> LowReadyRecovery;
    internal static ConfigEntry<float> LowReadyWalkSpeed;
    internal static ConfigEntry<float> StanceRaiseTime;
    internal static ConfigEntry<float> LowReadyRaiseTime;
    internal static ConfigEntry<float> StanceCant;
    internal static ConfigEntry<Vector3> StanceOffset;
    internal static ConfigEntry<float> StanceDrainMultiplier;
    internal static ConfigEntry<float> StanceStability;
    internal static ConfigEntry<float> StanceReentryStamina;
    internal static ConfigEntry<bool> StanceNotifications;
    internal static ConfigEntry<float> StanceCentreNormal;
    internal static ConfigEntry<float> StanceCentreCentred;
    internal static ConfigEntry<float> StanceCentreLeft;
    internal static ConfigEntry<float> StanceCantCentred;
    internal static ConfigEntry<bool> StanceDefaultCentred;
    internal static ConfigEntry<float> StanceCentreMaxShift;
    internal static ConfigEntry<bool> HoldEnabled;
    internal static ConfigEntry<float> HoldTau;
    internal static ConfigEntry<float> HoldMax;
    internal static ConfigEntry<float> HoldFreezeRate;
    internal static ConfigEntry<float> HoldFreezeError;
    internal static ConfigEntry<bool> ProfileEnabled;
    internal static ConfigEntry<bool> RecoilEnabled;
    internal static ConfigEntry<float> RecoilKickHip;
    internal static ConfigEntry<float> RecoilKickStance;
    internal static ConfigEntry<float> RecoilNoStock;
    internal static ConfigEntry<float> RecoilPush;
    internal static ConfigEntry<float> RecoilPushStance;
    internal static ConfigEntry<bool> ClearEnabled;
    internal static ConfigEntry<float> ClearAngle;
    internal static ConfigEntry<float> ClearMax;
    internal static ConfigEntry<float> RecoilResidual;
    internal static ConfigEntry<float> RecoilResidualReturn;
    internal static ConfigEntry<float> RecoilBodyHz;
    internal static ConfigEntry<float> ProfileRefMass;
    internal static ConfigEntry<bool> ProfileMassDistribution;
    internal static ConfigEntry<float> ProfileRefLength;
    internal static ConfigEntry<float> ProfileSoftness;
    internal static ConfigEntry<float> ProfileWeightEmphasis;
    internal static ConfigEntry<float> ProfileErgoEffect;
    internal static ConfigEntry<float> ProfileFatigueHold;
    internal static ConfigEntry<float> ProfileKappaRef;
    internal static ConfigEntry<float> YawDeg;
    internal static ConfigEntry<float> PitchDeg;
    internal static ConfigEntry<float> BlendSpeed;
    internal static ConfigEntry<bool> ApplyWhenAiming;
    internal static ConfigEntry<bool> ShowOverlay;
    internal static ConfigEntry<float> MarkerDistance;

    private static Texture2D _dot;

    private void Awake()
    {
        Log = Logger;

        var t = new ConfigText(Config);

        // English sections and keys are what the .cfg stores; the second string pair is the F12 display.
        const string general = "0. General";
        const string lead = "1. Lead";
        const string body = "1a. View & body";
        const string inertia = "1b. Gun inertia";
        const string stance = "1c. Ready stance";
        const string hold = "1d. Stance hold";
        const string profile = "1e. Weapon handling";
        const string recoil = "1f. Recoil feedback";
        const string spike = "2. Fixed offset (spike)";
        const string debug = "3. Debug";
        const string cGeneral = "0. 总设置";
        const string cLead = "1. 枪口领先";
        const string cBody = "1a. 视角与身体";
        const string cInertia = "1b. 手臂与枪的惯性";
        const string cStance = "1c. 据枪";
        const string cHold = "1d. 据枪积分（I 项）";
        const string cProfile = "1e. 按武器调整";
        const string cRecoil = "1f. 后坐力反馈";
        const string cSpike = "2. 固定偏移（测试用）";
        const string cDebug = "3. 调试";

        // ---- 0. General
        Enabled = t.Bind(general, "Enabled", true, cGeneral, "启用",
            "总开关。关闭后枪口、视角、身体全部回到原版行为。");
        ToggleKey = t.Bind(general, "Toggle key", new KeyboardShortcut(KeyCode.F8), cGeneral, "开关快捷键",
            "战局中切换总开关，方便和原版对比。");
        t.Advanced = true; // test-only: shown with F12 "Advanced settings"
        Mode = t.Bind(general, "Mode", AimMode.Deadzone, cGeneral, "模式",
            "四点模型：鼠标转视角，枪口带着手臂惯性领先、落后、晃动。固定偏移：只给一个恒定偏移，仅用于验证。");
        t.Advanced = false;

        // ---- 1. Lead
        LeadGain = t.Bind(lead, "Lead gain", 1.3f, cLead, "领先倍数",
            "转向时枪口期望位置比视角快多少倍，也就是枪先进入转向。1 = 不领先。重枪的手臂惯性大，可能领先不出来。",
            ConfigText.Range(0.5f, 2.5f));
        LeadKeep = t.Bind(lead, "Lead kept", 0.5f, cLead, "停下后保留比例",
            "停止转向后，领先偏移保留的比例（自由瞄准偏移），其余部分按“回落时间”消失。",
            ConfigText.Range(0f, 1f));
        LeadFadeTime = t.Bind(lead, "Lead fade time (s)", 0.5f, cLead, "回落时间（秒）",
            "停止转向后，不保留的那部分领先偏移消失的时间常数。",
            ConfigText.Range(0.05f, 3f));
        ConeYaw = t.Bind(lead, "Max lead yaw (deg)", 12f, cLead, "最大领先·左右（度）",
            "枪口期望位置离屏幕中心的最大左右偏移。",
            ConfigText.Range(0.5f, 40f));
        ConePitch = t.Bind(lead, "Max lead pitch (deg)", 6f, cLead, "最大领先·上下（度）",
            "枪口期望位置离屏幕中心的最大上下偏移。",
            ConfigText.Range(0.5f, 25f));
        IdleDelay = t.Bind(lead, "Idle recentre delay (s)", 5f, cLead, "静止回正延迟（秒）",
            "鼠标这么久没有输入后，保留的领先偏移开始慢慢回到屏幕中心。",
            ConfigText.Range(0.5f, 30f));
        IdleRate = t.Bind(lead, "Idle recentre speed (1/s)", 0.8f, cLead, "静止回正速度（1/秒）",
            "开始回正后，回到中心的速度。",
            ConfigText.Range(0.05f, 10f));
        RecenterSpeed = t.Bind(lead, "Recenter speed (1/s)", 10f, cLead, "收回速度（1/秒）",
            "据枪、开镜、冲刺时领先偏移收回的速度。收回后枪口经过手臂摆回屏幕中心。",
            ConfigText.Range(1f, 40f));

        // ---- 1a. View & body
        NeckYaw = t.Bind(body, "Neck range (deg)", 45f, cBody, "脖子范围（度）",
            "视角可以相对身体左右转多少度。范围内甩枪和鼠标一样快；超出后视角只能跟着身体的速度转，多余的鼠标输入丢弃。",
            ConfigText.Range(5f, 120f));
        AdsNeckYaw = t.Bind(body, "Neck range in ADS (deg)", 45f, cBody, "开镜脖子范围（度）",
            "开镜时（脸贴枪托）的脖子范围。调小后，开镜大角度转向会受身体转速限制。",
            ConfigText.Range(0f, 120f));
        BodyTurnSpeed = t.Bind(body, "Body turn speed (deg/s)", 300f, cBody, "身体转速（度/秒）",
            "站立、拿参考枪时身体的最快转速。会按枪的惯性、蹲姿、超重、腿伤降低。",
            ConfigText.Range(60f, 1500f));
        BodyFollowTime = t.Bind(body, "Body follow time (s)", 0.12f, cBody, "身体跟随时间（秒）",
            "身体转向视角方向的快慢（在转速上限之前）。",
            ConfigText.Range(0.02f, 1f));
        CrouchTurn = t.Bind(body, "Crouched turn speed (x)", 0.8f, cBody, "蹲姿转速倍率",
            "完全蹲下时身体转速的倍率。",
            ConfigText.Range(0.2f, 1.5f));
        OverweightTurn = t.Bind(body, "Overweight turn speed (x)", 0.6f, cBody, "超重转速倍率",
            "完全超重时身体转速的倍率。",
            ConfigText.Range(0.2f, 1f));
        LegInjuryTurn = t.Bind(body, "Injured leg turn speed (x)", 0.7f, cBody, "腿伤转速倍率",
            "腿部受伤时身体转速的倍率。",
            ConfigText.Range(0.2f, 1f));

        // ---- 1b. Gun inertia
        Inertia = t.Bind(inertia, "Enabled", true, cInertia, "启用手臂惯性",
            "手臂带着枪的惯性把枪口送到期望位置（落后、平滑、过滤晃动）。关闭后枪口直接在期望位置，身体晃动不经过滤直接作用到枪上。");
        SpringHz = t.Bind(inertia, "Response frequency (Hz)", 2f, cInertia, "手臂响应（Hz）",
            "参考枪腰射时手臂的响应速度（临界阻尼，不会来回晃）。越高越跟手，修正身体晃动也越快。按武器自动缩放（见 1e）。",
            ConfigText.Range(0.5f, 15f));
        ArmSmoothing = t.Bind(inertia, "Arm smoothing (s)", 0.1f, cInertia, "起停平滑（秒）",
            "参考枪开始和停止移动有多柔和。越大，加速、减速越慢。按武器自动缩放（见 1e）。停止转向时不会过冲。",
            ConfigText.Range(0.02f, 0.6f));
        ArmYaw = t.Bind(inertia, "Arm reach yaw (deg)", 30f, cInertia, "手臂摆动范围·左右（度）",
            "手臂相对身体能把枪左右摆多远。大角度转向时身体落后于视角，枪可能被拖在视角后面。",
            ConfigText.Range(5f, 90f));
        ArmPitch = t.Bind(inertia, "Arm reach pitch (deg)", 15f, cInertia, "手臂摆动范围·上下（度）",
            "手臂相对视角能把枪上下摆多远。",
            ConfigText.Range(2f, 45f));
        StanceArmStiffness = t.Bind(inertia, "Stance arm stiffness (x)", 1.5f, cInertia, "据枪/开镜手臂绷紧倍率",
            "据枪和开镜时（手臂绷紧、脸贴枪托）手臂响应的倍率。",
            ConfigText.Range(1f, 4f));
        ShoulderCarry = t.Bind(inertia, "Shoulder carry (x)", 0.5f, cInertia, "肩膀带动",
            "转向时肩膀和上身跟着头一起转，枪从一开始就以这个比例跟着视角动，手臂只需要追剩下的部分。参考枪的值；" +
            "重枪按惯量减少（÷√惯量系数，最多 0.85）。越大越跟手、起步越快；0 = 枪完全靠手臂追。",
            ConfigText.Range(0f, 1f));
        SwingGain = t.Bind(inertia, "Gun swing (x)", 0.4f, cInertia, "枪身甩动强度",
            "枪身自身的惯性：手臂加速时枪往后甩，减速时冲过头一次再回来（不来回晃）。幅度和转速成正比，按枪重 × 枪长²放大，" +
            "重枪明显、轻枪很小。参考枪 150°/s 停下约冲过 1°。0 = 关闭。",
            ConfigText.Range(0f, 2f));
        SwingHz = t.Bind(inertia, "Gun swing return (Hz)", 1.2f, cInertia, "枪身甩动回正（Hz）",
            "参考枪甩过头后回正的快慢（临界阻尼）。重枪按惯量自动变慢。越低，甩得越远、回得越慢。",
            ConfigText.Range(0.3f, 5f));
        SwingMax = t.Bind(inertia, "Gun swing max (deg)", 4f, cInertia, "枪身甩动上限（度）",
            "每个方向枪身甩动的最大角度。",
            ConfigText.Range(0.5f, 15f));
        HipSag = t.Bind(inertia, "Gravity sag (deg)", 0.3f, cInertia, "重力下垂（度）",
            "参考枪枪口持续下垂的角度，按枪重 × 枪长缩放。据枪和开镜时由积分项顶住，腰射时保留。",
            ConfigText.Range(0f, 3f));
        SagFatigue = t.Bind(inertia, "Tired arms sag (x)", 1f, cInertia, "手累额外下垂",
            "上肢耐力耗空时额外的下垂：下垂 ×（1 + 此值）。",
            ConfigText.Range(0f, 4f));

        // ---- 1c. Ready stance
        StanceEnabled = t.Bind(stance, "Enabled", true, cStance, "启用据枪",
            "启用据枪和高、低准备姿态。据枪时准星回到中心，枪收近并倾斜，消耗上肢耐力。");
        StanceKey = t.Bind(stance, "Key", new KeyboardShortcut(KeyCode.Mouse3), cStance, "据枪按键",
            "默认鼠标侧键 4。据枪用此键；Ctrl + 此键为低准备，Alt + 此键为高准备。");
        StanceMode = t.Bind(stance, "Key mode", StanceKeyMode.Toggle, cStance, "按键方式",
            "切换：按一下进入，再按同一组合退出。按住：按住期间保持对应姿态。");
        LowReadyPitch = t.Bind(stance, "Low ready pitch (deg)", 20f, cStance, "低准备下压角（度）",
            "低准备时 T 相对视角中心向下偏的角度。", ConfigText.Range(0f, 50f));
        HighReadyPitch = t.Bind(stance, "High ready pitch (deg)", 25f, cStance, "高准备上抬角（度）",
            "高准备时 T 相对视角中心向上偏的角度。", ConfigText.Range(0f, 50f));
        HighReadyLeftCm = t.Bind(stance, "Right shoulder high ready left of centre (cm)", 10f, cStance, "右肩高准备枪身靠左（厘米）",
            "右肩高准备时，枪身移到视角中心左侧的目标距离。0 为关闭横向调整，实际偏移受双臂可达范围限制。",
            ConfigText.Range(0f, 25f));
        HighReadyDownCm = t.Bind(stance, "Right shoulder high ready drop (cm)", 18f, cStance, "右肩高准备双手下移（厘米）",
            "右肩高准备时，枪和双手一起下移的距离。实际偏移受双臂可达范围限制。",
            ConfigText.Range(0f, 30f));
        ReadyBodyFollow = t.Bind(stance, "High and low ready body follow (x)", 1.25f, cStance, "高低准备身体跟随倍率",
            "高、低准备时 A 追 V 的速度和最高转速倍率。", ConfigText.Range(1f, 2f));
        LowReadyRecovery = t.Bind(stance, "Low ready arm recovery (x)", 1.5f, cStance, "低准备上肢恢复倍率",
            "低准备时的自然恢复倍率。仍要等游戏原有的恢复冷却结束。", ConfigText.Range(1f, 3f));
        LowReadyWalkSpeed = t.Bind(stance, "Low ready walk speed (x)", 1.10f, cStance, "低准备步行速度倍率",
            "低准备姿态的步行移速倍率，1.10 表示加快 10%。开镜、冲刺或离开低准备时取消。",
            ConfigText.Range(1f, 1.3f));
        StanceRaiseTime = t.Bind(stance, "Raise time (s)", 0.45f, cStance, "举枪时间（秒）",
            "参考枪进入和退出据枪姿态所需的时间。按武器缩放：重、长、人机差的枪更慢（0.6–1.8 倍）。",
            ConfigText.Range(0.05f, 2f));
        LowReadyRaiseTime = t.Bind(stance, "Low ready raise time (s)", 0.70f, cStance, "低准备抬枪时间（秒）",
            "参考枪退出低准备或从低准备直接开镜时的抬枪时间，按武器缩放。数值越大，抬枪越慢。",
            ConfigText.Range(0.05f, 2f));
        ClearEnabled = t.Bind(stance, "Keep centre clear", true, cStance, "避开屏幕中心",
            "据枪时按枪的实际外形（瞄具、支架、导轨的高度）把枪往下放，直到不挡住屏幕中心。只平移，枪管方向不变。");
        ClearAngle = t.Bind(stance, "Clear angle (deg)", 3f, cStance, "中心留空（度）",
            "屏幕中心周围留空的角度：枪的任何部分都要低于中心这么多度。",
            ConfigText.Range(0f, 15f));
        ClearMax = t.Bind(stance, "Max lowering (m)", 0.15f, cStance, "最多下放（米）",
            "为避开中心最多把枪往下放多少。",
            ConfigText.Range(0f, 0.4f));
        StanceCant = t.Bind(stance, "Cant (deg)", 15f, cStance, "倾斜角（度）",
            "绕枪膛的滚转，正值 = 枪顶向左（顺时针）。不影响子弹方向。",
            ConfigText.Range(-45f, 45f));
        StanceOffset = t.Bind(stance, "Pose offset (m)", new Vector3(-0.03f, 0.02f, -0.02f), cStance, "姿态位移（米）",
            "枪在视角坐标中的平移：x = 右，y = 上，z = 前。默认往中线、往上、往里收。");
        StanceStability = t.Bind(stance, "Stability", 0.5f, cStance, "身体撑稳",
            "据枪时身体晃动（呼吸、步伐、移动）在进入手臂前被抑制的比例。不影响后坐力。",
            ConfigText.Range(0f, 1f));
        StanceDrainMultiplier = t.Bind(stance, "Arm stamina drain (x ADS)", 0.6f, cStance, "上肢耐力消耗（× 开镜）",
            "据枪时上肢耐力消耗，相对同一把枪开镜消耗的倍数。0 = 不消耗。",
            ConfigText.Range(0f, 2f));
        StanceReentryStamina = t.Bind(stance, "Re-entry stamina", 20f, cStance, "重新据枪所需耐力",
            "上肢耐力耗尽导致退出据枪后，需要恢复到这个值才能再次据枪（不超过耐力上限的一半）。",
            ConfigText.Range(1f, 100f));
        StanceCantCentred = t.Bind(stance, "Cant, centred variant (deg)", 15f, cStance, "居中变体倾斜角（度）",
            "右肩居中变体的倾斜角，正负含义同上。",
            ConfigText.Range(-45f, 45f));
        StanceCentreNormal = t.Bind(stance, "Centring, normal variant", 0f, cStance, "居中程度·右肩普通",
            "右肩普通据枪把枪膛滑向屏幕竖直中线的程度。0 = 用姿态位移 x，1 = 完全居中。",
            ConfigText.Range(0f, 1f));
        StanceCentreCentred = t.Bind(stance, "Centring, centred variant", 1f, cStance, "居中程度·右肩居中变体",
            "右肩居中变体的居中程度（见“默认居中”）。",
            ConfigText.Range(0f, 1f));
        StanceCentreLeft = t.Bind(stance, "Centring, left shoulder", 1f, cStance, "居中程度·左肩",
            "左肩据枪把枪膛滑向屏幕竖直中线的程度。0 = 留在左肩（姿态位移 x 镜像），1 = 完全居中。据枪留在进入时的肩膀，据枪中换肩键照常换肩。",
            ConfigText.Range(0f, 1f));
        StanceDefaultCentred = t.Bind(stance, "Centred by default", false, cStance, "右肩默认居中",
            "在右肩进入据枪时使用居中变体。");
        StanceCentreMaxShift = t.Bind(stance, "Centre max shift (m)", 0.25f, cStance, "居中最大位移（米）",
            "居中横移的安全上限。",
            ConfigText.Range(0.02f, 0.5f));
        StanceNotifications = t.Bind(stance, "Notifications", true, cStance, "耐力提示",
            "因上肢耐力不足拒绝或退出据枪时，在游戏里显示提示。");

        // ---- 1d. Stance hold
        HoldEnabled = t.Bind(hold, "Enabled", true, cHold, "启用积分",
            "据枪和开镜时手臂的积分项：肌肉持续出力，慢慢把枪口顶回期望位置，对抗重力下垂和持续偏斜。模型看不到后坐力，所以不影响后坐力。");
        HoldTau = t.Bind(hold, "Time constant (s)", 1.2f, cHold, "积分时间常数（秒）",
            "参考枪修正持续误差需要的时间（约修正 63%）。按武器和上肢疲劳自动缩放（见 1e）。",
            ConfigText.Range(0.1f, 5f));
        HoldMax = t.Bind(hold, "Max correction (deg)", 3f, cHold, "最大修正（度）",
            "每个方向积分修正量的上限。",
            ConfigText.Range(0.5f, 10f));
        HoldFreezeRate = t.Bind(hold, "Freeze above turn rate (deg/s)", 10f, cHold, "转动时冻结（度/秒）",
            "视角或枪的转速超过此值时，该方向的积分暂停（只保持不累积），转向时不会累积过头。",
            ConfigText.Range(1f, 90f));
        HoldFreezeError = t.Bind(hold, "Freeze above error (deg)", 1.5f, cHold, "偏差大时冻结（度）",
            "枪口离手臂目标超过此值（还在跟随或甩动中）时，该方向的积分暂停。积分只修正稳定的小偏差（下垂、持续偏斜）。",
            ConfigText.Range(0.2f, 10f));

        // ---- 1e. Weapon handling
        ProfileEnabled = t.Bind(profile, "Enabled", true, cProfile, "启用按武器调整",
            "按手上的枪调整手臂响应、起停平滑、身体转速、抖动过滤、重力下垂和积分：依据转动惯量（枪重 × 枪长²）和人机效率。参考枪正好得到 1b、1d 的基础值。关闭后所有枪用同一组值。");
        ProfileMassDistribution = t.Bind(profile, "Use mass distribution", true, cProfile, "按配件位置算惯量",
            "按每个配件的重量和它在枪上的位置（离转动中心的距离）算转动惯量和重力力矩：枪口挂消音器比肩边装重瞄具更难甩。" +
            "关闭 = 简单估算（总重 × 枪长²）。");
        ProfileRefMass = t.Bind(profile, "Reference weight (kg)", 3.5f, cProfile, "参考枪重量（公斤）",
            "得到基础值的那把参考枪的总重（含配件和弹匣）。",
            ConfigText.Range(0.5f, 15f));
        ProfileRefLength = t.Bind(profile, "Reference length (m)", 0.8f, cProfile, "参考枪长度（米）",
            "参考枪从枪身基准点到枪口的长度（含消音器，不含枪托）。",
            ConfigText.Range(0.2f, 2f));
        ProfileSoftness = t.Bind(profile, "Inertia exponent", 0.5f, cProfile, "惯量指数",
            "转动惯量对枪的影响强度：响应 ∝ 惯量^−指数。0.5 = 物理值，0 = 不影响。",
            ConfigText.Range(0f, 1f));
        ProfileWeightEmphasis = t.Bind(profile, "Weight emphasis", 0.08f, cProfile, "额外枪重权重",
            "枪械总重额外增加的影响比例：0.2 表示增加 20%。影响转向、枪口跟随和据枪响应；参考重量处倍率为 1。",
            ConfigText.Range(0f, 1f));
        ProfileErgoEffect = t.Bind(profile, "Ergonomics effect", 0.2f, cProfile, "人机效率影响",
            "人机效率带来的响应倍率：人机 20 = 1 − 此值，人机 80 = 1 + 此值。",
            ConfigText.Range(0f, 0.5f));
        ProfileFatigueHold = t.Bind(profile, "Tired arms hold slowdown", 1.5f, cProfile, "手累积分变慢",
            "上肢耐力耗空时，积分时间常数的倍率：1 + 此值（满耐力 = 1）。",
            ConfigText.Range(0f, 4f));
        ProfileKappaRef = t.Bind(profile, "Shake inertia reference", 1f, cProfile, "抖动惯性参考",
            "枪的惯性能平滑掉多少快速身体晃动（步伐、手抖）：惯量 /（惯量 + 此值）。参考枪在 1 时为 0.5；重枪平滑更多，轻枪跟着身体抖。",
            ConfigText.Range(0.05f, 10f));

        // ---- 1f. Recoil feedback
        RecoilEnabled = t.Bind(recoil, "Enabled", true, cRecoil, "启用后坐力反馈",
            "腰射和据枪时把后坐力反馈到视角。开镜时不起作用（原版开镜相机本来就跟着枪）。");
        RecoilKickHip = t.Bind(recoil, "View kick, hip (deg)", 0.6f, cRecoil, "视角被顶·腰射（度）",
            "参考弹药（5.45x39 7N6）一发、枪托抵肩时视角被顶起的角度，随后回位。按每发子弹的动量（弹头质量 × 速度，含火药气体）缩放，" +
            "和枪口上翻多少无关：手枪弹小、步枪弹大。枪口同时反向补偿，子弹方向和原版一样，只让屏幕有被顶的感觉。",
            ConfigText.Range(0f, 4f));
        RecoilKickStance = t.Bind(recoil, "View kick, stance (deg)", 0.8f, cRecoil, "视角被顶·据枪（度）",
            "据枪时视角被顶起的角度（枪托紧抵肩，传到身体的更多）。",
            ConfigText.Range(0f, 4f));
        RecoilPush = t.Bind(recoil, "Body push, hip (deg)", 0.5f, cRecoil, "身体被推·腰射（度）",
            "参考弹药一发把上身、视角和手臂一起往上推的角度。枪口也跟着抬，所以会增加后坐力。腰射时枪托没完全抵肩，推得更多。0 = 关闭。",
            ConfigText.Range(0f, 4f));
        RecoilPushStance = t.Bind(recoil, "Body push, stance (deg)", 0.2f, cRecoil, "身体被推·据枪（度）",
            "据枪时（枪托抵肩、身体撑稳）身体被推的角度。",
            ConfigText.Range(0f, 4f));
        RecoilNoStock = t.Bind(recoil, "No stock transfer (x)", 0.15f, cRecoil, "无枪托传递",
            "没有枪托或枪托折叠时，传到身体的比例（手腕和手臂吸收了大部分）。手枪装上枪托就按有枪托算。",
            ConfigText.Range(0f, 1f));
        RecoilResidual = t.Bind(recoil, "Push residual (x)", 0.3f, cRecoil, "被推残留",
            "身体被推后留下的比例，需要自己往下压，或等它慢慢回位。",
            ConfigText.Range(0f, 1f));
        RecoilResidualReturn = t.Bind(recoil, "Residual return (s)", 1.5f, cRecoil, "残留回位时间（秒）",
            "残留慢慢回到原来瞄准方向的时间常数（约回 63%）。越大越需要自己压。",
            ConfigText.Range(0.2f, 10f));
        RecoilBodyHz = t.Bind(recoil, "Upper body (Hz)", 4f, cRecoil, "上身响应（Hz）",
            "被推时上身的响应快慢（临界阻尼）。高 = 每一发顶得更脆；低 = 连射时更连贯地往上爬。",
            ConfigText.Range(1f, 10f));

        // ---- 2. Fixed offset (spike) and 3. Debug: test-only, shown with F12 "Advanced settings"
        t.Advanced = true;

        // ---- 2. Fixed offset (spike)
        YawDeg = t.Bind(spike, "Yaw offset (deg)", 10f, cSpike, "左右偏移（度）",
            "正值 = 枪口偏右。仅在“固定偏移”模式下生效。",
            ConfigText.Range(-45f, 45f));
        PitchDeg = t.Bind(spike, "Pitch offset (deg)", 0f, cSpike, "上下偏移（度）",
            "正值 = 枪口偏上。仅在“固定偏移”模式下生效。",
            ConfigText.Range(-30f, 30f));
        BlendSpeed = t.Bind(spike, "Blend speed (1/s)", 4f, cSpike, "渐变速度（1/秒）",
            "偏移淡入淡出的速度（例如开镜时）。",
            ConfigText.Range(0.5f, 30f));
        ApplyWhenAiming = t.Bind(spike, "Apply when aiming (ADS)", false, cSpike, "开镜时保留偏移",
            "开镜时保留偏移。关闭 = 开镜时偏移淡出到 0。");

        // ---- 3. Debug
        ShowOverlay = t.Bind(debug, "Show overlay", false, cDebug, "显示调试信息",
            "显示枪口方向（红点）、枪口期望位置（灰点）、屏幕中心（白点）和数据读数，并把测量数据写入 BepInEx/FreeAim_sway.log。");
        MarkerDistance = t.Bind(debug, "Marker distance (m)", 25f, cDebug, "标记距离（米）",
            "红点沿枪口方向投射的距离。",
            ConfigText.Range(2f, 300f));

        try
        {
            var harmony = new Harmony(PluginGuid);
            var target = AccessTools.Method(typeof(EFT.Animations.ProceduralWeaponAnimation), "ApplyComplexRotation");
            if (target == null)
            {
                Log.LogError("FreeAim: ProceduralWeaponAnimation.ApplyComplexRotation not found; spike disabled.");
                return;
            }
            harmony.Patch(target, postfix: new HarmonyMethod(typeof(WeaponOffsetPatch), nameof(WeaponOffsetPatch.Postfix)));
            Log.LogInfo($"{PluginName} {PluginVersion} loaded, patched {target.DeclaringType?.Name}.{target.Name}");

            var rotate = AccessTools.Method(typeof(Player), nameof(Player.Rotate), new[] { typeof(Vector2), typeof(bool) });
            if (rotate == null)
            {
                Log.LogError("FreeAim: Player.Rotate(Vector2,bool) not found; deadzone mode will not work.");
                return;
            }
            harmony.Patch(rotate, prefix: new HarmonyMethod(typeof(AimModel), nameof(AimModel.RotatePrefix)));
            Log.LogInfo("FreeAim: patched Player.Rotate");

            var shot = AccessTools.Method(typeof(Player.FirearmController), nameof(Player.FirearmController.InitiateShot));
            if (shot != null)
            {
                harmony.Patch(shot, postfix: new HarmonyMethod(typeof(RecoilFeedback), nameof(RecoilFeedback.ShotPostfix)));
                Log.LogInfo("FreeAim: patched FirearmController.InitiateShot");
            }
            else
            {
                Log.LogError("FreeAim: FirearmController.InitiateShot not found; recoil feedback disabled.");
            }

            ArmRecoveryPatch.Install(harmony);
            LowReadyWalkPatch.Install(harmony);

            var shift = AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.ShiftWeaponRoot));
            if (shift == null)
            {
                Log.LogError("FreeAim: PlayerBones.ShiftWeaponRoot not found; stance centring and left-shoulder stance disabled.");
                return;
            }
            harmony.Patch(shift, postfix: new HarmonyMethod(typeof(FreeAimPlugin), nameof(ShiftWeaponRootPostfix)));
            Log.LogInfo("FreeAim: patched PlayerBones.ShiftWeaponRoot");
        }
        catch (Exception e)
        {
            Log.LogError($"FreeAim: failed to patch: {e}");
        }
    }

    private void Update()
    {
        ReadyStance.UpdateInput();
        if (ReadyStance.IsPressedThisFrame(ToggleKey.Value))
        {
            Enabled.Value = !Enabled.Value;
            Log.LogInfo($"FreeAim: offset {(Enabled.Value ? "ON" : "OFF")}");
        }
    }

    /// <summary>Body pass, after MouseLook placed the arms rig and before hand IK (local player only).</summary>
    private static void ShiftWeaponRootPostfix(PlayerBones __instance, EPointOfView pv, float leftStanceCurveValue)
    {
        if (pv != EPointOfView.FirstPerson)
        {
            return;
        }
        var player = GetLocalPlayer();
        if (player == null || player.PlayerBones != __instance)
        {
            return;
        }
        ReadyStance.ApplyBodyPass(player, leftStanceCurveValue);
    }

    internal static Player GetLocalPlayer()
    {
        if (!Singleton<GameWorld>.Instantiated)
        {
            return null;
        }
        var player = Singleton<GameWorld>.Instance.MainPlayer;
        return player != null && player.IsYourPlayer ? player : null;
    }

    private void OnGUI()
    {
        if (!ShowOverlay.Value || Event.current.type != EventType.Repaint)
        {
            return;
        }
        var player = GetLocalPlayer();
        if (player == null || player.HandsController is not Player.FirearmController fc)
        {
            return;
        }
        Camera cam;
        try
        {
            cam = CameraManager.Instance.Camera;
        }
        catch
        {
            return;
        }
        if (cam == null || fc.CurrentFireport == null)
        {
            return;
        }

        _dot ??= Texture2D.whiteTexture;

        Vector3 muzzleDir = fc.WeaponDirection;
        Vector3 point = fc.CurrentFireport.position + muzzleDir * MarkerDistance.Value;
        Vector3 sp = cam.WorldToScreenPoint(point);

        // screen centre reference
        DrawDot(Screen.width * 0.5f, Screen.height * 0.5f, 4f, Color.white);
        if (sp.z > 0f)
        {
            // camera may render at a different resolution than the screen (render scale / upscalers)
            float x = sp.x / cam.pixelWidth * Screen.width;
            float y = Screen.height - sp.y / cam.pixelHeight * Screen.height;
            DrawDot(x, y, 6f, Color.red);
        }

        // Grey dot = T, red dot = actual muzzle R.
        Vector2 target = AimModel.TargetOffset;
        if (Mode.Value == AimMode.Deadzone && target != Vector2.zero)
        {
            Transform ct = cam.transform;
            Vector3 aimDir = Quaternion.AngleAxis(target.x, ct.up)
                             * Quaternion.AngleAxis(target.y, ct.right) * ct.forward;
            Vector3 ap = cam.WorldToScreenPoint(ct.position + aimDir * MarkerDistance.Value);
            if (ap.z > 0f)
            {
                DrawDot(ap.x / cam.pixelWidth * Screen.width, Screen.height - ap.y / cam.pixelHeight * Screen.height,
                    4f, new Color(0.6f, 0.6f, 0.6f, 0.9f));
            }
        }

        float angle = Vector3.Angle(cam.transform.forward, muzzleDir);
        SwayProbe.SampleMuzzle(player, cam, muzzleDir);
        var pwa = player.ProceduralWeaponAnimation;
        string text = $"FreeAim {Mode.Value}  {(Enabled.Value ? "ON" : "OFF")} ({ToggleKey.Value})  " +
                      $"gun yaw:{WeaponOffsetPatch.Applied.x:0.0}° pitch:{-WeaponOffsetPatch.Applied.y:0.0}°  " +
                      $"target yaw:{target.x:0.0}° pitch:{-target.y:0.0}°\n" +
                      $"camera↔muzzle angle: {angle:0.0}°   aiming:{pwa.IsAiming}  sprint:{player.MovementContext.IsSprintEnabled}\n" +
                      $"stance: {ReadyStance.Current}{(ReadyStance.Suspended ? " (paused)" : "")}  exhausted:{ReadyStance.Exhausted}  pose:{ReadyStance.PoseWeight:0.00}  A follow:x{ReadyStance.BodyFollowMultiplier:0.00}  " +
                      $"arm stamina:{player.Physical.HandsStamina.Current:0.0}  high left/down:{-HighReadyPlacement.OffsetCm.x:0.0}/{-HighReadyPlacement.OffsetCm.y:0.0}cm  " +
                      $"variant:{(ReadyStance.Centred ? "centred" : "normal")}  left shoulder:{ReadyStance.LeftBlend:0.00} left pose:{ReadyStance.LeftPoseWeight:0.00}  centre shift:{ReadyStance.CentreShift * 100f:0.0}cm  clear:{StanceClearance.Applied * 100f:0.0}/{StanceClearance.Need * 100f:0.0}cm ({StanceClearance.Boxes} parts)\n" +
                      $"game sway (deg, P+ = down)  {SwayProbe.Source.Format()}\n" +
                      $"muzzle − free aim (deg)     {SwayProbe.Muzzle.Format()}\n" +
                      $"model (P+ = down)  {AimModel.Format()}\n" +
                      $"recoil (P+ = down)  {RecoilFeedback.Format()}\n" +
                      WeaponProfile.Format();
        GUI.color = Color.black;
        GUI.Label(new Rect(11, 101, 900, 140), text);
        GUI.color = Color.yellow;
        GUI.Label(new Rect(10, 100, 900, 140), text);
        GUI.color = Color.white;
    }

    private static void DrawDot(float x, float y, float size, Color color)
    {
        GUI.color = color;
        GUI.DrawTexture(new Rect(x - size * 0.5f, y - size * 0.5f, size, size), _dot);
        GUI.color = Color.white;
    }
}
