# Documentation

This directory contains the working project documentation for `Smart File Organizer`.

## Documents

- `product-overview.md` — product vision, target use cases, scope, and non-goals
- `status-and-roadmap.md` — current implementation status, shipped features, gaps, and next priorities
- `architecture.md` — solution structure, layer responsibilities, runtime flow, and key extension points
- `engineering-decisions.md` — important library choices, trade-offs, caveats, and lessons learned so far
- `code-guidelines.md` — repository-specific engineering and architectural guidelines

## Current summary

The repository already contains a functional vertical slice:

- recursive file scan into SQLite
- folder tree view with lazy expansion
- file table for the selected directory
- file details panel with image/text/PDF preview
- pause/cancel job controls
- repository and service abstractions across Domain / Application / Infrastructure / Desktop

The largest missing product feature is the end-to-end Ollama workflow: the service and classification pipeline exist, but classification is not yet wired into the active job lifecycle or surfaced in settings/UI.
