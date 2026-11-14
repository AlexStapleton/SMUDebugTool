# Upstream synchronization

The daily **Upstream sync** GitHub workflow watches `irusanov/SMUDebugTool:master`.
It can also be run manually from Actions. No personal token is required. Repository
defaults remain read-only; only the sync job receives branch/PR/issue/dispatch permissions.
GitHub's “Allow GitHub Actions to create and approve pull requests” repository setting
must be enabled for PR creation. The bot never approves or merges its own PRs.

If upstream is already an ancestor of master, the run exits without a PR or notification.
Otherwise the bot merges into `automation/upstream-sync`, opens/updates one PR, and
explicitly dispatches CI on that branch. A normal push preserves concurrent reviewer
edits. No force-push, automatic master merge, version bump, tag or release occurs.

Conflicts create/update one issue with the pending commit list and affected files.
The remote branch is not changed on conflict. Resolve the listed files on the open
automation PR, or create a normal integration PR if no automation branch was published.
An unmerged automation branch without an open PR is retained for manual review rather
than overwritten or silently reopening rejected work. Merge a reviewed integration PR
to acknowledge adapted changes; the next run resumes from the resulting ancestry.

Review source pins, driver changes, workflow/project files and tuning semantics even
when compilation succeeds. The application uses a compatibility project and different
core-library location from upstream, so Git merges cannot decide those adaptations.
Tests cover Git merge planning in temporary local repositories; normal CI covers the
resulting application. Hardware behavior remains a separate validation item.

## Reconciliation through fdac4ff

GitHub showed five commits behind because earlier releases adapted their code without
merging their ancestry. A deliberate `ours` strategy merge records the reviewed upstream
tip `fdac4ff33784a07e816bea78f2026508a9eeacc4` while retaining the tested application tree.
It does not claim that each upstream implementation was copied verbatim:

| Commit | Disposition in this fork |
| --- | --- |
| `18e50f0` CCD sizing | Superseded by dynamic CCD/core blocks, content-sized action rows and DPI-aware layouts. Retain our labels and controls instead of copying designer coordinates. |
| `7066cc8` binary core update | Superseded by the pinned, source-built core introduced in v1.48.0. No opaque binary downgrade. |
| `2c55d18` source core, APOB, driver lifecycle and editing UI | Source integration and validated physical target maps adapted in v1.48.0. Explicit owned-driver maintenance intentionally replaces automatic cleanup and notification helpers. Existing CO/CS edit controls and profile workflow are retained instead of upstream baseline/reset visuals. |
| `3c1cd0f` version bump | Superseded by this fork's v1.48.x version sequence. |
| `fdac4ff` Zen 5 P-states and VID | Frequency layout adapted in v1.47.3; separate VID editor in v1.48.0. Per-processor fresh reads, preserved unrelated bits and verified writes replace upstream's combined editor/broadcast behavior. |

References: [upstream tip](https://github.com/irusanov/SMUDebugTool/commit/fdac4ff33784a07e816bea78f2026508a9eeacc4),
[implementation notes](remaining-updates-implementation.md),
[GitHub token event behavior](https://docs.github.com/en/actions/concepts/security/github_token).
