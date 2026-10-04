"""alice_cradle 模块测试（纯新协议）：/data 接收、核心参数双向映射派发与回传。"""
from __future__ import annotations

import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import _bootstrap  # noqa: F401  定位 DGStudio 核心仓库

import asyncio
import json
import unittest

from dglab.state import EngineState, Slot
from modules.alice_cradle.server import GameDataServer


def _free_port() -> int:
    import socket
    s = socket.socket()
    try:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]
    finally:
        s.close()


class FakeCtx:
    """替代 ModuleContext：记录核心参数派发器对引擎的全部调用。"""

    def __init__(self, state: EngineState | None = None):
        self.calls: list[tuple] = []
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

    async def set_strength(self, channel, value, slot_id=None):
        self.state.slots["s1"].strength[channel] = value
        self.calls.append(("strength", channel, value))

    async def zap(self, channel, seconds=1.0, slot_id=None):
        self.calls.append(("zap", channel, round(seconds, 3)))

    async def set_wave(self, channel, name, slot_id=None):
        self.calls.append(("wave", channel, name))

    async def fire_start(self, slot_id=None, channel=None):
        self.calls.append(("fire", "start", channel))

    async def fire_stop(self, slot_id=None, channel=None):
        self.calls.append(("fire", "stop", channel))

    async def emergency_stop(self):
        self.calls.append(("emergency",))


class RoutingTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        self.ctx = FakeCtx()
        self.srv = GameDataServer(self.ctx, {
            "mappings": [{"param": "in_strength_a",
                          "expr": "{HP}/{HPmax}*200"}]})
        self.srv.apply_config()

    async def post(self, path, payload):
        body = json.dumps(payload).encode()
        return await self.srv._route("POST", path, body)

    async def get(self, path):
        return await self.srv._route("GET", path, b"")

    async def test_post_data_feeds_engine_and_dispatches(self):
        code, resp = await self.post("/data", {"HP": 60, "HPmax": 100})
        self.assertEqual(code, 200)
        self.assertEqual(sorted(resp["received"]), ["HP", "HPmax"])
        await asyncio.sleep(0.02)
        self.assertIn(("strength", "A", 120), self.ctx.calls)
        # 同值重复上报不再派发
        n = len(self.ctx.calls)
        await self.post("/data", {"HP": 60, "HPmax": 100})
        await asyncio.sleep(0.02)
        self.assertEqual(len(self.ctx.calls), n)

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

    async def test_get_data_default_core_outputs(self):
        # 未配置输出行时 GET /data 回退为核心输出参数实时值 + 短名别名
        code, resp = await self.get("/data")
        self.assertEqual(code, 200)
        data = resp["data"]
        self.assertEqual(data["COYOTE.StrengthA"], 0)
        self.assertEqual(data["COYOTE.Battery"], 77)
        self.assertEqual(data["max"], 200)

    async def test_get_data_renamed_output_rows(self):
        srv = GameDataServer(FakeCtx(), {"outputs": [
            {"param": "COYOTE.Battery", "name": "batteryPct",
             "expr": "{COYOTE.Battery}", "type": "Int"}]})
        srv.apply_config()
        code, resp = await srv._route("GET", "/data", b"")
        self.assertEqual(resp["data"], {"batteryPct": 77})

    async def test_status(self):
        await self.post("/data", {"HP": 30})
        code, resp = await self.get("/status")
        self.assertEqual(resp["mappings"],
                         {"in_strength_a": "{HP}/{HPmax}*200"})
        self.assertEqual(resp["signals"]["HP"], 30.0)

    async def test_unknown_paths(self):
        self.assertEqual((await self.get("/api/game/all"))[0], 404)
        self.assertEqual((await self.get("/healthz"))[0], 404)
        self.assertEqual((await self.post("/other", {}))[0], 404)


