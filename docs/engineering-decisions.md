# Engineering Decisions and Caveats

## Guiding constraints

This repository aims to stay:

- free to use
- based on open-source dependencies where practical
- cross-platform across Windows and Linux first
- maintainable over flashy short-term hacks

## Chosen libraries and rationale

## Avalonia UI

Chosen because it is:

- cross-platform desktop capable
- open source
- suitable for MVVM
- independent of web/HTML UI stacks

### Important design choice

We intentionally avoid paid Avalonia Accelerate components and did not base the product on `TreeDataGrid`.

Reason:

- keep the repository free/open-source-friendly
- avoid premium dependency lock-in
- keep long-term control over the core UX

## SQLite + Dapper

Chosen because they are a strong fit for a local desktop tool:

- SQLite is easy to distribute and works well for local persistent state
- Dapper keeps persistence explicit and lightweight
- the repository benefits from predictable SQL rather than heavy ORM machinery

## Docnet.Core for PDF preview

Chosen for first-page PDF rendering because it is:

- MIT licensed
- cross-platform
- capable of rendering PDF pages to image buffers
- sufficient for MVP-grade embedded preview without introducing paid components

It is referenced by `SmartFileOrganizer.Infrastructure` (not the desktop project), so the CLI and tests render PDF pages the same way as the GUI. Rendered pages are encoded with a small built-in PNG writer, so the core needs no imaging library.

### Trade-off

It introduces native PDFium runtimes. That is acceptable, but it means:

- enterprise environments may enforce additional application control policies
- packaging/runtime verification matters more than for pure managed libraries

## Metadata, tags and archive listings

