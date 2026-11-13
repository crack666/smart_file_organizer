# Bundles

A person who finds an old drive has no time to decide ten thousand files one by one. Bundles turn a
scanned job into a few dozen units, each decided once: a phone copy, a named folder, the photos of one
afternoon. Bundles are built from scan data and stored metadata facts only; no file is opened and no
model is called.

Issue: https://github.com/crack666/smart_file_organizer/issues/39

Why dumps come first: a structure check of a real private share (12.11.2026, recorded in
https://github.com/crack666/smart_file_organizer/issues/29) found that a handful of phone and computer
copies made up 38 to 68 percent of the folders of a share, held more than half of the photos, and
dominated every count. They are recognised first and left out of every other statistic.

```bash
sfo bundles build 7                 # build (replaces earlier bundles of the job)
sfo bundles list 7                  # top level: dumps, folders, events, scattered
sfo bundles show 7 12               # one bundle and its children (a dump's parts)
sfo files 7 --bundle 12             # down to the files, through the output guard
sfo summary 7                       # counts with and without dumps
```

Command reference and JSON shapes: [cli.md](cli.md#sfo-bundles-build-job).

## Kinds

Codes are fixed English strings (`BundleKind`, `DumpOrigin`, `DumpPart`, `TimeSource` in
`src/SmartFileOrganizer.Domain/Enums/BundleCodes.cs`), the same in the database and in JSON.

| `kind` | What it is |
|---|---|
| `dump` | A copy of a whole device: `origin` is `phone`, `computer`, `removable_drive` or `software`. |
| `dump_keep` | The worth-keeping part of a phone or computer dump; `part` says which (below). |
| `dump_noise` | Everything else of that dump: app data, caches, system and empty folders. |
| `folder` | An existing folder with a meaningful name. Taken as it is: never split, never renamed. |
| `event` | Loose photos and videos taken close together in time. |
| `scattered` | Loose photos and videos of one year that formed no group of `MinEventFiles`. |

`part`: `camera`, `screenshots`, `chat_media`, `received`, `recordings`, `documents`, `pictures`,
`videos`, `music`, `downloads`, `desktop`.

Bundles form a tree: a dump has its parts as children, the camera part has its events as children
(the camera roll is decided as one, but can be looked at by occasion). Every file belongs directly to
at most one bundle; counts and sizes cover a bundle with its children. Files outside every bundle are
counted as `unbundled_files`: files that are no photo or video and lie loose in folders without a
meaningful name.

## How a dump is recognised

From the root down, each directory is checked against the signatures of the pattern file, using
only the names of its own subdirectories and files. A directory is a dump when the weights of the
matching markers reach `min_score` and at least one `key` marker matches. The outermost match wins;
nothing below a dump is checked again. A dump nested in folders that hold nothing else
(`Phone/Internal storage/DCIM`) starts at the outermost of them.

| Signature | Origin | Key markers (examples) | Split |
|---|---|---|---|
| `phone-storage` | `phone` | `DCIM`, `Android`, `LOST.DIR` (alone enough), `WhatsApp`, `MIUI`, `Alarms`, `Notifications`, `Ringtones`, app package names (`com.example.app`, up to 3 counted) | yes |
| `user-profile` | `computer` | `AppData`, `Application Data`, `Local Settings`, `NTUSER.DAT`, `.bashrc`, `.config` | yes |
| `system-drive` | `computer` | `Windows`, `Program Files`, `Documents and Settings` | yes |
| `removable-drive` | `removable_drive` | `System Volume Information`, `autorun.inf`, `.Trashes`, `.Spotlight-V100` | no |
| `software` | `software` | `.git`, `.hg`, `node_modules`, `.venv`, `.idea`, `package.json`, `.sln`, `.exe` (with `.dll`) | no |

This recognition (`BundlePatterns.Recognise`) is the only one in the program: the structure review of a
library (`sfo library review`, [library-review.md](library-review.md)) and the directory verdict of the
analysis (rule `dump`, [architecture.md](architecture.md#phase-1--directory-verdict-and-pre-assessment-directorypreassessmentservice))
ask it too, so a phone copy is the same dump in all three places. The verdict gives a dump the code
`dump`, judges its parts and skips only its noise paths, at the scan root as below it (until Issue 59 on
13.11.2026 it made a dump a unit and never the scan root).

Guards against false dumps:

- A library with `Documents`, `Pictures`, `Music` is no user profile: profile folders alone never
  reach a key marker.
- A `software` match is dropped when photos and videos make up more than `max_media_share` (50 %) of
  the subtree: holiday photos next to a `setup.exe` stay photos.
- The scan root itself is only checked against signatures with `at_root: true` (phone, computer):
  a scanned USB stick is the medium, not a copy inside it.

### Worth keeping and noise

For a split dump, every directory belongs to a part or to the noise, by its path below the dump root:

1. Below a `noise_paths` entry (`Android/data`, `AppData`, `.thumbnails`, `cache`, `Windows`, ...) it is
   noise, whatever deeper names say: `Android/data/<app>/files/Pictures` is app data.
2. Otherwise the `keep_parts` path that ends deepest decides: `DCIM` is `camera`, but
   `DCIM/Screenshots` is `screenshots`; `WhatsApp/Media` is `chat_media`, `WhatsApp/Databases` noise.
3. Otherwise noise.

Unsplit dumps (`removable_drive`, `software`) stay one bundle with all their files.

## How loose files are grouped

Outside dumps, a folder whose name is not generic is a `folder` bundle with its whole subtree. Generic
are the names in `generic_folder_names` (`Neuer Ordner`, `Bilder`, `DCIM`, `Import`, ...), names
matching `generic_folder_regexes` (camera folders `100CANON`, `2015-07-12`, `Tag 1`), copies of those
(`Neu (2)`, `Bilder - Kopie`) and names with fewer than three letters. A named folder that holds two
or more named folders with files (`Family` with `Christmas 2015` and `Easter 2016`) is not one bundle:
each named subfolder is, and its own files form a `folder` bundle with `direct_files_only`.

Photos and videos in the root and in generically named folders are grouped into events:

- **Time of a file**: the capture date from the stored metadata facts (`taken_at`, see
  https://github.com/crack666/smart_file_organizer/issues/22), else a date in the file name
  (`IMG_20150712_143000`, `PXL_...`, `IMG-20150712-WA0001`, `Screenshot_2015-07-12-...`,
  `WhatsApp Image 2015-07-12 at 14.30.00`; a date without time counts as noon), else the modification
  time. Facts are written by the analysis (`sfo analyze`), not by the scan: for a job that was only
  scanned, names and modification times are all there is. `period.time_source` of each bundle says
  what its times rest on (`mixed` if more than one source).
- **Never together**: files of different devices (`device` fact), screenshots or scans with camera
  pictures (`image_origin` fact, or a screenshot name), and files whose only time is the modification
  time across different folders: a copy gives every file the time of the copy.
- **Split** where the pause between two consecutive files is longer than `EventGapHours`; a group
  spanning more than `MaxEventDays` is cut at its largest pauses until every part fits.
- **Small groups** (fewer than `MinEventFiles`) go to the `scattered` bundle of their year.
- **Copy suspicion**: an event of at least 20 files whose modification times all lie within one second
  per file is marked `copy_time_suspected`; its times are probably those of a copy.

Times in bundles are local wall-clock times without a zone (`2015-07-12T14:30:00`), because capture
dates and dates in names carry none; modification times are converted to local time for comparison.

## Settings

Configuration section `Bundles` (`settings.json` or `appsettings.json`); `sfo bundles build` overrides
each for one run.

| Setting | Default | Why |
|---|---|---|
| `EventGapHours` | 8 | A night separates two days, so occasions on consecutive days stay apart. A multi-day holiday falls into several events; that costs a few extra decisions, while a larger gap would merge a Saturday party with the Sunday hike. |
| `MaxEventDays` | 14 | A two-week holiday with photos every few hours stays one event; a camera roll used every day does not become one endless event. |
| `MinEventFiles` | 3 | One or two stray photos are no occasion; they are collected per year instead of each becoming a decision. |
| `PatternsFile` | none | A copy of `bundle-patterns.json` that replaces the shipped one. |

These are starting values, not measured ones.

## Pattern file

The shipped file is
[`src/SmartFileOrganizer.Application/Bundles/bundle-patterns.json`](../src/SmartFileOrganizer.Application/Bundles/bundle-patterns.json)
(embedded). Copy it, change it, and set `Bundles:PatternsFile` or pass `--patterns`. Comments and
trailing commas are allowed; unknown fields and every other problem are reported at once
(`patterns_invalid`, exit code 1). The pattern set used (name, source, hash) is stored with the build.

| Field | Meaning |
|---|---|
| `signatures[].origin` | A `DumpOrigin` code. |
| `signatures[].split` | `true`: worth-keeping parts and noise; `false`: one bundle. |
| `signatures[].at_root` | Whether the scan root may be this kind of dump. |
| `signatures[].min_score` | Sum of marker weights needed. |
| `signatures[].max_media_share` | Optional: above this share of photos and videos in the subtree the signature does not apply. |
| `signatures[].markers[]` | Exactly one of `dir`, `dir_regex`, `file`, `file_regex` (names are compared case-insensitively), `weight` (default 1), `max_count` (how many matches of a regex count, default 1), `key`, `label` (shown for regex matches). |
| `keep_parts[]` | `part` (a `DumpPart` code) and `paths` (runs of folder names, either separator). |
| `noise_paths` | Paths below which a dump is noise. |
| `generic_folder_names`, `generic_folder_regexes` | Folder names that say nothing about an occasion. |

## Storage

Schema step 11 (`M0011_Bundles`): `bundles` (one row per bundle, `parent_bundle_id`, codes,
`suggested_name`, `styled_name`, period, counts, `reason`, `facts_json`), `bundle_files` (the bundle each
file belongs to directly) and `bundle_builds` (per job: when, with which settings, and the statistics
with and without dumps). A new build replaces the bundles of the job; a rescan of a directory or deleting
the job drops them, because file ids change.

Counts of marked files (`sensitive_files`, rule and model markers, as the output guard sees them) and of
comparison states (`comparison`, from the latest `sfo compare` of the job against a library) are computed
when read, so they follow later analysis and comparisons without a rebuild.

## Not decided here

- `styled_name` stays empty. Since https://github.com/crack666/smart_file_organizer/issues/25 a name in
  the style of the library is proposed per match by `sfo library match --bundle <id>` (`new_folder.name`,
  see [library-match.md](library-match.md)); it depends on the chosen library and is not stored on the
  bundle. (Until 12.11.2026 this line said such a name needs a model.) `suggested_name` is the folder
  name, the date or date range, or the dump folder with origin and years.
- Whether a named folder is an occasion, a person or a topic: it is a bundle either way, never split.
  Only a folder holding two or more named folders with files is opened up.
- Content of unsplit dumps: a copied USB stick or a project stays one bundle, even if it holds photos;
  their file types are in `facts.by_file_type`.
- Worth-keeping files inside the noise (a photo in an app folder) are counted in the noise's
  `by_file_type`, not lifted out; `sfo findings` lists them as `in_noise`, one finding per noise part
  (https://github.com/crack666/smart_file_organizer/issues/26, [architecture.md, Findings flow](architecture.md#findings-flow)).
- Two occasions with less than `EventGapHours` between them, and shot with the same device, end in one
  event. Nothing without a model can tell them apart.
