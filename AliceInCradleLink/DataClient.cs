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
    /// 纯数据发送端客户端（事件触发上报）：主线程写入最新数值，任何变动
    /// 立即唤醒后台线程 POST /data；无变动不通信，不做周期性上报。
    ///
    /// 脉冲字段（Hurt/Heal 等差分信号）同名累加后只随一次 POST 发出，并在
    /// 回零延迟后补发一次 0 让下游映射回到基线；若回零前又来了同值脉冲，
    /// 先补 0 再发脉冲——映射引擎对同值信号去重，不隔值会被吞掉。
    /// 连续值只发相对上次成功发送的变动字段；首次连上与断线恢复后全量
    /// 重报，保证模块重启后映射引擎能重新拿到静态值。
    /// POST 失败时脉冲与变动保留重试（不丢数据），并做固定退避。
    /// GET /data 仍按 PollSeconds 轮询：那是回传字段的拉取通道，用于面板显示。
    /// </summary>
    public sealed class DataClient : IDisposable
    {
        private readonly LinkConfig _cfg;
        private readonly ManualLogSource _log;
        private readonly object _gate = new object();

        private readonly Dictionary<string, float> _payload = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _pulses = new Dictionary<string, float>();
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
        /// 主线程调用：并入待发脉冲（同名累加）并立即触发上报。一个唤醒
        /// 周期内的多次变化合并为一次总量；逐帧到达的事件各自成拍送达。
        /// </summary>
        public void UpdatePulses(Dictionary<string, float> pulses)
        {
            lock (_gate)
            {
                foreach (var kv in pulses)
                    _pulses[kv.Key] = (_pulses.TryGetValue(kv.Key, out var cur)
                                           ? cur : 0f) + kv.Value;
                Monitor.PulseAll(_gate);
            }
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

        /// <summary>距下一个待处理事件（待发内容 / 回零 / 退避 / 轮询）的等待时长。</summary>
        private TimeSpan WaitSpan(DateTime now)
        {
            if (_retryAfter > now) return _retryAfter - now;
            if (_dirty || _pulses.Count > 0 || (_announce && _payload.Count > 0))
                return TimeSpan.Zero;
            foreach (var due in _zeroDue.Values)
                if (due <= now) return TimeSpan.Zero;
            var wait = _nextPoll - now;
            foreach (var due in _zeroDue.Values)
            {
                var z = due - now;
                if (z < wait) wait = z;
            }
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        private void Report()
        {
            // 同值防吞：与上次成功发出的值相同的脉冲，必须先补发一个 0 隔开，
            // 否则映射引擎的同值去重会把这次事件整个丢掉
            List<string> swallowed = new List<string>();
            lock (_gate)
            {
                foreach (var kv in _pulses)
                    if (_lastSent.TryGetValue(kv.Key, out var sent) && sent == kv.Value)
                        swallowed.Add(kv.Key);
            }
            if (swallowed.Count > 0)
            {
                var zeros = new JObject();
                foreach (var name in swallowed)
                    zeros[name] = new JValue(0f);
                if (!Post(zeros)) return;
                lock (_gate)
                {
                    foreach (var name in swallowed)
                    {
                        _lastSent[name] = 0f;
                        _zeroDue.Remove(name);   // 回零义务已提前履行
                    }
                }
            }

            Dictionary<string, float> batch;
            Dictionary<string, float> sentPulses;
            List<string> zeroed;
            bool announce;
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                announce = _announce;
                batch = new Dictionary<string, float>();
                if (announce)
                {
                    foreach (var kv in _payload) batch[kv.Key] = kv.Value;
                }
                else
                {
                    // 连续值只发变动字段（相对上次成功发出的值）
                    foreach (var kv in _payload)
                        if (!_lastSent.TryGetValue(kv.Key, out var sent) || sent != kv.Value)
                            batch[kv.Key] = kv.Value;
                }
                foreach (var kv in _pulses)
                    batch[kv.Key] = kv.Value;
                zeroed = new List<string>();
                foreach (var kv in _zeroDue)
                    if (kv.Value <= now && !batch.ContainsKey(kv.Key))
                    {
                        batch[kv.Key] = 0f;
                        zeroed.Add(kv.Key);
                    }
                if (batch.Count == 0)
                {
                    if (!announce) _dirty = false;
                    // 全量重报但载荷为空（游戏尚未采样）：保持 announce 等首帧
                    return;
                }
                sentPulses = new Dictionary<string, float>(_pulses);
                _pulses.Clear();
                _announce = false;
                _dirty = false;
            }
            if (Post(ToJObject(batch)))
            {
                lock (_gate)
                {
                    var now = DateTime.UtcNow;
                    var delay = TimeSpan.FromSeconds(
                        Mathf.Clamp(_cfg.ZeroDelaySeconds.Value, 0.05f, 10f));
                    foreach (var kv in batch)
                        _lastSent[kv.Key] = kv.Value;
                    foreach (var name in zeroed)
                        _zeroDue.Remove(name);
                    // 脉冲发出后重新登记回零；同名新脉冲会顺延（去抖）
                    foreach (var kv in sentPulses)
                        _zeroDue[kv.Key] = now + delay;
                }
            }
            else
            {
                // 失败：脉冲放回、变动保留（_lastSent 未确认，自动重发）
                lock (_gate)
                {
                    foreach (var kv in sentPulses)
                        _pulses[kv.Key] = (_pulses.TryGetValue(kv.Key, out var cur)
                                               ? cur : 0f) + kv.Value;
                    _announce |= announce;
                    _dirty = true;
                }
            }
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
