# Measurements

Throughput and judgement quality on the target machine, one section per series, newest first.
How `sfo eval` scores and what each kind means: [evaluation.md](evaluation.md). A number here is
the state on the date it names; measure again before relying on it.

## Parallelism 1, 2, 4, 8 with gemma4:26b (22.11.2026)

_Measured 22.11.2026 16:34 to 16:47 local time by the issue sweep on crackdesk (RTX 5090, Ollama on
`localhost:11434`, GPU reserved through the machine's gate), corpus small v9 seed 36 from
`sfo eval generate`, release build of the branch of
https://github.com/crack666/smart_file_organizer/pull/115 (reports app version `1.0.0+b8f8223`),
prompt fingerprint `cb689fc2ed1e8169`, one run per setting, one database for the whole series
(`run_id` 1 to 4), `--parallel` sets `ollama.max_parallel_requests`
(https://github.com/crack666/smart_file_organizer/issues/10)._

| Parallel | Analysis | Files per minute | Model calls | `directory_verdict` | `file_category` | `sensitive` (false alarms) | `finding` (superfluous) | `target` | `video_kind` |
|---|---|---|---|---|---|---|---|---|---|
| 1 | 185.4 s | 71.5 | 225 | 74/82 | 101/124 | 209/221 (12) | 19/25 (5) | 10/14 | 22/24 |
| 2 | 138.3 s | 95.9 | 219 | 70/82 | 95/124 | 211/221 (10) | 19/25 (6) | 12/14 | 22/24 |
| 4 | 147.4 s | 90.0 | 234 | 74/82 | 104/124 | 209/221 (12) | 21/25 (6) | 11/14 | 22/24 |
| 8 | 146.3 s | 90.6 | 229 | 74/82 | 103/124 | 208/221 (13) | 19/25 (5) | 11/14 | 22/24 |

Files per minute counts the 221 files the `sensitive` kind scores, over the analysis time; the scan
took under a second in every run. No answer was cut off. Every marked file was found in every run
(20 of 20); the misses in `sensitive` are false alarms only.

Read with care:

- The model was not loaded when the run with parallel 1 started, so its time includes the first
  load, and its gap to the others is smaller than the table shows.
- From 2 requests on the time no longer falls: 2, 4 and 8 lie within 10 s of each other, one run
  each. The default of 4 (`appsettings.json`) is 9 s slower than 2 here, within what one run can tell.
- The judgements differ between runs by a few hits in either direction without a trend over the
  parallelism; one run per setting cannot separate that from chance.
- A comparison with other models has not been run: which models go into it is the owner's call
  (https://github.com/crack666/smart_file_organizer/issues/10).
