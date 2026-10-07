using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace AliceInCradleLink
{
    public sealed class HookStatus
    {
        public bool Hurt;
        public bool Heal;
        public bool MpLost;
        public bool MpGain;
        public bool All => Hurt && Heal && MpLost && MpGain;
    }

    public static class EventHooks
    {
        private static ManualLogSource _log;
        private static VitalSampler _sampler;
        private static FieldInfo _hpField;
        private static FieldInfo _mpField;

        public static HookStatus Install(Harmony harmony, VitalSampler sampler,
                                         ManualLogSource log)
        {
            _log = log;
            _sampler = sampler;
            const BindingFlags Inst = BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic;
            _hpField = typeof(m2d.M2Attackable).GetField("hp", Inst);
            _mpField = typeof(m2d.M2Attackable).GetField("mp", Inst);
            var status = new HookStatus();

            status.Hurt = PatchOverloads(harmony, typeof(m2d.M2Attackable),
                "applyHpDamage", null, null, Hm(nameof(HpPost))) > 0;
            status.MpLost = PatchOverloads(harmony, typeof(nel.PR),
                "applyMpDamage", new[] { 8 }, Hm(nameof(MpPre)), Hm(nameof(MpPost))) > 0;
            status.Heal = PatchOverloads(harmony, typeof(nel.PR),
                "cureHp", new[] { 3 }, Hm(nameof(CureHpPre)), Hm(nameof(CureHpPost))) > 0;
            status.MpGain = PatchOverloads(harmony, typeof(nel.PR),
                "cureMp", new[] { 1, 4 }, Hm(nameof(CureMpPre)), Hm(nameof(CureMpPost))) > 0;
            return status;
        }

        public static MethodInfo[] EnumerateTargets(Type type, string name,
                                                    int[] paramCounts)
        {
            var found = new List<MethodInfo>();
            const BindingFlags Decl = BindingFlags.DeclaredOnly |
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            foreach (var method in type.GetMethods(Decl))
            {
                if (method.Name != name) continue;
                if (paramCounts != null &&
                    Array.IndexOf(paramCounts, method.GetParameters().Length) < 0)
                    continue;
                found.Add(method);
            }
            return found.ToArray();
        }

        private static int PatchOverloads(Harmony harmony, Type type, string name,
                                          int[] paramCounts, HarmonyMethod prefix,
                                          HarmonyMethod postfix)
        {
            var targets = EnumerateTargets(type, name, paramCounts);
            var ok = 0;
            foreach (var method in targets)
            {
                try
                {
                    harmony.Patch(method, prefix, postfix);
                    ok++;
                }
                catch (Exception exc)
                {
                    SafeLog($"事件钩子 {type.Name}.{name}/{method.GetParameters().Length} 安装失败: {exc.Message}");
                }
            }
            if (ok == 0)
                SafeLog($"事件钩子 {type.Name}.{name} 不可用，对应通道回退为字段差分");
            return ok;
        }

        private static HarmonyMethod Hm(string name)
        {
            return new HarmonyMethod(typeof(EventHooks).GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        private static void HpPost(m2d.M2Attackable __instance, int __result)
        {
            if (__result > 0 && __instance is nel.PRNoel)
                _sampler.OnEventHurt(__result);
        }

        private static void MpPre(nel.PR __instance, int val, ref int[] __state)
        {
            __state = new[] { val, (int)_mpField.GetValue(__instance) };
        }

        private static void MpPost(nel.PR __instance, int __result, int[] __state)
        {
            if (!(__instance is nel.PRNoel)) return;
            if (__result > 0)
                _sampler.OnEventMpLost(__result);
            else if (__state[0] > 0 && __state[1] <= 0)
                _sampler.OnEventMpLost(__state[0]);
        }

        private static void CureHpPre(nel.PR __instance, ref int __state)
        {
            __state = (int)_hpField.GetValue(__instance);
        }

        private static void CureHpPost(nel.PR __instance, int __state)
        {
            var delta = (int)_hpField.GetValue(__instance) - __state;
            if (delta > 0)
                _sampler.OnEventHeal(delta);
        }

        private static void CureMpPre(nel.PR __instance, ref int __state)
        {
            __state = (int)_mpField.GetValue(__instance);
        }

        private static void CureMpPost(nel.PR __instance, int __state)
        {
            var delta = (int)_mpField.GetValue(__instance) - __state;
            if (delta > 0)
                _sampler.OnEventMpGain(delta);
        }

        private static void SafeLog(string message)
        {
            try { _log.LogWarning(message); } catch { }
        }
    }
}
