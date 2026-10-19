using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using MpvNet.Help;

using static MpvNet.Native.LibMpv;

namespace MpvNet;

public sealed class PlaybackCheckpoint
{
    public int Version { get; set; } = 1;
    public string Path { get; set; } = "";
    public double Position { get; set; }
    public bool Seekable { get; set; }
    public Dictionary<string, string> Options { get; set; } = new();
    public List<RecoveryTrack> ExternalTracks { get; set; } = new();
}

public sealed class RecoveryTrack
{
    public int Id { get; set; }
    public string Type { get; set; } = "";
    public string Filename { get; set; } = "";
}

public sealed class PlaybackRecovery
{
    static readonly string[] ExtraOptions = [
        "volume", "mute", "speed", "aid", "vid", "sid", "secondary-sid", "sub-visibility",
        "audio-delay", "sub-delay", "sub-scale", "sub-pos", "video-zoom", "video-pan-x",
        "video-pan-y", "video-rotate", "video-aspect-override", "edition",
        "http-header-fields", "user-agent", "referrer", "http-proxy", "cookies", "cookies-file",
        "tls-verify", "ytdl", "ytdl-format", "ytdl-raw-options", "demuxer-lavf-o"
    ];

    readonly MpvClient _player;
    readonly string _filename;
    readonly string[] _options;
    readonly object _sync = new();
    bool _loaded;
    bool _ready;
    bool _reportedWriteError;
    bool _restoreFailed;
    long _lastSave;
    PlaybackCheckpoint? _pending;
    bool _pendingStarted;

    public event Action<string>? Failed;

    // Error dialogs must not block libmpv's event loop while holding the checkpoint lock.
    void ReportFailure(string message) => TaskHelp.Run(() => Failed?.Invoke(message));

    public PlaybackRecovery(MpvClient player, string filename)
    {
        _player = player;
        _filename = filename;
        _options = player.GetPropertyString("watch-later-options").Split(',')
            .Concat(ExtraOptions).Where(name => name != "start" && name != "pause" && name != "")
            .Distinct().ToArray();

        player.StartFile += () => {
            lock (_sync)
            {
                _loaded = _ready = _restoreFailed = false;
                _pendingStarted = _pending != null;
            }
        };
        player.FileLoaded += OnFileLoaded;
        player.PlaybackRestart += OnPlaybackRestart;
        player.EndFile += reason => {
            lock (_sync)
            {
                _loaded = _ready = false;
                if (reason == mpv_end_file_reason.MPV_END_FILE_REASON_ERROR && _pending != null && _pendingStarted)
                {
                    _pending = null;
                    ReportFailure(_("Could not reconnect. Your saved position is still available; try Continue again when the source is reachable."));
                }
            }
        };
        player.ObservePropertyDouble("time-pos", value => SaveCheckpoint());
        foreach (string option in new[] { "pause", "speed", "volume", "mute", "aid", "sid", "secondary-sid", "audio-delay", "sub-delay" })
            player.ObserveProperty(option, () => SaveCheckpoint(true));
    }

    public bool SaveCheckpoint(bool force = false)
    {
        lock (_sync)
        {
            if (!_loaded || !_ready || _pending != null ||
                (!force && Stopwatch.GetElapsedTime(_lastSave) < TimeSpan.FromSeconds(1)))
                return false;

            try
            {
                long generation = _player.PlaybackGeneration;
                string path = _player.GetPropertyString("path");
                string entry = _player.GetPropertyString("playlist-entry-id");
                string originalPath = path;
                if (path == "" || path == "-" || _player.GetPropertyString("seeking") == "yes" ||
                    _player.GetPropertyString("eof-reached") == "yes" ||
                    !_player.TryGetPropertyString("time-pos", out var time) ||
                    !double.TryParse(time, CultureInfo.InvariantCulture, out double position) ||
                    !double.IsFinite(position) || position < 0)
                    return false;

                if (!path.Contains("://"))
                    path = System.IO.Path.GetFullPath(path);

                var state = new PlaybackCheckpoint {
                    Path = path, Position = position,
                    Seekable = _player.GetPropertyString("seekable") == "yes"
                };
                foreach (string option in _options)
                    if (_player.TryGetPropertyString(option, out string value))
                        state.Options[option] = value;

                using var tracks = JsonDocument.Parse(_player.GetPropertyString("track-list"));
                foreach (var track in tracks.RootElement.EnumerateArray())
                    if (track.TryGetProperty("external-filename", out var file) && file.GetString() is string external)
                        state.ExternalTracks.Add(new RecoveryTrack {
                            Id = track.GetProperty("id").GetInt32(),
                            Type = track.GetProperty("type").GetString()!, Filename = external
                        });

                if (_player.PlaybackGeneration != generation || _player.GetPropertyString("path") != originalPath ||
                    _player.GetPropertyString("playlist-entry-id") != entry)
                    return false;

                WriteCheckpoint(_filename, state);
                _lastSave = Stopwatch.GetTimestamp();
                _reportedWriteError = false;
                return true;
            }
            catch (Exception ex)
            {
                if (!_reportedWriteError)
                    ReportFailure(_("Could not save the playback recovery checkpoint.") + BR + ex.Message);
                _reportedWriteError = true;
                return false;
            }
        }
    }

