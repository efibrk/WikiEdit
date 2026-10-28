# WikiEdits

WPF desktop app that queries the MediaWiki API of Wikipedia (default: he.wikipedia.org) for user
contributions, page histories, diffs, and Hebrew Wikipedia voting eligibility.

Targets **.NET Framework 4.8**, pinned to **x64**. It was converted from .NET 10 to cut the
distribution from 142 MB to ~2.3 MB: .NET Framework 4.8 is part of Windows 10 1903+ and Windows 11,
so no runtime has to be bundled or installed.

## Code conventions

- **All code comments must be in English.** This includes `//` comments, `/// <summary>` XML docs, XAML `<!-- -->` comments, and comments in `.csproj`. Never write Hebrew comments in code.
- User-facing strings (window text, labels, messages, status bar) are in Hebrew. That is UI content, not comments.
- The UI is right-to-left (`FlowDirection="RightToLeft"` on the window). The WebView2 control stays `LeftToRight` because the hosted Win32 window would otherwise be mirrored; the diff HTML sets `dir="rtl"` itself.

## .NET Framework constraints

Things the framework lacks, and how this project covers them. Keep these in mind before using a newer API:

- `GlobalUsings.cs` replaces `ImplicitUsings`, which .NET Framework projects do not support.
- `Compat.cs` supplies `Math.Clamp` and `HttpStatusCode.TooManyRequests`.
- **PolySharp** (build-time only, ships nothing) generates `IsExternalInit`, `Index` and `Range`,
  so records, `init` setters and list-slicing syntax compile.
- No `SocketsHttpHandler`: connection reuse is configured through `ServicePointManager` in
  `WikipediaClient`. TLS 1.2 is set explicitly; the framework default is too old.
- No `[GeneratedRegex]` source generator: use a `static readonly Regex` instead.
- `HttpContent.ReadAsStringAsync` has no cancellable overload.
- `PlatformTarget` must stay **x64**. As AnyCPU the app runs 64-bit but the WebView2 package copies
  only the 32-bit `WebView2Loader.dll`, which cannot load into it.

## Layout

- `WikiEdits/` is the single project. Solution file is `Vik.slnx` (the .NET 10 solution format).
- `WikipediaClient.cs` is the only place that talks to the API. All requests go through one gate (serial), with retry on 429/503.
- `VoteEligibility.cs` mirrors the Hebrew Wikipedia gadget `MediaWiki:Gadget-checkVoteRights.js`. Keep thresholds and namespace list in sync with it.
- `_backup/WinForms/` holds the pre-WPF form files for reference only; it is not part of the build.

## Building and running

- `dotnet build Vik.slnx` or Ctrl+F5 in VS Code (launch config `WikiEdits`).
- Requires the Microsoft Edge WebView2 Runtime (preinstalled on Windows 11).
- If the app is running, the build cannot overwrite `bin/Debug/.../WikiEdits.exe`; close it first.

## Distribution

- `.\publish.ps1 [-Version 1.1.0] [-NoZip]` builds the release package: `dist\` with 16 files and
  no subfolders (~2.3 MB), plus `WikiEdits-<version>-win.zip` (~0.8 MB). `DIST-README.txt` is copied
  in as the Hebrew readme, rewritten with a UTF-8 BOM so Notepad renders it.
- The script deliberately leaves out `*.pdb`, `*.xml`, `Microsoft.Web.WebView2.WinForms.dll`
  (this is a WPF app) and the `runtimes\` folder (the matching loader is already next to the .exe).
- Publish settings live in `WikiEdits/Properties/PublishProfiles/Dist.pubxml`.
- `publish.ps1` must stay saved as **UTF-8 with a BOM**. Windows PowerShell 5.1 reads a BOM-less
  script as ANSI and mangles the Hebrew literal used for the readme file name.
- `dist/`, `artifacts/`, `bin/` and `obj/` are build output and can be deleted at any time.
- `_backup/` holds reference copies of superseded versions (`WinForms/` pre-WPF, `net10/` pre-conversion).
  Not part of the build.
