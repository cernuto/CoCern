"""Supporting protocol probes; never substitutes for the native-client release gates.

Requires explicit, hash-verified candidate DLLs. Creates disposable state only.
No builds, Connect/setup, installed-node changes, live hub calls, or raw logs.
"""
import argparse
from collections import deque
import hashlib
import http.client
import json
import os
from pathlib import Path
import queue
import re
import secrets
import shutil
import socket
import ssl
import subprocess
import tempfile
import threading
import time
import uuid
from urllib.parse import urlsplit


class ProbeFailure(Exception):
    pass


RECOVERY_SUFFIX = ("Restore the original hub certificate and its matching backup and identity marker "
                   "from a trusted backup, then restart the hub. Existing identity files were not replaced.")


def require(condition, message):
    if not condition:
        raise ProbeFailure(message)


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def port():
    with socket.socket() as stream:
        stream.bind(("127.0.0.1", 0))
        return stream.getsockname()[1]


def stop_owned(process):
    if process is not None and process.poll() is None:
        process.terminate()
        try:
            process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            process.kill()
            process.wait(timeout=5)


def body_data(reply):
    if isinstance(reply.get("dataJson"), str):
        return json.loads(reply["dataJson"])
    return reply.get("data", {})


def child_environment(parent, root):
    managed = {"HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "WEBSITE_INSTANCE_ID",
               "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY",
               "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT", "DOTNET_LAUNCH_PROFILE",
               "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES"}
    env = {name: value for name, value in parent.items()
           if not name.upper().startswith(("COCERN_", "RELAY__", "CODEX_", "CLAUDE_"))
           and name.upper() not in managed}
    profile = root / "profile"
    env.update({"HOME": str(profile), "USERPROFILE": str(profile),
                "APPDATA": str(profile / "AppData"),
                "LOCALAPPDATA": str(profile / "LocalAppData"),
                "CODEX_HOME": str(profile / ".codex"),
                "CLAUDE_CONFIG_DIR": str(profile / ".claude"),
                "COCERN_NODE_DIR": str(root / "unused-node"),
                # ASP.NET's Windows profile lookup can ignore LOCALAPPDATA overrides.
                # Its supported website path uses synthetic HOME for cookie keys.
                "WEBSITE_INSTANCE_ID": "acceptance-" + uuid.uuid4().hex,
                "NO_PROXY": "127.0.0.1,localhost,::1",
                "ASPNETCORE_ENVIRONMENT": "Production", "DOTNET_ENVIRONMENT": "Production",
                "Relay__DevelopmentAdapter__Enabled": "false"})
    return env


