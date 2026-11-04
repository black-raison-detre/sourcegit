# SVN history and file-diff UI checks

Run from the repository root with .NET 10, Git, and the Subversion `svn` and
`svnadmin` executables on PATH:

```sh
dotnet run --project tests/Svn.UI/SourceGit.Svn.UI.csproj
```

The harness creates a disposable local repository with 104 revisions and a
working-copy path containing spaces (including a trailing space), Unicode, and `@`. It exercises the
production view model, folder-opening flow, tab lifecycle, and actual Avalonia
views using Avalonia's headless platform with Skia rendering. Checks include
paging, complete multiline messages, real changed paths, keyboard row
navigation, message selection, failed-refresh recovery, saved-tab reopening,
and current Git/SVN marker precedence over a saved repository type. Selecting a
changed file exercises the production historical-content path and shared text
diff view, including the r103-to-r104 copy baseline, Message/Changes tab state,
combined and side-by-side layouts, and an unchanged working copy. The harness
also checks the snapshot parser against submodule-looking and Git LFS pointer
text, plus the ordinary Git diff command path.

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
directory in `sourcegit-svn-ui-artifacts`. Set `SOURCEGIT_SVN_UI_ARTIFACTS` to
choose another artifact directory.

Headless checks do not prove native macOS window integration, clipboard
behavior, native credential storage, or packaged-app behavior. Authenticated
SVN backend integration is covered separately by `tests/Svn`.

In the application, use **Open Local Repository** and select an existing SVN
working-copy folder. Selecting a nested folder opens the working-copy root.
The SVN tab shows 100 revisions per page. Select a row to view its changed
files and historical diff under **Changes**, or open **Message** to read and
copy the complete revision message. Choose **Load more** for older revisions,
or **Refresh** (F5) for current history. Browsing and historical diffs are
read-only; checkout, commit, and update are outside this increment. SVN over
SSH is not supported by this browser yet.
