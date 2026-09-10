# Linux Distribution Corrections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce the next WebAssistant Linux candidate with a same-name single ZIP root, ALT Linux 10.1-compatible runtime dependency detection, and only dependency-graph-safe Linux platform cleanup, without changing scanner API semantics.

**Architecture:** Keep packaging, installation dependency resolution, and platform-purity as independently reviewable units. Packaging creates the final archive topology before hashing; installation uses capability probes with package resolution only for missing capabilities; platform-purity is attempted only at the MSBuild/NuGet boundary and is deferred if it requires scanner/API redesign. VERSION changes exactly once only after all accepted implementation work is GREEN.

**Tech Stack:** Bash, .NET 10 / C#, xUnit, MSBuild/NuGet, GitHub Actions, repo-guard, ALT Linux apt-rpm/systemd/SANE.

**Spec:** `docs/superpowers/specs/2026-09-10-linux-distribution-corrections-design.md`

## Global Constraints

- Base accepted source is `bcfc06bb207d5f0f9f58436e8e73d76f26ab9e9b`, `webassist/VERSION = 0.3.20`.
- Existing unpublished `v0.3.20` Draft bytes are abandoned and MUST NOT be mutated, overwritten, repacked, or published.
- Accepted semantic authority remains `webassistant-contract/v0.2` + `webassistant-conformance/v0.2`; do not modify them.
- Do not modify `repo-policy.json` or `webassist/src/WebAssistant/Scanning/**`.
- Do not redesign scanner API, scanner identifiers, settings schema, or TWAIN/WIA behavior in this transaction.
- Linux ZIP basename is `WebAssistant-linux-x64-<VERSION>` and every archive entry must live under exactly that one top-level directory.
- Do not replace `libicu74` with another hard-coded ICU major such as `libicu69`.
- No post-publish manual DLL pruning and no post-hash archive mutation.
- #188 cleanup is accepted only if Windows and Linux builds, Linux startup, and Linux scanner path remain GREEN without scanner/API changes; otherwise #188 remains open and is documented as deferred.
- `webassist/VERSION` advances exactly once after implementation/tests are otherwise complete.
- Final candidate must preserve producer-build-once -> exact-byte acceptance -> immutable Draft staging.

---

### Task 1: Establish the combined implementation branch and Draft PR

**Files:**
- Existing design: `docs/superpowers/specs/2026-09-10-linux-distribution-corrections-design.md`
- Existing plan: `docs/superpowers/plans/2026-09-10-linux-distribution-corrections.md`
- No production changes in this task.

**Interfaces:**
- Consumes: accepted main `bcfc06bb207d5f0f9f58436e8e73d76f26ab9e9b` plus approved design/plan commits.
- Produces: one feature branch and one Draft PR linked to #186/#187 and referencing #188 without auto-closing it unless cleanup is ultimately proven complete.

- [ ] **Step 1: Create the implementation branch from the plan commit**

Create:

```text
feature/186-188-linux-distribution-corrections
```

from the exact plan commit so the approved spec and plan travel with implementation.

- [ ] **Step 2: Open a Draft PR**

PR title:

```text
Fix Linux distribution layout and ALT 10.1 dependencies
```

PR body must include:

```text
Closes #186
Closes #187
Refs #188
Parent #157
```

and a repo-guard ChangeIntent limiting the initial implementation surface to packaging/install/tests/docs/VERSION plus `WebAssistant.csproj`/`Program.cs` only if #188 reaches its clean branch.

- [ ] **Step 3: Verify no source mutation occurred before RED**

Read `webassist/VERSION`; expected:

```text
0.3.20
```

Confirm `main` remains `bcfc06bb207d5f0f9f58436e8e73d76f26ab9e9b` and Draft `v0.3.20` is not edited.

---

### Task 2: #186 — RED for canonical single-root Linux ZIP topology

**Files:**
- Modify: `tests/core/LinuxPackagingAutomaticBootstrapTests.cs`
- Modify: `tests/core/InstallerArtifactContractTests.cs`
- Later production: `webassist/build/linux/package.sh`

**Interfaces:**
- Consumes: canonical artifact name `WebAssistant-linux-x64-<VERSION>.zip`.
- Produces: executable regression evidence that inspects actual ZIP entries from `package.sh` and requires a single top-level directory equal to the ZIP basename.

