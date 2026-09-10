# Linux Distribution Corrections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Produce the next WebAssistant Linux candidate with a same-name single ZIP root, ALT Linux 10.1-compatible runtime dependency detection, and only dependency-graph-safe Linux platform cleanup, without changing scanner API semantics.

**Architecture:** Keep packaging, installation dependency resolution, and platform-purity as independently reviewable units. Packaging creates the final archive topology before hashing; installation uses capability probes with package resolution only for missing capabilities; platform-purity is attempted only at the MSBuild/NuGet/service-host boundary and is deferred if it requires scanner/API redesign. VERSION changes exactly once only after all accepted implementation work is GREEN.

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

Create `feature/186-188-linux-distribution-corrections` from the exact plan commit so the approved spec and plan travel with implementation.

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

Read `webassist/VERSION`; expected `0.3.20`. Confirm `main` remains `bcfc06bb207d5f0f9f58436e8e73d76f26ab9e9b` and Draft `v0.3.20` is not edited.

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

Add `using System.IO.Compression;`. After `package.sh` succeeds in `LinuxPackage_WithoutSdk_AutomaticallyBootstrapsDotnet10ByDefault`, add:

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
Assert.Single(entries
    .Where(entry => entry.Length > 0)
    .Select(entry => entry.Split('/', StringSplitOptions.RemoveEmptyEntries)[0])
    .Distinct(StringComparer.Ordinal));
```

- [ ] **Step 2: Add static contract coverage matching the intended staging implementation**

In `InstallerArtifactContractTests.cs`, add:

```csharp
[Fact]
public void LinuxProducer_StagesArchiveBasenameAsSingleRootBeforeCompression()
{
    var linux = ReadRequired("webassist/build/linux/package.sh");
    Assert.Contains("package_root_name=\"${artifact_name%.zip}\"", linux, StringComparison.Ordinal);
    Assert.Contains("package_root=\"$staging_root/$package_root_name\"", linux, StringComparison.Ordinal);
    Assert.Contains("cd -- \"$staging_root\"", linux, StringComparison.Ordinal);
    Assert.Contains("zip -q -r \"$artifact_path\" \"$package_root_name\"", linux, StringComparison.Ordinal);
}
```

This intentionally fails on current `package_root="$staging_root/package"` and `zip ... .` behavior.

- [ ] **Step 3: Commit tests only**

Commit message:

```text
test(linux): require same-name single-root ZIP layout RED
```

Do not edit `package.sh` or VERSION yet.

- [ ] **Step 4: Observe exact RED**

Use CI core tests on the exact tests-only head. Expected failures are only the new ZIP-root assertions. Compile/syntax errors do not count as valid RED.

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

Replace:

```bash
package_root="$staging_root/package"
```

with:

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

- [ ] **Step 3: Run focused tests**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter "FullyQualifiedName~LinuxPackagingAutomaticBootstrapTests|FullyQualifiedName~InstallerArtifactContractTests"
```

Expected: PASS.

- [ ] **Step 4: Commit #186 GREEN**

```text
fix(linux): package under canonical same-name ZIP root
```

---

### Task 4: #187 — RED-test ALT runtime capability resolution

**Files:**
- Create tests: `tests/core/LinuxRuntimeDependencyTests.cs`
- Modify tests: `tests/core/SystemServiceProductTests.cs`
- Later create: `webassist/install/linux/runtime-dependencies.sh`
- Later modify: `webassist/install/linux/install.sh`

**Interfaces:**
- Consumes: host commands `ldconfig`, `scanimage`, `apt-cache`, `apt-get` and runtime state.
- Produces after GREEN: shell function `ensure_webassistant_runtime_dependencies` returning 0 only when ICU, GTK3, libsane and scanimage capabilities are present after optional resolution.

- [ ] **Step 1: Add behavioral tests before creating the helper**

Create `LinuxRuntimeDependencyTests.cs` with a temporary fake `PATH`. Invoke:

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

ALT 10.1 fixture fake `ldconfig -p` output:

```text
libicuuc.so.69
libicui18n.so.69
libicudata.so.69
libgtk-3.so.0
libsane.so.1
```

and fake `scanimage` exists. Fake `apt-get` writes a marker then exits non-zero. Assert the marker is absent when all capabilities already exist.

Missing-ICU fixture: fake `apt-cache pkgnames` prints:

```text
libicu69
libicu-data
```

Expect `apt-get update`, then `apt-get install -y libicu69`; fake `ldconfig` switches to ICU-present after that install marker.

Unresolvable-ICU fixture: `apt-cache pkgnames` has no `libicu[0-9]+`; expect non-zero and stderr naming the ICU capability and inability to resolve a package.

- [ ] **Step 2: Update the existing system-service contract to the new helper boundary**

