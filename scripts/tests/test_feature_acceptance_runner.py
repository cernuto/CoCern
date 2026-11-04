"""Check runner safety boundaries without launching a hub or reading real state."""
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
        count = [0]
        hub.protected_posts.side_effect = lambda: count[0]
        def call(pin):
            correct = pin == "A" * 64
            if correct:
                count[0] += 1
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
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            hub = runner.Hub(root / "fixture", root / "host.dll", root / "adapter.dll", 1)
            hub.key_dir.mkdir(parents=True)
            (hub.key_dir / "key-decoy.xml").write_text("inert", encoding="utf-8")
            hub.lines.append(f"Using '{root / 'unrelated-keys'}' as key repository")
            with mock.patch.object(hub, "request", return_value=(200, {})):
                with self.assertRaises(runner.ProbeFailure):
                    hub.verify_key_isolation()

    def test_cookie_preflight_requires_login_and_actual_expected_repository(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            hub = runner.Hub(root / "fixture", root / "host.dll", root / "adapter.dll", 1)
            hub.key_dir.mkdir(parents=True)
            (hub.key_dir / "key-inert.xml").write_text("inert", encoding="utf-8")
            hub.lines.append(f"Using '{hub.key_dir}' as key repository")
            with mock.patch.object(hub, "request", return_value=(200, {})) as request:
                hub.verify_key_isolation()
                self.assertEqual(request.call_args.args[:2], ("POST", "/dashboard/api/login"))
            with mock.patch.object(hub, "request", return_value=(401, {})):
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
