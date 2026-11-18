using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Windows.Forms;

namespace MpvNet.Windows.WinForms;

public sealed partial class VideoDownloadForm
{
    static readonly Color Surface = Color.FromArgb(16, 21, 29);
    static readonly Color Card = Color.FromArgb(25, 33, 45);
    static readonly Color Field = Color.FromArgb(19, 27, 38);
    static readonly Color Border = Color.FromArgb(47, 61, 80);
    static readonly Color TextColor = Color.FromArgb(236, 242, 250);
    static readonly Color Muted = Color.FromArgb(164, 179, 199);
    static readonly Color Accent = Color.FromArgb(101, 225, 186);
    static readonly Color Cached = Color.FromArgb(116, 164, 254);
    static readonly Color Active = Color.FromArgb(246, 184, 119);
    static readonly Color Missing = Color.FromArgb(55, 68, 85);
    static readonly Color ErrorColor = Color.FromArgb(255, 151, 154);
    readonly List<Font> _layoutFonts = [];

    Font LayoutFont(float size, FontStyle style = FontStyle.Regular)
    {
        var font = new Font(Font.FontFamily, size, style);
        _layoutFonts.Add(font);
        return font;
    }

    void BuildLayout(string destination, string source)
    {
        var layout = Grid(1);
        layout.Padding = new(22, 15, 22, 10);
        layout.RowStyles.Add(new(SizeType.Absolute, 92));
        layout.RowStyles.Add(new(SizeType.Absolute, 163));
        layout.RowStyles.Add(new(SizeType.Percent, 100));
        layout.RowStyles.Add(new(SizeType.Absolute, 90));
        Controls.Add(layout);

        var header = Grid(2);
        header.ColumnStyles.Add(new(SizeType.Absolute, 94));
        header.ColumnStyles.Add(new(SizeType.Percent, 100));
        header.RowStyles.Add(new(SizeType.Absolute, 38));
        header.RowStyles.Add(new(SizeType.Absolute, 22));
        header.RowStyles.Add(new(SizeType.Absolute, 22));
        var title = TextLabel(Path.GetFileName(destination), font: LayoutFont(18, FontStyle.Bold));
        _details.SetToolTip(title, title.Text);
        header.Controls.Add(title, 0, 0);
        header.SetColumnSpan(title, 2);
        Uri.TryCreate(source, UriKind.Absolute, out var uri);
        AddPath(header, 1, _("Source"), uri?.Host is { Length: > 0 } host ? host : _("Video stream"),
            uri?.Host is { Length: > 0 } ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped) : null);
        AddPath(header, 2, _("Destination"), destination, destination);
        layout.Controls.Add(header, 0, 0);

        var transfer = Grid(2);
        transfer.ColumnStyles.Add(new(SizeType.Percent, 100));
        transfer.ColumnStyles.Add(new(SizeType.Absolute, 110));
        transfer.RowStyles.Add(new(SizeType.Absolute, 21));
        transfer.RowStyles.Add(new(SizeType.Absolute, 45));
        transfer.RowStyles.Add(new(SizeType.Absolute, 14));
        transfer.RowStyles.Add(new(SizeType.Percent, 100));
        _phase.Font = LayoutFont(10, FontStyle.Bold);
        _phase.Margin = new(2, 0, 2, 0);
        _percentage.Font = LayoutFont(23, FontStyle.Bold);
        _percentage.ForeColor = Accent;
        _status.ForeColor = TextColor;
        _status.TextAlign = ContentAlignment.TopLeft;
        transfer.Controls.Add(_phase, 0, 0);
        transfer.Controls.Add(_status, 0, 1);
        transfer.Controls.Add(_percentage, 1, 0);
        transfer.SetRowSpan(_percentage, 2);
        _progress.Margin = new(2, 2, 2, 4);
        transfer.Controls.Add(_progress, 0, 2);
        transfer.SetColumnSpan(_progress, 2);

