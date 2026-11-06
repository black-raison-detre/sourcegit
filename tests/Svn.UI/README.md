# SVN history and file-diff UI checks

Run from the repository root with .NET 10, Git, and the Subversion `svn` and
`svnadmin` executables on PATH:

```sh
dotnet run --project tests/Svn.UI/SourceGit.Svn.UI.csproj
```

For a focused working-copy status/diff UI run without the 104-revision history
fixture, use:

```sh
dotnet run --project tests/Svn.UI/SourceGit.Svn.UI.csproj -- --local-only
```

To run only conflict inspection and resolution checks and their rendered dialogs:

```sh
dotnet run --project tests/Svn.UI/SourceGit.Svn.UI.csproj -- --conflicts-only
```

The harness creates a disposable local repository with 104 revisions and a
working-copy path containing spaces (including a trailing space), Unicode, and `@`.
A second `file://` repository supplies local-only status and diff cases, including
modified, added, scheduled-deleted, unversioned, missing, conflicted,
property-only, directory, copied, moved, and replaced paths. It exercises the
production view models, folder-opening flow, tab lifecycle, and actual Avalonia
views using Avalonia's headless platform with Skia rendering. Checks include
paging, complete multiline messages, real changed paths, keyboard row
navigation, message selection, failed-refresh recovery, saved-tab reopening,
and current Git/SVN marker precedence over a saved repository type. Selecting a
changed file exercises the production historical-content path and shared text
diff view, including the r103-to-r104 copy baseline, Message/Changes tab state,
combined and side-by-side layouts, and an unchanged working copy. The local
checks verify BASE-to-local text diffs, property output, conflict notices,
missing and directory states, and copy/move/replacement behavior. They confirm
status rows and diffs cannot be edited directly, preserve selection across refresh
and tab switches, and leave history selection intact when the working-copy tab
refreshes. Extended-selection checks exercise right-click on selected and
unselected status/history rows, menu eligibility, all-or-nothing mixed selections,
busy-state disabling, and multi-path Remove/Undo while preserving local bytes.
Revert covers declined and accepted bounded confirmation, content and property
restoration, restoration of missing files, and preservation of unversioned files.
Update checks a specific revision and then HEAD; Commit submits one selected path
and confirms an unchecked edit remains local. Commit cancellation and stale
candidate failure retain the typed message for retry; success clears the draft.
Successful recovery must clear an earlier action error. Scheduling checks cover
keep-local removal of modified and missing files, non-recursive directory
addition, stale-selection errors with a refreshed status list, Undo of
additions/deletions/directories, and exclusion of
conflicts, copies, replacements, and ordinary edits from scheduling Undo.
An independent conflict fixture exercises the shared action menu and the conflict
dialog against real text, property, combined text/property, and tree conflicts.
It checks cancellation, explicit replacement confirmation, stale-snapshot
rejection and reload, preservation of manually repaired content and property
values, missing-artifact availability, and the acknowledgment required for a
manually repaired tree conflict.
Resolution must refresh status without resolving another path or changing
unversioned files. Text comparisons, property reports, tree details, and stale
recovery are rendered in both themes at a compact window size.
A fake SVN client delays local diffs to check cancellation, retry, stale-result
suppression, and local status access while remote history requests credentials.
The harness also checks the snapshot parser against submodule-looking and Git
LFS pointer text, plus the ordinary Git diff command path.

Its own apphost acts as a fake SVN executable for deterministic copy, binary,
empty, directory, unchanged, and large-file results; authentication and sign-in
for the same selected file; paging credentials; stale-response suppression;
cancellation and retry; and credential clearing after a working-copy URL
change. It also exercises added-empty-versus-absent endpoints and copy-source
metadata with spaces and Unicode paths.

All application preferences and SVN configuration are isolated in a temporary
directory. The harness does not start the installed app or modify an existing
working copy. It saves light/dark screenshots at 1024×600 for combined and
side-by-side historical diffs, a larger layout, error/authentication states,
and changed-path rows with Unicode and copy metadata under the system temporary
directory in `sourcegit-svn-ui-artifacts`. Working-copy property, conflict,
selected-action, narrow-window, Update, Commit, and bounded Revert confirmation
screens are captured in both themes. Remove/Undo results and action errors are
also captured in both themes. Set `SOURCEGIT_SVN_UI_ARTIFACTS`
to choose another artifact directory.

Headless checks do not prove native macOS window integration, clipboard
behavior, native credential storage, or packaged-app behavior. Authenticated
SVN backend integration is covered separately by `tests/Svn`.

In the application, use **Open Local Repository** and select an existing SVN
working-copy folder. Selecting a nested folder opens the working-copy root.
The **History** tab shows 100 revisions per page. Select a row to view its
changed files and historical diff under **Changes**, or open **Message** to
read and copy the complete revision message. The **Working copy** tab shows
local status and read-only diffs against BASE. Select an unversioned row and choose
**Add** to schedule just that path
(directory contents are added separately). Choose **Remove (keep local)** on an
eligible tracked or missing row and confirm to schedule deletion while retaining
local files. **Undo** cancels a pending addition or removal and preserves local
contents; cancelling an addition leaves files unversioned. It does not discard
ordinary local edits. The **Selected actions** menu applies Add, Remove, Undo,
Revert, and Commit to the selected status rows; confirmation screens list the full
selected scope. The toolbar updates to HEAD or a chosen revision and opens the
commit form for path selection. Choose **Load more** for older revisions, or
**Refresh** (F5) for the active tab. For one conflicted row, choose
**Inspect and resolve conflict…**. Compare available file versions or inspect
the property report and tree details, then explicitly choose a resolution.
The resolution applies to all conflicts on that path. Repair tree conflicts
manually before acknowledging them. After editing a file or its properties
outside the dialog, reload and review the snapshot before marking it resolved.
SVN over SSH remains outside this increment.
