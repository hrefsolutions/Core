# Branch protection

`main` is production (Railway deploys it), so it is protected by repository settings, not only by CI. CI checks run on every pull request, but only branch protection makes them mandatory and blocks direct pushes. These settings live in GitHub, not in the repository: the owner (`hrefsolutions`, the only admin of this personal repository) applies them.

**Status:** applied on 2026-11-23 with the commands below. The CodeQL merge protection is still pending in the web UI.

## Rules for `main`

| Rule | Why |
|---|---|
| Require a pull request before merging | No direct pushes to production. |
| Require status checks to pass: `repository-rules`, `release-rules`, `build-and-test`, `docker`, `analyze` (CodeQL) | `release-rules` only runs on pull requests to `main`: it checks that the source is `develop`, the version bump, the CHANGELOG release section and that the tag is new. `build-and-test` and `docker` block on vulnerable packages and on HIGH/CRITICAL image vulnerabilities toward `main`. |
| Do not require branches to be up to date (`strict: false`) | `main` gets a merge commit from every release that `develop` never contains. With `strict`, every release PR would be out of date, and updating it would mean pushing `main` into the protected `develop`. The PR checks already run on the merge result (`refs/pull/N/merge`). |
| Include administrators (`enforce_admins`) | Not even the owner can push straight to production. |
| Do not allow force pushes or deletions | The release history and the tags stay intact. |
| Code scanning merge protection: block on CodeQL alerts of severity `high` or higher (Settings → Code security → Code scanning) | CodeQL reports alerts; this setting makes new high or critical ones block the merge. |

## Rules for `develop`

| Rule | Why |
|---|---|
| Require a pull request before merging | Every change goes through review and CI. |
| Require status checks to pass: `repository-rules`, `build-and-test`, `docker` | On `develop`, vulnerable packages and image findings are only warnings, so the release PR is where they must be fixed. |
| Administrators are not included | The owner can still fix `develop` in an emergency. |

Because `develop` takes no direct pushes, the release commit (`chore: release vX.Y.Z`) goes through a `chore/release-vX.Y.Z` pull request into `develop`, before the `develop` → `main` pull request.

## Apply them with the GitHub CLI

Run as a repository admin. Review each command before running it, because it replaces the current protection of the branch.

```bash
gh api -X PUT repos/hrefsolutions/Core/branches/main/protection --input - <<'EOF'
{
  "required_status_checks": {
    "strict": false,
    "checks": [
      { "context": "repository-rules" },
      { "context": "release-rules" },
      { "context": "build-and-test" },
      { "context": "docker" },
      { "context": "analyze" }
    ]
  },
  "enforce_admins": true,
  "required_pull_request_reviews": { "required_approving_review_count": 0 },
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
EOF
```

```bash
gh api -X PUT repos/hrefsolutions/Core/branches/develop/protection --input - <<'EOF'
{
  "required_status_checks": {
    "strict": false,
    "checks": [
      { "context": "repository-rules" },
      { "context": "build-and-test" },
      { "context": "docker" }
    ]
  },
  "enforce_admins": false,
  "required_pull_request_reviews": { "required_approving_review_count": 0 },
  "restrictions": null,
  "allow_force_pushes": false,
  "allow_deletions": false
}
EOF
```

`release-rules` is skipped (not failed) on pull requests to `develop`, so it must be required only on `main`. Set the CodeQL merge protection in the web UI (Settings → Code security → Code scanning → Protection rules).
