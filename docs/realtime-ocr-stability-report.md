# Báo cáo kỹ thuật: ổn định OCR thời gian thực và độ trễ overlay

## 1. Phạm vi và kết luận ngắn

Báo cáo này mô tả **trạng thái triển khai hiện tại** của ScreenTranslator, không phải một
thiết kế giả định. Các tên lớp, symbol và đường dẫn bên dưới được đối chiếu với source trong
repository; các số đo chấp nhận được ghi lại trong `.slim/deepwork/realtime-ocr-stability.md`.

Mục tiêu của thay đổi là làm cho đường đi từ ảnh chụp đến bản dịch hữu ích có tính “latest
value” thay vì tính “xử lý mọi sự kiện”, đồng thời không để hoạt ảnh nền hoặc jitter của OCR
hủy công việc dịch còn có giá trị. Kết quả chính:

- WGC dùng callback nhẹ, một handoff frame mới nhất và OCR tuần tự; không tạo hàng đợi vô hạn.
- Crop vùng OCR đã bỏ round-trip PNG, chuyển sang copy trực tiếp các hàng BGRA8.
- Bộ chọn ổn định tách identity của nội dung khỏi identity của cách trình bày.
- Điều phối dịch theo identity của dòng, giữ bản dịch của dòng không đổi và giới hạn còn tối
  đa 3 provider call thực sự trong một phiên.
- MainPage truyền các snapshot bất biến qua các slot một phần tử và chỉ áp dụng overlay mới nhất.
- Khi nội dung mới đã được chấp nhận, overlay cũ bị thay thế toàn bộ; khi OCR rỗng nhất thời,
  overlay cũ được giữ trong thời gian grace có chủ đích.

Các contract lõi trong `src/Translator.Core/` vẫn giữ nguyên, đặc biệt
`TranslationSession` và `LatestValueMailbox<T>`; coordinator stateful được dùng ở lớp WinUI.

## 2. Vấn đề ban đầu

### 2.1. Độ trễ và starvation

Đường capture/OCR cũ chỉ cho phép một frame OCR đang chạy. Khi frame đến trong lúc OCR bận,
frame mới bị bỏ; việc sampling bị claim trước khi công việc chậm hoàn tất. Một frame hữu ích
có thể vì vậy không bao giờ đến được OCR sau một chuỗi frame không phù hợp.

Ngoài ra, crop cũ phải đi qua encode/decode PNG. Đây là latency cố định không cần thiết trên
đường realtime, nhất là với một ROI lớn.

### 2.2. Hoạt ảnh không phải văn bản gây OCR churn

Một vùng game/web có thể giữ nguyên chữ nhưng thay đổi nền, shader, particle hoặc hiệu ứng.
Bounds OCR cũng có thể dao động một pixel. Nếu identity của document phụ thuộc vào bounds,
mỗi dao động tạo ra một document mới dù nội dung ngữ nghĩa không đổi.

Hệ quả trong baseline được ghi nhận là MainPage hủy toàn bộ line translation khi mỗi document
mới được publish. Các dòng không đổi cũng bị hủy và khởi động lại. Với provider chậm hoặc
provider không tuân thủ cancellation, các lần hủy lặp lại tạo ra churn và làm chậm hoặc làm
starve bản dịch ổn định.

### 2.3. Overlay cũ và kết quả đến muộn

Nếu một kết quả cũ đến sau khi nội dung đã thay đổi, nó không được phép thay thế trạng thái
mới. Ngược lại, khi nội dung mới đang pending/error, giữ nguyên overlay cũ sẽ hiển thị câu
không còn tương ứng với vùng chữ hiện tại.

Hai yêu cầu tưởng như mâu thuẫn được tách ra như sau:

1. **Trong lúc chỉ có jitter hoặc cùng identity nội dung:** giữ thành công trước đó.
2. **Sau khi identity nội dung mới được selector chấp nhận:** áp dụng snapshot đầy đủ, kể cả
   snapshot chưa có dòng thành công; overlay cũ phải biến mất.

## 3. Luồng dữ liệu trước và sau

### 3.1. Baseline

Đây là mô hình của đường đi trước remediation, được đối chiếu với các ghi chú baseline trong
`.slim/deepwork/realtime-ocr-stability.md`:

