# Status and Roadmap

This page records what exists and the traps worth knowing. It does not plan
work: everything open is tracked as issues in the milestones

- [A: Kern ohne GUI und CLI](https://github.com/crack666/smart_file_organizer/milestone/1) (core runnable without the GUI, plus a CLI)
- [B: Ordner-Urteil](https://github.com/crack666/smart_file_organizer/milestone/2) (folder verdict)
- [C: Plan und Ausfuehrung](https://github.com/crack666/smart_file_organizer/milestone/3) (target structure, plan and execution)
- [D: GUI-Anschluss](https://github.com/crack666/smart_file_organizer/milestone/4) (GUI integration)

Every statement about the current state below carries the date it was
checked and how. If you change what a line describes, update the line and
its date, or delete it.

## What exists

_Checked 12.11.2026 by reading the code on `master`; build and tests run
locally (`dotnet build` 0 errors, `dotnet test` 271 passed with #9 on top of #19 and #5; the
line said 47 before) and in CI on Windows and Linux._

### Scan and persistence

- Layered solution: Domain / Application / Infrastructure / Scanning / Desktop.
- Recursive scan engine with an iterative queue, so deep trees do not
  overflow the stack.
- SQLite persistence (Dapper) for scan jobs, directory nodes, file nodes,
  AI results, directory AI results and user overrides.
- File type detection by extension (`FileTypeDetector`).
- Scan pruning (`HeuristicsEngine`): dot-directories and skip patterns such
  as `AppData`, `Windows`, `node_modules`, `$Recycle.Bin`. Patterns match
  whole path segments (case-insensitive, either separator) and only below
  the scan root, so `Combinations` is not skipped for `bin` and a root
  inside `AppData` is scanned. Each skip stores its rule in `dir_reason`,
  e.g. `Skip: matches skip pattern 'bin'` _(checked 12.11.2026, #19, by
  `HeuristicsEngineTests`)_.
- Resume: directories already scanned are skipped on restart.
- `ScanExportService` (CSV export) exists and is tested; it is
  registered in DI but not reachable from the UI. Every row goes through
  `SensitiveOutputGuard`. (Until 12.11.2026 this line said "CSV/JSON
  export"; the service never wrote JSON. JSON output of a file is
  `SensitiveOutputGuard.ToJson`.)
- Sensitive files ([#9](https://github.com/crack666/smart_file_organizer/issues/9)):
  rules without a model mark files during the scan
  (`SensitiveFileRules`: names, extensions, folders, keywords, header probe
  of small non-media files); the local model adds markers in the regular
  classification call. Stored in `file_nodes.sensitive_markers` /
  `sensitive_rule` and `ai_results.sensitive_markers` (schema step 2).
  Outputs withhold their content by default; see
  [engineering-decisions.md](engineering-decisions.md#8-sensitive-files-local-and-external-readers).

### AI pipeline (three phases, run after the scan, never inside it)

1. **Directory pre-assessment** (`DirectoryPreAssessmentService`): sends the
   file list of each directory to Ollama and gets a sampling strategy
   (`analyze_all`, `random_sample`, `skip`). Files not sampled are marked
   `Skipped`. Stored in `ai_directory_results` with phase `pre_assessment`.
2. **Per-file classification** (`ClassificationService`,
   `OllamaService`): images as base64 (default limit 10 MB), documents as
   extracted text or first PDF page rendered to an image (default limit
   5 MB), videos and audio by file name and path only. Returns category,
   importance, confidence, summary, suggested target and sensitive markers.
   Stored in `ai_results`. A marked file is stored as `ReviewNeeded`
   instead of `TrashCandidate`.
3. **Directory summaries** (`DirectorySummaryService`): bottom-up, one
   Ollama call per directory. Stored in `ai_directory_results` with phase
   `summary`.

All three phases run with bounded concurrency set by `MaxParallelRequests`
(default 4 in `appsettings.json`; `ClassificationService` clamps it to 1..8).

### Desktop UI (Avalonia)

- Root folder selection; scan start, pause, cancel.
- AI classification start, pause, resume, cancel, independent of the scan.
- Lazy folder tree; file table with category colour, confidence, summary.
- Directory summary banner above the file table.
- File detail panel with metadata and image, text and first-page PDF preview.
- Manual review: accept or override category and target per file, in the
  detail panel and in the table row (`ReviewService`, `UserOverride`).
- Ollama settings (base URL, model, keep-alive) editable and persisted;
  installed models listed from `/api/tags`; manual model warm-up.
- Recent scans on the welcome screen.
- Right-click on a folder opens it in the file manager. This calls
  `explorer.exe` and therefore only works on Windows.

### Command line

_Checked 12.11.2026 on the branch for
https://github.com/crack666/smart_file_organizer/issues/5: `dotnet test`
130 passed after rebasing on the scan-rule change of #20, and `scan`, `status --json`, `analyze` against a closed port
(exit code 3) and `doctor` run by hand against a temp database._

- `sfo scan`, `analyze`, `jobs`, `status`, `doctor` with `--json` output
  and JSONL progress; see [cli.md](cli.md).
- Read commands for folders and files are not there yet
  (https://github.com/crack666/smart_file_organizer/issues/6).

### Known limits

- PDFs: only the first page is seen; text extraction is preview-sized; no
  OCR beyond what the vision model reads from rendered pages.
- If the model gives a valid category but omits `confidence`, the parser
  sets `0.85`.
- gemma4 ignores schema field names; the parser accepts aliases such as
  `classification` for `category`. Other models may differ.
- No bulk review, no review filters, no file operations (move, copy,
  archive) yet. See milestones C and D.
- Sensitive-file protection covers what goes through `SensitiveOutputGuard`
  (today: the CSV export). The GUI shows everything, as it is used at the
  machine itself. The header probe runs only during a scan or rescan;
  databases scanned before schema step 2 are protected by the name rules
  (re-evaluated on output) and by model markers, not by the header rule.

## Operating notes

_Checked 12.11.2026 against the code: `think = false` in all three request
builders of `OllamaService`, `TimeoutSeconds` default 180 in
`OllamaOptions` and `appsettings.json`, configuration order in
`ServiceConfigurator`. The timing figures come from the development
sessions of April 2026 and were not re-measured._

- **`think: false` stays.** Every Ollama request sends it. Without it gemma4
  emits reasoning tokens and trivial prompts took 90 to 150 s (April 2026).
- **Timeout 180 s.** Keep `Ollama:TimeoutSeconds` at 180. With 90 s, Phase 3
  hung: Ollama keeps working after a client-side timeout, so the next
  request queues behind the abandoned one.
- **`settings.json` wins.** The per-user `settings.json` in the
  application data folder (`%APPDATA%\SmartFileOrganizer\` on Windows,
  `~/.config/SmartFileOrganizer/` on Linux) is loaded after the shipped
  `appsettings.json` and overrides it. Check it first when the app does not
  behave as `appsettings.json` suggests. The SQLite database `data.db` lives
  in the same folder.
- **Stale AI results.** Phases treat existing `ai_directory_results` rows as
  done. After changing the AI pipeline, start a new scan job or delete the
  database, otherwise old results are reused.
- **Model warm-up.** The first request to a large vision model waits for it
  to load into VRAM. Warm-up and a long `KeepAlive` (default `60m`) help;
  Ollama still unloads after idle time or a restart.
- **Check model names.** Query `/api/tags` before testing; 404s seen during
  development came from a model name that was not installed.
- **Logs.** Prompts and truncated responses are logged at `Information`
  through the console and debug providers. The prompt contains the text
  excerpt, also of sensitive files: do not hand console logs of an AI run
  to an external reader.

## Known traps (UI and runtime)

Root causes and fixes are in
[engineering-decisions.md](engineering-decisions.md).

- Avalonia `DataGrid` needs its theme included explicitly or renders as a
  black slab.
- Dark theme defaults left panels black until explicit backgrounds were set.
- PDF previews rendered transparent until a white background was forced.
- Native PDFium runtimes (Docnet.Core) may be blocked by application control
  policies in managed Windows environments.
