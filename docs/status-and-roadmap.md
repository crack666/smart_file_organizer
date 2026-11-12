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
locally (`dotnet build` 0 errors and 0 warnings, `dotnet test` 582 passed with
https://github.com/crack666/smart_file_organizer/issues/39 on top of the second check
(https://github.com/crack666/smart_file_organizer/issues/23); the line said 551 with the second check on top of
the library review and the evaluation, 454 with #22 on top of #8 and was not updated by those two, 411 with #8 on top of the
scan rules, read commands and library comparison, 312 before that, 293 before that, 271 before that, and 47
before that) and in CI on Windows and Linux._

### Scan and persistence

- Layered solution: Domain / Application / Infrastructure / Scanning / Desktop.
- Recursive scan engine with an iterative queue, so deep trees do not
  overflow the stack.
- SQLite persistence (Dapper) for scan jobs, directory nodes, file nodes,
  AI results, directory AI results and user overrides.
- File type detection by extension (`FileTypeDetector`).
- Scan pruning (`HeuristicsEngine`) from a rule file (profile): the shipped
  `default-rules.json` or one given with `sfo scan --rules`, stored at the
  job; `skip`, `shallow` (with `max_depth`) and `user_data` rules. Rules match
  whole path segments (case-insensitive, either separator), skip and shallow
  rules only below the scan root. Beyond `shallow_from` (6) the content
  decides: user files → scanned, program files only → shallow. Every
  decision stores rule, profile and reason in `dir_reason`, e.g.
  `Skip: matches skip rule 'bin' of rules 'default': build output`;
  `sfo rules check` shows it for any path. Format: [scan-rules.md](scan-rules.md)
  _(checked 12.11.2026, https://github.com/crack666/smart_file_organizer/issues/21,
  by `HeuristicsEngineTests`, `ScanPipelineIntegrationTests`, `CliTests` and
  `sfo rules check` / `sfo scan --rules` run by hand on a temp folder; until
  then the line read "Each skip stores its rule in `dir_reason`, e.g.
  `Skip: matches skip pattern 'bin'`" and the patterns were fixed in code)_.
- Resume: directories already scanned are not read again; directories stored
  as skipped or shallow are re-evaluated with the job's current rules and
  entered if those let them in (until 12.11.2026 stored shallow decisions
  were kept as they were and re-evaluated skips were not written back).
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
  Until 12.11.2026 a stored `private_nachricht` marker made every read of
  its folder fail (Dapper parsed the token as an enum name); fixed with
  issue 36, tested for every marker in `SensitivePersistenceTests`.

### AI pipeline (three phases, run after the scan, never inside it)

1. **Directory verdict and pre-assessment** (`DirectoryPreAssessmentService`,
   since 12.11.2026 by [#7](https://github.com/crack666/smart_file_organizer/issues/7)):
   every directory gets a verdict (`einheit`, `einheitlich`, `gemischt`, `leer`,
   `system`) with purpose, retention, reason and the profile it rests on, stored
   in `directory_verdicts`. Clear cases are decided by rule without Ollama; the
   rest in one Ollama call. Files of units and system directories are marked
   `Skipped` (the directory carries the result). The model's sampling fields
   are still asked for but no longer followed. Not yet shown in the GUI
   ([#14](https://github.com/crack666/smart_file_organizer/issues/14)) or the CLI.
2. **Per-file classification, in stages** (`ClassificationService`,
   `DirectorySamplingService`, `OllamaService`; since 12.11.2026 by
   [#8](https://github.com/crack666/smart_file_organizer/issues/8)): a uniform
   directory is judged by a reproducible stratified sample (at most 15 calls
   plus its sensitive files); if the sample agrees, the other files inherit
   category and target (`origin = inherited`, confidence at most 0.6), otherwise
   the directory is raised to mixed (`escalated_from`) and every file is judged.
   Mixed directories: every file, with a keep hint (`temporary`,
   `worth_keeping`). Since 12.11.2026 every file type takes part
   ([#22](https://github.com/crack666/smart_file_organizer/issues/22)): the
   evidence holds facts read without the model (capture date, device,
   location present, image origin, page count, document properties, audio
   tags and duration, speech or music, archive listing and content), the
   content the model may look at (image, PDF text and rendered pages, video
   frames if `ffmpeg` is installed, text), and what is missing. Programs,
   fonts, databases and unknown binaries get a rule result (`origin = rule`)
   without a model call. Facts are stored in `file_facts` (schema step 7),
   the evidence notes in `ai_results.evidence`. _(Until then: "images as
   base64, documents as extracted text or first PDF page rendered to an
   image, videos and audio by file name and path only"; checked by
   `FileEvidenceTests` and `Phase2EvidenceTests` with generated files, no
   run against a real model.)_ Returns category,
   importance, confidence, summary, suggested target and sensitive markers.
   Stored in `ai_results`; samples in `directory_samples` (schema step 6).
   A marked file is stored as `ReviewNeeded` instead of `TrashCandidate`
   and as `worth_keeping` instead of `temporary`. The thresholds are starting
   values until the measurement of
   [#10](https://github.com/crack666/smart_file_organizer/issues/10).
   Since 12.11.2026 ([#23](https://github.com/crack666/smart_file_organizer/issues/23))
   every result carries a certainty (`certain`, `likely`, `uncertain`,
   `review`) from agreement between signals; unsure or contradicted answers
   get a second call with more evidence and the directory as context, both
   answers are kept in `result_checks` (schema step 10), disagreement puts the
   file to `review`. `sfo analyze --retry-errors` retries failed files and
   counts the attempts.
   Samples draw only images, videos and documents; the other files of a
   uniform directory are judged one by one. (Until 12.11.2026 this line
   read: "Files of other types (audio, archives, executables, ...) still get
   no result in any directory.") Samples and escalations are not yet shown by
   the CLI (https://github.com/crack666/smart_file_organizer/issues/6).
3. **Directory summaries** (`DirectorySummaryService`): bottom-up. One
   Ollama call per mixed directory or directory without a verdict; the
   others are summarized from verdict and sample outcome without a call
   (until 12.11.2026 every directory with files cost a call). Stored in
   `ai_directory_results` with phase `summary`.

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
- `sfo summary`, `dirs`, `files`, `show`, `export` read a job in pages with
  filters on verdict, retention and the sensitive marker
  (https://github.com/crack666/smart_file_organizer/issues/6, measured
  12.11.2026 by `dotnet test`: 372 passed after rebasing on the scan rules, and every command run by hand
  with `--json` against a temp database). (Until 12.11.2026 this line said
  the read commands were not there yet.)
- `sfo eval` measures directory verdicts, categories and sensitive markers
  against the generated corpus (41 folders, 97 files) and names every
  failure; corrections in the review are kept as test cases
  (`sfo eval --cases`); runs are stored and compared (`sfo eval --compare`).
  See [evaluation.md](evaluation.md). _(checked 12.11.2026 on the branch for
  https://github.com/crack666/smart_file_organizer/issues/36, rebased on the
  staged analysis, the comparison, the read commands, the file evidence and the library review: `dotnet test`
  523 passed, `sfo eval generate` and `sfo eval --stub` run by hand
  against a temp database; CI runs the same with scripted answers. No run with
  a real model yet.)_ What perfect scripted answers still get wrong is listed
  in [evaluation.md](evaluation.md#measured).

### Comparison with the library

_Checked 12.11.2026 on the branch for
https://github.com/crack666/smart_file_organizer/issues/24, rebased on #31,
#32, #41 (comparison migration now step 5) and #44: `dotnet test` 392 passed; `sfo scan`, `scan --role library`,
`compare --json` (twice, the second run computing no checksum; `match_hash`
equal to `sha256sum` of the file) and `analyze` of a `library` job run by hand
against two temp folders and a temp database._

- `sfo scan <path> --role library` marks the organised archive (the library);
  `sfo compare <source-job> --against <library-job>` sets `backed_up`, `new`,
  `differs` or `error` per source file, finds duplicates within the
  source and counts per directory; see [cli.md](cli.md) and
  [architecture.md](architecture.md#comparison-flow-source-against-library).
- Directories a scan cannot read are now recorded as errors (before, they
  looked empty).
- `sfo analyze` does not yet skip files that are `backed_up`; the
  per-directory counts say which ones need no analysis
  (https://github.com/crack666/smart_file_organizer/issues/42).

### Bundles

_Checked 12.11.2026 on the branch for
https://github.com/crack666/smart_file_organizer/issues/39, rebased on master after the library review, the evaluation corpus and the second check (bundles migration is step 11):
`dotnet test` 582 passed; `sfo scan`, `bundles build`, `bundles list`, `bundles show` and `summary` run by hand
with `--json` on a generated tree (a phone copy nested in two folders, 60 loose photos from three periods, one
named folder) against a temp database, output checked with a JSON parser: one `phone` dump with `camera`,
`chat_media` and noise parts, three events of 20, one folder. On the evaluation corpus (`sfo eval generate`)
the phone backup is one `phone` dump with `DCIM` as `camera` part; the two programs and two projects are
`software`._

- `sfo bundles build <job>` groups a scanned job without a model: dumps (`phone`, `computer`,
  `removable_drive`, `software`) with worth-keeping parts and noise, named folders taken as they are, loose
  photos and videos as events by pauses (capture date from `file_facts`, else date in the name, else
  modification time). `sfo bundles list|show`, `sfo files --bundle`, and `summary` with and without dumps;
  see [bundles.md](bundles.md).
- Capture dates exist only after `sfo analyze` has run Phase 2 on the photos; for a job that was only
  scanned, events rest on names and modification times (`period.time_source` says which).
- Not yet used by anything else: findings
  (https://github.com/crack666/smart_file_organizer/issues/26) and the plan
  (https://github.com/crack666/smart_file_organizer/issues/11) are meant to work per bundle.

### Known limits

- PDFs: embedded text of the first pages up to 6,000 characters, page 1 as
  image (plus the middle page for PDFs without text); no OCR beyond what the
  vision model reads from rendered pages; document properties are not read.
  (Until 12.11.2026: "only the first page is seen".)
- Not read at all: sound (no transcription), text of `.doc`/`.xls`/`.ppt`,
  listings of `.tar.gz`/`.gz`/`.bz2`/`.xz`/`.iso`, video frames when `ffmpeg`
  is not on the PATH. The evidence names each of these as missing.
- Files of units and system directories stay `Skipped` without a Phase 2
  result of their own; their directory verdict carries it.
- A missing `confidence` is no longer assumed: the result gets a second
  check (until 12.11.2026 this line read "If the model gives a valid
  category but omits `confidence`, the parser sets `0.85`").
- The second check and its thresholds are tested with a scripted model only
  (`SecondCheckTests`); how often the real model agrees with itself, and so
  how many extra calls a run costs, is not measured yet
  (https://github.com/crack666/smart_file_organizer/issues/10).
- `--retry-errors` retries files only; directories whose Phase 1 call failed
  and summaries already written are not redone by it.
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
  done. Since 12.11.2026 Phase 2 takes every `Discovered` file, so the next
  run on an older job also classifies its audio, archives and other files
  that were left out before. After changing the AI pipeline, start a new scan job or delete the
  database, otherwise old results are reused. Exception: Phase 1 decides by
  `directory_verdicts`, so jobs analysed before #7 get their verdicts (and one
  Ollama call per undecided directory) on the next AI run. Files that the old
  random sampling (before #8) marked `Skipped` stay without a result; start a
  new scan job for such data.
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
