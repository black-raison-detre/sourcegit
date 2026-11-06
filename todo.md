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
- [x] Add single-path conflict inspection and explicit resolution: compare available file versions, inspect property reports/current diffs, and review tree-conflict details. Confirm path-wide resolution, require acknowledgment for manually repaired trees, reject stale snapshots, and refresh local status after attempted changes.
- [x] Add working-copy-local SVN-aware Rename, Move, and Copy for tracked files/folders, including clean-source entry, exact destinations, retained local edits and ancestry, and explicit Commit selection for copied paths and paired moves.

## Later increments

- [x] Add Ignore and editable direct Properties, including exact-name ignore previews, protected and binary read-only values, stale-snapshot recovery, and commit-aware working-copy dialogs.
- [ ] Add Changelists.
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

## Conflict increment verification (2026-11-06)

- [x] 65 portable backend regression groups, including bounded artifact reads, stale snapshot rejection, malformed/unsafe metadata, missing tree victims, cancellation, and refusal to resolve an existing file without a bounded current-content snapshot.
- [x] Disposable `file://` conflict integration: Base/Local/Incoming choices, combined text/property conflicts, root and directory properties, missing and manually recreated tree victims, and preservation of child conflicts and unversioned files. Authenticated local `svnserve` integration also passes.
- [x] 432 full headless UI/workflow checks, followed by 84 focused conflict checks covering the final source. Light/dark renders inspected, including the minimum 620×480 dialog size, stale recovery, and property-report layout. Independent source review approved.
- [x] Release build and formatting verification on macOS arm64 with .NET 10.0.400 and SVN 1.14.5.

## Rename/Move/Copy increment verification (2026-11-06)

- [x] Disposable `file://` operations preserve local text/property edits and copy ancestry through Commit, including file/folder moves, folder copies, and exact embedded/trailing `@` names. Unsafe untracked/ignored trees remain unchanged; authenticated local `svnserve` integration also passes.
- [x] 501 full headless GUI/workflow checks, followed by 69 focused checks on the final production source. Coverage includes clean-source entry, selected-action keyboard navigation, Enter/Escape, move-pair Commit selection, switched-subtree exclusion, stale-source recovery, and literal Unix backslashes. Light/dark renders inspected at the minimum 540×480 dialog size, including visible failure summaries above the fixed footer.
- [x] Release build and formatting verification on macOS arm64 with .NET 10.0.400 and SVN 1.14.5. Independent source review approved after correcting Windows path-separator validation; Windows runtime proof remains outstanding.

## Ignore/Properties increment verification (2026-11-06)

- [x] 100 portable backend regression checks, including whitespace-only property preservation and stale-token rejection, bounded XML/base64 reads, unsafe-path and binary-edit refusal, exact-name ignore escaping, cancellation, and protected temporary-file cleanup after preparation failure.
- [x] Disposable `file://` property integration covers root/file/folder properties, exact Unicode and newline values, empty values versus deletion, stale external changes, and literal wildcard/backslash/`@` ignore names while preserving existing rules and unrelated properties/files. Authenticated local `svnserve` integration also passes.
- [x] 87 focused Properties/Ignore headless checks and 590 full GUI/workflow checks, including clean-source/root entry, keyboard menus, draft/deletion confirmations, multiline input, stale recovery, status/property diffs, and explicit Commit with repository-HEAD verification. Compact Properties, Ignore, stale-error, and resulting working-copy renders inspected in both themes.
- [x] Release build and formatting verification on macOS arm64 with .NET 10.0.400 and SVN 1.14.5. Independent source review approved after fixing whitespace-only XML parsing and temporary-file failure cleanup.

## Remaining validation

- [ ] Exercise the native desktop interface in light/dark themes: folder opening, F5, keyboard navigation, clipboard copy, resizing, authentication, cancellation, conflict inspection/resolution, Rename/Move/Copy, Properties/Ignore and their native file/folder pickers, and closing/restoring tabs. Include Windows path and case-only rename behavior. Headless checks already cover the core workflows and rendered layouts.
- [ ] Verify native credential-provider behavior and remembered credentials on supported platforms; current authenticated integration uses an isolated config with password stores disabled.
- [ ] Validate Native AOT publishing and packaged-app behavior for the SVN feature. Release compilation alone does not cover these.