```text
WGC FrameArrived
      |
      +--> OCR đang bận? -- yes --> bỏ frame mới
      |
      +--> copy/crop qua PNG encode/decode
      |
      +--> OCR
      |
      +--> dedupe nhạy với text + bounds
      |
      +--> MainPage hủy toàn bộ translation của document trước
      |
      +--> provider calls lại cho các dòng cũ lẫn dòng mới
      |
      +--> kết quả từng dòng --> rebuild overlay đồng bộ nhiều lần
```

Điểm yếu của mô hình này là arrival rate của frame không được tách khỏi tốc độ OCR/provider;
hoạt ảnh không phải text có thể liên tục tạo lý do để hủy và khởi động lại work.

### 3.2. Luồng hiện tại

```text
Direct3D11CaptureFramePool (FreeThreaded, 2 WGC buffers)
      |
      +--> OnFrameArrived (callback nhẹ, chỉ nhận frame)
      |
      +--> LatestCaptureFramePump
      |       pending raw capture frame = 1
      |       frame cũ bị thay thế --> Dispose
      |
      +--> CopyFrameAsync
      |       CreateCopyFromSurfaceAsync --> SoftwareBitmap BGRA8
      |       SoftwareBitmapCropper.CopyBgra8 --> ROI BGRA8
      |
      +--> LatestOcrFrameScheduler<SoftwareBitmap>
      |       active OCR = 1, pending bitmap = 1
      |       sampling interval tối thiểu = 100 ms
      |
      +--> Windows OCR --> OcrDocumentMapper.MapLines
      |
      +--> OcrLineAppearanceSampler.AttachHints
      |       (appearance chỉ là presentation hint)
      |
      +--> OcrDocumentStabilitySelector
      |       content identity / presentation identity
      |
      +--> WindowsCaptureOcrController.OcrResultPublished
      |
      +--> MainPage.pendingOcrDocument (latest slot = 1)
      |       immutable OcrDocumentHandoff + dispatcher coalescing
      |
      +--> BoundedLineTranslationCoordinator
      |       line identity, cache, active calls <= 3
      |
      +--> MainPage.pendingPresentation (latest slot = 1)
      |       immutable snapshot + (generation, revision) check
      |
      +--> ApplyPresentation
              full current snapshot
              --> UpdateLines hoặc Clear/Hide overlay
```

Hai buffer của WGC frame pool không phải là hàng đợi nghiệp vụ. Các handoff trong ứng dụng đều
có capacity một; frame mới nhất thay thế frame đang chờ, không tích lũy backlog.

## 4. Vòng đời WGC và crop BGRA trực tiếp

### 4.1. Khởi tạo và nhận frame

`WindowsCaptureOcrController.StartAsync` và `StartForWindowAsync`:

- tạo `GraphicsCaptureItem`;
- lấy `CanvasDevice.GetSharedDevice()` và `IDirect3DDevice`;
- tạo `Direct3D11CaptureFramePool.CreateFreeThreaded` với
  `DirectXPixelFormat.B8G8R8A8UIntNormalized` và `FramePoolBufferCount = 2`;
- tạo session, đăng ký `FrameArrived` và bắt đầu capture;
- tạo `LatestCaptureFramePump` và `LatestOcrFrameScheduler<SoftwareBitmap>` cho epoch mới.

`OnFrameArrived` không chạy OCR và không encode ảnh. Nó gọi `TryGetNextFrame`, lấy epoch hiện
tại rồi giao ownership của `Direct3D11CaptureFrame` cho `LatestCaptureFramePump.Submit`.
Pump chỉ giữ một `PendingCaptureFrame`; nếu frame mới thay thế frame đang chờ,
`replaced.Frame.Dispose()` được gọi ngay.

Worker của pump lấy frame đang chờ, gọi `CopyFrameAsync`, rồi luôn dispose raw WGC frame trong
`finally`. Nếu epoch đã hết hạn hoặc copy lỗi, bitmap kết quả bị bỏ/dispose và không đi tiếp.

### 4.2. Copy surface và crop

`CopyFrameAsync` trước hết gọi:

```text
SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)
```

Với bounded selection, kích thước `ContentSize` của frame và kích thước `SoftwareBitmap` được
đối chiếu với `WindowCaptureSelection`. Mismatch tạo
`WindowCaptureSelectionInvalidation`, vì crop cũ không còn an toàn.

`SoftwareBitmapCropper.CropAsync` hiện thực crop như sau:

