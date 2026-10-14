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
        private const float ChipHeight = 28f;
        private const float ChipMinWidth = 120f;
        private const float GripHeight = 34f;
        private const float RowHeight = 22f;

        public bool Visible;

        private readonly LinkConfig _cfg;
        private readonly DataClient _client;
        private readonly VitalSampler _sampler;

        private Texture2D _pixel;
        private GUIStyle _titleStyle;
        private GUIStyle _bodyStyle;
        private GUIStyle _hotStyle;
        private bool _stylesReady;

        private bool _folded;
        private bool _dragging;
        private Vector2 _dragGrab;
        private bool _dragMoved;

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

            // 中文字形比拉丁字形高出行的 ascent，按矩形裁切会削掉笔画上下沿
            _titleStyle = Style(15, FontStyle.Bold, TextAnchor.UpperLeft);
            _bodyStyle = Style(14, FontStyle.Normal, TextAnchor.UpperLeft);
            _hotStyle = Style(14, FontStyle.Bold, TextAnchor.MiddleRight);
            if (font != null)
            {
                _titleStyle.font = font;
                _bodyStyle.font = font;
                _hotStyle.font = font;
            }
        }

        private static GUIStyle Style(int size, FontStyle style, TextAnchor align)
        {
            return new GUIStyle(GUI.skin.label)
            {
                fontSize = size,
                fontStyle = style,
                alignment = align,
                wordWrap = false,
                clipping = TextClipping.Overflow
            };
        }

        public void Draw()
        {
            if (!Visible) return;
            EnsureStyles();

            var link = _client.Snapshot();
            var origin = DragAndFold(new Vector2(_cfg.OverlayX.Value, _cfg.OverlayY.Value), link);
            if (_folded) DrawChip(origin, link);
            else DrawPanel(origin, link);
        }

        private void DrawPanel(Vector2 origin, RemoteData link)
        {
            var title = new Color(0.86f, 0.79f, 0.55f);
            var text = new Color(0.92f, 0.92f, 0.92f);
            var dim = new Color(0.62f, 0.62f, 0.62f);

            Panel(new Rect(origin.x, origin.y, Width, Height), new Color(0.04f, 0.04f, 0.05f, 0.78f));
            Panel(new Rect(origin.x, origin.y, 3f, Height),
                  link.online ? new Color(0.35f, 0.78f, 0.55f) : new Color(0.82f, 0.34f, 0.32f));

            var x = origin.x + 14f;
            var y = origin.y + 10f;

            _titleStyle.normal.textColor = title;
            GUI.Label(new Rect(x, y, Width - 84, RowHeight + 2), "DGStudio 联动", _titleStyle);
            _hotStyle.normal.textColor = title;
            GUI.Label(FoldSpot(origin), "折叠", _hotStyle);
            y += 26f;

            _bodyStyle.normal.textColor = link.online ? text : dim;
            GUI.Label(new Rect(x, y, Width - 24, RowHeight),
                      link.online ? "数据服务在线 · 回传 " + link.fields.Count + " 个字段"
                                  : "DGStudio 未启动", _bodyStyle);
            y += 26f;

            _bodyStyle.normal.textColor = text;
            var shown = 0;
            var keys = new List<string>(link.fields.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            foreach (var key in keys)
            {
                if (shown >= 6) break;
                GUI.Label(new Rect(x + (shown % 3) * 118f, y + (shown / 3) * RowHeight, 116f, RowHeight),
                          key + " " + link.fields[key], _bodyStyle);
                shown++;
            }
            if (shown == 0)
            {
                _bodyStyle.normal.textColor = dim;
                GUI.Label(new Rect(x, y, Width - 24, RowHeight),
                          "等待回传字段（在事件流里接好写入卡片）", _bodyStyle);
            }
            y += 52f;

            _bodyStyle.normal.textColor = text;
            GUI.Label(new Rect(x, y, Width - 24, RowHeight),
                      "HP " + _sampler.Hp + "/" + _sampler.HpMax +
                      "    MP " + _sampler.Mp + "/" + _sampler.MpMax +
                      "    EP " + _sampler.Ep +
                      "    高潮 " + _sampler.OrgasmCount, _bodyStyle);
            y += RowHeight + 2f;

            _bodyStyle.normal.textColor = dim;
            GUI.Label(new Rect(x, y, Width - 24, RowHeight),
                      "上报 " + _client.PostedCount + " 次 · 错误 " + _client.ErrorCount +
                      " · 最近 " + Safe(_sampler.LastSignal), _bodyStyle);
            if (!string.IsNullOrEmpty(link.error))
            {
                y += RowHeight + 2f;
                _bodyStyle.normal.textColor = new Color(0.9f, 0.55f, 0.5f);
                GUI.Label(new Rect(x, y, Width - 24, RowHeight), "错误 " + link.error, _bodyStyle);
            }
        }

        private void DrawChip(Vector2 origin, RemoteData link)
        {
            var rect = ChipRect(origin, link);
            Panel(rect, new Color(0.04f, 0.04f, 0.05f, 0.78f));
            Panel(new Rect(rect.x, rect.y, 3f, rect.height),
                  link.online ? new Color(0.35f, 0.78f, 0.55f) : new Color(0.82f, 0.34f, 0.32f));

            _bodyStyle.normal.textColor = link.online
                ? new Color(0.92f, 0.92f, 0.92f) : new Color(0.62f, 0.62f, 0.62f);
            GUI.Label(new Rect(rect.x + 12f, rect.y + 4f, rect.width - 16f, RowHeight),
                      ChipLabel(link), _bodyStyle);
        }

        private Vector2 DragAndFold(Vector2 origin, RemoteData link)
        {
            var e = Event.current;
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if (GrabRect(origin, link).Contains(e.mousePosition))
                {
                    _dragging = true;
                    _dragGrab = e.mousePosition - origin;
                    _dragMoved = false;
                    e.Use();
                }
            }
            else if (e.type == EventType.MouseDrag && e.button == 0 && _dragging)
            {
                origin = Pin(e.mousePosition - _dragGrab, link);
                WriteOrigin(origin);
                _dragMoved = true;
                e.Use();
            }
            else if (e.type == EventType.MouseUp && e.button == 0 && _dragging)
            {
                _dragging = false;
                if (_dragMoved) _cfg.Save();
                else if (ClickSpot(origin, link).Contains(e.mousePosition))
                {
                    _folded = !_folded;
                    origin = Pin(origin, link);
                    WriteOrigin(origin);
                }
                e.Use();
            }
            return origin;
        }

        private Rect GrabRect(Vector2 origin, RemoteData link)
        {
            return _folded ? ChipRect(origin, link)
                           : new Rect(origin.x, origin.y, Width, GripHeight);
        }

        private Rect ClickSpot(Vector2 origin, RemoteData link)
        {
            return _folded ? ChipRect(origin, link) : FoldSpot(origin);
        }

        private Rect ChipRect(Vector2 origin, RemoteData link)
        {
            var label = ChipLabel(link);
            var w = _bodyStyle.CalcSize(new GUIContent(label)).x + 26f;
            return new Rect(origin.x, origin.y, Math.Max(ChipMinWidth, w), ChipHeight);
        }

        private static string ChipLabel(RemoteData link)
        {
            return "已折叠 · " + (link.online ? "DGStudio 在线" : "DGStudio 离线");
        }

        private static Rect FoldSpot(Vector2 origin)
        {
            return new Rect(origin.x + Width - 70f, origin.y + 10f, 56f, RowHeight);
        }

        private Vector2 Pin(Vector2 pos, RemoteData link)
        {
            var chip = ChipRect(pos, link);
            var w = _folded ? chip.width : Width;
            var h = _folded ? chip.height : Height;
            pos.x = Mathf.Clamp(pos.x, 0f, Mathf.Max(0f, Screen.width - w));
            pos.y = Mathf.Clamp(pos.y, 0f, Mathf.Max(0f, Screen.height - h));
            return pos;
        }

        private void WriteOrigin(Vector2 pos)
        {
            _cfg.OverlayX.Value = pos.x;
            _cfg.OverlayY.Value = pos.y;
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
