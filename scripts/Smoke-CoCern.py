"""Unattended smoke test: starts a throwaway hub from the build output and checks happy and sad paths end to end.

    python scripts/Smoke-CoCern.py            # build first: dotnet build CoCern.slnx (Debug or Release)

Everything it touches is temporary: its own data folder, its own home folder (for the app settings Connect edits), and free
ports. It never reads or changes your real hub, database or app settings. Every check runs even if an earlier one fails;
the exit code is the number of failed checks (0 = all passed).
"""
import concurrent.futures
import hashlib
import http.cookiejar
import json
import os
from pathlib import Path
import queue
import re
import secrets
import signal
import socket
import ssl
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
        self.docs = work / "docs-repo"
        self.https_port = free_port()
        self.certificate = work / "data" / "hub-certificate.pfx"
        make_docs_repo(self.docs)
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
            "Relay__Network__Enabled": "true",
            "Relay__Network__HttpsPort": str(self.https_port),
            "Relay__Network__CertificatePath": str(self.certificate),
            "Relay__DocCache__SyncMinutes": "0",
            "Relay__DocCache__Repositories__0__Id": REPO,
            "Relay__DocCache__Repositories__0__ClonePath": str(self.docs),
            "Relay__DocCache__Repositories__0__Branch": "main",
            "Relay__DocCache__Repositories__0__Fetch": "false",
        })
        self.log_path = work / "host.log"
        self.process = None

    def start(self):
        with self.log_path.open("a", encoding="utf-8") as log:
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

    def restart(self):
        stop(self.process)
        self.start()

    def fingerprints(self):
        return re.findall(r"certificate fingerprint ([0-9A-F]{64})", self.log())

    def log(self):
        return self.log_path.read_text(encoding="utf-8", errors="replace")

    @property
    def admin_token(self):
        return self.token_file.read_text().strip()


def make_docs_repo(folder):
    """A one-commit git repository for doc search; nothing is ever fetched."""
    (folder / "docs").mkdir(parents=True)
    (folder / "docs" / "guide.md").write_text("# Guide\n\n## Refunds\n\nRefunds take five business days.\n\n## Shipping\n\nParcels leave within a day.\n")
    git = ["git", "-C", str(folder), "-c", "user.name=smoke", "-c", "user.email=smoke@example.invalid"]
    for command in (["init", "-q", "-b", "main"], ["add", "."], ["commit", "-q", "-m", "docs"]):
        subprocess.run(git + command, check=True, capture_output=True)


def peer_fingerprint(port):
    """SHA-256 of the certificate the hub's HTTPS listener presents (self-signed, so not verified here: the fingerprint is the check)."""
    context = ssl.create_default_context()
    context.check_hostname, context.verify_mode = False, ssl.CERT_NONE
    with socket.create_connection(("127.0.0.1", port), timeout=10) as raw, context.wrap_socket(raw, server_hostname="127.0.0.1") as tls:
        return hashlib.sha256(tls.getpeercert(binary_form=True)).hexdigest().upper()