1. yêu cầu `BitmapPixelFormat.Bgra8`;
2. dùng `CopyToBuffer` để đọc pixel nguồn vào buffer byte BGRA;
3. `CopyBgra8` copy từng row từ offset `(crop.Top + row) * sourceStride + crop.Left * 4`;
4. tạo `SoftwareBitmap(Bgra8, crop.Width, crop.Height, Premultiplied)`;
5. dùng `CopyFromBuffer` ghi các row đã crop vào bitmap đích.

`sourceStride` và `destinationStride` được giữ riêng; phần padding cuối row không bị nhầm là
pixel ROI. Các contract validate source bounds, stride, kích thước buffer và cancellation.
Không còn PNG encoder, PNG stream, decode bitmap hoặc round-trip nén trên hot path. Test
`Copies_bgra_crop_rows_without_reencoding_or_changing_pixels` và
`Crops_a_software_bitmap_with_the_same_direct_bgra_pixels` kiểm tra cả byte row lẫn
`SoftwareBitmap` thực tế.

## 5. Scheduler frame mới nhất: ownership, interval và epoch

### 5.1. Ownership và trạng thái

`LatestOcrFrameScheduler<TFrame>` có các trạng thái logic sau:

| Trạng thái | Owner | Quy tắc |
|---|---|---|
| `pendingFrame` | scheduler dưới `gate` | chỉ giữ frame mới nhất; frame bị thay thế được dispose tại `Submit` |
| active frame | worker loop, sau khi lấy khỏi mailbox | không bị frame mới hủy; được dispose trong `finally` của worker |
| worker task | scheduler | duy nhất một worker; xử lý serial rồi quay lại lấy pending |
| shutdown pending | `DisposeAsync` | lấy khỏi slot, dispose trước; sau đó await worker |

Do `TFrame : IDisposable`, ownership không bị bỏ ngỏ giữa các handoff. Trong controller,
`SoftwareBitmap` chỉ được scheduler submit khi còn đúng epoch; nếu scheduler từ chối hoặc
stop đã xảy ra, bitmap được dispose.

### 5.2. Sampling và latest-value policy

`WindowsCaptureOcrController.MinimumSampleInterval` là **100 ms**. Scheduler ghi
`hasStarted` và `lastStart`; nếu frame pending đến quá sớm, nó giữ frame mới nhất và tạo
`intervalWake`. Worker đợi phần thời gian còn lại hoặc wake/cancellation. Khi đến thời điểm,
worker lấy pending, cập nhật `lastStart`, rồi gọi `processAsync`.

Vì frame pending được thay thế trong thời gian chờ, OCR không chạy lại toàn bộ frame cũ. Vì
active frame không bị hủy bởi arrival mới, một OCR operation hiện tại luôn có cơ hội hoàn tất;
ngay sau đó worker xử lý latest bitmap còn lại. `StartEligible` tồn tại để đánh thức scheduler
trong các test/điều phối kiểm soát thời gian.

### 5.3. Stop/restart và retirement race

`SelectionEpochGate.BeginSelection()` tăng epoch bằng `Interlocked.Increment`; mỗi start,
stop hoặc invalidation làm các frame cũ trở nên stale. `isCurrentEpoch` được kiểm tra ở các
điểm copy, submit, bắt đầu process và trước publish. Do đó một OCR/copy hoàn tất muộn không thể
đẩy document của session trước vào session mới.

`LatestOcrFrameScheduler.DisposeAsync` là idempotent theo `shutdownTask`: đặt `stopped`, cancel
stop token, lấy/dispose pending, đánh thức interval wait và await worker. Worker chỉ hoàn tất
sau khi active frame đã rời `processAsync` và được dispose.

Một race quan trọng đã được sửa: worker có thể vừa thấy mailbox rỗng trong lúc producer submit
frame. Việc kiểm tra mailbox rỗng và xóa `workerTask` hiện nằm trong cùng `gate`. Producer sau
đó chỉ thấy một trong hai trạng thái hợp lệ: worker cũ vẫn tồn tại hoặc worker cũ đã retire và
`Submit` khởi động worker mới. Không có pending frame bị “stranded”. `WorkerRetirementProbe`
và test `Scheduler_submits_during_worker_retirement_and_starts_a_new_worker` kiểm tra đúng
điểm này. `intervalWake` cũng được dọn dưới cùng gate.

## 6. OCR stability selector

### 6.1. Mapping và hai loại identity

`OcrDocumentMapper.MapLines` giữ text OCR đã trim, ghép word bằng khoảng trắng và tính bounding
box physical-pixel bao quanh các word. Bounds raw không bị làm tròn lại cho mục đích ổn định.