        var metrics = Grid(3);
        metrics.Margin = new(0, 5, 0, 0);
        metrics.ColumnStyles.Add(new(SizeType.Percent, 45));
        metrics.ColumnStyles.Add(new(SizeType.Percent, 27));
        metrics.ColumnStyles.Add(new(SizeType.Percent, 28));
        metrics.RowStyles.Add(new(SizeType.Absolute, 18));
        metrics.RowStyles.Add(new(SizeType.Percent, 100));
        AddMetric(metrics, 0, TextLabel(_("Downloaded"), Muted), _downloaded);
        AddMetric(metrics, 1, _rateCaption, _rate);
        AddMetric(metrics, 2, _timingCaption, _timing);
        transfer.Controls.Add(metrics, 0, 3);
        transfer.SetColumnSpan(metrics, 2);
        layout.Controls.Add(Section(transfer, new Padding(15, 8, 15, 7)), 0, 1);

        var details = Grid(1);
        details.RowStyles.Add(new(SizeType.Absolute, 26));
        details.RowStyles.Add(new(SizeType.Absolute, 25));
        details.RowStyles.Add(new(SizeType.Absolute, 17));
        details.RowStyles.Add(new(SizeType.Percent, 100));
        details.Controls.Add(TextLabel(_("Download chunks"), font: LayoutFont(12, FontStyle.Bold)), 0, 0);
        var legend = Flow();
        AddLegend(legend, _("Cached"), Cached);
        AddLegend(legend, _("Downloaded"), Accent);
        AddLegend(legend, _("Receiving"), Active);
        AddLegend(legend, _("Missing"), Missing);
        details.Controls.Add(legend, 0, 1);
        _map.Margin = new(2, 0, 2, 7);
        _map.Paint += PaintMap;
        details.Controls.Add(_map, 0, 2);
        var list = new Panel { Dock = DockStyle.Fill, Margin = new(2, 2, 2, 0), BackColor = Field };
        _emptyChunks.Text = _("Chunk details will appear here when available.");
        _emptyChunks.ForeColor = Muted;
        list.Controls.Add(_chunks);
        list.Controls.Add(_emptyChunks);
        _chunks.Visible = false;
        details.Controls.Add(list, 0, 3);
        layout.Controls.Add(Section(details, new Padding(15, 8, 15, 12)), 0, 2);