Added 12.11.2026 for [#22](https://github.com/crack666/smart_file_organizer/issues/22): what can be read from
metadata is more reliable than any model answer and costs no GPU time. All three are pure managed code,
referenced by `SmartFileOrganizer.Infrastructure`, and run on Windows and Linux.

| Library | License | Used for |
|---|---|---|
| MetadataExtractor 2.9 | Apache-2.0 (pulls in XmpCore, BSD) | EXIF/PNG/QuickTime/MP4/AVI metadata of images and videos, without decoding pixels |
| z440.atl.core (ATL, Audio Tools Library) 7.19 | MIT | audio tags (ID3, Vorbis, MP4, ...) and duration |
| SharpCompress 0.50 | MIT | table of contents of ZIP, RAR, 7z and TAR archives |

Rejected: TagLibSharp (LGPL-2.1, not permissive). Office properties and text are read from the package XML with
`System.IO.Compression`, PDF page count and text through Docnet.Core, which was already there.

Archives are never unpacked: only the archive directory is read, at most 10,000 entries
(`ArchiveListingReader.MaxEntries`), so an archive bomb costs nothing. Compressed tarballs (`.tar.gz`, `.gz`, `.bz2`,
`.xz`) are therefore not listed; the evidence says so.

### Optional external tool: ffmpeg

Video still frames come from `ffmpeg` if it is found on the `PATH` (`VideoFrameExtractor`). It is not a dependency
and not shipped: without it videos get their container metadata only, and the evidence tells the model that frames
are missing. Each call has a 30 s timeout and reads one frame, scaled to 768 px width.

## System.CommandLine for the CLI

`sfo` parses arguments with System.CommandLine (MIT, maintained by the .NET team, version 2.0). It gives typed arguments and options, validation, help and typo suggestions without own parsing code.

- Parse errors are answered by `SfoApp` itself, so a wrong invocation ends with exit code 2 and, with `--json`, a JSON envelope; the library's default would be exit code 1.
- Ctrl+C is not left to the library: `Program` cancels a token, so a handler can store a scan as paused before the process ends.
- `Program` sets the invariant UI culture, because the library localises its messages and a calling model should see one language.

## Ollama HTTP API

Chosen because the project wants local AI inference rather than cloud-only dependency.

Benefits:

- privacy-friendly local execution
- flexible model selection
- good fit for offline-ish desktop workflows

Current state:

- the product now queries available local models from Ollama
- the chosen model can be warmed explicitly and kept loaded longer via `keep_alive`
- classification uses multimodal `/api/chat` requests instead of metadata-only `/api/generate`

### Important design choice

The AI classification path is intentionally **not** embedded into the scan loop.

Reason:

- scanning local files and calling a large multimodal model have very different performance characteristics
- the scan engine should remain fast and predictable even if model inference is slow
- pause/resume/cancel semantics are easier to reason about in a separate post-scan pipeline

Current posture:

- scan first
- classify afterward in a separate coordinator
- keep AI concurrency bounded: each phase runs `MaxParallelRequests` workers
  (default 4, checked in code 12.11.2026; this section previously said the
  path was kept sequential, which was no longer true)

## Important caveats encountered so far

## 1. "All black UI" problem

Symptom:

- controls rendered as apparently empty black surfaces even though data binding worked

Root causes:

- Avalonia `DataGrid` theme include was missing
- several panels relied too heavily on dark-theme defaults or transparent backgrounds

Resolution:

- added the DataGrid Fluent theme include to `App.axaml`
- applied explicit backgrounds/borders for the main panels

## 2. DataGrid showed data in logs but not visually

Symptom:

- tree selection worked and rows loaded, but the grid looked blank

Resolution:

- included DataGrid styles explicitly
- adjusted grid styling and column sizing

## 3. Preview image did not respond to pane size

Symptom:

- preview image stayed visually huge/similar regardless of splitter position

Root cause:

- the preview lived in a layout that effectively gave the image unbounded measurement

Resolution:

- converted preview area to a bounded grid-based layout
- separated metadata scrolling from preview sizing

## 4. PDF preview looked transparent on dark UI

Symptom:

- PDF content rendered directly over the dark panel background

Resolution:

- rendered the page with a white transparency remover so the preview looks like an actual page

## 5. Native runtime/policy sensitivity

When native PDFium files were introduced, startup behavior in Windows environments required extra care. Even when builds succeeded, environments with application control rules can behave differently than local developer machines.

Recommended posture:

- keep runtime selection explicit where possible
- validate on target environments early
- document native dependencies clearly

## 6. Ollama model warm-up and VRAM residency

Large local vision models can take a long time to load into VRAM on the first request.

Resolution/approach:

- exposed model warm-up in the UI
- pass `keep_alive` to Ollama requests
- allow the operator to choose a longer residency window for local workflows

Trade-off:

- keeping big models resident improves responsiveness
- but it also reserves GPU memory longer and may conflict with other GPU workloads

## 7. Evidence per file type: facts first, and say what is missing

Decided 12.11.2026 in [#22](https://github.com/crack666/smart_file_organizer/issues/22): quality comes before
throughput, and misassignments come from thin evidence. Until then the model saw images without capture data, PDFs
by their first page, videos by name only, and audio, archives and everything else not at all.

- Facts that metadata settle (capture date, device, page count, tags, archive listing summary) are stored as facts with
  their source in `file_facts`, not only written into the prompt. A later decision step can use them without the model
  ([#37](https://github.com/crack666/smart_file_organizer/issues/37)).
- Some facts are derived by a rule (`image_origin`, `audio_kind`, `archive_content`) and carry source `rule`.
- Every piece of evidence lists what it contains and what is missing, so the model does not guess (no frames, no tags,
  text cut after page 4).
- Location metadata are reduced to `has_location = true`; coordinates are never stored. For sensitive files,
  free-text facts (title, author, artist, album) are not stored.
- Files whose content no model can judge (programs, fonts, databases, unknown binaries) get a rule result instead of a
  model call, so Phase 2 leaves no file without a result.

Still missing: OCR beyond what the vision model reads from rendered pages, audio transcription, PDF document
properties, text of legacy binary Office formats, listings of compressed tarballs and ISO images.

## 8. Sensitive files: local and external readers

Decided 12.11.2026 in
[#9](https://github.com/crack666/smart_file_organizer/issues/9).

Old drives hold keys, password lists, private messages, ID scans, financial
and health records. Two kinds of reader see the results:

- **Local:** the Ollama model on this machine, and the user at the GUI. They
  may read the content of any file.
- **External:** anything that receives output of the tool, in particular a
  model outside the machine that drives the tool through the CLI, and anyone
  who gets an export. For a marked file it sees path, file type, marker and
  a neutral summary only. Never a text excerpt, the raw model response, the
  model's own summary, the suggested target or an error text.

How it is enforced:

- Markers (`zugangsdaten`, `schluessel`, `private_nachricht`, `ausweis`,
  `finanzen`, `gesundheit`; several per file) come from rules without a
  model, run during the scan so they also cover files the model never sees,
  and from an extra field in the regular classification schema (no second
  model call).
- `SensitiveOutputGuard.Project` in the Application project builds the only
  output record of a file. CLI commands and exports format that record and
  never read `FileNode` or `FileClassification` fields directly. The neutral
  summary is written by the guard from markers and file type, not by the
  model, so a model that quotes a password in its summary cannot leak it.
- The guard re-evaluates the name rules, so rows from older scans are
  protected without a backfill.
- `--include-sensitive` (`SensitiveOutputOptions.IncludeSensitive`) lifts the
  protection on purpose. It is meant for a human at the machine, never set by
  an external caller.
- Marked files are never proposed as disposable: a model `TrashCandidate`
  becomes `ReviewNeeded`. A user override stays as the user set it.
- Directory summaries are stored and shown as well, so a marked file enters
  the directory-summary prompt with its neutral line, not with its model
  summary.

Trade-off: the rules prefer false positives (a presentation `.key`, a file
named `Befund`), which only cost a manual review, over false negatives.

Not covered by the guard: the GUI (local by definition), log output (prompts
contain text excerpts), the SQLite file itself, directory-level output
(pre-assessment theme and anomaly reasons are model text about file names),
and the header rule for rows scanned before schema step 2.

## 9. Scan rules: in doubt, enter the folder

Decided 12.11.2026 in
[#21](https://github.com/crack666/smart_file_organizer/issues/21).

The tool exists to find user data on old drives. A user folder that a scan
skips or records only shallow is lost to every later phase; a program folder
scanned in full only costs time. The rules are weighted accordingly:

- The depth limit applies only to folders recognised as program folders
  (program files and no user files among their own files). Every other
  folder beyond `shallow_from` is entered, including folders whose content
  is not known. The former fixed skip from depth 12 became a safety limit
  (32) that records shallow, only against loops through links.
- Rules are explicit: a `shallow` or `skip` rule in a profile wins over the
  content check, because the person who wrote it decided.
- On resume only wider decisions are applied to stored folders; a narrower
  rule never deletes data already collected.
- The job stores its rules, not only the file path, so a resume or an
  investigation months later sees exactly the rules that produced the data.

Trade-off: deep program trees without executables (game assets, source
trees) are scanned in full. A profile can limit them with a `shallow` rule
and `max_depth`. Format and order of precedence: [scan-rules.md](scan-rules.md).

## Design stance going forward

Prefer:

- simple, inspectable dependencies
- open-source and permissive licenses
- predictable runtime behavior
- architecture that can evolve without rewriting the app shell

Avoid:

- premium UI lock-in
- browser-first pivots for a desktop-native tool
- heavy abstractions before the product flow is proven
