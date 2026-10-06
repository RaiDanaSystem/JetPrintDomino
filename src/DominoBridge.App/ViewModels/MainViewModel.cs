using System.Collections.ObjectModel;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using DominoBridge.App.Services;
using DominoBridge.Core.Configuration;
using DominoBridge.Core.Excel;
using DominoBridge.Core.Logging;
using DominoBridge.Core.Protocol;
using DominoBridge.Core.Queue;
using DominoBridge.Core.Transport;

namespace DominoBridge.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private const string TestPayload = "TEST123";

    private readonly IDialogService _dialogs;
    private readonly AppLogger _logger;
    private readonly PrinterConnectionManager _conn;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private ExcelTable? _table;
    private PrintQueue? _queue;
    private SendScheduler? _scheduler;
    private List<(string Header, int Edc)> _lastMapping = new();
    private CancellationTokenSource? _cts;

    private string _excelPath = "";
    private string _excelInfo = "No file loaded";
    private IReadOnlyList<PrintRow> _rows = Array.Empty<PrintRow>();
    private PrintRow? _selectedRow;
    private string _previewText = "Load an Excel file, map the columns and press Preview.";
    private string _connectionText = "Disconnected";
    private string _schedulerText = "Idle";
    private string _queueText = "";
    private string _countsText = "";
    private bool _running;

    public MainViewModel(IDialogService dialogs)
    {
        _dialogs = dialogs;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Settings = SettingsStore.Load();

        _logger = new AppLogger(System.IO.Path.Combine(SettingsStore.DefaultDirectory, "logs"));
        _logger.EntryLogged += e => Post(() => AddLog(e));

        _conn = new PrinterConnectionManager(Settings, null, _logger);
        _conn.StateChanged += _ => Post(RefreshStatus);

        BrowseCommand = new RelayCommand(Browse, () => !_running);
        LoadCommand = new RelayCommand(() => LoadExcel(ExcelPath), () => !_running && ExcelPath.Length > 0);
        ConnectCommand = new RelayCommand(ConnectAsync, () => !_running && !_conn.IsConnected);
        DisconnectCommand = new RelayCommand(() => { _conn.Disconnect(); RefreshStatus(); }, () => !_running && _conn.State != ConnectionState.Disconnected);
        TestConnectionCommand = new RelayCommand(TestConnectionAsync, () => !_running);
        SendTestCommand = new RelayCommand(SendTestAsync, () => !_running && _conn.IsConnected);
        PreviewCommand = new RelayCommand(Preview, () => !_running && _table != null);
        StartCommand = new RelayCommand(StartAsync, () => !_running && _queue != null && _queue.Rows.Count > 0);
        PauseCommand = new RelayCommand(() => _scheduler?.Pause(), () => _scheduler?.State == SchedulerState.Running);
        ResumeCommand = new RelayCommand(() => _scheduler?.Resume(), () => _scheduler?.State == SchedulerState.Paused);
        StopCommand = new RelayCommand(() => _scheduler?.RequestStop(), () => _running);
        BufferDrainedCommand = new RelayCommand(BufferDrained, () => !_running && _scheduler != null);
        RetryCommand = new RelayCommand(() => Decide(r => _queue!.Retry(r)), () => CanDecide(RowStatus.Rejected));
        MarkAcceptedCommand = new RelayCommand(() => Decide(r => _queue!.MarkAccepted(r)), () => CanDecide(RowStatus.Unknown));
        MarkNotReceivedCommand = new RelayCommand(() => Decide(r => _queue!.MarkNotReceived(r)), () => CanDecide(RowStatus.Unknown));
        SkipCommand = new RelayCommand(SkipSelected, () => !_running && _selectedRow is { Status: RowStatus.Rejected or RowStatus.Failed or RowStatus.Unknown or RowStatus.Pending });

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => RefreshStatus();
        _timer.Start();

        _excelPath = Settings.Excel.LastFile;
        RefreshStatus();
        _logger.Info("Application started");
    }

    private void Post(Action a)
    {
        if (_dispatcher.CheckAccess()) a();
        else _dispatcher.BeginInvoke(a);
    }

    public AppSettings Settings { get; }
    public IReadOnlyList<string> EncodingNames => EdcEncodings.Names;
    public IReadOnlyList<string> DelimiterChoices { get; } = new[] { ",", ";", "|", "TAB" };
    public ObservableCollection<string> LogLines { get; } = new();
    public ObservableCollection<MappingRowVM> MappingRows { get; } = new();

    public ICommand BrowseCommand { get; }
    public ICommand LoadCommand { get; }
    public ICommand ConnectCommand { get; }
    public ICommand DisconnectCommand { get; }
    public ICommand TestConnectionCommand { get; }
    public ICommand SendTestCommand { get; }
    public ICommand PreviewCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand BufferDrainedCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand MarkAcceptedCommand { get; }
    public ICommand MarkNotReceivedCommand { get; }
    public ICommand SkipCommand { get; }

    public string ExcelPath { get => _excelPath; set => Set(ref _excelPath, value); }
    public string ExcelInfo { get => _excelInfo; private set => Set(ref _excelInfo, value); }
    public IReadOnlyList<PrintRow> Rows { get => _rows; private set => Set(ref _rows, value); }
    public string PreviewText { get => _previewText; private set => Set(ref _previewText, value); }
    public string ConnectionText { get => _connectionText; private set => Set(ref _connectionText, value); }
    public string SchedulerText { get => _schedulerText; private set => Set(ref _schedulerText, value); }
    public string QueueText { get => _queueText; private set => Set(ref _queueText, value); }
    public string CountsText { get => _countsText; private set => Set(ref _countsText, value); }

    public PrintRow? SelectedRow
    {
        get => _selectedRow;
        set { if (Set(ref _selectedRow, value)) PreviewText = BuildPreview(value); }
    }

    // ---------- connection ----------

    private async Task ConnectAsync()
    {
        if (!await _conn.ConnectAsync())
            _dialogs.Error("Connection Failed: " + _conn.LastError);
        RefreshStatus();
    }

    private async Task TestConnectionAsync()
    {
        var (ok, msg) = await _conn.TestConnectionAsync();
        if (ok) _dialogs.Info("Test Connection", msg + "\n\nNothing was sent to the printer.");
        else _dialogs.Error(msg);
    }

    private async Task SendTestAsync()
    {
        var protocol = new EdcProtocol(Settings.Printer);
        var errors = protocol.ValidateConfiguration().Where(i => i.Severity == IssueSeverity.Error).ToList();
        if (errors.Count > 0) { _dialogs.Error(errors[0].Message); return; }
        if (!_dialogs.Confirm("Send Test Data",
                $"This sends \"{TestPayload}\" as a real EDC data string. The printer will put it in its buffer and it can be printed on the next product trigger.\n\nSend it?"))
            return;
        var packet = protocol.BuildDataPacket(TestPayload);
        var r = await _conn.SendPacketAsync(protocol, packet);
        _logger.Info($"Test data: {r.Outcome} - {r.Message}");
        var msg = $"TX: {EdcProtocol.ToHex(packet)}\nRX: {EdcProtocol.ToHex(r.Response)}\n\n{r.Message}";
        if (r.Outcome == DeliveryOutcome.Accepted) _dialogs.Info("Send Test Data", msg + "\n\n(ACK = accepted by the printer, not printed.)");
        else _dialogs.Error(msg);
        RefreshStatus();
    }

    // ---------- excel / mapping ----------

    private void Browse()
    {
        var path = _dialogs.PickExcelFile();
        if (path == null) return;
        ExcelPath = path;
        LoadExcel(path);
    }

    private void LoadExcel(string path)
    {
        if (_queue != null && _queue.Counts().Sent > 0 &&
            !_dialogs.Confirm("Load Excel", "The current queue has already been (partly) sent. Loading a new file discards this progress. Continue?"))
            return;
        try
        {
            _table = ExcelReader.Load(path);
        }
        catch (Exception ex)
        {
            _logger.Error("Excel parsing error: " + ex.Message);
            _dialogs.Error(ex.Message);
            return;
        }
        _logger.Info($"Row loaded: {_table.Rows.Count} rows, {_table.Headers.Length} columns from '{System.IO.Path.GetFileName(path)}' (sheet '{_table.SheetName}')");
        ExcelInfo = $"Sheet '{_table.SheetName}': {_table.Rows.Count} rows, {_table.Headers.Length} columns";
        Settings.Excel.LastFile = path;

        MappingRows.Clear();
        for (int i = 0; i < _table.Headers.Length; i++)
        {
            var saved = Settings.Excel.Mapping.FirstOrDefault(m => m.Header == _table.Headers[i]);
            var sample = _table.Rows.Count > 0 ? _table.Rows[0].Cells[i] : "";
            var vm = new MappingRowVM(i, _table.Headers[i], sample);
            // First load: map columns in order. Later loads: reuse the saved mapping for matching headers.
            vm.EdcIndexText = Settings.Excel.Mapping.Count == 0 ? i.ToString()
                : saved?.EdcIndex?.ToString() ?? "";
            MappingRows.Add(vm);
        }
        _queue = null; _scheduler = null;
        Rows = Array.Empty<PrintRow>();
        SelectedRow = null;
        PreviewText = "Check the mapping, then press Preview.";
        RefreshStatus();
    }

    private void Preview()
    {
        if (_table == null) return;
        if (_queue != null && _queue.Counts().Sent > 0 &&
            !_dialogs.Confirm("Preview", "Rebuilding the queue discards the send progress of the current queue. Continue?"))
            return;

        var mapping = new List<FieldMapping>();
        foreach (var m in MappingRows)
        {
            var t = m.EdcIndexText.Trim();
            if (t.Length == 0) continue;
            if (!int.TryParse(t, out var idx)) { _dialogs.Error($"EDC index of '{m.Header}' is not a number."); return; }
            mapping.Add(new FieldMapping(m.ColumnIndex, idx));
        }
        var v = ColumnMapper.Validate(mapping, _table.Headers.Length);
        if (!v.IsValid) { _dialogs.Error(string.Join("\n", v.Errors)); return; }
        foreach (var w in v.Warnings) _logger.Warn(w);

        var protocol = new EdcProtocol(Settings.Printer);
        var issues = protocol.ValidateConfiguration();
        foreach (var i in issues.Where(i => i.Severity == IssueSeverity.Warning)) _logger.Warn(i.Message);
        var err = issues.FirstOrDefault(i => i.Severity == IssueSeverity.Error);
        if (err != null) { _dialogs.Error(err.Message); return; }

        _lastMapping = mapping.OrderBy(m => m.EdcIndex).Select(m => (_table.Headers[m.ColumnIndex], m.EdcIndex)).ToList();
        Settings.Excel.Mapping = MappingRows.Select(m => new MappingEntry
        {
            Header = m.Header,
            EdcIndex = int.TryParse(m.EdcIndexText.Trim(), out var i) ? i : null
        }).ToList();

        _queue = PrintQueue.FromExcel(_table, mapping, protocol);
        _scheduler = new SendScheduler(_queue, _conn, Settings, _logger);
        _scheduler.StateChanged += (_, _) => Post(RefreshStatus);
        Rows = _queue.Rows;

        var invalid = _queue.Rows.Count(r => r.Status == RowStatus.Failed);
        var dups = _queue.Rows.Where(r => r.DataString != null).GroupBy(r => r.DataString).Count(g => g.Count() > 1);
        _logger.Info($"Preview built: {_queue.Rows.Count} rows, {invalid} invalid");
        if (dups > 0) _logger.Warn($"{dups} data string(s) occur more than once in the file (they will print more than once).");
        SelectedRow = _queue.Rows.FirstOrDefault();
        if (invalid > 0)
            _dialogs.Info("Preview", $"{invalid} row(s) cannot be sent (see Message column). Sending is blocked until you fix the file or Skip those rows.");
        RefreshStatus();
    }

    private string BuildPreview(PrintRow? row)
    {
        if (row == null) return "No row selected.";
        var sb = new StringBuilder();
        sb.AppendLine($"Row {row.Sequence} (Excel row {row.ExcelRowId})");
        sb.AppendLine();
        foreach (var (header, edc) in _lastMapping)
            sb.AppendLine($"  {header,-18} = {(edc < row.Fields.Length ? row.Fields[edc] : "")}   (Index {edc})");
        sb.AppendLine();
        if (row.DataString != null)
        {
            sb.AppendLine("Payload:  " + row.DisplayPayload);
            sb.AppendLine("Hex:      " + row.HexPayload);
        }
        else sb.AppendLine(row.Message);
        sb.AppendLine();
        sb.AppendLine("(STX/ETX are shown as text here; real bytes 0x02 / 0x03 are sent.)");
        return sb.ToString();
    }

    // ---------- sending ----------

    private async Task StartAsync()
    {
        if (_scheduler == null || _queue == null) return;
        if (!_conn.IsConnected)
        {
            if (!await _conn.ConnectAsync()) { _dialogs.Error("Connection Failed: " + _conn.LastError); RefreshStatus(); return; }
        }
        _cts = new CancellationTokenSource();
        _running = true;
        RefreshStatus();
        try { await _scheduler.RunAsync(_cts.Token); }
        finally
        {
            _running = false;
            _cts.Dispose(); _cts = null;
            RefreshStatus();
        }
        if (_scheduler.State == SchedulerState.Halted && _scheduler.StateMessage != null)
            _dialogs.Error(_scheduler.StateMessage);
        else if (_scheduler.State == SchedulerState.WaitingForOperator && _scheduler.StateMessage != null)
            _dialogs.Info("Batch sent", _scheduler.StateMessage + "\n\nWhen the printer has printed/consumed them, press 'Printer buffer empty' and then Start again.");
    }

    private void BufferDrained()
    {
        if (_scheduler == null) return;
        if (!_dialogs.Confirm("Printer buffer empty",
                "Confirm that the printer's EDC buffer is empty (all queued items were printed/consumed). The app cannot read the buffer level (BufferDepth is not implemented). If the buffer is not empty, the printer will reject items beyond its Maximum Queue Size.\n\nReset the buffer counter?"))
            return;
        _scheduler.SetBufferEstimate(0);
        _logger.Info("Operator reset the printer buffer estimate to 0");
        RefreshStatus();
    }

    private bool CanDecide(RowStatus status) => !_running && _selectedRow?.Status == status;

    private void Decide(Func<PrintRow, bool> action)
    {
        if (_selectedRow == null || _queue == null) return;
        var row = _selectedRow;
        var before = row.Status;
        if (action(row)) _logger.Info($"Row {row.ExcelRowId}: operator changed {before} -> {row.Status}");
        if (before == RowStatus.Unknown && row.Status == RowStatus.Accepted) _scheduler?.SetBufferEstimate((_scheduler?.BufferEstimate ?? 0) + 1);
        PreviewText = BuildPreview(row);
        RefreshStatus();
    }

    private void SkipSelected()
    {
        if (_selectedRow == null || _queue == null) return;
        if (!_dialogs.Confirm("Skip row", $"Excel row {_selectedRow.ExcelRowId} will NOT be printed. Continue?")) return;
        Decide(r => _queue!.Skip(r));
    }

    // ---------- status ----------

    private void AddLog(LogEntry e)
    {
        LogLines.Add(e.ToString());
        while (LogLines.Count > 3000) LogLines.RemoveAt(0);
    }

    private void RefreshStatus()
    {
        ConnectionText = _conn.State switch
        {
            ConnectionState.Connected => $"Connected ({Settings.Printer.IpAddress}:{Settings.Printer.Port})",
            ConnectionState.Connecting => "Connecting...",
            _ => "Disconnected"
        };
        if (_scheduler != null)
        {
            SchedulerText = _scheduler.State + (string.IsNullOrEmpty(_scheduler.StateMessage) ? "" : " - " + _scheduler.StateMessage);
            QueueText = $"Printer queue (estimate): {_scheduler.BufferEstimate} / {Settings.Queue.MaxQueueSize}   Batch limit: {_scheduler.BatchLimit}";
        }
        else { SchedulerText = "Idle"; QueueText = ""; }
        if (_queue != null)
        {
            var c = _queue.Counts();
            CountsText = $"Sent: {c.Sent}   Accepted: {c.Accepted}   Rejected/Failed: {c.Rejected + c.Failed}   Unknown: {c.Unknown}   Skipped: {c.Skipped}   Remaining: {c.Pending + c.Sending}   (Accepted = taken by printer, not printed)";
        }
        else CountsText = "";
        CommandManager.InvalidateRequerySuggested();
    }

    public bool ConfirmClose()
    {
        if (!_running) return true;
        return _dialogs.Confirm("Exit", "Sending is in progress. Closing now may leave a row in an unknown state. Exit anyway?");
    }

    public void Dispose()
    {
        _timer.Stop();
        _cts?.Cancel();
        try { SettingsStore.Save(Settings); } catch { }
        _conn.Dispose();
    }
}
