using Translator.Core;
using Translator.Windows;
using System.Reflection;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Translator.Windows.Tests;

public sealed class WindowsPureTests
{
    [Theory]
    [InlineData("ja", "ja-JP")]
    [InlineData("ja-JP", "ja-JP")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh_Hant", "zh-Hant")]
    public void Maps_supported_ocr_languages(string input, string expected)
    {
        Assert.Equal(expected, OcrLanguageCatalog.Map(input));
    }

    [Fact]
    public void Rejects_unsupported_ocr_language()
    {
        Assert.Throws<ArgumentException>(() => OcrLanguageCatalog.Map("en-US"));
    }

    [Fact]
    public void Aggregates_words_into_physical_pixel_line_bounds()
    {
        var result = OcrDocumentMapper.MapLines(
            [
                new OcrLineSnapshot(
                    [
                        new OcrWordSnapshot("Hello", new PhysicalPixelRect(10, 20, 30, 12)),
                        new OcrWordSnapshot("world", new PhysicalPixelRect(45, 18, 25, 20))
                    ])
            ]);

        var line = Assert.Single(result.Text);
        Assert.Equal("Hello world", line.Text.Value);
        Assert.Equal(new PhysicalPixelRect(10, 18, 60, 20), line.Bounds);
    }

    [Fact]
    public void Suppresses_exact_presentation_duplicates_but_publishes_changed_bounds()
    {
        var deduplicator = new OcrDocumentDeduplicator();
        var first = OcrDocumentMapper.MapLines(
            [new OcrLineSnapshot([new OcrWordSnapshot("Hello", new PhysicalPixelRect(1, 2, 3, 4))])]);
        var equivalent = OcrDocumentMapper.MapLines(
            [new OcrLineSnapshot([new OcrWordSnapshot(" Hello ", new PhysicalPixelRect(1, 2, 3, 4))])]);
        var moved = OcrDocumentMapper.MapLines(
            [new OcrLineSnapshot([new OcrWordSnapshot("Hello", new PhysicalPixelRect(2, 2, 3, 4))])]);

        Assert.True(deduplicator.ShouldPublish(first));
        Assert.False(deduplicator.ShouldPublish(equivalent));
        Assert.True(deduplicator.ShouldPublish(moved));
    }

    [Fact]
    public void Publishes_initial_and_distinct_subsequent_documents_but_suppresses_duplicate()
    {
        var deduplicator = new OcrDocumentDeduplicator();
        var alpha = OcrDocumentMapper.MapLines(
            [new OcrLineSnapshot([new OcrWordSnapshot("REGION TEST ALPHA", new PhysicalPixelRect(1, 2, 100, 20))])]);
        var beta = OcrDocumentMapper.MapLines(
            [new OcrLineSnapshot([new OcrWordSnapshot("REGION TEST BETA", new PhysicalPixelRect(1, 2, 100, 20))])]);

        Assert.True(deduplicator.ShouldPublish(alpha));
        Assert.False(deduplicator.ShouldPublish(alpha));
        Assert.True(deduplicator.ShouldPublish(beta));
        Assert.False(deduplicator.ShouldPublish(beta));
    }

    [Fact]
    public void Projects_crop_local_line_to_scaled_negative_desktop_coordinates()
    {
        var selection = new WindowCaptureSelection(
            123,
            new CaptureItemPixelSize(1000, 800),
            new ItemLocalCropRect(200, 100, 400, 200),
            new DesktopScreenSelectionRect(-1200, 300, 800, 400),
            epoch: 1);
        var line = new OcrText("line", new PhysicalPixelRect(50, 25, 100, 50));

        var projected = OcrLineOverlayProjector.ProjectToDesktop(line, selection);

        Assert.Equal(new PhysicalPixelRect(-1100, 350, 200, 100), projected);
    }

    [Fact]
    public void Projects_crop_origin_when_desktop_metadata_describes_the_full_capture()
    {
        var selection = new WindowCaptureSelection(
            123,
            new CaptureItemPixelSize(1000, 800),
            new ItemLocalCropRect(200, 100, 400, 200),
            new DesktopScreenSelectionRect(0, 0, 4000, 3200),
            epoch: 1);
        var line = new OcrText("line", new PhysicalPixelRect(50, 25, 100, 50));

        var projected = OcrLineOverlayProjector.ProjectFromCaptureDesktopBounds(
            line,
            selection,
            new DesktopScreenSelectionRect(-2000, 100, 2000, 1600));

        Assert.Equal(new PhysicalPixelRect(-1500, 350, 200, 100), projected);
    }

    [Fact]
    public void Projects_overlay_immediately_above_the_source_line()
    {
        var selection = new WindowCaptureSelection(
            123,
            new CaptureItemPixelSize(400, 200),
            new ItemLocalCropRect(20, 10, 200, 100),
            new DesktopScreenSelectionRect(1000, 500, 800, 400),
            epoch: 1);
        var line = new OcrText("line", new PhysicalPixelRect(25, 10, 50, 20));

        var placement = OcrLineOverlayProjector.ProjectAbove(line, selection, 240, 40, gap: 4);

        Assert.Equal(new PhysicalPixelRect(1100, 540, 200, 80), placement.SourceBounds);
        Assert.Equal(new PhysicalPixelRect(1100, 496, 240, 40), placement.OverlayBounds);
    }

    [Theory]
    [InlineData(0, OverlayForegroundTone.Light)]
    [InlineData(0.1, OverlayForegroundTone.Light)]
    [InlineData(1, OverlayForegroundTone.Dark)]
    public void Selects_the_higher_contrast_overlay_foreground(double luminance, OverlayForegroundTone expected)
    {
        var appearance = new OcrLineAppearanceHint(luminance);

        Assert.Equal(expected, OcrContrastSelector.Select(appearance));
        Assert.Equal(expected, appearance.PreferredForeground);
    }

    [Fact]
    public void Samples_relative_luminance_from_a_small_bgra_grid()
    {
        var pixels = new byte[]
        {
            255, 255, 255, 255,
            0, 0, 0, 255,
            0, 0, 0, 255,
            255, 255, 255, 255
        };

        var luminance = OcrLineAppearanceSampler.SampleBgra8(
            pixels,
            pixelWidth: 2,
            pixelHeight: 2,
            stride: 8,
            new PhysicalPixelRect(0, 0, 2, 2));

        Assert.Equal(0.5, luminance, precision: 6);
    }

    [Fact]
    public void Publishes_appearance_updates_for_unchanged_ocr_and_keeps_changed_line_hints()
    {
        var selection = new WindowCaptureSelection(
            123,
            new CaptureItemPixelSize(400, 200),
            new ItemLocalCropRect(20, 10, 200, 100),
            new DesktopScreenSelectionRect(1000, 500, 800, 400),
            epoch: 1);
        var deduplicator = new OcrDocumentDeduplicator();
        var first = new OcrResult(
            [new OcrText(
                "same",
                new PhysicalPixelRect(25, 10, 50, 20),
                new OcrLineAppearanceHint(0.1))]);
        var backgroundChanged = new OcrResult(
            [new OcrText(
                "same",
                new PhysicalPixelRect(25, 10, 50, 20),
                new OcrLineAppearanceHint(0.9))]);
        var changed = new OcrResult(
            [new OcrText(
                "changed",
                new PhysicalPixelRect(75, 20, 60, 20),
                new OcrLineAppearanceHint(0.9))]);

        Assert.True(deduplicator.ShouldPublish(first));
        Assert.True(deduplicator.ShouldPublish(backgroundChanged));
        Assert.True(deduplicator.ShouldPublish(changed));
        Assert.Equal(
            new PhysicalPixelRect(1300, 580, 240, 80),
            OcrLineOverlayProjector.ProjectToDesktop(changed.Text[0], selection));
        Assert.Equal(0.9, changed.Text[0].RelativeBackgroundLuminance);
    }

    [Theory]
    [InlineData(true, true, "Chrome_WidgetWin_1", "Chrome", true)]
    [InlineData(false, true, "Chrome_WidgetWin_1", "Chrome", false)]
    [InlineData(true, false, "Chrome_WidgetWin_1", "Chrome", false)]
    [InlineData(true, true, "OtherWindow", "Chrome", false)]
    [InlineData(true, true, "Chrome_WidgetWin_1", "", false)]
    public void Filters_visible_top_level_chrome_windows_without_reading_bounds(
        bool isVisible,
        bool isTopLevel,
        string windowClass,
        string title,
        bool expected)
    {
        var metadata = new ChromeWindowMetadata(123, windowClass, title, isVisible, isTopLevel);

        Assert.Equal(expected, ChromeWindowEnumerator.IsCandidate(metadata));
    }

    [Fact]
    public void Rounds_image_edges_outward_when_mapping_dips_to_item_pixels()
    {
        var crop = DipToItemPixelTransform.ToItemPixelCrop(
            new DipRect(1.1, 2.2, 10.1, 5.1),
            new DipSize(100, 50),
            new CaptureItemPixelSize(300, 100));

        Assert.Equal(new ItemLocalCropRect(3, 4, 31, 11), crop);
    }

    [Fact]
    public void Rejects_crop_outside_capture_item_bounds()
    {
        var itemSize = new CaptureItemPixelSize(100, 80);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CaptureCropContract.Validate(new ItemLocalCropRect(90, 10, 11, 20), itemSize));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            CaptureCropContract.Validate(new ItemLocalCropRect(-1, 10, 10, 20), itemSize));
    }

    [Fact]
    public void Converts_item_crop_to_bitmap_source_bounds()
    {
        var bounds = SoftwareBitmapCropContract.ToSourceBounds(
            new ItemLocalCropRect(11, 7, 23, 19),
            new CaptureItemPixelSize(100, 80));

        Assert.Equal(new BitmapSourceCropBounds(11, 7, 23, 19), bounds);
    }

    [Fact]
    public void Invalidates_selection_when_content_size_changes()
    {
        var selection = new WindowCaptureSelection(
            123,
            new CaptureItemPixelSize(100, 80),
            new ItemLocalCropRect(10, 8, 40, 20),
            new DesktopScreenSelectionRect(10, 20, 40, 20),
            epoch: 1);

        Assert.True(SoftwareBitmapCropContract.IsContentSizeCompatible(
            selection,
            new CaptureItemPixelSize(100, 80)));
        Assert.False(SoftwareBitmapCropContract.IsContentSizeCompatible(
            selection,
            new CaptureItemPixelSize(101, 80)));
    }

    [Fact]
    public void Validates_snapshot_metadata_without_constructing_a_gpu_bitmap()
    {
        var bounds = new DesktopScreenSelectionRect(1000, 200, 1000, 500);

        WindowCaptureSnapshotContract.ValidateMetadata(
            123,
            new CaptureItemPixelSize(200, 100),
            new CaptureItemPixelSize(200, 100),
            bounds);

        Assert.Throws<InvalidOperationException>(() =>
            WindowCaptureSnapshotContract.ValidateMetadata(
                123,
                new CaptureItemPixelSize(200, 100),
                new CaptureItemPixelSize(201, 100),
                bounds));
        Assert.Throws<ArgumentException>(() =>
            WindowCaptureSnapshotContract.ValidateWindowGeometry(0, bounds));
    }

    [Fact]
    public void Snapshot_failure_diagnostic_contains_stage_type_and_hresult()
    {
        var inner = new InvalidCastException("internal cast detail");
        var failure = new WindowCaptureSnapshotException("CreateGraphicsCaptureItem", inner);

        Assert.Equal("CreateGraphicsCaptureItem", failure.Stage);
        Assert.Equal(inner.HResult, failure.ErrorHResult);
        Assert.Contains("CreateGraphicsCaptureItem", failure.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidCastException), failure.Message, StringComparison.Ordinal);
        Assert.Contains($"0x{inner.HResult:X8}", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("internal cast detail", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Maps_image_dips_to_item_and_desktop_physical_edges()
    {
        var coordinates = CaptureCoordinateTransform.MapImageSelection(
            new DipRect(10, 5, 20, 10),
            new DipSize(100, 50),
            new CaptureItemPixelSize(200, 100),
            new DesktopScreenSelectionRect(1000, 200, 1000, 500));

        Assert.Equal(new ItemLocalCropRect(20, 10, 40, 20), coordinates.ItemLocalCrop);
        Assert.Equal(
            new DesktopScreenSelectionRect(1100, 250, 200, 100),
            coordinates.DesktopScreenSelection);
    }

    [Fact]
    public void Single_frame_guard_claims_only_one_frame_and_rejects_stale_completion()
    {
        var guard = new SingleFrameCaptureGuard();

        Assert.True(guard.TryClaimFrame());
        Assert.False(guard.TryClaimFrame());
        Assert.True(guard.HasClaimedFrame);
        Assert.True(guard.TryComplete());
        Assert.True(guard.IsTerminal);
        Assert.False(guard.TryComplete());
        Assert.False(guard.TryCancel());

        var cancelled = new SingleFrameCaptureGuard();
        Assert.True(cancelled.TryCancel());
        Assert.False(cancelled.TryClaimFrame());
    }

    [Fact]
    public void Places_card_below_selection_and_clamps_horizontally()
    {
        var placement = SingleCardPlacementCalculator.Place(
            new DesktopScreenSelectionRect(1900, 300, 100, 80),
            new DesktopWorkAreaRect(0, 0, 1920, 1080),
            CardSide.Below,
            preferredWidth: 400,
            preferredHeight: 200,
            maxVisibleHeight: 300);

        Assert.NotNull(placement);
        Assert.Equal(CardSide.Below, placement!.Side);
        Assert.Equal(new DesktopCardRect(1520, 388, 400, 200), placement.Bounds);
    }

    [Fact]
    public void Places_card_above_selection_without_overlap()
    {
        var placement = SingleCardPlacementCalculator.Place(
            new DesktopScreenSelectionRect(500, 500, 120, 80),
            new DesktopWorkAreaRect(0, 0, 1920, 1080),
            CardSide.Above,
            preferredWidth: 300,
            preferredHeight: 200,
            maxVisibleHeight: 300);

        Assert.NotNull(placement);
        Assert.Equal(new DesktopCardRect(500, 292, 300, 200), placement!.Bounds);
        Assert.True(placement.Bounds.Bottom <= 500);
    }

    [Fact]
    public void Caps_card_height_to_available_space()
    {
        var placement = SingleCardPlacementCalculator.Place(
            new DesktopScreenSelectionRect(100, 900, 200, 100),
            new DesktopWorkAreaRect(0, 0, 1920, 1080),
            CardSide.Below,
            preferredWidth: 300,
            preferredHeight: 500,
            maxVisibleHeight: 120);

        Assert.NotNull(placement);
        Assert.Equal(72, placement!.Bounds.Height);
        Assert.Equal(1008, placement.Bounds.Top);
    }

    [Fact]
    public void Hides_card_when_chosen_side_has_no_readable_space()
    {
        var placement = SingleCardPlacementCalculator.Place(
            new DesktopScreenSelectionRect(100, 1000, 200, 80),
            new DesktopWorkAreaRect(0, 0, 1920, 1080),
            CardSide.Below,
            preferredWidth: 300,
            preferredHeight: 200,
            maxVisibleHeight: 120);

        Assert.Null(placement);
    }

    [Fact]
    public void Rejects_stale_selection_epochs_before_publication()
    {
        var epochs = new SelectionEpochGate();
        var first = epochs.BeginSelection();
        var current = epochs.BeginSelection();
        var publishedEpochs = new List<long>();

        if (epochs.IsCurrent(first))
        {
            publishedEpochs.Add(first);
        }

        if (epochs.IsCurrent(current))
        {
            publishedEpochs.Add(current);
        }

        Assert.False(epochs.IsCurrent(first));
        Assert.True(epochs.IsCurrent(current));
        Assert.Equal([current], publishedEpochs);
    }

    [Fact]
    public void Placement_state_retains_requested_side_across_updates_and_hides()
    {
        var state = new TranslationCardPlacementState(
            CardSide.Above,
            preferredWidth: 300,
            preferredHeight: 180,
            maxVisibleHeight: 220);

        var placement = state.Update(
            new DesktopScreenSelectionRect(500, 500, 100, 80),
            new DesktopWorkAreaRect(0, 0, 1920, 1080));
        Assert.NotNull(placement);
        Assert.Equal(CardSide.Above, placement!.Side);
        Assert.Same(placement, state.CurrentPlacement);

        state.Hide();

        Assert.Equal(CardSide.Above, state.Side);
        Assert.Null(state.CurrentPlacement);
    }

    [Fact]
    public void Placement_state_preserves_non_overlap_on_the_selected_side()
    {
        var state = new TranslationCardPlacementState(
            CardSide.Below,
            preferredWidth: 300,
            preferredHeight: 180,
            maxVisibleHeight: 220);
        var selection = new DesktopScreenSelectionRect(400, 200, 160, 90);

        var placement = state.Update(selection, new DesktopWorkAreaRect(0, 0, 1200, 900));

        Assert.NotNull(placement);
        Assert.True(placement!.Bounds.Top >= selection.Bottom);
        Assert.Equal(CardSide.Below, placement.Side);
    }

    [Fact]
    public void Composes_non_activating_owned_card_interop_request_without_topmost()
    {
        var placement = new SingleCardPlacement(
            CardSide.Below,
            new DesktopCardRect(10, 20, 300, 120));

        var request = TranslationCardWindowInterop.ComposeRequest(123, placement);

        Assert.Equal((nint)123, request.OwnerWindowHandle);
        Assert.Equal(TranslationCardWindowInterop.RequiredExtendedStyles, request.RequiredExtendedStyles);
        Assert.True((request.PositionFlags & TranslationCardWindowInterop.SwpNoActivate) != 0);
        Assert.True((request.PositionFlags & TranslationCardWindowInterop.SwpNoZOrder) != 0);
        Assert.Equal(placement.Bounds, request.Bounds);
        Assert.Equal(TranslationCardWindowInterop.HtTransparent,
            TranslationCardWindowInterop.HandleMessage(TranslationCardWindowInterop.WmNcHitTest));
        Assert.Equal(TranslationCardWindowInterop.MaNoActivate,
            TranslationCardWindowInterop.HandleMessage(TranslationCardWindowInterop.WmMouseActivate));
    }

    [Fact]
    public void Composes_hide_request_without_activating_or_changing_z_order()
    {
        var request = TranslationCardWindowInterop.ComposeRequest(123, null);

        Assert.Null(request.Bounds);
        Assert.True((request.PositionFlags & TranslationCardWindowInterop.SwpHideWindow) != 0);
        Assert.True((request.PositionFlags & TranslationCardWindowInterop.SwpNoMove) != 0);
        Assert.True((request.PositionFlags & TranslationCardWindowInterop.SwpNoSize) != 0);
        Assert.True((request.PositionFlags & TranslationCardWindowInterop.SwpNoActivate) != 0);
        Assert.True((request.PositionFlags & TranslationCardWindowInterop.SwpNoZOrder) != 0);
    }

    [Fact]
    public void Copies_bgra_crop_rows_without_reencoding_or_changing_pixels()
    {
        const int sourceWidth = 4;
        const int sourceHeight = 3;
        const int sourceStride = 20;
        const int destinationStride = 12;
        var source = Enumerable.Range(0, sourceStride * sourceHeight)
            .Select(value => (byte)value)
            .ToArray();
        var destination = Enumerable.Repeat((byte)0xee, destinationStride * 2).ToArray();

        SoftwareBitmapCropper.CopyBgra8(
            source,
            sourceWidth,
            sourceHeight,
            sourceStride,
            new ItemLocalCropRect(1, 1, 2, 2),
            destination,
            destinationStride);

        Assert.Equal(
            [
                24, 25, 26, 27, 28, 29, 30, 31, 0xee, 0xee, 0xee, 0xee,
                44, 45, 46, 47, 48, 49, 50, 51, 0xee, 0xee, 0xee, 0xee
            ],
            destination);
    }

    [Fact]
    public async Task Crops_a_software_bitmap_with_the_same_direct_bgra_pixels()
    {
        var sourcePixels = Enumerable.Range(0, 4 * 3 * 4)
            .Select(value => (byte)value)
            .ToArray();
        using var source = new SoftwareBitmap(
            BitmapPixelFormat.Bgra8,
            4,
            3,
            BitmapAlphaMode.Premultiplied);
        using (var writer = new DataWriter())
        {
            writer.WriteBytes(sourcePixels);
            source.CopyFromBuffer(writer.DetachBuffer());
        }

        using var cropped = await SoftwareBitmapCropper.CropAsync(
            source,
            new ItemLocalCropRect(1, 1, 2, 2));
        var outputBuffer = new global::Windows.Storage.Streams.Buffer(2 * 2 * 4)
        {
            Length = 2 * 2 * 4
        };
        cropped.CopyToBuffer(outputBuffer);
        using var reader = DataReader.FromBuffer(outputBuffer);
        var output = new byte[2 * 2 * 4];
        reader.ReadBytes(output);

        Assert.Equal(
            [
                20, 21, 22, 23, 24, 25, 26, 27,
                36, 37, 38, 39, 40, 41, 42, 43
            ],
            output);
    }

    [Fact]
    public void Stability_emits_same_content_presentation_updates_without_content_settling()
    {
        var selector = new OcrDocumentStabilitySelector();
        var first = Document(
            new OcrText("alpha", new PhysicalPixelRect(1, 2, 10, 10)),
            new OcrText("alpha", new PhysicalPixelRect(20, 2, 10, 10)),
            new OcrText("beta", new PhysicalPixelRect(40, 2, 10, 10)));
        var jitter = Document(
            new OcrText(" beta ", new PhysicalPixelRect(41, 3, 11, 9), new OcrLineAppearanceHint(0.8)),
            new OcrText("alpha", new PhysicalPixelRect(21, 3, 11, 9)),
            new OcrText("alpha", new PhysicalPixelRect(2, 3, 11, 9)));

        Assert.Same(first, selector.Observe(first, At(0)));
        Assert.Same(jitter, selector.Observe(jitter, At(50)));
        Assert.Null(selector.Observe(jitter, At(60)));
    }

    [Fact]
    public void Stability_rejects_A_B_A_and_accepts_A_B_B()
    {
        var selector = new OcrDocumentStabilitySelector();
        var alpha = Document(new OcrText("A", new PhysicalPixelRect(1, 1, 10, 10)));
        var beta = Document(new OcrText("B", new PhysicalPixelRect(2, 2, 11, 11)));

        Assert.Same(alpha, selector.Observe(alpha, At(0)));
        Assert.Null(selector.Observe(beta, At(10)));
        Assert.Null(selector.Observe(alpha, At(20)));
        Assert.Null(selector.Observe(beta, At(30)));
        var accepted = selector.Observe(
            Document(new OcrText("B", new PhysicalPixelRect(30, 30, 12, 12))),
            At(40));

        Assert.NotNull(accepted);
        Assert.Equal(new PhysicalPixelRect(30, 30, 12, 12), accepted!.Text[0].Bounds);
    }

    [Fact]
    public void Stability_publishes_latest_content_at_the_bounded_settle_deadline()
    {
        var selector = new OcrDocumentStabilitySelector();
        var alpha = Document(new OcrText("A", new PhysicalPixelRect(1, 1, 10, 10)));
        var beta = Document(new OcrText("B", new PhysicalPixelRect(2, 2, 10, 10)));
        var gamma = Document(new OcrText("C", new PhysicalPixelRect(3, 3, 10, 10)));

        Assert.Same(alpha, selector.Observe(alpha, At(0)));
        Assert.Null(selector.Observe(beta, At(10)));
        Assert.Null(selector.Observe(gamma, At(100)));
        var settled = selector.Observe(
            Document(new OcrText("D", new PhysicalPixelRect(40, 50, 12, 14))),
            At(240));

        Assert.NotNull(settled);
        Assert.Equal("D", settled!.Text[0].Text.Value);
        Assert.Equal(new PhysicalPixelRect(40, 50, 12, 14), settled.Text[0].Bounds);
    }

    [Fact]
    public void Stability_uses_empty_grace_before_clear_and_then_recovers_immediately()
    {
        var selector = new OcrDocumentStabilitySelector();
        var alpha = Document(new OcrText("A", new PhysicalPixelRect(1, 1, 10, 10)));
        var empty = new OcrResult([]);

        Assert.Same(alpha, selector.Observe(alpha, At(0)));
        Assert.Null(selector.Observe(empty, At(100)));
        Assert.Null(selector.Observe(empty, At(699)));
        Assert.Same(empty, selector.Observe(empty, At(700)));
        Assert.Null(selector.Observe(empty, At(701)));
        var recovered = Document(new OcrText("B", new PhysicalPixelRect(2, 2, 10, 10)));
        Assert.Same(recovered, selector.Observe(recovered, At(702)));
    }

    [Fact]
    public async Task Scheduler_replaces_and_disposes_only_the_old_pending_frame()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentEpoch = 1L;
        var scheduler = new LatestOcrFrameScheduler<TestFrame>(
            async (frame, _) =>
            {
                if (frame.Name == "first")
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
                else
                {
                    secondStarted.SetResult();
                }
            },
            epoch => epoch == currentEpoch,
            TimeSpan.Zero);
        var at = At(0);
        var first = new TestFrame("first");
        var oldPending = new TestFrame("old");
        var newest = new TestFrame("newest");

        Assert.True(scheduler.Submit(first, 1, at));
        await firstStarted.Task;
        Assert.True(scheduler.Submit(oldPending, 1, At(1)));
        Assert.True(scheduler.Submit(newest, 1, At(2)));
        Assert.Equal(1, oldPending.DisposeCount);
        Assert.Equal(0, newest.DisposeCount);

        releaseFirst.SetResult();
        await secondStarted.Task;
        await scheduler.DisposeAsync();
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, newest.DisposeCount);
    }

    [Fact]
    public async Task Scheduler_submits_during_worker_retirement_and_starts_a_new_worker()
    {
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSubmitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TestFrame("first");
        var second = new TestFrame("second");
        var scheduler = new LatestOcrFrameScheduler<TestFrame>(
            (frame, _) =>
            {
                if (frame.Name == "second")
                {
                    secondStarted.SetResult();
                }

                return Task.CompletedTask;
            },
            _ => true,
            TimeSpan.Zero);
        var retirementCount = 0;
        scheduler.WorkerRetirementProbe = () =>
        {
            if (Interlocked.Exchange(ref retirementCount, 1) == 0)
            {
                secondSubmitted.SetResult(scheduler.Submit(second, 1, At(1)));
            }
        };

        try
        {
            Assert.True(scheduler.Submit(first, 1, At(0)));
            Assert.True(await secondSubmitted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await scheduler.DisposeAsync();
        }

        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task Scheduler_retains_an_early_frame_until_the_sample_interval()
    {
        var now = At(0);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new LatestOcrFrameScheduler<TestFrame>(
            async (frame, _) =>
            {
                if (frame.Name == "first")
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
                else
                {
                    secondStarted.SetResult();
                }
            },
            _ => true,
            TimeSpan.FromMilliseconds(100),
            () => now);
        var first = new TestFrame("first");
        var early = new TestFrame("early");

        Assert.True(scheduler.Submit(first, 1, now));
        await firstStarted.Task;
        now = At(50);
        Assert.True(scheduler.Submit(early, 1, now));
        releaseFirst.SetResult();
        await Task.Delay(10);
        Assert.False(secondStarted.Task.IsCompleted);

        now = At(100);
        Assert.True(scheduler.StartEligible(now));
        await secondStarted.Task;
        await scheduler.DisposeAsync();
        Assert.Equal(1, early.DisposeCount);
    }

    [Fact]
    public async Task Scheduler_rejects_stale_epoch_frames_and_disposes_them()
    {
        var scheduler = new LatestOcrFrameScheduler<TestFrame>(
            (_, _) => Task.CompletedTask,
            epoch => epoch == 2,
            TimeSpan.Zero);
        var stale = new TestFrame("stale");

        Assert.False(scheduler.Submit(stale, 1, At(0)));
        Assert.Equal(1, stale.DisposeCount);
        await scheduler.DisposeAsync();
    }

    [Fact]
    public async Task Scheduler_shutdown_after_initial_start_waits_for_and_disposes_active_frame()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new LatestOcrFrameScheduler<TestFrame>(
            async (_, _) =>
            {
                started.SetResult();
                await release.Task;
            },
            _ => true,
            TimeSpan.Zero);
        var first = new TestFrame("first");

        Assert.True(scheduler.Submit(first, 1, At(0)));
        await started.Task;

        var shutdown = scheduler.DisposeAsync().AsTask();

        Assert.False(shutdown.IsCompleted);
        Assert.Equal(0, first.DisposeCount);
        release.SetResult();
        await shutdown;
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task Scheduler_shutdown_after_a_to_b_handoff_disposes_pending_newest_frame()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scheduler = new LatestOcrFrameScheduler<TestFrame>(
            async (frame, _) =>
            {
                if (frame.Name == "first")
                {
                    firstStarted.SetResult();
                    await releaseFirst.Task;
                }
            },
            _ => true,
            TimeSpan.Zero);
        var first = new TestFrame("first");
        var second = new TestFrame("second");

        Assert.True(scheduler.Submit(first, 1, At(0)));
        await firstStarted.Task;
        Assert.True(scheduler.Submit(second, 1, At(1)));

        var shutdown = scheduler.DisposeAsync().AsTask();

        Assert.Equal(1, second.DisposeCount);
        Assert.False(shutdown.IsCompleted);
        releaseFirst.SetResult();
        await shutdown;
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task Controller_concurrent_stop_and_dispose_join_the_same_startup_drain()
    {
        var controller = new WindowsCaptureOcrController();
        var startup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var startupCancellation = new CancellationTokenSource();
        SetPrivateField(controller, "starting", true);
        SetPrivateField(controller, "startupCancellation", startupCancellation);
        SetPrivateField(controller, "startupCompletion", startup);

        var stop = controller.StopAsync();
        var dispose = controller.DisposeAsync().AsTask();

        Assert.False(stop.IsCompleted);
        Assert.False(dispose.IsCompleted);
        startup.SetResult();
        await Task.WhenAll(stop, dispose);
        startupCancellation.Dispose();
    }

    private static void SetPrivateField<T>(object instance, string name, T value)
    {
        var field = instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private static OcrResult Document(params OcrText[] lines) => new(lines);

    private static DateTimeOffset At(int milliseconds) =>
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero).AddMilliseconds(milliseconds);

    private sealed class TestFrame(string name) : IDisposable
    {
        public string Name { get; } = name;

        public int DisposeCount { get; private set; }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
