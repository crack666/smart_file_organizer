# Library review: judging a folder tree by its structure

`sfo library review` and `sfo library propose` judge how a library is filed, not what the
files contain. They work on a completed scan, ideally one made with
`sfo scan <path> --role library --structure-only`, which records names, sizes and dates and
opens no file. `review` is meant for a job with the role `library`; a `source` job is judged
too, with a note (`role_note`). `propose` is meant for a source without an ordered library.
Both commands only read: nothing in the judged folder is moved, renamed or written, and the
suggestions they return are data. Where a part of a source belongs in the library is answered by
`sfo library show` and `sfo library match`, which build on these findings:
[library-match.md](library-match.md).

Background and the manual review the thresholds come from:
https://github.com/crack666/smart_file_organizer/issues/29

## Order of work

1. **Dumps first.** Copies of phones, computers and software packages are recognised and
   taken out before any other rule runs. They are reported once each, as a whole, and never
   produce naming, empty-folder or depth findings of their own.
2. **Rules.** Each rule looks at the remaining tree. Findings are bundled per parent folder:
   "6 empty folders under X", not six findings.
3. **Model (optional).** The local Ollama model gets an outline of the tree and the top
   findings and returns roles, what is good, and a few changes. `--no-model` skips it; the
   rule part is complete without it.

Subtree sums (files, bytes, folders, depth) are computed in memory from the stored direct
sums, so the rules see recursive totals.

## Dump detection

