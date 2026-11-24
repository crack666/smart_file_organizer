# Exchange format

The judgement of a job (what the scan found, what the local model and the rules said about every folder and file)
in a fixed, documented form that another program or agent can read without `sfo`, and the way back for judgements
made elsewhere. Asked for on 12.11.2026 in https://github.com/crack666/smart_file_organizer/issues/34; in the code
since 22.11.2026.

Three ways in:

| Way | For | Protected files |
|---|---|---|
| `sfo export <job> --format jsonl --out <folder>` | Files that leave the machine or another program reads | Through the output guard, as `sfo files` |
| `sfo import-results <job> <files.jsonl>` | Judgements of another program or model, stored with origin `external` | The markers of the line are stored |
| The views `v_files` and `v_directories` | Reading the database directly on the machine | Not protected: whoever reads the database reads everything |

Codes and their meanings: [glossary.md](glossary.md). Commands: [cli.md](cli.md#sfo-export-job---csv-file).

## Files of an export

| File | Content |
|---|---|
| `job.json` | Head: format name and version, the job, the disclosure the export ran with, the number of lines. Written last: when it is there, the line files are complete. |
| `directories.jsonl` | One directory per line, with its verdict. |
| `files.jsonl` | One file per line, with its result, facts and comparison state. |
| `findings.jsonl` | Since 1.1, for a source: one finding per line, in every state, with the ids of its files and folder. |
| `plan.jsonl` | Since 1.1, for a source: one action of a plan of the job per line, with its state. |

`parts` in `job.json` lists the line files there are. A source always gets `findings.jsonl` and `plan.jsonl`, empty
when it has no finding or no plan; a library job gets neither (findings are about sources), and `findings` and
`plan_actions` in `job.json` are then `null`. The two were added on 23.11.2026
(https://github.com/crack666/smart_file_organizer/issues/116); a reader of 1.0 ignores them.

Every line is one JSON object, readable on its own, UTF-8 without BOM, ending with `\n` on every system. Keys are
`snake_case`, fixed values are codes, times are ISO 8601 UTC with `Z`, and every key is written, `null` when empty.
Lines are joined by id: `files.jsonl` `directory_id` and `directories.jsonl` `parent_id` name the `id` of a
directory line; `relative_path` (from the job root, `/` as separator, `.` for the root) says the same in words and is
what an import matches on.

The schemas (JSON Schema 2020-12) are in the repository and embedded in `sfo`:

| Schema | Describes |
|---|---|
| [`schemas/exchange/v1/job.schema.json`](../schemas/exchange/v1/job.schema.json) | `job.json` |
| [`schemas/exchange/v1/directory.schema.json`](../schemas/exchange/v1/directory.schema.json) | a line of `directories.jsonl` |
| [`schemas/exchange/v1/file.schema.json`](../schemas/exchange/v1/file.schema.json) | a line of `files.jsonl`, and the input of `sfo import-results` |
| [`schemas/exchange/v1/finding.schema.json`](../schemas/exchange/v1/finding.schema.json) | a line of `findings.jsonl` (1.1) |
| [`schemas/exchange/v1/plan-action.schema.json`](../schemas/exchange/v1/plan-action.schema.json) | a line of `plan.jsonl` (1.1) |

Fixed values are enumerations in the schemas; for the judgement codes (verdict, retention, category, origin,
certainty, markers, keep hint, fact source, comparison state, finding kind and state, action kind, target status)
each value carries its meaning. A test checks every
enumeration against the code (`PublishedSchemaTests`) and every exported line against its schema
(`ExchangeFormatTests`).

### A line of `files.jsonl`

| Key | Meaning |
|---|---|
| `id`, `directory_id` | Ids in the exporting database. |
| `relative_path`, `name`, `extension` | Where the file is. |
| `file_type`, `size_bytes`, `last_write_time`, `status` | What the scan recorded. |
| `analysis_skip` | `backed_up` when `sfo analyze --against` left the file out because a library holds it. |
| `scan_error_cause` | Why the scan could not read size or time. |
| `sensitive`, `sensitive_rule` | Markers from every source and the ids of the rules that set them. Never content. |
| `protected` | True when fields that can carry the text of the file were withheld. |
| `result` | The judgement, `null` when there is none: `category`, `category_source` (who set the category), `origin` (who made the result: `rule`, `model`, `inherited`, `human`, `agent`, `external`, `reused`), `importance`, `confidence`, `certainty`, `summary`, `keep_hint`, `keep_reason`, `model`, `analyzed_at`, `inherited_from_directory_id`, `attempts`, `error`, `raw_response`. |
| `facts` | Stored facts (`fact`, `value`, `source`, `certainty`): format, size in pixels, dates, device, video kind, description of a picture or document. |
| `content` | Comparison with the libraries of the newest `sfo compare`: `state` and, for `backed_up`, the `library_job_id` that holds the content. `null` when the job was never compared. |

There is no target per file: the old free-text target was dropped
(https://github.com/crack666/smart_file_organizer/issues/74). Targets belong to findings and plan actions.

### A line of `directories.jsonl`

`id`, `parent_id`, `relative_path`, `name`, `depth`, `status` (what the scan did with the folder), `scan_reason`,
`link_kind`, `link_target`, `error_cause`, `direct_files`, `files` and `size_bytes` (the whole subtree), `subdirs`,
`sensitive` and `sensitive_rule` (markers of the folder itself), `sensitive_files` (marked files in the subtree),
`text_withheld`, `summary` (Phase 3), and `verdict`, `null` before Phase 1: `verdict`, `source`, `rule`,
`retention`, `purpose`, `reason`, `unit_directory_id`, `escalated_from`, `escalation_reason`, `model`,
`judged_at`, `error` and `profile` (what the scan counted in the folder before it was judged).

### A line of `findings.jsonl`

What `sfo findings` computes from the stored data at export time, against the libraries of the newest `sfo compare`,
in rank order and in every state (`open`, `accepted`, `rejected`, `wrong`), not only the listed ones.

| Key | Meaning |
|---|---|
| `key` | Stable key: the same subject gives the same key in every run. Plan actions name it in `finding_key`. |
| `rank`, `kind`, `certainty`, `section`, `state` | Position (1 = most important), what it says, how sure, `sure`/`unsure`, the decision. |
| `title`, `reason`, `notes`, `examples` | One line, one checkable sentence, what else to know, a few file paths. |
| `relative_path`, `directory_id` | The folder it is about and its id in `directories.jsonl`; both `null` for an event without a folder. |
| `bundle_id`, `bundle_kind` | The bundle, as built at export time (ids change with every `sfo bundles build`). |
| `files`, `bytes`, `group`, `by_file_type`, `period`, `comparison` | Size, kind of files (`images`, ...), files per type, earliest and latest time (`from_local`, `to_local`: local wall-clock time without offset, as the bundles record it), files per comparison state. |
| `markers`, `sensitive_rules` | For `sensitive`: marker codes and rule ids. Never content. |
| `action` | Recommended `kind`, `reason`, `single_decision`. Nothing is executed by a finding. |
| `target`, `target_status`, `target_decided_by`, `target_parts` | The place in a library (`library_job_id`, `library_root`, `path`, `relative_path` from the library root, `certainty`, `content_check`, ...), what the target match says, who decided, and for `split` the parts with their own place and `file_ids`. |
| `duplicates` | For `duplicate`: groups, redundant files and bytes, folders, the copy to keep, `files` or `folders`. |
| `file_ids` | Ids of its files in `files.jsonl`, ascending. |
| `decided_by`, `decided_at`, `decision_note` | Who decided the state (`human`, `agent`), when, with what note. |

### A line of `plan.jsonl`

Every action of every plan of the job (`sfo plan create`), in plan and id order, as `sfo plan show` lists it. The
plans themselves stand in `plans` of `job.json` (`id`, `created_at`, `library_job_ids`, `notes`, `actions`).

`plan_id`, `id`, `finding_key` (the line of `findings.jsonl` it comes from), `finding_kind`, `unit`, `source_kind`
(`file` or `folder`), `relative_path`, `file_id` (for a file: its id in `files.jsonl`) or `directory_id` (for a
folder: its id in `directories.jsonl`), `files`, `bytes`, `kind`, `recommended`, `target_path` (full path at the
target), `library_job_id`, `reason`, `note`, `origin`, `state` (`proposed`, `approved`, ..., what `sfo plan apply`
did), `sensitive`, `single_decision`, `decided_by`, `decided_at`, `updated_at`.

### Protected files

The export passes the same guard as `sfo files` and `sfo dirs` (setting `Sensitive:Disclosure`, option
`--include-sensitive`, [cli.md](cli.md#sensitive-files)). A file with an access data marker (`credentials`, `keys`)
keeps its classification but never shows its summary (a neutral sentence instead), `keep_reason`, `error`,
`raw_response` or a free-text fact; with `restricted`, every marked file looks like that, and its category is
`review_needed`. A disposable category of a marked file is raised to `review_needed` with `category_source` `rule`.
Folder texts that name a withheld file are replaced and `text_withheld` is set.

## Example

Generated on 22.11.2026 (format 1.0) from a scanned test folder (`sfo scan`), one result through `sfo import-results` (the
line under "Import" below), a folder verdict written into the database by hand, then `sfo export --format jsonl`.
Ids and times are from that run; the root path is shortened.

`job.json`:

```json
{
  "format": "sfo-exchange",
  "format_version": "1.0",
  "exported_at": "2026-11-22T15:21:19.4870217Z",
  "disclosure": "open",
  "include_sensitive": false,
  "job_id": 1,
  "root_path": "/media/usb-stick",
  "role": "source",
  "status": "completed",
  "structure_only": false,
  "rules_name": "default",
  "created_at": "2026-11-22T15:20:51.8632615Z",
  "completed_at": "2026-11-22T15:20:51.9624983Z",
  "directories": 2,
  "files": 2,
  "parts": [
    "directories.jsonl",
    "files.jsonl"
  ]
}
```

`directories.jsonl` (two lines):

```json
{"id":1,"parent_id":null,"relative_path":".","name":"usb-stick","depth":0,"status":"scanned","scan_reason":"Descend: scan root","link_kind":null,"link_target":null,"error_cause":null,"direct_files":1,"files":2,"size_bytes":37,"subdirs":1,"sensitive":[],"sensitive_rule":null,"sensitive_files":0,"text_withheld":false,"verdict":null,"summary":null}
{"id":2,"parent_id":1,"relative_path":"docs","name":"docs","depth":1,"status":"scanned","scan_reason":"Descend: no rule matches","link_kind":null,"link_target":null,"error_cause":null,"direct_files":1,"files":1,"size_bytes":27,"subdirs":0,"sensitive":[],"sensitive_rule":null,"sensitive_files":0,"text_withheld":false,"verdict":{"verdict":"uniform","source":"model","rule":null,"retention":"keep","purpose":"Letters to and from the landlord.","reason":"All files are letters about the flat.","unit_directory_id":null,"escalated_from":null,"escalation_reason":null,"model":"gemma4:26b","judged_at":"2026-11-22T15:20:52.1Z","error":null,"profile":{"file_count":1,"total_bytes":27,"subdirectory_count":0,"subdirectories_not_listed":false,"extensions":[{"key":".txt","count":1,"bytes":27}],"file_types":[{"key":"Document","count":1,"bytes":27}],"oldest_write":"2026-11-22T15:20:50.4338654Z","newest_write":"2026-11-22T15:20:50.4338654Z","span_days":0,"name_patterns":[],"subdirectory_dominant_type":null,"unit_markers":[],"suggests":null,"suggestion_reason":null}},"summary":null}
```

`files.jsonl` (two lines):

```json
{"id":2,"directory_id":2,"relative_path":"docs/letter.txt","name":"letter.txt","extension":".txt","file_type":"document","size_bytes":27,"last_write_time":"2026-11-22T15:20:50.4338654Z","status":"ai_analyzed","analysis_skip":null,"scan_error_cause":null,"sensitive":[],"sensitive_rule":null,"protected":false,"result":{"category":"letter","category_source":"external","origin":"external","importance":"medium","confidence":0.8,"certainty":"likely","summary":"Letter to the landlord about the rent.","keep_hint":null,"keep_reason":null,"model":"other-tool 2.1","analyzed_at":"2026-11-22T15:20:53.2844363Z","inherited_from_directory_id":null,"attempts":1,"error":null,"raw_response":null},"facts":[],"content":null}
{"id":1,"directory_id":1,"relative_path":"notes.txt","name":"notes.txt","extension":".txt","file_type":"document","size_bytes":10,"last_write_time":"2026-11-22T15:20:50.4343994Z","status":"discovered","analysis_skip":null,"scan_error_cause":null,"sensitive":[],"sensitive_rule":null,"protected":false,"result":null,"facts":[],"content":null}
```

Since 1.1 a source also gets `findings.jsonl` and `plan.jsonl`, and `job.json` carries `findings`, `plan_actions` and
`plans`. Generated on 23.11.2026 from the test source of `PlanCommandTests` (analysed with scripted answers, compared
with a library, every finding accepted, a plan created and approved); the library path is shortened. The first line
of each:

```json
{"key":"not_backed_up-916f672f83","rank":1,"kind":"not_backed_up","certainty":"certain","section":"sure","state":"accepted","title":"4 images (36 B) worth keeping, not in the library: folder Urlaub Kroatien 2021","reason":"4 images of the 4 in Urlaub Kroatien 2021 are worth keeping by the model, and the comparison with library job 2 (mostly photos and videos) on 2026-11-23 found no file with the same content.","relative_path":"Urlaub Kroatien 2021","directory_id":5,"bundle_id":2,"bundle_kind":"folder","files":4,"bytes":36,"group":"images","by_file_type":{"image":4},"period":{"from_local":"2021-07-03T10:00:00","to_local":"2021-07-03T13:00:00","time_source":"file_name"},"comparison":{"new":4},"markers":[],"sensitive_rules":[],"action":{"kind":"move","reason":"Worth keeping and not in the library: move it there (copy, verify, then remove the original).","single_decision":false},"target":null,"target_status":"not_matched","target_decided_by":null,"target_parts":[],"duplicates":null,"examples":["Urlaub Kroatien 2021/IMG_20210703_100000.jpg","Urlaub Kroatien 2021/IMG_20210703_110000.jpg","Urlaub Kroatien 2021/IMG_20210703_120000.jpg"],"notes":["Where in the library: sfo library match 1 <folder> --against 2."],"file_ids":[10,11,12,13],"decided_by":"human","decided_at":"2026-11-23T00:35:34.2930807Z","decision_note":null}
```

```json
{"plan_id":1,"id":1,"finding_key":"not_backed_up-916f672f83","finding_kind":"not_backed_up","unit":"4 images (36 B) worth keeping, not in the library: folder Urlaub Kroatien 2021","source_kind":"folder","relative_path":"Urlaub Kroatien 2021","file_id":null,"directory_id":5,"files":4,"bytes":36,"kind":"move","recommended":"move","target_path":"/media/library/Fotos/Urlaub Kroatien 2021","library_job_id":2,"reason":"The folder is one whole (uniform): it moves as it is, its name stays. Worth keeping and not in the library: move it there (copy, verify, then remove the original).","note":null,"origin":"rule","state":"approved","sensitive":false,"single_decision":false,"decided_by":"human","decided_at":"2026-11-23T00:35:34.5830481Z","updated_at":"2026-11-23T00:35:34.5830481Z"}
```

Read without `sfo`, in Python with nothing but the standard library:

```python
import json, pathlib

folder = pathlib.Path("export")
head = json.loads((folder / "job.json").read_text(encoding="utf-8"))
assert head["format"] == "sfo-exchange" and head["format_version"].split(".")[0] == "1"

dirs = {}
for line in (folder / "directories.jsonl").read_text(encoding="utf-8").splitlines():
    d = json.loads(line)
    dirs[d["id"]] = d

for line in (folder / "files.jsonl").read_text(encoding="utf-8").splitlines():
    f = json.loads(line)
    verdict = (dirs[f["directory_id"]]["verdict"] or {}).get("verdict")
    result = f["result"] or {}
    print(f["relative_path"], result.get("category"), result.get("confidence"), verdict, f["sensitive"])
```

## Import: `sfo import-results <job> <files.jsonl>`

A program that judges files itself writes lines of `files.jsonl`; `sfo` then finds, plans and moves as if its own
model had judged. Only `relative_path` and `result` with `category` are needed:

```json
{"relative_path":"docs/letter.txt","result":{"category":"letter","importance":"medium","confidence":0.8,"certainty":"likely","summary":"Letter to the landlord about the rent.","model":"other-tool 2.1"}}
```

- Every line is checked against `file.schema.json` first. One invalid line (not JSON, an unknown code, a wrong type,
  a time without offset) and nothing is stored; the error `import_invalid` names line and location. Keys the schema
  does not know are allowed, so lines of a newer minor version can be read.
- A line is matched to a file of the job by `relative_path`: exactly, else ignoring case when only one file fits.
  Lines without `result` and paths that are not in the job are counted and listed, not fatal.
- A matched file gets the result with origin `external` (`is_ai_result` false) and status `ai_analyzed`; a result it
  had is replaced. A person's correction (`user_overrides`) stays and wins. Keys of the line outside `result`
  and `sensitive` (sizes, facts, ids) are not imported: they are what the scan of this job measured.
- `sensitive` of the line is stored as the markers of the result, so a file the other program calls sensitive is
  protected like one the local model marked. The rules of `sfo` keep marking on their own.

Exporting a job, importing its `files.jsonl` into a new scan of the same folder and exporting again gives the same
category, importance, confidence, certainty, summary, keep hint, model and markers per file
(`ExchangeFormatTests.ExportImportExport_GivesTheSameJudgements`); `origin` then says `external`.

## The answer the model must give

The schemas the local model is asked to fill are published under [`schemas/model/`](../schemas/model/), one per
phase and variant. They are the same objects `sfo` sends as the answer format (Ollama `format`, OpenAI
`response_format`), and the parser reads the same enumerations, so another model or service can be made to give the
same judgement:

| Schema | Asked for |
|---|---|
| `phase1-directory-verdict.schema.json` | the verdict on a folder (Phase 1) |
| `phase2-classification.schema.json` | one file (Phase 2) |
| `phase2-classification-mixed-folder.schema.json` | one file of a mixed folder, with keep hint |
| `phase2-classification-picture.schema.json` | a picture or a video the model sees, with its description and, for a video whose kind the files leave open, the kind |
| `phase2-classification-document.schema.json` | a document, with its description |
| `phase3-directory-summary.schema.json` | the summary of a folder (Phase 3) |

The model answers categories and importance with the enum names (`PersonalDocument`), which the export writes in
`snake_case` (`personal_document`). The files are written from the code by `PublishedSchemaTests`: the test fails
when a schema in the code and its file differ; after a deliberate change, write them again with
`SFO_WRITE_SCHEMAS=1 dotnet test --filter PublishedSchemaTests` and commit them with the change. Category and
importance lists come from the enums the parser reads into, so a new value reaches prompt and parser at once.

## Reading the database

For a program on the same machine, two views keep their columns over every schema step:

| View | Columns |
|---|---|
| `v_files` | `job_id`, `file_id`, `directory_id`, `relative_path`, `full_path`, `name`, `extension`, `file_type`, `size_bytes`, `last_write_time`, `status`, `analysis_skip`, `rule_markers`, `sensitive_rule`, `model_markers`, `category` (a correction wins), `category_source`, `result_origin`, `importance`, `confidence`, `certainty`, `keep_hint`, `summary`, `model`, `analyzed_at`, `error` |
| `v_directories` | `job_id`, `directory_id`, `relative_path`, `full_path`, `name`, `parent_path`, `depth`, `status`, `direct_files`, `direct_bytes`, `subdirs`, `sensitive_markers`, `sensitive_rule`, `analysis_skip`, `verdict`, `verdict_source`, `verdict_rule`, `retention`, `purpose`, `reason`, `escalated_from`, `model`, `judged_at`, `verdict_error` |

```bash
sqlite3 data.db "SELECT relative_path, category, confidence FROM v_files WHERE job_id = 7 AND category = 'invoice'"
```

Values are the same codes as in the export; markers are comma-separated lists. The tables behind them stay internal
and change with schema steps. The views are not schema steps: `sfo` drops them before it applies a step and writes
them again from its enums when it opens the database, so they always fit the build that opened it last. They show
what the database holds, without the output guard: whatever leaves the machine goes through `sfo export`.

## Rules for changes

- **Minor version** (`1.0` → `1.1`): new keys, a new line file, a new code in an enumeration. 1.1 (23.11.2026) added
  `findings.jsonl`, `plan.jsonl` and `findings`, `plan_actions`, `plans` in `job.json`. 1.2 (24.11.2026) added the
  code `human` to `verdict_source` of `directories.jsonl` and `v_directories`: a verdict a person set
  ([#14](https://github.com/crack666/smart_file_organizer/issues/14)). A reader of version 1
  ignores keys it does not know and treats an unknown code like `unknown`.
- **Major version** (`1.x` → `2.0`, new folder `schemas/exchange/v2/`): a key that changes meaning or type, a key that
  is removed, a code that is removed or renamed. `sfo import-results` reads only the major version it was built with.
- The view columns are only appended to; their order and meaning stay.
- The answer schemas under `schemas/model/` follow the prompt and carry no version of their own: the prompt
  fingerprint of an evaluation run (`sfo eval`) says which ones a result was made with.
