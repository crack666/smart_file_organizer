# Status and Roadmap

## Current implementation status

## Implemented and working

### Scan and persistence

- solution and layered project structure created
- recursive scan engine implemented
- SQLite persistence implemented for:
  - scan jobs
  - directory nodes
  - file nodes
  - AI results
  - user overrides
- file type detection and heuristic scan pruning implemented
- tests exist for core scan heuristics/file type behavior and scan pipeline persistence

### Desktop UI

- root folder selection works
- scan start works
- pause/cancel controls exist
- folder tree loads and expands lazily
- file table loads selected directory contents correctly
- file details panel works
- image preview works
- text preview works
- first-page PDF preview works
- bottom details area and inner details/preview area are resizable via splitters

### Architectural groundwork

- Domain / Application / Infrastructure / Desktop separation in place
- repositories and services abstract persistence and integrations
- Ollama service contract and implementation exist
- classification pipeline service exists
- manual override service exists

## Implemented but not yet fully wired into product flow

### Ollama classification

The following pieces already exist:

- `IOllamaService`
- `OllamaService`
- `OllamaOptions`
- `ClassificationService`
- persistence for `ai_results`

What is still missing:

- automatic invocation of classification after or during scan jobs
- UI indication of Ollama availability/state
- user-configurable endpoint/model settings
- refresh flow that updates table/details once classifications arrive
- strategy for batching and throttling AI work in large datasets

### Review workflow

The following groundwork exists:

- `UserOverride` model
- `ReviewService`
- override persistence

What is still missing:

- override UI
- confirm/reject workflow
- review filters
- approval-driven file action planning

## Planned next priorities

1. **Wire Ollama into the real job lifecycle**
   - trigger classification from running/completed jobs
   - show status in the UI
   - refresh table/details when results are available

2. **Configuration and settings**
   - expose Ollama base URL/model
   - expose scan heuristics/blacklist settings
   - persist app-level configuration

3. **Review UX**
   - edit category/target suggestions
   - show AI summary/confidence inline
   - add manual approval state transitions

4. **Action pipeline**
   - plan file operations from approved items
   - preview actions before execution
   - execute with logging and rollback-friendly design where possible

5. **Performance and scale improvements**
   - larger dataset profiling
   - better virtualization strategy for large tables/trees
   - more resilient background processing for long AI runs

## Known caveats from development so far

- Avalonia `DataGrid` requires an explicit theme include; without it the grid can bind data but render like a black slab
- dark theme defaults caused several areas to appear fully black until explicit panel backgrounds/styles were applied
- preview sizing initially used an unconstrained layout; image previews did not respond to pane size until the preview area was converted to a bounded grid-based layout
- PDF previews initially rendered transparent backgrounds; this was fixed by forcing white background rendering
- integrating native PDFium runtimes may interact with Windows application control policies; this should be verified in restricted enterprise environments

## Definition of “MVP complete”

A meaningful MVP for this repository should include:

- scan + persist + browse working reliably
- AI classification actually running in product flow
- review and override workflow exposed in UI
- basic settings/configuration
- enough documentation for new contributors to reason about architecture and direction
