# Contributing to ScreenTranslator

ScreenTranslator is organized around a small platform-neutral Core and explicit Windows,
provider, and WinUI composition layers. Keep changes narrow, preserve the invariants in
[`AGENTS.md`](AGENTS.md), and update the relevant documentation when behavior changes.

## Active branch ownership

Active development is divided by ownership:

| Branch | Owns |
|---|---|
| `core` | `src/Translator.Core/` and Core-focused tests. This branch owns contracts, latest-value session behavior, cache behavior, generation checks, and Core invariants. |
| `platform-and-providers` | `src/Translator.Windows/`, `src/Translator.Providers.OpenAICompatible/`, and their focused tests. This branch owns Windows capture/OCR integration and provider adapters. |
| `app-and-docs` | `src/Translator.App.WinUI/`, `README.md`, `docs/`, `CONTRIBUTING.md`, and documentation/configuration updates. |
| `main` | Integration only. Do not use it for direct feature development; integrate completed branch work there and resolve cross-layer integration changes deliberately. |

When a change crosses ownership boundaries, coordinate the affected branch owners and
keep the dependency direction `App -> Windows/Providers -> Core`. Do not move Windows,
network, OCR, model-download, or overlay concerns into Core.

## Change guidelines

- Preserve the capacity-one latest-value mailbox. Do not replace it with an unbounded
  queue.
- Preserve monotonically increasing generations and suppress results from superseded
  generations.
- Cancel superseded work, but continue tracking provider tasks until they really finish
  when enforcing a concurrency bound.
- Keep OCR and overlay bounds in physical-pixel coordinates.
- Use `ITextTranslator` and `ITranslationMemory` for Core extension points instead of a
  service locator or a speculative plugin system.
- Keep API keys runtime-only. Never commit an API key, endpoint credential, local secret,
  signing key, certificate, or generated package containing secrets.

## Focused verification

Run the focused test project that covers the code you changed:

```powershell
dotnet test .\tests\Translator.Core.Tests\Translator.Core.Tests.csproj --no-restore
dotnet test .\tests\Translator.Providers.OpenAICompatible.Tests\Translator.Providers.OpenAICompatible.Tests.csproj --no-restore
dotnet test .\tests\Translator.Windows.Tests\Translator.Windows.Tests.csproj --no-restore
```

Before integration, run the complete solution build and, for app or packaging changes,
the WinUI MSIX-producing app build:

```powershell
dotnet build .\ScreenTranslator.slnx --no-restore
dotnet build .\src\Translator.App.WinUI\Translator.App.WinUI.csproj `
  -c Release -p:Platform=x64 -p:PublishReadyToRun=false --no-restore
```

The app project is configured for single-project MSIX generation. Check package and
overlay behavior locally when changing capture, OCR, lifecycle, or UI presentation.
`tests/fixtures/dynamic-ocr-fixture.html` is the repeatable local fixture for animated
backgrounds, bounds jitter, replacement text, and transient empty OCR.

## Review checklist

- [ ] The change stays within the owning layer and preserves `App -> Windows/Providers -> Core`.
- [ ] Core remains free of network, WinRT/Windows interop, OCR provider, model-download,
      and overlay code.
- [ ] Latest-value, generation, cancellation, physical-pixel, and stale-result invariants
      still hold.
- [ ] Focused tests pass; solution build passes when integration is affected.
- [ ] Runtime secrets and package/signing artifacts are absent from the diff.
- [ ] Documentation describes only implemented behavior and existing local commands.
