# Command line: `sfo`

`sfo` drives the same core as the desktop app: scan a folder into the database, run the
AI phases, and read where a job stands. It is meant for people and for a controlling
model alike; everything a machine needs is in [Machine contract](#machine-contract).

```bash
dotnet run --project src/SmartFileOrganizer.Cli -- <command> [options]
# or, after a build:
dotnet src/SmartFileOrganizer.Cli/bin/Debug/net10.0/sfo.dll <command> [options]
```

Issue: https://github.com/crack666/smart_file_organizer/issues/5

## Global options

Accepted by every command.

| Option | Meaning |
|---|---|
| `--json` | Write exactly one JSON document to stdout. |
| `--progress none\|text\|jsonl` | Progress on stderr. Default `text`, or `none` with `--json`. |
| `--db <path>` | SQLite database. Overrides `SFO_DB`. |
| `-v`, `--verbose` | Log at information level on stderr (default: warnings and errors). Prompts and model answers never go to stderr; with `--verbose` they are written at debug level to `sfo-debug.log` next to the database (appended, rotated to `sfo-debug.log.1` above 50 MB). For a marked file even that file holds a placeholder instead of prompt and answer. |
| `-h`, `--help` | Help for the command. |

Database and settings are found as described in
[architecture.md, File locations](architecture.md#file-locations): `--db`, then `SFO_DB`, then
the per-user default. With a redirected database, settings are read from `settings.json` next
to it (or from `SFO_SETTINGS`), so a scratch database never touches the user's settings.
The CLI never writes `settings.json`; `--model` and `--parallel` apply to one run only.

## Commands

### `sfo scan <path>` / `sfo scan --resume <job>`

Creates a job for `<path>` and scans it, or continues an interrupted job. Give exactly one of
the two. Ctrl+C stores the job as `paused`; `--resume` continues it. Resuming a completed job
returns it unchanged.

`--rules <file>` scans with a rule file (profile, see [scan-rules.md](scan-rules.md)) and
stores it at the job; without it the shipped rules are used. With `--resume`, rules that
differ from the stored ones switch the job to them, reopen it even if it was completed, and
re-evaluate the folders stored as skipped or shallow; `rules_changed` is then `true`.

| Option | Meaning |
|---|---|
| `--role source\|library` | Only with `<path>`. `source` (default): a medium to clean up. `library`: the user's organised archive (NAS, a folder on disk); it is only read, never changed and never analysed by a model (`sfo analyze` refuses it with `wrong_role`). The role is fixed when the job is created. |
| `--structure-only` | Only with `<path>`. Records names, sizes and dates and opens no file: sensitive files are marked by name and extension only, without looking at their first bytes. The job keeps the flag (`structure_only` in the job view), so a later `--resume` scans the same way. Use it for `sfo library review`. |

A directory the scan cannot read (access denied, network path gone) is stored with that error
and counted in `error_count` instead of looking empty; the scan itself continues and completes.

```bash
sfo scan /media/usb-stick --json
sfo scan /mnt/nas/library --role library --json
```

```json
{
  "schema_version": 1,
  "command": "scan",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job": {
      "id": 7,
      "root_path": "/media/usb-stick",
      "role": "source",
      "status": "completed",
      "created_at": "2026-11-12T13:36:42.138Z",
      "started_at": "2026-11-12T13:36:42.176Z",
      "completed_at": "2026-11-12T13:36:44.210Z",
      "files_total": 1830,
      "files_processed": 1830,
      "directories_total": 112,
      "directories_processed": 112,
      "error_count": 0,
      "last_error": null,
      "rules": { "name": "default", "source": "built-in", "hash": "3f9c…" }
    },
    "resumed": false,
    "rules_changed": false
  },
  "error": null
}
```

`status` is one of `created`, `running`, `paused`, `completed`, `cancelled`, `failed`.
`rules` is `null` for a job created before rules were stored and not run since.
Errors: `path_not_found` (1, also for a missing rule file), `rules_invalid` (1),
`job_not_found` (1), `scan_failed` (1, with `result`).

### `sfo analyze <job>`

Runs the AI phases for a job whose scan is `completed` and waits until they are done. Each
phase skips work already stored, so running it again after Ctrl+C or an error continues.

| Option | Meaning |
|---|---|
| `--phase <name>` | `pre_assessment`, `classification`, `summary` or `all` (default). Repeatable; phases always run in this order. |
| `--parallel <n>` | Concurrent Ollama requests, 1 to 8. Default: `Ollama.MaxParallelRequests`. |
| `--model <name>` | Ollama model for this run. Default: `Ollama.Model`. |
| `--retry-errors` | Classification: try files that failed in an earlier run again. Files with a result are never called again; files still pending are judged as in any run; files a person corrected are left alone. Summary: write again the summaries that failed and those older than the results they rest on, so the folder of a retried file gets a summary that knows it. Failed directory verdicts (phase 1) are asked again in every run, with or without this option. |

```bash
sfo analyze 7 --phase classification --parallel 2 --json
```

```json
{
  "schema_version": 1,
  "command": "analyze",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job_id": 7,
    "model": "gemma4:26b",
    "parallel": 2,
    "phases": [
      { "phase": "classification", "total": 940, "processed": 940, "errors": 3,
        "message": "AI classification finished with 3 error(s). 41 result(s) checked a second time.",
        "retried": 0, "second_checks": 41 }
    ],
    "error_count": 3,
    "model_server_lost": null,
    "model_calls": [
      { "kind": "classification", "calls": 899, "prompt_tokens": 2037963, "cached_prompt_tokens": 1650000,
        "answer_tokens": 54000, "mean_seconds": 0.95, "cut_off": 0 },
      { "kind": "second_check", "calls": 41, "prompt_tokens": 98000, "cached_prompt_tokens": 70000,
        "answer_tokens": 2500, "mean_seconds": 1.02, "cut_off": 0 }
    ]
  },
  "error": null
}
```

`model_calls` has one entry per kind of call made in this run (`pre_assessment`, `classification`,
`second_check`, `summary`, see [glossary.md](glossary.md#kind-of-a-model-call-modelcallkinds)), with the
counts the model server returns with every answer: `prompt_tokens` (`prompt_eval_count`), of them
`cached_prompt_tokens` taken from the server prompt cache (`prompt_eval_cached_count`), `answer_tokens`
(`eval_count`), `mean_seconds` (prompt and answer time per call as the server measured it) and `cut_off`
(answers that stopped at the token limit and were asked again). A repeated call counts again. The numbers
in the example are illustrative. The text output prints the same as one line per kind.

`retried` is the number of failed files `--retry-errors` released, `second_checks` the number of
second calls made for unsure or contradicted results (see
[architecture.md](architecture.md#phase-2--per-file-classification-in-stages-classificationservice-directorysamplingservice));
both are 0 for the other phases. Errors of single files or directories do not fail the command; they are counted here and
listed by `sfo status`. Failures: `ollama_unavailable` (3), `model_server_lost` (4),
`scan_not_completed` (1), `job_not_found` (1), `wrong_role` (1, the job is a `library`), `cancelled` (130).

**When the model server goes away during the run.** `ollama_unavailable` (3) is only the check at
the start: nothing has been asked yet. Once the run has started, a server that stops answering is
waited for, not counted as errors of the files
([architecture.md](architecture.md#when-the-model-server-goes-away)). With `--progress jsonl` each
change is one `wait` line on stderr:

```json
{"schema_version":1,"type":"wait","phase":"classification","state":"waiting","since":"2026-11-13T09:12:04Z","cause":"connection_failed","waited_seconds":0,"message":"waiting for the model server since ..."}
{"schema_version":1,"type":"wait","phase":"classification","state":"resumed","since":"2026-11-13T09:12:04Z","cause":"connection_failed","waited_seconds":41.5,"message":"model server answers again after 42s; continuing"}
```

`state` and `cause` are codes from the [glossary](glossary.md#model-server). When the server stays
away longer than `ModelServer.MaxWaitMinutes`, a `given_up` line follows and the command ends with
exit code 4, `model_server_lost`. `result` is then filled as usual plus `model_server_lost`:
`{ "phase": "classification", "since": "...Z", "cause": "connection_failed", "waited_seconds": 1800 }`.
The files and folders not judged stay pending, none is stored as an error; `sfo analyze <job>` again
finishes them.

### `sfo jobs`

Lists all jobs, newest first.

```bash
sfo jobs --json
```

```json
{
  "schema_version": 1,
  "command": "jobs",
  "ok": true,
  "exit_code": 0,
  "result": { "jobs": [ { "id": 7, "root_path": "/media/usb-stick", "status": "completed", "...": "same fields as scan" } ] },
  "error": null
}
```

### `sfo status <job>`

Counters per phase and the most recent errors. `--max-errors <n>` (default 20) limits the list.

```bash
sfo status 7 --json
```

```json
{
  "schema_version": 1,
  "command": "status",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job": { "id": 7, "status": "completed", "...": "same fields as scan" },
    "phases": {
      "scan": { "status": "completed", "files_total": 1830, "files_processed": 1830,
                "directories_total": 112, "directories_processed": 112, "errors": 0 },
      "pre_assessment": { "total": 104, "done": 104, "errors": 0, "pending": 0, "skipped": 0 },
      "classification": { "total": 940, "done": 610, "errors": 3, "pending": 0, "skipped": 327 },
      "summary": { "total": 104, "done": 0, "errors": 0, "pending": 104, "skipped": 0 }
    },
    "errors": [
      { "phase": "classification", "path": "/media/usb-stick/scan_004.pdf",
        "message": "Ollama request timed out after 180s." }
    ],
    "scan_errors": [
      { "cause": "access_denied", "directories": 97, "files": 0,
        "examples": [ { "path": "C:/Users/Old/Local Settings", "message": "Read error: Access to the path ... is denied." } ] }
    ],
    "links": [
      { "kind": "junction", "not_followed": 12, "followed": 0, "files": 0,
        "examples": [ { "path": "C:/Users/All Users", "target": "C:/ProgramData", "followed": false } ] }
    ]
  },
  "error": null
}
```

`phases.scan.errors` counts the folders and files the scan could not read. `scan_errors` groups them by
cause (`access_denied`, `not_found`, `path_too_long`, `io_error`) with counts and the first three paths of
each; `errors` lists the AI phases only. `links` lists the links the scan found, by kind (`junction`,
`symlink`, `mount_point`): folders not entered, folders a `follow_link` rule entered, and files that are
links; none of them counts as content ([scan-rules.md](scan-rules.md#links-and-read-errors)). After a
finished scan `directories_total` equals `directories_processed` and `files_total` equals `files_processed`:
both are counted from the stored rows (since 13.11.2026; before, the scan root was missing from the total).

`model_server_wait` is present only while an analysis of the job waits for the model server:
`{ "since": "...Z", "checked_at": "...Z", "cause": "connection_failed", "stale": false }`. `checked_at`
is the last probe. `stale` is `true` when no probe was recorded for longer than three probe intervals
(at least one minute): the waiting run was killed, and `sfo analyze <job>` continues it.

For each AI phase, `total = done + errors + pending + skipped`.

- `pre_assessment` and `summary` count directories the AI phases consider: not skipped by the
  scan rules and not hidden (name starting with a dot).
- `classification` counts every file (since 12.11.2026; before: only image, video, document).
  Files a rule decides (programs, fonts, unknown binaries) count as `done` without a model call. `done`
  includes files that inherited the result of their directory's confirmed sample; `pending`
  includes files waiting for that sample. `skipped` are files of units and system directories,
  which are not analysed one by one.

### `sfo compare <job> --against <job>`

Which files of a `source` scan are already in the `library` (the user's organised archive), by
content, not by name. Codes in JSON and on the command line are English; this command never
deletes or changes anything. Both scans must be `completed` and both roots reachable. Issue:
https://github.com/crack666/smart_file_organizer/issues/24

| Option | Meaning |
|---|---|
| `--against <job>` | The `library` job. Without it, only duplicates within the source are found and every `state` is `null`. |
| `--files <state>` | List the files with this state (`backed_up`, `new`, `differs`, `error`, or `all`). Repeatable. Default: no file list. |
| `--limit <n>` | Maximum entries per list (`directories`, `errors`, `duplicates.groups`, `files`); default 100, `0` lists everything. The `*_total` fields count all. |

State per source file:

| `state` | Meaning |
|---|---|
| `backed_up` | A file with exactly the same content is in the library: the full SHA-256 of both files is equal (head and tail alone never count). `match_path` is one of them (first by path), `match_count` how many, `match_hash` the shared full SHA-256 and `match_verified_at` the older of the two times that checksum was computed from the files (possibly in an earlier run, see the cache below). A step that deletes the source file must read both files again and compare with `match_hash` first. For a marked file, or a file whose match is marked, `match_hash` is `null`: for a short secret the checksum would confirm a guess of its content. |
| `new` | No library file has this content, and none has the same name in a folder of the same name. |
| `differs` | Not in the library by content, but a file with the same name lies in a folder of the same name there (`match_path`): probably another version. |
| `error` | The content could not be determined: the source file is unreadable, gone or changed since its scan, or a library file of the same size that could hold this content cannot be read. `error` says which. Never counted as `new` or `backed_up`. |

How content is compared, so a large library on a network drive is read as little as possible:
size first; for files sharing a size, SHA-256 over the size and the first and last 64 KiB; only
where those agree, SHA-256 over the whole file. Files up to 128 KiB are read whole at once.
Checksums are stored per path and reused while size and modification time are unchanged, so a
second run without changes reads no file content (`hashing` shows the counts). A file never
matches itself, even when the source lies inside the library.
Folders a scan skipped by rule (`dir_status` skip) are not part of the comparison, on either side: no file
below them is compared and no read error below them is reported, even when the folder is gone by now.

`duplicates` groups source files with identical content (empty files excluded); a file's
`duplicate_group` points to its group. `directories` counts each source directory including its
subdirectories: a directory with `backed_up_share` 1.0 is fully in the library and needs no model
analysis; otherwise only its files that are not `backed_up` do.

```bash
sfo compare 7 --against 8 --files new --files error --json
```

```json
{
  "schema_version": 1,
  "command": "compare",
  "ok": true,
  "exit_code": 0,
  "result": {
    "comparison_id": 3,
    "source": { "id": 7, "root_path": "/media/usb-stick", "role": "source", "...": "same fields as scan" },
    "against": { "id": 8, "root_path": "/mnt/nas/library", "role": "library", "...": "same fields as scan" },
    "compared_at": "2026-11-12T15:02:11.410Z",
    "complete": true,
    "counts": { "files": 1830, "backed_up": 1712, "new": 101, "differs": 17, "errors": 0 },
    "duplicates": {
      "groups_total": 1, "files": 2,
      "groups": [ { "group": 1, "size": 48211, "paths": [ "/media/usb-stick/a.jpg", "/media/usb-stick/copy/a.jpg" ] } ]
    },
    "directories_total": 112,
    "directories": [
      { "path": "/media/usb-stick", "files": 1830, "backed_up": 1712, "new": 101, "differs": 17, "errors": 0,
        "backed_up_share": 0.9355 }
    ],
    "errors_total": 0,
    "errors": [],
    "files_total": 101,
    "files": [
      { "path": "/media/usb-stick/notes/2014.txt", "state": "new", "match_path": null, "match_count": 0,
        "match_hash": null, "match_verified_at": null, "duplicate_group": null, "error": null }
    ],
    "hashing": { "head_tail_computed": 240, "full_computed": 1716, "reused": 0 }
  },
  "error": null
}
```

Errors are listed in `errors` with `role` (`source` or `library`), `kind` (`scan`: a directory the
scan could not read, so its files are missing from the job; `read`: a file that could not be read
now) and `path`. If there is any, `complete` is `false` and the command exits 1 with
`compare_incomplete`; the result is still returned and stored. Other failures: `job_not_found` (1),
`scan_not_completed` (1), `wrong_role` (1, the first job is not a `source` or `--against` not a
`library`), `path_not_found` (1, a root is not reachable), `cancelled` (130; checksums computed so
far are kept).

The result is stored per source and library job (a new run replaces it) and dropped when either
job is deleted or a directory of it is rescanned. Not decided here: a file the library holds
under the same content but whose scan was skipped by the scan rules counts as `new`; files that
changed in the library after its scan are judged by the library scan's sizes.

### `sfo doctor`

Checks the database (creates and migrates it if missing), whether Ollama answers within
5 seconds, and whether the configured model is installed.

```bash
sfo doctor --json
```

```json
{
  "schema_version": 1,
  "command": "doctor",
  "ok": false,
  "exit_code": 3,
  "result": {
    "ok": false,
    "database": { "path": "/home/me/.config/SmartFileOrganizer/data.db", "existed": true,
                  "schema_version": 1, "ok": true, "error": null },
    "settings_path": "/home/me/.config/SmartFileOrganizer/settings.json",
    "ollama": { "base_url": "http://localhost:11434", "reachable": false },
    "model": { "name": "gemma4:26b", "available": null, "installed": [] }
  },
  "error": { "code": "ollama_unavailable", "message": "Ollama is not reachable at http://localhost:11434." }
}
```

`model.available` is `null` when Ollama could not be asked. Exit code: 1 for a database
error (`database_error`), 3 if Ollama is unreachable, 1 if the model is missing
(`model_missing`), else 0.

### `sfo rules check <path>...`

Shows for each path which rule applies and why, without scanning and without opening the
database. The paths need not exist; if one does, its files are looked at where the decision
depends on them (beyond `shallow_from`), otherwise that check reports `content_unknown`.

| Option | Meaning |
|---|---|
| `--root <dir>` | Scan root the paths are checked against. Default: the drive or file system root of each path. A path outside the root is a usage error (2). |
| `--rules <file>` | Rule file to check with. Default: the shipped rules. |

```bash
sfo rules check /media/disk/app/bin /media/disk/Documents /media/disk/app/bin/Documents --root /media/disk --json
```

```json
{
  "schema_version": 1,
  "command": "rules check",
  "ok": true,
  "exit_code": 0,
  "result": {
    "rules": { "name": "default", "source": "built-in", "hash": "3f9c…" },
    "checks": [
      { "path": "/media/disk/app/bin", "root": "/media/disk", "depth": 2, "exists": true,
        "decision": "skip", "basis": "rule",
        "reason": "Skip: matches skip rule 'bin' of rules 'default': build output",
        "rule": { "index": 4, "match": "bin", "action": "skip", "max_depth": null, "why": "build output" },
        "content": null, "reached": true, "blocked_by": null },
      { "path": "/media/disk/Documents", "...": "decision descend, rule user_data" },
      { "path": "/media/disk/app/bin/Documents", "decision": "descend", "reached": false,
        "blocked_by": { "path": "/media/disk/app/bin", "decision": "skip",
                        "reason": "Skip: matches skip rule 'bin' of rules 'default': build output" },
        "...": "" }
    ]
  },
  "error": null
}
```

- `decision`: `descend`, `shallow` or `skip`.
- `basis`: `scan_root`, `rule`, `safety_limit`, `hidden`, `no_rule`, `user_content`,
  `program_content`, `no_program_content`, `content_unknown`.
- `rule`: the matching rule and its position in the file, or `null`.
- `content`: `{ "files", "user_files", "program_files" }` when the content decided, else `null`.
- `reached`: `false` if a folder between root and path is skipped or shallow; `blocked_by` names
  the first one. The path's own `decision` is then what would apply if it were reached.

Errors: `path_not_found` (1) or `rules_invalid` (1) for the rule file, `usage` (2).

### `sfo rules show`

Prints the rules with every setting spelled out; `--rules <file>` shows that file instead of
the shipped rules. Without `--json` the output is a valid rule file, so
`sfo rules show > my-disk.json` starts a profile. With `--json`, `result` is
`{ "rules": { "name", "source", "hash" }, "content": { ...the rule file... } }`.

### `sfo eval <corpus>`

Scans `<corpus>/<root>` (from `<corpus>/truth.json`) into a new job, runs the pre-assessment and
the classification, and scores directory verdicts, categories and sensitive markers against the
truth. The run is stored for `--compare`. Corpus, truth format and what counts:
[evaluation.md](evaluation.md). Phase 3 (directory summaries) is left out on purpose: nothing it
writes is measured, and it would cost a model call per mixed folder. `sfo status <job>` therefore
shows the summary phase as pending; `sfo analyze <job>` writes the summaries if you want them.

| Option | Meaning |
|---|---|
| `--stub <answers.json>` | Answer from this file instead of Ollama. Same shape as `truth.json`; the truth itself is a perfect answer sheet. |
| `--model <name>` | Ollama model for this run. Not saved. |
| `--parallel <n>` | Concurrent Ollama requests for this run, 1 to 8. |

```bash
sfo eval ~/sfo-eval --db ~/sfo-eval.db --json
```

```json
{
  "schema_version": 1,
  "command": "eval",
  "ok": true,
  "exit_code": 0,
  "result": {
    "run_id": 4,
    "mode": "corpus",
    "subject": "small v1 seed 36",
    "job_id": 9,
    "model": "gemma4:26b",
    "stub": null,
    "prompt_fingerprint": "c5304d1e2d5295a4",
    "app_version": "1.0.0+525c535…",
    "settings": { "ollama.max_parallel_requests": "4", "scan.rules": "default 3f9c…", "...": "" },
    "scan_seconds": 0.41,
    "analyze_seconds": 212.7,
    "scores": [
      { "kind": "directory_verdict", "total": 41, "hits": 35, "wrong": 6, "missing": 0, "accuracy": 0.8537, "sensitive": null },
      { "kind": "file_category", "total": 49, "hits": 44, "wrong": 5, "missing": 0, "accuracy": 0.898, "sensitive": null },
      { "kind": "sensitive", "total": 97, "hits": 93, "wrong": 3, "missing": 1, "accuracy": 0.9588,
        "sensitive": { "expected_marked": 13, "fully_found": 10, "missed": 3, "false_alarms": 1 } }
    ],
    "failures": [
      { "kind": "file_category", "key": "file_category:Fotos/2019 Skifahrt/IMG_20190212_101530.jpg",
        "path": "Fotos/2019 Skifahrt/IMG_20190212_101530.jpg", "outcome": "wrong",
        "expected": "photo", "actual": "screenshot", "decided_by": "model gemma4:26b", "detail": null }
    ],
    "untracked_count": 0,
    "untracked": [],
    "cases": null,
    "model_calls": []
  },
  "error": null
}
```

`model_calls` counts the calls and tokens of this run as for
[`sfo analyze`](#sfo-analyze-job); it is empty with `--stub`, which makes no model call.

(Numbers illustrative.) `kind` is `directory_verdict`, `file_category` or `sensitive`; `outcome`
is `wrong` or `missing` (`hit` appears only in `cases`). `expected` and `actual` are codes; markers
are comma-separated, `none` for no marker. `decided_by` is `rule <id>`, `model <name>`, both
for markers, or `sample` when the sample check of the staged analysis raised a uniform folder to
mixed or a file took its category from a confirmed sample. `key` is stable across runs. `untracked` lists (up to 50) paths the scan found that the
truth does not list.

Errors: `path_not_found` (1, corpus, truth or answer file), `truth_invalid` (1, with every problem
in the message), `scan_failed` (1), `ollama_unavailable` (3), `cancelled` (130).

### `sfo eval --cases`

Runs every stored test case (corrections made in the review, see
[evaluation.md](evaluation.md#test-cases-from-corrections)) against the current model and prompts,
and stores the run. Accepts `--stub`, `--model`, `--parallel`. `result` has the shape above with
`mode` `cases`, `job_id` and `scan_seconds` `null`, one score (`file_category`), and `cases`:

```json
"cases": [
  { "case_id": 3, "kind": "file_category", "path": "/media/usb-stick/Fotos/IMG_0001.jpg",
    "expected": "photo", "corrected_from": "screenshot", "actual": "photo", "outcome": "hit",
    "sensitive": false, "detail": null }
]
```

`corrected_from` is the model's answer when the human corrected it; `actual` is the answer now.
No output ever contains the stored evidence. Progress phase: `eval_cases`.

### `sfo eval --compare [<run> [<run>]]`

Sets two stored runs side by side. No id: the newest run against the newest earlier run with the
same mode and subject. One id: that run against the one before it. Two ids: the first as before,
the second as after.

```json
"result": {
  "before": { "id": 3, "mode": "corpus", "subject": "small v1 seed 36", "model": "gemma4:26b", "...": "" },
  "after": { "id": 4, "...": "" },
  "same_subject": true,
  "changes": [ { "name": "model", "before": "gemma4:26b", "after": "other-model" } ],
  "scores": [ { "kind": "file_category", "before_hits": 44, "before_total": 49, "before_accuracy": 0.898,
                "after_hits": 46, "after_total": 49, "after_accuracy": 0.9388, "change": 0.0408 } ],
  "fixed": [ { "kind": "file_category", "key": "...", "...": "failure of the earlier run" } ],
  "new_failures": [ { "...": "failure of the later run" } ],
  "still_failing": 3
}
```

`changes` covers model, stub, prompt fingerprint, app version and every stored setting.
Errors: `run_not_found` (1, unknown id or no earlier run to compare with).

### `sfo eval generate <dir>`

Writes the small built-in corpus to `<dir>` (must be empty or missing): `<dir>/tree/` and
`<dir>/truth.json`. `--seed <n>` (default 36) selects the variant; the same seed gives the same
corpus. `result`: `{ "directory", "truth", "root", "name", "generator_version", "seed",
"directories", "files" }`. Errors: `output_not_empty` (1).

## Reading results

Issue: https://github.com/crack666/smart_file_organizer/issues/6

`summary`, `dirs`, `files`, `show` and `export` read a job without changing it. They are
built for a model that reads in small, predictable pieces: start with `sfo summary`, narrow
down with `dirs` and `files`, look at single entries with `show`.

**Paths.** Wherever a command takes a path (`--under`, `--dir`, `show`), give the full path
as listed, or a path relative to the job root (`photos/2024`, `.` for the root).

**Verdict codes.** `--verdict` and `--retention` accept the codes the core defines in
`VerdictCodes` (`--verdict mixed`, `--retention keep`; all codes and their meaning in
[glossary.md](glossary.md)), plus `error` (only an error is stored for the directory) and `none`
(not judged yet, or no retention). JSON output carries the same codes; the German words used
before 12.11.2026 (`gemischt`, `behalten`) are no longer accepted. `sfo dirs --help` always lists
the current codes.

**Words in the text output.** Without `--json`, verdict, retention, verdict source, result origin
and sensitive markers are shown as words for people, in German when the summary language in the
settings is German (`"SummaryLanguage": "Deutsch"`), in English otherwise (`mixed` / `gemischt`,
`credentials` / `Zugangsdaten`). Programs read the JSON, which never changes with the language.

### Lists and paging

`dirs` and `files` return one page:

```json
{
  "job_id": 7,
  "total": 1830,
  "limit": 200,
  "offset": 0,
  "next_offset": 200,
  "items": [ ... ]
}
```

| Option | Meaning |
|---|---|
| `--limit <n>` | Items per page, 1 to 1000. Default 200. |
| `--offset <n>` | Items to skip. Use `next_offset` from the previous page; it is `null` on the last page. |

Order is stable (ties are broken by path), so paging with `next_offset` sees every item once.

Filters shared by `dirs` and `files` (for `files` they apply to the directory holding the file):

| Option | Meaning |
|---|---|
| `--verdict <code>` | Directory verdict, `error` or `none`. Repeatable; any value matches. |
| `--retention <code>` | Retention code or `none`. Repeatable. |
| `--sensitive yes\|no` | `yes`: only marked files, or directories with marked files in their subtree. `no`: the others. |
| `--include-sensitive` | Lift the protection described below. For a person at this machine only. |

### Sensitive files

Every file passes `SensitiveOutputGuard` (see
[engineering-decisions.md](engineering-decisions.md#8-sensitive-files-local-and-external-readers)).
A marked file shows path, type, markers (`sensitive`), `protected: true`, a neutral summary,
category `review_needed`, and `null` for confidence, importance, target and model output.
Filters see the same values: `--category trash_candidate` never returns a marked file, and
`--max-confidence` leaves marked files out.

Model texts about a directory (purpose, reason, summary, error texts, raw verdict answer,
anomaly reasons) are replaced by a neutral hint when they name a marked file of the subtree
(full name, or the name without extension if it has at least 4 characters). The item then has
`text_withheld: true`. This name check is a second net; the protection is at the source: the
verdict model sees names and the folder profile only, and the summary model sees a marked file
as its neutral line. A summary written before a file of its directory was marked is shown as
a stale hint (summary, theme, anomaly reasons and error withheld, `text_withheld: true`) until
the next `sfo analyze` writes it again
(https://github.com/crack666/smart_file_organizer/issues/33).

Folders the scan skipped without entering are marked by their name (`.ssh`, `.gnupg`,
`.aws`, wallets, `AppData`, browser and mail profiles). `dirs` and `show` give such a folder
`sensitive` (marker tokens) and `sensitive_rule` (for example `dir:ssh`); `sensitive_dirs`
counts marked folders in the subtree, this one included, and `--sensitive yes` lists them.
`summary` has `sensitive_directories`.

`--include-sensitive` turns both protections off for one run and writes a note to stderr. No
setting or environment variable turns it on.

### `sfo summary <job>`

One page as an entry point. Its size does not grow with the number of files: counts,
distributions over fixed value sets and the largest directories. `--top <n>` (0 to 25,
default 10) sets how many directories are listed. With 10,000 files the JSON is about 4 kB at
the default and 8 kB at `--top 25` (measured 12.11.2026 by
`ReadCommandTests.Summary_OfTenThousandFiles_StaysUnder20KB`, which fails above 20 kB).

```bash
sfo summary 7 --json
```

```json
{
  "schema_version": 1,
  "command": "summary",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job": { "id": 7, "status": "completed", "...": "same fields as scan" },
    "directories": 112,
    "sensitive_directories": 2,
    "files": { "total": 1830, "pending": 330, "errors": 3, "sensitive": 4, "total_bytes": 9126805504 },
    "by_category": { "unclassified": 1220, "photo": 410, "invoice": 120, "review_needed": 80 },
    "by_file_type": { "image": 700, "document": 640, "other": 490 },
    "by_status": { "discovered": 1100, "ai_analyzed": 610, "skipped": 117, "error": 3 },
    "by_sensitive_marker": { "...": "marker code: count" },
    "directories_by_verdict": { "...": "verdict code, error or none: count" },
    "directories_by_retention": { "...": "retention code or none: count" },
    "largest_directories": [
      { "relative_path": "photos", "size_bytes": 7340032000, "files": 900,
        "verdict": "...", "retention": "...", "sensitive_files": 0 }
    ]
  },
  "error": null
}
```

- `files.pending`: files of any type still waiting for classification (as in
  `sfo status`). `files.errors`: files whose classification failed.
- `by_category` counts the category `files` reports, so marked files count as `review_needed`;
  `unclassified` are files without a category.
- Distributions are sorted by count, largest first. `largest_directories` leaves out the job
  root; sizes and file counts cover the whole subtree.
- `bundles`: the job with and without dumps, from the last `sfo bundles build`; `null` when bundles
  were never built or a rescan dropped them. `{ "built_at", "dump_count", "bundle_count",
  "unbundled_files", "dumps": {...}, "without_dumps": {...} }`, each part with `directories`,
  `empty_directories`, `files`, `total_bytes` and `by_file_type`. The other numbers of the summary
  include dumps.
- `findings`: counts of [`sfo findings`](#sfo-findings-job): `{ "listed", "sure", "unsure", "by_kind",
  "by_certainty", "by_state", "missing_steps" }` (listed are open and accepted findings); `null` for a library.

### `sfo dirs <job>`

Directories with subtree size, file count, verdict and summary.

| Option | Meaning |
|---|---|
| `--under <path>` | Only this directory and its subtree. |
| `--depth <n>` | Levels below `--under` (or the root); `0` lists only that directory. |
| `--min-size <size>` | Minimum subtree size: bytes or with `K`, `M`, `G`, `T` (powers of 1024), for example `500M`. |
| `--sort size\|files\|path` | Largest or most files first, or by path (default). |

```bash
sfo dirs 7 --sort size --depth 1 --limit 2 --json
```

```json
{
  "job_id": 7, "total": 9, "limit": 2, "offset": 0, "next_offset": 2,
  "items": [
    { "path": "/media/usb-stick", "relative_path": ".", "depth": 0, "status": "scanned",
      "size_bytes": 9126805504, "files": 1830, "direct_files": 12, "subdirs": 8,
      "verdict": "...", "verdict_source": "...", "retention": "...", "sensitive_files": 4,
      "sensitive": [], "sensitive_rule": null, "sensitive_dirs": 1,
      "purpose": "...", "summary": "...", "text_withheld": false,
      "scan_reason": "Descend: scan root", "link_kind": null, "link_target": null, "error_cause": null }
  ]
}
```

`status` is the scan state (`scanned`, `skip`, `shallow`, `error`, `link`, `pending`; see the
[glossary](glossary.md#what-the-scan-did-with-a-folder-directorystatus)). `scan_reason` says why the scan
treated the folder as it did. A link that was not followed has status `link`, `link_kind` and
`link_target`, and no files; a link a `follow_link` rule entered keeps `link_kind` and `link_target` with its
normal status. A folder that could not be read has status `error` and `error_cause` (never `scanned` with 0
files). `verdict` is a
verdict code, `error` or `null`; `verdict_source` tells whether a rule or the model decided.
`size_bytes`, `files`, `sensitive_files` and `sensitive_dirs` cover the subtree, `direct_files`
the directory itself. `sensitive` and `sensitive_rule` are the markers of the folder itself
(a skipped folder such as `.ssh`). `summary` is the Phase 3 summary, `null` until that phase ran.

### `sfo files <job>`

Files with type, category, confidence, summary and status.

| Option | Meaning |
|---|---|
| `--dir <path>` | Only files directly in this directory. |
| `--recursive`, `-r` | With `--dir`: include subdirectories. |
| `--bundle <id>` | Only files of this bundle and its child bundles (see [`sfo bundles`](#sfo-bundles-build-job)); `bundle_not_found` (1) for an unknown id. |
| `--category <value>` | Category as reported (`photo`, `invoice`, `review_needed`, ...). `unknown` also matches unclassified files. Repeatable. |
| `--status <value>` | `discovered`, `ai_analyzed`, `skipped`, `error`, ..., or `pending` (`discovered` or `processing`). Repeatable. |
| `--type <value>` | `image`, `video`, `document`, `archive`, ... Repeatable. |
| `--max-confidence <x>` | Confidence at most `x` (0 to 1, decimal point). Files without a confidence are left out. |
| `--certainty <value>` | `certain`, `likely`, `uncertain` or `review`. Repeatable. `review` is the work list: two answers disagree or the model could not say what the file is. Files without a certainty (no result, or analysed before 12.11.2026) are left out. |
| `--sort path\|size` | By path (default) or largest first. |

`sfo files --help` lists every accepted value. Values are `snake_case`; `ReviewNeeded` is
accepted as well.

```bash
sfo files 7 --dir photos --max-confidence 0.5 --json
```

```json
{
  "job_id": 7, "total": 1, "limit": 200, "offset": 0, "next_offset": null,
  "items": [
    { "path": "/media/usb-stick/photos/scan_004.jpg", "name": "scan_004.jpg",
      "file_type": "image", "size_bytes": 482113, "last_write_time": "2019-07-02T10:14:00.000Z",
      "status": "ai_analyzed", "category": "document_scan", "category_source": "model",
      "confidence": 0.42, "summary": "Scanned page of a rental contract.",
      "sensitive": [], "protected": false, "dir_verdict": "...", "certainty": "likely" }
  ]
}
```

`category_source` is an origin code (see [glossary.md](glossary.md)): `model`, `human` (an override), `rule` (forced to review by a marker; until 12.11.2026 `ai`, `user` and `sensitive`) or
`null`. `dir_verdict` is the verdict of the directory holding the file. `certainty` says how far the
result can be relied on: `certain` (two independent signals agree), `likely` (one answer nothing
contradicts, or inherited), `uncertain` (a due second check did not happen), `review` (a person should
look); `null` without a result or for results written before 12.11.2026.

### `sfo show <job> <path>`

One directory or file with everything that is known about it. `kind` is `directory` or
`file`; the other member is `null`.

- File: every field of `files`, plus `relative_dir`, `extension`, `importance`,
  `suggested_target`, `model_used`, `analyzed_at`, `error`, `raw_response`, `sensitive_rule`,
  `dir_retention`, `attempts` (how often the file went through classification), `previous_error`
  (the error a retry replaced) and `checks`: every second check with `reasons`, `outcome`
  (`agreed`, `disagreed`, `corrected`, `rule_wins`, `failed`, `skipped`), `rule_note`, `first` and
  `second` (category, importance, confidence, keep hint, summary, target, keep reason, model, error,
  raw response; for a protected file only the codes, model texts are `null`) and `checked_at`.
- Directory: every field of `dirs`, plus `verdict_rule`, `reason`, `verdict_error`,
  `unit_directory` (for a unit: the directory that is the whole), `verdict_model`,
  `judged_at`, `verdict_raw_response`, `profile` (the facts the verdict was based on, as
  stored), `summary_error` and `analyses` (the stored pre-assessment and summary rows with
  theme, sampling and anomalies).

```bash
sfo show 7 photos/2019 --json
```

A path that is neither a directory nor a file of the job fails with `path_not_found` (1).

### `sfo export <job> --csv <file>`

Writes every file of the job as CSV (UTF-8 with BOM, the columns of `ScanExportService`) and
nothing else. An existing file is replaced. `--include-sensitive` as above.

```json
{ "job_id": 7, "path": "/home/me/usb-stick.csv", "rows": 1830, "include_sensitive": false }
```

Errors: `job_not_found` (1), `path_not_found` (1, the target folder does not exist),
`export_failed` (1, the file could not be written).

## Judging the structure

Both commands judge how a library is filed and change nothing in it. Finding kinds,
thresholds, the word file and the target file are described in
[library-review.md](library-review.md).

### `sfo library review <job>`

Runs the structure rules on a completed job and, unless `--no-model` is given, asks the
local model about roles and a few changes. It is meant for a job with the role `library`;
a `source` job is judged too, with a note on stderr and in `role_note` (otherwise `null`).

| Option | Meaning |
|---|---|
| `--no-model` | Rules only. |
| `--model <name>` | Ollama model for this run. Default: `Ollama.Model`. |
| `--refresh` | Ask the model again even if an answer to the same facts is stored. |
| `--kind <code>` | Only findings of this kind. Repeatable. |
| `--limit <n>`, `--offset <n>` | Paging over the findings, as in [Lists and paging](#lists-and-paging). Default limit 50. |
| `--words <file>` | Word file with extra words and exceptions. |

```bash
sfo scan /media/archive --role library --structure-only --json
sfo library review 7 --no-model --json
```

```json
{
  "job": { "id": 7, "status": "completed", "structure_only": true, "...": "..." },
  "structure": { "directories": 80, "files": 374, "bytes": 3669301, "system_files": 1,
                 "max_depth": 11, "median_depth": 2, "complete": false,
                 "dumps": 2, "dump_directories": 24, "dump_files": 5, "dump_bytes": 17009 },
  "by_kind": { "dump": 2, "naming_inconsistent": 2, "...": 1 },
  "excluded_by_exceptions": 0,
  "words": "built-in",
  "role_note": null,
  "job_id": 7, "total": 21, "limit": 50, "offset": 0, "next_offset": null,
  "items": [
    {
      "kind": "dump", "impact": "high",
      "path": "/media/archive/Handy", "relative_path": "Handy",
      "reason": "device dump (holds Android, DCIM, LOST.DIR): ...",
      "items": [], "more": 0, "directories": 21, "files": 3, "bytes": 15006,
      "details": { "dump_kind": "device", "empty_dirs": 4, "levels": 7, "share_of_dirs": 0.262,
                   "evidence": "holds Android, DCIM, LOST.DIR" }
    }
  ],
  "advice": { "status": "skipped", "model": null, "created_at": null, "good": [], "roles": [],
              "changes": [], "outline": [], "dropped": 0, "error": null }
}
```

`structure.complete` is `false` when folders were skipped or recorded shallow by the scan
rules; their contents are then unknown. `items[].items` names up to five members of a
bundled finding (relative paths), `more` counts the rest. `sensitive_location` findings
show path and marker only.

Errors: `job_not_found` (1), `scan_not_completed` (1), `path_not_found` (1, word file),
`words_invalid` (1), `ollama_unavailable` (3, with `result`: the rule findings are complete,
`advice.status` is `unavailable`).

### `sfo library propose <job>`

Suggests a target structure and, with `--out`, writes it as a target file.

| Option | Meaning |
|---|---|
| `--out <file>` | Write the target file here (replaced if it exists). Refused inside the job's folder. |
| `--root <folder>` | Folder the targets go under, written into the file. Default: empty. |
| `--lang <en\|de>` | Language of folder names when the rules build the structure. Default `en`. |
| `--no-model`, `--model`, `--refresh`, `--words` | As for `review`. |

The result holds `job`, `status` (as `advice.status`), `error`, `dropped`, `path` (the
written file or `null`) and `target` (the target structure). When the model is unreachable
(`status: "unavailable"`) or its answer cannot be used (`status: "failed"`, reason in
`error`), the rule-built structure is returned with exit code 0 and a note on stderr.

Errors: `job_not_found` (1), `scan_not_completed` (1), `output_inside_job` (1),
`path_not_found` (1, missing target folder or word file), `export_failed` (1),
`words_invalid` (1).

## Placing into the library

Both commands read the target map of a library job; neither moves or renames anything. Map,
signals, certainty, new folder names, the patterns file and the settings are described in
[library-match.md](library-match.md). Issue:
https://github.com/crack666/smart_file_organizer/issues/25

### `sfo library show <job>`

The target map of a `library` job, one page of folders in tree order.

| Option | Meaning |
|---|---|
| `--under <path>` | Only this folder and its subtree (relative to the library root, or a full path). |
| `--depth <n>` | Levels below `--under` (or the root); `0` lists only that folder. |
| `--search <text>` | Only folders whose name contains the text (case and umlauts aside). |
| `--from <date>`, `--to <date>` | Only folders whose period overlaps: a year (`2015`), month (`2015-07`) or day (`2015-07-12`). |
| `--role <code>` | Only folders with this role. Repeatable. |
| `--limit <n>`, `--offset <n>` | Paging as in [Lists and paging](#lists-and-paging). Default limit 100. |
| `--reset-patterns` | Write the patterns file anew from the folders, replacing hand edits. |
| `--no-model`, `--model`, `--refresh`, `--words` | As for `review`. The model writes the `purpose` of the upper folders. |

```json
{
  "job": { "id": 8, "role": "library", "...": "..." },
  "patterns": { "file": "/home/me/.local/share/SmartFileOrganizer/library-patterns/job-8.json",
                "created": true, "ignored": [], "note": null, "targets": [ { "path": "Fotos", "pattern": "{occasion} {year}", "...": "..." } ] },
  "job_id": 8, "total": 14, "limit": 100, "offset": 0, "next_offset": null,
  "items": [
    { "path": "/media/archive/Fotos", "relative_path": "Fotos", "name": "Fotos", "depth": 1, "parent": ".",
      "role": "collection", "children_role": "occasion",
      "purpose": "3 folders, mostly occasions named like '{occasion} {year}' (Grill 2016, Ostern 2018, Ski 2015): 15 image, taken 2015-02-07 to 2018-04-01.",
      "purpose_source": "rule", "files": 15, "direct_files": 0, "subdirs": 3, "bytes": 82, "by_file_type": { "image": 15 },
      "period": { "from": "2015-02-07T10:00:00", "to": "2018-04-01T12:30:00", "time_source": "file_name" },
      "name_period": null,
      "children_pattern": { "template": "{occasion} {year}", "name_pattern": "year_suffix", "count": 3, "of": 3,
                            "examples": [ "Grill 2016", "Ostern 2018", "Ski 2015" ] },
      "pattern": "{occasion} {year}", "findings": [], "weak": false, "dump_kind": null }
  ],
  "advice": { "status": "skipped", "model": null, "error": null, "dropped": 0 }
}
```

`patterns.ignored` lists hand-edited targets whose folder no longer exists. Errors:
`job_not_found` (1), `scan_not_completed` (1), `wrong_role` (1, not a library job),
`path_not_found` (1, `--under` or word file), `words_invalid` (1), `library_patterns_invalid` (1),
`settings_invalid` (1, a `LibraryMatch` value), `ollama_unavailable` (3, with `result`: the map is
complete with rule purposes).

### `sfo library match <job> [path]`

Where a part of a `source` job belongs in the library: a file or folder (`path`, relative to the
source root or full) or a bundle (`--bundle <id>`), exactly one of them.

| Option | Meaning |
|---|---|
| `--bundle <id>` | Match a bundle of `sfo bundles build` as a whole. |
| `--against <job>` | Library job. Default: the one of the latest `sfo compare`, else the newest completed library job. |
| `--limit <n>` | Proposals to list, 1 to 50. Default `LibraryMatch.MaxCandidates` (3). |
| `--no-model`, `--model`, `--refresh`, `--words` | As for `review`. The model only names a new folder the rules cannot complete. |

```json
{
  "source": { "id": 9, "role": "source", "...": "..." },
  "library": { "id": 8, "role": "library", "...": "..." },
  "library_from": "comparison",
  "status": "ok", "message": null,
  "unit": { "kind": "folder", "path": "/media/usb/Neuer Ordner", "bundle_id": null, "bundle_kind": null,
            "name": "Neuer Ordner", "keep_whole": false,
            "keep_reason": "'Neuer Ordner' says nothing about its content; its files need a place.",
            "role": "occasion", "files": 5, "by_file_type": { "image": 5 },
            "period": { "from": "2015-02-07T10:00:00", "to": "2015-02-08T10:30:00", "time_source": "file_name" },
            "words": [], "comparison": { "backed_up": 1, "new": 4 } },
  "comparison": { "comparison_id": 3, "compared_at": "2026-11-12T20:18:51.998Z", "complete": true },
  "considered": 9,
  "candidates": [
    { "rank": 1, "placement": "into_existing",
      "target": "/media/archive/Fotos/Ski 2015", "target_relative": "Fotos/Ski 2015",
      "target_role": "occasion", "target_purpose": "Photos and videos of one occasion: 6 image, taken 2015-02-07.",
      "destination": "/media/archive/Fotos/Ski 2015", "destination_relative": "Fotos/Ski 2015",
      "keeps_name": false, "score": 0.56, "certainty": "certain",
      "reason": "Into Fotos/Ski 2015: 1 of 5 files already lie here with the same content; taken 2015-02-07 to 2015-02-08, within the folder's 2015-02-07; 100% of its files are photos and videos.",
      "signals": [ { "signal": "content_neighbours", "value": 0.4, "weight": 0.4, "agrees": true, "contradicts": false,
                     "detail": "1 of 5 files already lie here with the same content" } ],
      "weak_place": null }
  ],
  "new_folder": null,
  "patterns": { "file": "...", "created": false, "...": "..." },
  "advice": { "status": "skipped", "model": null, "error": null, "dropped": 0 },
  "notes": []
}
```

- `placement`: `into_existing` (the files go into the folder), `as_subfolder` (a named source
  folder goes under the target as a whole, `keeps_name: true`) or, in `new_folder`, `new_folder`.
- `certainty`: `certain`, `likely`, `uncertain` or `review`, from agreeing signals;
  `signal`: `content_neighbours`, `period`, `name`, `file_type`, `siblings`.
- `weak_place`: `null`, or `{ "findings": [ { "kind", "impact", "relative_path" } ], "note" }`
  when the review found the order at the target weak; the certainty is capped then.
- `new_folder` when no candidate fits: `parent`, `parent_relative`, `name`, `name_source`
  (`rule` or `model`), `name_complete` (`false` while a placeholder such as `{occasion}` is left),
  `pattern`, `destination`, `destination_relative`, `certainty`, `reason`, `weak_place`.
- `status: "no_library"` with a `message` and no proposals, exit code 0, when there is no library
  to match against. `notes` says when there is no or an incomplete comparison, when the newest
  library job was taken, or when every file is already backed up.

Errors: `job_not_found` (1, source or `--against`), `scan_not_completed` (1), `wrong_role` (1, the
source is a library or `--against` is not), `path_not_found` (1), `bundle_not_found` (1),
`words_invalid` (1), `library_patterns_invalid` (1), `settings_invalid` (1), `usage` (2, both or
neither of `path` and `--bundle`). An unreachable model is not an error here: the new folder keeps
the name of the rules, `advice.status` says `unavailable`.

## Bundles

### `sfo bundles build <job>`

Groups a completed job into bundles the person decides once: dumps (phone, computer, USB stick or
software copies) with their worth-keeping parts and noise, named folders, and events of loose photos
and videos. Replaces earlier bundles of the job. Reads only the database: no file is opened, no model
called. How bundles are formed and why: [bundles.md](bundles.md). Issue:
https://github.com/crack666/smart_file_organizer/issues/39

| Option | Meaning |
|---|---|
| `--patterns <file>` | Pattern file for this run (default: `Bundles:PatternsFile`, else the shipped patterns). |
| `--gap-hours <h>` | Pause that starts a new event (default `Bundles:EventGapHours`, 8). |
| `--max-event-days <d>` | Longer events are cut at their largest pauses (default 14). |
| `--min-event-files <n>` | Smaller groups go to the year's `scattered` bundle (default 3). |

```json
{
  "job_id": 7,
  "built_at": "2026-11-12T19:25:52.350Z",
  "settings": { "event_gap_hours": 8, "max_event_days": 14, "min_event_files": 3,
                "patterns": { "name": "default", "source": "built-in", "hash": "…" } },
  "stats": {
    "all": { "directories": 15, "empty_directories": 1, "files": 71, "total_bytes": 229, "by_file_type": { "image": 70, "other": 1 } },
    "dumps": { "...": "same fields" },
    "without_dumps": { "...": "same fields" },
    "dump_count": 1, "bundle_count": 10, "unbundled_files": 0
  },
  "top_level_by_kind": { "event": 3, "dump": 1, "folder": 1 },
  "comparison": { "comparison_id": 3, "against_job_id": 8, "compared_at": "2026-11-12T15:02:11.410Z" }
}
```

`comparison` is the latest `sfo compare` of the job against a library, or `null`. Errors:
`job_not_found` (1), `scan_not_completed` (1), `patterns_invalid` (1, the pattern file or a threshold).

### `sfo bundles list <job>`

One page of bundles (paging as in [Lists and paging](#lists-and-paging)): the top level by default.

| Option | Meaning |
|---|---|
| `--parent <id>` | The children of this bundle: a dump's parts, a camera part's events. |
| `--all` | Every bundle, whatever its parent. |
| `--kind <code>` | `dump`, `dump_keep`, `dump_noise`, `folder`, `event`, `scattered`. Repeatable. |

```json
{
  "job_id": 7, "total": 5, "limit": 200, "offset": 0, "next_offset": null,
  "items": [
    { "id": 1, "parent_id": null, "kind": "dump", "origin": "phone", "part": null,
      "suggested_name": "Handy (phone 2013)", "styled_name": null, "root_path": "/media/disk/Handy",
      "period": { "from": "2013-06-10T10:00:00", "to": "2013-06-20T12:00:00", "time_source": "file_name" },
      "files": 10, "total_bytes": 18, "directories": 12, "empty_directories": 1, "children": 3,
      "sensitive_files": 0, "comparison": { "backed_up": 4, "new": 6 },
      "reason": "Recognised as phone dump by signature 'phone-storage' (score 5 of at least 3): dir:DCIM, dir:Android, dir:LOST.DIR.",
      "facts": { "by_file_type": { "image": 9, "other": 1 }, "time_sources": { "file_name": 9, "modified_date": 1 },
                 "markers": [ "dir:DCIM", "dir:Android", "dir:LOST.DIR" ], "directories": [], "directories_total": 0,
                 "device": null, "copy_time_suspected": false, "direct_files_only": false } }
  ]
}
```

- `origin` for `dump`, `part` for `dump_keep`, otherwise `null`. `root_path` is the directory that is
  the bundle (dump, folder, first folder of a part); `null` for events.
- `period.from`/`to` are local wall-clock times **without** a zone and without `Z`: capture dates and
  dates in names carry none. `time_source`: `capture_date`, `file_name`, `modified_date` or `mixed`.
- `files`, `total_bytes`, `directories`, `sensitive_files` and `comparison` cover the bundle with its
  children. `sensitive_files` and `comparison` (counts per state of the latest comparison with a
  library, `null` without one) are computed when read.
- `styled_name` is always `null`. A name in the style of the library is proposed per match by
  `sfo library match` (`new_folder.name`), not stored on the bundle.
- `facts.directories` lists at most 20 directories (relative to the root) of a part or an event;
  `directories_total` counts all.

### `sfo bundles show <job> <bundle>`

`{ "job_id", "bundle": {...}, "children": [ {...} ] }` with the fields of `list`. The files:
`sfo files <job> --bundle <bundle>`. Errors: `job_not_found` (1), `bundle_not_found` (1).

## Findings

The short list a job ends in (https://github.com/crack666/smart_file_organizer/issues/26). Rules, certainty,
ranking and thresholds: [architecture.md, Findings flow](architecture.md#findings-flow). No model call, no
scanned file opened; decisions change only their row.

### `sfo findings <job>`

| Option | Meaning |
|---|---|
| `--summary` | One page: counts per kind, certainty and state, coverage, missing steps and the first findings. Stays under 20 kB for a 10,000-file job. |
| `--top <n>` | With `--summary`: findings listed, 0 to 20, default 10. |
| `--kind <code>` | `not_backed_up`, `in_noise`, `misplaced`, `sensitive`, `duplicate`. Repeatable. |
| `--under <path>` | Only findings about files or folders at or below this folder (full, or relative to the job root). |
| `--min-certainty <code>` | `certain`, `likely`, `uncertain`, `review`: only findings at least this certain. |
| `--state <code>` | `open`, `accepted`, `rejected`, `wrong`. Repeatable. Default: open and accepted. |
| `--limit`, `--offset` | Paging as in every list. |

```json
{
  "job_id": 1, "total": 12, "limit": 200, "offset": 0, "next_offset": null,
  "items": [
    { "rank": 1, "key": "not_backed_up-880444df56", "kind": "not_backed_up", "certainty": "likely",
      "section": "sure", "state": "open",
      "title": "300 images (60 KB) worth keeping, not in the library: loose files in Neuer Ordner",
      "reason": "300 images of the 300 in Neuer Ordner are worth keeping by the model, and the comparison with library job 2 on 2026-11-12 found no file with the same content.",
      "path": "...", "relative_path": "Neuer Ordner", "bundle_id": null, "bundle_kind": null,
      "files": 300, "bytes": 61200, "group": "images", "by_file_type": { "image": 300 },
      "period": { "from": "2018-08-05T10:00:00", "to": "2018-08-05T14:59:00", "time_source": "file_name" },
      "comparison": { "new": 300 }, "markers": [], "sensitive_rules": [],
      "action": { "kind": "move", "reason": "...", "single_decision": false },
      "target": null, "duplicates": null,
      "examples": [ "Neuer Ordner/IMG_20180805_100000.jpg", "..." ], "notes": [ "..." ],
      "decided_by": null, "decided_at": null, "decision_note": null }
  ],
  "inputs": { "files", "files_judged", "files_pending", "directories", "directories_judged",
              "bundles_built_at", "library_job_id", "library_compared_at", "duplicates_compared_at" },
  "coverage": { "not_backed_up": { "coverage": "complete", "note": null }, "...": "" },
  "missing_steps": []
}
```

- `section` is `sure` (certainty `certain` or `likely`) or `unsure`; unsure findings come last.
- `key` is stable across `sfo bundles build` and a rescan of the same tree.
- `bytes` of a `duplicate` is what removing the redundant copies frees; `duplicates` gives `groups`,
  `redundant_files`, `redundant_bytes`, `directories` (at most 10), `directories_total`, `keep` and `scope`
  (`files`, or `folders` when a whole program or system folder exists twice; `directories` are then the copied
  folders). The action keeps the copy in `keep`: `delete_source` for the others with a library, `leave` without.
- `group` (for `not_backed_up` and `in_noise`): the kind of files, one finding per place and kind
  ([glossary](glossary.md#kind-of-files-of-a-finding-group)); `null` for the other kinds.
- `target` (for `misplaced`): `path`, `relative_path`, `certainty`, `reason`, `same_content_files`,
  `content_check` (`not_checked` until targets are checked by content).
- `sensitive` findings name path, marker and rule ids, never content.
- `--summary` gives `{ "job_id", "root_path", "inputs", "coverage", "missing_steps", "listed", "sure", "unsure",
  "by_kind": { "<kind>": { "findings", "sure", "unsure", "files", "bytes" } }, "by_certainty", "by_state",
  "below_threshold", "top": [ { "rank", "key", "kind", "certainty", "state", "title", "relative_path", "files",
  "bytes", "action" } ] }`.

Errors: `job_not_found` (1), `wrong_role` (1, a library), `settings_invalid` (1, a `Findings` value), `usage` (2).

### `sfo findings show <job> <key>`

`{ "job_id", "finding": {...}, "total", "limit", "offset", "next_offset", "files": [ ... ] }`: the finding and
its files, one page at a time, as `sfo files` writes them (marked files protected; `--include-sensitive` as
there). Errors: `job_not_found` (1), `finding_not_found` (1).

### `sfo findings accept|reject|wrong|reopen <job> <key>...`

`accept`: the plan turns the finding into an action. `reject`: not listed again. `wrong`: not listed again and
stored as a test case ([evaluation.md](evaluation.md#findings-reported-wrong)). `reopen`: the decision is
withdrawn. `--note <text>` is kept with the decision; `--by human|agent` says who decided (default `human`).
Result: `{ "job_id", "items": [ { "key", "state", "decided_by", "decided_at", "note", "eval_case_id",
"finding": {...} } ] }`. Changes no file. Errors: `job_not_found` (1), `finding_not_found` (1, the key is not
part of the job, or the data it rested on changed).

## Machine contract

Applies to every command, including the ones added later.

**stdout.** With `--json`, exactly one JSON document, also on failure and on a wrong
invocation. Without `--json`, human-readable text that may change at any time.

**Envelope.**

| Field | Meaning |
|---|---|
| `schema_version` | Version of the document shapes, currently `1`. Raised when a field is removed or changes meaning; new fields can appear without a raise. |
| `command` | The command path, for example `scan`; `sfo` when no command was recognised. |
| `ok` | `true` exactly when `exit_code` is 0. |
| `exit_code` | Same as the process exit code. |
| `result` | Command-specific payload; `null` on most failures, present where it helps (the paused job after Ctrl+C during a scan, the doctor report). |
| `error` | `null`, or `{ "code": "...", "message": "..." }`. `code` is stable; `message` is for people. |

Field names are `snake_case`, enum values are `snake_case` strings, timestamps are ISO 8601
UTC with a trailing `Z`.

**stderr.** Logs and progress, never results. With `--progress jsonl`, every stderr line is
one JSON object with `schema_version` and `type`:

```json
{"schema_version":1,"type":"progress","phase":"scan","processed":120,"total":480,"errors":0,"current_path":"/media/usb-stick/photos","message":null}
{"schema_version":1,"type":"log","level":"warning","category":"ClassificationService","message":"...","exception":null}
```

`phase` is `scan`, `pre_assessment`, `classification`, `summary`, `compare` or `eval_cases`. Progress is throttled to
one line per phase every 500 ms, plus the final line of a phase. `sfo analyze` also writes `"type":"wait"`
lines when it starts or stops waiting for the model server (see `sfo analyze`); they are never throttled.

**Exit codes.**

| Code | Meaning | `error.code` |
|---|---|---|
| 0 | Done | |
| 1 | The command ran but could not do its job | `job_not_found`, `path_not_found`, `scan_failed`, `scan_not_completed`, `wrong_role`, `compare_incomplete`, `database_error`, `model_missing`, `rules_invalid`, `words_invalid`, `output_inside_job`, `export_failed`, `truth_invalid`, `run_not_found`, `output_not_empty`, `bundle_not_found`, `patterns_invalid`, `library_patterns_invalid`, `settings_invalid`, `finding_not_found`, `internal_error` |
| 2 | Wrong invocation: unknown command or option, missing or invalid value | `usage` |
| 3 | Ollama not reachable | `ollama_unavailable` |
| 4 | The model server went away during `sfo analyze` and did not come back within `ModelServer.MaxWaitMinutes`; open items stay open, run the command again | `model_server_lost` |
| 130 | Interrupted with Ctrl+C; stored work stays and the command can be run again | `cancelled` |

A second Ctrl+C terminates the process immediately.

## Adding a command

The contract is enforced in one place, so a new command does not repeat it:

1. Add `src/SmartFileOrganizer.Cli/Commands/<Name>Command.cs` with a static
   `Create(CliHost host, GlobalOptions globals)` that builds a `System.CommandLine.Command`
   and binds the handler with `CommandRunner.SetHandler`.
2. In the handler, get services from `ctx.GetServicesAsync(ct)`, report progress through
   `ctx.Progress`, and return a `CommandOutcome`: `Success(result, text)` or
   `Fail(exitCode, ErrorCodes.X, message)`. Throwing `CliFailureException` does the same.
   Never write to stdout. A command that needs another service for one run (for example
   `sfo eval --stub` replacing Ollama) calls `ctx.ConfigureServices` before the first
   `GetServicesAsync`.
3. Define the result as records in the command class; `CliJson` turns them into `snake_case`.
   Reuse `JobView` for jobs. A command that lists or prints files reuses `ReadOptions` (paging,
   filters, `--include-sensitive`) and gets its files from `JobReadService`, which applies
   `SensitiveOutputGuard`.
4. Register the command in `SfoApp.BuildRoot`, describe it here, and add tests in
   `tests/SmartFileOrganizer.Cli.Tests`.

`CommandRunner` turns the outcome into the envelope or text, maps Ctrl+C to 130 and any
unexpected exception to 1 (`internal_error`). Parse errors never reach the handler; `SfoApp`
answers them with exit code 2.