- [ ] **Step 1: Extend the existing fake-publish package test to inspect the generated ZIP**

After `package.sh` succeeds in `LinuxPackage_WithoutSdk_AutomaticallyBootstrapsDotnet10ByDefault`, locate the only canonical ZIP in the output directory and add assertions equivalent to:

```csharp
var version = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "webassist", "VERSION")).Trim();
var rootName = $"WebAssistant-linux-x64-{version}";
var zipPath = Path.Combine(output, rootName + ".zip");
Assert.True(File.Exists(zipPath));

using var archive = ZipFile.OpenRead(zipPath);
var entries = archive.Entries.Select(entry => entry.FullName).ToArray();
Assert.NotEmpty(entries);
Assert.All(entries, entry => Assert.StartsWith(rootName + "/", entry, StringComparison.Ordinal));
Assert.Contains(rootName + "/install.sh", entries);
Assert.Contains(rootName + "/uninstall.sh", entries);
Assert.Contains(rootName + "/VERSION", entries);
Assert.Contains(rootName + "/webassist.service", entries);
Assert.Contains(rootName + "/app/WebAssistant", entries);
Assert.DoesNotContain(entries, entry => entry.StartsWith("/", StringComparison.Ordinal));
Assert.DoesNotContain(entries, entry => entry.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains(".."));
Assert.Single(entries.Select(entry => entry.Split('/', StringSplitOptions.RemoveEmptyEntries)[0]).Distinct(StringComparer.Ordinal));
```

Add `using System.IO.Compression;` at the top.

- [ ] **Step 2: Add static contract coverage for the staging model**

In `InstallerArtifactContractTests.cs`, add:

```csharp
[Fact]
public void LinuxProducer_StagesArchiveBasenameAsSingleRootBeforeCompression()
{
    var version = ReadRequired("webassist/VERSION").Trim();
    var linux = ReadRequired("webassist/build/linux/package.sh");
    Assert.Contains("package_root=\"$staging_root/WebAssistant-linux-x64-${version}\"", linux, StringComparison.Ordinal);
    Assert.Contains("cd -- \"$staging_root\"", linux, StringComparison.Ordinal);
    Assert.Contains("zip -q -r \"$artifact_path\" \"WebAssistant-linux-x64-${version}\"", linux, StringComparison.Ordinal);
}
```

This static test intentionally fails on the current `package_root="$staging_root/package"` implementation.

- [ ] **Step 3: Commit tests only**

Commit message:

```text
test(linux): require same-name single-root ZIP layout RED
```

Do not edit `package.sh` or VERSION yet.

- [ ] **Step 4: Run/observe exact RED**

Run through CI core tests on the exact tests-only head. Expected failures must be the new ZIP-root assertions; no compile/syntax failure counts as valid RED.

---

### Task 3: #186 — GREEN canonical ZIP staging before compression

**Files:**
- Modify: `webassist/build/linux/package.sh`
- Test: `tests/core/LinuxPackagingAutomaticBootstrapTests.cs`
- Test: `tests/core/InstallerArtifactContractTests.cs`

**Interfaces:**
- Consumes: `artifact_name="WebAssistant-linux-x64-${version}.zip"`.
- Produces: archive whose only top-level entry is `${artifact_name%.zip}/`.

- [ ] **Step 1: Replace the generic package staging directory**

Change:

```bash
package_root="$staging_root/package"
```

to:

```bash
package_root_name="${artifact_name%.zip}"
package_root="$staging_root/$package_root_name"
```

Keep `app_directory="$package_root/app"` unchanged.

- [ ] **Step 2: Compress from the staging parent**

Replace:

```bash
(
    cd -- "$package_root"
    zip -q -r "$artifact_path" .
)
```

with:

```bash
(
    cd -- "$staging_root"
    zip -q -r "$artifact_path" "$package_root_name"
)
```

Do not touch the artifact after provenance/checksum generation.