class Hub:
    def __init__(self, root, host, adapter, timeout):
        self.root = root.resolve()
        self.root.mkdir()
        self.host, self.adapter, self.timeout = host, adapter, timeout
        self.http, self.https, self.local_https = port(), port(), port()
        require(len({self.http, self.https, self.local_https}) == 3,
                "OS returned duplicate fixture ports; retry with a fresh fixture")
        self.origin = f"http://127.0.0.1:{self.http}"
        self.repo = "acceptance-" + uuid.uuid4().hex
        self.admin, self.worker = secrets.token_hex(32), secrets.token_hex(32)
        self.certificate = self.root / "data" / "hub-certificate.pfx"
        self.backup = Path(str(self.certificate) + ".backup")
        self.marker = Path(str(self.certificate) + ".identity")
        self.process = None
        self.reader = None
        self.lines = deque(maxlen=2000)  # Memory only; never published or persisted.
        profile = self.root / "profile"
        profile.mkdir()
        self.key_dir = profile / "ASP.NET" / "DataProtection-Keys"
        self.env = child_environment(dict(os.environ), self.root)
        self.env.update({
            "ASPNETCORE_URLS": self.origin,
            "Relay__DatabasePath": str(self.root / "data" / "relay.db"),
            "Relay__SiloPort": str(port()), "Relay__GatewayPort": str(port()),
            "Relay__ClusterId": "acceptance-" + uuid.uuid4().hex,
            "Relay__Clients__0__Token": self.admin,
            "Relay__Clients__0__PrincipalId": "acceptance-admin",
            "Relay__Clients__0__IsAdministrator": "true",
            "Relay__Clients__0__Repositories__0": self.repo,
            "Relay__Clients__1__Token": self.worker,
            "Relay__Clients__1__PrincipalId": "acceptance-worker",
            "Relay__Clients__1__Repositories__0": self.repo,
            "Relay__Dashboard__ViewerPrincipalIds__0": "acceptance-admin",
            "Relay__Discovery__Enabled": "true",
            "Relay__Discovery__Repositories__0": self.repo,
            "Relay__Network__Enabled": "false",
            "Relay__Network__HttpsPort": str(self.https),
            "Relay__Local__HttpsPort": str(self.local_https),
            "Relay__Network__CertificatePath": str(self.certificate),
            "Relay__Connect__AdapterPath": str(adapter),
            "Relay__DocCache__SyncMinutes": "0",
            "Logging__LogLevel__Microsoft.AspNetCore.Hosting.Diagnostics": "Information",
            "Logging__LogLevel__Microsoft.AspNetCore.DataProtection": "Information",
            "Logging__LogLevel__Microsoft.Hosting.Lifetime": "Information",
        })

    def capture(self, stream):
        for line in stream:
            self.lines.append(line)

    def start(self, expect_failure=False):
        require(self.process is None or self.process.poll() is not None,
                "Refusing to start a second fixture hub")
        self.lines.clear()
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        self.process = subprocess.Popen(
            ["dotnet", str(self.host)], cwd=self.root, env=self.env,
            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            encoding="utf-8", errors="replace", creationflags=flags)
        self.reader = threading.Thread(target=self.capture, args=(self.process.stdout,), daemon=True)
        self.reader.start()
        if expect_failure:
            try:
                code = self.process.wait(timeout=self.timeout)
            except subprocess.TimeoutExpired:
                raise ProbeFailure("Established-identity startup did not refuse within timeout")
            require(code != 0, "Established-identity startup unexpectedly succeeded")
            self.reader.join(timeout=3)
            require(not self.reader.is_alive(), "Fixture log reader did not finish after exit")
            if expect_failure == "missing-identity-copies":
                path = self.certificate
                reason = "The established hub identity is missing, and no automatic backup is available."
            elif expect_failure == "corrupt-backup":
                path = self.backup
                reason = "The hub certificate file exists but could not be loaded. It was left unchanged."
            else:
                raise ProbeFailure("Unknown expected recovery refusal category")
            expected = f"{reason} Identity file: '{path}'. {RECOVERY_SUFFIX}"
            require(any(expected in line for line in self.lines),
                    "Fixture exited without the intended established-identity recovery refusal; raw logs withheld")
            return
        deadline = time.monotonic() + self.timeout
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                raise ProbeFailure("Fixture hub exited before readiness; raw logs withheld")
            try:
                status, _ = self.request("GET", "/health")
                if status == 200:
                    self.verify_listener_isolation()
                    self.fingerprint()  # HTTPS and the private key must work too.
                    self.verify_key_isolation()
                    return
            except (OSError, ssl.SSLError):
                pass
            time.sleep(0.2)
        raise ProbeFailure("Fixture readiness timeout; no live endpoint used")

    def stop(self):
        try:
            stop_owned(self.process)
        finally:
            if self.reader is not None:
                self.reader.join(timeout=3)
            if self.process is not None and self.process.stdout is not None:
                self.process.stdout.close()

    def verify_listener_isolation(self):
        expected = {self.origin, f"https://127.0.0.1:{self.local_https}"}
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            actual = set()
            for line in list(self.lines):
                match = re.search(r"Now listening on:\s*(\S+)", line)
                if match:
                    actual.add(match.group(1).rstrip("/"))
            require(actual.issubset(expected), "Fixture reported a listener outside its owned loopback origins")
            if actual == expected:
                return
            time.sleep(0.05)
        raise ProbeFailure("Fixture loopback listener isolation was not verified")

    def remove_owned(self, path):
        require(path.resolve().is_relative_to(self.root), "Fixture path escapes temporary root")
        require(path in (self.certificate, self.backup), "Unexpected deletion target")
        path.unlink()

    def request(self, method, path, body=None, token=None):
        target = urlsplit(path)
        require(path.startswith("/") and not path.startswith("//")
                and not target.scheme and not target.netloc
                and not any(ord(char) < 32 for char in path),
                "HTTP target must be a relative path on the owned fixture origin")
        # http.client connects directly; it never consults proxy environment or
        # follows a redirect. The host/port cannot be supplied by a test payload.
        connection = http.client.HTTPConnection("127.0.0.1", self.http, timeout=3)
        headers = {"Content-Type": "application/json", "Origin": self.origin}
        if token is not None:
            headers["Authorization"] = "Bearer " + token
        try:
            connection.request(method, path, json.dumps(body) if body is not None else None, headers)
            response = connection.getresponse()
            raw = response.read(200_001)
            require(len(raw) <= 200_000, "Unexpected oversized fixture response")
            try:
                parsed = json.loads(raw)
            except ValueError:
                parsed = {}
            return response.status, parsed
        finally:
            connection.close()

    def tool(self, name, arguments, token):
        require(re.fullmatch(r"[a-z][a-z0-9_]*", name) is not None, "Invalid fixture tool name")
        return self.request("POST", "/api/tools/" + name,
                            {"repositoryId": self.repo, **arguments}, token)

    def verify_key_isolation(self):
        require(self.key_dir.resolve().is_relative_to(self.root), "Cookie key path escapes fixture")
        # Force cookie protection using only this disposable hub's synthetic
        # viewer credential. Never publish the request, response cookies or keys.
        status, _ = self.request("POST", "/dashboard/api/login", {"token": self.admin})
        require(status == 200, "Synthetic dashboard login preflight failed")
        deadline = time.monotonic() + 3
        while time.monotonic() < deadline:
            repositories = []
            for line in list(self.lines):
                match = re.search(r"Using '([^']+)' as key repository", line, re.IGNORECASE)
                if match:
                    repositories.append(Path(match.group(1)).resolve())
            require(all(path.is_relative_to(self.root) for path in repositories),
                    "ASP.NET reported a key repository outside disposable state")
            if self.key_dir.is_dir() and any(self.key_dir.glob("key-*.xml")) \
                    and self.key_dir.resolve() in repositories:
                return
            time.sleep(0.05)
        raise ProbeFailure("ASP.NET key directory was not verified; refusing further probes")

    def opened(self):
        status, reply = self.tool("session_open", {"requestId": uuid.uuid4().hex}, self.worker)
        require(status == 200 and reply.get("ok"), "Fixture worker session failed")
        data = body_data(reply)
        return {"sessionId": data["sessionId"], "sessionToken": data["sessionToken"]}

    def fingerprint(self):
        context = ssl._create_unverified_context()
        with socket.create_connection(("127.0.0.1", self.local_https), timeout=3) as raw:
            with context.wrap_socket(raw, server_hostname="localhost") as stream:
                return hashlib.sha256(stream.getpeercert(binary_form=True)).hexdigest().upper()

    def protected_posts(self):
        return sum("Request starting" in line and "POST" in line and "/api/tools/" in line
                   for line in list(self.lines))

    def adapter_call(self, fingerprint):
        env = dict(self.env, COCERN_URL=f"https://127.0.0.1:{self.local_https}/",
                   COCERN_TOKEN=self.worker, COCERN_FINGERPRINT=fingerprint)
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0
        process = subprocess.Popen(["dotnet", str(self.adapter)], cwd=self.root, env=env,
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                   stderr=subprocess.DEVNULL, text=True, encoding="utf-8",
                                   creationflags=flags)
        replies = queue.Queue()
        def read():
            try:
                for line in process.stdout:
                    try:
                        replies.put(json.loads(line))
                    except ValueError:
                        continue
            except (OSError, ValueError):
                pass  # Owned stream closed during cleanup; no raw diagnostics.
        reader = threading.Thread(target=read, daemon=True)
        reader.start()
        def rpc(message):
            process.stdin.write(json.dumps(message) + "\n")
            process.stdin.flush()
            deadline = time.monotonic() + self.timeout
            while time.monotonic() < deadline:
                try:
                    reply = replies.get(timeout=max(0.01, deadline - time.monotonic()))
                except queue.Empty:
                    break
                if reply.get("id") == message.get("id"):
                    require("error" not in reply, "MCP protocol error; raw reply withheld")
                    return reply["result"]
            raise ProbeFailure("MCP fixture timeout")
        try:
            rpc({"jsonrpc": "2.0", "id": 1, "method": "initialize", "params": {
                "protocolVersion": "2024-11-05", "capabilities": {},
                "clientInfo": {"name": "FeatureAcceptanceProtocolProbe", "version": "1"}}})
            process.stdin.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
            process.stdin.flush()
            return rpc({"jsonrpc": "2.0", "id": 2, "method": "tools/call", "params": {
                "name": "task_get", "arguments": {"repositoryId": self.repo,
                "limit": 1, "attention": False}}})
        finally:
            try:
                if process.stdin:
                    try:
                        process.stdin.close()
                    except OSError:
                        pass
            finally:
                try:
                    stop_owned(process)
                finally:
                    reader.join(timeout=3)
                    if process.stdout:
                        process.stdout.close()
                    require(not reader.is_alive(), "Adapter fixture reader did not stop")


