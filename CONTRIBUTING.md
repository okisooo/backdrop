# contributing to backdrop

bug reports, small improvements, and clear documentation are welcome ♡

## report a problem or suggest an idea

check the [existing issues](https://github.com/okisooo/backdrop/issues) and [known limitations](docs/reference.md#limitations), then [open an issue](https://github.com/okisooo/backdrop/issues/new/choose).

for a bug, include your backdrop version, windows and target app versions, display scaling, and steps to reproduce. use an empty sample chat for screenshots, with private content outside the capture.

## make a change

use windows x64 and the .NET 9 SDK. fork the repository, make a focused change on a branch, and explain the before-and-after behavior in your pull request. for a larger feature or a change to window detection, start with an issue so the intended behavior is clear.

run the existing checks for code changes:

```powershell
.\tools\verify.ps1
```

this builds the app and runs the detector and engine checks. the [verification reference](docs/reference.md#verification) describes optional GIF checks and their limits. for documentation-only edits, verify the affected instructions and links.

include any relevant manual checks in the pull request, such as app attachment, window movement, readability, or wallpaper changes. explain what remains untested. keep generated builds, captures, private chats, and credentials out of commits.
