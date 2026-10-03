using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using BepInEx.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace AliceInCradleLink
{
    /// <summary>DGStudio GET /data 回传的输出字段快照。</summary>
    public sealed class RemoteData
    {
        public bool online;
        public readonly Dictionary<string, string> fields = new Dictionary<string, string>();
        public string error = "";
    }

    /// <summary>
    /// 纯数据发送端客户端（事件触发 + 脉冲合并窗口）：连续值变动即时上报；
    /// 脉冲值（Hurt/Heal 等事件量）按「每单次伤害脉冲时间」合并——
    /// 首个事件立即开口输出并开启窗口，窗口内（距上次输出小于脉冲时间）
    /// 的新事件只并入同一脉冲、顺延归零期限、不产生新输出；窗口到期时
    /// 一次性输出合并累计总量，随后归零。窗口内的输出严格递增、窗口之间
    /// 以 0 隔开，因此不会被映射引擎的同值去重吞掉。
    ///
    /// 首次连上与断线恢复后全量重报连续值；POST 失败保留待发内容退避重试。
    /// GET /data 仍按 PollSeconds 轮询：那是回传字段的拉取通道，用于面板显示。
    /// </summary>
    public sealed class DataClient : IDisposable
    {
        private readonly LinkConfig _cfg;
        private readonly ManualLogSource _log;
        private readonly object _gate = new object();

        private readonly Dictionary<string, float> _payload = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _pulses = new Dictionary<string, float>();
        private readonly Dictionary<string, DateTime> _windowUntil = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> _zeroDue = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, float> _lastSent = new Dictionary<string, float>();

        private bool _dirty;        // 连续值存在未确认的变动
        private bool _announce;     // 待全量重报（首次 / 断线恢复）
        private bool _hadError;     // 距上次成功发送之间出现过失败
        private DateTime _retryAfter = DateTime.MinValue;

        private Thread _worker;
        private volatile bool _stopping;
        private DateTime _nextPoll = DateTime.MinValue;

        private RemoteData _remote = new RemoteData();
        private int _posted;
        private int _errors;
        private bool? _lastOnline;
        private string _lastError = "";

        public DataClient(LinkConfig cfg, ManualLogSource log)
        {
            _cfg = cfg;
            _log = log;
        }

        public int PostedCount => _posted;
        public int ErrorCount => _errors;
        public int FieldCount { get { lock (_gate) return _payload.Count; } }

        public RemoteData Snapshot()
        {
            lock (_gate) return _remote;
        }

        /// <summary>主线程调用：合并最新数值（同名字段取最新），有变动即唤醒上报。</summary>
        public void UpdateValues(Dictionary<string, float> values)
        {
            lock (_gate)
            {
                foreach (var kv in values)
                {
                    if (_payload.TryGetValue(kv.Key, out var cur) && cur == kv.Value)
                        continue;
                    _payload[kv.Key] = kv.Value;
                    _dirty = true;
                }
                if (_dirty) Monitor.PulseAll(_gate);
            }
        }

        /// <summary>
        /// 主线程调用：并入待发脉冲（同名累加）。距上次输出小于脉冲时间时
        /// 并入当前窗口（顺延归零期限，不产生新输出）；否则作为新脉冲开口，
        /// 由后台线程立即输出。
        /// </summary>
        public void UpdatePulses(Dictionary<string, float> pulses)
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var window = PulseWindow();
                foreach (var kv in pulses)
                {
                    _pulses[kv.Key] = (_pulses.TryGetValue(kv.Key, out var cur)
                                           ? cur : 0f) + kv.Value;
                    if (_windowUntil.TryGetValue(kv.Key, out var until) && until > now)
                        _windowUntil[kv.Key] = now + window;   // 窗口内：合并
                    else
                        _windowUntil.Remove(kv.Key);           // 无窗口/已过期：重新开口
                }
                Monitor.PulseAll(_gate);
            }
        }

        private TimeSpan PulseWindow()
        {
            return TimeSpan.FromSeconds(Mathf.Clamp(_cfg.PulseSeconds.Value, 0.05f, 10f));
        }

        public void Start()
        {
            if (_worker != null) return;
            _stopping = false;
            _worker = new Thread(Run) { IsBackground = true, Name = "DGStudioDataLink" };
            _worker.Start();
        }

        public void Dispose()
        {
            _stopping = true;
            lock (_gate) Monitor.PulseAll(_gate);
            if (_worker != null)
            {
                try { _worker.Join(500); } catch { /* 退出时忽略 */ }
                _worker = null;
            }
        }

        private void Run()
        {
            lock (_gate) _announce = true;   // 首次连上先全量报一遍静态初值
            while (!_stopping)
            {
                try
                {
                    lock (_gate)
                    {
                        var wait = WaitSpan(DateTime.UtcNow);
                        if (wait > TimeSpan.Zero) Monitor.Wait(_gate, wait);
                    }
                    if (_stopping) break;
                    var now = DateTime.UtcNow;
                    if (now >= _nextPoll)
                    {
                        _nextPoll = now.AddSeconds(Mathf.Clamp(_cfg.PollSeconds.Value, 0.1f, 5f));
                        Poll();
                    }
                    Report();
                }
                catch (Exception exc)
                {
                    Fail(exc);
                }
            }
        }

        /// <summary>距下一个待处理事件的等待时长（开口 / 收口 / 回零 / 退避 / 轮询）。</summary>
        private TimeSpan WaitSpan(DateTime now)
        {
            if (_retryAfter > now) return _retryAfter - now;
            if (_dirty || HasOpenable() || (_announce && _payload.Count > 0))
                return TimeSpan.Zero;
            foreach (var kv in _windowUntil)
                if (kv.Value <= now) return TimeSpan.Zero;   // 窗口到期待收口
            foreach (var kv in _zeroDue)
                if (kv.Value <= now) return TimeSpan.Zero;   // 回零待发
            var wait = _nextPoll - now;
            foreach (var kv in _windowUntil)
            {
                var z = kv.Value - now;
                if (z < wait) wait = z;
            }
            foreach (var kv in _zeroDue)
            {
                var z = kv.Value - now;
                if (z < wait) wait = z;
            }
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        private bool HasOpenable()
        {
            foreach (var kv in _pulses)
                if (!_windowUntil.ContainsKey(kv.Key)) return true;
            return false;
        }

        private void Report()
        {
            var now = DateTime.UtcNow;
            var cont = TakeContSnapshot(out var announce);

            // —— 阶段 Z：到期回零（先于新脉冲，隔离同名同值防吞） ——
            List<string> zeros = new List<string>();
            lock (_gate)
                foreach (var kv in _zeroDue)
                    if (kv.Value <= now) zeros.Add(kv.Key);
            if (zeros.Count > 0)
            {
                var batch = new Dictionary<string, float>();
                foreach (var n in zeros) batch[n] = 0f;
                var attached = AttachCont(batch, cont);
                if (!Post(ToJObject(batch))) return;
                lock (_gate)
                {
                    foreach (var n in zeros)
                    {
                        _zeroDue.Remove(n);
                        _lastSent[n] = 0f;
                    }
                    if (attached) { CommitCont(cont, announce); cont.Clear(); }
                }
            }

            // —— 阶段 O：新脉冲开口输出并开启合并窗口 ——
            Dictionary<string, float> openSnap;
            lock (_gate)
            {
                openSnap = new Dictionary<string, float>();
                foreach (var kv in _pulses)
                    if (!_windowUntil.ContainsKey(kv.Key))
                        openSnap[kv.Key] = kv.Value;
            }
            if (openSnap.Count > 0)
            {
                var batch = new Dictionary<string, float>(openSnap);
                var attached = AttachCont(batch, cont);
                if (!Post(ToJObject(batch))) return;
                lock (_gate)
                {
                    var window = PulseWindow();
                    foreach (var kv in openSnap)
                    {
                        Deduct(kv.Key, kv.Value);
                        _windowUntil[kv.Key] = DateTime.UtcNow + window;
                        _lastSent[kv.Key] = kv.Value;
                    }
                    if (attached) { CommitCont(cont, announce); cont.Clear(); }
                }
            }

            // —— 阶段 C：窗口到期收口（有新事件输出合并累计，随后登记回零） ——
            Dictionary<string, float> closeSnap;
            Dictionary<string, float> closeDelta;
            lock (_gate)
            {
                closeSnap = new Dictionary<string, float>();
                closeDelta = new Dictionary<string, float>();
                foreach (var kv in _windowUntil)
                {
                    if (kv.Value > now) continue;
                    var delta = _pulses.TryGetValue(kv.Key, out var d) ? d : 0f;
                    var last = _lastSent.TryGetValue(kv.Key, out var l) ? l : 0f;
                    closeSnap[kv.Key] = delta > 0f ? last + delta : 0f;
                    closeDelta[kv.Key] = delta;
                }
            }
            if (closeSnap.Count > 0)
            {
                var batch = new Dictionary<string, float>(closeSnap);
                var attached = AttachCont(batch, cont);
                if (!Post(ToJObject(batch))) return;
                lock (_gate)
                {
                    foreach (var kv in closeSnap)
                    {
                        _windowUntil.Remove(kv.Key);
                        _lastSent[kv.Key] = kv.Value;
                        if (closeDelta[kv.Key] > 0f)
                        {
                            Deduct(kv.Key, closeDelta[kv.Key]);
                            _zeroDue[kv.Key] = DateTime.UtcNow;   // 下一拍回零
                        }
                    }
                    if (attached) { CommitCont(cont, announce); cont.Clear(); }
                }
            }

            // —— 阶段 D：仅连续值 ——
            if (cont.Count > 0)
            {
                if (Post(ToJObject(cont)))
                {
                    lock (_gate) CommitCont(cont, announce);
                }
                // 失败：_lastSent 未确认，下拍自动重发
            }
        }

        /// <summary>连续值待发快照（announce 时全量）。</summary>
        private Dictionary<string, float> TakeContSnapshot(out bool announce)
        {
            lock (_gate)
            {
                announce = _announce;
                var cont = new Dictionary<string, float>();
                if (announce)
                {
                    foreach (var kv in _payload) cont[kv.Key] = kv.Value;
                }
                else
                {
                    foreach (var kv in _payload)
                        if (!_lastSent.TryGetValue(kv.Key, out var sent) || sent != kv.Value)
                            cont[kv.Key] = kv.Value;
                }
                return cont;
            }
        }

        /// <summary>把连续值搭进第一个批次；搭上了返回 true（发送成功后需 CommitCont）。</summary>
        private static bool AttachCont(Dictionary<string, float> batch,
                                       Dictionary<string, float> cont)
        {
            if (cont.Count == 0) return false;
            foreach (var kv in cont)
                batch[kv.Key] = kv.Value;
            return true;
        }

        /// <summary>连续值批次发送成功后确认基线（含 announce 撤销）。调用方须持锁。</summary>
        private void CommitCont(Dictionary<string, float> cont, bool announce)
        {
            foreach (var kv in cont)
                _lastSent[kv.Key] = kv.Value;
            _dirty = false;
            if (announce) _announce = false;
        }

        private void Deduct(string name, float amount)
        {
            // 仅在持锁时调用
            if (!_pulses.TryGetValue(name, out var cur)) return;
            cur -= amount;
            if (cur > 0f) _pulses[name] = cur;
            else _pulses.Remove(name);
        }

        private static JObject ToJObject(Dictionary<string, float> batch)
        {
            var obj = new JObject();
            foreach (var kv in batch)
                obj[kv.Key] = new JValue(kv.Value);
            return obj;
        }

        private bool Post(JObject body)
        {
            try
            {
                Request("POST", "/data", body.ToString(Formatting.None));
                MarkOnline(true);
                return true;
            }
            catch (Exception exc)
            {
                Fail(exc);
                return false;
            }
        }

        private void Poll()
        {
            var remote = new RemoteData();
            try
            {
                var root = JObject.Parse(Request("GET", "/data", null));
                remote.online = (int?)root["status"] == 1;
                var data = root["data"] as JObject;
                if (data != null)
                {
                    foreach (var prop in data.Properties())
                    {
                        if (prop.Value == null || prop.Value.Type == JTokenType.Null) continue;
                        if (prop.Value.Type == JTokenType.Boolean)
                            remote.fields[prop.Name] = (bool)prop.Value ? "1" : "0";
                        else if (prop.Value.Type == JTokenType.Integer ||
                                 prop.Value.Type == JTokenType.Float)
                            remote.fields[prop.Name] = Math.Round((double)prop.Value, 3)
                                .ToString(CultureInfo.InvariantCulture);
                        else
                            remote.fields[prop.Name] = prop.Value.ToString();
                    }
                }
                MarkOnline(true);
            }
            catch (Exception exc)
            {
                remote.online = false;
                Fail(exc);
            }
            lock (_gate) _remote = remote;
        }

        private string Request(string method, string path, string json)
        {
            var url = _cfg.BaseUrl.Value.TrimEnd('/') + path;
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Proxy = null;              // 本机回环，绕开系统代理
            request.KeepAlive = false;
            request.Timeout = 2000;
            request.ReadWriteTimeout = 2000;
            if (!string.IsNullOrEmpty(json))
            {
                var body = Encoding.UTF8.GetBytes(json);
                request.ContentType = "application/json";
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream())
                    stream.Write(body, 0, body.Length);
            }
            using (var response = (HttpWebResponse)request.GetResponse())
            using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
            {
                _posted++;
                return reader.ReadToEnd();
            }
        }

        private void MarkOnline(bool online)
        {
            _lastError = "";
            if (online && _hadError)
            {
                _hadError = false;
                lock (_gate) _announce = true;   // 断线恢复：全量重报（模块可能已重启）
            }
            if (_lastOnline == online) return;
            _lastOnline = online;
            SafeLog(online ? "已连上 DGStudio 数据服务" : "与 DGStudio 的连接中断");
        }

        /// <summary>后台线程里绝不能让日志器抛出来，否则整个线程会静默终止。</summary>
        private void SafeLog(string message)
        {
            try { _log.LogInfo(message); } catch { /* 忽略日志器异常 */ }
        }

        private void Fail(Exception exc)
        {
            _errors++;
            _hadError = true;
            _lastOnline = null;
            _retryAfter = DateTime.UtcNow.AddSeconds(0.5);   // 失败退避，避免热循环
            _lastError = exc.Message;
            if (_errors == 1 || _errors % 100 == 0)
                SafeLog($"DGStudio 通信失败 ({_errors} 次): {exc.Message}");
        }
    }
}
