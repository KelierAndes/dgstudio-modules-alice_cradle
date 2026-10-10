
META = {
    "id": "alice_cradle",
    "name": "Alice in Cradle 联动",
    "version": "0.7.2",
    "description": "游戏侧 MOD 上报 HP/MP 等命名数值，本模块把它们与设备读数"
                   "登记成变量供「事件流」取用，并把设备读数按同名字段回传游戏"
                   "（HTTP + JSON）；本模块不下发设备动作。",
    "settings_key": "alice_cradle",
    "actions": [],
    "mods": {"dest": "BepInEx/plugins/AliceInCradleLink",
             "marker": "AliceInCradle.exe"},
    "params": {
        "HP": {"label": "当前生命", "desc": "玩家当前 HP", "dir": "in",
               "type": "Float"},
        "HPmax": {"label": "生命上限", "desc": "玩家 HP 上限", "dir": "in",
                  "type": "Float"},
        "MP": {"label": "当前魔力", "desc": "玩家当前 MP", "dir": "in",
               "type": "Float"},
        "MPmax": {"label": "魔力上限", "desc": "玩家 MP 上限", "dir": "in",
                  "type": "Float"},
        "EP": {"label": "兴奋度", "desc": "累计兴奋度", "dir": "in",
               "type": "Float"},
        "Hurt": {"label": "本期掉血", "desc": "伤害事件实际造成的 HP 减少量（游戏事件钩子，含血量清零后的过量伤害）",
                 "dir": "in", "type": "Float"},
        "Heal": {"label": "本期回血", "desc": "回血事件实际回复的 HP 增加量（游戏事件钩子，钳满时无虚增脉冲）",
                 "dir": "in", "type": "Float"},
        "MpLost": {"label": "本期耗蓝", "desc": "耗蓝事件实际消耗的 MP（游戏事件钩子；魔力槽空时的施放按请求消耗量上报）",
                   "dir": "in", "type": "Float"},
        "MpGain": {"label": "本期回蓝", "desc": "回蓝事件实际回复的 MP（游戏事件钩子）",
                   "dir": "in", "type": "Float"},
        "Orgasm": {"label": "高潮次数", "desc": "本局累计高潮次数", "dir": "in",
                   "type": "Int"},
        "Orgasming": {"label": "高潮中", "desc": "高潮效果持续期间为 1",
                      "dir": "in", "type": "Bool"},
    },
    "reads": {
        "StrengthA": {"label": "设备通道 A 强度", "name": "StrengthA",
                      "dir": "in", "type": "Int"},
        "StrengthB": {"label": "设备通道 B 强度", "name": "StrengthB",
                      "dir": "in", "type": "Int"},
        "Battery": {"label": "设备电量", "name": "Battery", "dir": "in",
                    "type": "Int"},
        "Connected": {"label": "连接状态", "name": "Connected", "dir": "in",
                      "type": "Bool"},
        "Pressure": {"label": "气压 (kPa)", "name": "Pressure", "dir": "in",
                     "type": "Float"},
    },
    "config": {
        "host": {
            "label": "服务地址", "type": "str", "default": "127.0.0.1",
            "group": "bridge", "desc": "数据服务监听地址（MOD 直连）",
        },
        "port": {
            "label": "服务端口", "type": "int", "default": 8920,
            "min": 1, "max": 65535, "group": "bridge",
            "desc": "Unity MOD 收发的 HTTP 端口",
        },
        "rate": {
            "label": "设备读数采样间隔", "type": "float", "default": 0.2,
            "min": 0.05, "max": 5.0, "step": 0.05, "unit": "s",
            "group": "bridge", "desc": "设备读数变量多久刷新一次",
        },
    },
}

from plugins import ModuleBase, spec_defaults

from modules.alice_cradle.server import GameDataServer

_CONFIG_DEFAULTS = spec_defaults(META["config"])

# 映射表时代留下的设置项：换算与设备派发已全部迁到「事件流」画布
_LEGACY_KEYS = ("mappings", "outputs", "output_map")


class AliceCradleModule(ModuleBase):
    id = META["id"]
    name = META["name"]
    version = META["version"]
    description = META["description"]
    settings_key = META["settings_key"]

    def __init__(self):
        self.server: GameDataServer | None = None
        self.ctx = None

    def config_spec(self) -> dict:
        return META["config"]

    def link_params(self) -> list[dict]:
        """游戏上报的通道：模块产出的读数，事件流只读。"""
        return [{"name": name, "label": str(item.get("label") or name),
                 "dir": str(item.get("dir") or "in"),
                 "type": str(item.get("type") or "Float"),
                 "desc": str(item.get("desc") or "")}
                for name, item in META["params"].items()]

    def on_load(self, ctx) -> None:
        self.ctx = ctx
        drop_legacy_family(ctx.settings)
        drop_mapping_tables(ctx.settings, ctx.log)

    def on_unload(self) -> None:
        if self.server is not None:
            self.server.close()
        self.server = None
        self.ctx = None

    async def start(self) -> None:
        if self.server is not None and self.server.is_running():
            return
        await self.stop()
        cfg = {k: self.ctx.settings.get(k, default)
               for k, default in _CONFIG_DEFAULTS.items()}
        self.server = GameDataServer(self.ctx, cfg, META["reads"])
        try:
            await self.server.start()
        except OSError as exc:
            self.server = None
            self.ctx.log(f"Alice 数据服务启动失败（端口 {cfg['port']} 可能被占用）: {exc!r}")
            raise

    async def reload_config(self) -> None:
        if self.server is None:
            return
        for key in ("rate",):
            self.server.config[key] = self.ctx.settings.get(
                key, _CONFIG_DEFAULTS[key])

    async def stop(self) -> None:
        if self.server is not None:
            await self.server.stop()
            self.server = None

    def is_running(self) -> bool:
        return self.server is not None and self.server.is_running()


def drop_mapping_tables(settings: dict, log=None) -> bool:
    """清除映射表时代的设置项：换算与派发都改在事件流里用写入卡片表达。"""
    stale = [key for key in _LEGACY_KEYS if key in settings]
    if not stale:
        return False
    for key in stale:
        settings.pop(key, None)
    if log is not None:
        log("映射表时代的设置项（" + "、".join(stale) +
            "）已清除：模块只登记变量，换算与设备动作请在「事件流」里"
            "用写入卡片驱动")
    return True


def drop_legacy_family(settings: dict) -> bool:
    if "family" not in settings:
        return False
    settings.pop("family", None)
    if hasattr(settings, "save"):
        settings.save()
    return True
