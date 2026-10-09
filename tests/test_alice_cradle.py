from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _bootstrap  # noqa: F401  定位 DGStudio 核心仓库

import asyncio
import json
import unittest

from dglab.state import EngineState, Slot
from modules.alice_cradle.server import GameDataServer, read_rows

# 宿主 ModuleContext 已拦下的设备直写方法：模块一旦调用就必须炸出来
BLOCKED_METHODS = ("set_strength", "add_strength", "reset_strength", "set_wave",
                   "push_pulse_stream", "fire", "fire_start", "fire_stop",
                   "zap", "set_intensity_param")


def _free_port() -> int:
    import socket
    s = socket.socket()
    try:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]
    finally:
        s.close()


class FakeCtx:
    """只读侧的宿主桩：提供状态与日志，设备写方法由 GuardedCtx 另行拦截。"""

    def __init__(self, state: EngineState | None = None):
        self.logs: list[str] = []
        self.state = state or EngineState(
            connected=True, paired=True,
            slots={"s1": Slot(slot_id="s1", name="Coyote 03", type="COYOTE",
                              strength={"A": 0, "B": 0}, battery=77)})

    def log(self, msg):
        self.logs.append(msg)

    def resolve_slot(self, slot_id=None, family=None, output_only=False):
        return "s1"

    def get_state(self):
        return self.state

    def wave_order(self, family="COYOTE"):
        return ["静默", "呼吸", "波浪"]

    def wave_selection(self) -> dict:
        return {"A": "呼吸", "B": ""}


class GuardedCtx(FakeCtx):
    """任何设备直写都立刻抛错：模块跑完整周期必须一次都不碰到它。"""

    def __init__(self, state: EngineState | None = None):
        super().__init__(state)
        self.settings: dict = {}
        self.touched: list[str] = []

    def emergency_stop(self):
        self.touched.append("emergency_stop")

    def submit(self, coro):
        coro.close()
        return None


def _block(name: str):
    def _raise(self, *args, **kwargs):
        self.touched.append(name)
        raise AssertionError(f"模块直写设备被调用：{name}()")
    return _raise


for _name in BLOCKED_METHODS:
    setattr(GuardedCtx, _name, _block(_name))


def _server(ctx, config=None) -> GameDataServer:
    from modules.alice_cradle.plugin import META

    return GameDataServer(ctx, config, META["reads"])


class RoutingTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.ctx = FakeCtx()
        self.srv = _server(self.ctx)

    async def post(self, path, payload):
        body = json.dumps(payload).encode()
        return await self.srv._route("POST", path, body)

    async def get(self, path):
        return await self.srv._route("GET", path, b"")

    async def test_post_data_publishes_signals_only(self):
        code, resp = await self.post("/data", {"HP": 60, "HPmax": 100})
        self.assertEqual(code, 200)
        self.assertEqual(sorted(resp["received"]), ["HP", "HPmax"])
        self.assertEqual(self.srv.engine.signals["HP"], 60.0)
        self.assertEqual(self.srv.engine.signals["HPmax"], 100.0)
        self.assertEqual(self.srv.engine.mappings, {})
        self.assertEqual(self.srv.engine.outputs, [])

    async def test_non_numeric_values_ignored(self):
        code, resp = await self.post("/data", {"name": "alice", "HP": 50,
                                               "flag": True})
        self.assertEqual(sorted(resp["received"]), ["HP", "flag"])
        self.assertEqual(self.srv.engine.signals.get("HP"), 50.0)
        self.assertEqual(self.srv.engine.signals.get("flag"), 1.0)

    async def test_bad_json(self):
        code, resp = await self.srv._route("POST", "/data", b"{oops")
        self.assertEqual(code, 200)
        self.assertEqual(resp["status"], 0)

    async def test_get_data_returns_device_reads(self):
        code, resp = await self.get("/data")
        self.assertEqual(code, 200)
        data = resp["data"]
        self.assertEqual(sorted(data), ["Battery", "Connected", "Pressure",
                                         "StrengthA", "StrengthB"])
        self.assertEqual(data["StrengthA"], 0)
        self.assertEqual(data["Battery"], 77)
        self.assertIs(data["Connected"], True)

    async def test_get_data_tracks_device_state(self):
        self.ctx.state.slots["s1"].strength["A"] = 120
        code, resp = await self.get("/data")
        self.assertEqual(resp["data"]["StrengthA"], 120)

    async def test_status(self):
        await self.post("/data", {"HP": 30})
        code, resp = await self.get("/status")
        self.assertEqual(code, 200)
        self.assertEqual(resp["signals"]["HP"], 30.0)
        self.assertEqual(resp["output_values"]["Battery"], 77)
        self.assertNotIn("mappings", resp)
        self.assertNotIn("outputs", resp)

    async def test_unknown_paths(self):
        self.assertEqual((await self.get("/api/game/all"))[0], 404)
        self.assertEqual((await self.get("/healthz"))[0], 404)
        self.assertEqual((await self.post("/other", {}))[0], 404)

    async def test_engine_keeps_host_required_members(self):
        self.assertIsInstance(self.srv.engine.signals, dict)
        self.assertIsInstance(self.srv.engine.errors, dict)
        self.srv.engine.pump()
        self.srv.engine.signal("HP", 12)
        self.assertEqual(self.srv.engine.values()["HP"], 12.0)