- [ ] **Step 3: Run the focused tests**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter "FullyQualifiedName~LinuxPackagingAutomaticBootstrapTests|FullyQualifiedName~InstallerArtifactContractTests"
```

Expected: PASS.

- [ ] **Step 4: Commit #186 GREEN**

Commit message:

```text
fix(linux): package under canonical same-name ZIP root
```

---

### Task 4: #187 — isolate and RED-test ALT runtime capability resolution

**Files:**
- Create: `webassist/install/linux/runtime-dependencies.sh`
- Create: `tests/core/LinuxRuntimeDependencyTests.cs`
- Modify later: `webassist/install/linux/install.sh`

**Interfaces:**
- Consumes: host commands `ldconfig`, `scanimage`, `apt-cache`, `apt-get` and package/runtime state.
- Produces: shell function `ensure_webassistant_runtime_dependencies` returning 0 only when ICU, GTK3, libsane and scanimage capabilities are present after optional resolution.

- [ ] **Step 1: Add tests before creating the helper**

Create `LinuxRuntimeDependencyTests.cs` with a temporary fake `PATH`. Tests invoke:

```bash
bash -c 'source "$RUNTIME_HELPER"; ensure_webassistant_runtime_dependencies'
```

Required cases:

```text
ALT10_1_Icu69AndExistingGtkSane_SucceedsWithoutAptMutation
MissingIcu_ResolvesAvailableMajorFromAptCacheWithoutHardCodedMajor
MissingCapability_WithNoResolvablePackage_FailsActionably
RuntimeDependencyContract_ContainsNoHardCodedLibicuMajor
```

For the ALT 10.1 fixture, fake `ldconfig -p` must print:

```text
libicuuc.so.69
libicui18n.so.69
libicudata.so.69
libgtk-3.so.0
libsane.so.1
```

and fake `scanimage` must exist. Fake `apt-get` writes a marker and exits non-zero; the test asserts the marker was never created.

For missing ICU, fake `apt-cache pkgnames` prints:

```text
libicu69
libicu-data
```

The helper must choose `libicu69`, call `apt-get update`, then `apt-get install -y libicu69`, and re-probe capabilities. The fake `ldconfig` switches from missing to present after the install marker exists.

For unresolvable ICU, fake `apt-cache pkgnames` prints no `libicu[0-9]+`; expected non-zero exit with stderr containing `ICU` and `не найдена` or equivalent actionable text.

- [ ] **Step 2: Add a current-install regression assertion**

Update `SystemServiceProductTests.LinuxPackage_ContainsHardenedSystemdLifecycleSurface` by replacing the old positive assertion:

```csharp
Assert.Contains("libicu74", install, StringComparison.OrdinalIgnoreCase);
```

with:

```csharp
Assert.DoesNotContain("libicu74", install, StringComparison.OrdinalIgnoreCase);
Assert.Contains("runtime-dependencies.sh", install, StringComparison.OrdinalIgnoreCase);
Assert.Contains("ensure_webassistant_runtime_dependencies", install, StringComparison.Ordinal);
```

- [ ] **Step 3: Commit tests only and observe RED**

Commit message:

```text
test(alt): require capability-based runtime dependencies RED
```

Expected RED: helper missing plus old `libicu74` contract still present. No production edit and no VERSION bump.

---

### Task 5: #187 — GREEN capability-oriented ALT dependency helper

**Files:**
- Create: `webassist/install/linux/runtime-dependencies.sh`
- Modify: `webassist/install/linux/install.sh`
- Modify: `webassist/build/linux/package.sh`
- Test: `tests/core/LinuxRuntimeDependencyTests.cs`
- Test: `tests/core/SystemServiceProductTests.cs`

**Interfaces:**
- `runtime-dependencies.sh` exports `ensure_webassistant_runtime_dependencies`.
- `install.sh` sources `runtime-dependencies.sh` from its own directory and calls the function before user/service installation.
- `package.sh` includes `runtime-dependencies.sh` inside the canonical ZIP root next to `install.sh`.

- [ ] **Step 1: Implement capability probes**

Create `runtime-dependencies.sh` with:

```bash
#!/usr/bin/env bash
set -euo pipefail

webassistant_has_icu() {
    ldconfig -p 2>/dev/null | grep -Eq 'libicuuc\.so\.[0-9]+' &&
    ldconfig -p 2>/dev/null | grep -Eq 'libicui18n\.so\.[0-9]+' &&
    ldconfig -p 2>/dev/null | grep -Eq 'libicudata\.so\.[0-9]+'
}

webassistant_has_gtk3() {
    ldconfig -p 2>/dev/null | grep -q 'libgtk-3\.so\.0'
}

webassistant_has_libsane() {
    ldconfig -p 2>/dev/null | grep -q 'libsane\.so\.1'
}