def run_node(hub, folder, join_code, seconds):
    """Runs the node until it prints that it enrolled (or for `seconds`); returns its exit code (None if still running) and output."""
    env = dict(hub.env, COCERN_NODE_DIR=str(folder))
    node = subprocess.Popen(["dotnet", str(ADAPTER), "--node", "--data-dir", str(folder), "--hub", f"https://127.0.0.1:{hub.https_port}/", "--join-code", join_code],
                            env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    output, deadline = [], time.monotonic() + seconds
    threading.Thread(target=lambda: [output.append(line) for line in node.stdout], daemon=True).start()
    while time.monotonic() < deadline and node.poll() is None and not any("enrolled" in line for line in output):
        time.sleep(0.2)
    code = node.poll()
    stop(node)
    time.sleep(0.2)
    return code, "".join(output)


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

    def https_listener():
        logged = hub.fingerprints()
        expect(logged, "no certificate fingerprint in the hub log")
        expect(peer_fingerprint(hub.https_port) == logged[-1], "the HTTPS listener presents a different certificate than the one logged")
        state["fingerprint"] = logged[-1]
    checks.run("happy", "HTTPS listener serves the certificate whose fingerprint it logs", https_listener)

    def add_pc_filled_in():
        status, data = request(hub.origin + "/dashboard/api/add-pc", opener=browser)
        expect(status == 200 and data.get("unavailable") is None, f"HTTP {status} {data}")
        command = data["addresses"][0]["command"]
        expect(f"--join-code {state['join_code']}" in command and f"--fingerprint {state['fingerprint']}" in command and f":{hub.https_port}/" in command, command)
    checks.run("happy", "Add another PC fills in the join code, fingerprint and port", add_pc_filled_in)

    def add_pc_only_here():
        status, data = request(hub.origin + "/dashboard/api/add-pc")
        expect(status == 401, f"HTTP {status}")
    checks.run("sad", "Add another PC needs a signed-in viewer", add_pc_only_here)

    def node_joins_over_https():
        code, output = run_node(hub, hub.work / "node", state["join_code"], 45)
        expect("enrolled" in output, f"exit {code}: {output.strip()[-300:]}")
        expect((hub.work / "node").is_dir() and any((hub.work / "node").iterdir()), "the node saved nothing")
    checks.run("happy", "a real node joins over HTTPS", node_joins_over_https)

    def node_wrong_code():
        code, output = run_node(hub, hub.work / "node-wrong", "WRONG-CODE-0000", 45)
        expect(code is not None and code != 0 and "join code" in output, f"exit {code}: {output.strip()[-300:]}")
    checks.run("sad", "a node with a wrong join code stops and says why", node_wrong_code)

    def enroll_rate_limited():
        statuses = [request(enroll_url, {"machineId": machine + "-x", "machineName": "Smoke PC", "joinCode": "WRONG"})[0] for _ in range(12)]
        expect(429 in statuses, f"statuses {statuses}")
    checks.run("sad", "repeated join attempts are rate limited", enroll_rate_limited)

    # ---- Resources, actions and notes --------------------------------------------------------------------------------
    print("Resources, actions and notes")

    def resource_claims():
        ok("control_request", {"requestId": rid(), "command": "register_resource", "resourceId": "rig-1", "machineId": "smoke-pc"})
        _, held = ok("claim_acquire", {**state["a"], "requestId": rid(), "resourceId": "rig-1"})
        state["generation"] = held["generation"]
        refused("claim_acquire", {**state["b"], "requestId": rid(), "resourceId": "rig-1"}, "Busy", 409, hub.worker_token)
        ok("claim_renew", {**state["a"], "requestId": rid(), "resourceId": "rig-1", "generation": state["generation"], "leaseSeconds": 600})
    checks.run("happy", "register a resource, claim it, renew it; a second claim is Busy", resource_claims)
    checks.run("sad", "releasing with a stale generation is refused",
               lambda: refused("claim_release", {**state["a"], "requestId": rid(), "resourceId": "rig-1", "generation": state["generation"] + 5}, "StaleClaim", 409))

    def release_then_other_claims():
        ok("claim_release", {**state["a"], "requestId": rid(), "resourceId": "rig-1", "generation": state["generation"]})
        ok("claim_acquire", {**state["b"], "requestId": rid(), "resourceId": "rig-1"}, hub.worker_token)
    checks.run("happy", "after release, another chat can claim it", release_then_other_claims)
    checks.run("sad", "claiming an unregistered resource is refused",
               lambda: refused("claim_acquire", {**state["a"], "requestId": rid(), "resourceId": "no-such-rig"}, "ResourceUnknown", 400))

    def action_flow():
        ok("task_update", {**state["a"], "requestId": rid(), "taskId": "act", "expectedVersion": 0, "workflow": workflow("ready")})
        ok("task_claim", {**state["a"], "requestId": rid(), "taskId": "act", "expectedVersion": 1})
        ok("action_begin", {**state["a"], "requestId": rid(), "taskId": "act", "actionId": "deploy", "inputFingerprint": "v1", "expectedVersion": 2})
        _, task = ok("task_get", {"taskId": "act"})
        expect(task["actions"]["deploy"]["status"] in ("started", "invoking", "begun"), f"action status {task['actions']['deploy'].get('status')}")
        ok("action_report", {**state["a"], "requestId": rid(), "taskId": "act", "actionId": "deploy", "attempt": 1, "outcome": "completed", "expectedVersion": task["version"]})
        _, task = ok("task_get", {"taskId": "act"})
        expect(task["actions"]["deploy"]["status"] == "completed", f"action status {task['actions']['deploy'].get('status')}")
    checks.run("happy", "begin an action and report it completed", action_flow)
    checks.run("sad", "another chat can't begin an action on a task it didn't claim",
               lambda: refused("action_begin", {**state["b"], "requestId": rid(), "taskId": "act", "actionId": "other", "inputFingerprint": "v1", "expectedVersion": 4}, "ClaimRequired", 400, hub.worker_token))
    checks.run("sad", "reporting an action that never began is refused",
               lambda: refused("action_report", {**state["a"], "requestId": rid(), "taskId": "act", "actionId": "ghost", "attempt": 1, "outcome": "completed", "expectedVersion": 4}, "ActionUnknown", 400))

    def notes():
        ok("context_put", {**state["a"], "requestId": rid(), "contextId": "note-1", "content": "The rig password rotates on Mondays.", "expectedVersion": 0})
        _, note = ok("context_get", {**state["b"], "contextId": "note-1"}, hub.worker_token)
        expect("Mondays" in json.dumps(note), "note content not returned")
    checks.run("happy", "write a shared note and read it from another chat", notes)
    checks.run("sad", "overwriting a note with a stale version gets VersionConflict",
               lambda: refused("context_put", {**state["b"], "requestId": rid(), "contextId": "note-1", "content": "late", "expectedVersion": 0}, "VersionConflict", 409, hub.worker_token))

    def search():
        _, hits = ok("search", {**state["a"], "query": "Mondays"})
        expect("note-1" in json.dumps(hits), f"note not found: {json.dumps(hits)[:200]}")
    checks.run("happy", "search finds the note", search)
    checks.run("sad", "search in another repository is refused",
               lambda: refused("search", {**state["b"], "query": "Mondays"}, "RepositoryDenied", 403, hub.worker_token, "not-mine"))

    # ---- Standing, orders, attention, pause --------------------------------------------------------------------------
    print("Standing, orders, attention, pause")

    def poll_attention(who, kind, text=None):
        """The first offer matching kind and text. Other offers are dismissed; one the hub closed meanwhile (NotFound) is fine."""
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            _, inbox = ok("check_inbox", who)
            offer = inbox.get("attention")
            if offer:
                state.setdefault("offers", []).append({key: offer.get(key) for key in ("id", "k", "t")})
            if offer and (kind is None or offer.get("k") == kind) and (text is None or text in json.dumps(offer)):
                return offer
            if offer:
                status, reply = tool("attention_respond", {**who, "requestId": rid(), "itemId": offer["id"], "action": "dismiss"})
                expect(status == 200 or (isinstance(reply, dict) and reply.get("code") == "NotFound"), f"dismissing {offer['id']}: HTTP {status} {reply}")
            time.sleep(0.3)
        return None

    def standing():
        _, proposal = ok("standing_propose", {**state["b"], "requestId": rid(), "line": {"text": "Always run the smoke test."}}, hub.worker_token)
        proposal_id = proposal.get("proposalId") or proposal.get("id")
        expect(proposal_id, f"no proposal ID in {proposal}")
        _, written = ok("standing_put", {"requestId": rid(), "expectedRevision": 0, "approve": [proposal_id]})
        state["standing_revision"] = written.get("revision", 1)
        _, opened = ok("session_open", {"requestId": rid()}, hub.worker_token)
        expect("Always run the smoke test." in json.dumps(opened), "a new session did not receive the approved line")
    checks.run("happy", "a worker proposes a rule, an admin approves it, new sessions get it", standing)
    checks.run("sad", "standing_put with a stale revision gets VersionConflict",
               lambda: refused("standing_put", {"requestId": rid(), "expectedRevision": 0, "lines": [{"id": "x", "text": "late"}]}, "VersionConflict", 409))
    checks.run("sad", "a worker can't write standing rules",
               lambda: refused("standing_put", {"requestId": rid(), "expectedRevision": 1, "lines": [{"id": "x", "text": "mine"}]}, "AdministratorRequired", 403, hub.worker_token))

    def attention_and_order():
        _, opened = ok("session_open", {"requestId": rid(), "role": "manager"})
        manager = {"sessionId": opened["sessionId"], "sessionToken": opened["sessionToken"]}
        state["manager"] = manager
        ok("order_put", {"requestId": rid(), "orderId": "notify-done", "trigger": {"on": "workflow_completed", "taskId": "second"},
                         "actions": [{"do": "notify", "sessionId": manager["sessionId"], "note": "second is done"}]})
        ok("task_claim", {**state["b"], "requestId": rid(), "taskId": "second", "expectedVersion": 1}, hub.worker_token)
        blocked = workflow("blocked")
        blocked["blockers"] = [{"id": "b1", "kind": "missing_fixture", "summary": "No test database", "recheckCondition": "Database restored"}]
        ok("task_update", {**state["b"], "requestId": rid(), "taskId": "second", "expectedVersion": 2, "workflow": blocked}, hub.worker_token)
        offer = poll_attention(manager, "blocked")
        expect(offer is not None, f"the manager got no blocked item; offers seen: {state.get('offers')}")
        status, reply = tool("attention_respond", {**manager, "requestId": rid(), "itemId": offer["id"], "action": "dismiss"})
        expect(status == 200, f"dismissing the offered blocked item {offer['id']}: HTTP {status} {reply}; offers seen: {state.get('offers')}")
        refused("attention_respond", {**manager, "requestId": rid(), "itemId": "no-such-item", "action": "dismiss"}, "NotFound", 404)
        _, task = ok("task_get", {"taskId": "second"})
        ready = workflow("ready")
        ready["recheckEvidenceRef"] = "smoke:database-restored"
        ok("task_update", {**state["b"], "requestId": rid(), "taskId": "second", "expectedVersion": task["version"], "workflow": ready}, hub.worker_token)
        _, task = ok("task_get", {"taskId": "second"})
        ok("task_update", {**state["b"], "requestId": rid(), "taskId": "second", "expectedVersion": task["version"], "workflow": workflow("completed", True)}, hub.worker_token)
        note = poll_attention(manager, None, "second is done")
        expect(note is not None, "the order's notify never reached the manager")
    checks.run("happy", "a blocked task alerts the manager; an order notifies on completion", attention_and_order)
    checks.run("sad", "a worker can't add orders",
               lambda: refused("order_put", {"requestId": rid(), "orderId": "mine", "trigger": {"on": "workflow_completed", "taskId": "act"}, "actions": [{"do": "release_claims"}]}, "AdministratorRequired", 403, hub.worker_token))

    def pause():
        ok("control_request", {"requestId": rid(), "command": "pause", "reason": "smoke"})
        ok("task_update", {**state["a"], "requestId": rid(), "taskId": "paused", "expectedVersion": 0, "workflow": workflow("ready")})
        refused("task_claim", {**state["a"], "requestId": rid(), "taskId": "paused", "expectedVersion": 1}, "Paused", 409)
        ok("control_request", {"requestId": rid(), "command": "resume"})
        ok("task_claim", {**state["a"], "requestId": rid(), "taskId": "paused", "expectedVersion": 1})
    checks.run("sad", "while paused, claims are refused; after resume they work", pause)

    # ---- Doc search ----------------------------------------------------------------------------------------------
    print("Doc search")

    def doc_search():
        ok("doc_sync", {"requestId": rid()})
        deadline, hits = time.monotonic() + 30, []
        while not hits and time.monotonic() < deadline:
            _, found = ok("doc_search", {"query": "refunds"}, hub.worker_token)
            hits = found.get("hits", [])
            time.sleep(0 if hits else 0.5)
        expect(hits and hits[0]["path"] == "docs/guide.md", f"hits {hits}")
        _, section = ok("doc_section", {"path": hits[0]["path"], "anchor": hits[0]["anchor"]}, hub.worker_token)
        expect("five business days" in json.dumps(section), "section text missing")
    checks.run("happy", "sync a git repo's docs, search them, read a section", doc_search)
    checks.run("sad", "a worker can't force a doc sync", lambda: refused("doc_sync", {"requestId": rid()}, "AdministratorRequired", 403, hub.worker_token))
    checks.run("sad", "reading a section that doesn't exist is 404",
               lambda: refused("doc_section", {"path": "docs/guide.md", "anchor": "nope"}, "NotFound", 404, hub.worker_token))

    # ---- Edge cases ----------------------------------------------------------------------------------------------
    print("Edge cases")

    def raw_body(body):
        return request(f"{hub.origin}/api/tools/task_get", raw=body, token=hub.admin_token)

    checks.run("sad", "a repeated JSON key is 400, not a server error",
               lambda: expect(raw_body(b'{"repositoryId":"smoke","repositoryId":"other"}')[0] == 400, "not 400"))
    checks.run("sad", "invalid UTF-8 is 400, not a server error",
               lambda: expect(raw_body(b'{"repositoryId":"\xff\xfe"}')[0] == 400, "not 400"))
    checks.run("sad", "a fractional or oversized version is refused, never read as 0",
               lambda: [refused("task_update", {**state["a"], "requestId": rid(), "taskId": f"v-{i}", "expectedVersion": v}, "InvalidArguments", 400) for i, v in enumerate((1.5, 2**64))])
    checks.run("sad", "IDs that are blank or hold control characters are refused",
               lambda: [refused("task_update", {**state["a"], "requestId": rid(), "taskId": t, "expectedVersion": 0}, "InvalidArguments", 400) for t in ("   ", "a\nb", "a\u0000b")])

    def quoted_and_null_numbers():
        ok("task_update", {**state["a"], "requestId": rid(), "taskId": "quoted", "expectedVersion": "0"})
        ok("task_claim", {**state["a"], "requestId": rid(), "taskId": "quoted", "expectedVersion": 1, "leaseSeconds": None})
    checks.run("happy", "a quoted number works, and a null optional number counts as absent", quoted_and_null_numbers)

    def unknown_recipient_drains():
        ok("send_message", {**state["a"], "requestId": rid(), "recipientSessionId": "no-such-session", "text": "into the void"})
        ok("send_message", {**state["a"], "requestId": rid(), "recipientSessionId": state["b"]["sessionId"], "text": "after the void"})
        deadline = time.monotonic() + 10
        while time.monotonic() < deadline:
            _, inbox = ok("check_inbox", state["b"], hub.worker_token)
            if any("after the void" in json.dumps(item) for item in inbox["items"]):
                return
            time.sleep(0.2)
        raise AssertionError("a message to an unknown session held up the sender's later messages")
    checks.run("sad", "a message to an unknown session doesn't block later ones", unknown_recipient_drains)

    def parallel_claims():
        ok("task_update", {**state["a"], "requestId": rid(), "taskId": "race", "expectedVersion": 0, "workflow": workflow("ready")})
        racers = [session() for _ in range(10)]
        with concurrent.futures.ThreadPoolExecutor(10) as pool:
            results = list(pool.map(lambda racer: tool("task_claim", {**racer, "requestId": rid(), "taskId": "race", "expectedVersion": 1}), racers))
        winners = [result for result in results if result[0] == 200]
        expect(len(winners) == 1, f"{len(winners)} chats won the same task")
    checks.run("sad", "10 chats claiming one task at once: exactly one wins", parallel_claims)

    def parallel_retries():
        same = rid()
        with concurrent.futures.ThreadPoolExecutor(10) as pool:
            list(pool.map(lambda _: tool("task_update", {**state["a"], "requestId": same, "taskId": "once", "expectedVersion": 0, "patch": {"n": 1}}), range(10)))
        _, task = ok("task_get", {"taskId": "once"})
        expect(task["version"] == 1, f"the write ran {task['version']} times")
    checks.run("sad", "the same write sent 10 times at once runs once", parallel_retries)

    def lapsed_lease():
        ok("task_update", {**state["a"], "requestId": rid(), "taskId": "lapse", "expectedVersion": 0, "workflow": workflow("ready")})
        _, held = ok("task_claim", {**state["a"], "requestId": rid(), "taskId": "lapse", "expectedVersion": 1, "leaseSeconds": 1})
        time.sleep(2.2)
        ok("task_claim", {**state["b"], "requestId": rid(), "taskId": "lapse", "expectedVersion": 2}, hub.worker_token)
        refused("claim_renew", {**state["a"], "requestId": rid(), "taskId": "lapse", "claimGeneration": held["claimGeneration"], "leaseSeconds": 60}, "StaleClaim", 409)
    checks.run("sad", "after a lease runs out another chat can claim it, and the old claim can't be renewed", lapsed_lease)

    def paging():
        reader = session(hub.worker_token)
        for i in range(45):
            ok("send_message", {**state["a"], "requestId": rid(), "recipientSessionId": reader["sessionId"], "text": f"page {i:02}"})
        deadline, texts = time.monotonic() + 15, set()
        while len(texts) < 45 and time.monotonic() < deadline:
            texts, cursor = set(), None
            while True:
                _, inbox = ok("check_inbox", {**reader, **({"cursor": cursor} if cursor else {})}, hub.worker_token)
                texts |= {item["text"] for item in inbox["items"]}
                cursor = inbox.get("nextCursor")
                if not cursor:
                    break
            time.sleep(0.2)
        expect(len(texts) == 45, f"read {len(texts)} of 45 across pages")
        state["paging_reader"] = reader
    checks.run("happy", "45 messages read exactly once across inbox pages", paging)

    def ack_only_own():
        _, inbox = ok("check_inbox", state["paging_reader"], hub.worker_token)
        _, acked = ok("ack", {**state["a"], "requestId": rid(), "messageIds": [inbox["items"][0]["messageId"]]})
        expect(acked.get("acknowledged") == 0, f"acknowledged {acked}")
        _, after = ok("check_inbox", state["paging_reader"], hub.worker_token)
        expect(after["total"] == inbox["total"], "another chat's acknowledgment removed a message")
    checks.run("sad", "a chat can't acknowledge another chat's messages", ack_only_own)

    def killed_mid_flight():
        for i in range(10):
            ok("send_message", {**state["a"], "requestId": rid(), "recipientSessionId": state["b"]["sessionId"], "text": f"crash {i}"})
        os.kill(hub.process.pid, signal.SIGKILL)
        hub.process.wait()
        hub.start()
        deadline, texts = time.monotonic() + 20, set()
        while len(texts) < 10 and time.monotonic() < deadline:
            texts, cursor = set(), None
            while True:
                _, inbox = ok("check_inbox", {**state["b"], **({"cursor": cursor} if cursor else {})}, hub.worker_token)
                texts |= {item["text"] for item in inbox["items"] if item["text"].startswith("crash")}
                cursor = inbox.get("nextCursor")
                if not cursor:
                    break
            time.sleep(0.3)
        expect(len(texts) == 10, f"{len(texts)} of 10 messages delivered after the hub was killed")
    checks.run("happy", "messages in flight survive the hub being killed (kill -9)", killed_mid_flight)

    # ---- Restart -------------------------------------------------------------------------------------------------
    print("Restart")

    def survives_restart():
        hub.restart()
        _, task = ok("task_get", {"taskId": "act"})
        expect(task["actions"]["deploy"]["status"] == "completed", "task state was lost across a restart")
        _, note = ok("context_get", {**state["b"], "contextId": "note-1"}, hub.worker_token)
        expect("Mondays" in json.dumps(note), "note lost across a restart")
        expect(peer_fingerprint(hub.https_port) == state["fingerprint"], "the certificate changed across a restart")
        status, _ = request(hub.origin + "/dashboard/api/auth", opener=browser)
        expect(status in (200, 401), f"HTTP {status}")
    checks.run("happy", "tasks, notes and the certificate survive a restart", survives_restart)

    def changed_certificate():
        stop(hub.process)
        hub.certificate.unlink()
        hub.start()
        expect(hub.fingerprints()[-1] != state["fingerprint"], "the hub did not make a new certificate")
        code, output = run_node(hub, hub.work / "node", state["join_code"], 45)
        expect(code == 4, f"exit {code}: {output.strip()[-300:]}")
    checks.run("sad", "a joined node refuses a hub whose certificate changed (exit 4)", changed_certificate)

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
