"""Unattended smoke test: starts a throwaway hub from the build output and checks happy and sad paths end to end.

    python scripts/Smoke-CoCern.py            # build first: dotnet build CoCern.slnx (Debug or Release)

Everything it touches is temporary: its own data folder, its own home folder (for the app settings Connect edits), and free
ports. It never reads or changes your real hub, database or app settings. Every check runs even if an earlier one fails;
the exit code is the number of failed checks (0 = all passed).
"""
import http.cookiejar
import json
import os
from pathlib import Path
import queue
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[1]
REPO = "smoke"


def newest(relative):
    """The newest of the Debug and Release builds, so the check runs whatever was built last."""
    builds = [ROOT / "src" / relative.split("/")[0] / "bin" / config / "net10.0" / relative.split("/")[1] for config in ("Debug", "Release")]
    builds = [path for path in builds if path.is_file()]
    if not builds:
        raise SystemExit(f"Build CoCern first (dotnet build CoCern.slnx): {relative} was not found.")
    return max(builds, key=lambda path: path.stat().st_mtime)


HOST = newest("CoCern.Host/CoCern.Host.dll")
ADAPTER = newest("CoCern.Adapter/CoCern.Adapter.dll")


def free_port():
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def stop(process):
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


class Hub:
    """One hub process in a temporary folder, with an administrator (token file), a worker, and discovery on."""

    def __init__(self, work):
        self.work = work
        self.port = free_port()
        self.origin = f"http://127.0.0.1:{self.port}"
        self.worker_token = secrets.token_hex(32)
        self.token_file = work / "data" / "admin-token"
        self.home = work / "home"
        self.home.mkdir(parents=True)
        self.env = os.environ.copy()
        for name in [name for name in self.env if name.startswith(("COCERN_", "Relay__"))]:
            del self.env[name]
        self.env.update({
            "HOME": str(self.home), "USERPROFILE": str(self.home), "APPDATA": str(self.home / "AppData"),
            "ASPNETCORE_URLS": self.origin,
            "Relay__DatabasePath": str(work / "data" / "relay.db"),
            "Relay__SiloPort": str(free_port()), "Relay__GatewayPort": str(free_port()), "Relay__ClusterId": "smoke-" + uuid.uuid4().hex,
            "Relay__Clients__0__TokenFile": str(self.token_file),
            "Relay__Clients__0__PrincipalId": "smoke-admin",
            "Relay__Clients__0__IsAdministrator": "true",
            "Relay__Clients__0__Repositories__0": REPO,
            "Relay__Clients__1__Token": self.worker_token,
            "Relay__Clients__1__PrincipalId": "smoke-worker",
            "Relay__Clients__1__Repositories__0": REPO,
            "Relay__Dashboard__ViewerPrincipalIds__0": "smoke-admin",
            "Relay__Discovery__Enabled": "true",
            "Relay__Discovery__Repositories__0": REPO,
            "Relay__Connect__AdapterPath": str(ADAPTER),
        })
        self.log_path = work / "host.log"
        self.process = None

    def start(self):
        with self.log_path.open("w", encoding="utf-8") as log:
            self.process = subprocess.Popen(["dotnet", str(HOST)], env=self.env, cwd=self.work, stdout=log, stderr=log)
        deadline = time.monotonic() + 60
        while True:
            try:
                request(self.origin + "/health")
                return
            except OSError:
                if self.process.poll() is not None or time.monotonic() > deadline:
                    raise RuntimeError(f"The hub did not start; see {self.log_path}")
                time.sleep(0.2)

    def log(self):
        return self.log_path.read_text(encoding="utf-8", errors="replace")

    @property
    def admin_token(self):
        return self.token_file.read_text().strip()


