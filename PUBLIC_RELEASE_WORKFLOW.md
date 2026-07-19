# Public release workflow

Normal development is private-only. Push normal branches and pull requests only to the private repository. Do not configure the public repository as a remote in a private checkout, and never manually dual-push a branch.

Public publication follows this controlled path:

1. A maintainer selects an exact, approved full private commit SHA already reachable from private `origin/main`.
2. From the private checkout, run `tools/public-release/Test-PrivateRemotePolicy.ps1`, then run `Export-PublicRelease.ps1 -SourceCommit <40-character-sha> -OutputDirectory <empty-directory>`.
3. The exporter uses `git archive` for that commit, applies the explicit public allowlist and sanitization, generates audit records with the source SHA, and rejects forbidden content.
4. In a separate checkout of `crmaris/hardware-busters-gpu-test-suite`, create a public candidate branch with one root commit from the exported directory. Open a public pull request; do not copy private history.
5. Required public checks, policy review, and maintainer approvals complete before merging the public candidate into public `main`.

`audit/export-manifest.json` is the durable link between a public candidate and its private source SHA. The exporter refuses a real export if its templates are not themselves present at the selected private commit. A `-DryRun` is only a bootstrap validation aid and is not publishable output.

For private checkouts, `Install-PrivatePushGuard.ps1` installs a local pre-push guard that refuses operation when the public repository is configured as a remote.
