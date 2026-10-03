"""Alice in Cradle 数据联动服务（纯新协议，替代 Game Hub 兼容层）。

Unity MOD 作为纯数据发送端，按数据变动即时上报游戏内命名数值
（HP、MP、Hurt 等，见插件 ``META["params"]`` 声明；无变动不通信，
本服务只被动接收解析，不做按时间的刷新逻辑）：

* ``POST /data``  body 为 JSON 对象 ``{"HP": 60, "MP": 30, ...}``，
  每个字段名进入共享 :class:`dglab.mapping.MappingEngine` 信号空间；
* 模块配置 ``mappings``（行 ``{param: 核心输入参数 id, expr: 表达式}``）把
  模块参数与核心输出参数经四则运算组合后驱动设备，表达式留空即同名直传；
* ``GET /data``   返回 ``outputs``（行 ``{param, name, expr, type}``）求值后的
  ``{模块侧参数名: 值}``；未配置输出行时回退为核心输出参数原样值；
* ``GET /status`` 返回模块与引擎摘要，便于调试。

协议与 DG-Lab Game Hub 完全无关；游戏侧仅需 HTTP + JSON。
"""

from __future__ import annotations

import asyncio
import json
import time
from typing import Any

from dglab.mapping import MappingEngine, signal_specs
from dglab.params import (build_dispatchers, core_alias_values, core_inputs,
                          device_state_values)

MAX_BODY = 64 * 1024

DEFAULTS = {
    "host": "127.0.0.1",
    "port": 8920,
    "rate": 2.0,          # 设备状态 → 引擎变量重算节流（秒）
    "mappings": [],       # [{"param": 核心输入参数 id, "expr": 表达式}]
    "outputs": [],        # [{"param": 核心输出参数 id, "name": 游戏侧名,
                          #   "expr": 表达式, "type": "Int"}]
}


def _num(value, default):
    try:
        out = float(value)
    except (TypeError, ValueError):
        return default
    return out