webassistant_has_scanimage() {
    command -v scanimage >/dev/null 2>&1
}
```

Do not encode any `libicuNN` major in capability checks.

- [ ] **Step 2: Implement deterministic ICU package discovery**

Add:

```bash
webassistant_resolve_icu_package() {
    apt-cache pkgnames 2>/dev/null |
        sed -n -E 's/^(libicu([0-9]+))$/\2 \1/p' |
        sort -nr |
        awk 'NR == 1 { print $2 }'
}
```

If no package is returned, fail with an ICU-specific actionable message rather than guessing a major.

- [ ] **Step 3: Map other missing capabilities to stable ALT package names**

Use the exact mapping:

```text
GTK3 shared library -> libgtk+3
libsane shared library -> libsane
scanimage command -> sane
```

Build an array only for capabilities that are actually missing. ICU contributes the dynamically resolved `libicuNN` package name.

- [ ] **Step 4: Install only missing packages and re-probe**

The helper must:

```bash
apt-get update
apt-get install -y "${missing_packages[@]}"
```

only when `missing_packages` is non-empty, then re-run all four probes. If any capability remains absent, exit non-zero with the missing capability names.

- [ ] **Step 5: Wire helper into `install.sh`**

Remove:

```bash
runtime_packages=(libicu74 libgtk+3 libsane sane)
runtime_dependencies_installed() { ... }
```

and the old package-loop block. Add near the path definitions:

```bash
runtime_helper="$script_dir/runtime-dependencies.sh"
[[ -f "$runtime_helper" ]] || {
    echo "Package повреждён: отсутствует runtime-dependencies.sh" >&2
    exit 1
}
# shellcheck source=/dev/null
source "$runtime_helper"
ensure_webassistant_runtime_dependencies
```

Keep root validation before package-manager mutation.

- [ ] **Step 6: Package the helper**

In `package.sh`, add:

```bash
cp -- "$install_root/runtime-dependencies.sh" "$package_root/runtime-dependencies.sh"
chmod +x -- "$package_root/runtime-dependencies.sh"
```

and add it to package integrity assertions/tests.

- [ ] **Step 7: Run focused tests**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter "FullyQualifiedName~LinuxRuntimeDependencyTests|FullyQualifiedName~SystemServiceProductTests|FullyQualifiedName~LinuxPackagingAutomaticBootstrapTests"
```

Expected: PASS.

- [ ] **Step 8: Commit #187 GREEN**

Commit message:

```text
fix(alt): resolve Linux runtime dependencies by capability
```

---

### Task 6: #188 — RED audit of Linux publish dependency graph

**Files:**
- Create: `tests/core/LinuxPublishPlatformPurityTests.cs`
- Potential later modifications: `webassist/src/WebAssistant/WebAssistant.csproj`, `webassist/src/WebAssistant/Program.cs`

**Interfaces:**
- Consumes: actual `dotnet publish -r linux-x64 --self-contained true` output and `WebAssistant.deps.json`.
- Produces: evidence distinguishing Windows native payload (forbidden) from Windows-oriented managed assemblies (cleanup candidate).

- [ ] **Step 1: Add an actual Linux publish inventory test**

On Linux only, run canonical project publish into a temporary directory using the available .NET 10 SDK and assert first that native Windows payload never appears:

```csharp
Assert.DoesNotContain(files, path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
Assert.DoesNotContain(files, path => path.Contains("runtimes/win-", StringComparison.OrdinalIgnoreCase));
```

Then add RED assertions for the two direct Windows-only package outputs currently caused by unconditional references:

```csharp
Assert.DoesNotContain(files, path => path.EndsWith("Microsoft.Extensions.Hosting.WindowsServices.dll", StringComparison.OrdinalIgnoreCase));
Assert.DoesNotContain(files, path => path.EndsWith("NAPS2.Sdk.Worker.Win32.dll", StringComparison.OrdinalIgnoreCase));
```

Do not initially ban `NAPS2.Wia.dll`, `WindowsBase.dll`, `NTwain.dll`, or all `Microsoft.Win32.*` files by name; those may be transitive/cross-platform compile assets and need separate reachability evidence.

- [ ] **Step 2: Commit audit test only and observe RED**

Commit message:

```text
test(linux): expose direct Windows runtime dependencies RED
```

