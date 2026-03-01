using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;

// Orgasm hooks:
// - Male orgasm duration: wraps DickDescriptor.CumRoutine() enumerator so we get start + end.
// - Female orgasm: Kobold.Cum() fires but there is no CumRoutine; we emit FemaleOrgasmTriggered.
[HarmonyPatch]
public static class KKButtplugOrgasmHooks
{
    public static event Action<Kobold> MaleOrgasmStarted;
    public static event Action<Kobold> MaleOrgasmEnded;
    public static event Action<Kobold> FemaleOrgasmTriggered;

    // Cache reflection field once (not per call)
    private static readonly FieldInfo _attachedKoboldField =
        typeof(DickDescriptor).GetField(
            "attachedKobold",
            BindingFlags.Instance | BindingFlags.NonPublic
        );

    // =============================
    // Male orgasm (wrap CumRoutine)
    // =============================
    [HarmonyPatch(typeof(DickDescriptor), nameof(DickDescriptor.CumRoutine))]
    private static class Patch_DickDescriptor_CumRoutine
    {
        [HarmonyPostfix]
        private static IEnumerator Postfix(IEnumerator __result, DickDescriptor __instance)
        {
            if (__result == null)
                yield break;

            Kobold k = GetAttachedKobold(__instance);

            if (k != null)
                MaleOrgasmStarted?.Invoke(k);

            bool finishedNaturally = false;

            while (__result.MoveNext())
            {
                yield return __result.Current;
            }

            finishedNaturally = true;

            if (k != null && finishedNaturally)
                MaleOrgasmEnded?.Invoke(k);
        }
    }

    // =============================
    // Female orgasm (Kobold.Cum)
    // =============================
    [HarmonyPatch(typeof(Kobold), nameof(Kobold.Cum))]
    private static class Patch_Kobold_Cum
    {
        [HarmonyPostfix]
        private static void Postfix(Kobold __instance)
        {
            if (__instance == null)
                return;

            // Female/no-dick orgasm has no coroutine duration signal.
            if (__instance.activeDicks == null || __instance.activeDicks.Count == 0)
                FemaleOrgasmTriggered?.Invoke(__instance);
        }
    }

    private static Kobold GetAttachedKobold(DickDescriptor descriptor)
    {
        if (descriptor == null || _attachedKoboldField == null)
            return null;

        try
        {
            return (Kobold)_attachedKoboldField.GetValue(descriptor);
        }
        catch
        {
            return null;
        }
    }
}