`OcrDocumentDeduplicator.Normalize` cung cấp content identity:

```text
content = sort(
    TextNormalization.Normalize(line.Text.Value)
    cho từng line
)
```

Các line được sort và nối bằng delimiter, nhưng **không loại duplicate**. Đây là normalized
line-text multiset: thứ tự OCR thay đổi không làm đổi identity, còn số lần xuất hiện vẫn được
giữ lại. `TextNormalization` normalize Unicode và gom mọi whitespace thành một space.

`NormalizePresentation` mở rộng identity bằng:

- normalized text;
- `Left`, `Top`, `Width`, `Height` của physical bounds;
- `RelativeBackgroundLuminance` nếu có appearance hint.

Vì vậy:

- **content identity giống nhau, presentation identity khác:** text ổn định nhưng vị trí,
  kích thước hoặc nền thay đổi; được publish như presentation update, không restart translation;
- **cả hai giống nhau:** document bị suppress;
- **content identity khác:** phải qua settle policy.

`OcrLineAppearanceSampler` đọc một grid tối đa `8 x 8` từ crop và tính relative luminance.
Nếu sampling lỗi, controller fallback về document OCR không có hint; lỗi appearance không được
phép làm mất text hợp lệ.

### 6.2. Quy tắc settle/deadline

Các hằng số trong `OcrDocumentStabilitySelector` là:

- `ChangedContentSettleWindow = 225 ms`;
- `EmptyGracePeriod = 600 ms`.

Quy tắc cụ thể:

1. **Non-empty đầu tiên:** publish ngay. Không chờ hai mẫu cho lần xuất hiện đầu tiên.
2. **Cùng content đã publish:** xóa pending content change. Nếu presentation cũng giống thì
   suppress; nếu bounds/appearance khác thì publish document mới nhất ngay.
3. **Content mới:** đặt `pendingContentIdentity`, `pendingDocument`, `pendingSince` và
   `pendingMatches = 1`.
4. **Mẫu kế tiếp trùng pending content:** tăng match và giữ document mới nhất. Khi có hai mẫu
   trùng, publish ngay.
5. **A-B-A:** khi trở lại content đang publish, pending B bị hủy; không publish B.
6. **Nhiều content dao động:** content mới thay thế pending cũ. Nếu chưa có hai match, mẫu mới
   nhất được chờ đến khi đạt deadline.
7. **Deadline:** nếu đã qua 225 ms kể từ `pendingSince`, publish document pending/latest dù
   chưa có hai mẫu giống nhau. Điều này tránh starvation khi OCR luôn dao động.
8. **OCR empty:** bắt đầu `emptySince`, nhưng không clear trước 600 ms. Ở mốc grace, publish
   document rỗng một lần và đánh dấu `hasPublishedClear`; empty lặp lại sau đó bị suppress.
9. **Text trở lại sau clear:** non-empty được publish ngay, không phải settle từ đầu.

Bounds jitter và appearance jitter do đó không tạo provider work mới. Chúng vẫn có thể tạo
presentation update để overlay bám theo vị trí/tương phản mới; đó là chủ ý khác với content
churn.

### 6.3. Điều selector cố ý không làm

Selector hiện **không** thực hiện:

- text-mask hoặc tile-level pixel gating trước OCR;
- fuzzy text matching, edit distance hoặc semantic matching;
- tracking không gian để gán line qua các frame;
- phân loại hoạt ảnh nền so với hoạt ảnh chữ;
- hủy active OCR chỉ vì có frame mới.

Các mục này là hướng mở rộng. Chính sách hiện tại ưu tiên deterministic, pure selector và
để coordinator xử lý việc giữ translation.

## 7. Stateful line translation coordinator

### 7.1. Identity của translation và identity của occurrence

`BoundedLineTranslationCoordinator` nhận `LineTranslationRequest`, nhưng cố ý không dùng
`LineId` làm identity. Hai identity tách biệt:

```text
TranslationIdentity = TranslationRequest.MemoryKey
                   = normalized source text
                     + LanguagePair
                     + ProviderRevision

OccurrenceId       = normalized source text + delimiter + ordinal xuất hiện
```

`TranslationIdentity` quyết định có cần provider call hay không. `OccurrenceId` chỉ quyết định
cách biểu diễn một dòng cụ thể trong ordered snapshot. Vì vậy hai dòng duplicate có occurrence
khác nhau nhưng chia sẻ một translation call và một kết quả.