def request(url, payload=None, token=None, headers=None, raw=None, opener=None, method=None):
    """Any HTTP call; returns (status, parsed JSON or text) and never raises for an HTTP error status."""
    all_headers = {"Content-Type": "application/json", **(headers or {})}
    if token:
        all_headers["Authorization"] = f"Bearer {token}"
    body = raw if raw is not None else None if payload is None else json.dumps(payload).encode()
    req = urllib.request.Request(url, data=body, headers=all_headers, method=method)
    try:
        response = (opener or urllib.request.build_opener()).open(req, timeout=30)
        status, text = response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        status, text = error.code, error.read().decode(errors="replace")
    try:
        return status, json.loads(text) if text else None
    except json.JSONDecodeError:
        return status, text


class Checks:
    def __init__(self):
        self.results = []

    def run(self, kind, name, check):
        started = time.monotonic()
        try:
            check()
            self.results.append((kind, name, True, ""))
        except Exception as error:  # every check reports, whatever it raises
            self.results.append((kind, name, False, f"{type(error).__name__}: {error}"))
        mark = "PASS" if self.results[-1][2] else "FAIL"
        print(f"  {mark}  {kind:5} {name}  ({time.monotonic() - started:.1f}s)" + ("" if self.results[-1][2] else f"\n        {self.results[-1][3]}"), flush=True)

    def summary(self):
        failed = [result for result in self.results if not result[2]]
        print(f"\n{len(self.results) - len(failed)} of {len(self.results)} checks passed.")
        return len(failed)


def expect(condition, message):
    if not condition:
        raise AssertionError(message)


def main():
    work = Path(tempfile.mkdtemp(prefix="cocern-smoke-")).resolve()
    print(f"Hub: {HOST}\nAdapter: {ADAPTER}\nWork folder: {work}\n")
    hub = Hub(work)
    checks = Checks()
    try:
        hub.start()
        run_checks(hub, checks)
    finally:
        stop(hub.process)
    failed = checks.summary()
    print(f"Logs and data kept in {work}. No credentials were printed.")
    return failed


