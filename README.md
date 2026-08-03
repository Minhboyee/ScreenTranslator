# ScreenTranslator

ScreenTranslator is a WinUI 3 desktop application on .NET 10 for translating text in a
bounded region of a Windows application. It captures the selected region in real time,
runs Windows OCR, sends OCR lines to a translation provider, and renders the latest
successful translations in a click-through overlay.

The current app flow is:

1. Select a visible Chrome window and drag a bounded region on a frozen preview.
2. Capture that region with Windows Graphics Capture.
3. Copy and crop the frame as BGRA8 pixels, then run Windows OCR.
4. Stabilize OCR content and presentation changes so background animation and one-pixel
   bounds jitter do not restart useful translation work.
5. Translate distinct lines with bounded concurrency and a translation-memory cache.
6. Replace the overlay with the newest immutable presentation snapshot.

The realtime path is deliberately latest-value oriented: each handoff keeps at most one
pending value, a newer value replaces stale pending work, and stale OCR or translation
results are suppressed. OCR bounds and overlay coordinates use physical pixels. The
implementation details and measured behavior are recorded in
[`docs/realtime-ocr-stability-report.md`](docs/realtime-ocr-stability-report.md).

## Repository layout

```text
src/
  Translator.App.WinUI/                  WinUI 3 app, session orchestration, and overlay
  Translator.Windows/                   Windows capture, cropping, and OCR integration
  Translator.Providers.OpenAICompatible/ HTTP translation-provider adapter
  Translator.Core/                      platform-neutral contracts and coordination
tests/
  Translator.Core.Tests/
  Translator.Windows.Tests/
  Translator.Providers.OpenAICompatible.Tests/
docs/
  architecture.md
  realtime-ocr-stability-report.md
```

The solution is `ScreenTranslator.slnx`. The dependency direction and the restrictions
on the Core layer are documented in [`docs/architecture.md`](docs/architecture.md).
The repository invariants in [`AGENTS.md`](AGENTS.md) are part of the engineering
contract, especially for latest-value mailboxes, generation checks, cancellation, and
physical-pixel bounds.

## Runtime configuration and secrets

The app currently uses an OpenAI-compatible HTTP provider. Endpoint, model, source and
target languages, and the optional provider API key are supplied at runtime when a
session starts. An API key is runtime-only: it must never be committed, placed in source
or project configuration, or added to a package. Use local environment/configuration
outside the repository only when a local workflow needs to supply a secret.

## Build and run

Use a Windows development environment with the .NET 10 SDK and the Windows/WinUI
workloads and packages required by the Windows-targeting projects.

From the repository root:

```powershell
dotnet restore .\ScreenTranslator.slnx
dotnet build .\ScreenTranslator.slnx --no-restore
```

For a local packaged WinUI run, use the app project and an available platform profile:

```powershell
dotnet run --project .\src\Translator.App.WinUI\Translator.App.WinUI.csproj -c Debug -p:Platform=x64
```

The app is configured as a single-project MSIX project. Its project enables
`GenerateAppxPackageOnBuild` and `EnableMsixTooling`; therefore an app build also
exercises MSIX package generation:

```powershell
dotnet build .\src\Translator.App.WinUI\Translator.App.WinUI.csproj `
  -c Release -p:Platform=x64 -p:PublishReadyToRun=false --no-restore
```

Package and intermediate output is generated below the app's build directories and is
not source-controlled. The manifest currently declares package version `1.0.0.20`.

## Focused verification

Run the smallest relevant check while developing, then run the full solution build and
the test projects before integration:

```powershell
# Core mailbox, generation, cache, and coordinator behavior
dotnet test .\tests\Translator.Core.Tests\Translator.Core.Tests.csproj --no-restore

# OpenAI-compatible request, response, and provider-error behavior
dotnet test .\tests\Translator.Providers.OpenAICompatible.Tests\Translator.Providers.OpenAICompatible.Tests.csproj --no-restore

# Windows capture/OCR support logic, bounds, crop, stability, and scheduler behavior
dotnet test .\tests\Translator.Windows.Tests\Translator.Windows.Tests.csproj --no-restore

# Integration build for every project in the solution
dotnet build .\ScreenTranslator.slnx --no-restore
```

For the realtime path, `tests/fixtures/dynamic-ocr-fixture.html` provides a local
repeatable fixture for animated backgrounds, bounds jitter, text replacement, and a
short empty-OCR interval. Use the app and fixture for manual overlay/lifecycle checks
when changing capture, OCR, or presentation behavior. Use the commands above as the
local verification baseline.
