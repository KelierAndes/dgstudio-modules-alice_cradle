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
    /// 纯数据发送端客户端：主线程只写最新数值载荷，后台线程按配置周期
    /// POST /data 上报游戏数值、GET /data 拉取回传字段。
    /// 强度换算与设备命令全部由 DGStudio 侧映射表完成，模组不做任何换算。
    /// </summary>
    public sealed class DataClient : IDisposable
    {
        private readonly LinkConfig _cfg;
        private readonly ManualLogSource _log;
        private readonly object _gate = new object();
        private readonly Dictionary<string, float> _payload = new Dictionary<string, float>();

        private Thread _worker;
        private volatile bool _stopping;

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

        /// <summary>主线程调用：合并最新数值（同名字段取最新）。</summary>
        public void UpdateValues(Dictionary<string, float> values)
        {
            lock (_gate)
            {
                foreach (var kv in values)
                    _payload[kv.Key] = kv.Value;
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
            if (_worker != null)
            {
                try { _worker.Join(500); } catch { /* 退出时忽略 */ }
                _worker = null;
            }
        }

        private void Run()
        {
            var nextReport = DateTime.MinValue;
            var nextPoll = DateTime.MinValue;
            while (!_stopping)
            {
                try
                {
                    var now = DateTime.UtcNow;
                    if (now >= nextReport)
                    {
                        nextReport = now.AddSeconds(Mathf.Clamp(_cfg.ReportSeconds.Value, 0.1f, 5f));
                        Report();
                    }
                    if (now >= nextPoll)
                    {
                        nextPoll = now.AddSeconds(Mathf.Clamp(_cfg.PollSeconds.Value, 0.1f, 5f));
                        Poll();
                    }
                }
                catch (Exception exc)
                {
                    Fail(exc);
                }
                Thread.Sleep(30);
            }
        }

        private void Report()
        {
            Dictionary<string, float> batch;
            lock (_gate)
            {
                if (_payload.Count == 0) return;
                batch = new Dictionary<string, float>(_payload);
            }
            try
            {
                var obj = new JObject();
                foreach (var kv in batch)
                    obj[kv.Key] = new JValue(kv.Value);
                Request("POST", "/data", obj.ToString(Formatting.None));
                MarkOnline(true);
            }
            catch (Exception exc)
            {
                Fail(exc);
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
            _lastError = exc.Message;
            if (_errors == 1 || _errors % 100 == 0)
                SafeLog($"DGStudio 通信失败 ({_errors} 次): {exc.Message}");
        }
    }
}
