# PS5Craft Architecture

**Status:** Research complete — ready for Phase 1 implementation  
**Date:** 2026-09-30  
**Product:** PS5Craft v1.0 — PS5 Game Package Tool

---

## 1. Executive summary

PS5Craft is a **WPF (.NET) frontend** that orchestrates external tools for:

```text
.fpkg / .pkg  →  extract  →  game folder  →  MkPFS CLI  →  .ffpfsc or .ffpfs
```

Heavy work **never** runs on the WPF UI thread. Extraction and compression run as **independent child processes**. The UI only starts processes, streams stdout/stderr, updates progress, and handles cancellation.

---

## 2. Reference repository findings

### 2.1 PS5-FPKG-Builder (`Phoenixx1202/PS5-FPKG-Builder`)

| Fact | Detail |
|------|--------|
| Published source | **README only** — no extractable application source on `main` |
| Distribution | Binary zip releases only (e.g. `PS5.FPKG.Builder-1.0.3.zip`, ~178 MB) |
| Purpose | Build PS5 PKG/FPKG from folders / `.exfat` / `.ffpkg` / `.ffpfs` / `.ffpfsc`; Reader tab inspects/extracts PKG |
| Engine (from release notes) | **LibProsperoPkg** (progress log percentages mentioned explicitly) |
| Optional mount helper | OSFMount for large image mounts |
| Credits | Drakmor, SvenGDK |
| CLI | **None documented** — cannot shell to a stable FPKG Builder CLI |

**Implication for PS5Craft:** We **cannot** implement extraction by copying FPKG Builder source. We treat it as a **UX / workflow reference** only. Extraction must use the same open technical lineage: **LibProsperoPkg** (SvenGDK), wrapped in our own worker process.

### 2.2 MkPFS (`PSBrew/MkPFS`)

| Fact | Detail |
|------|--------|
| License | **GPL-3.0** |
| Language | Python 3.9+ |
| Package version (source) | `1.0.0` (`mkpfs/__init__.py`) |
| Entry points | `mkpfs` console script; `python -m mkpfs` |
| GUI (do **not** use) | `python -m mkpfs.gui` / `mkpfs-gui` |

#### Verified pack workflows (from CLI source, not only README)