class ReadRowTests(unittest.TestCase):
    def test_read_rows_resolve_core_keys(self):
        rows = {row["name"]: row for row in read_rows({
            "StrengthA": {"name": "StrengthA", "type": "Int"},
            "Battery": {"name": "Battery", "type": "Int"},
            "Connected": {"name": "Connected", "type": "Bool"},
            "Pressure": {"name": "Pressure", "type": "Float"},
            "NoSuchSignal": {"name": "NoSuchSignal"}})}
        self.assertEqual(rows["StrengthA"]["key"], "COYOTE.StrengthA")
        self.assertEqual(rows["Battery"]["key"], "COYOTE.Battery")
        self.assertEqual(rows["Pressure"]["key"], "BMTR.Pressure")
        self.assertNotIn("NoSuchSignal", rows)

    def test_sampling_publishes_reads_into_signals(self):
        ctx = FakeCtx()
        ctx.state.slots["s1"].strength = {"A": 88, "B": 12}
        srv = _server(ctx)
        srv._sample_reads()
        self.assertEqual(srv.engine.signals["StrengthA"], 88.0)
        self.assertEqual(srv.engine.signals["StrengthB"], 12.0)
        self.assertEqual(srv.engine.signals["Battery"], 77.0)
        self.assertEqual(srv.engine.signals["Connected"], 1.0)


class LifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_start_stop_over_real_socket(self):
        ctx = FakeCtx()
        srv = _server(ctx, {"port": _free_port(), "host": "127.0.0.1"})
        await srv.start()
        try:
            port = srv._server.sockets[0].getsockname()[1]
            code, resp = await self._request(port, "POST", "/data", {"HP": 42})
            self.assertEqual(code, 200)
            self.assertEqual(resp["received"], ["HP"])
            code, resp = await self._request(port, "GET", "/data")
            self.assertEqual(resp["data"]["Battery"], 77)
        finally:
            await srv.stop()
        self.assertFalse(srv.is_running())

    async def test_sampling_loop_runs_while_started(self):
        ctx = FakeCtx()
        srv = _server(ctx, {"port": _free_port(), "host": "127.0.0.1",
                            "rate": 0.05})
        await srv.start()
        try:
            ctx.state.slots["s1"].strength["A"] = 65
            await asyncio.sleep(0.2)
            self.assertEqual(srv.engine.signals["StrengthA"], 65.0)
        finally:
            await srv.stop()

    async def _request(self, port, method, path, payload=None):
        reader, writer = await asyncio.open_connection("127.0.0.1", port)
        body = b"" if payload is None else json.dumps(payload).encode()
        head = (f"{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\n"
                f"Content-Type: application/json\r\n"
                f"Content-Length: {len(body)}\r\n\r\n").encode("latin-1")
        writer.write(head + body)
        await writer.drain()
        status_line = await asyncio.wait_for(reader.readline(), 5)
        code = int(status_line.decode().split()[1])
        while True:
            line = await asyncio.wait_for(reader.readline(), 5)
            if line in (b"\r\n", b"\n", b""):
                break
        rest = await asyncio.wait_for(reader.read(), 5)
        writer.close()
        await writer.wait_closed()
        idx = rest.find(b"{")
        out = json.loads(rest[idx:].decode("utf-8")) if idx >= 0 else {}
        return code, out


class GuardTests(unittest.IsolatedAsyncioTestCase):
    """模块全程不碰设备：一个会对直写抛错的宿主桩要撑过完整生命周期。"""

    async def test_full_module_cycle_never_touches_device(self):
        from modules.alice_cradle.plugin import AliceCradleModule

        ctx = GuardedCtx()
        ctx.settings["port"] = _free_port()
        ctx.settings["host"] = "127.0.0.1"
        ctx.settings["rate"] = 0.05
        mod = AliceCradleModule()
        mod.on_load(ctx)
        await mod.start()
        try:
            await mod.server._route("POST", "/data",
                                    json.dumps({"HP": 50, "Orgasming": 1})
                                    .encode())
            for _ in range(5):
                ctx.state.slots["s1"].strength["A"] = 30
                mod.server._sample_reads()
                mod.server.engine.pump()
            await mod.reload_config()
            code, resp = await mod.server._route("GET", "/data", b"")
            self.assertEqual(code, 200)
            self.assertEqual(resp["data"]["StrengthA"], 30)
            self.assertEqual(mod.server.engine.signals["HP"], 50.0)
            self.assertEqual(ctx.touched, [])
        finally:
            await mod.stop()
            mod.on_unload()