class ExpressionTests(unittest.IsolatedAsyncioTestCase):
    async def test_device_vars_mix_and_clamp(self):
        # 用户口径示例：{Strength-max}*({HP}+{Hurt}/{HPmax}) 取整钳制
        ctx = FakeCtx()
        srv = GameDataServer(ctx, {
            "mappings": [{"param": "in_strength_a",
                          "expr": "{Strength-max}*({HP}+{Hurt}/{HPmax})"}]})
        srv.apply_config()
        srv.engine.signal("HP", 60)
        srv.engine.signal("Hurt", 30)
        srv.engine.signal("HPmax", 100)
        await asyncio.sleep(0)
        # Strength=0 → 负值钳到 0
        self.assertEqual(srv.engine.last_values["in_strength_a"], 0)
        slot = ctx.state.slots["s1"]
        slot.strength["A"] = 300
        srv.engine.signal("HP", 61)      # 触发重算
        await asyncio.sleep(0.02)
        self.assertEqual(srv.engine.last_values["in_strength_a"], 200)
        self.assertIn(("strength", "A", 200), ctx.calls)

    async def test_fire_and_emergency_targets(self):
        ctx = FakeCtx()
        srv = GameDataServer(ctx, {"mappings": [
            {"param": "in_fire", "expr": "{danger}"},
            {"param": "in_fire_a", "expr": "{danger}"},
            {"param": "in_emergency", "expr": "{dead}"}]})
        srv.apply_config()
        srv.engine.signal("danger", 1)
        await asyncio.sleep(0.02)
        self.assertIn(("fire", "start", None), ctx.calls)
        self.assertIn(("fire", "start", "A"), ctx.calls)
        srv.engine.signal("danger", 0)
        await asyncio.sleep(0.02)
        self.assertIn(("fire", "stop", None), ctx.calls)
        self.assertIn(("fire", "stop", "A"), ctx.calls)
        srv.engine.signal("dead", 1)
        await asyncio.sleep(0.02)
        self.assertIn(("emergency",), ctx.calls)


class LifecycleTests(unittest.IsolatedAsyncioTestCase):
    async def test_start_stop_over_real_socket(self):
        ctx = FakeCtx()
        srv = GameDataServer(ctx, {"port": _free_port(), "host": "127.0.0.1",
                                   "mappings": [
                {"param": "in_strength_a", "expr": "{HP}"}]})
        await srv.start()
        try:
            port = srv._server.sockets[0].getsockname()[1]
            code, resp = await self._request(port, "POST", "/data", {"HP": 42})
            self.assertEqual(code, 200)
            await asyncio.sleep(0.05)
            self.assertIn(("strength", "A", 42), ctx.calls)
            code, resp = await self._request(port, "GET", "/data")
            self.assertEqual(resp["data"]["COYOTE.StrengthA"], 42)
        finally:
            await srv.stop()
        self.assertFalse(srv.is_running())

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


class PluginMetaTests(unittest.TestCase):
    def test_meta_is_literal_and_class_found(self):
        import _bootstrap
        import plugins

        path = os.path.join(_bootstrap.HERE,
                            "modules", "alice_cradle", "plugin.py")
        meta = plugins._read_meta(path)
        self.assertEqual(meta["id"], "alice_cradle")
        self.assertEqual(meta["settings_key"], "alice_cradle")
        self.assertIn("mappings", meta["config"])
        self.assertIn("outputs", meta["config"])
        self.assertNotIn("output_map", meta["config"])
        self.assertIn("HP", meta["params"])
        self.assertIn("StrengthA", meta["reads"])
        cls = plugins._load_plugin_class("alice_cradle", path)
        self.assertEqual(cls.__name__, "AliceCradleModule")
        inst = cls()
        self.assertFalse(inst.is_running())
        self.assertIn(("HP", "当前生命"), inst.link_params())
        self.assertIn(("StrengthA", "设备通道 A 强度"), inst.read_params())


class MigrateTests(unittest.TestCase):
    def test_legacy_output_map_to_rows(self):
        from modules.alice_cradle.plugin import migrate_legacy

        settings = {"output_map": {"COYOTE.Battery": "batt"}}
        self.assertTrue(migrate_legacy(settings))
        self.assertNotIn("output_map", settings)
        row = next(r for r in settings["outputs"]
                   if r["param"] == "COYOTE.Battery")
        self.assertEqual(row["name"], "batt")


class MaterializeReadsTests(unittest.TestCase):
    def test_empty_outputs_get_default_read_fields(self):
        from modules.alice_cradle.plugin import materialize_reads

        settings = {}
        self.assertTrue(materialize_reads(settings))
        rows = {row["name"]: row for row in settings["outputs"]}
        self.assertEqual(set(rows), {"StrengthA", "StrengthB", "Battery",
                                     "Connected", "Pressure"})
        self.assertEqual(rows["StrengthA"]["param"], "COYOTE.StrengthA")
        self.assertEqual(rows["StrengthA"]["expr"], "{COYOTE.StrengthA}")
        self.assertEqual(rows["Connected"]["type"], "Bool")
        self.assertEqual(rows["Pressure"]["param"], "BMTR.Pressure")

    def test_existing_outputs_untouched(self):
        from modules.alice_cradle.plugin import materialize_reads

        settings = {"outputs": [{"param": "COYOTE.Battery", "name": "batt",
                                 "expr": "{COYOTE.Battery}", "type": "Int"}]}
        self.assertFalse(materialize_reads(settings))
        self.assertEqual([row["name"] for row in settings["outputs"]],
                         ["batt"])

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