        var footer = Grid(2);
        footer.ColumnStyles.Add(new(SizeType.Percent, 54));
        footer.ColumnStyles.Add(new(SizeType.Percent, 46));
        footer.RowStyles.Add(new(SizeType.Absolute, 35));
        footer.RowStyles.Add(new(SizeType.Percent, 100));
        var settings = Flow();
        AddSetting(settings, _("Connections"), _connections);
        AddSetting(settings, _("Speed limit (KiB/s)"), _speed);
        _details.SetToolTip(_speed, _("0 means unlimited."));
        footer.Controls.Add(settings, 0, 0);
        footer.SetColumnSpan(settings, 2);
        _openFolder.Dock = DockStyle.Fill;
        _openFolder.AutoEllipsis = true;
        _openFolder.ForeColor = Muted;
        _openFolder.UseVisualStyleBackColor = false;
        footer.Controls.Add(_openFolder, 0, 1);
        var buttons = Flow();
        buttons.FlowDirection = FlowDirection.RightToLeft;
        buttons.Padding = new(0, 4, 0, 0);
        _pause.Visible = false;
        _close.AccessibleDescription = _("Cancel the save and keep downloaded chunks.");
        buttons.Controls.AddRange([_start, _pause, _close]);
        footer.Controls.Add(buttons, 1, 1);
        layout.Controls.Add(footer, 0, 3);
    }

    void AddPath(TableLayoutPanel header, int row, string caption, string text, string? hint)
    {
        header.Controls.Add(TextLabel(caption, Muted), 0, row);
        var value = TextLabel(text);
        header.Controls.Add(value, 1, row);
        if (hint != null) _details.SetToolTip(value, hint);
    }

    void AddMetric(TableLayoutPanel metrics, int column, Label caption, Label value)
    {
        caption.ForeColor = Muted;
        value.Font = LayoutFont(10, FontStyle.Bold);
        caption.Dock = value.Dock = DockStyle.Fill;
        caption.TextAlign = value.TextAlign = ContentAlignment.TopLeft;
        caption.Margin = value.Margin = new(2, 0, 2, 0);
        metrics.Controls.Add(caption, column, 0);
        metrics.Controls.Add(value, column, 1);
    }

    static TableLayoutPanel Grid(int columns) => new() { Dock = DockStyle.Fill, Margin = Padding.Empty, ColumnCount = columns };
    static FlowLayoutPanel Flow() => new() { Dock = DockStyle.Fill, Margin = Padding.Empty, WrapContents = false };

    static Label TextLabel(string text, Color? color = null, Font? font = null)
    {
        var label = new Label { Text = text, ForeColor = color ?? TextColor, Dock = DockStyle.Fill,
            AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, UseMnemonic = false, Margin = new(2, 0, 2, 0) };
        if (font != null) label.Font = font;
        return label;
    }

    static Panel Section(Control contents, Padding inset)
    {
        var section = new SectionPanel { Dock = DockStyle.Fill, Padding = inset, Margin = new(0, 0, 0, 12) };
        contents.BackColor = Card;
        section.Controls.Add(contents);
        return section;
    }

    static void AddLegend(FlowLayoutPanel legend, string text, Color color)
    {
        var swatch = new Swatch(color) { Size = new(9, 9), Margin = new(2, 7, 5, 0) };
        var caption = new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new(0, 3, 16, 0), UseMnemonic = false };
        legend.Controls.Add(swatch);
        legend.Controls.Add(caption);
    }

    static void AddSetting(FlowLayoutPanel settings, string caption, NumericUpDown input)
    {
        settings.Controls.Add(new Label { Text = caption, AutoSize = true, ForeColor = Muted, Margin = new(2, 8, 5, 0) });
        input.ForeColor = TextColor;
        input.BackColor = Field;
        input.BorderStyle = BorderStyle.FixedSingle;
        input.Margin = new(0, 4, 20, 0);
        input.AccessibleName = caption;
        settings.Controls.Add(input);
    }

    void ConfigureChunkList()
    {
        _chunks.BackColor = Field;
        _chunks.ForeColor = TextColor;
        _chunks.BorderStyle = BorderStyle.None;
        _chunks.HeaderStyle = ColumnHeaderStyle.Nonclickable;
        _chunks.HideSelection = false;
        _chunks.OwnerDraw = true;
        _chunks.AccessibleName = _("Download chunks");
        foreach (string title in new[] { _("Chunk"), _("Start"), _("Size"), _("Received"), _("Status") })
            _chunks.Columns.Add(title);
        _chunks.RetrieveVirtualItem += (sender, e) => {
            if (_snapshot == null || e.ItemIndex >= _snapshot.Chunks.Length) { e.Item = new(""); return; }
            var chunk = _snapshot.Chunks[e.ItemIndex];
            e.Item = new ListViewItem([(e.ItemIndex + 1).ToString(CultureInfo.CurrentCulture), Bytes(chunk.Start),
                Bytes(chunk.Length), Bytes(chunk.Received), chunk.Cached ? _("Cached / resumed") : _(chunk.State)]);
        };
        _chunks.DrawColumnHeader += (sender, e) => {
            var bounds = e.Bounds;
            if (e.ColumnIndex == _chunks.Columns.Count - 1) bounds.Width = Math.Max(bounds.Width, _chunks.ClientSize.Width - bounds.Left);
            DrawCell(e.Graphics, bounds, e.Header?.Text, Border, Muted);
        };
        _chunks.DrawSubItem += (sender, e) => {
            bool selected = e.Item?.Selected == true;
            Color background = selected ? Color.FromArgb(43, 66, 89) : e.ItemIndex % 2 == 0 ? Field : Card;
            var snapshot = _snapshot;
            Color foreground = e.ColumnIndex == 4 && snapshot != null && e.ItemIndex < snapshot.Chunks.Length
                ? ChunkColor(snapshot.Chunks[e.ItemIndex], Muted) : e.ColumnIndex == 0 ? Muted : TextColor;
            DrawCell(e.Graphics, e.Bounds, e.SubItem?.Text, background, foreground);
        };
        _chunks.ClientSizeChanged += (_, _) => SizeLastColumn();
    }

    void DrawCell(Graphics graphics, Rectangle bounds, string? text, Color background, Color foreground)
    {
        using var fill = new SolidBrush(background);
        graphics.FillRectangle(fill, bounds);
        var inset = Rectangle.Inflate(bounds, -9, 0);
        TextRenderer.DrawText(graphics, text, Font, inset, foreground,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    void SizeLastColumn()
    {
        if (_chunks.Columns.Count == 0) return;
        int width = _chunks.Width - SystemInformation.VerticalScrollBarWidth - 2;
        if (width <= 0) return;
        _chunks.BeginUpdate();
        try
        {
            _chunks.Columns[0].Width = width * 12 / 100;
            for (int index = 1; index <= 3; index++) _chunks.Columns[index].Width = width * 20 / 100;
            _chunks.Columns[4].Width = width - _chunks.Columns.Cast<ColumnHeader>().Take(4).Sum(c => c.Width);
        }
        finally { _chunks.EndUpdate(); }
    }

    static Color ChunkColor(DownloadChunk chunk, Color missing) => chunk.Cached ? Cached :
        chunk.Received == chunk.Length ? Accent : chunk.Received > 0 ? Active : missing;

    void PaintMap(object? sender, PaintEventArgs e)
    {
        Rectangle bounds = _map.ClientRectangle;
        if (bounds.Width < 2 || bounds.Height < 2) return;
        using var outline = Rounded(bounds, bounds.Height / 2F);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = new SolidBrush(Missing);
        e.Graphics.FillPath(track, outline);
        if (_snapshot is not { Total: > 0 } snapshot) return;
        using var cached = new SolidBrush(Cached);
        using var downloaded = new SolidBrush(Accent);
        using var receiving = new SolidBrush(Active);
        double scale = (double)bounds.Width / snapshot.Total;
        e.Graphics.SetClip(outline);
        foreach (var chunk in snapshot.Chunks)
        {
            if (chunk.Received == 0) continue;
            float x = (float)(chunk.Start * scale);
            float width = (float)Math.Max(chunk.Received * scale, 1);
            var fill = chunk.Cached ? cached : chunk.Received == chunk.Length ? downloaded : receiving;
            e.Graphics.FillRectangle(fill, x, 0, width, bounds.Height);
        }
        e.Graphics.ResetClip();
    }

    static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    sealed class SectionPanel : Panel
    {
        public SectionPanel() { BackColor = Surface; DoubleBuffered = true; ResizeRedraw = true; }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Rounded(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 12 * DeviceDpi / 96F);
            using var fill = new SolidBrush(Card);
            using var border = new Pen(Border);
            e.Graphics.FillPath(fill, shape);
            e.Graphics.DrawPath(border, shape);
        }
    }

    sealed class Swatch(Color color) : Control
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            using var fill = new SolidBrush(color);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillEllipse(fill, 0, 0, Width - 1, Height - 1);
        }
    }

    sealed class DownloadProgressStrip : Control
    {
        int _value;
        float? _animation;
        public int Value { get => _value; set { _value = Math.Clamp(value, 0, 1000); Invalidate(); } }
        public float? Animation { get => _animation; set { _animation = value; Invalidate(); } }
        public DownloadProgressStrip() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Card);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Rounded(ClientRectangle, Height / 2F);
            using var track = new SolidBrush(Missing);
            e.Graphics.FillPath(track, shape);
            float x = Animation is float position ? (Width + Width / 5F) * position - Width / 5F : 0;
            float width = Animation.HasValue ? Width / 5F : Width * Value / 1000F;
            if (width <= 0) return;
            e.Graphics.SetClip(shape);
            using var fill = new LinearGradientBrush(ClientRectangle, Cached, Accent, LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(fill, x, 0, width, Height);
            e.Graphics.ResetClip();
        }
    }

    sealed class DownloadButton : Button
    {
        readonly bool _primary;

        public DownloadButton(bool primary = false)
        {
            _primary = primary;
            FlatStyle = FlatStyle.Flat;
            UseVisualStyleBackColor = false;
            AutoSize = true;
            MinimumSize = new(88, 35);
            Padding = new(12, 5, 12, 5);
            Margin = new(7, 4, 0, 4);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Surface);
            if (Width < 2 || Height < 2) return;
            bool hover = Enabled && ClientRectangle.Contains(PointToClient(MousePosition));
            Color background = Enabled && _primary ? (hover ? Color.FromArgb(143, 239, 211) : Accent) : hover ? Border : Card;
            Color foreground = !Enabled ? Muted : _primary ? Surface : TextColor;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var shape = Rounded(new RectangleF(0.5F, 0.5F, Width - 1, Height - 1), 8 * DeviceDpi / 96F);
            using var fill = new SolidBrush(background);
            using var border = new Pen(Enabled && _primary ? background : Border);
            e.Graphics.FillPath(fill, shape);
            e.Graphics.DrawPath(border, shape);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, foreground,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5), foreground, background);
        }
    }
}