def authentication(hub):
    hub.start()
    for token in (None, "invalid-acceptance-credential"):
        status, _ = hub.tool("session_open", {"requestId": uuid.uuid4().hex}, token)
        require(status == 401, "Missing/invalid credential was not HTTP 401")
    session = hub.opened()
    status, reply = hub.tool("get_status", session, hub.worker)
    require(status == 200 and reply.get("ok"), "Valid owner control failed")
    status, reply = hub.tool("get_status", session, hub.admin)
    require(status in (401, 403) and not reply.get("ok"), "Other principal could use private session")
    return {"missingAndInvalidRefused": True, "validOwnerWorks": True, "otherPrincipalRefused": True}


def restores_backup(hub):
    hub.start()
    require(hub.backup.is_file() and hub.marker.is_file(), "Automatic backup/identity marker missing")
    original = hub.fingerprint()
    session = hub.opened()
    marker = hub.marker.read_bytes()
    hub.stop()
    hub.remove_owned(hub.certificate)
    hub.start()
    require(hub.fingerprint() == original, "Restored hub changed its identity")
    require(hub.marker.read_bytes() == marker, "Recovery changed identity marker")
    status, reply = hub.tool("get_status", session, hub.worker)
    require(status == 200 and reply.get("ok"), "Existing session failed after identity recovery")
    return {"identityUnchanged": True, "tlsPrivateKeyWorks": True, "sameSessionWorks": True}


