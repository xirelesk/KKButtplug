using System;
using System.Collections;
using HarmonyLib;

// Orgasm hooks:
// - Male orgasm duration: wraps DickDescriptor.CumRoutine() enumerator so we get start + end.
// - Female orgasm: Kobold.Cum() fires but there is no CumRoutine; we emit FemaleOrgasmTriggered.
public static class KKButtplugOrgasmHooks
{
    public static event Action<Kobold> MaleOrgasmStarted;
    public static event Action<Kobold> MaleOrgasmEnded;
    public static event Action<Kobold> FemaleOrgasmTriggered;

    // Incremented whenever we reattach/reset to invalidate in-flight events.
    public static int OrgasmGeneration = 0;

    [HarmonyPatch(typeof(DickDescriptor), nameof(DickDescriptor.CumRoutine))]
    private static class Patch_DickDescriptor_CumRoutine
    {
        // Postfix replacement enumerator: we yield the original but can run code before/after.
        private static IEnumerator Postfix(IEnumerator __result, DickDescriptor __instance)
        {
            Kobold k = GetAttachedKobold(__instance);
            if (k != null)
                MaleOrgasmStarted?.Invoke(k);

            while (__result != null && __result.MoveNext())
                yield return __result.Current;

            if (k != null)
                MaleOrgasmEnded?.Invoke(k);
        }
    }

    [HarmonyPatch(typeof(Kobold), nameof(Kobold.Cum))]
    private static class Patch_Kobold_Cum
    {
        private static void Postfix(Kobold __instance)
        {
            if (__instance == null) return;

            // Female/no-dick orgasm has no coroutine duration signal.
            if (__instance.activeDicks == null || __instance.activeDicks.Count == 0)
                FemaleOrgasmTriggered?.Invoke(__instance);
        }
    }

    private static Kobold GetAttachedKobold(DickDescriptor descriptor)
    {
        // DickDescriptor has a private field "attachedKobold".
        // Using reflection here is OK because it's not per-frame, and avoids a brittle "FindObjectOfType" guess.
        try
        {
            var f = typeof(DickDescriptor).GetField("attachedKobold",
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic);

            return f != null ? (Kobold)f.GetValue(descriptor) : null;
        }
        catch
        {
            return null;
        }
    }
}