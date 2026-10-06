# Domino Ax150i Excel Printer Bridge (EDC TCP)

برنامه Windows (C# / .NET 8 / WPF / MVVM) که ردیف‌های فایل Excel را به‌صورت Data String (`STX + DATA + ETX`) از طریق **Domino EDC TCP** (پورت پیش‌فرض 16000) به Ax150i می‌فرستد تا در **FIFO Buffer** پرینتر قرار بگیرند (Buffer Printing، Persistence = Disabled). هر Trigger یک ردیف مصرف می‌کند.

## اجرا
فایل `DominoAx150iBridge.exe` (Self-contained، نیازی به نصب .NET یا Excel نیست) را اجرا کنید.

1. IP پرینتر را وارد و **Test Connection** بزنید (فقط TCP connect؛ هیچ داده‌ای ارسال نمی‌شود).
2. روی پرینتر: `Home → Setup → Printer network → Protocol settings → EDC TCP`، Port 16000، STX/ETX، ASCII، Ack = DefaultSpecialChars (0x06)، AfterParsing، Persistence = Disabled. Label (با External Data Elementهای Delimited و Index) باید قبلاً روی پرینتر ساخته و Online باشد.
3. (اختیاری) **Send Test Data** → بایت‌های `02 54 45 53 54 31 32 33 03` و نمایش ACK.
4. فایل xlsx را انتخاب، ستون‌ها را به EDC Index نگاشت، **Preview** و سپس **Start**.

## رفتار مهم
* ACK یعنی «پذیرفته شده توسط EDC پرینتر» نه «چاپ شده». وضعیت `Printed` هرگز توسط برنامه ست نمی‌شود.
* NAK / Timeout / قطع اتصال / پاسخ غیرمنتظره → ارسال متوقف می‌شود؛ ردیف `Rejected` یا `Unknown` می‌ماند و **هرگز خودکار دوباره ارسال نمی‌شود**. تا تعیین تکلیف (Retry / Mark accepted / Mark NOT received / Skip) ارسال ردیف‌های بعدی بلوکه است تا ترتیب چاپ به‌هم نخورد.
* Stop / Pause فقط ارسال را متوقف می‌کنند و Buffer پرینتر را پاک نمی‌کنند. Clear Buffer باید روی خود پرینتر انجام شود (دستور آن در مشخصات EDC TCP ذکر نشده است).
* ردیفی که Delimiter یا Marker داشته باشد، یا با Encoding انتخابی (مثلاً فارسی در ASCII) قابل ارسال نباشد، `Failed` می‌شود و ارسال نمی‌شود (هیچ `?` ای جایگزین نمی‌شود).
* مقدار سلول‌های متنی (مثل `001245`) بدون تغییر ارسال می‌شود.
* تنظیمات: `%APPDATA%\DominoAx150iBridge\settings.json`، لاگ‌ها: `%APPDATA%\DominoAx150iBridge\logs`.

## مدیریت صف (مهم)
بدون BufferDepth برنامه نمی‌تواند بداند پرینتر چند آیتم مصرف کرده است. بنابراین برنامه حداکثر `Batch size` (پیش‌فرض 10، سقف = Max queue size پرینتر یعنی 32) ردیف را می‌فرستد و می‌ایستد (`WaitingForOperator`). پس از اینکه آیتم‌ها چاپ/مصرف شدند، **Printer buffer empty** و سپس **Start / Continue** را بزنید. شمارنده «Queue» فقط یک تخمین است.

## UNKNOWN / REQUIRES DEVICE TEST
* بایت NAK در مشخصات نیامده؛ `0x15` فقط placeholder است (Advanced settings → NAK byte).
* BufferDepth ack (Filled/Empty، فرمت‌های Text/1 byte/2 byte) پیاده‌سازی نشده (نقطه توسعه: `IAckParser`).
* Data Packet Typeهای غیر از «Use start end marker» و Ack Typeهای غیر از DefaultSpecialChars پشتیبانی نمی‌شوند.
* با Encodingهای UTF-16 مشخص نیست Markerها یک‌بایتی‌اند یا کاراکتر Encode شده؛ برنامه یک بایت می‌فرستد. برای متن فارسی، Encoding و فونت دستگاه باید با دستگاه تست شود.
* اینکه پرینتر چند اتصال TCP همزمان می‌پذیرد مشخص نیست؛ Test Connection در حالت متصل از همان اتصال استفاده می‌کند.

## ساخت
```
dotnet test tests/DominoBridge.Tests
dotnet publish src/DominoBridge.App -c Release -o dist   # dist/DominoAx150iBridge.exe
```
معماری: `DominoBridge.Core` (EdcProtocol، AckParser، EdcTcpClient، PrinterConnectionManager، ExcelReader، ColumnMapper، PrintQueue، SendScheduler — مستقل از UI و Unit-test شده) و `DominoBridge.App` (WPF/MVVM).
