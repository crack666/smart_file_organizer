# Move notice

Issue: https://github.com/crack666/smart_file_organizer/issues/86

When `sfo plan apply <plan> --remove-sources --execute` takes originals out of a folder, it leaves a small text file in
that folder that says where they went, or that they were removed (`discard`). The folder itself stays, even when nothing else is left in it. The notice is
meant to be read by a person who finds the folder on an old disk years later, without sfo and without its database.

## Name and place

- Fixed name: `_moved-files.txt`, in the folder the files lay in (for a folder action: in every folder below it that
  lost a file).
- One notice per folder, shared by all plans. A plan only ever changes its own lines; lines of other plans stay.
- `--no-notice` writes none in that run. `sfo plan undo` removes the lines of the files that came back, and the file
  when no line is left. A later `--remove-sources` run of the same plan writes the notices a run left out.
- A file with this name that is not a notice (its first line is not the marker below) is never changed, replaced or
  removed; the result names it with outcome `skipped`.

## Format, version 1

Plain text, UTF-8 without byte order mark, lines end in a line feed.

```text
# sfo-move-notice 1
# Files from this folder were moved or removed by Smart File Organizer (sfo). The folder may stay empty.
# One line per file, separated by tabs: moved, date and time (UTC), plan, action, name here, new place.
# 'removed' instead of 'moved': no copy was made; the file lies in the hold folder of this drive until it is emptied.
# 'sfo where <old path>' shows the history; 'sfo plan undo <plan>' brings the files back.
moved	2026-11-14T10:00:00Z	5	12	IMG_0001.jpg	/media/archive/Fotos/2019/IMG_0001.jpg
moved	2026-11-14T10:00:01Z	5	12	IMG_0002.jpg	/media/archive/Fotos/2019/IMG_0002.jpg
removed	2026-11-14T10:00:02Z	5	13	setup_old.exe	
```

- **First line:** `# sfo-move-notice <version>`. It marks the file as a notice. A notice of another version is
  recognised (the scan leaves it out) but not changed.
- **Lines starting with `#`:** explanation for a person; ignored when read.
- **Entry lines:** six fields separated by one tab:
  1. the word `moved`, or `removed` for a file a plan discarded (`discard`): no copy of it was made anywhere;
  2. when the original went into the hold folder, ISO 8601 UTC to the second;
  3. the plan id;
  4. the action id;
  5. the file name in this folder;
  6. the new place: the checked copy, or for `delete_source` the library file with the same content; empty for
     `removed`.
- `removed` exists since 14.11.2026 (https://github.com/crack666/smart_file_organizer/issues/105) and stays within
  version 1: a removed file is named as removed, not as moved. Until then the notice had only `moved` lines, and the
  second comment line said "were moved". A build from before reads a notice with `removed` lines without them, so it
  would not list them in `sfo where`; if it changes such a notice for its own plan, it drops them.
- In fields 5 and 6 a backslash, tab, line feed and carriage return are written as `\\`, `\t`, `\n`, `\r`.
- Entries are sorted by time, plan, action and name.

Nothing about the content of a file is written: no category, no summary, no checksum. A file marked sensitive appears
with its name only, like every other file.

## How it is written

The notice is derived from the journal (`plan_steps`): it lists the originals of the plan that are held or purged and
not put back. Each change is a journal step `notice` (`source_path` is the notice), written as `<notice>.sfo-partial`
and then named; a run cut off in between leaves the old notice or the new one, and the next run brings it in line.
The notice is the tool's own file: it is replaced in place only after it was read and recognised as a notice just
before. A dry run (`sfo plan apply --remove-sources` or `sfo plan undo` without `--execute`) writes nothing and reports
the notice as it would be: `would_write`, or `would_remove` when no entry would be left.

## How it is read

- The scan does not count a notice as a file (it is no finding and no content) and stores its entries in
  `notice_entries`; `sfo where` names them with source `notice`, also when the plan was executed with another database.
  A structure-only scan opens no file: there the name alone counts and the entries stay unread.
- The execution leaves notices out when it compares a source folder with its scan, and a folder that holds only its
  notice counts as left empty (`empty_folders`).