Expected: the two direct Windows-oriented assemblies are present in current linux-x64 publish; native-Windows assertions remain GREEN.

---

### Task 7: #188 — attempt the narrow dependency-graph cleanup, with an explicit stop branch

**Files:**
- Potentially modify: `webassist/src/WebAssistant/WebAssistant.csproj`
- Potentially modify: `webassist/src/WebAssistant/Program.cs`
- Test: `tests/core/LinuxPublishPlatformPurityTests.cs`
- Test: existing Windows/Linux build and scanner suites.

**Interfaces:**
- Consumes: RED evidence from Task 6.
- Produces one of two explicit outcomes:
  - **Outcome A:** safe dependency-graph cleanup merged into this transaction and #188 can close;
  - **Outcome B:** no production cleanup; RED audit assertions specific to managed cleanup are reverted/converted to documented inventory evidence, #188 remains open, and #186/#187 continue unblocked.

- [ ] **Step 1: Try MSBuild runtime-asset conditioning without scanner changes**

First attempt only this narrow project-level change:

```xml
<ItemGroup Condition="'$(RuntimeIdentifier)' == 'linux-x64'">
  <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.11" ExcludeAssets="runtime" />
  <PackageReference Include="NAPS2.Sdk.Worker.Win32" Version="1.3.0" ExcludeAssets="runtime" />
</ItemGroup>
<ItemGroup Condition="'$(RuntimeIdentifier)' != 'linux-x64'">
  <PackageReference Include="Microsoft.Extensions.Hosting.WindowsServices" Version="10.0.11" />
  <PackageReference Include="NAPS2.Sdk.Worker.Win32" Version="1.3.0" />
</ItemGroup>
```

Keep `Systemd`, fixed SDK, GDI and GTK references unchanged initially. Do not edit `Scanning/**`.

- [ ] **Step 2: Run Linux publish/startup and Windows compile tests immediately**

Run:

```bash
dotnet publish webassist/src/WebAssistant/WebAssistant.csproj -c Release -r linux-x64 --self-contained true -o /tmp/webassistant-linux-purity
/tmp/webassistant-linux-purity/WebAssistant --urls http://127.0.0.1:0
```

and the repository's existing core/Windows scanner/build tests through CI. The process only needs to reach successful host startup; stop it after the startup evidence is observed.

- [ ] **Step 3A: If runtime asset exclusion succeeds cleanly, keep it**

Conditions for Outcome A are all mandatory:

```text
Linux publish succeeds
both direct Windows runtime assemblies are absent
Linux WebAssistant startup succeeds
Linux scanner tests remain GREEN
Windows compile/scanner/service tests remain GREEN
no Scanning/** or API changes were needed
```

If all hold, keep the project conditioning and let the Task 6 RED assertions become GREEN. Commit:

```text
fix(linux): exclude direct Windows-only runtime assets
```

Then #188 can be closed by the eventual PR.

- [ ] **Step 3B: If any condition fails, revert only the #188 production experiment**

Return `WebAssistant.csproj`/`Program.cs` to their pre-Task-7 state. Change `LinuxPublishPlatformPurityTests` so it permanently enforces only the already-proven native boundary:

```csharp
Assert.DoesNotContain(files, path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
Assert.DoesNotContain(files, path => path.Contains("runtimes/win-", StringComparison.OrdinalIgnoreCase));
```

and records the managed-assembly cleanup as deferred evidence in an issue comment on #188. Do not weaken #186/#187 tests. Commit the audit evidence as:

```text
test(linux): preserve native platform-purity boundary
```

#188 stays OPEN and the PR body remains `Refs #188`, not `Closes #188`.

---

### Task 8: Full pre-version regression and exact VERSION transition

**Files:**
- Modify: `webassist/VERSION`
- Update test fixtures only if they intentionally embed the current product version.
- No semantic contract/policy files.

**Interfaces:**
- Consumes: GREEN #186/#187 and whichever explicit #188 outcome was accepted.
- Produces: one monotonic product transition `0.3.20 -> 0.3.21`.

- [ ] **Step 1: Run core tests before VERSION bump**