def run_checks(hub, checks):
    state = {}

    def tool(name, args, token=None, repository=REPO):
        return request(f"{hub.origin}/api/tools/{name}", {"repositoryId": repository, **args}, token=token or hub.admin_token)

    def ok(name, args, token=None):
        status, reply = tool(name, args, token)
        expect(status == 200 and isinstance(reply, dict) and reply.get("ok"), f"{name}: HTTP {status} {reply}")
        return reply, json.loads(reply["dataJson"])

    def refused(name, args, code, status_code, token=None, repository=REPO):
        status, reply = tool(name, args, token, repository)
        actual = reply.get("code") if isinstance(reply, dict) else reply
        expect((status, actual) == (status_code, code), f"expected HTTP {status_code} {code}, got HTTP {status} {actual}")

    def rid():
        return str(uuid.uuid4())

    def session(token=None):
        _, opened = ok("session_open", {"requestId": rid()}, token)
        return {"sessionId": opened["sessionId"], "sessionToken": opened["sessionToken"]}

    # ---- Startup -------------------------------------------------------------------------------------------------
    print("Startup")

    def token_file_created():
        expect(re.fullmatch(r"[0-9A-F]{64}", hub.admin_token) is not None, "the admin token file was not created with a new token")
        if os.name != "nt":
            expect(oct(hub.token_file.stat().st_mode & 0o777) == "0o600", f"token file mode is {oct(hub.token_file.stat().st_mode & 0o777)}")
    checks.run("happy", "missing token file is created at startup, owner-only", token_file_created)

    def health_and_reference():
        for path in ("/health", "/scalar", "/openapi/v1.json"):
            status, _ = request(hub.origin + path)
            expect(status == 200, f"{path}: HTTP {status}")
    checks.run("happy", "health check and API reference respond", health_and_reference)

    def join_code_logged_once():
        log = hub.log()
        expect("Discovery join code" in log and (hub.work / "data" / "join-code").is_file(), "join code not created or not logged")
        state["join_code"] = (hub.work / "data" / "join-code").read_text().strip()
    checks.run("happy", "join code is generated, saved and logged on first start", join_code_logged_once)

    def plain_http_refused():
        env = dict(hub.env, ASPNETCORE_URLS=f"http://0.0.0.0:{free_port()}", Relay__DatabasePath=str(hub.work / "refused" / "relay.db"))
        result = subprocess.run(["dotnet", str(HOST)], env=env, cwd=hub.work, capture_output=True, text=True, timeout=60)
        expect(result.returncode != 0 and "clear text" in (result.stdout + result.stderr), f"exit {result.returncode}")
    checks.run("sad", "hub refuses to start on plain HTTP beyond loopback", plain_http_refused)

    # ---- Authentication ------------------------------------------------------------------------------------------
    print("Authentication")
    checks.run("sad", "no token is refused (401)", lambda: expect(request(hub.origin + "/api/tools")[0] == 401, "not 401"))
    checks.run("sad", "a wrong token is refused (401)", lambda: expect(request(hub.origin + "/api/tools", token="0" * 64)[0] == 401, "not 401"))
    checks.run("happy", "a worker token lists tools", lambda: expect(request(hub.origin + "/api/tools", token=hub.worker_token)[0] == 200, "not 200"))
    checks.run("sad", "unknown tool is 404 UnknownTool", lambda: refused("no_such_tool", {}, "UnknownTool", 404))

    def malformed_json():
        status, _ = request(f"{hub.origin}/api/tools/task_get", raw=b"{not json", token=hub.admin_token)
        expect(status == 400, f"HTTP {status}")
    checks.run("sad", "malformed JSON is 400", malformed_json)
    checks.run("sad", "worker calling an admin tool gets AdministratorRequired",
               lambda: refused("control_request", {"requestId": rid(), "command": "pause"}, "AdministratorRequired", 403, hub.worker_token))
    checks.run("sad", "another repository gets RepositoryDenied", lambda: refused("task_get", {}, "RepositoryDenied", 403, hub.worker_token, "not-mine"))

    # ---- Sessions and tasks --------------------------------------------------------------------------------------
    print("Sessions and tasks")

    def open_sessions():
        state["a"], state["b"] = session(), session(hub.worker_token)
    checks.run("happy", "two chats open sessions", open_sessions)
    checks.run("sad", "a wrong session token gets SessionDenied",
               lambda: refused("check_inbox", {"sessionId": state["a"]["sessionId"], "sessionToken": "f" * 64}, "SessionDenied", 403))

    def workflow(state_name, criterion_passed=False):
        criterion = {"id": "smoke", "description": "Smoke step checked", "result": "passed" if criterion_passed else "not_run"}
        if criterion_passed:
            criterion["evidenceRef"] = "smoke:evidence"
        return {"state": state_name, "packet": {"goal": "Smoke the hub", "target": "This throwaway hub", "authorityRef": "smoke:local",
                "restoration": "Nothing to restore", "stopCondition": "Stop on anything unexpected"}, "criteria": [criterion], "blockers": []}

    def create_tasks():
        first, _ = ok("task_update", {**state["a"], "requestId": rid(), "taskId": "first", "expectedVersion": 0, "workflow": workflow("ready")})
        ok("task_update", {**state["a"], "requestId": rid(), "taskId": "second", "expectedVersion": 0, "workflow": workflow("ready")})
        expect(first["version"] == 1, f"version {first['version']}")
    checks.run("happy", "create two ready tasks", create_tasks)

    def retry_is_idempotent():
        args = {**state["a"], "requestId": "same-request", "taskId": "retried", "expectedVersion": 0, "patch": {"note": "once"}}
        first, _ = ok("task_update", args)
        again, _ = ok("task_update", args)
        expect(first["version"] == again["version"] == 1, f"versions {first['version']}, {again['version']}")
    checks.run("happy", "a retry with the same requestId does not run twice", retry_is_idempotent)
    checks.run("sad", "the same requestId with other arguments gets RequestConflict",
               lambda: refused("task_update", {**state["a"], "requestId": "same-request", "taskId": "retried", "expectedVersion": 0, "patch": {"note": "different"}}, "RequestConflict", 409))
    checks.run("sad", "a stale expectedVersion gets VersionConflict",
               lambda: refused("task_update", {**state["a"], "requestId": rid(), "taskId": "first", "expectedVersion": 0, "patch": {"note": "late"}}, "VersionConflict", 409))
    checks.run("sad", "completing without passed criteria is refused",
               lambda: refused("task_update", {**state["a"], "requestId": rid(), "taskId": "second", "expectedVersion": 1, "workflow": workflow("completed")}, "WorkflowIncomplete", 409))

    def claim_task():
        ok("task_claim", {**state["a"], "requestId": rid(), "taskId": "first", "expectedVersion": 1})
    checks.run("happy", "chat A claims a task", claim_task)
    checks.run("sad", "chat B claiming the same task is refused",
               lambda: refused("task_claim", {**state["b"], "requestId": rid(), "taskId": "first", "expectedVersion": 2}, "Busy", 409, hub.worker_token))

    def complete_and_offer():
        reply, data = ok("task_update", {**state["a"], "requestId": rid(), "taskId": "first", "expectedVersion": 2,
                                         "workflow": workflow("completed", True), "includeNextWork": True})
        offer = data["nextWork"]["offer"]
        expect(offer["taskId"] == "second" and offer["claimRequired"] is True, f"offer {offer}")
    checks.run("happy", "completing a task offers the next ready one", complete_and_offer)

    def dependency_blocks():
        ok("task_update", {**state["a"], "requestId": rid(), "taskId": "later", "expectedVersion": 0,
                           "workflow": {**workflow("ready"), "dependsOn": ["second"]}})
        refused("task_claim", {**state["b"], "requestId": rid(), "taskId": "later", "expectedVersion": 1}, "DependenciesIncomplete", 409, hub.worker_token)
    checks.run("sad", "a task with an unfinished dependency can't be claimed", dependency_blocks)

    # ---- Messages ------------------------------------------------------------------------------------------------
    print("Messages")

    def message_round_trip():
        ok("send_message", {**state["a"], "requestId": rid(), "recipientSessionId": state["b"]["sessionId"], "text": "hello from A"})
        # Delivery is asynchronous (a durable effect). Agents wait with waitSeconds, which the adapter handles; over REST, poll.
        deadline, items = time.monotonic() + 10, []
        while not items and time.monotonic() < deadline:
            _, inbox = ok("check_inbox", state["b"], hub.worker_token)
            items = [item for item in inbox["items"] if "hello from A" in json.dumps(item)]
            time.sleep(0 if items else 0.2)
        expect(items, "message not in B's inbox")
        ok("ack", {**state["b"], "requestId": rid(), "messageIds": [items[0]["messageId"]]}, hub.worker_token)
        _, after = ok("check_inbox", state["b"], hub.worker_token)
        expect(not any("hello from A" in json.dumps(item) for item in after["items"]), "acknowledged message is still in the inbox")
    checks.run("happy", "send, read and acknowledge a message", message_round_trip)

    def inbox_is_private():
        refused("check_inbox", {"sessionId": state["b"]["sessionId"], "sessionToken": state["a"]["sessionToken"]}, "SessionDenied", 403)
    checks.run("sad", "a chat can't read another chat's inbox", inbox_is_private)

    def too_large():
        status, reply = tool("send_message", {**state["a"], "requestId": rid(), "recipientSessionId": state["b"]["sessionId"], "text": "x" * 300_000})
        expect(status == 413 or (isinstance(reply, dict) and reply.get("code") == "PayloadTooLarge"), f"HTTP {status} {reply}")
    checks.run("sad", "an oversized message is refused", too_large)

    # ---- Stdio MCP adapter ---------------------------------------------------------------------------------------
    print("Adapter")
    checks.run("happy", "stdio MCP adapter: handshake, tools, a call, an unchanged etag read, a denied call", lambda: adapter_round_trip(hub))

    def adapter_without_token():
        env = {key: value for key, value in hub.env.items()}
        env["COCERN_TOKEN"] = ""
        env["COCERN_NODE_DIR"] = str(hub.work / "no-node")
        result = subprocess.run(["dotnet", str(ADAPTER)], env=env, input="", capture_output=True, text=True, timeout=60)
        expect(result.returncode != 0 and "COCERN_TOKEN" in result.stderr, f"exit {result.returncode}: {result.stderr.strip()[:200]}")
    checks.run("sad", "adapter without a token stops with a clear message", adapter_without_token)

    # ---- Dashboard -----------------------------------------------------------------------------------------------
    print("Dashboard")
    jar = http.cookiejar.CookieJar()
    browser = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(jar))
    same_origin = {"Origin": hub.origin}

    def links():
        return re.findall(r"/dashboard/#link=([A-Z0-9]{24})", hub.log())

    checks.run("sad", "dashboard API without signing in is 401", lambda: expect(request(hub.origin + "/dashboard/api/auth", opener=browser)[0] == 401, "not 401"))

    def sign_in_with_link():
        expect(links(), "no sign-in link in the hub log")
        state["link"] = links()[-1]
        status, reply = request(hub.origin + "/dashboard/api/login", {"link": state["link"]}, headers=same_origin, opener=browser)
        expect(status == 200 and reply.get("principalId") == "smoke-admin", f"HTTP {status} {reply}")
        expect(request(hub.origin + "/dashboard/api/auth", opener=browser)[0] == 200, "not signed in afterwards")
    checks.run("happy", "the logged sign-in link signs in", sign_in_with_link)

    def link_works_once():
        status, _ = request(hub.origin + "/dashboard/api/login", {"link": state["link"]}, headers=same_origin)
        expect(status == 401, f"HTTP {status}")
        expect(links()[-1] != state["link"], "no new link was logged after the first was used")
    checks.run("sad", "a used sign-in link is refused, and a new one is logged", link_works_once)
    checks.run("sad", "a worker token can't open the dashboard",
               lambda: expect(request(hub.origin + "/dashboard/api/login", {"token": hub.worker_token}, headers=same_origin)[0] == 401, "not 401"))
    checks.run("happy", "the admin token signs in too",
               lambda: expect(request(hub.origin + "/dashboard/api/login", {"token": hub.admin_token}, headers=same_origin)[0] == 200, "not 200"))
    checks.run("sad", "sign-in from another site is refused",
               lambda: expect(request(hub.origin + "/dashboard/api/login", {"token": hub.admin_token}, headers={"Origin": "https://evil.example"})[0] == 403, "not 403"))

    def snapshot():
        # The hub only collects while a live (SignalR) viewer is connected, so this checks the shape, not the counts.
        status, data = request(hub.origin + "/dashboard/api/snapshot", opener=browser)
        expect(status == 200 and "host" in data and "sessions" in data, f"HTTP {status}")
    checks.run("happy", "the dashboard snapshot is served to a signed-in viewer", snapshot)

    def connect_page():
        status, data = request(hub.origin + "/dashboard/api/connect", opener=browser)
        expect(status == 200 and data.get("unavailable") is None, f"HTTP {status} {data}")
        states = {client["id"]: client["state"] for client in data["clients"]}
        expect(states.get("codex") == "available" and states.get("claude-code") == "available", f"states {states}")
        status, data = request(hub.origin + "/dashboard/api/connect/codex", raw=b"", headers=same_origin, opener=browser, method="POST")
        expect(status == 200 and data["clients"][0]["state"] == "added", f"HTTP {status} {data}")
        config = (hub.home / ".codex" / "config.toml").read_text()
        expect("[mcp_servers.cocern]" in config and str(hub.token_file) in config, "codex config.toml was not written as expected")
        status, data = request(hub.origin + "/dashboard/api/connect", opener=browser)
        expect({client["id"]: client["state"] for client in data["clients"]}.get("codex") == "connected", "codex not shown as connected afterwards")
    checks.run("happy", "Connect adds CoCern to Codex (in a temporary home folder)", connect_page)

    def connect_cli():
        result = subprocess.run(["dotnet", str(ADAPTER), "--connect", "--check", "--token-file", str(hub.token_file)],
                                env=hub.env, capture_output=True, text=True, timeout=60)
        expect(result.returncode == 0 and "Codex: already connected" in result.stdout and "Claude Code: not connected yet" in result.stdout, result.stdout + result.stderr)
    checks.run("happy", "adapter --connect --check reports each app", connect_cli)

    def connect_cli_no_token():
        result = subprocess.run(["dotnet", str(ADAPTER), "--connect", "--token-file", str(hub.work / "missing")],
                                env=hub.env, capture_output=True, text=True, timeout=60)
        expect(result.returncode == 1 and "No token file" in result.stderr, result.stdout + result.stderr)
    checks.run("sad", "adapter --connect without a token file says so", connect_cli_no_token)
    checks.run("sad", "connecting an unknown app is 404",
               lambda: expect(request(hub.origin + "/dashboard/api/connect/notepad", raw=b"", headers=same_origin, opener=browser, method="POST")[0] == 404, "not 404"))

    # ---- Other PCs -----------------------------------------------------------------------------------------------
    print("Other PCs")
    enroll_url = hub.origin + "/api/discovery/enroll"
    machine = "smoke-" + uuid.uuid4().hex[:12]

    def wrong_join_code():
        status, reply = request(enroll_url, {"machineId": machine, "machineName": "Smoke PC", "joinCode": "WRONG-CODE"})
        expect((status, reply.get("code")) == (403, "JoinCodeRequired"), f"HTTP {status} {reply}")
    checks.run("sad", "joining with a wrong join code is refused", wrong_join_code)

    def join_and_announce():
        status, reply = request(enroll_url, {"machineId": machine, "machineName": "Smoke PC", "joinCode": state["join_code"].lower()})
        expect(status == 200 and reply.get("machineId") == machine and len(reply.get("credential", "")) == 64, f"HTTP {status} {reply}")
        state["machine_credential"] = reply["credential"]
        own = session(state["machine_credential"])
        chat = {"provider": "codex", "threadId": "t-1", "relaySessionId": own["sessionId"], "repositoryId": REPO, "label": "Smoke chat", "wakeable": True, "waiterRunning": False}
        status, announced = request(hub.origin + "/api/discovery/announce", {"machineName": "Smoke PC", "clients": ["codex"], "chats": [chat]}, token=state["machine_credential"])
        expect(status == 200 and not announced.get("refused"), f"announce HTTP {status} {announced}")
        _, directory = ok("chat_directory", {})
        expect("Smoke chat" in json.dumps(directory), "the announced chat is not in chat_directory")
    checks.run("happy", "a PC joins (code in any case), announces a chat, and it shows in chat_directory", join_and_announce)

    def announce_someone_elses_chat():
        chat = {"provider": "codex", "threadId": "t-2", "relaySessionId": state["a"]["sessionId"], "repositoryId": REPO, "label": "Not mine", "wakeable": True, "waiterRunning": False}
        status, reply = request(hub.origin + "/api/discovery/announce", {"machineName": "Smoke PC", "clients": ["codex"], "chats": [chat]}, token=state["machine_credential"])
        expect(status == 200 and reply["refused"] == [{"relaySessionId": state["a"]["sessionId"], "code": "SessionDenied"}], f"HTTP {status} {reply}")
    checks.run("sad", "a PC can't announce another principal's chat", announce_someone_elses_chat)

    def machine_cannot_admin():
        refused("control_request", {"requestId": rid(), "command": "pause"}, "AdministratorRequired", 403, state["machine_credential"])
    checks.run("sad", "a joined PC can't use admin tools", machine_cannot_admin)

    def add_pc_needs_network():
        status, data = request(hub.origin + "/dashboard/api/add-pc", opener=browser)
        expect(status == 200 and "Relay:Network:Enabled" in (data.get("unavailable") or ""), f"HTTP {status} {data}")
    checks.run("sad", "Add another PC says to turn the network listener on", add_pc_needs_network)

    def enroll_rate_limited():
        statuses = [request(enroll_url, {"machineId": machine + "-x", "machineName": "Smoke PC", "joinCode": "WRONG"})[0] for _ in range(12)]
        expect(429 in statuses, f"statuses {statuses}")
    checks.run("sad", "repeated join attempts are rate limited", enroll_rate_limited)

    # ---- Analytics -----------------------------------------------------------------------------------------------
    print("Analytics")

    def analytics():
        status, reply = tool("analytics_query", {"name": "tokens_per_task"})
        code = reply.get("code") if isinstance(reply, dict) else None
        expect((status == 200 and reply.get("ok")) or code == "AnalyticsUnavailable", f"HTTP {status} {code}")
    checks.run("happy", "analytics answers (or AnalyticsUnavailable before its first snapshot / without the extension)", analytics)
    checks.run("sad", "a worker can't run analytics", lambda: refused("analytics_query", {"name": "tokens_per_task"}, "AdministratorRequired", 403, hub.worker_token))


