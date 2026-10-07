using System;
using System.Threading;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace AliceInCradleLink
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("AliceInCradle.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "dev.dgstudio.alicein_cradle.link";
        public const string PluginName = "AliceInCradleLink";
        public const string PluginVersion = "0.5.0";

        private static ManualLogSource Log;

        private LinkConfig _cfg;
        private DataClient _client;
        private VitalSampler _sampler;
        private OverlayUi _overlay;
        private SynchronizationContext _syncCtx;
        private Timer _watchdog;
        private GameObject _runnerGo;
        private LinkRunner _runner;
        private int _respawns;

        private void Awake()
        {
            Log = Logger;
            _cfg = new LinkConfig(Config);
            _client = new DataClient(_cfg, Log);
            _sampler = new VitalSampler(_cfg, Log);
            var hooks = EventHooks.Install(new Harmony(PluginGuid), _sampler, Log);
            _sampler.SetHooks(hooks);
            Log.LogInfo("事件钩子就绪情况：受伤=" + (hooks.Hurt ? "√" : "×（差分回退）") +
                        " 回血=" + (hooks.Heal ? "√" : "×（差分回退）") +
                        " 耗蓝=" + (hooks.MpLost ? "√" : "×（差分回退）") +
                        " 回蓝=" + (hooks.MpGain ? "√" : "×（差分回退）"));
            _overlay = new OverlayUi(_cfg, _client, _sampler);
            _client.Start();

            _syncCtx = SynchronizationContext.Current;
            EnsureRunner();
            _watchdog = new Timer(_ => _syncCtx?.Post(_ => EnsureRunner(), null),
                                  null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            Application.quitting += OnQuitting;

            Log.LogInfo($"联动模组已加载：目标 {_cfg.BaseUrl.Value}，按 {_cfg.OverlayKey.Value} 切换状态面板" +
                        $"（同步上下文 {(_syncCtx == null ? "缺失" : "可用")}）");
        }

        private void EnsureRunner()
        {
            if (_runner != null && _runnerGo != null) return;
            if (_runnerGo != null) Destroy(_runnerGo);

            _runnerGo = new GameObject("AliceInCradleLink");
            DontDestroyOnLoad(_runnerGo);
            _runner = _runnerGo.AddComponent<LinkRunner>();
            _runner.Init(_cfg, _sampler, _client, _overlay, Log);

            _respawns++;
            if (_respawns <= 3)
                Log.LogInfo($"每帧执行组件已挂载（第 {_respawns} 次）");
        }

        private void OnQuitting()
        {
            _watchdog?.Dispose();
            _client?.Dispose();
        }
    }
}