Run:

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj
```

Expected: all tests GREEN except any governance rule that intentionally requires accepted-transition VERSION monotonicity only at final PR head.

- [ ] **Step 2: Confirm generated Linux ZIP topology with a real .NET 10 publish**

Run canonical producer:

```bash
WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=0 webassist/build/linux/package.sh /tmp/webassistant-linux-artifacts
unzip -Z1 /tmp/webassistant-linux-artifacts/WebAssistant-linux-x64-0.3.20.zip
```

Expected: every entry starts with `WebAssistant-linux-x64-0.3.20/` and exactly one top-level directory exists.

- [ ] **Step 3: Bump VERSION exactly once**

Change only:

```text
0.3.20
```

to:

```text
0.3.21
```

Update version-pinned release test fixtures from `0.3.20/0.3.19` to `0.3.21/0.3.20` only where they model the current transition; do not change historical evidence unnecessarily.

- [ ] **Step 4: Commit final product transition**

Commit message:

```text
chore(version): advance WebAssistant to 0.3.21
```

No subsequent implementation commit may change VERSION again in this PR.

---

### Task 9: Ready/full distribution acceptance and merge

**Files:**
- No new implementation files unless a failing acceptance test proves a defect.
- PR metadata may be updated without moving head.

**Interfaces:**
- Consumes: exact final feature head with VERSION 0.3.21.
- Produces: exact accepted merge transition eligible for frozen-main resolver.

- [ ] **Step 1: Run Draft fast feedback**

Require exact-head core and repo-guard evidence. Any failure must be diagnosed before Ready; no no-op commits solely to retrigger metadata.

- [ ] **Step 2: Update PR ChangeIntent to actual final scope**

If #188 Outcome A succeeded, PR body may use `Closes #188`; otherwise keep `Refs #188` and comment the defer reason in #188. Budgets must reflect exact final diff rather than relaxing unrelated policy.

- [ ] **Step 3: Mark PR Ready without changing SHA**

Ready must trigger the full distribution-sensitive CI classification.

- [ ] **Step 4: Require full exact-head GREEN**

Verify all applicable gates:

```text
core
repo-guard
canonical Windows installer producer
canonical Linux installer producer
Windows exact-byte installer lifecycle
Linux exact-byte installer lifecycle
scanner smoke/final regression matrix
ci-required
```

For the Linux producer artifact, inspect ZIP entries and platform inventory from the produced bytes; do not rebuild for inspection.

- [ ] **Step 5: Merge with exact-head guard**

Use normal merge commit, not direct push/squash, preserving resolver identity:

```text
main merge SHA -> exact accepted PR head
```

Verify post-merge main, VERSION 0.3.21, auto-close #186/#187, and #188 state according to Outcome A/B.

---

### Task 10: New frozen candidate and real ALT Linux 10.1 evidence

**Files:**
- No repository source mutation after freeze.
- External evidence only until final PDF generation.

**Interfaces:**
- Consumes: exact post-merge main with VERSION 0.3.21.
- Produces: fresh unpublished Draft candidate built once from that SHA and target evidence on real ALT Workstation 10.1.

- [ ] **Step 1: Freeze exact post-merge main**

Verify:

```text
open repository-side PRs = 0
current authority = v0.2
v0.3 remains candidate/accepted=false
no conflicting v0.3.21 Release/tag
```

Abandoned `v0.3.20` Draft remains untouched.

- [ ] **Step 2: Dispatch `release-candidate.yml` once for exact frozen SHA**

Input:

```text
source_sha=<exact new main merge SHA>
```

Require resolver -> producers -> same-byte acceptance -> Draft staging SUCCESS.

- [ ] **Step 3: Verify staged Linux artifact structure without rebuilding**

Download the exact staged `WebAssistant-linux-x64-0.3.21.zip` and verify:

```text
single top-level directory = WebAssistant-linux-x64-0.3.21
no direct root payload
no traversal/absolute entries
```

Verify SHA/provenance against Draft metadata.

- [ ] **Step 4: Retest exact staged bytes on real ALT Workstation 10.1**

User flow becomes:

```bash
unzip WebAssistant-linux-x64-0.3.21.zip
cd WebAssistant-linux-x64-0.3.21
sudo ./install.sh
```

Expected:

```text
no libicu74 lookup
existing ICU 69 accepted
no unnecessary apt mutation when all capabilities are present
systemd service active
/v1/health responds
/v1/scanners responds
restart succeeds
uninstall succeeds
```

Use these results as final ALT 10.1 evidence for the installation guide; do not modify source or installer bytes after this point.