    public async Task ContinueAsync()
    {
        string[] command;
        lock (_sync)
        {
            SaveCheckpoint(true);
            var state = ReadCheckpoint(_filename) ?? throw new InvalidOperationException(_("There is no saved playback to continue."));

            if (_loaded && _ready && _player.GetPropertyString("path") == state.Path &&
                _player.GetPropertyString("pause") == "yes" &&
                _player.GetPropertyString("paused-for-cache") != "yes" && _player.GetPropertyString("eof-reached") != "yes")
            {
                _player.SetPropertyBool("pause", false);
                return;
            }

            if (!state.Seekable && state.Position > 0)
                throw new InvalidOperationException(_("This stream does not support seeking to the saved position. A live source must provide a DVR or seekable recording to resume exactly."));

            // Explicit per-file options take precedence over stale/disabled watch-later state.
            var options = state.Options.Where(pair => _options.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value);
            options["start"] = state.Position.ToString("R", CultureInfo.InvariantCulture);
            options["pause"] = "no";
            options["hr-seek"] = "yes";
            options["resume-playback"] = "no";
            string encoded = string.Join(",", options.Select(pair => pair.Key + "=%" +
                Encoding.UTF8.GetByteCount(pair.Value) + "%" + pair.Value));

            using var commands = JsonDocument.Parse(_player.GetPropertyString("command-list"));
            bool hasIndex = commands.RootElement.EnumerateArray().First(item => item.GetProperty("name").GetString() == "loadfile")
                .GetProperty("args").GetArrayLength() >= 4;
            command = hasIndex ? ["loadfile", state.Path, "replace", "-1", encoded] : ["loadfile", state.Path, "replace", encoded];
            _pending = state;
            _pendingStarted = false;
            _restoreFailed = false;
            _ready = false;
        }

        mpv_error result = await _player.CommandVAsync(command);
        if (result < 0)
        {
            lock (_sync) { _pending = null; }
            throw new InvalidOperationException(_("Could not continue playback. Your checkpoint has been kept.") + BR + GetError(result));
        }
    }

    void OnFileLoaded()
    {
        lock (_sync)
        {
            _loaded = true;
            if (_pending == null || !_pendingStarted)
                return;

            if (_player.GetPropertyString("path") != _pending.Path)
            {
                _pending = null;
                return;
            }

            foreach (var external in _pending.ExternalTracks)
            {
                if (external.Type is not ("audio" or "sub" or "video"))
                    continue;
                using var tracks = JsonDocument.Parse(_player.GetPropertyString("track-list"));
                if (!tracks.RootElement.EnumerateArray().Any(track =>
                    track.GetProperty("type").GetString() == external.Type &&
                    track.TryGetProperty("external-filename", out var file) && file.GetString() == external.Filename))
                    _player.CommandV(external.Type == "sub" ? "sub-add" : external.Type == "audio" ? "audio-add" : "video-add",
                        external.Filename, "auto");

                using var updated = JsonDocument.Parse(_player.GetPropertyString("track-list"));
                var restored = updated.RootElement.EnumerateArray().FirstOrDefault(track =>
                    track.GetProperty("type").GetString() == external.Type &&
                    track.TryGetProperty("external-filename", out var file) && file.GetString() == external.Filename);
                if (restored.ValueKind == JsonValueKind.Undefined)
                {
                    _restoreFailed = true;
                    _player.SetPropertyBool("pause", true);
                    ReportFailure(_("An external audio, video or subtitle file could not be restored. Your checkpoint has been kept."));
                    continue;
                }
                foreach (string selection in new[] { "aid", "vid", "sid", "secondary-sid" })
                    if (_pending.Options.TryGetValue(selection, out var id) && id == external.Id.ToString(CultureInfo.InvariantCulture) &&
                        (selection == "aid" && external.Type == "audio" || selection == "vid" && external.Type == "video" ||
                         selection is "sid" or "secondary-sid" && external.Type == "sub"))
                        _player.SetPropertyString(selection, restored.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    void OnPlaybackRestart()
    {
        lock (_sync)
        {
            // Restart events from the old stream can still be queued when Continue is clicked.
            if (!_loaded || _restoreFailed || _pending != null && !_pendingStarted)
                return;
            if (_pending != null)
            {
                if (!_player.TryGetPropertyString("time-pos", out var value) ||
                    !double.TryParse(value, CultureInfo.InvariantCulture, out double actual) ||
                    Math.Abs(actual - _pending.Position) > 1)
                {
                    _restoreFailed = true;
                    _player.SetPropertyBool("pause", true);
                    ReportFailure(_("The source could not resume at the saved position. Your checkpoint has been kept."));
                    return;
                }
                _pending = null;
            }
            _ready = true;
            SaveCheckpoint(true);
        }
    }

    public static PlaybackCheckpoint? ReadCheckpoint(string filename)
    {
        foreach (string path in new[] { filename, filename + ".bak" })
        {
            try
            {
                if (File.Exists(path))
                {
                    var state = JsonSerializer.Deserialize<PlaybackCheckpoint>(File.ReadAllBytes(path));
                    if (state != null && state.Version == 1 && !string.IsNullOrWhiteSpace(state.Path) &&
                        double.IsFinite(state.Position) && state.Position >= 0 && state.Options != null && state.ExternalTracks != null)
                        return state;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return null;
    }

    public static void WriteCheckpoint(string filename, PlaybackCheckpoint state)
    {
        string temporary = filename + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(filename)!);
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Options = FileOptions.WriteThrough };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(stream, state);
                stream.Flush(true);
            }
            if (File.Exists(filename))
                File.Replace(temporary, filename, filename + ".bak");
            else
                File.Move(temporary, filename);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }
}
