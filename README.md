# ScreenTranslator

ScreenTranslator là ứng dụng desktop Windows dùng WinUI 3 để dịch văn bản trong một
vùng được chọn của cửa sổ Chrome đang hiển thị. Ứng dụng chụp vùng chọn, nhận dạng
văn bản bằng Windows OCR, gửi các dòng đã nhận dạng tới dịch vụ tương thích OpenAI,
và hiển thị bản dịch trong một overlay không nhận click.

## Luồng xử lý

1. Chọn một cửa sổ Chrome top-level đang hiển thị.
2. Kéo chọn vùng cần dịch trên ảnh snapshot của cửa sổ.
3. Xác nhận vùng chọn và đặt ngôn ngữ nguồn, ngôn ngữ đích.
4. Nhập endpoint, model và API key trong ứng dụng rồi bắt đầu phiên dịch.
5. Ứng dụng chuyển sang capture realtime vùng đã chọn bằng Windows Graphics Capture.
6. Mỗi frame được crop theo tọa độ pixel vật lý và đưa qua Windows OCR.
7. Các dòng OCR mới được lọc, khử trùng lặp và gửi tới translation endpoint.
8. Overlay cập nhật bản dịch mới nhất và bám theo vị trí vùng chọn trên cửa sổ Chrome.

## Ngôn ngữ hỗ trợ

- Nguồn: `JA`, `zh-CN`, `zh-TW`.
- Đích: tiếng Anh hoặc tiếng Việt.
- Kết quả thực tế còn phụ thuộc vào khả năng của endpoint và model đã cấu hình.

## Điểm kỹ thuật chính

- WinUI 3 trên .NET 10, target Windows SDK `10.0.26100.0`, x64.
- Windows Graphics Capture và Windows OCR cho capture/nhận dạng tại máy.
- Adapter HTTP tương thích OpenAI để gọi các dịch vụ dịch thuật khác nhau.
- Overlay click-through theo dõi vị trí cửa sổ và vùng chọn.
- Pipeline latest-value có giới hạn: giá trị mới thay thế công việc đang chờ cũ.
- Hủy công việc bị thay thế và loại bỏ kết quả không còn hiệu lực theo generation hiện tại.
- Khử trùng lặp các yêu cầu đang chạy và dùng LRU translation-memory cache.

## Kiến trúc module

```text
src/
├── Translator.App.WinUI/
│   └── UI WinUI 3, điều phối phiên và translation overlay
├── Translator.Windows/
│   └── Window capture, crop, OCR và tọa độ vùng chọn
├── Translator.Providers.OpenAICompatible/
│   └── Adapter HTTP cho translation endpoint tương thích OpenAI
└── Translator.Core/
    └── Contract, session, mailbox và cache độc lập nền tảng
```

Các test project nằm dưới `tests/`. Solution chính là `ScreenTranslator.slnx`.

## Dữ liệu và cấu hình

- Capture và OCR được thực hiện trên Windows.
- Trong thời gian phiên chạy, văn bản đã nhận dạng được gửi tới translation endpoint
  đã cấu hình để lấy bản dịch.
- Endpoint, model và API key được nhập trong ứng dụng khi chạy; chúng không được đọc
  từ biến môi trường.
- API key được nhập theo từng phiên và không được ứng dụng lưu lại.
- README này không giả định rằng ảnh chụp màn hình luôn được lưu hoặc luôn không được
  lưu. Quy trình capture và các thư mục tạm của hệ điều hành có thể cần được kiểm tra
  theo môi trường triển khai.

## Yêu cầu trước khi chạy

- Windows có hỗ trợ Windows App SDK/WinUI 3.
- .NET 10 SDK.
- Windows SDK target `10.0.26100.0` và môi trường build x64.
- Windows OCR language packs tương ứng với ngôn ngữ nguồn cần dùng.
- Một cửa sổ Chrome top-level đang hiển thị nội dung cần dịch.
- Translation endpoint tương thích OpenAI, model hợp lệ và API key nếu endpoint yêu cầu.

Endpoint, model và API key phải được nhập trong giao diện ứng dụng, không cấu hình bằng
biến môi trường.

## Restore, build và chạy debug

Chạy PowerShell từ thư mục gốc repository:

```powershell
dotnet restore .\ScreenTranslator.slnx
dotnet build .\ScreenTranslator.slnx --no-restore
dotnet run --project .\src\Translator.App.WinUI\Translator.App.WinUI.csproj -c Debug -p:Platform=x64
```

Project WinUI là single-project MSIX; lệnh build app cũng kiểm tra phần đóng gói:

```powershell
dotnet build .\src\Translator.App.WinUI\Translator.App.WinUI.csproj `
  -c Release -p:Platform=x64 -p:PublishReadyToRun=false --no-restore
```

## Kiểm thử đã xác minh

Các lệnh dưới đây là baseline kiểm thử sau khi restore:

```powershell
dotnet test .\tests\Translator.Core.Tests\Translator.Core.Tests.csproj --no-restore
dotnet test .\tests\Translator.Providers.OpenAICompatible.Tests\Translator.Providers.OpenAICompatible.Tests.csproj --no-restore
dotnet test .\tests\Translator.Windows.Tests\Translator.Windows.Tests.csproj --no-restore
dotnet build .\ScreenTranslator.slnx --no-restore
```

## Giới hạn hiện tại

- Chỉ chọn được cửa sổ Chrome top-level đang hiển thị; không bao quát mọi loại cửa sổ
  hoặc nội dung bị che.
- Ngôn ngữ nguồn và đích bị giới hạn như danh sách ở trên.
- Dịch từ xa phụ thuộc vào tính sẵn sàng, giao thức, model và giới hạn của endpoint.
