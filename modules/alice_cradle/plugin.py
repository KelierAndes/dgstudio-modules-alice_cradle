"""Alice in Cradle 联动模块：游戏数据接收 + 核心参数映射（纯新协议）。

Unity MOD 只作为数据发送端，把游戏内命名数值（HP、MP、Hurt 等，本模块
``META["params"]`` 声明的参数集）经 ``POST /data`` 上报。映射表以核心定义的
参数为锚点：

* ``mappings`` 行 ``{param: 核心输入参数, expr: 表达式}``（如
  ``郊狼通道 A 强度 ← {HP}/{HPmax}*200``）驱动设备；
* ``outputs`` 行 ``{param: 核心输出参数, name: 游戏侧字段名, expr: 表达式}``
  决定 ``GET /data`` 回传给游戏显示的内容，字段名可由用户重命名。

核心参数名固定不可改；表达式可自由混合本模块参数与核心输出参数，
结果取整钳制。META["config"] 声明全部配置项，宿主自动装载
config/alice_cradle.json，联动页据此渲染 输出表 / 输入表 / 模块设置。
"""

META = {
    "id": "alice_cradle",
    "name": "Alice in Cradle 联动",
    "version": "0.6.4",
    "description": "游戏侧 MOD 仅上报 HP/MP 等命名数值，本模块按核心参数映射表"
                   "求值驱动设备，并把设备状态表达式回传游戏（HTTP + JSON）。",
    "settings_key": "alice_cradle",
    "actions": [],
    # 携带的游戏端模组：mods/ 内的已编译文件一键释放到游戏目录
    # （dest 相对游戏根目录；marker 为游戏主程序，用于自动扫描定位）
    "mods": {"dest": "BepInEx/plugins/AliceInCradleLink",
             "marker": "AliceInCradle.exe"},
    # 模块自定义参数：MOD 上报的命名数值，可在输入表达式中以 {名称} 引用
    "params": {
        "HP": {"label": "当前生命", "desc": "玩家当前 HP"},
        "HPmax": {"label": "生命上限", "desc": "玩家 HP 上限"},
        "MP": {"label": "当前魔力", "desc": "玩家当前 MP"},
        "MPmax": {"label": "魔力上限", "desc": "玩家 MP 上限"},
        "EP": {"label": "兴奋度", "desc": "累计兴奋度"},
        "Hurt": {"label": "本期掉血", "desc": "伤害事件实际造成的 HP 减少量（游戏事件钩子，含血量清零后的过量伤害）"},
        "Heal": {"label": "本期回血", "desc": "回血事件实际回复的 HP 增加量（游戏事件钩子，钳满时无虚增脉冲）"},
        "MpLost": {"label": "本期耗蓝", "desc": "耗蓝事件实际消耗的 MP（游戏事件钩子；魔力槽空时的施放按请求消耗量上报）"},
        "MpGain": {"label": "本期回蓝", "desc": "回蓝事件实际回复的 MP（游戏事件钩子）"},
        "Orgasm": {"label": "高潮次数", "desc": "本局累计高潮次数"},
        "Orgasming": {"label": "高潮中", "desc": "高潮效果持续期间为 1"},
    },
    # 可读参数：核心输出信号 → GET /data 回传给游戏的默认字段（可重命名）
    "reads": {
        "StrengthA": {"label": "设备通道 A 强度", "name": "StrengthA"},
        "StrengthB": {"label": "设备通道 B 强度", "name": "StrengthB"},
        "Battery": {"label": "设备电量", "name": "Battery"},
        "Connected": {"label": "连接状态", "name": "Connected"},
        "Pressure": {"label": "气压 (kPa)", "name": "Pressure"},
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
            "label": "状态重算间隔", "type": "float", "default": 0.2,
            "min": 0.05, "max": 5.0, "step": 0.05, "unit": "s",
            "group": "bridge", "desc": "设备状态变量参与运算时的重算节流",
        },
        "mappings": {
            "label": "输入映射表", "type": "list", "default": [],
            "group": "map", "rows": "in",
            "desc": "行 {param: 核心输入参数, expr: 表达式}，变量 {名称} 可混合"
                    "本模块参数与核心输出参数，结果取整钳制后派发",
        },
        "outputs": {
            "label": "输出映射表", "type": "list", "default": [],
            "group": "map", "rows": "out",
            "desc": "行 {param: 核心输出参数, name: 回传给游戏的字段名, "
                    "expr: 表达式}，字段名可自由更改",
        },
    },
}

