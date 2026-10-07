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
* Stop / Pause فقط ارسال را متوقف می‌کنند و Buffer پرینتر را پاک نمی‌کنند. Clear Buffer باید روی خود پرینتر انجام شود (`Home → Setup → I/O Port → Monitor`).
* ردیفی که Delimiter یا Marker داشته باشد، یا با Encoding انتخابی (مثلاً فارسی در ASCII) قابل ارسال نباشد، `Failed` می‌شود و ارسال نمی‌شود (هیچ `?` ای جایگزین نمی‌شود).
* مقدار سلول‌های متنی (مثل `001245`) بدون تغییر ارسال می‌شود.
* تنظیمات: `%APPDATA%\DominoAx150iBridge\settings.json`، لاگ‌ها: `%APPDATA%\DominoAx150iBridge\logs`.

## مدیریت صف (مهم)
بدون BufferDepth برنامه نمی‌تواند بداند پرینتر چند آیتم مصرف کرده است. بنابراین برنامه حداکثر `Batch size` (پیش‌فرض 10، سقف = Max queue size پرینتر یعنی 32) ردیف را می‌فرستد و می‌ایستد (`WaitingForOperator`). پس از اینکه آیتم‌ها چاپ/مصرف شدند، **Printer buffer empty** و سپس **Start / Continue** را بزنید. شمارنده «Queue» فقط یک تخمین است.

## تنظیمات پرینتر طبق مستند EDC (EPT077984)
`Home → Setup → Printer network → Protocol settings → EDC TCP` (قبل از تغییر، Protocol enabled را غیرفعال کنید؛ بعد از تغییر دوباره فعال کنید):
* Ack type = **DefaultSpecialChars** (ACK = بایت 06). نوع «Default» حروف `ACK` (41 43 4B) می‌فرستد که برنامه آن را هم می‌پذیرد.
* Ack schedule = **AfterParsing**، Persistence = **Disabled**.
* **Maximum queue size** = 32 یا بیشتر. **Buffer overwrite** باید خاموش باشد (صف را به ۱ محدود می‌کند). History checked را خاموش بگذارید (داده تکراری Alert می‌دهد).
* **Buffer empty behavior** را روی **Blank label** بگذارید. پیش‌فرض (Blank EDC element) وقتی داده تمام شود محصول را بدون متغیر چاپ می‌کند.
* Buffer empty alert را از `Home → Setup → Alert configuration → Configure alerts` روی Red بگذارید تا چاپ بدون داده انجام نشود.
* تعداد رشته‌های موجود در Buffer: `Home → Setup → I/O Port → Monitor` ← `EdcTcpSing queue state` (دکمهٔ Clear همان‌جا Buffer را پاک می‌کند).
* بعد از هر تغییر Protocol، Label را دوباره با `Label finder → Send to print` ارسال کنید. پرینتر باید Ready باشد. فیلدهای EDC تا رسیدن داده و Trigger خالی‌اند.

## ساخت Label
`Label creator → Element → Add → Text (یا Barcode) → +Variable → Insert new… → External data`:
* Source = **EDC TCP**، Delimited = ✔، Delimiter = `,` (همان Delimiter برنامه)، Index = 0، 1، 2، … (اولین بلوک Index 0 است).
* **Length** حداکثر تعداد کاراکتر چاپ‌شده است؛ آن را بزرگ‌تر از طولانی‌ترین مقدار بگذارید. Offset را 0 نگه دارید.
* برای Barcode: `Element → Add → Barcode`، نوع را انتخاب و Add کنید، سپس `+Variable → Insert new… → External data`. اندازهٔ Data Matrix بر اساس بدترین حالت Length محاسبه می‌شود.

## UNKNOWN / REQUIRES DEVICE TEST
* قالب NAK در EDC TCP در مستند نیامده (در Codenet برابر `15 A B C` است). هر پاسخ حداکثر ۴ بایتی که با `15` شروع شود NAK حساب می‌شود و بایت‌ها در لاگ و ستون Message نمایش داده می‌شوند.
* BufferDepth ack (Filled/Empty، Text/1 byte/2 bytes) پیاده‌سازی نشده. مستند می‌گوید فقط برای Buffer تا ۲۵۵ آیتم کار می‌کند.
* مستند توصیه می‌کند برای بهترین کارایی، کمترین تعداد رشته را همزمان در Buffer نگه دارید.
* Data Packet Typeهای غیر از «Use start end marker» و Ack Typeهای RegExpression پشتیبانی نمی‌شوند.
* با Encodingهای Unicode(BE/LE) مشخص نیست Markerها یک‌بایتی‌اند یا کاراکتر Encode شده؛ برنامه یک بایت می‌فرستد.
* Clear Buffer از برنامه انجام نمی‌شود. فرمان `ESC OE 0000 0 EOT` فقط روی پورت Codenet (7000) هست و عمداً استفاده نشده؛ از منوی Monitor پرینتر پاک کنید.

## ساخت
```
dotnet test tests/DominoBridge.Tests
dotnet publish src/DominoBridge.App -c Release -o dist   # dist/DominoAx150iBridge.exe
```
معماری: `DominoBridge.Core` (EdcProtocol، AckParser، EdcTcpClient، PrinterConnectionManager، ExcelReader، ColumnMapper، PrintQueue، SendScheduler — مستقل از UI و Unit-test شده) و `DominoBridge.App` (WPF/MVVM).