| User format | Actual MkPFS command | Notes |
|-------------|----------------------|-------|
| **FFPFSC** (recommended) | `mkpfs pack folder <src> <out.ffpfsc>` | Default: folder → exFAT wrap → PFSC in **one pass**, no temp `.exfat` |
| **FFPFS** (advanced) | `mkpfs pack folder --raw <src> <out.ffpfs>` | Direct PFS pack. **Compatibility warning:** compressed raw game folders often misread on console (see MkPFS limitations / issue #49) |

**Do not rename `.ffpfsc` → `.ffpfs`.**  
MkPFS may auto-adjust extensions unless `--no-adjust-output-file-extension` is set. PS5Craft must pass the intended path and respect MkPFS output naming (log any adjustment).

#### Important CLI flags (pack folder)

```text
--raw
--compress | --no-compress
--block-size {auto|N|auto-fit}
--version {PS4|PS5}          # default PS5
--cpu-count N                # 0 = auto (min(16, max(1, cpu_count()-1)))
--compression-level 0-9
--max-compressed-ratio
--min-compress-size
--skip-executable-compression
--temp-folder PATH
--verify
--verify-structure | --no-verify-structure
--verbose
--dry-run
--adjust-output-file-extension | --no-adjust-output-file-extension
--require-game-files
```

Other commands used by PS5Craft services:

- `mkpfs -V` / `mkpfs -h` — version / help detection  
- `mkpfs unpack [--deep] <image> <out_dir>` — reverse  
- `mkpfs verify` / `mkpfs inspect` / `mkpfs tree` — diagnostics  

#### Progress reporting (verified in `mkpfs/pbar.py`)

- Progress is written to **stderr** (not stdout).
- Terminal form (carriage-return updates):

```text
[################------------]  75% compress @ 1.2 GB/s ETA 21s
```

- Phases include names such as `compress`, `write`, plus `status()` lines.
- When progress cannot be parsed reliably → UI shows **indeterminate** state and phase text (`Сканирование…` / `Сжатие…`). **Never invent percentages.**

#### Known MkPFS warnings PS5Craft must surface

1. Default **FFPFSC** path is the most compatible for game backups.  
2. **`--raw` + compression** can verify successfully but console may misread files — show explicit GUI warning when user selects FFPFS with compression.  
3. Default `--block-size 65536` can waste space on many tiny files — expose block-size control.  
4. Antivirus can slow large conversions — optional tip in logs/docs only.

### 2.3 LibProsperoPKG (`SvenGDK/LibProsperoPKG`) — extraction foundation

| Fact | Detail |
|------|--------|
| License | **GPL-3.0** (+ NOTICE for LibOrbisPkg / ooz-derived parts) |
| Target | **.NET 10** / C# 14 |
| Role | Same package engine lineage referenced by FPKG Builder releases |
| Extract API | `ProsperoPackageExtractor.Inspect` / `Extract` |
| Metadata | Package headers + `param.json` / icons from package or extracted tree |

**Extraction contract (public API):**

1. `Inspect(path)` → package type, retail flag, content id, `RequiresSuppliedKey`.  
2. If `RequiresSuppliedKey == true` (retail `0x80`) → **refuse** without a user-supplied 32-byte EKPFS. **No key discovery / brute force.**  
3. Debug / passcode-derivable images → extract with passcode (default zeros) to output folder.  
4. Output is a normal game/app folder (`sce_sys/param.json`, content files).

**`.fpkg` vs `.pkg`:** Treat both as PS5 package containers when magic is `\x7FCNT` / `\x7FFIH` (same family used by PS5PKGReader / LibProsperoPkg). Extension alone must not decide format.

### 2.4 PS5PKGReader (`SvenGDK/PS5PKGReader`)

Open-source Avalonia GUI for partial PKG read/extract. Useful as a **metadata/cover parsing reference** (param.json, PNG entries, retail vs debug). Not the primary extraction engine.

---

## 3. Legal / security boundaries

| Allowed | Forbidden |
|---------|-----------|
| Operate on user-provided files | Bundled proprietary Sony keys |
| Wrap MkPFS CLI + LibProsperoPkg (with GPL notices) | DRM bypass, key extraction, brute force |
| Debug/passcode extraction when public inputs suffice | Silent format rename / fake success |
| User-supplied EKPFS if they already have it | `cmd.exe /c` with concatenated untrusted input |
| Attribution in `THIRD-PARTY-NOTICES.txt` | Claiming FFPFS when output is FFPFSC |

**Licensing note:** MkPFS and LibProsperoPkg are **GPL-3.0**. Shipping a worker that links LibProsperoPkg and redistributing MkPFS requires GPL-compliant notices and source offer terms. Prefer calling MkPFS as an **external process** (aggregation). Document final license choice for PS5Craft itself during Phase 1 (recommended: **GPL-3.0** if linking LibProsperoPkg into distributed binaries).

---

## 4. High-level architecture

```text
┌─────────────────────────────────────────────────────────────┐
│  PS5Craft.App (WPF UI thread)                               │
│  Views ←→ ViewModels (CommunityToolkit.Mvvm)                │
└───────────────────────────┬─────────────────────────────────┘
                            │ async commands / IProgress
┌───────────────────────────▼─────────────────────────────────┐
│  PS5Craft.Services                                          │
│  MkPfsService · FpkgService · GameMetadataService           │
│  ResourceMonitor · LogService · SettingsService             │
└───────────────────────────┬─────────────────────────────────┘
                            │
┌───────────────────────────▼─────────────────────────────────┐
│  PS5Craft.Infrastructure                                    │
│  ExternalProcessRunner · ProcessTreeKiller · PathValidator  │
└─────────────┬───────────────────────────────┬───────────────┘
              │                               │
              ▼                               ▼
   ┌──────────────────┐           ┌──────────────────────────┐
   │ mkpfs CLI        │           │ PS5Craft.FpkgWorker.exe  │
   │ (Python process) │           │ (hosts LibProsperoPkg)   │
   │ stderr progress  │           │ JSONL progress on stdout │
   └──────────────────┘           └──────────────────────────┘
```

### Why a dedicated FpkgWorker?

LibProsperoPkg is a **library**, not a CLI. To satisfy “all heavy operations as separate background processes” without freezing the WPF process under high CPU/RAM/IO:

- `PS5Craft.FpkgWorker` is a small console host.  
- Arguments via `ArgumentList` (no shell).  
- Progress / log events as **JSON Lines** on stdout.  
- Errors on stderr + non-zero exit codes.  
- Cancellation = kill worker process tree.

MkPFS already is a separate process — no Python embedding in the WPF app.

---

## 5. Solution structure

```text
PS5Craft/
├── src/
│   ├── PS5Craft.App/                 # WPF entry, DI composition root
│   ├── PS5Craft.Core/                # Models, enums, interfaces (no UI)
│   ├── PS5Craft.Infrastructure/      # Process runner, FS, JSON settings
│   ├── PS5Craft.Services/            # MkPFS / FPKG / metadata / monitor
│   ├── PS5Craft.ViewModels/          # MVVM
│   ├── PS5Craft.Views/               # XAML views/controls
│   └── PS5Craft.FpkgWorker/          # Console worker (LibProsperoPkg)
├── tests/
│   └── PS5Craft.Tests/
├── tools/                            # Optional bundled tools (not committed binaries by default)
│   ├── mkpfs/
│   └── fpkg/
├── docs/                             # Optional deeper docs
├── _refs/                            # Local clones for research (gitignored)
├── ARCHITECTURE.md
├── DEVELOPMENT_PLAN.md
├── THIRD-PARTY-NOTICES.txt
└── settings schema → %LOCALAPPDATA%\PS5Craft\settings.json
```

**Runtime folders:**

| Path | Use |
|------|-----|
| `%LOCALAPPDATA%\PS5Craft\Temp` | Temp / spool (never inside game folder) |
| `%LOCALAPPDATA%\PS5Craft\Logs` | Persistent logs |
| `AppBase\tools\mkpfs\` | Preferred local MkPFS discovery |
| `AppBase\tools\fpkg\` | Optional bundled worker overrides |

**Target framework:** **.NET 10** (LTS-capable; required by LibProsperoPkg). WPF on Windows only.

---

## 6. Core models

```csharp
enum OperationState { Idle, Preparing, Running, Completed, Failed, Cancelled }

enum OutputFormat { Ffpfsc, Ffpfs }   // never fake rename

enum ProcessPriorityLevel { Normal, BelowNormal, Low }

sealed class OperationProgress
{
    double? Percent;                 // null => indeterminate
    string? Phase;                   // "compress", "write", "extract", ...
    string? CurrentFile;
    long ProcessedBytes;
    long? TotalBytes;
    double SpeedBytesPerSecond;
    TimeSpan Elapsed;
    TimeSpan? EstimatedRemaining;
    double? CpuUsage;
    long? MemoryUsageBytes;
    double? DiskReadBytesPerSecond;
    double? DiskWriteBytesPerSecond;
}

sealed class GameInfo / PackageInfo
{
    string? Title, TitleId, ContentId, Region, Console, Version;
    string? PackageType, SdkVersion, RequiredFirmware;
    long? InstalledSizeBytes, FileSizeBytes;
    IReadOnlyList<string> Languages;
    byte[]? CoverPngOrJpeg;          // display only; do not keep huge blobs longer than needed
}

sealed class CompressionSettings { /* format, cpu, level, block size, verify, flags */ }
sealed class ExtractionSettings { /* verify, extract artwork, passcode optional */ }
sealed class ToolInfo { string Path; string Version; bool Available; }
```

---

## 7. Service contracts

### 7.1 `IExternalProcessRunner`

```csharp
Task<ProcessResult> RunAsync(
    ProcessStartRequest request,
    IProgress<ProcessOutput>? progress,
    CancellationToken cancellationToken);
```

Requirements:

- `ProcessStartInfo`: `UseShellExecute=false`, redirect stdout/stderr, `CreateNoWindow=true`
- Prefer `ArgumentList` over manual quoting
- Async line reading (never `ReadToEnd()` / `WaitForExit()` on UI thread)
- Timeout, priority, optional CPU affinity
- `KillProcessTreeAsync` on cancel (Windows Job Object or recursive PID tree — only our tree)
- Working directory + environment dictionary

### 7.2 `IMkPfsService`

- Discover: `tools\mkpfs` → settings path → PATH → configured Python `python -m mkpfs`
- `GetVersionAsync`, `TestAsync`
- `PackFolderAsync(CompressionSettings, …)`
- `VerifyAsync`, `UnpackAsync`, `InspectAsync`
- Parse stderr progress via `MkPfsProgressParser`
- Map `OutputFormat.Ffpfs` → `--raw`; `Ffpfsc` → default (no `--raw`)
- Pass `--cpu-count`, `--temp-folder`, `--verify`, etc. from settings

### 7.3 `IFpkgService`

- Discover worker: `tools\fpkg\PS5Craft.FpkgWorker.exe` or adjacent app directory
- `InspectAsync` → metadata preview without full extract
- `ExtractAsync` → launch worker; stream progress
- Map failures: missing tool, invalid package, retail key required, disk space, access denied, cancel

### 7.4 `IGameMetadataService`

Best-effort readers (never crash UI):

1. Package inspect (worker / LibProsperoPkg-compatible inspect)  
2. Folder `sce_sys/param.json` + icon0  
3. Optional MkPFS-compatible image metadata for `.ffpfs` / `.ffpfsc` / `.ffpkg` / `.exfat` later  

Missing fields → `Unknown` / `—`.

### 7.5 Other services

- `IResourceMonitor` — child process only; ~1 s tick; `TotalProcessorTime`, `WorkingSet64`; optional counters  
- `ILogService` — INFO / WARNING / ERROR / SUCCESS; clear / save / open folder  
- `ISettingsService` — JSON under LocalAppData  

---

## 8. MkPFS command construction examples

**FFPFSC (default / recommended):**

```text
mkpfs pack folder
  --version PS5
  --cpu-count 0
  --compression-level 7
  --skip-executable-compression
  --temp-folder "C:\Users\...\AppData\Local\PS5Craft\Temp"
  --verify
  "D:\Games\My Game\Spider-Man 2"
  "D:\Games\My Game\Spider-Man 2.ffpfsc"
```

**FFPFS (raw — show warning if compression enabled):**

```text
mkpfs pack folder --raw
  --version PS5
  --cpu-count 8
  --compression-level 7
  --temp-folder "..."
  --verify
  "D:\Games\Game_Extracted"
  "D:\Games\Game.ffpfs"
```

Spaces in paths: always via `ArgumentList` (no shell quoting bugs).

---

## 9. UI architecture

Dark PS5-inspired shell matching the provided mockup:

- Sidebar: Распаковка / Упаковка / Информация / Инструменты  
- Top: PS5Craft v1.0 + subtitle + Settings / About  
- Game Information: **full width** — cover left, metadata right (no separate path card)  
- Workflow strip: Распаковка → Просмотр → Упаковка  
- Progress + live resource strip + log pane  
- Russian UI strings first (settings language selector later)

**MVVM rules:** Views bind only to ViewModels; ViewModels call services; no `Process` usage in Views.

---

## 10. Error model

All failures become `OperationResult` with:

- User message (RU): e.g. `Сжатие завершилось с ошибкой.`  
- Technical: exit code, stderr excerpt, exception type  
- Actions: Copy error / Close  

Never crash the process because a child failed.

---

## 11. Testing strategy

| Layer | Focus |
|-------|-------|
| Unit | `ExternalProcessRunner`, argument builders, progress parsers, settings, path validation |
| Fake process | Stub executables that print known stdout/stderr and exit codes |
| Integration | Tiny fixture folder → MkPFS pack (if mkpfs available) → verify; skip if tool missing |
| Paths | Spaces, long paths, Unicode |

Do **not** require 100 GB games for CI.

---

## 12. Open decisions / risks

| Item | Decision |
|------|----------|
| FPKG Builder source unavailable | Use LibProsperoPkg via `FpkgWorker`; document FPKG Builder as UX reference only |
| `.fpkg` extension | Accept if container magic matches; else clear error |
| Retail packages | Block unless user supplies EKPFS; never invent keys |
| Progress accuracy | Parse MkPFS stderr; else indeterminate |
| Python missing | Clear “MkPFS / Python not found” with Settings browse |
| GPL redistribution | Ship notices; decide source-offer packaging in Release phase |
| .NET version | .NET 10 for solution (LibProsperoPkg requirement) |

---

## 13. Non-goals (v1)

- Re-implementing MkPFS compression in C#  
- Using `mkpfs.gui`  
- Embedding Sony proprietary materials  
- Silent conversion between FFPFS and FFPFSC  
- Automating OSFMount (optional later; not required for v1)  

---

## 14. Next document

See [`DEVELOPMENT_PLAN.md`](DEVELOPMENT_PLAN.md) for phased implementation order, exit criteria, and verification steps.