### 7.2. Reconcile, cache và giới hạn 3 call

`MainPage` khởi tạo coordinator với `maxConcurrency: 3`. Khi `Reconcile` nhận document mới:

1. tạo `currentLines` theo thứ tự OCR;
2. tạo `desiredKeys` theo translation identity, tự loại call trùng;
3. hủy các active call không còn trong desired set;
4. bỏ outcomes của identity đã bị loại;
5. nạp kết quả từ `ITranslationMemory` nếu có;
6. enqueue key chưa có cache/outcome/active call;
7. reserve call khi `activeCalls.Count < 3`;
8. compose một `LinePresentationSnapshot` bất biến.

`TranslationMemoryCache` là cache LRU thread-safe, capacity mặc định 256. Key gồm normalized
text, language pair và provider revision; đổi model/revision sẽ không dùng nhầm bản dịch cũ.
Translation thành công được lưu và document quay lại sau đó có thể hoàn tất từ cache mà không
gọi provider.

“Global cap 3” ở đây là cap của toàn bộ coordinator trong một phiên, không phải cap riêng cho
mỗi line. `activeCalls` bao gồm cả call đã bị request cancellation nhưng provider vẫn chưa
thực sự return.

### 7.3. Provider bỏ qua cancellation và stale suppression

`CancelRemovedCallsLocked` chỉ signal `CancellationTokenSource`; nó không xóa call khỏi
`activeCalls`. Call vẫn được theo dõi trong `runningWork` cho tới khi
`translator.TranslateAsync` kết thúc. Chỉ khi `CompleteCall` chạy, slot mới được giải phóng
và call kế tiếp mới được reserve. Vì vậy provider cố tình bỏ qua cancellation cũng không làm
đẩy số call thực tế vượt quá 3.

Khi call kết thúc, coordinator kiểm tra cả key và object `ActiveCall` hiện tại. Kết quả của call
đã bị thay thế không được mutate snapshot hiện tại. Nếu key không còn desired, translation có
thể được cache nhưng không được render cho document mới. Nếu key còn desired và call không bị
cancel, outcome là `Success`; lỗi trở thành `Error`; call bị cancel không tạo success stale.

`currentGeneration` loại bỏ `Reconcile` cũ hơn. Mỗi snapshot có thêm `presentationRevision`,
được tăng dưới `gate` trong `ComposeSnapshotLocked`. Cặp `(Generation, Revision)` là thứ tự
đầy đủ để xử lý trường hợp callback publication bị đảo thứ tự.

### 7.4. Snapshot và retention

`LinePresentationSnapshot` dùng `ReadOnlyCollection` và mỗi line có một trong các state:
`Pending`, `Success`, `Error`. Snapshot còn có `IsComplete`, `IsClear`, generation và revision.

Khi content mới được chấp nhận:

- success của identity vẫn còn trong `desiredKeys` được giữ, kể cả bounds đã đổi;
- identity đã biến mất không được giữ lại;
- dòng mới ở `Pending` hoặc `Error` vẫn nằm trong snapshot;
- snapshot đầy đủ được publish, kể cả khi số success bằng zero.

Đây là điểm bảo vệ overlay khỏi hiển thị câu cũ sau replacement. `TranslationSession` trong
`src/Translator.Core/TranslationSession.cs` vẫn duy trì latest mailbox, cancellation và
generation suppression độc lập; code UI hiện dùng coordinator theo dòng để đạt retention và
cap call nêu trên.

## 8. MainPage, dispatcher và overlay handoff

### 8.1. OCR handoff

`MainPage.OnOcrResultPublished` tăng `ocrDocumentGeneration`, lấy selection hiện tại và publish
`OcrDocumentHandoff` vào `pendingOcrDocument`, một `LatestValueHandoff<T>`. Dispatcher callback
được arm bằng `Interlocked.CompareExchange`, vì vậy nhiều OCR callback liên tiếp chỉ tạo một
callback UI đang chờ.

`DrainLatestOcrDocument` lấy một handoff mới nhất, gọi `HandleOcrDocument`, rồi hạ cờ armed và
re-arm nếu slot đã có value mới. `HandleOcrDocument` bỏ qua generation không còn mới nhất.
Đây là coalescing, không phải queueing: callback UI chậm không làm tích lũy hàng trăm document.

### 8.2. Presentation handoff

