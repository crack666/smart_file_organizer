# Product Overview

## Vision

`Smart File Organizer` is a cross-platform desktop application for scanning and understanding large local file collections. The long-term goal is to combine filesystem heuristics, persistent scan state, and local AI-assisted classification to help users review and organize old disks, backups, NAS exports, and home directories.

## Core use case

A user selects a root directory and lets the app:

- recursively scan the tree
- persist file and directory metadata in SQLite
- distinguish likely user data from irrelevant system/software areas
- classify images, documents, videos, and related content
- provide a review-oriented UI instead of generating static reports
- eventually propose and execute file actions such as move/copy/archive

## Product principles

- **Desktop-first**: real interactive UI, no HTML report as the primary product
- **Cross-platform**: target Windows and Linux first, without platform-specific UI dependencies
- **Open-source-friendly stack**: prefer permissive licenses and free-to-use libraries
- **Long-running robustness**: jobs should survive large datasets and future resume flows
- **Architecture over shortcuts**: business logic stays out of views; persistence and AI integrations stay behind services

## In scope

- recursive scan of a selected root path
- persistent scan results in SQLite
- folder tree + file table + detail/preview UI
- directory/file metadata inspection
- future AI-assisted classification via Ollama
- future review workflow and action execution

## Explicit non-goals

- paid Avalonia Accelerate controls
- TreeDataGrid dependency for the core UX
- browser-hosted frontend or HTML report workflow
- cloud-only AI dependency

## User experience direction

The intended interaction model is inspired by Explorer, TreeSize, and WizTree:

- left: folder hierarchy
- right/top: files of the selected node
- right/bottom: file details and preview
- later: filters, review actions, storage analysis, treemap, batch operations

## Technology goals

- .NET 10
- Avalonia UI
- MVVM
- SQLite persistence
- Dapper-based data access
- local Ollama HTTP API integration
- only free/open-source dependencies where practical
