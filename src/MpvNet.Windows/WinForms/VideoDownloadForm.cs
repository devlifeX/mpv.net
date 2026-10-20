using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MpvNet.Windows.WinForms;

public sealed class VideoDownloadForm : Form
{
    static readonly Color Surface = Color.FromArgb(38, 41, 47);
    static readonly Color Field = Color.FromArgb(27, 30, 35);
    static readonly Color TextColor = Color.FromArgb(235, 238, 243);
    static readonly Color Border = Color.FromArgb(74, 80, 91);
    static readonly Color Accent = Color.FromArgb(75, 161, 245);
    public static string FileFilter => _("Matroska video").Replace('|', ' ') + " (*.mkv)|*.mkv";
    readonly VideoDownloadControl _control = new();
    readonly CancellationTokenSource _cancel;
    readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill };
    readonly Label _statistics = new() { AutoSize = false, Dock = DockStyle.Fill };
    readonly DownloadProgressStrip _progress = new() { Dock = DockStyle.Fill };
    readonly Panel _map = new() { Dock = DockStyle.Fill, Height = 26 };
    readonly ListView _chunks = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, VirtualMode = true };
    readonly NumericUpDown _connections = new() { Minimum = 1, Maximum = 32, Value = 8, Width = 75 };
    readonly NumericUpDown _speed = new() { Minimum = 0, Maximum = 1000000, Increment = 256, Width = 105 };
    readonly Button _start = new DownloadButton { Text = _("Start"), AutoSize = true };
    readonly Button _pause = new DownloadButton { Text = _("Pause"), AutoSize = true, Enabled = false };
    readonly Button _close = new DownloadButton { Text = _("Cancel (keep chunks)"), AutoSize = true };
    readonly CheckBox _openFolder = new() { Text = _("Open destination folder when complete"), AutoSize = true };
    TaskCompletionSource<bool>? _startSignal;
    DownloadSnapshot? _snapshot;
    bool _running;
    bool _finished;
    readonly string _filename;

    public VideoDownloadForm(string filename, BufferedVideoSelection selection, CancellationTokenSource cancel)
    {
        _filename = filename; _cancel = cancel;
        Text = _("Save buffered video") + " — " + Path.GetFileName(filename);
        StartPosition = FormStartPosition.Manual;
        AutoScaleDimensions = new(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        MinimumSize = new(760, 620); Size = new(820, 720);
        BackColor = Surface; ForeColor = TextColor;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new(16), ColumnCount = 1, RowCount = 10 };
        foreach (int height in new[] { 65, 52, 78, 24, 32, 26 }) layout.RowStyles.Add(new(SizeType.Absolute, height));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 38));
        layout.RowStyles.Add(new(SizeType.Absolute, 30));
        layout.RowStyles.Add(new(SizeType.Absolute, 44));
        string source = selection.Tracks.First(t => t.Type == "video").Source;
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)) source = uri.GetLeftPart(UriPartial.Path);
        layout.Controls.Add(new TextBox { Text = source + Environment.NewLine + filename, Multiline = true,
            ReadOnly = true, Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle }, 0, 0);
        _status.Text = _("Ready. Complete cached chunks are reused; missing chunks will be downloaded.");
        layout.Controls.Add(_status, 0, 1);
        layout.Controls.Add(_statistics, 0, 2);
        layout.Controls.Add(_progress, 0, 3);
        layout.Controls.Add(new Label { Text = _("Blue: cached / resumed   Green: downloaded   Orange: receiving   Gray: missing"),
            Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, 4);
        layout.Controls.Add(_map, 0, 5);
        foreach (var column in new[] { (_("Chunk"), 65), (_("Start"), 115), (_("Size"), 105), (_("Received"), 115), (_("Status"), 190) })
            _chunks.Columns.Add(column.Item1, column.Item2);
        _chunks.RetrieveVirtualItem += (sender, e) => {
            if (_snapshot == null || e.ItemIndex >= _snapshot.Chunks.Length) { e.Item = new(""); return; }
            var c = _snapshot.Chunks[e.ItemIndex];
            e.Item = new ListViewItem([(e.ItemIndex + 1).ToString(CultureInfo.CurrentCulture), Bytes(c.Start), Bytes(c.Length),
                Bytes(c.Received), c.Cached ? _("Cached / resumed") : _(c.State)]);
        };
        layout.Controls.Add(_chunks, 0, 6);
        var settings = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        settings.Controls.Add(new Label { Text = _("Connections:"), AutoSize = true, Padding = new(0, 5, 0, 0) });
        settings.Controls.Add(_connections);
        settings.Controls.Add(new Label { Text = _("Speed limit (KiB/s, 0 = unlimited):"), AutoSize = true, Padding = new(12, 5, 0, 0) });
        settings.Controls.Add(_speed);
        layout.Controls.Add(settings, 0, 7);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false };
        _openFolder.Dock = DockStyle.Fill;
        layout.Controls.Add(_openFolder, 0, 8);
        buttons.Controls.AddRange([_start, _pause, _close]);
        layout.Controls.Add(buttons, 0, 9);
        Controls.Add(layout);
        ApplyColors(this);
        _chunks.OwnerDraw = true;
        _chunks.DrawColumnHeader += (sender, e) => {
            using var fill = new SolidBrush(Surface);
            using var border = new Pen(Border);
            e.Graphics.FillRectangle(fill, e.Bounds);
            e.Graphics.DrawLine(border, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text, Font, Rectangle.Inflate(e.Bounds, -6, 0), TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        _chunks.DrawSubItem += (sender, e) => {
            bool selected = e.Item?.Selected == true;
            using var fill = new SolidBrush(selected ? Color.FromArgb(46, 76, 110) : Field);
            e.Graphics.FillRectangle(fill, e.Bounds);
            TextRenderer.DrawText(e.Graphics, e.SubItem?.Text, Font, Rectangle.Inflate(e.Bounds, -6, 0), TextColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        };
        _chunks.DrawItem += (sender, e) => { if (_chunks.View != View.Details) e.DrawDefault = true; };
        _chunks.ClientSizeChanged += (_, _) => SizeLastColumn();
        _map.Paint += PaintMap;
        _start.Click += (_, _) => _startSignal?.TrySetResult(true);
        _pause.Click += (_, _) => {
            if (_control.IsPaused) { _control.Resume(); _pause.Text = _("Pause"); }
            else { _control.Pause(); _pause.Text = _("Resume"); }
        };
        _speed.ValueChanged += (_, _) => _control.BytesPerSecond = (long)_speed.Value * 1024;
        _close.Click += (_, _) => { if (_finished) Close(); else Cancel(); };
        FormClosing += (_, e) => { if (_running) { e.Cancel = true; Cancel(); } else _startSignal?.TrySetResult(false); };
        if (selection.ByteCache != null) UpdateProgress(selection.ByteCache.Snapshot(_control));
        else _statistics.Text = _("The source will be checked for byte-range support. Streams without it use one connection.");
    }

    static void ApplyColors(Control parent)
    {
        parent.ForeColor = TextColor;
        parent.BackColor = parent is TextBoxBase or ListView or NumericUpDown ? Field : Surface;
        if (parent is Button button)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.UseVisualStyleBackColor = false;
            button.BackColor = Color.FromArgb(52, 58, 67);
            button.FlatAppearance.BorderColor = Border;
            button.FlatAppearance.MouseOverBackColor = Color.FromArgb(66, 76, 91);
            button.FlatAppearance.MouseDownBackColor = Color.FromArgb(42, 73, 105);
            button.Padding = new(12, 4, 12, 4);
            button.MinimumSize = new(95, 32);
        }
        if (parent is CheckBox checkBox) checkBox.UseVisualStyleBackColor = false;
        foreach (Control child in parent.Controls) ApplyColors(child);
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

    void SizeLastColumn()
    {
        if (_chunks.Columns.Count == 0) return;
        int preceding = _chunks.Columns.Cast<ColumnHeader>().SkipLast(1).Sum(c => c.Width);
        _chunks.Columns[^1].Width = Math.Max(180, _chunks.ClientSize.Width - preceding);
    }

    void Cancel()
    {
        _startSignal?.TrySetResult(false); _cancel.Cancel(); _control.Resume(); _close.Enabled = false;
        if (!_running) Close();
    }

    public async Task RunAsync(Func<VideoDownloadControl, IProgress<string>, IProgress<DownloadSnapshot>, Task> operation)
    {
        Show(MainForm.Instance!);
        while (!_cancel.IsCancellationRequested)
        {
            _startSignal = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _start.Enabled = true;
            try { if (!await _startSignal.Task.WaitAsync(_cancel.Token)) return; }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { Close(); return; }
            _running = true; _start.Enabled = false; _connections.Enabled = false; _pause.Enabled = true;
            _control.Connections = (int)_connections.Value;
            try
            {
                await operation(_control, new Progress<string>(text => {
                    if (!IsDisposed) _status.Text = text;
                }), new Progress<DownloadSnapshot>(snapshot => { if (!IsDisposed) UpdateProgress(snapshot); }));
                _finished = true;
                _progress.Value = 1000;
                _status.Text = _("Video saved and verified.") + Environment.NewLine + _filename;
                _close.Text = _("Close");
                if (_openFolder.Checked) Process.Start(new ProcessStartInfo("explorer.exe") {
                    UseShellExecute = true, ArgumentList = { "/select,", _filename }
                });
                break;
            }
            catch (OperationCanceledException) when (_cancel.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _status.Text = ex.Message + Environment.NewLine + _("Complete chunks are kept. Click Retry to continue.");
                _start.Text = _("Retry"); _connections.Enabled = true;
            }
            finally { _running = false; _pause.Enabled = false; _control.Resume(); _pause.Text = _("Pause"); }
        }
        if (_cancel.IsCancellationRequested) Close();
    }

    void UpdateProgress(DownloadSnapshot snapshot)
    {
        _snapshot = snapshot;
        double fraction = snapshot.Total > 0 ? (double)snapshot.Received / snapshot.Total : 0;
        _progress.Value = Math.Clamp((int)(fraction * 1000), 0, 1000);
        string eta = snapshot.BytesPerSecond > 0 && !snapshot.Paused
            ? TimeSpan.FromSeconds(Math.Min(99999999, (snapshot.Total - snapshot.Received) / snapshot.BytesPerSecond)).ToString(@"hh\:mm\:ss") : "—";
        _statistics.Text = _("Downloaded:") + " " + Bytes(snapshot.Received) + " / " + Bytes(snapshot.Total) +
            "  (" + fraction.ToString("P1", CultureInfo.CurrentCulture) + ")" + Environment.NewLine +
            _("Speed:") + " " + Bytes(snapshot.Paused ? 0 : (long)snapshot.BytesPerSecond) + "/s     " + _("Time left:") + " " + eta +
            Environment.NewLine + _("Resume capability:") + " " + _("Yes — complete chunks are kept beside the destination") +
            (snapshot.Paused ? "   • " + _("Paused") : "");
        _chunks.VirtualListSize = snapshot.Chunks.Length;
        _chunks.Invalidate(); _map.Invalidate();
    }

    void PaintMap(object? sender, PaintEventArgs e)
    {
        e.Graphics.Clear(Color.DimGray);
        if (_snapshot == null) return;
        foreach (var chunk in _snapshot.Chunks)
        {
            float x = (float)((double)chunk.Start / _snapshot.Total * _map.Width);
            float width = Math.Max(1, (float)((double)chunk.Length / _snapshot.Total * _map.Width));
            using var brush = new SolidBrush(chunk.Cached ? Color.DeepSkyBlue : chunk.Received == chunk.Length
                ? Color.LimeGreen : chunk.Received > 0 ? Color.Orange : Color.DimGray);
            e.Graphics.FillRectangle(brush, x, 0, width, _map.Height);
        }
    }

    static string Bytes(long bytes) => bytes >= 1024 * 1024 * 1024 ? (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GiB"
        : bytes >= 1024 * 1024 ? (bytes / (1024.0 * 1024)).ToString("F2") + " MiB" : (bytes / 1024.0).ToString("F1") + " KiB";

    sealed class DownloadProgressStrip : Control
    {
        int _value;
        public int Value { get => _value; set { _value = Math.Clamp(value, 0, 1000); Invalidate(); } }
        public DownloadProgressStrip() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Field);
            using var fill = new SolidBrush(Accent);
            using var border = new Pen(Border);
            e.Graphics.FillRectangle(fill, 0, 0, Width * Value / 1000, Height);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
        }
    }

    sealed class DownloadButton : Button
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            var background = Enabled ? BackColor : Surface;
            e.Graphics.Clear(background);
            using var border = new Pen(Border);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
                Enabled ? TextColor : Color.FromArgb(153, 163, 179), background,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4), TextColor, background);
        }
    }
}
