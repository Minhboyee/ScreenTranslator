# ScreenTranslator architecture

This document describes the implemented architecture of the .NET 10 WinUI 3
ScreenTranslator application. It complements the repository invariants in
[`AGENTS.md`](../AGENTS.md) and the realtime implementation record in
[`realtime-ocr-stability-report.md`](realtime-ocr-stability-report.md).

## Product flow

ScreenTranslator translates text from a bounded region of a selected Windows window and
keeps the translated text aligned with the source region in a separate overlay. The
implemented flow is:

```text
WinUI app
  -> selected window and bounded region
  -> Windows Graphics Capture
  -> latest-value frame pump
  -> BGRA8 copy and bounded crop
  -> serial Windows OCR with a latest pending bitmap
  -> OCR line mapping and stability selection
  -> immutable OCR handoff to the app
  -> bounded line translation and translation memory
  -> immutable presentation handoff
  -> full overlay replacement
```

The realtime policy values useful to maintainers are:

- the capture frame pool has two Windows Graphics Capture buffers, but application
  handoffs are capacity-one latest-value slots;
- the OCR scheduler has one active bitmap and one replaceable pending bitmap, with a
  minimum sample interval of 100 ms;
- changed OCR content settles for up to 225 ms, while an empty result has a 600 ms grace
  period before the overlay is cleared;
- the app-side line coordinator limits actual provider calls to three per session,
  including calls whose providers have not yet returned after cancellation; and
- overlay updates apply a complete current snapshot, not an incremental append of
  whichever provider callback happened to finish last.

For the ownership, epoch, retirement-race, and overlay details behind these rules, see
the [realtime OCR stability report](realtime-ocr-stability-report.md).

## Layer direction

The repository publishes this dependency direction:

```text
App
 |-- Windows
 |     `-- Core
 |-- Providers
 |     `-- Core
 `-- Core (contracts used when composing the app)
```

In shorthand:

```text
App -> Windows/Providers -> Core
```

`Translator.App.WinUI` composes the other projects and also consumes Core contracts
directly at its orchestration boundary. `Translator.Windows` and
`Translator.Providers.OpenAICompatible` reference Core; neither lower layer references
the app or the other lower layer. The solution projects reflect this arrangement.

### App

`src/Translator.App.WinUI/` owns WinUI pages and windows, region selection, session
lifecycle, provider construction, line-level reconciliation, and overlay presentation.
`MainPage` receives immutable OCR documents, creates `TranslationRequest` values, and
publishes only the newest presentation version to the UI dispatcher. The app is the
composition root: it supplies the runtime provider options and owns the UI-facing
resources.

### Windows

`src/Translator.Windows/` contains Windows-specific capture and OCR integration. It uses
Windows Graphics Capture and WinRT/Windows OCR APIs, copies frames to `SoftwareBitmap`,
performs direct BGRA8 row cropping, maps OCR words to lines, samples presentation hints,
and manages selection epochs and shutdown. `PhysicalPixelRect` is used for the OCR and
overlay coordinate contract; logical UI units must not cross that contract.

### Providers

`src/Translator.Providers.OpenAICompatible/` contains the current HTTP adapter for
OpenAI-compatible chat-completion endpoints. It implements Core's `ITextTranslator`,
constructs provider requests, applies the optional runtime authorization header, parses
successful responses, and bounds/sanitizes provider diagnostics. Provider-specific
request details stay out of Core.

### Core

`src/Translator.Core/` is the platform-neutral layer. It owns immutable text, OCR,
language, translation, and memory-key contracts; `ITextTranslator` and
`ITranslationMemory`; the bounded translation-memory cache; and `TranslationSession`
with latest-value mailbox, generation, cancellation, in-flight deduplication, and stale
publication suppression.

## Core restrictions

Core must remain independent of the operating system and provider implementations. It
must not contain:

- network clients or HTTP calls;
- WinRT, Windows interop, or Windows Graphics Capture code;
- OCR engine/provider integration;
- model downloads;
- WinUI, overlay, or UI-dispatcher code; or
- a service locator or speculative plugin system.

The supported extension points are intentionally narrow: `ITextTranslator` and
`ITranslationMemory`. New platform or provider behavior belongs above Core and should
be expressed through those contracts rather than by adding platform conditionals to
Core.

The Core invariants that must be preserved are:

1. The pending-input mailbox has capacity one; new input replaces stale pending input.
2. Every submission receives a monotonically increasing generation.
3. A result may be published only when its generation is still current.
4. Superseded work receives cancellation, and a provider that completes late cannot
   publish a stale result.
5. OCR bounds remain physical-pixel coordinates.
6. The latest-value mailbox must not become an unbounded queue.

These rules are also recorded in [`AGENTS.md`](../AGENTS.md); changes to Core should be
checked against that file before implementation.

## Translation identity and presentation

The app-side coordinator separates translation work from line placement:

- `TranslationMemoryKey` is based on normalized source text, language pair, and
  provider revision. It determines cache reuse and whether a provider call is needed.
- An occurrence identity identifies a particular repeated line in the ordered OCR
  document. It determines placement in the current snapshot, not translation reuse.
- A changed presentation (bounds or appearance hint) can move an existing successful
  translation without restarting provider work.
- When new content is accepted, the app publishes a full snapshot. Removed lines are
  not retained, and pending/error lines remain represented until a later snapshot
  provides successful text.

This separation prevents animated backgrounds and small bounds changes from causing
translation churn while preventing obsolete overlay text from surviving a real content
replacement.

## Packaging

`Translator.App.WinUI.csproj` is a single-project MSIX WinUI app. It enables
`UseWinUI`, `EnableMsixTooling`, and `GenerateAppxPackageOnBuild`; the package identity
and capabilities are declared in `Package.appxmanifest`. The checked-in publish
profiles provide x86, x64, and ARM64 runtime identifiers, while local verification can
target x64 with:

```powershell
dotnet build .\src\Translator.App.WinUI\Translator.App.WinUI.csproj `
  -c Release -p:Platform=x64 -p:PublishReadyToRun=false --no-restore
```

The manifest currently declares version `1.0.0.20`. Build output, package artifacts,
certificates, and local signing material are local artifacts and must not be committed.
An API key is runtime-only and must never be included in source, configuration,
packaging, or documentation.

## Verification map

Use focused tests for the layer being changed:

| Change area | Focused command |
|---|---|
| Core contracts, mailbox, cache, session, or line coordinator | `dotnet test .\tests\Translator.Core.Tests\Translator.Core.Tests.csproj --no-restore` |
| OpenAI-compatible request/response behavior | `dotnet test .\tests\Translator.Providers.OpenAICompatible.Tests\Translator.Providers.OpenAICompatible.Tests.csproj --no-restore` |
| Windows bounds, crop, OCR stability, scheduler, or epoch behavior | `dotnet test .\tests\Translator.Windows.Tests\Translator.Windows.Tests.csproj --no-restore` |
| Cross-project build or packaging changes | `dotnet build .\ScreenTranslator.slnx --no-restore` and the app build above |

The local dynamic OCR fixture is at
`tests/fixtures/dynamic-ocr-fixture.html`. The full test and measurement context is in
the [realtime OCR stability report](realtime-ocr-stability-report.md).