In `SystemServiceProductTests.LinuxPackage_ContainsHardenedSystemdLifecycleSurface`, add:

```csharp
var runtimeDependenciesPath = Path.Combine(product, "install", "linux", "runtime-dependencies.sh");
Assert.True(File.Exists(runtimeDependenciesPath));
```

and after reading `install.sh`:

```csharp
var runtimeDependencies = File.ReadAllText(runtimeDependenciesPath);
Assert.Contains("runtime-dependencies.sh", install, StringComparison.OrdinalIgnoreCase);
Assert.Contains("ensure_webassistant_runtime_dependencies", install, StringComparison.Ordinal);
Assert.DoesNotContain("libicu74", install, StringComparison.OrdinalIgnoreCase);
Assert.Contains("apt-get", runtimeDependencies, StringComparison.OrdinalIgnoreCase);
Assert.DoesNotContain("libicu74", runtimeDependencies, StringComparison.OrdinalIgnoreCase);
```

Remove the old assertions requiring `apt-get` and `libicu74` directly inside `install.sh`; package-manager ownership moves to the helper.

- [ ] **Step 3: Commit tests only and observe RED**

```text
test(alt): require capability-based runtime dependencies RED
```

Expected RED: helper missing and old `libicu74` contract present. No production edit and no VERSION bump.

---

### Task 5: #187 — GREEN capability-oriented ALT dependency helper

**Files:**
- Create: `webassist/install/linux/runtime-dependencies.sh`
- Modify: `webassist/install/linux/install.sh`
- Modify: `webassist/build/linux/package.sh`
- Test: `tests/core/LinuxRuntimeDependencyTests.cs`
- Test: `tests/core/SystemServiceProductTests.cs`
- Test: `tests/core/LinuxPackagingAutomaticBootstrapTests.cs`

**Interfaces:**
- `runtime-dependencies.sh` exports `ensure_webassistant_runtime_dependencies`.
- `install.sh` sources it from its own package directory and invokes it before user/service installation.
- `package.sh` includes it next to `install.sh` under the canonical ZIP root.

- [ ] **Step 1: Implement capability probes**

Create:

```bash
#!/usr/bin/env bash
set -euo pipefail

webassistant_has_icu() {
    local cache
    cache="$(ldconfig -p 2>/dev/null || true)"
    grep -Eq 'libicuuc\.so\.[0-9]+' <<<"$cache" &&
    grep -Eq 'libicui18n\.so\.[0-9]+' <<<"$cache" &&
    grep -Eq 'libicudata\.so\.[0-9]+' <<<"$cache"
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

Do not encode any ICU major.

- [ ] **Step 2: Implement deterministic ICU package discovery in Bash only**

Use `apt-cache pkgnames`, selecting the numerically greatest exact `libicuNN` package without `sed`/`sort` dependencies:

```bash
webassistant_resolve_icu_package() {
    local package best_package="" best_major=-1
    while IFS= read -r package; do
        if [[ "$package" =~ ^libicu([0-9]+)$ ]]; then
            local major="${BASH_REMATCH[1]}"
            if (( major > best_major )); then
                best_major="$major"
                best_package="$package"
            fi
        fi
    done < <(apt-cache pkgnames 2>/dev/null || true)
    [[ -n "$best_package" ]] || return 1
    printf '%s\n' "$best_package"
}
```

- [ ] **Step 3: Map other missing capabilities to stable ALT package names**

Exact mapping:

```text
GTK3 shared library -> libgtk+3
libsane shared library -> libsane
scanimage command -> sane
```

Build `missing_packages` only for actually missing capabilities; ICU contributes the dynamically resolved package.

- [ ] **Step 4: Install only missing packages and re-probe**

Only if `missing_packages` is non-empty, require `apt-get` and `apt-cache`, run:

```bash
apt-get update
apt-get install -y "${missing_packages[@]}"
```

then re-run all capability probes. If anything remains absent, exit non-zero and print the missing capability names.

- [ ] **Step 5: Wire helper into `install.sh`**

Remove the `runtime_packages=(libicu74 ...)` array, `runtime_dependencies_installed`, and old apt block. Add after root validation and package-path setup:

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

Keep root validation before any package-manager mutation.

- [ ] **Step 6: Package the helper**

In `package.sh`:

```bash
cp -- "$install_root/runtime-dependencies.sh" "$package_root/runtime-dependencies.sh"
chmod +x -- "$package_root/runtime-dependencies.sh"
```

Extend the ZIP-entry test to require `${rootName}/runtime-dependencies.sh`.

- [ ] **Step 7: Run focused tests**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj --filter "FullyQualifiedName~LinuxRuntimeDependencyTests|FullyQualifiedName~SystemServiceProductTests|FullyQualifiedName~LinuxPackagingAutomaticBootstrapTests"
```

