using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace AliceInCradleLink
{
    /// <summary>
    /// 玩家状态采样：每帧反射读取 HP/MP/EP 与高潮计数，产生瞬时脉冲差分
    /// 信号（Hurt/Heal/MpLost/MpGain），并维护 Orgasming 保持窗。
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

            var diffHp = Diff(ref _hp, hp);
            if (diffHp < 0) Signal("Hurt", -diffHp, "受伤");
            else if (diffHp > 0) Signal("Heal", diffHp, "回血");

            var diffMp = Diff(ref _mp, mp);
            if (diffMp < 0) Signal("MpLost", -diffMp, "耗蓝");
            else if (diffMp > 0) Signal("MpGain", diffMp, "回蓝");

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
            lock (_gate) _pending[name] = value;
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
