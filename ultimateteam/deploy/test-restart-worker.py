#!/usr/bin/env python3
"""Exercise real deploy/worker scripts with fake host tools; never contact Docker or systemd."""
import fcntl
import os
from pathlib import Path
import shutil
import stat
import subprocess
import tempfile
import unittest


class RestartWorkerTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory(prefix="aurora21-worker-")
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.deploy = self.root / "deploy with spaces"
        self.deploy.mkdir()
        source = Path(__file__).resolve().parent
        for name in ("restart-worker.sh", "remote-deploy.sh"):
            shutil.copyfile(source / name, self.deploy / name)
        (self.deploy / "compose.yaml").write_text("name: aurora21\n")
        (self.deploy / ".env").write_text("POSTGRES_PASSWORD=synthetic-only\nPUBLIC_IPV4=127.0.0.1\nIMAGE_TAG=old\n")
        self.requests = self.deploy / "restart-requests"
        self.requests.mkdir(mode=0o700)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.env = {**os.environ, "PATH": f"{self.bin}:{os.environ['PATH']}",
                    "XDG_CONFIG_HOME": str(self.root / "config"),
                    "TEST_DEPLOY": str(self.deploy), "TEST_LOG": str(self.root / "commands"),
                    "IMAGE": "ghcr.io/example/server", "IMAGE_TAG": "sha-test"}
        self.env.pop("RESTART_REQUESTS_DIR", None)
        self.tool("docker", '''printf 'docker %s\\n' "$*" >> "$TEST_LOG"
if [[ "$*" == 'compose restart server' && "${FAIL_RESTART:-}" == 1 ]]; then exit 1; fi
if [[ "$*" == 'compose ps --status running --services' ]]; then echo db; fi
''')
        self.tool("loginctl", '''printf 'loginctl %s\\n' "$*" >> "$TEST_LOG"
if [[ "$1" == show-user ]]; then echo "${TEST_LINGER:-yes}"; fi
if [[ "$*" == *enable-linger* && "${FAIL_LINGER:-}" == 1 ]]; then exit 1; fi
''')
        self.tool("systemctl", '''printf 'systemctl %s\\n' "$*" >> "$TEST_LOG"
if [[ "$*" == '--user restart aurora21-restart-worker.service' ]]; then
  bash "$TEST_DEPLOY/restart-worker.sh" once
  touch -d '+2 seconds' "$TEST_DEPLOY/restart-requests/worker.heartbeat"
fi
''')
        self.tool("sleep", "exit 0\n")
        self.tool("curl", '''case "$*" in
  *18443*) printf 401 ;;
  *42230*) printf '<secure>1</secure>' ;;
  *) printf 200 ;;
esac
''')

    def tool(self, name, body):
        path = self.bin / name
        path.write_text("#!/usr/bin/env bash\nset -euo pipefail\n" + body)
        path.chmod(0o755)

    def run_script(self, name="restart-worker.sh", *args, success=True):
        result = subprocess.run(["bash", str(self.deploy / name), *args], cwd=self.root,
                                env=self.env, text=True, capture_output=True, timeout=15)
        if success:
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        else:
            self.assertNotEqual(result.returncode, 0, result.stdout + result.stderr)
        return result

    def commands(self):
        return (self.root / "commands").read_text()

    def test_install_repairs_permissions_and_enables_service(self):
        (self.requests / "worker.heartbeat").write_text("old")
        (self.requests / "worker.heartbeat").chmod(0o600)
        self.run_script("restart-worker.sh", "install")
        self.assertEqual(stat.S_IMODE(self.requests.stat().st_mode), 0o2770)
        self.assertEqual(stat.S_IMODE((self.requests / "worker.heartbeat").stat().st_mode), 0o640)
        self.assertEqual(self.requests.stat().st_gid, os.getgid())
        self.assertIn(f"RESTART_WORKER_GID={os.getgid()}", (self.deploy / ".env").read_text())
        unit = (self.root / "config/systemd/user/aurora21-restart-worker.service").read_text()
        self.assertIn(f'ExecStart=/usr/bin/bash "{self.deploy}/restart-worker.sh" loop', unit)
        self.assertIn("systemctl --user enable aurora21-restart-worker.service", self.commands())

    def test_linger_refusal_fails_with_setup_instruction(self):
        self.env.update(TEST_LINGER="no", FAIL_LINGER="1")
        result = self.run_script("restart-worker.sh", "install", success=False)
        self.assertIn("sudo loginctl enable-linger", result.stderr)
        self.assertNotIn("docker ", self.commands())

    def test_request_content_cannot_select_other_services_or_commands(self):
        (self.requests / "restart-server.request").write_text("db; touch injected\n")
        self.run_script("restart-worker.sh", "once")
        self.assertEqual(self.commands(), "docker compose restart server\n")
        self.assertTrue((self.requests / "worker.result").read_text().startswith("success "))
        self.assertFalse((self.requests / "restart-server.request").exists())
        self.assertFalse((self.requests / "restart-server.in-progress").exists())
        self.run_script("restart-worker.sh", "once")
        self.assertEqual(self.commands().count("restart server"), 1)

    def test_restart_failure_records_result_and_clears_pending(self):
        self.env["FAIL_RESTART"] = "1"
        (self.requests / "restart-server.request").touch()
        self.run_script("restart-worker.sh", "once")
        self.assertTrue((self.requests / "worker.result").read_text().startswith("failed "))
        self.assertFalse((self.requests / "restart-server.in-progress").exists())

    def test_deploy_lock_keeps_request_queued_and_heartbeat_fresh(self):
        (self.requests / "restart-server.request").touch()
        with (self.requests / "deploy.lock").open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            self.run_script("restart-worker.sh", "once")
        self.assertTrue((self.requests / "restart-server.request").exists())
        self.assertTrue((self.requests / "worker.heartbeat").exists())
        self.assertFalse((self.root / "commands").exists())
        self.run_script("restart-worker.sh", "once")
        self.assertIn("docker compose restart server", self.commands())

    def test_second_worker_cannot_claim_requests(self):
        with (self.requests / "worker.lock").open("w") as lock:
            fcntl.flock(lock, fcntl.LOCK_EX)
            result = self.run_script("restart-worker.sh", "once", success=False)
        self.assertIn("already running", result.stderr)

    def test_interrupted_claim_does_not_replay_restart(self):
        (self.requests / "restart-server.in-progress").touch()
        self.run_script("restart-worker.sh", "once")
        self.assertFalse((self.requests / "restart-server.in-progress").exists())
        self.assertTrue((self.requests / "worker.result").read_text().startswith("interrupted "))
        self.assertFalse((self.root / "commands").exists())

    def test_remote_deploy_installs_worker_preserves_secrets_and_takes_backup(self):
        self.env["RESTART_WORKER_GID"] = "99999"
        self.tool("docker", '''[[ "$RESTART_WORKER_GID" == "$(id -g)" ]]
printf 'docker %s\\n' "$*" >> "$TEST_LOG"
if [[ "$*" == 'compose ps --status running --services' ]]; then echo db; fi
''')
        self.run_script("remote-deploy.sh")
        settings = (self.deploy / ".env").read_text()
        self.assertIn("POSTGRES_PASSWORD=synthetic-only", settings)
        self.assertIn("IMAGE_TAG=sha-test", settings)
        self.assertIn(f"RESTART_WORKER_GID={os.getgid()}", settings)
        commands = self.commands()
        self.assertLess(commands.index("systemctl --user restart"), commands.index("docker compose up"))
        self.assertLess(commands.index("backup once predeploy"), commands.index("docker compose up"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
