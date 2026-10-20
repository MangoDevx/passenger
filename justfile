# Passenger's recipes. `just --list` shows them all.

set windows-shell := ["powershell.exe", "-NoLogo", "-Command"]

app := "src/Passenger.App"

# Avalonia's build task (Avalonia.BuildServices) sends usage data from every build of an Avalonia project unless this
# is in the environment, the only place it looks; every recipe here builds with it set.
export AVALONIA_TELEMETRY_OPTOUT := "1"

# Build everything (Debug). Warnings are errors.
build:
    dotnet build

# Every test: the IDE driven whole, headless. A filter narrows them: just test Editing
test filter="":
    dotnet test tests/Passenger.Tests {{ if filter == "" { "" } else { "--filter " + quote(filter) } }}

# Each test class's seconds on CI's runners, by which CI deals each system's tests to its shards (TestTimes.json),
# rewritten from CI runs' results, which gh downloads, for committing: just test-times RUN [RUN...]
test-times +runs:
    python3 -I tools/test-shards.py update {{runs}}

# Run the IDE (Debug, JIT): just run [--theme dark|light|system]
run *args:
    dotnet run --project {{app}} -- {{args}}

# --window-buttons draws in macOS's traffic lights, which the off-screen window lacks (Windows' and Linux's are the bar's).
# Draw the main window off-screen to a PNG: just screenshot out.png [--size 1600x1000] [--scale 2] [--theme light] [--window-buttons]
screenshot path *args:
    dotnet run --project {{app}} -- --screenshot {{path}} {{args}}

# A self-contained Release build for one platform, precompiled with ReadyToRun for startup:
# just publish osx-arm64 | win-x64 | win-arm64 | linux-x64 | linux-arm64 (no Intel Mac build ships)
publish rid:
    dotnet publish {{app}} -c Release -r {{rid}} --self-contained -p:PublishReadyToRun=true

# The macOS disk image, artifacts/package/Passenger-macos-arm64.dmg (Apple Silicon only): the app signed ad hoc, or with
# PASSENGER_SIGN_IDENTITY and then, with PASSENGER_NOTARY_PROFILE, the app and the image notarized and stapled; checked
# mounted. --dry-run writes what those two would run instead: just package-mac [osx-arm64] [--dry-run]
package-mac *args:
    tools/package-mac.sh {{args}}

# Passenger's Windows installer, packed by Velopack's vpk (dotnet-tools.json): it installs per user with no
# administrator, adds a Start menu entry, opens .sln, .slnx and .csproj in Passenger, and uninstalls from Settings > Apps.
# Signed on Windows by PASSENGER_SIGNTOOL_PARAMS or PASSENGER_ARTIFACT_SIGNING_METADATA (tools/package-win.sh).
# The Windows installer, artifacts/package/Passenger-windows-x64.exe or -arm64.exe: just package-win [win-x64|win-arm64]
[unix]
package-win rid="win-x64":
    tools/package-win.sh {{rid}}

# On Windows the script runs in Git's sh: the one on the path, else Git for Windows' own beside git.exe.
# The Windows installer, artifacts/package/Passenger-windows-x64.exe or -arm64.exe: just package-win [win-x64|win-arm64]
[windows]
package-win rid="win-x64":
    $sh = (Get-Command sh -ErrorAction SilentlyContinue).Source; if (-not $sh) { $sh = Join-Path (Split-Path (Split-Path (Get-Command git).Source)) 'bin\sh.exe' }; & $sh tools/package-win.sh {{rid}}

# Passenger for Linux as site/linux.html describes it: one passenger/ folder (the app, its launcher, install.sh and the
# desktop entry with its icon) in artifacts/package/Passenger-linux-x64.tar.gz or -arm64.tar.gz, unpacked and checked.
# On Linux or macOS: just package-linux [linux-x64|linux-arm64]
[unix]
package-linux rid="linux-x64":
    tools/package-linux.sh {{rid}}

# Passenger's own icons (Passenger.icns, passenger.png, Passenger.ico) from Assets/passenger.svg; macOS, by sips.
app-icon:
    tools/app-icon.sh

# Rewrite the generated themes (Theme/Sorbet.axaml and the rest) from the palettes in tools/themes.py, each checked
# against the contrast UiRulesTests asks.
themes:
    python3 -I tools/themes.py

# Rewrite Theme/Icons.axaml from the list in tools/icons.py (fetches any icon not yet cached).
icons:
    python3 -I tools/icons.py

# Fail naming any package the solution restores, Roslyn's server carries or dotnet-tools.json pins that the notices miss.
notices:
    python3 -I tools/check-notices.py

# Rewrite the terminal's table of wide characters from the Unicode Character Database (tools/unicode-widths.py).
unicode-widths:
    python3 -I tools/unicode-widths.py

# Passenger as an MCP server over a folder, its tools on standard input and output, run from a clone of the
# last build so rebuilding Passenger never changes the files under it (tools/mcp.sh). `.mcp.json` starts it.
mcp folder="":
    @tools/mcp.sh {{folder}}

# The site's pictures, site/images/<scene>-<dark|light>.webp, taken again from the sample solution in tools/site-shots
# (after just build; macOS, with cwebp, and python3 for the agent scene's agent): just site-shots [SCENE...]
[unix]
site-shots *scenes:
    tools/site-shots/shots.sh {{scenes}}
