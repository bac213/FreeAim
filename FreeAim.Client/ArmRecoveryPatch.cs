using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using HarmonyLib;

namespace FreeAim.Client;

internal static class ArmRecoveryPatch
{
    internal static void Install(Harmony harmony)
    {
        var process = AccessTools.Method(typeof(Stamina), nameof(Stamina.Process), new[] { typeof(float) });
        if (process == null)
        {
            FreeAimPlugin.Log.LogWarning("FreeAim: stamina process not found; low ready recovery disabled.");
            return;
        }
        try
        {
            harmony.Patch(process, transpiler: new HarmonyMethod(typeof(ArmRecoveryPatch), nameof(Transpiler)));
        }
        catch (Exception e)
        {
            FreeAimPlugin.Log.LogWarning($"FreeAim: could not patch low ready recovery: {e.Message}");
        }
    }

    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var code = instructions.ToList();
        var field = AccessTools.Field(typeof(Stamina), nameof(Stamina.SelfRestoration));
        var readRate = AccessTools.Method(typeof(Compute<float>), "op_Implicit", new[] { typeof(Compute<float>) });
        var scale = AccessTools.Method(typeof(ReadyStance), nameof(ReadyStance.ScaleArmRecovery));
        for (int i = 0; i + 1 < code.Count; i++)
        {
            if (code[i].opcode != OpCodes.Ldfld || !Equals(code[i].operand, field)
                || code[i + 1].opcode != OpCodes.Call || !Equals(code[i + 1].operand, readRate))
            {
                continue;
            }
            // The vanilla branch has checked cooldown and capacity. Scale only the natural rate,
            // before the game applies elapsed time and its own stamina multiplier.
            code.Insert(i + 2, new CodeInstruction(OpCodes.Ldarg_0));
            code.Insert(i + 3, new CodeInstruction(OpCodes.Call, scale));
            return code;
        }
        FreeAimPlugin.Log.LogWarning("FreeAim: natural stamina recovery rate not found; low ready recovery disabled.");
        return code;
    }
}