from plugins import ModuleBase, spec_defaults

from modules.alice_cradle.server import GameDataServer

_CONFIG_DEFAULTS = spec_defaults(META["config"])


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

    def link_params(self) -> list[tuple[str, str]]:
        """模块可写参数表（MOD 上报的命名数值，输入表达式变量池）。"""
        return [(name, str(item.get("label") or name))
                for name, item in META["params"].items()]

    def read_params(self) -> list[tuple[str, str]]:
        """模块可读参数表（GET /data 回传字段的默认信号集）。"""
        return [(signal, str(item.get("label") or signal))
                for signal, item in META["reads"].items()]

    def on_load(self, ctx) -> None:
        self.ctx = ctx
        migrate_legacy(ctx.settings)
        drop_legacy_family(ctx.settings)
        materialize_reads(ctx.settings)

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
        self.server = GameDataServer(self.ctx, cfg)
        try:
            await self.server.start()
        except OSError as exc:
            self.server = None
            self.ctx.log(f"Alice 数据服务启动失败（端口 {cfg['port']} 可能被占用）: {exc!r}")
            raise

    async def reload_config(self) -> None:
        """设置变更后把两张映射表与节流重新装载进运行中的服务。"""
        if self.server is None:
            return
        for key in ("rate", "mappings", "outputs"):
            self.server.config[key] = self.ctx.settings.get(
                key, _CONFIG_DEFAULTS[key])
        self.server.apply_config()

    async def stop(self) -> None:
        if self.server is not None:
            await self.server.stop()
            self.server = None

    def is_running(self) -> bool:
        return self.server is not None and self.server.is_running()


def migrate_legacy(settings: dict) -> bool:
    """旧版 ``output_map``（信号键 → 字段名）迁到 ``outputs`` 行表。"""
    old = settings.get("output_map")
    if not isinstance(old, dict) or not old:
        return False
    rows = list(settings.get("outputs") or [])
    known = {str(row.get("param") or "") for row in rows if isinstance(row, dict)}
    from dglab.params import output_spec

    for key, name in old.items():
        if key in known:
            continue
        spec = output_spec(str(key)) or {}
        rows.append({"param": str(key),
                     "name": str(name or "").strip() or str(key),
                     "expr": "{" + str(key) + "}",
                     "type": str(spec.get("type") or "Int")})
    settings["outputs"] = rows
    settings.pop("output_map", None)
    if hasattr(settings, "save"):
        settings.save()
    return True


def drop_legacy_family(settings: dict) -> bool:
    """清理已移除的「核心输出取值家族」配置项（别名现按全家族解析）。"""
    if "family" not in settings:
        return False
    settings.pop("family", None)
    if hasattr(settings, "save"):
        settings.save()
    return True


def materialize_reads(settings: dict) -> bool:
    """空输出表按 META["reads"] 落地默认可读字段行（信号跨家族解析，
    郊狼 → 负鼠 → 灵猫取第一个有该信号的家族，接入任意设备均可联动）。"""
    if any(isinstance(row, dict) and str(row.get("name") or "").strip()
           for row in (settings.get("outputs") or [])):
        return False
    rows = []
    for signal, item in META["reads"].items():
        spec = _signal_spec(signal)
        if spec is None:
            continue
        rows.append({"param": spec["key"],
                     "name": str(item.get("name") or signal),
                     "expr": "{" + spec["key"] + "}",
                     "type": str(spec.get("type") or "Int")})
    if not rows:
        return False
    settings["outputs"] = rows
    if hasattr(settings, "save"):
        settings.save()
    return True


def _signal_spec(signal: str) -> dict | None:
    from dglab.params import output_specs

    for family in ("COYOTE", "OVC", "BMTR"):
        for spec in output_specs(family, 1):
            if spec["signal"] == str(signal):
                return spec
    return None