Dumps are recognised by the signatures shared with the bundles: `bundle-patterns.json`, described in
[bundles.md](bundles.md#how-a-dump-is-recognised). The origin of the signature gives the kind:

| `dump_kind` | Signature origin |
|---|---|
| `device` | `phone` (`LOST.DIR`, `Android`, `DCIM`, `WhatsApp`, app package names, ...) or `removable_drive` (`System Volume Information`, `autorun.inf`, ...); found at `Android/data`, the dump is the folder above `Android` |
| `computer` | `computer` (`Windows`, `Program Files`, `AppData`, `NTUSER.DAT`, ...) |
| `package` | `software` (`.git`, `node_modules`, `.venv`, `site-packages`, `.idea`, ...), or the unit markers of the directory profile when the folder has files of its own |

The shipped word list has no dump markers of its own any more; `device_markers`, `computer_markers`,
`package_markers` and `app_package_pattern` in a `--words` file add markers on top of the signatures.

Only the outermost dump counts; everything below it belongs to it. A dump finding carries
`dump_kind`, `empty_dirs`, `levels`, `share_of_dirs` and `evidence` in `details`.
`Pictures` or `Movies` alone are deliberately not device markers.

## Finding kinds

Codes are stable; `details` keys are `snake_case`. Impact is `high`, `medium` or `low`;
the list is ordered by impact, then by what a finding covers (files plus 10 per folder),
then by bytes.

| Code | What it means | Impact |
|---|---|---|
| `dump` | A device, computer or package copy, see above | high |
| `sensitive_location` | Files with access data, keys or identity papers in this folder; only path and marker are shown | high |
| `dominant_file` | One file holds more than 50 % of all bytes | high |
| `likely_copy` | Two or more subtrees with the same file count and byte sum (at least 3 files and 1 MiB); the innermost match is kept | high / medium |
| `type_mismatch` | A name promises a type (`Bilder`, `Videos`, `Music`, ...) and at least 10 files hold mostly another one; or a subtree of at least 20 files differs from its top-level area (area: at least 50 files, 60 % one type) | high / medium |
| `placeholder_name` | `Neuer Ordner`, `asdf`, `Sonstiges` and the like; a grown one (100 files or 1 GiB) is `high` and reported alone | high / medium / low |
| `naming_inconsistent` | At least 5 siblings with at least 2 name patterns among the dated ones (`2017 London`, `Paris 2016`, `2018-07-01 Prag`); details carry the pattern counts and the majority | medium |
| `variant_spellings` | One level spelled two ways (`Staffel 1` and `S01`, `CD1` and `Disc 1`), across cousins or within one folder | medium |
| `flat_among_structured` | A folder with at least 50 files and no subfolders, while at least half of its siblings have subfolders | medium |
| `loose_files` | Files at depth 0 or 1 next to at least 3 subfolders | medium / low |
| `same_name_elsewhere` | The same name (at least 4 letters) in 2 to 8 places under different parents | medium |
| `similar_names` | Names with the same words in another order or spelling (`Urlaub Italien`, `Italien Urlaub 2`), or siblings one letter apart | medium |
| `very_full` | At least 500 files directly in one folder | medium |
| `very_deep` | Branches deeper than the median depth plus 4, and at least 8 levels | medium |
| `empty` | Branches with no content file (system files such as `Thumbs.db` do not count) | medium / low |
| `camera_folder_name` | Names the camera gave (`100CANON`) | low |
| `saved_web_page` | `Page_files` or `Seite-Dateien` next to the saved page | low |
| `case_inconsistent` | At least 5 siblings, 70 % in one letter case, the rest not | low |

`empty`, `placeholder_name`, `camera_folder_name`, `saved_web_page` and
`case_inconsistent` findings below one folder fold into one finding there once there are 3
of them (`details.places`). The thresholds live in `LibraryReviewOptions`.

Measured on 12.11.2026 with a generated tree of 10,021 folders and 6,500 files: 60
findings; the default page of 50 is about 46 KB of JSON and is produced in under a second.

## Word file (`--words <file>`)

The shipped word lists (German and English) are an embedded resource,
`src/SmartFileOrganizer.Application/Library/library-words.json`. A file given with
`--words` has the same shape; its lists are added to the shipped ones. Comments (`//`) are
allowed, unknown keys are an error (`words_invalid`), and all problems are reported at once.
Names are compared in lower case with umlauts transliterated.

```json
{
  "schema_version": 1,
  "placeholder_names": ["neuer ordner (3)"],
  "catch_all_names": ["krempel"],
  "type_words": { "image": ["schnappschuesse"] },
  "variant_groups": [
    { "role": "volume", "variants": [ { "id": "band_n", "pattern": "^band[ ._-]*\\d+$" } ] }
  ],
  "exceptions": [
    { "path": "Archiv/Sonstiges", "kinds": ["placeholder_name"], "why": "kept on purpose" },
    { "path": "Musik/Alben", "kinds": [], "why": "no findings below this folder" }
  ]
}
```

Other keys: `system_files`, `system_file_prefixes`, `common_child_names`, `stop_words`,
`sensitive_name_words`, `device_markers`, `computer_markers`, `package_markers`,
`app_package_pattern`, `app_package_min_children`.

An exception covers its folder and everything below it, for the listed kinds (all kinds when
`kinds` is empty). `path` is relative to the job's root with `/`; `.` is the root.
`excluded_by_exceptions` in the result counts what was left out.

## What the model is asked

`review` sends facts, not files: totals, the dumps, an outline down to depth 3 (at most
250 lines, at most 20 children per folder) with subfolder count, file count, type shares,
years and the name patterns of the children, and the top 40 findings. File names are never
sent; sensitive files only as a count. Folder names are sent, and only to the local model.

The principles in the prompt: keep the habits the person has (occasion names they find
things by), name what is already good, decide the role of folders before comparing them
(occasions, persons, devices and topics may lie side by side), a majority is a hint and
not a rule, sort by areas of life, keep dumps whole, and suggest few changes.

The answer follows a JSON schema whose codes come from the domain enums:

| Field | Content |
|---|---|
| `good` | `path`, `reason`: what to keep |
| `roles` | `path`, `role`, `children_role`, `reason`; roles: `occasion`, `person`, `device`, `topic`, `time`, `media_title`, `season`, `document_type`, `project`, `collection`, `catch_all`, `dump`, `mixed` |
| `changes` | `action` (`rename`, `move`, `merge`, `split`, `create`), `path`, `target` (required except for `split`), `reason`, `finding` (a finding code) |
| `outline` | `path`, `purpose`, `pattern`: the target structure in at most three levels |

Entries with a path that is not in the tree or a code that does not exist are dropped and
counted in `advice.dropped`. Answers are stored per job, task, model and facts
(`library_advice` table); the same facts reuse the stored answer (`status: "cached"`) until
`--refresh`. `advice.status` is `ok`, `cached`, `skipped` (`--no-model`), `unavailable` or
`failed`.

## Target structure file (`library propose --out <file>`)

```json
{
  "schema_version": 1,
  "kind": "sfo_target_structure",
  "root": null,
  "source_job": 7,
  "created_at": "2026-11-12T14:39:05Z",
  "origin": "rule",
  "model": null,
  "targets": [
    {
      "id": "photos",
      "path": "Photos",
      "pattern": "{occasion} {year}",
      "holds": ["image", "video"],
      "reason": "172 photos and 72 videos; one folder per occasion with its year.",
      "examples": ["Hochzeit Anna 2017", "Ostern 2018"]
    }
  ]
}
```

| Field | Meaning |
|---|---|
| `root` | Absolute folder the targets go under; `null` until the person fills it in (`--root`) |
| `origin` | `rule` (built from the tree's types) or `model` |
| `targets[].id` | `[a-z0-9_-]+`, unique |
| `targets[].path` | Relative, `/`-separated, no `.` or `..` |
| `targets[].pattern` | Naming pattern for the folders inside, or `null` |
| `targets[].holds` | File types (`image`, `video`, `audio`, `document`, `archive`, `executable`, `code`, `database`, ...) |
| `targets[].examples` | Existing folder names that already fit |

Without the model, the rules build one target per type group that occurs (photos, videos,
music, documents by area and year, archives, software, device backups), named in English or,
with `--lang de`, in German. `TargetStructureFile.Load` validates a file and lists every
problem. The file is meant as input for later sorting steps; this command applies nothing.
`sfo decide <job> --targets <file>` reads it ([cli.md](cli.md#sfo-decide-job)): a kept file goes to the first target
whose `holds` names its type, and the pattern is filled from the stored facts of the file. Placeholders are fact codes
of [glossary.md](glossary.md#fact-key-filefactkey) (`{occasion}`, `{year}`, `{issuer}`, `{scene}`, ...) and the names
the rules write: `{area}` is the document kind, `{year}` the year of the document, else the year it was taken or
created; the German names (`{Anlass}`, `{Jahr}`, `{Bereich}`, `{Aussteller}`, `{Titel}`, `{Interpret}`, `{Gerät}`)
mean the same. `{name}` has no fact and stays open.
