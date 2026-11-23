# Branch protection

`main` is production (Railway deploys it), so it is protected by repository settings, not only by CI. CI checks run on every pull request, but only branch protection makes them mandatory and blocks direct pushes. These settings live in GitHub, not in the repository: an admin applies them once.

## Rules for `main`

| Rule | Why |
|---|---|
| Require a pull request before merging | No direct pushes to production. |
| Require status checks to pass: `repository-rules`, `release-rules`, `build-and-test`, `docker`, `analyze` (CodeQL) | `release-rules` only runs on pull requests to `main`: it checks that the source is `develop`, the version bump, the CHANGELOG release section and that the tag is new. `build-and-test` and `docker` block on vulnerable packages and on HIGH/CRITICAL image vulnerabilities toward `main`. |
| Require branches to be up to date before merging | The checks run on what will actually be deployed. |
| Do not allow force pushes or deletions | The release history and the tags stay intact. |
| Code scanning merge protection: block on CodeQL alerts of severity `high` or higher (Settings → Code security → Code scanning) | CodeQL reports alerts; this setting makes new high or critical ones block the merge. |

## Rules for `develop`

| Rule | Why |
|---|---|
| Require a pull request before merging | Every change goes through review and CI. |
| Require status checks to pass: `repository-rules`, `build-and-test`, `docker` | On `develop`, vulnerable packages and image findings are only warnings, so the release PR is where they must be fixed. |

## Apply them with the GitHub CLI

Run as a repository admin. Review each command before running it, because it replaces the current protection of the branch.

```bash
gh api -X PUT repos/hrefsolutions/Core/branches/main/protection --input - <<'EOF'
{
  "required_status_checks": {
    "strict": true,
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
