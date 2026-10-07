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
    public sealed class RemoteData
    {
        public bool online;
        public readonly Dictionary<string, string> fields = new Dictionary<string, string>();
        public string error = "";
    }

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

        private bool _dirty;
        private bool _announce;
        private bool _hadError;
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
                        _windowUntil[kv.Key] = now + window;
                    else
                        _windowUntil.Remove(kv.Key);
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
                try { _worker.Join(500); } catch { }
                _worker = null;
            }
        }

        private void Run()
        {
            lock (_gate) _announce = true;
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

        private TimeSpan WaitSpan(DateTime now)
        {
            if (_retryAfter > now) return _retryAfter - now;
            if (_dirty || HasOpenable() || (_announce && _payload.Count > 0))
                return TimeSpan.Zero;
            foreach (var kv in _windowUntil)
                if (kv.Value <= now) return TimeSpan.Zero;
            foreach (var kv in _zeroDue)
                if (kv.Value <= now) return TimeSpan.Zero;
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
                            _zeroDue[kv.Key] = DateTime.UtcNow;
                        }
                    }
                    if (attached) { CommitCont(cont, announce); cont.Clear(); }
                }
            }

            if (cont.Count > 0)
            {
                if (Post(ToJObject(cont)))
                {
                    lock (_gate) CommitCont(cont, announce);
                }
            }
        }

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

        private static bool AttachCont(Dictionary<string, float> batch,
                                       Dictionary<string, float> cont)
        {
            if (cont.Count == 0) return false;
            foreach (var kv in cont)
                batch[kv.Key] = kv.Value;
            return true;
        }

        private void CommitCont(Dictionary<string, float> cont, bool announce)
        {
            foreach (var kv in cont)
                _lastSent[kv.Key] = kv.Value;
            _dirty = false;
            if (announce) _announce = false;
        }

        private void Deduct(string name, float amount)
        {
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
            request.Proxy = null;
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
                lock (_gate) _announce = true;
            }
            if (_lastOnline == online) return;
            _lastOnline = online;
            SafeLog(online ? "已连上 DGStudio 数据服务" : "与 DGStudio 的连接中断");
        }

        private void SafeLog(string message)
        {
            try { _log.LogInfo(message); } catch { }
        }

        private void Fail(Exception exc)
        {
            _errors++;
            _hadError = true;
            _lastOnline = null;
            _retryAfter = DateTime.UtcNow.AddSeconds(0.5);
            _lastError = exc.Message;
            if (_errors == 1 || _errors % 100 == 0)
                SafeLog($"DGStudio 通信失败 ({_errors} 次): {exc.Message}");
        }
    }
}
