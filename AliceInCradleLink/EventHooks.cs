using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using HarmonyLib;

namespace AliceInCradleLink
{
    /// <summary>各事件通道是否由游戏调用钩子提供（否则采样器回退为字段差分）。</summary>
    public sealed class HookStatus
    {
        public bool Hurt;
        public bool Heal;
        public bool MpLost;
        public bool MpGain;
        public bool All => Hurt && Heal && MpLost && MpGain;
    }

    /// <summary>
    /// 游戏事件钩子（Harmony postfix）：监听玩家 nel.PRNoel 真实的
    /// 受伤/耗蓝/回血/回蓝调用，取得每次事件的精确数值，替代字段差分。
    ///
    /// 依据反汇编确认的覆盖链与调用关系（ver030）：
    ///   PRNoel : PRMain : PR : M2MoverPr : M2AttackableP : M2Attackable
    /// * applyHpDamage 全链无重写——补丁打在 M2Attackable，实例过滤 PRNoel。
    ///   血量清零后游戏走 overkill 分支：hp 字段停在 0，后续伤害仍以完整
    ///   数值送达本方法并随伤害数字显示——字段差分对此完全失明，钩子能看到；
    /// * applyMpDamage 被 PR 重写为 3/5/8 参漏斗（3→5→8→基类），只补 8 参
    ///   末级实现，避免重载互调导致重复计数；返回值为实际消耗的 MP；
    /// * cureHp 被 PR 重写，1 参转发 3 参，只补 3 参；cureMp 两个重载各自
    ///   独立实现，都补；它们返回 void——前缀记旧值、后缀取实际变化量
    ///   （数值钳满时变化为 0，不产生虚增脉冲）；
    /// * 无敌帧/减伤判定后 apply* 返回 0，后缀按 &lt;=0 过滤。
    /// </summary>
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
                "applyMpDamage", new[] { 8 }, null, Hm(nameof(MpPost))) > 0;
            status.Heal = PatchOverloads(harmony, typeof(nel.PR),
                "cureHp", new[] { 3 }, Hm(nameof(CureHpPre)), Hm(nameof(CureHpPost))) > 0;
            status.MpGain = PatchOverloads(harmony, typeof(nel.PR),
                "cureMp", new[] { 1, 4 }, Hm(nameof(CureMpPre)), Hm(nameof(CureMpPost))) > 0;
            return status;
        }

        /// <summary>
        /// 按方法名与参数个数筛选声明重载（public 供兼容性测试复用）。
        /// 返回的每个 MethodInfo 都会被打上对应补丁。
        /// </summary>
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

        /// <summary>按方法名与参数个数逐重载补丁；返回成功个数。</summary>
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

        /// <summary>把静态处理方法包装成 HarmonyMethod。</summary>
        private static HarmonyMethod Hm(string name)
        {
            return new HarmonyMethod(typeof(EventHooks).GetMethod(name,
                BindingFlags.Static | BindingFlags.NonPublic));
        }

        // --- applyHpDamage：__result = 实际应用伤害（overkill 时不钳制） ---
        private static void HpPost(m2d.M2Attackable __instance, int __result)
        {
            if (__result > 0 && __instance is nel.PRNoel)
                _sampler.OnEventHurt(__result);
        }

        // --- applyMpDamage：__result = 实际消耗的 MP ---
        private static void MpPost(nel.PR __instance, int __result)
        {
            if (__result > 0 && __instance is nel.PRNoel)
                _sampler.OnEventMpLost(__result);
        }

        // --- cureHp/cureMp：前缀记旧值，后缀取实际变化量 ---
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
            try { _log.LogWarning(message); } catch { /* 忽略日志器异常 */ }
        }
    }
}
