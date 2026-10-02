using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace AliceInCradleLink
{
    /// <summary>
    /// 真正承载每帧逻辑的组件。本作的场景切换会连带销毁 BepInEx 自带的管理器对象，
    /// 因此逻辑挂在自己的 GameObject 上，由后台线程通过 Unity 同步上下文定时重建。
    /// </summary>
    public class LinkRunner : MonoBehaviour
    {
        private LinkConfig _cfg;
        private VitalSampler _sampler;
        private DataClient _client;
        private OverlayUi _overlay;
        private ManualLogSource _log;
        private readonly Dictionary<string, float> _values = new Dictionary<string, float>();
        private bool _firstFrame = true;

        public void Init(LinkConfig cfg, VitalSampler sampler, DataClient client,
                         OverlayUi overlay, ManualLogSource log)
        {
            _cfg = cfg;
            _sampler = sampler;
            _client = client;
            _overlay = overlay;
            _log = log;
        }

        private void Update()
        {
            if (_firstFrame)
            {
                _firstFrame = false;
                _sampler.Reset();
                _log.LogInfo("每帧逻辑已接入，开始上报游戏数值");
            }
            try
            {
                if (Input.GetKeyDown(_cfg.OverlayKey.Value)) _overlay.Visible = !_overlay.Visible;
                if (!_cfg.Enabled.Value) return;
                _sampler.Tick();
                _values.Clear();
                _sampler.Collect(_values);
                if (_values.Count > 0) _client.UpdateValues(_values);
            }
            catch (Exception exc)
            {
                _log.LogError($"每帧逻辑异常: {exc.GetType().Name}: {exc.Message}");
            }
        }

        private void OnGUI()
        {
            try
            {
                _overlay.Draw();
            }
            catch (Exception exc)
            {
                enabled = false;   // OnGUI 每帧触发，关掉组件防止刷屏
                _log.LogError($"面板绘制异常: {exc.GetType().Name}: {exc.Message}");
            }
        }
    }
}
