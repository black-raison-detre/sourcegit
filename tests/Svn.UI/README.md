# SVN history UI checks

Run from the repository root with .NET 10, Git, and the Subversion `svn` and
`svnadmin` executables on PATH:

```sh
dotnet run --project tests/Svn.UI/SourceGit.Svn.UI.csproj
```

The harness creates a disposable local repository with 104 revisions and a
working-copy path containing spaces (including a trailing space), Unicode, and `@`. It exercises the
production view model, folder-opening flow, tab lifecycle, and actual Avalonia
views using Avalonia's headless platform with Skia rendering. Checks include
paging, complete multiline messages, empty messages, keyboard row navigation,
message selection, failed-refresh recovery, saved-tab reopening, and current
Git/SVN marker precedence over a saved repository type. Its own apphost also
acts as a fake SVN executable for deterministic view-model sign-in, credential
reuse during paging, and credential clearing after a working-copy URL change.

All application preferences and SVN configuration are isolated in a temporary
directory. The harness does not start the installed app or modify an existing
working copy. It saves light/dark screenshots at 1024×600, a larger layout,
and error/authentication states under the system temporary directory in
`sourcegit-svn-ui-artifacts`. Set `SOURCEGIT_SVN_UI_ARTIFACTS` to choose another
artifact directory.

Headless checks do not prove native macOS window integration, clipboard
behavior, native credential storage, or packaged-app behavior. Authenticated
SVN backend integration is covered separately by `tests/Svn`.

In the application, use **Open Local Repository** and select an existing SVN
working-copy folder. Selecting a nested folder opens the working-copy root.
The SVN tab shows 100 revisions per page; select a row to read its full message,
choose **Load more** for older revisions, or **Refresh** (F5) for current history.
This feature is read-only; checkout, commit, update, and diffs are outside this
increment. SVN over SSH is not supported by this browser yet.
