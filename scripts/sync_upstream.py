"""Prepare a reviewable upstream merge; never merge into master or publish a release."""
import json
import os
import subprocess
from dataclasses import dataclass

UPSTREAM = "https://github.com/irusanov/SMUDebugTool.git"
BRANCH = "automation/upstream-sync"
MARKER = "<!-- smudebugtool-upstream-sync -->"


def run(*args, check=True):
    return subprocess.run(args, text=True, encoding="utf-8", capture_output=True, check=check)


def git(*args, check=True):
    return run("git", *args, check=check)


def ancestor(older, newer):
    result = git("merge-base", "--is-ancestor", older, newer, check=False)
    if result.returncode not in (0, 1):
        raise RuntimeError(result.stderr)
    return result.returncode == 0


@dataclass
class Plan:
    status: str
    upstream: str
    commits: str
    reason: str = ""
    changed: bool = False


def prepare(upstream=UPSTREAM, resume=False):
    git("fetch", "--no-tags", "origin", "+refs/heads/master:refs/remotes/origin/master")
    git("fetch", "--no-tags", upstream, "+refs/heads/master:refs/remotes/upstream/master")
    tip = git("rev-parse", "refs/remotes/upstream/master").stdout.strip()
    commits = git("log", "--format=%H %s", "origin/master..refs/remotes/upstream/master").stdout.strip()
    if ancestor(tip, "origin/master"):
        return Plan("current", tip, commits)
    exists = git("ls-remote", "--exit-code", "--heads", "origin", "refs/heads/" + BRANCH, check=False)
    if exists.returncode not in (0, 2):
        raise RuntimeError(exists.stderr)
    start = "origin/master"
    if exists.returncode == 0:
        git("fetch", "--no-tags", "origin", "+refs/heads/" + BRANCH + ":refs/remotes/origin/" + BRANCH)
        previous = "refs/remotes/origin/" + BRANCH
        if resume:
            start = previous
        elif not ancestor(previous, "origin/master"):
            return Plan("blocked", tip, commits, "An unmerged automation branch exists without an open PR. It was retained for review; no branch was overwritten.")
    git("checkout", "-B", BRANCH, start)
    before = git("rev-parse", "HEAD").stdout.strip()
    for source in ("origin/master", "refs/remotes/upstream/master"):
        merged = git("merge", "--no-ff", "--no-edit", source, check=False)
        if merged.returncode:
            conflicts = git("diff", "--name-only", "--diff-filter=U").stdout.strip()
            git("merge", "--abort", check=False)
            if not conflicts:
                raise RuntimeError(merged.stderr or merged.stdout)
            return Plan("blocked", tip, commits, "Manual merge conflicts:\n\n" + conflicts)
    return Plan("ready", tip, commits, changed=before != git("rev-parse", "HEAD").stdout.strip())


def api(method, endpoint, payload=None):
    args = ["gh", "api", "--method", method, endpoint]
    if payload is not None:
        args += ["--input", "-"]
    result = subprocess.run(args, input=json.dumps(payload) if payload is not None else None,
                            text=True, encoding="utf-8", capture_output=True, check=True)
    return json.loads(result.stdout) if result.stdout.strip() else None


def report_body(plan):
    # Subjects are data, never shell commands; avoid accidental notification mentions.
    commits = plan.commits.replace("@", "@\u200b")
    return (f"{MARKER}\nUpstream: {UPSTREAM}\n\nUpstream commit: `{plan.upstream}`\n\n"
            f"{plan.reason}\n\nCommits pending from upstream:\n```text\n{commits}\n```\n\n"
            "Review the diff and CI before merging. Core-library pins, native drivers, workflows, "
            "project files and register writers need deliberate compatibility review. A clean Git merge "
            "does not prove firmware behavior. This bot does not merge master or publish releases.")


def attention(repo, plan):
    title = "Upstream sync needs manual resolution"
    page = 1
    while True:
        items = api("GET", f"repos/{repo}/issues?state=open&per_page=100&page={page}")
        for item in items:
            if "pull_request" not in item and MARKER in (item.get("body") or ""):
                body = report_body(plan)
                if item["body"] != body:
                    api("PATCH", f"repos/{repo}/issues/{item['number']}", {"body": body})
                return
        if len(items) < 100:
            break
        page += 1
    api("POST", f"repos/{repo}/issues", {"title": title, "body": report_body(plan)})


def main():
    repo = os.environ["GH_REPO"]
    owner = repo.split("/")[0]
    pulls = api("GET", f"repos/{repo}/pulls?state=open&head={owner}:{BRANCH}&base=master")
    plan = prepare(resume=bool(pulls))
    print(f"Upstream sync: {plan.status}; upstream {plan.upstream}")
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as summary:
            summary.write(f"Upstream sync: **{plan.status}** at `{plan.upstream}`.\n\n{plan.reason}\n")
    if plan.status == "blocked":
        attention(repo, plan)
    elif plan.status == "ready":
        if plan.changed:
            # A normal push preserves any concurrent edits; never force-push a review branch.
            git("push", "origin", "HEAD:refs/heads/" + BRANCH)
        payload = {"title": "Sync upstream through " + plan.upstream[:7], "body": report_body(plan)}
        if pulls:
            if pulls[0]["body"] != payload["body"]:
                api("PATCH", f"repos/{repo}/pulls/{pulls[0]['number']}", payload)
        else:
            api("POST", f"repos/{repo}/pulls", dict(payload, head=BRANCH, base="master"))
        if plan.changed or not pulls:
            # Explicit dispatch runs CI for workflow-created PRs without a separate PAT.
            api("POST", f"repos/{repo}/actions/workflows/ci.yml/dispatches", {"ref": BRANCH})


if __name__ == "__main__":
    main()