Expected: PASS.

- [ ] **Step 8: Commit #187 GREEN**

```text
fix(alt): resolve Linux runtime dependencies by capability
```

---

### Task 6: #188 — RED audit of Linux publish dependency graph

**Files:**
- Create: `tests/core/LinuxPublishPlatformPurityTests.cs`
- Potential later modifications: `webassist/src/WebAssistant/WebAssistant.csproj`, `webassist/src/WebAssistant/Program.cs`

**Interfaces:**
- Consumes: actual `dotnet publish -r linux-x64 --self-contained true` output.
- Produces: evidence distinguishing forbidden Windows native payload from Windows-oriented managed cleanup candidates.

- [ ] **Step 1: Add an actual Linux publish inventory test**

On Linux only, run canonical project publish into a temporary directory and enumerate all output files. Enforce the already-proven native boundary:

```csharp
Assert.DoesNotContain(files, path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
Assert.DoesNotContain(files, path => path.Contains("runtimes/win-", StringComparison.OrdinalIgnoreCase));
```

Then add intentional RED assertions only for the two direct unconditional Windows package outputs:

```csharp
Assert.DoesNotContain(files, path => path.EndsWith("Microsoft.Extensions.Hosting.WindowsServices.dll", StringComparison.OrdinalIgnoreCase));
Assert.DoesNotContain(files, path => path.EndsWith("NAPS2.Sdk.Worker.Win32.dll", StringComparison.OrdinalIgnoreCase));
```

Do not initially ban `NAPS2.Wia.dll`, `WindowsBase.dll`, `NTwain.dll`, or all `Microsoft.Win32.*` files by name.

- [ ] **Step 2: Commit audit test only and observe RED**

```text
test(linux): expose direct Windows runtime dependencies RED
```

Expected: the two direct Windows-oriented assemblies are present; native-Windows assertions remain GREEN.

---

### Task 7: #188 — attempt narrow dependency-graph cleanup with an explicit stop branch

**Files:**
- Potentially modify: `webassist/src/WebAssistant/WebAssistant.csproj`
- Potentially modify: `webassist/src/WebAssistant/Program.cs`
- Test: `tests/core/LinuxPublishPlatformPurityTests.cs`
- Existing Windows/Linux build and scanner suites.

**Interfaces:**
- Consumes: Task 6 RED evidence.
- Produces exactly one outcome:
  - **Outcome A:** safe dependency-graph cleanup, #188 may close.
  - **Outcome B:** production experiment reverted, native purity audit retained, #188 stays open.

- [ ] **Step 1: First narrow experiment — exclude runtime assets only for linux-x64**

Split the two direct Windows references as:

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

Keep Systemd, fixed SDK, GDI, GTK unchanged and do not edit `Scanning/**`.

- [ ] **Step 2: Publish and test Linux startup immediately**

```bash
dotnet publish webassist/src/WebAssistant/WebAssistant.csproj -c Release -r linux-x64 --self-contained true -o /tmp/webassistant-linux-purity
```

Start `/tmp/webassistant-linux-purity/WebAssistant`, wait for successful host startup/health, then terminate it. Also run the purity test and existing Linux scanner tests.

- [ ] **Step 3: If startup fails specifically because WindowsServices runtime assembly is absent, make one permitted service-host guard attempt**

Change only the current unconditional registration:

```csharp
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "WebAssistant";
});
```

to:

```csharp
if (OperatingSystem.IsWindows())
{
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "WebAssistant";
    });
}
```

This changes hosting registration only, not scanner API or adapters. Re-run Linux publish/startup and Windows build/service tests.

- [ ] **Step 4A: Keep cleanup only if every boundary is GREEN**

Mandatory conditions:

```text
Linux publish succeeds
both direct Windows runtime assemblies are absent
Linux startup/health succeeds
Linux scanner tests remain GREEN
Windows compile/scanner/service tests remain GREEN
no Scanning/** or API changes were needed
```

If all hold, keep the project/service-host conditioning and commit:

```text
fix(linux): exclude direct Windows-only runtime assets
```

The final PR may change `Refs #188` to `Closes #188`.

- [ ] **Step 4B: Otherwise revert only #188 production experimentation**

Restore `WebAssistant.csproj` and `Program.cs` exactly to their pre-Task-7 state. Convert `LinuxPublishPlatformPurityTests` to permanently enforce only:

```csharp
Assert.DoesNotContain(files, path => path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
Assert.DoesNotContain(files, path => path.Contains("runtimes/win-", StringComparison.OrdinalIgnoreCase));
```

Comment on #188 with the exact failed boundary and evidence. Keep `Refs #188`; #188 remains OPEN. Commit retained audit evidence as:

