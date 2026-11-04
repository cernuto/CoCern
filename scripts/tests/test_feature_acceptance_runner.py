"""Check runner safety boundaries without launching a hub or reading real state."""
import base64
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest import mock

spec = importlib.util.spec_from_file_location(
    "acceptance_runner", Path(__file__).resolve().parents[1] / "Smoke-FeatureAcceptance.py")
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


class SafetyBoundaryTests(unittest.TestCase):
    # Hand-checked .NET GUID byte order; the cookie and XML identify this key.
    cookie_key = "35918bc9-196d-40ea-9779-889d79b753f0"
    cookie_frame = bytes.fromhex("09f0c9f0c98b91356d19ea409779889d79b753f0") + b"inert-protected-payload"

    def key_evidence(self, hub, key_id=None):
        hub.key_dir.mkdir(parents=True, exist_ok=True)
        key_id = key_id or self.cookie_key
        path = hub.key_dir / f"key-{self.cookie_key}.xml"
        path.write_text(f'<key id="{key_id}" version="1"><descriptor deserializerType="inert"><descriptor /></descriptor></key>', encoding="utf-8")
        return path

    def login_response(self, connection, cookie_frame=None, status=200):
        response = connection.return_value.getresponse.return_value
        response.status = status
        response.read.return_value = b"{}"
        encoded = base64.urlsafe_b64encode(cookie_frame or self.cookie_frame).decode().rstrip("=")
        response.getheaders.return_value = [("Set-Cookie", f"CoCern.Dashboard={encoded}; path=/dashboard; httponly; samesite=strict")]
        return response

    def make_hub(self, root):
        return runner.Hub(root / "fixture", root / "host.dll", root / "adapter.dll", 1)

    def adapter_process(self):
        process = mock.Mock()
        process.poll.return_value = None
        process.wait.return_value = 0
        process.stdout = io.StringIO(json.dumps({"id": 1, "result": {}}) + "\n" +
                                       json.dumps({"id": 2, "result": {"isError": False}}) + "\n")
        return process

    def test_hub_disables_network_listener_without_disabling_discovery(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            self.assertEqual(hub.env["Relay__Network__Enabled"], "false")
            self.assertEqual(hub.env["Relay__Discovery__Enabled"], "true")
            with mock.patch.object(runner.socket, "create_connection") as connect, \
                    mock.patch.object(runner.ssl, "_create_unverified_context") as context:
                context.return_value.wrap_socket.return_value.__enter__.return_value.getpeercert.return_value = b"inert-certificate"
                hub.fingerprint()
                connect.assert_called_once_with(("127.0.0.1", hub.local_https), timeout=3)

    def test_adapter_uses_owned_loopback_tls_port_and_closes_streams(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            process = self.adapter_process()
            with mock.patch.object(runner.subprocess, "Popen", return_value=process) as launch:
                hub.adapter_call("A" * 64)
            self.assertEqual(launch.call_args.kwargs["env"]["COCERN_URL"],
                             f"https://127.0.0.1:{hub.local_https}/")
            self.assertTrue(process.stdout.closed)

    def test_effective_listeners_must_be_exact_owned_loopback_origins(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            hub.lines.extend([f"Now listening on: {hub.origin}",
                              f"Now listening on: https://127.0.0.1:{hub.local_https}"])
            hub.verify_listener_isolation()
            hub.lines.append(f"Now listening on: https://[::]:{hub.https}")
            with self.assertRaises(runner.ProbeFailure):
                hub.verify_listener_isolation()

    def test_corrupt_backup_refusal_requires_backup_path_and_specific_reason(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            process = mock.Mock()
            process.wait.return_value = 1
            reason = "The hub certificate file exists but could not be loaded. It was left unchanged."
            process.stdout = io.StringIO(f"{reason} Identity file: '{hub.backup}'. {runner.RECOVERY_SUFFIX}\n")
            with mock.patch.object(runner.subprocess, "Popen", return_value=process):
                hub.start(expect_failure="corrupt-backup")

    def test_previous_startup_log_cannot_prove_current_refusal(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            reason = "The established hub identity is missing, and no automatic backup is available."
            hub.lines.append(f"{reason} Identity file: '{hub.certificate}'. {runner.RECOVERY_SUFFIX}")
            process = mock.Mock()
            process.wait.return_value = 1
            process.stdout = io.StringIO("Unrelated configuration crash\n")
            with mock.patch.object(runner.subprocess, "Popen", return_value=process):
                with self.assertRaises(runner.ProbeFailure):
                    hub.start(expect_failure="missing-identity-copies")

    def test_adapter_cleanup_still_stops_child_when_stdin_close_raises(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            process = self.adapter_process()
            process.stdin.close.side_effect = BrokenPipeError("inert-close-error")
            with mock.patch.object(runner.subprocess, "Popen", return_value=process):
                try:
                    hub.adapter_call("A" * 64)
                except BrokenPipeError:
                    pass
            process.terminate.assert_called_once()
            process.wait.assert_called_once()
            self.assertTrue(process.stdout.closed)

    def test_unrelated_nonzero_startup_exit_is_not_recovery_refusal(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            process = mock.Mock()
            process.wait.return_value = 1
            process.stdout = io.StringIO("Address already in use; inert-bootstrap-failure\n")
            with mock.patch.object(runner.subprocess, "Popen", return_value=process):
                with self.assertRaises(runner.ProbeFailure):
                    hub.start(expect_failure="missing-identity-copies")

    def test_recovery_refusal_requires_intended_reason_and_owned_identity_path(self):
        suffix = ("Restore the original hub certificate and its matching backup and identity marker "
                  "from a trusted backup, then restart the hub. Existing identity files were not replaced.")
        reason = "The established hub identity is missing, and no automatic backup is available."
        for wrong_path, wrong_reason in ((False, False), (True, False), (False, True)):
            with self.subTest(wrong_path=wrong_path, wrong_reason=wrong_reason), tempfile.TemporaryDirectory() as folder:
                hub = self.make_hub(Path(folder))
                path = hub.root / "unrelated.pfx" if wrong_path else hub.certificate
                expected_reason = "Unrelated failure." if wrong_reason else reason
                process = mock.Mock()
                process.wait.return_value = 1
                process.stdout = io.StringIO(f"System.InvalidOperationException: {expected_reason} Identity file: '{path}'. {suffix}\n")
                with mock.patch.object(runner.subprocess, "Popen", return_value=process):
                    if wrong_path or wrong_reason:
                        with self.assertRaises(runner.ProbeFailure):
                            hub.start(expect_failure="missing-identity-copies")
                    else:
                        hub.start(expect_failure="missing-identity-copies")

    def pin_hub(self, failure_code):
        hub = mock.Mock()
        hub.fingerprint.return_value = "A" * 64
        revision = [0]
        events = []
        def snapshot():
            revision[0] += 1
            return {"revision": revision[0], "traffic": list(events)}
        hub.traffic_snapshot.side_effect = snapshot
        hub.fresh_traffic.side_effect = snapshot
        def call(pin):
            correct = pin == "A" * 64
            if correct:
                events.append({"sequence": len(events) + 1, "kind": "request", "name": "task_get"})
                events.append({"sequence": len(events) + 1, "kind": "response", "name": "task_get"})
            return {"isError": not correct, "content": [{"type": "text", "text": json.dumps(
                {"ok": correct, "code": "Ok" if correct else failure_code})}]}
        hub.adapter_call.side_effect = call
        return hub

    def test_generic_mcp_error_cannot_prove_identity_refusal(self):
        for code in ("hub_unavailable", "invalid_hub_reply", "unauthorized"):
            with self.subTest(code=code), mock.patch.object(runner.time, "sleep"):
                with self.assertRaises(runner.ProbeFailure):
                    runner.pin_refusal(self.pin_hub(code))

    def test_pin_refusal_requires_fresh_correct_pin_control_after_negative(self):
        hub = self.pin_hub("hub_identity_changed")
        with mock.patch.object(runner.time, "sleep"):
            result = runner.pin_refusal(hub)
        self.assertTrue(result["wrongIdentityRefused"])
        self.assertEqual(hub.adapter_call.call_count, 3)
        self.assertEqual(hub.adapter_call.call_args.args, ("A" * 64,))


    def traffic(self, revision, *events):
        return {"revision": revision, "traffic": [
            {"sequence": seq, "kind": kind, "name": name} for seq, kind, name in events]}

    def test_structured_observation_counts_requests_only_and_accepts_empty_baseline(self):
        before = self.traffic(0)
        after = self.traffic(2, (1, "request", "get_status"), (2, "response", "get_status"),
                             (3, "request", "task_get"), (4, "response", "task_get"))
        self.assertEqual(runner.protected_request_count(before, after), 1)
        self.assertEqual(runner.protected_request_count(self.traffic(0), self.traffic(1)), 0)
        self.assertEqual(runner.protected_request_count(after, self.traffic(3, *[
            (e["sequence"], e["kind"], e["name"]) for e in after["traffic"]])), 0)

    def test_observation_rejects_stale_rollback_gaps_and_eviction_before_filtering(self):
        baseline = self.traffic(4, (1, "request", "task_get"), (2, "response", "task_get"))
        for after in (self.traffic(4, (1, "request", "task_get"), (2, "response", "task_get")),
                      self.traffic(3), self.traffic(5),
                      self.traffic(5, (1, "request", "task_get")),
                      self.traffic(5, (4, "request", "get_status")),
                      self.traffic(5, (2, "response", "task_get"), (4, "response", "get_status"))):
            with self.subTest(after=after), self.assertRaises(runner.ProbeFailure):
                runner.protected_request_count(baseline, after)
        with self.assertRaises(runner.ProbeFailure):
            runner.protected_request_count(self.traffic(0), self.traffic(1, (2, "response", "get_status")))

    def test_retained_all_event_boundary_allows_unrelated_new_events(self):
        before = self.traffic(1, (1, "request", "task_get"), (2, "response", "task_get"))
        after = self.traffic(2, (3, "request", "get_status"), (4, "response", "get_status"))
        self.assertEqual(runner.protected_request_count(before, after), 0)

    def test_malformed_observation_fails_closed(self):
        for snapshot in ({}, {"revision": True, "traffic": []}, {"revision": 1, "traffic": {}},
                         self.traffic(1, (0, "request", "task_get")),
                         self.traffic(1, (1, "request", "task_get"), (1, "response", "task_get")),
                         self.traffic(1, (1, "unknown", "task_get"))):
            with self.subTest(snapshot=snapshot), self.assertRaises(runner.ProbeFailure):
                runner.protected_request_count(self.traffic(0), snapshot)

    def test_positive_reply_without_request_or_duplicate_requests_cannot_pass(self):
        for counts in ((0,), (2,), (1, 0, 0), (1, 1)):
            hub = self.pin_hub("hub_identity_changed")
            with mock.patch.object(runner, "protected_request_count", side_effect=counts):
                with self.assertRaises(runner.ProbeFailure):
                    runner.pin_refusal(hub)
            hub.stop_observation.assert_called_once()

    def test_both_correct_controls_and_wrong_pin_have_exact_request_counts(self):
        hub = self.pin_hub("hub_identity_changed")
        with mock.patch.object(runner, "protected_request_count", wraps=runner.protected_request_count) as count:
            result = runner.pin_refusal(hub)
        self.assertTrue(result["noProtectedRequestOnMismatch"])
        self.assertEqual([runner.protected_request_count(*call.args) for call in count.call_args_list], [1, 0, 1])
        self.assertEqual(hub.fresh_traffic.call_count, 3)
        hub.stop_observation.assert_called_once()

    def test_observer_setup_or_collection_failure_still_cleans_up(self):
        for method in ("start_observation", "traffic_snapshot", "fresh_traffic"):
            hub = self.pin_hub("hub_identity_changed")
            getattr(hub, method).side_effect = runner.ProbeFailure("inert observation failure")
            with self.subTest(method=method), self.assertRaises(runner.ProbeFailure):
                runner.pin_refusal(hub)
            hub.stop_observation.assert_called_once()

    def test_freshness_requires_revision_newer_than_postcall_fence(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            with mock.patch.object(hub, "traffic_snapshot", side_effect=[self.traffic(4), self.traffic(4), self.traffic(5)]):
                self.assertEqual(hub.fresh_traffic()["revision"], 5)

    def test_stale_or_rollback_collection_cannot_prove_zero_requests(self):
        for revisions in ([4, 4, 4], [4, 3]):
            with self.subTest(revisions=revisions), tempfile.TemporaryDirectory() as folder:
                hub = self.make_hub(Path(folder))
                snapshots = [self.traffic(r) for r in revisions]
                with mock.patch.object(hub, "traffic_snapshot", side_effect=snapshots), \
                        mock.patch.object(runner.time, "monotonic", side_effect=[0, 0, 2]):
                    with self.assertRaises(runner.ProbeFailure):
                        hub.fresh_traffic()

    def test_dashboard_cookie_is_private_verified_and_scoped(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            self.key_evidence(hub)
            with mock.patch.object(runner.http.client, "HTTPConnection") as connection:
                self.login_response(connection)
                hub.verify_key_isolation()
                hub.dashboard_exchange("GET", "/dashboard/api/snapshot")
                headers = connection.return_value.request.call_args.args[3]
                self.assertTrue(headers["Cookie"].startswith("CoCern.Dashboard="))
                for path in ("/api/tools/task_get", "//elsewhere/dashboard/api/snapshot", "/dashboard/../api/tools/task_get"):
                    with self.assertRaises(runner.ProbeFailure):
                        hub.dashboard_exchange("GET", path)

    def test_dashboard_requires_verified_cookie_and_valid_snapshot(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            with mock.patch.object(runner.http.client, "HTTPConnection") as connection:
                with self.assertRaises(runner.ProbeFailure):
                    hub.dashboard_exchange("GET", "/dashboard/api/snapshot")
                connection.assert_not_called()
            hub.dashboard_cookie = "inert-cookie"
            for reply in ((401, b"{}"), (200, b"not-json"), (200, b"{}")):
                with mock.patch.object(hub, "dashboard_exchange", return_value=reply), self.assertRaises(runner.ProbeFailure):
                    hub.traffic_snapshot()

    def test_signalr_failure_and_stop_release_only_owned_transport(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            hub.dashboard_cookie = "inert-cookie"
            with mock.patch.object(hub, "dashboard_exchange", return_value=(500, b"{}")):
                with self.assertRaises(runner.ProbeFailure):
                    hub.start_observation()
            hub.observer_path = "/dashboard/hub?id=inert-token"
            hub.observer_reader = mock.Mock()
            hub.observer_reader.is_alive.return_value = False
            with mock.patch.object(hub, "dashboard_exchange", side_effect=OSError("inert")):
                hub.stop_observation()
            hub.observer_reader.join.assert_called_once()
            self.assertIsNone(hub.observer_path)


    def test_signalr_owned_connection_handshakes_drains_and_deletes_on_success(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            negotiation = json.dumps({"connectionToken": "inert-token", "availableTransports": [
                {"transport": "LongPolling"}]}).encode()
            first_poll = [True]
            calls = []
            def exchange(method, path, body=None, polling=False):
                calls.append((method, path))
                if "negotiate" in path:
                    return 200, negotiation
                if polling:
                    if first_poll[0]:
                        first_poll[0] = False
                        return 200, b'{}\x1e{"type":1,"target":"snapshot","arguments":[]}\x1e'
                    hub.observer_stopping.wait(2)
                    return 204, b""
                return 200, b""
            with mock.patch.object(hub, "dashboard_exchange", side_effect=exchange):
                hub.start_observation()
                self.assertTrue(hub.observer_ready.is_set())
                self.assertFalse(hub.observer_failed.is_set())
                hub.stop_observation()
            self.assertFalse(hub.observer_reader.is_alive())
            self.assertIn(("DELETE", "/dashboard/hub?id=inert-token"), calls)
            self.assertIsNone(hub.observer_path)

    def test_signalr_partial_setup_and_bad_handshake_delete_owned_transport(self):
        negotiation = json.dumps({"connectionToken": "inert-token", "availableTransports": [
            {"transport": "LongPolling"}]}).encode()
        for stage in ("initial", "send", "handshake", "closed", "incomplete", "poll-exception"):
            with self.subTest(stage=stage), tempfile.TemporaryDirectory() as folder:
                hub = self.make_hub(Path(folder))
                calls = []
                def exchange(method, path, body=None, polling=False):
                    calls.append((method, path))
                    if "negotiate" in path:
                        return 200, negotiation
                    if polling:
                        if stage == "poll-exception":
                            raise OSError("inert transport error")
                        return 200, {"closed": b'{"type":7}\x1e', "incomplete": b'{}'}.get(
                            stage, b'{"error":"inert"}\x1e')
                    if method == "GET" and stage == "initial" or method == "POST" and stage == "send":
                        return 500, b""
                    return 200, b""
                with mock.patch.object(hub, "dashboard_exchange", side_effect=exchange):
                    with self.assertRaises(runner.ProbeFailure):
                        hub.start_observation()
                self.assertIn(("DELETE", "/dashboard/hub?id=inert-token"), calls)
                self.assertIsNone(hub.observer_path)
                if hub.observer_reader is not None:
                    self.assertFalse(hub.observer_reader.is_alive())

    def test_transport_failure_blocks_fresh_evidence_and_closes_response(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            hub.observer_failed.set()
            with mock.patch.object(hub, "dashboard_exchange") as exchange:
                with self.assertRaises(runner.ProbeFailure):
                    hub.fresh_traffic()
                exchange.assert_not_called()
            hub.dashboard_cookie = "inert-cookie"
            for raw in (b"{}", b"x" * 200_001):
                with self.subTest(oversized=len(raw) > 200_000), \
                        mock.patch.object(runner.http.client, "HTTPConnection") as connection:
                    response = connection.return_value.getresponse.return_value
                    response.status = 200
                    response.read.return_value = raw
                    if len(raw) > 200_000:
                        with self.assertRaises(runner.ProbeFailure):
                            hub.dashboard_exchange("GET", "/dashboard/api/snapshot")
                    else:
                        hub.dashboard_exchange("GET", "/dashboard/api/snapshot")
                    response.close.assert_called_once()
                    connection.return_value.close.assert_called_once()

    def test_delete_failure_still_closes_owned_socket_joins_and_stops_process(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            hub.observer_path = "/dashboard/hub?id=inert-token"
            connection = hub.observer_connection = mock.Mock()
            hub.observer_reader = mock.Mock()
            hub.observer_reader.is_alive.return_value = False
            with mock.patch.object(hub, "dashboard_exchange", side_effect=OSError("inert")), \
                    mock.patch.object(hub, "_stop_process") as stop:
                hub.stop()
            connection.sock.shutdown.assert_called_once_with(runner.socket.SHUT_RDWR)
            connection.close.assert_called_once()
            hub.observer_reader.join.assert_called_once()
            stop.assert_called_once()

    def test_failed_reader_join_still_stops_owned_product_process(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            hub.observer_reader = mock.Mock()
            hub.observer_reader.is_alive.return_value = True
            with mock.patch.object(hub, "_stop_process") as stop:
                with self.assertRaises(runner.ProbeFailure):
                    hub.stop()
            stop.assert_called_once()

    def test_evidence_created_during_probe_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            host, adapter, output = root / "host.dll", root / "adapter.dll", root / "report.json"
            content = b"inert-not-an-assembly"
            host.write_bytes(content)
            adapter.write_bytes(content)
            sha = hashlib.sha256(content).hexdigest()
            argv = ["runner", "--host", str(host), "--adapter", str(adapter),
                    "--host-sha256", sha, "--adapter-sha256", sha,
                    "--declared-commit", "test-only", "--output", str(output)]
            def concurrent_writer(hub):
                output.write_bytes(b"concurrent-preserved-evidence")
                return {"inert": True}
            with mock.patch("sys.argv", argv), mock.patch.object(runner.shutil, "which", return_value="dotnet"), \
                    mock.patch.object(runner, "Hub"), mock.patch.object(runner, "PROBES", [("inert", [], concurrent_writer)]), \
                    mock.patch("builtins.print"):
                with self.assertRaises(runner.ProbeFailure):
                    runner.main()
            self.assertEqual(output.read_bytes(), b"concurrent-preserved-evidence")

    def test_environment_filter_is_case_insensitive_and_clears_proxy_paths(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            original = {"cOcErN_tOkEn": "inert-live", "rELay__DatabasePath": "inert-live",
                        "https_proxy": "http://inert-proxy", "HOME": "inert-real-profile",
                        "CODEX_THREAD_ID": "inert-live-chat", "claude_code_session_id": "inert-live-chat",
                        "CODEX_HOME": "inert-real-profile", "CLAUDE_CONFIG_DIR": "inert-real-profile",
                        "PATH": "preserved"}
            before = dict(original)
            env = runner.child_environment(original, root)
            self.assertEqual(original, before)
            self.assertNotIn("cOcErN_tOkEn", env)
            self.assertNotIn("rELay__DatabasePath", env)
            self.assertNotIn("https_proxy", env)
            self.assertNotIn("CODEX_THREAD_ID", env)
            self.assertNotIn("claude_code_session_id", env)
            self.assertTrue(Path(env["CODEX_HOME"]).is_relative_to(root))
            self.assertTrue(Path(env["CLAUDE_CONFIG_DIR"]).is_relative_to(root))
            self.assertEqual(env["PATH"], "preserved")
            self.assertEqual(env["NO_PROXY"], "127.0.0.1,localhost,::1")
            self.assertEqual(env["Relay__DevelopmentAdapter__Enabled"], "false")
            self.assertTrue(env["WEBSITE_INSTANCE_ID"].startswith("acceptance-"))
            self.assertTrue(Path(env["HOME"]).is_relative_to(root))

    def test_fixture_child_environment_cannot_inherit_live_cocern_identity(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            with mock.patch.dict(os.environ, {"COCERN_TOKEN": "inert-inherited-sentinel",
                                             "Relay__DatabasePath": "inert-live-path"}):
                before = dict(os.environ)
                hub = runner.Hub(root / "fixture", root / "host.dll", root / "adapter.dll", 1)
                self.assertEqual(dict(os.environ), before)
                self.assertNotIn("COCERN_TOKEN", hub.env)
                self.assertNotEqual(hub.env["Relay__DatabasePath"], "inert-live-path")
                for key in ("HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "COCERN_NODE_DIR", "CODEX_HOME", "CLAUDE_CONFIG_DIR"):
                    self.assertTrue(Path(hub.env[key]).is_relative_to(hub.root))
                self.assertEqual(hub.env["ASPNETCORE_ENVIRONMENT"], "Production")

    def test_http_cannot_target_an_external_origin_or_inject_headers(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            hub = runner.Hub(root / "fixture", root / "host.dll", root / "adapter.dll", 1)
            for target in ("https://inert.example/api", "//inert.example/api", "/health\r\nX: injected"):
                with self.subTest(target=target), mock.patch.object(runner.http.client, "HTTPConnection") as connection:
                    with self.assertRaises(runner.ProbeFailure):
                        hub.request("GET", target)
                    connection.assert_not_called()

    def test_http_stays_on_owned_origin_and_does_not_follow_redirect(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            hub = runner.Hub(root / "fixture", root / "host.dll", root / "adapter.dll", 1)
            with mock.patch.object(runner.http.client, "HTTPConnection") as connection:
                response = connection.return_value.getresponse.return_value
                response.status = 302
                response.read.return_value = b"{}"
                status, _ = hub.request("GET", "/health")
                self.assertEqual(status, 302)
                connection.assert_called_once_with("127.0.0.1", hub.http, timeout=3)
                self.assertEqual(connection.return_value.request.call_count, 1)
                self.assertEqual(connection.return_value.request.call_args.args[:2], ("GET", "/health"))
                connection.return_value.close.assert_called_once()

    def test_cookie_preflight_rejects_effective_repository_outside_fixture(self):
        for outside in (True, False):
            with self.subTest(outside=outside), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                hub = self.make_hub(root)
                self.key_evidence(hub)
                unexpected = (root if outside else hub.root) / "unrelated-keys"
                hub.lines.append(f"Using '{unexpected}' as key repository")
                with mock.patch.object(runner.http.client, "HTTPConnection") as connection:
                    self.login_response(connection)
                    with self.assertRaises(runner.ProbeFailure):
                        hub.verify_key_isolation()

    def test_cookie_preflight_requires_login_and_actual_expected_repository(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            hub = runner.Hub(root / "fixture", root / "host.dll", root / "adapter.dll", 1)
            self.key_evidence(hub)
            hub.lines.append(f"Using '{hub.key_dir}' as key repository")
            with mock.patch.object(runner.http.client, "HTTPConnection") as connection:
                response = self.login_response(connection)
                hub.verify_key_isolation()
                self.assertEqual(connection.return_value.request.call_args.args[:2], ("POST", "/dashboard/api/login"))
                response.status = 401
                with self.assertRaises(runner.ProbeFailure):
                    hub.verify_key_isolation()

    def test_cookie_preflight_accepts_private_cookie_key_without_information_logs(self):
        with tempfile.TemporaryDirectory() as folder:
            hub = self.make_hub(Path(folder))
            self.key_evidence(hub)
            with mock.patch.object(runner.http.client, "HTTPConnection") as connection:
                self.login_response(connection)
                hub.verify_key_isolation()

    def test_cookie_preflight_rejects_missing_wrong_and_decoy_key_evidence(self):
        for defect in ("missing-cookie", "wrong-cookie-key", "wrong-magic", "truncated-cookie", "wrong-xml-id", "decoy-xml", "missing-key", "outside-key"):
            with self.subTest(defect=defect), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                hub = self.make_hub(root)
                key = self.key_evidence(hub)
                hub.lines.append(f"Using '{hub.key_dir}' as key repository")
                if defect == "wrong-xml-id":
                    self.key_evidence(hub, "aaaaaaaa-0b0b-1c1c-2d2d-333333333333")
                elif defect == "decoy-xml":
                    key.write_text("inert-decoy", encoding="utf-8")
                elif defect in ("missing-key", "outside-key"):
                    key.rename(root / key.name)
                with mock.patch.object(runner.http.client, "HTTPConnection") as connection:
                    frame = self.cookie_frame
                    if defect == "wrong-cookie-key":
                        frame = frame[:4] + bytes.fromhex("aaaaaaaa0b0b1c1c2d2d333333333333") + frame[20:]
                    elif defect == "wrong-magic":
                        frame = b"bad!" + frame[4:]
                    elif defect == "truncated-cookie":
                        frame = frame[:20]
                    response = self.login_response(connection, frame)
                    if defect == "missing-cookie":
                        response.getheaders.return_value = []
                    with self.assertRaises(runner.ProbeFailure):
                        hub.verify_key_isolation()

    def test_deletion_cannot_escape_fixture_root(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            hub = runner.Hub(root / "fixture", root / "host.dll", root / "adapter.dll", 1)
            outside = root / "unrelated.pfx"
            outside.write_bytes(b"inert-preserve-me")
            with self.assertRaises(runner.ProbeFailure):
                hub.remove_owned(outside)
            self.assertEqual(outside.read_bytes(), b"inert-preserve-me")

    def test_deletion_cannot_remove_unrelated_file_even_inside_fixture(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            hub = runner.Hub(root / "fixture", root / "host.dll", root / "adapter.dll", 1)
            unrelated = hub.root / "unrelated.db"
            unrelated.write_bytes(b"inert-preserve-me")
            with self.assertRaises(runner.ProbeFailure):
                hub.remove_owned(unrelated)
            self.assertTrue(unrelated.exists())

    def test_bad_candidate_hash_refuses_before_any_process_or_report_write(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            host, adapter, output = root / "host.dll", root / "adapter.dll", root / "report.json"
            host.write_bytes(b"inert-not-an-assembly")
            adapter.write_bytes(b"inert-not-an-assembly")
            argv = ["runner", "--host", str(host), "--adapter", str(adapter),
                    "--host-sha256", "0" * 64, "--adapter-sha256", "0" * 64,
                    "--declared-commit", "test-only", "--output", str(output)]
            with mock.patch("sys.argv", argv), mock.patch.object(runner.shutil, "which", return_value="dotnet"), \
                    mock.patch.object(runner.subprocess, "Popen") as process:
                with self.assertRaises(runner.ProbeFailure):
                    runner.main()
                process.assert_not_called()
            self.assertFalse(output.exists())

    def test_existing_evidence_is_never_overwritten(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            host, adapter, output = root / "host.dll", root / "adapter.dll", root / "report.json"
            content = b"inert-not-an-assembly"
            host.write_bytes(content)
            adapter.write_bytes(content)
            output.write_bytes(b"preserved-evidence")
            sha = hashlib.sha256(content).hexdigest()
            argv = ["runner", "--host", str(host), "--adapter", str(adapter),
                    "--host-sha256", sha, "--adapter-sha256", sha,
                    "--declared-commit", "test-only", "--output", str(output)]
            with mock.patch("sys.argv", argv), mock.patch.object(runner.shutil, "which", return_value="dotnet"), \
                    mock.patch.object(runner.subprocess, "Popen") as process:
                with self.assertRaises(runner.ProbeFailure):
                    runner.main()
                process.assert_not_called()
            self.assertEqual(output.read_bytes(), b"preserved-evidence")


if __name__ == "__main__":
    unittest.main(verbosity=2)
