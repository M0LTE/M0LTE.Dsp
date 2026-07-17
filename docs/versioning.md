# Versioning policy

`M0LTE.Dsp` follows [Semantic Versioning 2.0.0](https://semver.org/). The package
version lives in [`Directory.Build.props`](../Directory.Build.props) (`<Version>`).

## The public API is the contract

"The API" means the public surface of the `M0LTE.Dsp` assembly — every public type and
member. That surface is captured, byte-for-byte, in
[`tests/M0LTE.Dsp.Tests/PublicApi.approved.txt`](../tests/M0LTE.Dsp.Tests/PublicApi.approved.txt)
and enforced by `PublicApiTests` (via [PublicApiGenerator](https://github.com/PublicApiGenerator/PublicApiGenerator)).
CI runs this test on every push and pull request, so **the public API cannot change
without the change being made explicit in the diff** — the approval snapshot moves in the
same commit, and this document says which version part must move with it.

## What bumps which part

| Change to the approved snapshot | Version part |
| --- | --- |
| A public member is removed or renamed, a signature changes, or an existing member's contract is narrowed (a **breaking change**) | **MAJOR** |
| Public members are only **added** — existing consumers keep compiling and behaving | **MINOR** |
| The snapshot does **not** change — bug fix, performance, docs, internal refactor, dependency bump | **PATCH** |

A behavioural break with no signature change (e.g. a method that starts throwing where it
used to return) is still a **MAJOR** change even though the snapshot is unchanged — judgement
applies; the snapshot is the floor, not the ceiling.

## Updating the snapshot

When you intend to change the API:

1. Make the change and run `dotnet test`. `PublicApiTests` fails and writes
   `PublicApi.received.txt` next to the approved file.
2. Diff `received` against `approved`. Confirm the delta is exactly what you intended.
3. Replace `PublicApi.approved.txt` with the received content and delete the received file.
4. Bump `<Version>` in `Directory.Build.props` per the table above, in the **same commit**.

## 0.x pre-release

While the version is `0.x`, the API is still settling. Per SemVer §4 anything may change;
in practice we keep the discipline above (breaking → minor, additive/fixes → patch) so the
history stays readable. `1.0.0` will be cut when the surface is declared stable.

## Releasing

Releases are cut by tagging `v<MAJOR>.<MINOR>.<PATCH>` on green `main`. The
[`publish`](../.github/workflows/publish.yml) workflow fires on that tag: it checks the tag
matches `<Version>`, gates on a green build + the full test suite (including the API lock),
packs, pushes the `.nupkg` + `.snupkg` to nuget.org, and opens a GitHub release. Nothing
publishes on an ordinary push.

```sh
# after bumping <Version> and moving PublicApi.approved.txt in the same commit:
git tag v0.1.0 && git push origin v0.1.0
```

Publishing requires a `NUGET_API_KEY` repository secret (a nuget.org API key scoped to push
`M0LTE.Dsp`). Set it once under **Settings ▸ Secrets and variables ▸ Actions**, or with
`gh secret set NUGET_API_KEY`.