`OnPresentationPublished` dùng `presentationGate` để loại snapshot khác generation hoặc không
mới hơn `(generation, revision)`, sau đó publish snapshot vào `pendingPresentation`. Dispatcher
chỉ gọi `ApplyPresentation` nếu snapshot vẫn đúng latest version tại thời điểm drain.

Snapshot được tạo mới bằng `ReadOnlyCollection`; UI không sửa danh sách line mà chỉ đọc snapshot
và tạo payload overlay mới. Điều này loại data race giữa provider callback và UI thread.

### 8.3. Quy tắc replacement overlay

`ApplyPresentation`:

- `IsClear` thì `ClearOverlay`, xóa current presentation và hide surface;
- snapshot thường thì cập nhật panel state và lấy **toàn bộ** các line `Success` có text;
- gọi `SetOverlayLines` bằng tập success của snapshot hiện tại, không append incremental result.

`SetOverlayLines` so sánh sequence immutable trước khi cập nhật. Nếu tập rỗng, surface được
`Clear` và `Hide`; nếu không rỗng, surface nhận toàn bộ tập mới rồi `Show`.
`TranslationOverlayWindow.UpdateLines` cũng clear children trước khi layout lại các label,
đồng thời lọc line invalid. Vì vậy replacement pending/error không để lại label của document
trước; chỉ success cùng translation identity mới được giữ qua `Reconcile`.

`TranslatedOverlayLine` mang occurrence id, desktop physical bounds, text, appearance và font.
`OcrLineOverlayProjector` chuyển bounds local của crop thành desktop physical coordinates.
Overlay window là surface click-through và không activate cửa sổ nguồn.

### 8.4. Stop/restart single-flight

`MainPage.StopCoreAsync` dùng `sessionGate` và `stopDrainTask` dùng chung cho mọi caller. Caller
thứ hai của Stop/Unload/selection change join đúng drain đang chạy, không tạo drain thứ hai.
`CaptureSessionResourcesLocked` capture ownership của session cũ, tăng OCR generation, cancel
run token, unsubscribe event, detach controller/coordinator/overlay khỏi fields hiện tại, rồi
`DrainSessionAsync` lần lượt:

1. await startup nếu startup còn chạy;
2. dispose `WindowsCaptureOcrController`;
3. dispose coordinator và chờ provider task thực sự kết thúc;
4. dispose HTTP/cancellation resources;
5. clear, hide và dispose overlay surface.

Start bị từ chối nếu `startTask` hoặc `stopDrainTask` còn tồn tại. Cuối drain mới mở lại Start.
Do resource cũ được capture vào `StopSessionResources`, stop muộn không thể dispose nhầm vào
session mới.

`WindowsCaptureOcrController` cũng có `stopDrainTask` riêng; `StopAsync` và `DisposeAsync` join
cùng drain. Capture item đóng, content size thay đổi hoặc window selection invalid đều đi qua
epoch invalidation và stop path này.

## 9. Error behavior và các invariant

### 9.1. Error/lifecycle behavior

- WGC/copy/OCR lỗi trên một frame chỉ kết thúc frame đó; `LatestCaptureFramePump` và scheduler
  không bị chết theo frame lỗi.
- `ProcessFrameAsync` nuốt `OperationCanceledException` khi stop và cô lập lỗi transient.
- Lỗi đọc appearance hint fallback về OCR document; hint không được phép suppress text.
- Kích thước capture không tương thích phát `SelectionInvalidated` và dừng session để người dùng
  chọn lại vùng, thay vì crop ngoài bounds.
- Provider exception được chuyển thành `LinePresentationState.Error`; panel hiển thị
  `[Translation unavailable]`, còn overlay chỉ render success.
- HTTP provider có timeout 30 giây từ `MainPage.TranslationTimeout`; lỗi được đưa qua
  `ActionableError` ở UI khi phù hợp.
- `GraphicsCaptureItem.Closed` gọi stop. Startup failure dispose các resource đã tạo một phần.
- Một event subscriber lỗi không làm đổi state capture/coordinator; các event invoke được bọc
  bằng catch.
- Empty OCR ngắn hơn 600 ms không clear overlay; empty kéo dài đủ grace mới clear.

### 9.2. Invariant cần giữ

