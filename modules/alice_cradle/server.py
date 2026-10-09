
from __future__ import annotations

import asyncio
import json
import time
from typing import Any

from dglab.mapping import as_number
from dglab.params import core_alias_values, device_state_values, output_specs

MAX_BODY = 64 * 1024

DEFAULTS = {
    "host": "127.0.0.1",
    "port": 8920,
    "rate": 0.2,
}

_BOOL_EPS = 1e-9


def _num(value, default):
    try:
        out = float(value)
    except (TypeError, ValueError):
        return default
    return out


def _typed(value, value_type: str):
    kind = str(value_type or "Int").upper()
    if kind == "BOOL":
        return bool(value > _BOOL_EPS)
    if kind == "FLOAT":
        return round(float(value), 3)
    return int(round(float(value)))


def signal_spec(signal: str) -> dict | None:
    """裸信号名（StrengthA/Battery/Pressure…）→ 核心输出参数规格。

    只查 1 号设备：回传给游戏的是「当前这台设备」的读数，与设备家族无关的
    名字（如 Battery）由核心键 ``COYOTE.Battery`` 落地。
    """
    for family in ("COYOTE", "OVC", "BMTR"):
        for spec in output_specs(family, 1):
            if spec["signal"] == str(signal):
                return spec
    return None


def read_rows(reads: dict[str, Any] | None) -> list[dict[str, Any]]:
    """META["reads"] → 回传字段行（字段名 / 核心键 / 值类型），查不到的丢弃。"""
    rows: list[dict[str, Any]] = []
    for signal, item in (reads or {}).items():
        spec = signal_spec(str(signal))
        if spec is None:
            continue
        item = item if isinstance(item, dict) else {}
        rows.append({"name": str(item.get("name") or signal),
                     "key": str(spec["key"]),
                     "signal": str(signal),
                     "type": str(item.get("type") or spec.get("type") or "Int")})
    return rows


class _SignalBoard:
    """模块唯一的对外面：只登记变量，不派发设备。

    宿主按 ``runtime.engine`` 找实时值（``plugins._mapping_engine`` 的兼容挂表、
    ``flow_host.module_signals`` 的事件流读数、``ui/live.py`` 的概览、
    ``ui/modules_page._var_pool`` 的变量下拉），需要的成员是 ``signals``、
    ``errors`` 与 ``pump()``；``values()`` 给变量下拉提供全部可引用名；
    ``temps`` 是给宿主 ``attach_temps`` 挂共享变量表用的，本模块不往里写。
    设备直写（set_strength/set_wave/zap/fire_*）在宿主侧已被拦下，模块的映射表
    派发层随之整体移除——设备动作改由事件流的写入卡片驱动。
    """

    def __init__(self, device_vars=None):
        self._device_vars = device_vars or (lambda: {})
        self.signals: dict[str, float] = {}
        self.errors: dict[str, str] = {}
        self.temps: dict[str, float] = {}
        self.last_values: dict[str, float] = {}
        # 兼容宿主概览/仪表盘的映射时代读数：本模块恒空
        self.mappings: dict[str, str] = {}
        self.outputs: list[dict[str, Any]] = []
        self.out_values: dict[str, Any] = {}
        self.out_errors: dict[str, str] = {}

    def signal(self, name: str, value: Any) -> None:
        num = as_number(value)
        if num is None:
            return
        self.signals[str(name)] = num

    def values(self) -> dict[str, float]:
        """取值口径：设备读数 < 共享变量 < 本模块发布的读数（与旧映射引擎一致）。"""
        merged = self._device_vars()
        for key, value in self.temps.items():
            merged.setdefault(str(key), value)
        merged.update(self.signals)
        return merged

    def attach_temps(self, shared: dict[str, float]) -> None:
        if shared is not self.temps:
            shared.update(self.temps)
            self.temps = shared

    def pump(self) -> None:
        return None

    def reset(self) -> None:
        self.signals.clear()
        self.errors.clear()
        self.last_values.clear()


class GameDataServer:

    def __init__(self, ctx, config: dict | None = None,
                 reads: dict[str, Any] | None = None):
        self.ctx = ctx
        self.config = dict(DEFAULTS)
        self.config.update({k: v for k, v in (config or {}).items()
                            if k in self.config})
        self.read_rows = read_rows(reads)
        self.engine = _SignalBoard()
        self._server: asyncio.Server | None = None
        self._running = False
        self._recv: dict[str, float] = {}
        self._last_rx: float | None = None
        self._tasks: set[asyncio.Task] = set()
        self._pump_task: asyncio.Task | None = None

    async def start(self) -> None:
        self._server = await asyncio.start_server(
            self._handle, str(self.config.get("host") or "127.0.0.1"),
            int(self.config.get("port") or 8920))
        self._running = True
        self._sample_reads()
        self._pump_task = asyncio.ensure_future(self._pump_loop())
        self.ctx.log(f"Alice 数据服务已启动 http://{self.config['host']}"
                     f":{self.config['port']}")

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
        interval = max(0.05, _num(self.config.get("rate"), 0.2))
        try:
            while self._running:
                await asyncio.sleep(interval)
                self._sample_reads()
                self.engine.pump()
        except asyncio.CancelledError:
            pass

    def device_vars(self) -> dict[str, float]:
        try:
            state = self.ctx.get_state()
        except Exception:
            return {}
        vals = device_state_values(state)
        vals.update(core_alias_values(vals))
        return vals

    def _sample_reads(self) -> None:
        """设备读数 → 同名变量：只读不写，事件流与游戏面板都取这一份。"""
        vals = self.device_vars()
        for row in self.read_rows:
            self.engine.signal(row["name"],
                               _typed(vals.get(row["key"], 0.0), row["type"]))

    def output_values(self) -> dict[str, Any]:
        vals = self.device_vars()
        return {row["name"]: _typed(vals.get(row["key"], 0.0), row["type"])
                for row in self.read_rows}

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
            "reads": [dict(row) for row in self.read_rows],
            "output_values": dict(self.out_snapshot()),
            "errors": dict(self.engine.errors),
            "signals": dict(self._recv),
            "last_report_age": age,
        }

    def out_snapshot(self) -> dict[str, Any]:
        return self.output_values()


def _clamp_int(value, lo, hi, default) -> int:
    try:
        n = int(value)
    except (TypeError, ValueError):
        return default
    return max(lo, min(hi, n))
