using BepInEx.Configuration;
using UnityEngine;

namespace AliceInCradleLink
{
    public sealed class LinkConfig
    {
        public ConfigEntry<bool> Enabled;
        public ConfigEntry<string> BaseUrl;
        public ConfigEntry<float> PulseSeconds;
        public ConfigEntry<float> PollSeconds;

        public ConfigEntry<int> MaxChange;
        public ConfigEntry<int> OrgasmHoldMs;

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
            PulseSeconds = config.Bind("0. 总开关", "每单次伤害脉冲时间 (秒)", 0.5f,
                "伤害/回血/耗蓝/回蓝脉冲的存续时长：首个事件立即输出并开启窗口；" +
                "距上次输出小于该时长的新事件并入同一脉冲（只累计、不拆分输出），" +
                "窗口结束时一次性输出合并累计总量并归零。设小则逐事件拆分，" +
                "设大则持续伤害合并为持续脉冲。");
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