1. Mỗi handoff pending có capacity một; value mới thay value cũ.
2. Active OCR không bị cancel chỉ vì frame mới; pending cũ bị dispose khi bị thay.
3. Mỗi resource owner dispose đúng một lần; shutdown await active worker/provider work.
4. Frame/document của epoch cũ không được publish.
5. Snapshot cũ hơn theo `(generation, revision)` không được áp dụng lên UI.
6. Translation identity quyết định provider work; occurrence identity chỉ quyết định placement.
7. Trong một coordinator/session, provider call thực tế không vượt 3.
8. Bounds OCR và overlay dùng physical pixels; không trộn với logical UI units trong contract.
9. Thay đổi content đã settle phải thay thế full overlay state; chỉ unchanged successful identity
   mới được retention.
10. `TranslationSession` lõi vẫn dùng latest-value mailbox và không bị biến thành unbounded queue.

## 10. Test, fixture và số đo kiểm soát

### 10.1. Phạm vi test liên quan

Validation cuối được ghi nhận là **83 solution tests passed**; specialist validation là **17
focused tests** cho các thay đổi realtime/UI. Các nhóm test liên quan gồm:

- `tests/Translator.Windows.Tests/WindowsPureTests.cs`: physical bounds, crop contract và byte
  BGRA copy; appearance/luminance; normalized multiset; content settle, A-B-A/B-B, deadline,
  empty grace; epoch; scheduler replacement, disposal, interval, shutdown và retirement race;
  concurrent controller stop/dispose.
- `tests/Translator.Core.Tests/BoundedLineTranslationCoordinatorTests.cs`: bounds đổi nhưng
  translation giữ lại; duplicate sharing; removed line cancellation; provider bỏ qua
  cancellation; cap 3; reverse publication; changed pending/error; stop/restart; latest-value
  handoff và immutable snapshot.
- `tests/Translator.Core.Tests/CoreTests.cs`: mailbox replacement, cache key normalization và
  revision partitioning, stale generation, supersession cancellation và unchanged-text cache.
- `tests/Translator.Providers.OpenAICompatible.Tests/`: request/response và provider error
  behavior của OpenAI-compatible translator.

Các test scheduler có `TestFrame.DisposeCount`, nên kiểm tra được ownership chứ không chỉ kết
quả OCR. Test retirement cố ý submit đúng lúc worker đang rời đi để bắt regression stranded
pending frame.

### 10.2. Fixture động

`tests/fixtures/dynamic-ocr-fixture.html` là fixture local có thể replay, không phụ thuộc startup
của game ngoài:

- `.scene` dùng conic/radial gradient animated để thay đổi nền không phải text;
- dialogue giữ hai dòng tiếng Nhật cố định lúc bắt đầu;
- sau 3 giây, class `jitter` dịch vùng text ±1 pixel;
- sau 7 giây, text được thay bằng hai dòng tiếng Nhật khác;
- sau 10.5 giây, dialogue hidden để tạo OCR empty;
- sau 10.85 giây, dialogue hiện lại, tức empty chỉ kéo dài 350 ms;
- nút Reset làm sequence deterministic và status text giúp quan sát mốc.

Fixture kiểm tra đúng các property cần quan sát: nền đổi không làm dịch lại vô hạn, bounds
jitter không hủy translation, replacement không giữ overlay obsolete, empty ngắn hơn grace
không xóa overlay trước, và session không mắc kẹt ở `Translating lines…`.

### 10.3. Kết quả đo đã ghi nhận

Sau khi reset fixture trong controlled run:

- first stable translation xuất hiện ở **0.74 s**;
- replacement được render ở **7.6 s**, xấp xỉ **0.6 s sau** replacement được schedule ở 7 s;
- polling không quan sát thấy trạng thái `Translating lines…` tồn tại dai dẳng;
- hai English labels của fixture được render;
- replacement và transient-empty behavior đáp ứng acceptance criteria.

MSIX `.20` đã được build, sign, verify, install và launch trong validation được ghi nhận; Release
app build có zero errors và chỉ warning symbols hiện hữu. Acceptance dùng fixture local, không
dùng Tsukihime vì canvas title screen của game đó không cung cấp start action tự động đáng tin.

## 11. Mẫu tham khảo bên ngoài và ranh giới với code đã chép

Các project dưới đây được ghi nhận trong design/research log như **pattern inspiration**. Chúng
không phải dependency của solution này và không có source code nào được copy vào repository.
Code thực thi hiện tại chỉ nằm trong các file `src/Translator.*` đã dẫn ở trên; `.csproj` chỉ
tham chiếu project nội bộ và các package Windows/WinUI/OCR/provider cần thiết.

