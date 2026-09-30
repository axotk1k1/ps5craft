# PS5Craft Development Plan

**Based on:** [`ARCHITECTURE.md`](ARCHITECTURE.md) research (2026-09-30)  
**Rule:** Complete each phase (compile + tests green) before starting the next.

---

## Research gate (completed)

- [x] Inspected `PSBrew/MkPFS` source (CLI, progress, formats, licenses)  
- [x] Inspected `Phoenixx1202/PS5-FPKG-Builder` — **README-only / binary releases; no source**  
- [x] Identified LibProsperoPkg as the open extraction engine used by that lineage  
- [x] Documented FFPFS vs FFPFSC CLI mapping and compatibility warnings  
- [x] Wrote `ARCHITECTURE.md`

---

## Phase 0 — Repository bootstrap

**Goals**

- Create solution + project skeleton matching architecture  
- Add `.gitignore`, `THIRD-PARTY-NOTICES.txt` (MkPFS, LibProsperoPkg, CommunityToolkit, etc.)  
- Choose license (recommended GPL-3.0 if shipping LibProsperoPkg-linked workers)  
- DI composition root stub  

**Exit criteria**

- `dotnet build` succeeds on empty projects  
- `_refs/` gitignored  

---

## Phase 1 — WPF application shell

**Goals**

- `PS5Craft.App` WPF (.NET 10) starts  
- Window chrome: title `PS5Craft v1.0`, subtitle `PS5 Game Package Tool`  
- Dark theme tokens (navy / black / electric blue)  
- Empty main window with navigation host  

**Exit criteria**

- App launches on Windows  
- Release build produces `PS5Craft.exe`  

---

## Phase 2 — UI shell (navigation + layout)

**Goals**

- Sidebar pages: Распаковка / Упаковка / Информация / Инструменты  
- Top bar: Settings / About  
- Sidebar footer branding  
- Placeholder content regions matching mockup proportions  
- Game Information layout: cover left + metadata full remaining width  

**Exit criteria**

- Navigation switches views without flicker  
- Visual structure matches mockup (no functional tools yet)  

---

## Phase 3 — `ExternalProcessRunner`

**Goals**

- `IExternalProcessRunner` + `ExternalProcessRunner`  
- Async stdout/stderr streaming  
- Exit code, timeout, priority  
- `KillProcessTreeAsync` (Windows)  
- Path / executable validation (no `cmd.exe` concatenation)  

**Tests**

- Success / non-zero exit / missing exe / timeout / cancel  
- Paths with spaces  

**Exit criteria**

- Unit tests green  
- Demo: run `dotnet --info` or similar without UI freeze  

---

## Phase 4 — MkPFS detection

**Goals**

- Discovery order: `tools\mkpfs` → settings → PATH → `python -m mkpfs`  
- Version parse (`mkpfs -V` / help title)  
- Settings fields + **Проверить MkPFS** button  
- Friendly errors for missing Python / mkpfs  

**Exit criteria**

- Detection reports Available + Version or precise missing component  
- Unit tests for discovery resolution with fakes  

---

## Phase 5 — MkPFS CLI compression

**Goals**

- `IMkPfsService.PackFolderAsync`  
- Output format selector: **FFPFSC** / **FFPFS**  
- FFPFS → `--raw` + UI warning when compression enabled  
- CPU count, compression level, block size, verify, dry-run, verbose  
- Temp folder from settings  
- Argument builder unit tests (spaces, flags)  

**Exit criteria**

- Pack can be started against a tiny fixture when mkpfs is installed  
- No UI thread blocking  
- Extension never silently renamed by PS5Craft  

---

## Phase 6 — Cancellation

**Goals**

- Cancel button binds to `CancellationTokenSource`  
- Graceful terminate → wait → kill process tree  
- UI state → `Cancelled`  
- No orphan mkpfs / python children  

**Exit criteria**

- Cancel test with long-running fake process  
- Manual cancel during dry-run / small pack  

---

## Phase 7 — Resource monitoring

**Goals**

- `IResourceMonitor` for tracked child PID  
- Update every ~1 s: CPU %, WorkingSet, optional disk R/W  
- Bind to packing / extracting views  