class PluginMetaTests(unittest.TestCase):
    def test_meta_is_literal_and_class_found(self):
        import _bootstrap
        import plugins

        path = os.path.join(_bootstrap.HERE,
                            "modules", "alice_cradle", "plugin.py")
        meta = plugins._read_meta(path)
        self.assertEqual(meta["id"], "alice_cradle")
        self.assertEqual(meta["settings_key"], "alice_cradle")
        self.assertNotIn("mappings", meta["config"])
        self.assertNotIn("outputs", meta["config"])
        self.assertNotIn("output_map", meta["config"])
        self.assertEqual(set(meta["config"]), {"host", "port", "rate"})
        self.assertIn("HP", meta["params"])
        self.assertIn("StrengthA", meta["reads"])
        cls = plugins._load_plugin_class("alice_cradle", path)
        self.assertEqual(cls.__name__, "AliceCradleModule")
        inst = cls()
        self.assertFalse(inst.is_running())

    def test_declared_rows_carry_direction_and_type(self):
        from modules.alice_cradle.plugin import AliceCradleModule

        from modules.alice_cradle.plugin import META

        inst = AliceCradleModule()
        params = inst.link_params()
        self.assertTrue(all(isinstance(row, dict) for row in params))
        by = {row["name"]: row for row in params}
        self.assertEqual(sorted(by), sorted(META["params"]))
        self.assertEqual({row["dir"] for row in params}, {"in"})
        self.assertEqual(by["HP"]["type"], "Float")
        self.assertEqual(by["Orgasming"]["type"], "Bool")
        # 设备读数只回传给游戏，不再登记进共享变量表（核心读出本来就有）
        self.assertEqual(list(inst.read_params()), [])
        self.assertNotIn("StrengthA", by)
        self.assertTrue(any(row["name"] == "StrengthA"
                            for row in read_rows(META["reads"])))

    def test_host_can_find_module_variables(self):
        """宿主登记变量只走 link_params + runtime.engine.signals。"""
        from modules.alice_cradle.plugin import AliceCradleModule, META

        inst = AliceCradleModule()
        inst.ctx = FakeCtx()
        inst.server = _server(inst.ctx)
        names = {row["name"] for row in inst.link_params()}
        self.assertEqual(names, set(META["params"]))
        runtime = getattr(inst, "bridge", None) or getattr(inst, "server", None)
        self.assertIs(getattr(runtime, "engine", None), inst.server.engine)


class DropMappingTablesTests(unittest.TestCase):
    def test_stale_keys_popped_with_one_chinese_log(self):
        from modules.alice_cradle.plugin import drop_mapping_tables

        settings = {"mappings": [{"param": "in_strength_a", "expr": "{HP}"}],
                    "outputs": [{"param": "COYOTE.Battery", "name": "batt"}],
                    "output_map": {"COYOTE.Battery": "batt"},
                    "port": 8920}
        logs: list[str] = []
        self.assertTrue(drop_mapping_tables(settings, logs.append))
        for key in ("mappings", "outputs", "output_map"):
            self.assertNotIn(key, settings)
        self.assertEqual(settings["port"], 8920)
        self.assertEqual(len(logs), 1)
        self.assertIn("事件流", logs[0])
        self.assertFalse(drop_mapping_tables(settings, logs.append))
        self.assertEqual(len(logs), 1)

    def test_legacy_family_dropped(self):
        from modules.alice_cradle.plugin import drop_legacy_family

        settings = {"family": "COYOTE"}
        self.assertTrue(drop_legacy_family(settings))
        self.assertNotIn("family", settings)
        self.assertFalse(drop_legacy_family(settings))


class CoreAliasValuesTests(unittest.TestCase):
    def test_bare_aliases_cross_family(self):
        from dglab.params import core_alias_values

        vals = {"COYOTE.StrengthA": 10, "COYOTE.LimitA": 200,
                "COYOTE.Battery": 50, "BMTR.Pressure": 5.2}
        aliases = core_alias_values(vals)
        self.assertEqual(aliases["max"], 200)
        self.assertEqual(aliases["COYOTEmax"], 200)
        self.assertEqual(aliases["Strength"], 10)
        self.assertEqual(aliases["Pressure"], 5.2)
        self.assertEqual(aliases["BMTRPressure"], 5.2)


if __name__ == "__main__":
    unittest.main()
