using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MpvNet.Windows.WinForms;

public sealed partial class VideoDownloadForm : Form
{
    public static string FileFilter => _("Matroska video").Replace('|', ' ') + " (*.mkv)|*.mkv";
    readonly VideoDownloadControl _control = new();
    readonly CancellationTokenSource _cancel;
    readonly Label _status = new() { Dock = DockStyle.Fill, UseMnemonic = false };
    readonly Label _phase = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    readonly Label _percentage = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight };
    readonly Label _downloaded = new() { AutoEllipsis = true, UseMnemonic = false };
    readonly Label _rate = new() { AutoEllipsis = true };
    readonly Label _rateCaption = new() { AutoEllipsis = true };
    readonly Label _timing = new() { AutoEllipsis = true };
    readonly Label _timingCaption = new() { AutoEllipsis = true };
    readonly ToolTip _details = new() { AutoPopDelay = 15000 };
    readonly DownloadProgressStrip _progress = new() { Dock = DockStyle.Fill };
    readonly Panel _map = new() { Dock = DockStyle.Fill, Height = 26 };
    readonly ListView _chunks = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, VirtualMode = true };
    readonly Label _emptyChunks = new() { Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter };
    readonly NumericUpDown _connections = new() { Minimum = 1, Maximum = 32, Value = 8, Width = 75 };
    readonly NumericUpDown _speed = new() { Minimum = 0, Maximum = 1000000, Increment = 256, Width = 105 };
    readonly Button _start = new DownloadButton(true) { Text = _("Start") };
    readonly Button _pause = new DownloadButton(true) { Text = _("Pause"), Enabled = false };
    readonly Button _close = new DownloadButton { Text = _("Cancel") };
    readonly CheckBox _openFolder = new() { Text = _("Open destination folder when complete"), AutoSize = true };
    readonly System.Windows.Forms.Timer _saveTimer = new() { Interval = 150 };
    readonly Stopwatch _stageClock = new();
    TaskCompletionSource<bool>? _startSignal;
    DownloadSnapshot? _snapshot;
    VideoSaveProgress? _saveProgress;
    bool _running;
    bool _finished;
    readonly string _filename;

    public VideoDownloadForm(string filename, BufferedVideoSelection selection, CancellationTokenSource cancel)
    {
        _filename = filename; _cancel = cancel;
        Text = _("Save buffered video");
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new(720, 590);
        Size = new(840, 690);
        BackColor = Surface;
        ForeColor = TextColor;
        BuildLayout(filename, selection.Tracks.First(t => t.Type == "video").Source);
        ConfigureChunkList();
        _status.Text = _("Complete chunks are reused. Start to download or save the video.");
        SetPhase(_("Ready"), Accent);
        SetProgress(null);
        SetTransferMetrics(null);
        _saveTimer.Tick += (_, _) => RenderSaveProgress();
        _start.Click += (_, _) => _startSignal?.TrySetResult(true);
        AcceptButton = _start;
        _pause.Click += (_, _) => {
            if (_control.IsPaused) { _control.Resume(); _pause.Text = _("Pause"); }
            else { _control.Pause(); _pause.Text = _("Resume"); }
            SetPhase(_control.IsPaused ? _("Paused") : _("Downloading"), _control.IsPaused ? Active : Accent);
            if (_snapshot is { } snapshot) SetTransferMetrics(snapshot with { Paused = _control.IsPaused });
        };
        _speed.ValueChanged += (_, _) => _control.BytesPerSecond = (long)_speed.Value * 1024;
        _close.Click += (_, _) => { if (_finished) Close(); else Cancel(); };
        FormClosing += (_, e) => { if (_running) { e.Cancel = true; Cancel(); } else _startSignal?.TrySetResult(false); };
        if (selection.ByteCache != null) UpdateProgress(selection.ByteCache.Snapshot(_control));
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (Environment.OSVersion.Version >= new Version(10, 0, 18985))
            DwmSetWindowAttribute(Handle, 20, [1], 4);
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(nint hwnd, int attribute, int[] value, int size);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        var area = Screen.FromControl(Owner ?? this).WorkingArea;
        var anchor = Owner?.Bounds ?? area;
        Size = new(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
        Location = new(Math.Clamp(anchor.Left + (anchor.Width - Width) / 2, area.Left, Math.Max(area.Left, area.Right - Width)),
            Math.Clamp(anchor.Top + (anchor.Height - Height) / 2, area.Top, Math.Max(area.Top, area.Bottom - Height)));
        SizeLastColumn();
    }

    void Cancel()
    {
        _startSignal?.TrySetResult(false); _cancel.Cancel(); _control.Resume(); _close.Enabled = false;
        if (_running)
        {
            SetPhase(_("Cancelling"), Active);
            _status.Text = _("Cancelling...");
        }
        if (!_running) Close();
    }

    public async Task RunAsync(Func<VideoDownloadControl, IProgress<string>, IProgress<DownloadSnapshot>,
        IProgress<VideoSaveProgress?>, Task> operation)
    {
        Show(MainForm.Instance!);
        while (!_cancel.IsCancellationRequested)
        {
            _startSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _start.Visible = _start.Enabled = true;
            try { if (!await _startSignal.Task.WaitAsync(_cancel.Token)) return; }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { Close(); return; }
            _running = true;
            _start.Visible = _start.Enabled = _connections.Enabled = false;
            _pause.Visible = _pause.Enabled = true;
            SetPhase(_("Downloading"), Accent);
            _control.Connections = (int)_connections.Value;
            try
            {
                await operation(_control, new Progress<string>(text => {
                    if (!IsDisposed && _running && !_cancel.IsCancellationRequested) _status.Text = text;
                }), new Progress<DownloadSnapshot>(snapshot => {
                    if (!IsDisposed && _running && !_cancel.IsCancellationRequested) UpdateProgress(snapshot);
                }), new Progress<VideoSaveProgress?>(stage => {
                    if (!IsDisposed && _running && !_cancel.IsCancellationRequested) UpdateSaveProgress(stage);
                }));
                _finished = true;
                SetPhase(_("Complete"), Accent);
                SetProgress(1);
                _status.Text = _("Video saved and verified.");
                _pause.Visible = _openFolder.Visible = false;
                if (_connections.Parent is { } settings) settings.Visible = false;
                _close.Text = _("Close");
                _close.AccessibleDescription = _("Close the save window.");
                AcceptButton = _close;
                _close.Invalidate();
                if (_openFolder.Checked) Process.Start(new ProcessStartInfo("explorer.exe") {
                    UseShellExecute = true, ArgumentList = { "/select,", _filename }
                });
                break;
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                SetPhase(_("Could not save"), ErrorColor);
                _status.Text = ex.Message + Environment.NewLine + _("Click Retry to continue.");
                _details.SetToolTip(_status, _status.Text);
                _start.Text = _("Retry"); _connections.Enabled = true;
            }
            finally
            {
                _running = false;
                _saveTimer.Stop();
                _saveProgress = null;
                _progress.Animation = null;
                _pause.Visible = _pause.Enabled = false;
                _speed.Enabled = !_finished;
                _control.Resume();
                _pause.Text = _("Pause");
            }
        }
        if (_cancel.IsCancellationRequested) Close();
    }

    void UpdateSaveProgress(VideoSaveProgress? stage)
    {
        if (stage?.Message != _saveProgress?.Message) _stageClock.Restart();
        _saveProgress = stage;
        _pause.Visible = _pause.Enabled = _speed.Enabled = stage == null;
        if (stage == null)
        {
            _saveTimer.Stop();
            _progress.Animation = null;
            SetPhase(_control.IsPaused ? _("Paused") : _("Downloading"), _control.IsPaused ? Active : Accent);
            if (_snapshot != null) UpdateProgress(_snapshot);
        }
        else
        {
            // The download gate was passed. A click queued before this callback must not pause a later stage.
            _control.Resume();
            _pause.Text = _("Pause");
            RenderSaveProgress();
            _saveTimer.Start();
        }
    }

    void RenderSaveProgress()
    {
        if (_saveProgress is not { } stage || _cancel.IsCancellationRequested) return;
        SetPhase(_("Saving"), Accent);
        _status.Text = stage.Message;
        SetProgress(stage.Fraction);
        _progress.Animation = stage.Fraction is > 0 ? null : (float)(_stageClock.Elapsed.TotalSeconds % 2 / 2);
        SetDownloadedMetric(_snapshot);
        _rateCaption.Text = _("Stage time");
        _rate.Text = Clock(_stageClock.Elapsed);
        _timingCaption.Text = _("Transfer");
        _timing.Text = _snapshot is { Total: > 0 } snapshot && snapshot.Received == snapshot.Total
            ? _("Download complete") : _("Local processing");
    }

    void UpdateProgress(DownloadSnapshot snapshot)
    {
        _snapshot = snapshot;
        _chunks.VirtualListSize = snapshot.Chunks.Length;
        bool hasChunks = snapshot.Chunks.Length > 0;
        _emptyChunks.Visible = !hasChunks;
        _chunks.Visible = hasChunks;
        _chunks.Invalidate(); _map.Invalidate();
        if (_saveProgress != null) return;
        SetProgress(snapshot.Total > 0 ? (double)snapshot.Received / snapshot.Total : null);
        SetTransferMetrics(snapshot);
    }

    void SetPhase(string text, Color color)
    {
        _phase.Text = text;
        _phase.ForeColor = color;
    }

    void SetProgress(double? fraction)
    {
        double bounded = Math.Clamp(fraction.GetValueOrDefault(), 0, 1);
        _percentage.Text = fraction.HasValue ? bounded.ToString("P0", CultureInfo.CurrentCulture) : "—";
        _progress.Value = (int)(bounded * 1000);
    }

    void SetDownloadedMetric(DownloadSnapshot? snapshot)
    {
        _downloaded.Text = snapshot is { Total: > 0 }
            ? Bytes(snapshot.Received) + " / " + Bytes(snapshot.Total) : "—";
    }

    void SetTransferMetrics(DownloadSnapshot? snapshot)
    {
        SetDownloadedMetric(snapshot);
        _rateCaption.Text = _("Speed");
        _timingCaption.Text = _("Time left");
        _rate.Text = snapshot == null ? "—" : Bytes(snapshot.Paused ? 0 : (long)snapshot.BytesPerSecond) + "/s";
        _timing.Text = snapshot is { BytesPerSecond: > 0, Paused: false }
            ? Clock(TimeSpan.FromSeconds(Math.Clamp((snapshot.Total - snapshot.Received) / snapshot.BytesPerSecond, 0, 99999999)))
            : "—";
    }

    static string Clock(TimeSpan duration) => duration.ToString(@"hh\:mm\:ss");

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _saveTimer.Dispose();
            _details.Dispose();
            foreach (Font font in _layoutFonts) font.Dispose();
        }
        base.Dispose(disposing);
    }

    static string Bytes(long bytes) => bytes >= 1024 * 1024 * 1024 ? (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GiB"
        : bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString("F2") + " MiB" : (bytes / 1024.0).ToString("F1") + " KiB";

}
