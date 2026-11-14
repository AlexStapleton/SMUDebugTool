import importlib.util
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

SCRIPT = Path(__file__).resolve().parents[2] / "scripts" / "sync_upstream.py"
spec = importlib.util.spec_from_file_location("sync_upstream", SCRIPT)
sync = importlib.util.module_from_spec(spec)
sys.modules[spec.name] = sync
spec.loader.exec_module(sync)


class MergePlannerTests(unittest.TestCase):
    def cmd(self, *args, cwd=None):
        return subprocess.run(args, cwd=cwd or self.worker, check=True, text=True, capture_output=True).stdout.strip()

    def configure(self, folder):
        self.cmd("git", "config", "user.name", "Fixture", cwd=folder)
        self.cmd("git", "config", "user.email", "fixture@example.invalid", cwd=folder)

    def setUp(self):
        self.original = Path.cwd()
        self.temp = tempfile.TemporaryDirectory(prefix="smu-sync-test-")
        self.root = Path(self.temp.name).resolve()
        # Cleanup is limited to this newly generated direct child of the temp directory.
        self.assertEqual(Path(tempfile.gettempdir()).resolve(), self.root.parent)
        self.worker = self.root / "worker"
        self.origin = self.root / "origin.git"
        self.upstream = self.root / "upstream.git"
        for remote in (self.origin, self.upstream):
            self.cmd("git", "init", "--bare", "--initial-branch=master", str(remote), cwd=self.root)
        self.cmd("git", "clone", str(self.origin), str(self.worker), cwd=self.root)
        self.configure(self.worker)
        (self.worker / "shared.txt").write_text("base\n", encoding="utf-8")
        self.cmd("git", "add", ".")
        self.cmd("git", "commit", "-m", "Base")
        self.cmd("git", "push", "origin", "master")
        self.cmd("git", "push", str(self.upstream), "master")
        self.counter = 0
        os.chdir(self.worker)

    def tearDown(self):
        os.chdir(self.original)
        # Git pack/config files can be read-only on Windows; let tempfile handle that.
        self.temp.cleanup()

    def advance(self, remote, file, text):
        self.counter += 1
        writer = self.root / ("writer" + str(self.counter))
        self.cmd("git", "clone", str(remote), str(writer), cwd=self.root)
        self.configure(writer)
        (writer / file).write_text(text, encoding="utf-8")
        self.cmd("git", "add", ".", cwd=writer)
        self.cmd("git", "commit", "-m", "Fixture update", cwd=writer)
        self.cmd("git", "push", "origin", "master", cwd=writer)

    def test_current_upstream_does_not_create_branch(self):
        self.assertEqual("current", sync.prepare(str(self.upstream)).status)
        self.assertEqual("master", self.cmd("git", "branch", "--show-current"))

    def test_clean_merge_retains_fork_changes_and_does_not_publish(self):
        self.advance(self.origin, "fork.txt", "custom")
        self.advance(self.upstream, "upstream.txt", "new")
        plan = sync.prepare(str(self.upstream))
        self.assertEqual("ready", plan.status)
        self.assertTrue(plan.changed)
        self.assertEqual("custom", (self.worker / "fork.txt").read_text())
        self.assertEqual("new", (self.worker / "upstream.txt").read_text())
        self.assertTrue(sync.ancestor(plan.upstream, "HEAD"))
        self.assertEqual("", self.cmd("git", "ls-remote", "--heads", "origin", "refs/heads/" + sync.BRANCH))

    def test_conflict_is_aborted_without_touching_remote(self):
        self.advance(self.origin, "shared.txt", "fork")
        self.advance(self.upstream, "shared.txt", "upstream")
        plan = sync.prepare(str(self.upstream))
        self.assertEqual("blocked", plan.status)
        self.assertIn("shared.txt", plan.reason)
        self.assertEqual("", self.cmd("git", "status", "--porcelain"))
        self.assertEqual("fork", (self.worker / "shared.txt").read_text())
        self.assertEqual("", self.cmd("git", "ls-remote", "--heads", "origin", "refs/heads/" + sync.BRANCH))

    def publish_plan(self):
        self.advance(self.upstream, "upstream.txt", "first")
        self.assertEqual("ready", sync.prepare(str(self.upstream)).status)
        self.cmd("git", "push", "origin", "HEAD:refs/heads/" + sync.BRANCH)

    def test_open_pr_keeps_reviewer_changes_when_upstream_advances(self):
        self.publish_plan()
        (self.worker / "reviewer.txt").write_text("reviewed", encoding="utf-8")
        self.cmd("git", "add", ".")
        self.cmd("git", "commit", "-m", "Reviewer edit")
        reviewed = self.cmd("git", "rev-parse", "HEAD")
        self.cmd("git", "push", "origin", "HEAD:refs/heads/" + sync.BRANCH)
        self.advance(self.upstream, "next.txt", "next")
        plan = sync.prepare(str(self.upstream), resume=True)
        self.assertEqual("ready", plan.status)
        self.assertTrue(plan.changed)
        self.assertTrue(sync.ancestor(reviewed, "HEAD"))
        self.assertEqual("reviewed", (self.worker / "reviewer.txt").read_text())

    def test_existing_unmerged_branch_without_pr_is_not_overwritten(self):
        self.publish_plan()
        previous = self.cmd("git", "rev-parse", "HEAD")
        plan = sync.prepare(str(self.upstream), resume=False)
        self.assertEqual("blocked", plan.status)
        self.assertIn("without an open PR", plan.reason)
        self.assertEqual(previous, self.cmd("git", "rev-parse", "HEAD"))

    def test_unchanged_open_pr_does_not_generate_a_new_commit(self):
        self.publish_plan()
        plan = sync.prepare(str(self.upstream), resume=True)
        self.assertEqual("ready", plan.status)
        self.assertFalse(plan.changed)


class PublicationTests(unittest.TestCase):
    def test_ready_plan_creates_pr_and_explicitly_dispatches_ci(self):
        calls = []

        def api(method, endpoint, payload=None):
            calls.append((method, endpoint, payload))
            return [] if method == "GET" else {}

        with patch.dict(os.environ, {"GH_REPO": "Fixture/Repo"}, clear=True), \
                patch.object(sync, "prepare", return_value=sync.Plan("ready", "a" * 40, "commits", changed=True)), \
                patch.object(sync, "api", side_effect=api), patch.object(sync, "git") as git:
            sync.main()
        git.assert_called_once_with("push", "origin", "HEAD:refs/heads/" + sync.BRANCH)
        self.assertTrue(any(endpoint.endswith("/pulls") and method == "POST" for method, endpoint, _ in calls))
        self.assertEqual({"ref": sync.BRANCH}, calls[-1][2])
        self.assertTrue(calls[-1][1].endswith("/ci.yml/dispatches"))

    def test_repeated_conflict_reuses_issue_without_repeated_updates(self):
        plan = sync.Plan("blocked", "b" * 40, "new commit", "Manual conflicts")
        item = {"number": 42, "body": sync.report_body(plan)}
        with patch.object(sync, "api", return_value=[item]) as api:
            sync.attention("Fixture/Repo", plan)
        api.assert_called_once()

    def test_current_plan_has_no_github_mutations_or_ci_dispatch(self):
        with patch.dict(os.environ, {"GH_REPO": "Fixture/Repo"}, clear=True), \
                patch.object(sync, "prepare", return_value=sync.Plan("current", "c" * 40, "")), \
                patch.object(sync, "api", return_value=[]) as api, patch.object(sync, "git") as git:
            sync.main()
        api.assert_called_once()
        git.assert_not_called()


if __name__ == "__main__":
    unittest.main()
