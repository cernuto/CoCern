"""Exercise a built local host and stdio adapter without touching enrolled rigs."""
import json
import os
from pathlib import Path
import queue
import secrets
import socket
import subprocess
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
HOST = ROOT / "src/PkRelay.Host/bin/Debug/net10.0/PkRelay.Host.dll"
ADAPTER = ROOT / "src/PkRelay.Adapter/bin/Debug/net10.0/PkRelay.Adapter.dll"


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def stop_owned(process):
    if process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def main():
    if not HOST.is_file() or not ADAPTER.is_file():
        raise RuntimeError("Build PkRelay before the live smoke check.")
    work = Path(tempfile.mkdtemp(prefix="pkrelay-smoke-")).resolve()
    token = secrets.token_hex(32)
    port, silo_port, gateway_port = free_port(), free_port(), free_port()
    origin = f"http://127.0.0.1:{port}"
    env = os.environ.copy()
    env.update({
        "ASPNETCORE_URLS": origin,
        "Relay__DatabasePath": str(work / "relay.db"),
        "Relay__SiloPort": str(silo_port),
        "Relay__GatewayPort": str(gateway_port),
        "Relay__Clients__0__Token": token,
        "Relay__Clients__0__PrincipalId": "smoke-client",
        "Relay__Clients__0__IsAdministrator": "true",
        "Relay__Clients__0__Repositories__0": "smoke",
        "PKRELAY_URL": origin,
        "PKRELAY_TOKEN": token,
    })
    env.pop("PKRELAY_TOKEN_FILE", None)
    env.pop("PKRELAY_SIGNALR", None)

    def request(path, payload=None, authenticated=True):
        headers = {"Content-Type": "application/json"}
        if authenticated:
            headers["Authorization"] = f"Bearer {token}"
        body = None if payload is None else json.dumps(payload).encode()
        req = urllib.request.Request(origin + path, data=body, headers=headers)
        with urllib.request.urlopen(req, timeout=10) as response:
            return response.status, response.read()

    def tool(name, args):
        status, raw = request("/api/tools/" + name, {"repositoryId": "smoke", **args})
        assert status == 200
        reply = json.loads(raw)
        assert reply["ok"], reply.get("code")
        return reply, json.loads(reply["dataJson"])

    host_log = (work / "host.log").open("w", encoding="utf-8")
    adapter_log = (work / "adapter.log").open("w", encoding="utf-8")
    host = None
    adapter = None
    try:
        host = subprocess.Popen(["dotnet", str(HOST)], env=env, cwd=ROOT,
                                stdout=host_log, stderr=host_log)
        deadline = time.monotonic() + 30
        while True:
            try:
                request("/health", authenticated=False)
                break
            except (OSError, urllib.error.URLError):
                if host.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError(f"Host startup failed; inspect {work / 'host.log'}")
                time.sleep(0.1)

        try:
            request("/api/tools", authenticated=False)
            raise AssertionError("Anonymous tool access was accepted")
        except urllib.error.HTTPError as error:
            assert error.code == 401
        for path in ("/scalar", "/openapi/v1.json"):
            status, raw = request(path, authenticated=False)
            assert status == 200 and raw

        _, opened = tool("session_open", {"requestId": str(uuid.uuid4()), "machineId": "smoke-pc"})
        session = {"sessionId": opened["sessionId"], "sessionToken": opened["sessionToken"]}
        _, created = tool("task_update", {**session, "requestId": str(uuid.uuid4()),
            "taskId": "continuity", "expectedVersion": 0,
            "patch": {"goal": "Verify durable task continuity"}})
        _, task = tool("task_get", {"taskId": "continuity"})
        assert "continuity" in json.dumps(task)

        adapter = subprocess.Popen(["dotnet", str(ADAPTER)], env=env, cwd=ROOT,
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=adapter_log, text=True, encoding="utf-8")
        lines = queue.Queue()

        def read_stdout():
            for line in adapter.stdout:
                lines.put(line)
            lines.put(None)

        threading.Thread(target=read_stdout, daemon=True).start()

        def rpc(message):
            adapter.stdin.write(json.dumps(message) + "\n")
            adapter.stdin.flush()
            while True:
                line = lines.get(timeout=20)
                if line is None:
                    raise RuntimeError("Adapter closed before replying")
                reply = json.loads(line)
                if reply.get("id") == message["id"]:
                    assert "error" not in reply, reply.get("error", {}).get("code")
                    return reply["result"]

        initialized = rpc({"jsonrpc": "2.0", "id": 1, "method": "initialize",
            "params": {"protocolVersion": "2025-11-25", "capabilities": {},
                       "clientInfo": {"name": "pkrelay-smoke", "version": "1.0"}}})
        assert initialized["serverInfo"]
        adapter.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        adapter.stdin.flush()
        listed = rpc({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})
        names = {item["name"] for item in listed["tools"]}
        assert "task_get" in names and "session_open" in names
        assert "control_request" not in names
        result = rpc({"jsonrpc": "2.0", "id": 3, "method": "tools/call",
            "params": {"name": "task_get", "arguments": {"repositoryId": "smoke", "taskId": "continuity"}}})
        assert not result.get("isError", False)
        assert "continuity" in json.dumps(result)
        adapter.stdin.close()
        adapter.wait(timeout=10)
        assert adapter.returncode == 0
        print("PASS: local authentication, REST continuity, Scalar/OpenAPI, and real stdio MCP handshake/list/call.")
        print(f"Smoke artifacts retained at {work}; no credentials were printed.")
    finally:
        if adapter is not None:
            stop_owned(adapter)
        if host is not None:
            stop_owned(host)
        host_log.close()
        adapter_log.close()


if __name__ == "__main__":
    main()