**Exit criteria**

- Monitor stops when process exits  
- No aggressive polling (< 500 ms)  

---

## Phase 8 — FPKG extraction worker

**Goals**

- `PS5Craft.FpkgWorker` console app hosting LibProsperoPkg  
- Commands: `inspect`, `extract`  
- JSONL progress events on stdout  
- `IFpkgService` launches worker via `ExternalProcessRunner`  
- Accept `.fpkg` / `.pkg` when magic matches  
- Refuse retail-without-key cleanly  

**Exit criteria**

- Worker builds  
- Inspect/extract round-trip on a **debug/sample** fixture (or skip if unavailable)  
- UI remains responsive  

---

## Phase 9 — Metadata extraction

**Goals**

- `IGameMetadataService`  
- Title, Title ID, Content ID, Region, Version, SDK, FW, size, package type, languages  
- Cover / icon when present  
- Unknown fallbacks; never throw to UI  

**Exit criteria**

- Selecting a package updates Game Information panel  
- Unit tests for param.json / region heuristics  

---

## Phase 10 — Game information UI

**Goals**

- Full-width info section per mockup  
- Flag strip for languages (+N overflow)  
- Region / console badges  
- Wired to metadata service  

**Exit criteria**

- Matches design; no right-side path card  

---

## Phase 11 — Progress + logging UI

**Goals**

- Determinate progress only when parser has reliable %  
- Else indeterminate + phase text  
- Live stats: size, speed, ETA when known  
- Log pane: levels, clear, save, open folder  
- Log every external command invocation  

**Exit criteria**

- MkPFS stderr progress parsed in unit tests (`MkPfsProgressParser`)  
- Fake stdout/stderr integration with UI ViewModel  

---

## Phase 12 — Verification

**Goals**

- Post-pack: MkPFS `--verify` or separate verify call  
- Post-extract: optional folder checks (`sce_sys`, `param.json` presence)  
- Success summary dialog: sizes, ratio, time, open folder  

**Exit criteria**

- Failed verify → Failed state with stderr details  

---

## Phase 13 — Settings

**Goals**

- General: language, theme  
- Tools: MkPFS path, Python path, FPKG worker path  
- Performance: CPU Auto/1/2/…, priority (default Below Normal), compression level, block size  
- Output: default output + temp folders  
- Verification toggles  
- Persist `%LOCALAPPDATA%\PS5Craft\settings.json`  

**Exit criteria**

- Settings round-trip tests  
- Temp path shown and used by pack  

---

## Phase 14 — Tests consolidation

**Goals**

- Cover runner, parsers, settings, metadata, argument builders  
- Integration test: tiny folder → pack → verify (conditional)  
- CI-friendly skip when tools missing  

**Exit criteria**

- `dotnet test` green on a clean machine without games  

---

## Phase 15 — Standalone Release

**Goals**

- `dotnet publish` single-folder or single-file Windows x64  
- Bundle or document MkPFS install steps  
- Include `THIRD-PARTY-NOTICES.txt`  
- Temp cleanup on success; retain on failure (setting)  
- Smoke checklist for 50–200 GB workflow (manual)  

**Exit criteria**

- Published app runs without Visual Studio  
- Responsiveness verified under high CPU child load  

---

## Suggested immediate next action

Start **Phase 0 + Phase 1**: create the solution skeleton and a dark WPF window that launches.

Do **not** implement all services in one pass.

---

## Verification checklist (every phase)

1. `dotnet build`  
2. `dotnet test` (once tests exist)  
3. Fix errors before advancing  
4. Short phase report: what landed, what is blocked, next phase  

---

## Blockers to watch

| Blocker | Mitigation |
|---------|------------|
| No FPKG Builder source | FpkgWorker + LibProsperoPkg |
| No sample `.pkg` in CI | Conditional tests + tiny synthetic fixtures where legal |
| MkPFS not installed | Settings + clear detection errors; skip integration |
| Retail package without key | Explicit user message; no silent attempt |
| GPL obligations | Notices + source distribution plan in Phase 15 |
