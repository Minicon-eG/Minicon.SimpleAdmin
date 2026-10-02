---
name: release
description: Cut a new release of the Minicon.SimpleAdmin NuGet packages. Bumps the lockstep <Version> across every publishable .csproj, refreshes version strings in CLAUDE.md, drafts release notes from the git log, runs build + full test suite, commits in the repo's German conventional style, then tags vX.Y.Z and pushes the tag to trigger the Trusted-Publishing GitHub Actions workflow (publishes all packages to nuget.org), and finally creates a GitHub Release with the notes. Use when the user says "/release", "cut a release", "release the packages", "bump version and publish", "mach ein Release", "veröffentliche die Pakete", or wants to ship a new version to nuget.org.
---

# Minicon.SimpleAdmin Release

This repository publishes a set of inter-dependent NuGet packages (Shared, Services,
Checkers, HtmlGenerator, Worker, WebUI). A release is driven **entirely by pushing a
`vX.Y.Z` Git tag**: `.github/workflows/publish.yml` then restores, builds (Release),
packs and pushes every `.nupkg` to nuget.org via OIDC Trusted Publishing. There is no
separate publish button — **the tag IS the release**. A GitHub Release object is created
on top purely to hold human-readable notes.

## When to invoke
- User types `/release`, `/release 2.16.0`, "cut a release", "release the packages",
  "bump version and publish", "mach ein Release", "veröffentliche die Pakete".
- After a feature/fix is merged to `master`, build + tests are green, and the user wants
  it shipped to nuget.org.

## Inputs
- **Target version** (optional). If the user gives one (e.g. `2.16.0`), use it. Otherwise
  read the current `<Version>` and propose the next one (patch for fixes, minor for
  features, major for breaking changes) and confirm with the user via AskUserQuestion.

## Preconditions — check first, stop and report if any fails
1. On `master` (`git branch --show-current`). Releases are tags on master.
2. Working tree contains only the intended changes (`git status --short`). If there are
   unrelated modifications, ask before bundling them into the release commit.
3. `gh auth status` is logged in with `repo` + `workflow` scopes.
4. The proposed tag does not already exist (`git tag | grep -x vX.Y.Z`).

## Steps

### 1. Determine current + next version
```bash
grep -m1 "<Version>" src/Minicon.SimpleAdmin.Shared/*.csproj   # current
```
Decide `NEW` = next semantic version. Confirm with the user if not explicitly given.

### 2. Bump versions in lockstep
All publishable projects MUST share one version — mixed versions break consumers because
the packages depend on each other. Replace in every publishable csproj:
```bash
sed -i '' "s|<Version>OLD</Version>|<Version>NEW</Version>|" \
  src/Minicon.SimpleAdmin.HtmlGenerator/*.csproj \
  src/Minicon.SimpleAdmin.Services/*.csproj \
  src/Minicon.SimpleAdmin.Shared/*.csproj \
  src/Minicon.SimpleAdmin.Checkers/*.csproj \
  src/Minicon.SimpleAdmin.Worker/*.csproj \
  src/Minicon.SimpleAdmin.WebUI/*.csproj
grep -rn "<Version>" src/*/*.csproj   # verify all show NEW (the Tests project has none)
```
(Linux runners: `sed -i` without the `''`.)

### 3. Update documentation
**Developer docs:**
- In `CLAUDE.md`, update the "currently `X.Y.Z`" string in the **Versioning** section, and ensure any
  new feature area has a short section with correct `(vX.Y.Z)` header tags.
- If a `CHANGELOG.md` exists, prepend a section for `NEW`; otherwise the GitHub Release notes (step 7)
  are the changelog of record.

