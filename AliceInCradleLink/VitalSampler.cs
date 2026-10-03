using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace AliceInCradleLink
{
    /// <summary>
    /// 玩家状态采样：HP/MP/EP 与高潮计数逐帧反射读取（连续值）；差分脉冲
    /// （Hurt/Heal/MpLost/MpGain）优先来自 EventHooks 的游戏调用钩子——
    /// 每次事件取真实数值，能覆盖血量清零后 overkill 分支这类字段差分
    /// 看不见的场景；钩子安装失败的通道回退为逐帧差分。
    /// 数值口径与 DGStudio「Alice in Cradle 联动」模块 META["params"] 一致，
    /// 模组本身不做任何强度换算——映射由 DGStudio 侧表达式完成。
    /// 脉冲值经 DrainPulses 交给 DataClient 后只上报一次并自动回零，
    /// 不作为持续数值驻留在上报载荷里。
    /// </summary>
    public sealed class VitalSampler
    {
        private readonly LinkConfig _cfg;
        private readonly ManualLogSource _log;

        private nel.PRNoel _noel;
        private VitalReader _vitals;
        private DateTime _nextScan = DateTime.MinValue;
        private bool _loggedMissing;
        private HookStatus _hooks = new HookStatus();

        private int? _hp;
        private int? _mp;
        private int? _orgasms;
        private DateTime _orgasmEnds = DateTime.MinValue;
        private readonly object _gate = new object();
        private readonly Dictionary<string, float> _pending = new Dictionary<string, float>();

        public int Hp { get; private set; }
        public int HpMax { get; private set; }
        public int Mp { get; private set; }
        public int MpMax { get; private set; }
        public int Ep { get; private set; }
        public int OrgasmCount { get; private set; }
        public int SignalCount { get; private set; }
        public string LastSignal { get; private set; } = "—";
        public bool Ready => _noel != null;

        public VitalSampler(LinkConfig cfg, ManualLogSource log)
        {
            _cfg = cfg;
            _log = log;
        }

        public void Tick()
        {
            if (_noel == null)
            {
                if (DateTime.UtcNow < _nextScan) return;
                _nextScan = DateTime.UtcNow.AddSeconds(1);
                _noel = UnityEngine.Object.FindObjectOfType<nel.PRNoel>();
                if (_noel == null)
                {
                    if (!_loggedMissing)
                    {
                        _loggedMissing = true;
                        _log.LogInfo("尚未找到玩家组件（PRNoel），进入游戏后自动开始上报");
                    }
                    return;
                }
                _vitals = new VitalReader(typeof(nel.PRNoel));
                if (!_vitals.Ready)
                {
                    _log.LogError("玩家组件缺少 hp/maxhp/mp/maxmp/ep 字段，" +
                                  "当前游戏版本可能与此模组不兼容，联动已停止");
                    _noel = null;
                    return;
                }
                _log.LogInfo("已接入玩家状态：开始向 DGStudio 上报游戏数值");
                Reset();
            }

            _vitals.Read(_noel, out var hp, out var hpMax, out var mp, out var mpMax, out var ep);
            Hp = hp;
            HpMax = hpMax;
            Mp = mp;
            MpMax = mpMax;
            Ep = ep;
            if (_noel.EpCon != null) OrgasmCount = _noel.EpCon.getOrgasmedTotal();

            // 差分仅作钩子未覆盖通道的回退；基线照常推进，避免事件与差分双计
            var diffHp = Diff(ref _hp, hp);
            if (!_hooks.Hurt && diffHp < 0) Signal("Hurt", -diffHp, "受伤");
            else if (!_hooks.Heal && diffHp > 0) Signal("Heal", diffHp, "回血");

            var diffMp = Diff(ref _mp, mp);
            if (!_hooks.MpLost && diffMp < 0) Signal("MpLost", -diffMp, "耗蓝");
            else if (!_hooks.MpGain && diffMp > 0) Signal("MpGain", diffMp, "回蓝");

            var diffOrgasm = Diff(ref _orgasms, OrgasmCount);
            if (diffOrgasm > 0)
            {
                _orgasmEnds = DateTime.UtcNow.AddMilliseconds(Math.Max(0, _cfg.OrgasmHoldMs.Value));
                // 累计计数走连续值字段每拍上报；这里只刷新面板事件指示
                Note(OrgasmCount, "高潮");
            }
        }

        /// <summary>重新挂接时清空基线，避免把场景切换的数值跳变当成信号。</summary>
        public void Reset()
        {
            _hp = null;
            _mp = null;
            _orgasms = null;
            _orgasmEnds = DateTime.MinValue;
            lock (_gate) _pending.Clear();
        }

        /// <summary>注入事件钩子安装结果（各通道是否由真实事件提供）。</summary>
        public void SetHooks(HookStatus hooks)
        {
            _hooks = hooks ?? new HookStatus();
        }

        // --- EventHooks 回调：游戏调用事件（主线程） -------------------

        public void OnEventHurt(int amount)
        {
            if (amount > 0) Signal("Hurt", amount, "受伤");
        }

        public void OnEventHeal(int amount)
        {
            if (amount > 0) Signal("Heal", amount, "回血");
        }

        public void OnEventMpLost(int amount)
        {
            if (amount > 0) Signal("MpLost", amount, "耗蓝");
        }

        public void OnEventMpGain(int amount)
        {
            if (amount > 0) Signal("MpGain", amount, "回蓝");
        }

        /// <summary>合并最新连续值载荷：HP/MP 等每拍常驻的数值字段。</summary>
        public void Collect(Dictionary<string, float> sink)
        {
            if (!Ready) return;
            sink["HP"] = Hp;
            sink["HPmax"] = HpMax;
            sink["MP"] = Mp;
            sink["MPmax"] = MpMax;
            sink["EP"] = Ep;
            sink["Orgasm"] = OrgasmCount;
            sink["Orgasming"] = DateTime.UtcNow < _orgasmEnds ? 1f : 0f;
        }

        /// <summary>取走本帧产生的差分脉冲（取后清空），交给脉冲通道上报。</summary>
        public void DrainPulses(Dictionary<string, float> sink)
        {
            lock (_gate)
            {
                foreach (var kv in _pending)
                    sink[kv.Key] = kv.Value;
                _pending.Clear();
            }
        }

        private int Diff(ref int? baseline, int value)
        {
            var last = baseline;
            baseline = value;
            if (!last.HasValue) return 0;
            var diff = value - last.Value;
            if (diff == 0 || Mathf.Abs(diff) >= _cfg.MaxChange.Value) return 0;
            return diff;
        }

        private void Signal(string name, float value, string label)
        {
            // 同帧内可能多次事件（连续多段伤害等）：按名字累加成一次总量
            lock (_gate)
                _pending[name] = (_pending.TryGetValue(name, out var cur)
                                      ? cur : 0f) + value;
            SignalCount++;
            LastSignal = label + " " + Mathf.RoundToInt(value);
        }

        /// <summary>只刷新面板事件指示：值本身已在连续值字段里每拍上报。</summary>
        private void Note(float value, string label)
        {
            SignalCount++;
            LastSignal = label + " " + Mathf.RoundToInt(value);
        }
    }
}