| Nguồn tham khảo | Pattern dùng để định hướng | Trạng thái trong code hiện tại |
|---|---|---|
| Microsoft PowerToys PowerOCR | Tách capture callback khỏi công việc nặng, copy frame trước khi resource WGC hết lifetime, và coi frame mới nhất là dữ liệu có giá trị hơn backlog | Đã áp dụng bằng `CreateFreeThreaded`, `LatestCaptureFramePump`, scheduler serial và ownership/disposal; không chép code PowerToys |
| Genshin-Subtitles | Xử lý subtitle liên tục trên bề mặt game động, ưu tiên text hữu ích mới nhất và tránh churn khi frame thay đổi | Đã phản ánh trong latest-frame policy, stability selector và line retention; không chép code |
| ScreenLens-Detection | Tách detection/change gating khỏi OCR/overlay và tránh coi toàn bộ pixel ROI là thay đổi text | Đã dùng làm hướng thiết kế; text-mask/tile gate vẫn là limitation, chưa có code external |
| RSTGameTranslation | Chu kỳ capture–OCR–dịch cho game, cache/reuse và giới hạn work cạnh tranh | Đã phản ánh trong coordinator stateful, translation cache và cap 3; không chép code |
| LingoLens | Giữ trạng thái bản dịch theo dòng và cập nhật presentation thay vì hủy mọi dòng khi scene đổi | Đã phản ánh trong translation identity/occurrence identity và full immutable snapshot; không chép code |

Điểm cần nhấn mạnh: các reference trên chỉ giúp chọn hướng xử lý; các mốc 225/600 ms, slot
ownership, epoch gate, revision ordering và test race là contract được triển khai/kiểm chứng
trong repository này.

## 12. Giới hạn hiện tại và hướng cải tiến

1. **Text-mask/tile gating:** scheduler hiện vẫn đưa latest sample vào OCR tối thiểu mỗi
   100 ms; appearance sampling chỉ chạy sau OCR để làm hint. Có thể thêm tile statistics,
   text-mask difference hoặc dirty-region gating trước OCR để giảm CPU khi nền animate mạnh.
2. **Spatial tracking:** normalized multiset bỏ qua order nhưng chưa theo dõi vị trí/line qua
   frame. Với duplicate text hoặc line reorder, ordinal occurrence có thể không đại diện cho
   cùng vị trí vật lý; tracking theo vùng cần được đánh giá riêng.
3. **Fuzzy matching:** OCR typo nhỏ hiện là content change thật và đi qua settle/provider
   policy. Có thể thêm fuzzy identity nhưng phải tránh gộp nhầm hai câu ngắn.
4. **HDR:** WGC path chọn BGRA8 normalized và appearance dùng relative luminance kiểu sRGB;
   chưa có profile/tonemap chuyên biệt cho HDR hoặc wide-gamut capture.
5. **Fullscreen/protected content:** WGC có thể trả frame đen, lỗi hoặc không capture được
   protected surface, elevated surface hay một số fullscreen path. Code hiện xử lý lỗi/size
   invalidation nhưng chưa có detection và UX riêng cho từng loại.
6. **Scale profiling:** 100 ms là interval cố định. Chưa có profiling tự động theo kích thước
   crop, DPI/scale, frame rate WGC, thời gian Windows OCR, CPU và latency provider; các số đo
   production nên được thu thập trước khi điều chỉnh interval/adaptive sampling.
7. **Overlay mutation:** hiện ưu tiên full snapshot replacement để đảm bảo không còn text cũ.
   Keyed mutation có thể giảm layout work nhưng chỉ an toàn sau khi có invariant spatial
   identity tốt hơn.

## 13. Ghi chú vận hành và package

- Package identity trong `src/Translator.App.WinUI/Package.appxmanifest` là version
  **`1.0.0.20`**.
- API key là runtime-only: người dùng nhập vào `PasswordBox` trong mỗi session; `MainPage` đưa
  giá trị vào `OpenAICompatibleOptions`, provider chỉ dùng nó để tạo Authorization header.
  XAML ghi rõ key không được lưu; không có secret nào nằm trong report, source config hay
  package.
- Khi session dừng, `HttpClient`, cancellation sources, controller, coordinator và overlay
  surface đều được drain/dispose theo thứ tự. OCR crop/frame chỉ tồn tại trong memory path;
  fixture acceptance không yêu cầu lưu screenshot hay ảnh capture.
- Provider revision nằm trong `TranslationRequest.MemoryKey`, nên đổi model/provider revision
  sẽ partition cache thay vì trả nhầm bản dịch của revision trước.
