# SVN integration

## Completed

- [x] Configure the SVN executable and configuration directory; test connections and authentication.
- [x] Open existing working copies, including nested folders, and restore saved repository tabs.
- [x] Browse paged revision history with author, date, subject, and complete selectable messages.
- [x] Refresh history, cancel requests, retry authentication, and preserve Git repository detection.
- [x] Add backend, authenticated local-server, and headless UI regression checks.

## Next: revision files and diffs

- [ ] Load the selected revision's changed paths and show added, modified, deleted, and copied files within the working-copy scope.
- [ ] Show a before/after diff when selecting a changed file, using the existing diff presentation where suitable.
- [ ] Resolve historical content and paths correctly across copies and renames; handle additions, deletions, binary files, and empty files.
- [ ] Preserve full-message access alongside the changed-file list and diff.
- [ ] Load content on demand with cancellation, progress, useful errors, and the same SVN configuration and authentication safeguards as history queries.
- [ ] Add regression fixtures for revision gaps, copies/renames, Unicode and special-character paths, binary content, and authentication failures.
- [ ] Verify selection, paging, and large-diff behavior without modifying working-copy files.

## Later increments

- [ ] Show working-copy status and local diffs, including unversioned, missing, and conflicted files.
- [ ] Add deliberate user actions for add/remove, commit, update, and revert, with appropriate confirmation for discarded edits.
- [ ] Add conflict inspection and resolution workflows.
- [ ] Add checkout and remote repository browsing as separate entry points from opening an existing working copy.
- [ ] Support SVN over SSH with explicit key/agent integration.
- [ ] Add revision search/filtering and navigation by revision number or path.
- [ ] Define and implement SVN-specific branch/tag, merge, property, and lock workflows as needed; keep Git-only actions out of SVN views.

## Remaining validation

- [ ] Exercise the native desktop interface in light/dark themes: folder opening, F5, keyboard navigation, clipboard copy, resizing, authentication, cancellation, and closing/restoring tabs. Headless checks already cover the core workflows and rendered layouts.
- [ ] Verify native credential-provider behavior and remembered credentials on supported platforms; current authenticated integration uses an isolated config with password stores disabled.
- [ ] Validate Native AOT publishing and packaged-app behavior for the SVN feature. Release compilation alone does not cover these.
