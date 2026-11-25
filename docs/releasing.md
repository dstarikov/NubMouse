# Building and releasing

## Continuous integration

[`.github/workflows/build.yml`](../.github/workflows/build.yml) runs on every push to `main`,
on every pull request, and on demand (*Actions → Build → Run workflow*). It runs
[`build.ps1`](../build.ps1) on a Windows runner and uploads `NubMouse.exe` as a workflow
artifact, so any commit's build can be downloaded from its run page.

## Making a release

1. Add an entry for the new version to [`CHANGELOG.md`](../CHANGELOG.md) and commit it.
2. Tag the commit and push the tag:

   ```powershell
   git tag v0.2.0
   ```

   ```powershell
   git push origin v0.2.0
   ```

3. The same workflow sees the `v*` tag, builds the exe with that version stamped into its file
   properties, and creates a GitHub release named after the tag containing:
   - `NubMouse.exe`
   - `NubMouse-<tag>.zip` (the exe plus README, CHANGELOG and LICENSE)
   - `SHA256SUMS.txt`

   Release notes are generated from the commits and pull requests since the previous tag.

Tags with a pre-release suffix (for example `v0.2.0-beta.1`) are published as pre-releases.

If a release build fails, fix the problem, delete the tag (`git push origin :refs/tags/v0.2.0`
and `git tag -d v0.2.0`), and tag again.

## Versions and the updater

Installed copies compare the release tag with their own file version, so tags must start with a
numeric `vX.Y.Z`. The update check uses GitHub's "latest release", which never includes
pre-releases, so pre-release tags are not offered to users automatically. A release must contain
both `NubMouse.exe` and `SHA256SUMS.txt` (the workflow uploads both), or the in-app update
refuses to install it. Update checks only work while the repository is public.
