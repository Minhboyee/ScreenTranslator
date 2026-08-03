# Core invariants

- Keep the pending-input mailbox capacity at one; new input replaces stale pending input.
- Every submission receives a monotonically increasing generation.
- A result may be published only while its generation is the current generation.
- Superseded translation work must receive cancellation; providers may still complete, but
  their stale result must never be published.
- Core has no network, WinRT, Windows interop, OCR provider, model download, or overlay code.
- OCR bounds use physical-pixel coordinates (`PhysicalPixelRect`), not logical/UI units.
- Keep extension points narrow: use `ITextTranslator` and `ITranslationMemory`; do not add a
  service locator or speculative plugin system.
- Do not replace the latest-value mailbox with an unbounded queue.