**User-facing documentation — MANDATORY GATE.** The release must NOT proceed if a new user-visible
feature, config key, or CLI flag is undocumented. Both user-doc sources ship *inside* the
WebUI/HtmlGenerator packages, so a missing entry means shipping an undocumented feature. For every
operator-visible change since the previous tag, verify it is reflected in ALL applicable:
1. `src/Minicon.SimpleAdmin.WebUI/Views/Documentation/Index.cshtml` — the in-app `/Documentation`
   page (add a TOC `<li>` + a `<section>`).
2. `src/Minicon.SimpleAdmin.HtmlGenerator/Services/Resources/documentation-template.html` — the
   statically generated docs (add a TOC entry + a section).
3. If the feature is operator-configurable: a card in
   `src/Minicon.SimpleAdmin.WebUI/Views/Settings/Features.cshtml` (+ binding in `SettingsController`),
   or an explicit note in the docs that it is `config.json`-only.

To find gaps, diff the config/CLI surface since the previous tag and grep the two doc files for the
new keys/flags:
```bash
PREV=$(git tag --sort=-creatordate | grep '^v' | head -1)
git diff ${PREV}..HEAD -- 'src/**/Models/Config/*.cs' 'src/Minicon.SimpleAdmin.Worker/*.cs'   # new keys/flags
grep -rl "<new-key-or-flag>" src/Minicon.SimpleAdmin.WebUI/Views/Documentation \
  src/Minicon.SimpleAdmin.HtmlGenerator/Services/Resources/documentation-template.html
```
Any new key/flag with no doc hit is a release blocker — document it first.

### 4. Build + test — must be green before committing
```bash
dotnet build Minicon.SimpleAdmin.slnx
dotnet test src/Minicon.SimpleAdmin.Tests/Minicon.SimpleAdmin.Tests.csproj
```
Stop and report if either fails. Never tag a red build.

### 5. Commit (repo German conventional style, no emojis)
Message form: `<Topic> (vX.Y.Z)` with a German summary body. End with the trailer:
```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```
```bash
git add -A
git commit -m "<deutsche Zusammenfassung> (vNEW)" -m "<details>" -m "Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
git push origin master
```

### 6. Draft release notes
Summarize from the diff since the previous tag:
```bash
PREV=$(git tag --sort=-creatordate | grep '^v' | head -1)
git log --no-merges --pretty="- %s" ${PREV}..HEAD
```
Group into **Neu / Geändert / Behoben**, in German. Lead with a one-line headline that
matches the commit topic.

### 7. Tag and create the GitHub Release
Pushing the tag triggers the nuget.org publish — this is a **public, irreversible** action.
Confirm the user still wants to publish before pushing the tag.
```bash
git tag vNEW
git push origin vNEW                       # <-- triggers .github/workflows/publish.yml
gh release create vNEW --title "vNEW — <Headline>" --notes-file <notes>   # tag already exists
```
(`gh release create` on an existing tag only attaches the notes; it does not re-create the tag.)

### 8. Verify the publish workflow
```bash
gh run list --workflow=publish.yml --limit 1
gh run watch <run-id>     # optional: follow to completion
```
Report the run URL and outcome to the user. If the workflow fails (e.g. OIDC/login),
the tag and GitHub Release already exist — fix forward and re-run the workflow with
`gh workflow run publish.yml` or `gh run rerun <run-id>` rather than re-tagging.

## Notes & guardrails
- **Runners**: `publish.yml` runs on **GitHub-hosted** runners (`ubuntu-latest`). The repository is
  public — never switch it to self-hosted runners (untrusted code from forks must not reach them).
- **Lockstep versions** are non-negotiable — a single mismatched `<Version>` breaks
  consumer restore. Always `grep -rn "<Version>" src/*/*.csproj` after bumping.
- `GeneratePackageOnBuild=true` means a Release build alone emits `.nupkg`; the workflow
  uses `dotnet pack ... --no-build`.
- Downstream consumers only pick up changes
  after the new packages are live on nuget.org.
- Do not delete/move a published tag to "fix" a release — bump to the next patch instead;
  nuget.org versions are immutable.
