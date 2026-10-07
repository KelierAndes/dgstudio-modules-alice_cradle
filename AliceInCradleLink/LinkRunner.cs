using System;
using System.Collections.Generic;
using BepInEx.Logging;
using UnityEngine;

namespace AliceInCradleLink
{
    public class LinkRunner : MonoBehaviour
    {
        private LinkConfig _cfg;
        private VitalSampler _sampler;
        private DataClient _client;
        private OverlayUi _overlay;
        private ManualLogSource _log;
        private readonly Dictionary<string, float> _values = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _pulses = new Dictionary<string, float>();
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
                _pulses.Clear();
                _sampler.Collect(_values);
                _sampler.DrainPulses(_pulses);
                if (_values.Count > 0) _client.UpdateValues(_values);
                if (_pulses.Count > 0) _client.UpdatePulses(_pulses);
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
                enabled = false;
                _log.LogError($"面板绘制异常: {exc.GetType().Name}: {exc.Message}");
            }
        }
    }
}
