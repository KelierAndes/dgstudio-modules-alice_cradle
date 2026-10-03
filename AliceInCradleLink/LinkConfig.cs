using BepInEx.Configuration;
using UnityEngine;

namespace AliceInCradleLink
{
    /// <summary>BepInEx 配置项集中声明（写入 BepInEx/config/dev.dgstudio.aliceinradle.link.cfg）。
    /// 纯数据发送端：只保留通信、信号口径与状态面板配置，强度换算全部交给 DGStudio 映射表。</summary>
    public sealed class LinkConfig
    {
        // --- 通信 ---
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> BaseUrl;
        public ConfigEntry<float> ZeroDelaySeconds;
        public ConfigEntry<float> PollSeconds;

        // --- 信号口径 ---
        public ConfigEntry<int> MaxChange;
        public ConfigEntry<int> OrgasmHoldMs;

        // --- 状态面板 ---
        public ConfigEntry<KeyCode> OverlayKey;
        public ConfigEntry<bool> OverlayVisible;
        public ConfigEntry<float> OverlayX;
        public ConfigEntry<float> OverlayY;

        public LinkConfig(ConfigFile config)
        {
            Enabled = config.Bind("0. 总开关", "启用联动", true,
                "关闭后模组不再读取游戏状态，也不再与 DGStudio 通信。");
            BaseUrl = config.Bind("0. 总开关", "DGStudio 地址", "http://127.0.0.1:8920",
                "DGStudio「Alice in Cradle 联动」模块的数据服务地址，默认端口 8920。");
            ZeroDelaySeconds = config.Bind("0. 总开关", "脉冲回零延迟 (秒)", 0.2f,
                "差分信号（受伤/回血等）发出后，经过该时长补发一次 0 回到基线。" +
                "上报由数据变动即时触发，不再按周期轮询。");
            PollSeconds = config.Bind("0. 总开关", "回传轮询间隔 (秒)", 0.5f,
                "GET /data 拉取设备回传字段的间隔。");

            MaxChange = config.Bind("0. 总开关", "单次变化生效上限", 200,
                "差分信号超过此值视为数据异常（读档/换场），不产生信号。");
            OrgasmHoldMs = config.Bind("0. 总开关", "高潮状态保持 (毫秒)", 2000,
                "Orgasming 信号在一次高潮后保持为 1 的时长。");

            OverlayKey = config.Bind("1. 状态面板", "显示/隐藏热键", KeyCode.F9,
                "切换 DGStudio 状态面板。");
            OverlayVisible = config.Bind("1. 状态面板", "默认显示", true,
                "启动时是否显示面板。");
            OverlayX = config.Bind("1. 状态面板", "面板 X", 24f, "面板左上角横坐标。");
            OverlayY = config.Bind("1. 状态面板", "面板 Y", 96f, "面板左上角纵坐标。");
        }
    }
}
