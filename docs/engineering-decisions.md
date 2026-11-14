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

Office and OpenDocument files are ZIP packages whose parts must be unpacked to read their text, so they get limits
instead (`OfficePackage`): at most 10,000 entries, counted from the end record before the directory is loaded; at most
16 MiB unpacked per part and 32 MiB for all parts read from one file, counted while unpacking so a lying directory
does not help; no document type definitions, so entity expansion cannot multiply the text. A file over a limit is not
read and the evidence names the limit (`document text: not read, ...`). The limits are far above what a document needs
for the 12,000 characters the model sees; a spreadsheet whose single sheet unpacks to more than 16 MiB is the price.

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
  their source in `file_facts`, not only written into the prompt. The decision step uses them without the model
  (`sfo decide`, [#37](https://github.com/crack666/smart_file_organizer/issues/37); see [11](#11-describe-once-decide-often)).
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

- Markers (`credentials`, `keys`, `private_message`, `identity`,
  `finance`, `health`; several per file) come from rules without a
  model, run during the scan so they also cover files the model never sees,
  and from an extra field in the regular classification schema (no second
  model call).
- `SensitiveOutputGuard.Project` in the Application project builds the only
  output record of a file. CLI commands and exports format that record and
  never read `FileNode` or `FileClassification` fields directly. The neutral
  summary is written by the guard from markers and file type, not by the
  model, so a model that quotes a password in its summary cannot leak it.
- The guard re-evaluates the name rules, so rows from older scans are
  protected without a backfill. The rules that read the file (header lines)
  cannot run at output time: each file stores the rule version that ran on
  it, and the next analysis runs the rules again for older rows before any
  model call (`SensitiveRulesBackfill`). Markers are only ever added.
- `--include-sensitive` (`SensitiveOutputOptions.IncludeSensitive`) lifts the
  protection on purpose. It is meant for a human at the machine, never set by
  an external caller.
- Marked files are never proposed as disposable: a model `TrashCandidate`
  becomes `ReviewNeeded`. A user override stays as the user set it.
- Directory output is protected at the source. Phase 1 (verdict, purpose,
  reason, anomaly reasons) sees names and the folder profile only, never
  content. Phase 3 gives a marked file to the model as its neutral line and
  stores which marked files entered that way; a summary written before a
  file of its directory was marked is withheld and written again by the next
  analysis (`DirectorySummaryGuard`). `DirectoryTextGuard` stays as a second
  net: a directory text that names a marked file is withheld.
- A directory sample inherits its suggested target from unmarked members
  only; a target suggested for a marked file is model text about it.
- `sfo compare` leaves out the full checksum of a marked file (and of a file
  whose match is marked): for a short secret it confirms a guess offline.
- Folders the scan skips without entering (`.ssh`, `.gnupg`, wallets,
  `AppData`, ...) are marked by their name, so a caller learns that a
  sensitive folder exists without the scan ever reading it.
- Logs: prompts and model answers are written under the category
  `ModelTraffic` at Debug level. The CLI never writes that category to
  stderr; with `--verbose` it goes to `sfo-debug.log` next to the database
  instead, and for a marked file even there the prompt and answer are
  replaced by a fixed placeholder. Warnings and errors carry status codes,
  not response bodies.

Trade-off: the rules prefer false positives (a presentation `.key`, a file
named `Befund`), which only cost a manual review, over false negatives.

Decided in [#33](https://github.com/crack666/smart_file_organizer/issues/33)
what stays outside the boundary on purpose:

- **The GUI:** local by definition; it shows everything to the user at the
  machine.
- **The SQLite file itself:** it holds extracted text, model answers, facts
  and checksums of every file, marked or not. Whoever can read the database
  file can read the content. It is protected like any other local file, by
  the account it lives in; do not hand it out.
- **`sfo-debug.log`:** a local file next to the database with the prompts of
  unmarked files. It exists only after a run with `--verbose`.
- **Facts stored before a file was marked** are dropped when the header check
  catches up, but facts are not CLI output in the first place.

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

## 10. Library match: folder names in the output, rules before the model

Decided 12.11.2026 in
https://github.com/crack666/smart_file_organizer/issues/25.

`sfo library show` and `sfo library match` print folder names and paths of the library in
their CLI output, including to an external caller. Scanning a tree with `--role library` is
the person's release of that archive as the target to file into: its folder names are what
the tool is asked to continue. File contents are never read for it (the library is scanned
`--structure-only`), and the model, which runs locally, gets folder names, counters and
patterns only. File names of the library are not printed; the source side keeps the
protection of [8](#8-sensitive-files-local-and-external-readers).

The proposals are rules first: the map, the signals and the certainty are complete with
`--no-model`. Certainty comes from how many independent signals agree (same content, period,
name, neighbours) and from contradictions, not from a model number, so a person can check
every proposal from its one-sentence reason. When nothing reaches the minimum score, a new
folder is proposed instead of a forced hit, and a weak place at the target (review findings)
caps the certainty instead of being continued silently. A named source folder is never
renamed or split.

Trade-off: the thresholds are starting values without a measured library yet; they are
settings (`LibraryMatch`), not constants.

## 11. Describe once, decide often

Decided 13.11.2026 in https://github.com/crack666/smart_file_organizer/issues/37. One model call used to say at once
what a file is, how much it is worth and where it should go. A changed target structure or rule then meant analysing
again, and a wrong result did not show whether the model had seen wrong or decided wrong. The measurement of 12.11.2026
found the categories right with every model; the errors lay in markers and folder verdicts.

- Stage 1 describes: category, markers and, in the same call, a few fixed facts of what is to be seen (scene, occasion,
  people of a picture; kind, issuer, year of a document), each stored with its source. No second call, no longer
  fixed part: the fields come through the answer schema, only for pictures and documents.
- Stage 2 decides by rule from what is stored (`sfo decide`): worth, and a target from a target structure. It has no
  model service, so its cost is a database read, and each decision names the inputs it rests on: a wrong decision
  with a right fact is a rule to fix, a wrong fact is a description to fix.
- A marked file is not described, and its decision rests on its marker alone.

Trade-off: the description costs a few dozen answer tokens per picture and document. Importance and keep hint are
still asked in stage 1; moving them into stage 2 needs the measurement of the corpus first. A model call over facts
for what no rule can judge is left for later.

## 12. Assessments belong to the content

Decided 13.11.2026 in https://github.com/crack666/smart_file_organizer/issues/38. Stage 1 is the expensive part, and
it was paid per path: the same file on the next old disk, renamed, or in a new scan went to the model again.

- What the model said about a content is kept under its full SHA-256 and size, across jobs, and taken over before a
  model call. Only a full checksum says "the same content"; a sample checksum and names never do.
- Taken over only with the same model and the same prompt version. The version is a hash of the classification prompt
  itself, not a number in code: a number would be forgotten once, and then a changed prompt would silently reuse old
  answers. Old entries stay (`stale`) until cleared, so switching back costs nothing.
- Only what belongs to the content is kept. Folder verdict, findings, target and decision depend on the place and are
  made anew in every job; an answer shaped by a second call, which sees the folder, is not kept. A person's correction
  of a category belongs to the content and follows it.
- A taken-over result says so (`reused`), and `sfo status` counts it apart from model calls, so a fast run is not
  mistaken for a fast model. An evaluation never takes over.

Trade-off: every file the model would judge is read once more for its checksum (stored, so only once per state of the
file). The evidence preparation is not part of the prompt version: a change there (more PDF text, more frames) needs
`sfo cache clear` to take effect on content judged before.

## 13. Executing a plan: copy, check, hold, then purge on its own

Decided 14.11.2026 in https://github.com/crack666/smart_file_organizer/issues/12 and its addendum of 12.11.2026, which
replaced "no deletion" by "less lies twice at the end". The tool may now take an original away, so how it does that
was decided for safety first, speed second.

- Always copy, never rename into the library, even on one drive. A rename would be quicker, but then the original is
  gone in the same instant as it arrives, and the person could not confirm the removal on its own after seeing every
  copy checked. Two steps, each its own command, is the rule of the addendum.
- A checksum counts only when read just now. Before an original goes, both files are read whole; a stored checksum
  (fingerprint cache, `match_hash`) only has to agree as well. A file overwritten with the same size and time keeps its
  old value in the cache, and the cache would then approve a removal it should refuse.
- A hold folder on the drive of the original instead of the system recycle bin. It works the same on every system and
  medium (a USB stick has no recycle bin), `undo` can use it, and only renaming happens there, which loses nothing if a
  run is cut off. Purging is a separate command that refuses while the copy at the target is not the original's
  content; the recycle bin is not used at all.
- "Cannot tell whether these are one file" refuses like "one file". The comparison does not detect hard links or a
  volume mounted twice; the file identity of the system does, and where it gives none the original stays.
- Nothing is ever overwritten. A file at the target with the same content counts as present (and is never removed by
  `undo`); with other content the action fails. The issue text first proposed a suffix to the name; failing was chosen
  instead, because a second copy under another name is exactly the redundancy the package is meant to remove.
- A journal row before every touch of the file system, finished after. With copies only named after their check and
  originals only renamed, every state between two rows can be settled from what lies on disk.

Trade-off: step 1 needs room for a full copy before anything is freed, and every original is read three times (copy,
check before removal, the copy once more). The cache of the system is not bypassed when a copy on a network drive is
read back.

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