class GameDataServer:
    """asyncio HTTP/1.1 JSON 服务 + 共享映射引擎。"""

    def __init__(self, ctx, config: dict | None = None):
        self.ctx = ctx
        self.config = dict(DEFAULTS)
        self.config.update({k: v for k, v in (config or {}).items()
                            if k in self.config})
        self.engine = MappingEngine(self._dispatch,
                                    device_vars=self.device_vars)
        self.engine.set_ranges(signal_specs())
        self._api = self._DeviceApi(self)
        self.actions = build_dispatchers(self._api, core_inputs())
        self._server: asyncio.Server | None = None
        self._running = False
        self._recv: dict[str, float] = {}
        self._last_rx: float | None = None
        self._tasks: set[asyncio.Task] = set()
        self._pump_task: asyncio.Task | None = None
        self._primed = False

    class _DeviceApi:
        """把宿主 ModuleContext 适配成核心参数派发器需要的接口。"""

        def __init__(self, srv: "GameDataServer"):
            self._srv = srv

        @property
        def _ctx(self):
            return self._srv.ctx

        def resolve_slot(self, family: str = "") -> str | None:
            return self._ctx.resolve_slot(
                family=str(family or "COYOTE").upper(), output_only=True)

        def wave_order(self, family: str = "") -> list[str]:
            return self._ctx.wave_order(str(family or "COYOTE").upper())

        def wave_selection(self) -> dict:
            return self._ctx.wave_selection() or {}

        def set_strength(self, channel, value, slot_id=None):
            return self._ctx.set_strength(channel, value, slot_id=slot_id)

        def set_wave(self, channel, name, slot_id=None):
            return self._ctx.set_wave(channel, name, slot_id=slot_id)

        def zap(self, channel, seconds=1.0, slot_id=None):
            return self._ctx.zap(channel, seconds, slot_id=slot_id)

        def fire_start(self, slot_id=None):
            return self._ctx.fire_start(slot_id=slot_id)

        def fire_stop(self, slot_id=None):
            return self._ctx.fire_stop(slot_id=slot_id)

        def emergency_stop(self):
            return self._ctx.emergency_stop()

        def run(self, coro) -> None:
            self._srv._spawn(coro)

    # ---- 配置便捷属性 --------------------------------------------------

    # ---- 生命周期 -----------------------------------------------------
    async def start(self) -> None:
        self.apply_config()
        self._server = await asyncio.start_server(
            self._handle, str(self.config.get("host") or "127.0.0.1"),
            int(self.config.get("port") or 8920))
        self._running = True
        self._pump_task = asyncio.ensure_future(self._pump_loop())
        self.ctx.log(f"Alice 数据服务已启动 http://{self.config['host']}"
                     f":{self.config['port']}")

    def apply_config(self) -> None:
        """把 mappings / outputs 装载进引擎（首轮静默，不写设备）。"""
        first = not self._primed
        if first:
            self.engine.armed = False
        self.engine.set_mappings(self.config.get("mappings") or [])
        self.engine.set_outputs(self.config.get("outputs") or [])
        if first:
            self.engine.armed = True
            self._primed = True

    async def stop(self) -> None:
        self.close()
        if self._server is None:
            return
        try:
            await asyncio.wait_for(self._server.wait_closed(), 2)
        except Exception:
            pass

    def close(self) -> None:
        self._running = False
        if self._pump_task is not None:
            self._pump_task.cancel()
            self._pump_task = None
        if self._server is not None:
            try:
                self._server.close()
            except Exception:
                pass
        self.engine.reset()

    def is_running(self) -> bool:
        return self._running

    async def _pump_loop(self) -> None:
        """设备状态变量随时间变化：定期重算表达式。"""
        interval = max(0.05, _num(self.config.get("rate"), 2.0) / 10.0)
        try:
            while self._running:
                await asyncio.sleep(interval)
                self.engine.pump()
        except asyncio.CancelledError:
            pass

    def _spawn(self, coro) -> None:
        try:
            task = asyncio.ensure_future(coro)
        except RuntimeError:      # 无运行中的事件循环（测试/停机）
            coro.close()
            return
        self._tasks.add(task)
        task.add_done_callback(self._tasks.discard)

    # ---- 值空间 / 派发 -------------------------------------------------
    def device_vars(self) -> dict[str, float]:
        """核心输出参数实时值（``家族.信号``）+ 全家族短名别名。"""
        try:
            state = self.ctx.get_state()
        except Exception:
            return {}
        vals = device_state_values(state)
        vals.update(core_alias_values(vals))
        return vals

    def _dispatch(self, target: str, value: int) -> None:
        action = self.actions.get(target)
        if action is None:
            return
        try:
            action(value)
        except Exception as exc:
            self.ctx.log(f"Alice 表达式派发 {target}={value} 失败: {exc!r}")

    # ---- 输出（核心 → 游戏） -------------------------------------------
    def output_values(self) -> dict[str, Any]:
        """按输出映射表求值；未配置输出行时回退为核心输出参数原样值。"""
        if self.engine.outputs:
            return dict(self.engine.out_values)
        return {key: _round_num(value) for key, value in self.device_vars().items()}

    # ---- HTTP ---------------------------------------------------------
    async def _handle(self, reader, writer) -> None:
        try:
            request_line = await asyncio.wait_for(reader.readline(), 10)
            if not request_line:
                return
            try:
                method, path, _ = (request_line.decode("latin-1")
                                   .rstrip("\r\n").split(" ", 2))
            except ValueError:
                return
            headers = {}
            while True:
                line = await reader.readline()
                if not line or line in (b"\r\n", b"\n"):
                    break
                key, _, val = line.decode("latin-1").partition(":")
                headers[key.strip().lower()] = val.strip()
            body = b""
            length = _clamp_int(headers.get("content-length"), 0, MAX_BODY, 0)
            if length:
                body = await reader.readexactly(length)
            status, payload = await self._route(method, path.split("?")[0],
                                                body)
            self._respond(writer, status, payload)
        except Exception:
            try:
                self._respond(writer, 400, {"status": 0, "code": "ERR"})
            except Exception:
                pass
        finally:
            try:
                await writer.drain()
                writer.close()
            except Exception:
                pass

    def _respond(self, writer, status: int, payload: dict) -> None:
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        head = (f"HTTP/1.1 {status} OK\r\n"
                f"Content-Type: application/json; charset=utf-8\r\n"
                f"Content-Length: {len(data)}\r\n"
                f"Connection: close\r\n\r\n").encode("latin-1")
        writer.write(head + data)

    async def _route(self, method: str, path: str,
                     body: bytes) -> tuple[int, dict]:
        if path.rstrip("/") in ("/data", ""):
            if method == "POST":
                return 200, self._post_data(body)
            if method == "GET":
                return 200, {"status": 1, "data": self.output_values()}
        if path.rstrip("/") == "/status" and method == "GET":
            return 200, self._status()
        return 404, {"status": 0, "code": "ERR::NOT_FOUND",
                     "message": "未知接口"}

    def _post_data(self, body: bytes) -> dict:
        try:
            payload = json.loads(body.decode("utf-8") or "{}")
        except Exception:
            return {"status": 0, "code": "ERR::BAD_JSON",
                    "message": "请求体需为 JSON 对象"}
        if not isinstance(payload, dict):
            return {"status": 0, "code": "ERR::BAD_JSON",
                    "message": "请求体需为 JSON 对象"}
        accepted: list[str] = []
        for name, value in payload.items():
            if isinstance(value, (bool, int, float)):
                self._recv[str(name)] = _num(value, 0.0)
                accepted.append(str(name))
        if accepted:
            self._last_rx = time.monotonic()
            for name in accepted:
                self.engine.signal(name, self._recv[name])
        return {"status": 1, "code": "OK", "received": accepted}

    def _status(self) -> dict:
        age = (None if self._last_rx is None
               else round(time.monotonic() - self._last_rx, 2))
        return {
            "status": 1, "code": "OK", "app": "DGStudio",
            "mappings": dict(self.engine.mappings),
            "outputs": [dict(row) for row in self.engine.outputs],
            "output_values": dict(self.out_snapshot()),
            "errors": dict(self.engine.errors),
            "output_errors": dict(self.engine.out_errors),
            "last_values": dict(self.engine.last_values),
            "signals": dict(self._recv),
            "last_report_age": age,
        }

    def out_snapshot(self) -> dict[str, Any]:
        return self.output_values()


def _round_num(value: float):
    n = round(float(value), 3)
    return int(n) if n == int(n) else n


def _clamp_int(value, lo, hi, default) -> int:
    try:
        n = int(value)
    except (TypeError, ValueError):
        return default
    return max(lo, min(hi, n))
