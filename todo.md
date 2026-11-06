# SVN integration

## Completed

- [x] Configure the SVN executable and configuration directory; test connections and authentication.
- [x] Open existing working copies, including nested folders, and restore saved repository tabs.
- [x] Browse paged revision history with author, date, subject, and complete selectable messages.
- [x] Refresh history, cancel requests, retry authentication, and preserve Git repository detection.
- [x] Add backend, authenticated local-server, and headless UI regression checks.
- [x] Load the selected revision's changed paths and show added, modified, deleted, replaced, and copied entries within the working-copy scope, including historical copy-source metadata.

- [x] Show combined and side-by-side before/after diffs when selecting a changed file, using the existing text-diff presentation.
- [x] Resolve historical content and paths across copies and renames, including additions, deletions, and replacements; show explicit binary, empty, directory, and special-file states.
- [x] Preserve full-message access alongside the changed-file list and diff.
- [x] Load content on demand with cancellation, progress, useful errors, size limits, and the same SVN configuration and authentication safeguards as history queries.
- [x] Add regression fixtures for revision gaps, copies/renames, Unicode and special-character paths, binary content, and authentication failures.
- [x] Verify selection, paging, and large-diff behavior without modifying working-copy files.

- [x] Show working-copy status and read-only local content/property diffs, including unversioned, missing, conflicted, copied, moved, and replaced paths. Keep local inspection available independently of remote history and authentication.
- [x] Cache revision-history pages and changed-file lists on disk for five minutes; reuse them when opening tabs or revisiting revisions, and bypass the cache on explicit Refresh.

## Working-copy actions

- [x] Add selected unversioned paths and schedule removal of selected tracked/missing paths while keeping local files; confirm removal and refresh local status after actions.
- [x] Undo pending additions and removals before commit while preserving local file contents; use the separate Revert action to discard ordinary local edits.
- [x] Organize whole-working-copy actions in the repository toolbar, selected-path actions in shared menus, and revision actions in History; support bulk selection.
- [x] Add explicit local Revert with a path/scope confirmation, keeping Undo Add/Remove as content-preserving scheduling actions.
- [x] Add whole-working-copy Update and Update to revision, including an entry from the selected history revision.
- [x] Add Commit with a reviewed, explicit path selection and required message; leave unchecked changes uncommitted.

## Later increments

- [ ] Add conflict inspection and resolution workflows.
- [ ] Add SVN-aware Rename/Move/Copy, Ignore, editable Properties, and Changelists.
- [ ] Add Cleanup and missing-file/rename repair workflows with explicit scope.
- [ ] Add checkout and remote repository browsing as separate entry points from opening an existing working copy.
- [ ] Support SVN over SSH with explicit key/agent integration.
- [ ] Add revision search/filtering and navigation by revision number or path.
- [ ] Define and implement SVN-specific Branch/Tag, Switch/Merge, and Lock/Unlock workflows; keep Git-only actions out of SVN views.
- [ ] Add Blame, revision comparisons, Export, and patch creation/application workflows.

## First working-copy increment verification (2026-11-06)

- [x] Release build and formatting verification on macOS arm64 with .NET 10.0.400 and SVN 1.14.5.
- [x] 60 portable backend regression groups and authenticated local `svnserve` integration, including unsafe-path/ancestor rejection, exact commit selection, missing-file removal/commit, and Unicode commit messages.
- [x] 213 focused and 350 full headless UI checks, including pointer/keyboard paths, multi-selection, confirmation/cancellation, Update/Revert/Commit, and failed-commit draft recovery. Rendered working-copy and dialog screenshots inspected in both themes, including the compact layout.

## Remaining validation

- [ ] Exercise the native desktop interface in light/dark themes: folder opening, F5, keyboard navigation, clipboard copy, resizing, authentication, cancellation, and closing/restoring tabs. Headless checks already cover the core workflows and rendered layouts.
- [ ] Verify native credential-provider behavior and remembered credentials on supported platforms; current authenticated integration uses an isolated config with password stores disabled.
- [ ] Validate Native AOT publishing and packaged-app behavior for the SVN feature. Release compilation alone does not cover these.