def adapter_round_trip(hub):
    env = dict(hub.env, COCERN_URL=hub.origin, COCERN_TOKEN=hub.worker_token)
    log = (hub.work / "adapter.log").open("w", encoding="utf-8")
    adapter = subprocess.Popen(["dotnet", str(ADAPTER)], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log, text=True, encoding="utf-8")
    lines = queue.Queue()
    threading.Thread(target=lambda: [lines.put(line) for line in adapter.stdout] + [lines.put(None)], daemon=True).start()

    def text(result):
        return json.loads(next(content["text"] for content in result["content"] if content["type"] == "text"))

    def rpc(message):
        adapter.stdin.write(json.dumps(message) + "\n")
        adapter.stdin.flush()
        while True:
            line = lines.get(timeout=30)
            expect(line is not None, "the adapter closed before replying")
            reply = json.loads(line)
            if reply.get("id") == message["id"]:
                expect("error" not in reply, f"error {reply.get('error')}")
                return reply["result"]

    try:
        rpc({"jsonrpc": "2.0", "id": 1, "method": "initialize",
             "params": {"protocolVersion": "2025-11-25", "capabilities": {}, "clientInfo": {"name": "cocern-smoke", "version": "1.0"}}})
        adapter.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        adapter.stdin.flush()
        names = {item["name"] for item in rpc({"jsonrpc": "2.0", "id": 2, "method": "tools/list", "params": {}})["tools"]}
        expect({"task_get", "session_open"} <= names and "control_request" not in names, "worker tool list is wrong")
        result = rpc({"jsonrpc": "2.0", "id": 3, "method": "tools/call", "params": {"name": "task_get", "arguments": {"repositoryId": REPO, "taskId": "first"}}})
        expect(not result.get("isError") and "first" in json.dumps(result), "task_get through the adapter failed")
        first = text(rpc({"jsonrpc": "2.0", "id": 5, "method": "tools/call", "params": {"name": "task_get", "arguments": {"repositoryId": REPO, "taskId": "first"}}}))
        etag = first["data"]["etag"]
        again = text(rpc({"jsonrpc": "2.0", "id": 6, "method": "tools/call", "params": {"name": "task_get", "arguments": {"repositoryId": REPO, "taskId": "first", "ifNoneMatch": etag}}}))
        expect(again["data"] == {"unchanged": True, "etag": etag}, f"an unchanged read came back as {str(again)[:120]}")
        refused = rpc({"jsonrpc": "2.0", "id": 4, "method": "tools/call", "params": {"name": "task_get", "arguments": {"repositoryId": "not-mine"}}})
        expect(refused.get("isError") and "RepositoryDenied" in json.dumps(refused), "a denied call was not reported as an error")
        adapter.stdin.close()
        adapter.wait(timeout=15)
        expect(adapter.returncode == 0, f"adapter exit {adapter.returncode}")
    finally:
        stop(adapter)
        log.close()


if __name__ == "__main__":
    sys.exit(min(main(), 255))
