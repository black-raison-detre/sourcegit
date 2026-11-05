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
refreshes. Add/remove checks exercise keyboard activation of Add, action
eligibility, removal confirmation and cancellation, concurrent-action prevention,
keep-local removal of modified and missing files, non-recursive directory addition,
and stale-selection errors with a refreshed, usable status list.
Undo checks activate the actual toolbar control by keyboard, retain bytes when
cancelling additions and removals, restore absent deleted files, undo added
directories with scheduled children, reject stale selections, and exclude
ordinary edits, conflicts, copies, and replacements from scheduling Undo.
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
directory in `sourcegit-svn-ui-artifacts`. Working-copy property and conflict
views are captured in both themes at 1024×600. Add/remove controls, removal results, and
action errors and Undo results are also captured in both themes. Set `SOURCEGIT_SVN_UI_ARTIFACTS`
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
ordinary local edits. These actions do not commit changes. Choose **Load more** for older
revisions, or **Refresh** (F5) for the active tab. Checkout, commit, update,
conflict resolution, and SVN over SSH are outside this increment.
