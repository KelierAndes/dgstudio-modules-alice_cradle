using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using UnityEngine;

namespace AliceInCradleLink
{
    public sealed class OverlayUi
    {
        private const float Width = 380f;
        private const float Height = 190f;

        public bool Visible;

        private readonly LinkConfig _cfg;
        private readonly DataClient _client;
        private readonly VitalSampler _sampler;

        private Texture2D _pixel;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private bool _stylesReady;

        public OverlayUi(LinkConfig cfg, DataClient client, VitalSampler sampler)
        {
            _cfg = cfg;
            _client = client;
            _sampler = sampler;
            Visible = cfg.OverlayVisible.Value;
        }

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _stylesReady = true;

            _pixel = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            _pixel.SetPixel(0, 0, Color.white);
            _pixel.Apply();

            var font = Font.CreateDynamicFontFromOSFont(
                new[] { "Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "Noto Sans CJK SC" }, 14);
            if (font != null) font.hideFlags = HideFlags.HideAndDontSave;

            _titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _bodyStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = false };
            if (font != null)
            {
                _titleStyle.font = font;
                _bodyStyle.font = font;
            }
        }

        public void Draw()
        {
            if (!Visible) return;
            EnsureStyles();

            var link = _client.Snapshot();
            var origin = new Vector2(_cfg.OverlayX.Value, _cfg.OverlayY.Value);
            var title = new Color(0.86f, 0.79f, 0.55f);
            var text = new Color(0.92f, 0.92f, 0.92f);
            var dim = new Color(0.62f, 0.62f, 0.62f);

            Panel(new Rect(origin.x, origin.y, Width, Height), new Color(0.04f, 0.04f, 0.05f, 0.78f));
            Panel(new Rect(origin.x, origin.y, 3f, Height),
                  link.online ? new Color(0.35f, 0.78f, 0.55f) : new Color(0.82f, 0.34f, 0.32f));

            var x = origin.x + 14f;
            var y = origin.y + 10f;

            _titleStyle.normal.textColor = title;
            GUI.Label(new Rect(x, y, Width - 24, 20), "DGStudio 联动", _titleStyle);
            _bodyStyle.normal.textColor = link.online ? text : dim;
            GUI.Label(new Rect(x, y + 22f, Width - 24, 18),
                      link.online ? "数据服务在线 · 回传 " + link.fields.Count + " 个字段"
                                  : "DGStudio 未启动", _bodyStyle);

            y += 46f;
            _bodyStyle.normal.textColor = text;
            var shown = 0;
            var keys = new List<string>(link.fields.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                if (shown >= 6) break;
                GUI.Label(new Rect(x + (shown % 3) * 118f, y + (shown / 3) * 20f, 116f, 18),
                          key + " " + link.fields[key], _bodyStyle);
                shown++;
            }
            if (shown == 0)
            {
                _bodyStyle.normal.textColor = dim;
                GUI.Label(new Rect(x, y, Width - 24, 18), "等待回传字段（联动页输出映射表）", _bodyStyle);
            }

            y += 44f;
            _bodyStyle.normal.textColor = text;
            GUI.Label(new Rect(x, y, Width - 24, 18),
                      "HP " + _sampler.Hp + "/" + _sampler.HpMax +
                      "    MP " + _sampler.Mp + "/" + _sampler.MpMax +
                      "    EP " + _sampler.Ep +
                      "    高潮 " + _sampler.OrgasmCount, _bodyStyle);
            y += 20f;
            _bodyStyle.normal.textColor = dim;
            GUI.Label(new Rect(x, y, Width - 24, 18),
                      "上报 " + _client.PostedCount + " 次 · 错误 " + _client.ErrorCount +
                      " · 最近 " + Safe(_sampler.LastSignal), _bodyStyle);
            y += 20f;
            if (!string.IsNullOrEmpty(link.error))
            {
                _bodyStyle.normal.textColor = new Color(0.9f, 0.55f, 0.5f);
                GUI.Label(new Rect(x, y, Width - 24, 18), "错误 " + link.error, _bodyStyle);
            }
        }

        private void Panel(Rect rect, Color color)
        {
            var old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _pixel);
            GUI.color = old;
        }

        private static string Safe(string value)
        {
            return string.IsNullOrEmpty(value) ? "—" : value;
        }
    }
}