def missing_copies(hub):
    hub.start()
    require(hub.backup.is_file() and hub.marker.is_file(), "Automatic backup/identity marker missing")
    marker = hub.marker.read_bytes()
    hub.stop()
    hub.remove_owned(hub.certificate)
    hub.remove_owned(hub.backup)
    hub.start(expect_failure="missing-identity-copies")
    require(not hub.certificate.exists() and not hub.backup.exists(), "Missing identity silently regenerated")
    require(hub.marker.read_bytes() == marker, "Established identity evidence changed")
    return {"startupRefused": True, "refusalCategory": "established_identity_recovery_required",
            "noNewIdentity": True, "evidencePreserved": True}


def corrupt_backup(hub):
    hub.start()
    require(hub.backup.is_file() and hub.marker.is_file(), "Automatic backup/identity marker missing")
    marker = hub.marker.read_bytes()
    hub.stop()
    hub.remove_owned(hub.certificate)
    corrupt = b"inert-corrupt-fixture-backup"
    hub.backup.write_bytes(corrupt)
    hub.start(expect_failure="corrupt-backup")
    require(not hub.certificate.exists(), "Corrupt backup caused identity regeneration")
    require(hub.backup.read_bytes() == corrupt and hub.marker.read_bytes() == marker,
            "Corrupt identity evidence was overwritten")
    return {"startupRefused": True, "refusalCategory": "established_identity_recovery_required",
            "corruptEvidencePreserved": True, "noNewIdentity": True}


def relay_reply(result):
    for item in result.get("content", []):
        if item.get("type") == "text":
            try:
                candidate = json.loads(item.get("text", ""))
            except ValueError:
                continue
            if isinstance(candidate, dict) and "ok" in candidate:
                return candidate
    return None