```text
test(linux): preserve native platform-purity boundary
```

Do not weaken #186/#187 tests.

---

### Task 8: Full pre-version regression and exact VERSION transition

**Files:**
- Modify: `webassist/VERSION`
- Update only current-version test fixtures that intentionally model the accepted transition.

**Interfaces:**
- Consumes: GREEN #186/#187 and explicit #188 Outcome A or B.
- Produces: exactly one monotonic product transition `0.3.20 -> 0.3.21`.

- [ ] **Step 1: Run core tests before VERSION bump**

```bash
dotnet test tests/core/WebAssistant.CoreTests.csproj
```

Expected: implementation tests GREEN; repo-guard VERSION monotonicity may remain intentionally RED until the final version commit.

- [ ] **Step 2: Build a local canonical Linux package only as pre-merge test evidence**

```bash
WEBASSISTANT_ALLOW_DOTNET_BOOTSTRAP=0 webassist/build/linux/package.sh /tmp/webassistant-linux-artifacts
unzip -Z1 /tmp/webassistant-linux-artifacts/WebAssistant-linux-x64-0.3.20.zip
```

Expected: every entry starts with `WebAssistant-linux-x64-0.3.20/`; exactly one top-level directory exists. This is test evidence, not the future frozen release candidate.

- [ ] **Step 3: Bump VERSION exactly once**

Change `webassist/VERSION`:

```text
0.3.20
```

to:

```text
0.3.21
```

Update release test fixtures from current/base `0.3.20/0.3.19` to `0.3.21/0.3.20` only where they model the current transition; leave historical evidence untouched.

- [ ] **Step 4: Commit final product transition**

```text
chore(version): advance WebAssistant to 0.3.21
```

No later implementation commit in this PR may bump VERSION again.

---

### Task 9: Ready/full distribution acceptance and exact-head merge

**Files:**
- No new implementation files unless a failing acceptance test proves a defect.
- PR metadata may be updated without moving head.

**Interfaces:**
- Consumes: exact final feature head with VERSION 0.3.21.
- Produces: exact accepted merge transition eligible for frozen-main resolver.

- [ ] **Step 1: Require Draft fast-feedback GREEN**

Require exact-head core and repo-guard evidence. Diagnose any failure before Ready.

- [ ] **Step 2: Reconcile PR ChangeIntent with final #188 outcome**

If Outcome A succeeded, use `Closes #188`; otherwise keep `Refs #188` and record defer evidence in #188. Budgets reflect actual diff only.

- [ ] **Step 3: Mark Ready without changing SHA**

Ready must trigger full distribution-sensitive CI.

- [ ] **Step 4: Require full exact-head GREEN**

Verify:

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

Inspect the Linux producer artifact from that exact run rather than rebuilding it for review.

- [ ] **Step 5: Merge with exact-head guard**

Use normal merge commit, not direct push/squash. Verify post-merge main, VERSION 0.3.21, #186/#187 closed, and #188 state matches Outcome A/B.

---

### Task 10: New frozen candidate and real ALT Linux 10.1 evidence

**Files:**
- No repository source mutation after freeze.
- External evidence only until final PDF generation.

**Interfaces:**
- Consumes: exact post-merge main with VERSION 0.3.21.
- Produces: fresh unpublished Draft candidate built once from that SHA and target evidence on real ALT Workstation 10.1.

- [ ] **Step 1: Freeze exact post-merge main**

Verify open repository-side PRs = 0, current authority = v0.2, v0.3 remains candidate/accepted=false, and no conflicting `v0.3.21` Release/tag exists. Leave abandoned `v0.3.20` Draft untouched.

- [ ] **Step 2: Dispatch `release-candidate.yml` once**

Input:

```text
source_sha=<exact new main merge SHA>
```

Require resolver -> producers -> same-byte acceptance -> Draft staging SUCCESS.

- [ ] **Step 3: Verify staged Linux artifact without rebuilding**

Download exact staged `WebAssistant-linux-x64-0.3.21.zip`; verify one top-level `WebAssistant-linux-x64-0.3.21/`, no direct-root payload, no traversal/absolute entries, and SHA/provenance match Draft metadata.

- [ ] **Step 4: Retest exact staged bytes on real ALT Workstation 10.1**

User flow:

```bash
unzip WebAssistant-linux-x64-0.3.21.zip
cd WebAssistant-linux-x64-0.3.21
sudo ./install.sh
```

Expected:

```text
no libicu74 lookup
existing ICU 69 accepted
no apt mutation when all capabilities are present
systemd service active
/v1/health responds
/v1/scanners responds
restart succeeds
uninstall succeeds
```

Use this as final ALT 10.1 evidence for the installation guide; source and installer bytes remain frozen.
