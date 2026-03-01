using System;
using HarmonyLib;

public static class KKButtplugMilkingHooks
{
    public static event Action<Kobold> MilkingStarted;

    [HarmonyPatch(typeof(Kobold), nameof(Kobold.MilkRoutine))]
    private static class Patch_Kobold_MilkRoutine
    {
        private static void Postfix(Kobold __instance)
        {
            if (__instance == null)
                return;

            MilkingStarted?.Invoke(__instance);
        }
    }
}