def pin_refusal(hub):
    hub.start()
    fingerprint = hub.fingerprint()

    def correct_pin_control():
        before = hub.protected_posts()
        result = hub.adapter_call(fingerprint)
        relay = relay_reply(result)
        require(not result.get("isError") and relay is not None and relay.get("ok") is True,
                "Correct-pin control did not return a successful relay reply")
        deadline = time.monotonic() + 2
        while hub.protected_posts() <= before and time.monotonic() < deadline:
            time.sleep(0.05)
        require(hub.protected_posts() > before, "Request observation control unavailable")

    correct_pin_control()
    before = hub.protected_posts()
    wrong_pin = ("B" if fingerprint[0] == "A" else "A") + fingerprint[1:]
    result = hub.adapter_call(wrong_pin)
    relay = relay_reply(result)
    require(result.get("isError") is True and relay is not None and relay.get("ok") is False
            and relay.get("code") == "hub_identity_changed",
            "Wrong-pin call did not report a verified hub identity mismatch")
    time.sleep(0.2)
    require(hub.protected_posts() == before, "Protected request reached wrong-identity hub")
    correct_pin_control()
    return {"correctPinWorks": True, "freshCorrectPinWorksAfterRefusal": True, "wrongIdentityRefused": True,
            "noProtectedRequestOnMismatch": True, "humanRetrustJourney": "NOT RUN"}


PROBES = [("auth-refusal", ["8.4"], authentication),
          ("backup-restoration", ["8.2"], restores_backup),
          ("missing-identity-copies", ["8.2"], missing_copies),
          ("corrupt-backup", ["8.2"], corrupt_backup),
          ("identity-pin-refusal", ["8.3"], pin_refusal)]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", required=True, type=Path)
    parser.add_argument("--adapter", required=True, type=Path)
    parser.add_argument("--host-sha256", required=True)
    parser.add_argument("--adapter-sha256", required=True)
    parser.add_argument("--declared-commit", required=True,
                        help="Operator declaration; binary hashes are the independently checked identity")
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--timeout", type=int, default=30)
    parser.add_argument("--probe", choices=[x[0] for x in PROBES], action="append")
    args = parser.parse_args()
    require(shutil.which("dotnet") is not None, ".NET runtime unavailable")
    require(1 <= args.timeout <= 60, "Timeout must be 1 to 60 seconds")
    for path, expected in ((args.host, args.host_sha256), (args.adapter, args.adapter_sha256)):
        require(path.is_absolute() and path.is_file() and path.suffix.lower() == ".dll",
                "Explicit existing absolute candidate DLL path required")
        require(re.fullmatch(r"[0-9a-fA-F]{64}", expected) is not None
                and digest(path) == expected.upper(), "Candidate SHA-256 mismatch")
    require(not args.output.exists(), "Output already exists; refusing overwrite")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    records = []
    with tempfile.TemporaryDirectory(prefix="cocern-feature-acceptance-") as temporary:
        for name, cases, probe in PROBES:
            if args.probe and name not in args.probe:
                continue
            hub = Hub(Path(temporary) / name, args.host, args.adapter, args.timeout)
            started = time.time()
            record = {"probe": name, "supportsCases": cases, "kind": "supporting-protocol",
                      "startedUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(started))}
            try:
                record.update(status="PASS", evidence=probe(hub))
            except Exception as error:
                # Our own fixed diagnostics never contain raw HTTP/MCP bodies, tokens or logs.
                record.update(status="FAIL", failureType=type(error).__name__,
                              detail=str(error) if isinstance(error, ProbeFailure) else "Fixture exception; raw details withheld")
            finally:
                hub.stop()
            record["durationSeconds"] = round(time.time() - started, 3)
            records.append(record)
            print(json.dumps(record, ensure_ascii=False), flush=True)
    report = {"operatorDeclaredCommit": args.declared_commit,
              "verifiedHostSha256": args.host_sha256.upper(),
              "verifiedAdapterSha256": args.adapter_sha256.upper(),
              "nativeReleaseGate": "NOT RUN", "results": records}
    try:
        with args.output.open("x", encoding="utf-8") as output:
            output.write(json.dumps(report, indent=2, ensure_ascii=False) + "\n")
    except FileExistsError:
        raise ProbeFailure("Output appeared during execution; refusing overwrite") from None
    return int(any(record["status"] != "PASS" for record in records))


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except ProbeFailure as error:
        raise SystemExit(str(error))
