using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MpvNet;

public sealed record DownloadChunk(long Start, long Length, long Received, bool Cached, string State);
public sealed record DownloadSnapshot(long Total, long Received, double BytesPerSecond, bool Paused,
    DownloadChunk[] Chunks);

// Pause stops new reads; completed blocks survive pause, retries and application restarts.
public sealed class VideoDownloadControl
{
    volatile TaskCompletionSource? _resume;
    readonly object _rateLock = new();
    double _nextRead;
    public bool IsPaused => _resume != null;
    public long BytesPerSecond { get; set; }
    public int Connections { get; set; } = 8;
    public void Pause() => Interlocked.CompareExchange(ref _resume,
        new(TaskCreationOptions.RunContinuationsAsynchronously), null);
    public void Resume() => Interlocked.Exchange(ref _resume, null)?.TrySetResult();
    public async Task WaitAsync(CancellationToken token)
    {
        while (_resume is { } resume) await resume.Task.WaitAsync(token);
        token.ThrowIfCancellationRequested();
    }

    internal async Task ThrottleAsync(int bytes, CancellationToken token)
    {
        long rate = BytesPerSecond;
        if (rate <= 0) return;
        double target;
        lock (_rateLock)
        {
            double now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            _nextRead = Math.Max(now, _nextRead) + (double)bytes / rate;
            target = _nextRead;
        }
        while (BytesPerSecond > 0)
        {
            await WaitAsync(token);
            double remaining = target - Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            if (remaining <= 0) return;
            // Check cancellation, resume and an edited speed limit frequently.
            if (BytesPerSecond != rate) { lock (_rateLock) _nextRead = 0; return; }
            await Task.Delay(TimeSpan.FromSeconds(Math.Min(0.1, remaining)), token);
        }
    }
}

// Byte ranges are reusable only when the server proves they belong to the same representation.
// A demuxed mpv packet cache cannot be substituted for these bytes.
public sealed class VideoRangeSource : IDisposable
{
    public const int BlockSize = 1024 * 1024;
    readonly HttpClient _http;
    readonly string _url;
    readonly string _folder;
    readonly string _identity;
    readonly string? _etag;
    readonly DateTimeOffset? _modified;
    readonly ConcurrentDictionary<int, SemaphoreSlim> _locks = new();
    readonly ConcurrentDictionary<int, long> _received = new();
    readonly ConcurrentDictionary<int, string> _states = new();
    readonly HashSet<int> _seeded = [];
    long _networkBytes;
    bool _downloadStarted;
    public long Length { get; }
    public int Count => checked((int)((Length - 1) / BlockSize + 1));
    public string Url => _url;

    VideoRangeSource(HttpClient http, string url, long length, string? etag, DateTimeOffset? modified,
        string identity, string folder)
    {
        _http = http; _url = url; Length = length; _etag = etag; _modified = modified;
        _identity = identity; _folder = folder;
        Directory.CreateDirectory(folder);
    }

    public static async Task<VideoRangeSource?> OpenAsync(string url, Dictionary<string, string> options,
        string folder, CancellationToken token)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        // Let libmpv handle transports whose semantics cannot be reproduced by HttpClient.
        if (options.Any(p => p.Value is not ("" or "no") && p.Key is
            "cookies" or "cookies-file" or "http-proxy" or "tls-ca-file" or "tls-cert-file" or "tls-key-file" or "demuxer-lavf-o") ||
            options.GetValueOrDefault("tls-verify") == "no") return null;
        var http = new HttpClient(new SocketsHttpHandler {
            AutomaticDecompression = DecompressionMethods.None, MaxConnectionsPerServer = 32,
            ConnectTimeout = TimeSpan.FromSeconds(15), UseCookies = false
        }) { Timeout = TimeSpan.FromSeconds(45) };
        try
        {
            foreach (var pair in options)
            {
                if (pair.Key == "user-agent") http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", pair.Value);
                if (pair.Key == "referrer") http.DefaultRequestHeaders.TryAddWithoutValidation("Referer", pair.Value);
                if (pair.Key == "http-header-fields")
                    foreach (string field in SplitHeaders(pair.Value))
                    {
                        int colon = field.IndexOf(':');
                        if (colon > 0) http.DefaultRequestHeaders.TryAddWithoutValidation(field[..colon].Trim(), field[(colon + 1)..].Trim());
                    }
            }
            // Framing headers are owned by this downloader, even if supplied in mpv.conf.
            foreach (string name in new[] { "Range", "If-Range", "Accept-Encoding", "Host" }) http.DefaultRequestHeaders.Remove(name);
            http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            var range = response.Content.Headers.ContentRange;
            if (response.StatusCode != HttpStatusCode.PartialContent || range?.Unit != "bytes" ||
                range.From != 0 || range.To != 0 || range.Length is not > 0 ||
                response.Content.Headers.ContentEncoding.Count != 0) { http.Dispose(); return null; }
            long length = range.Length.Value;
            if ((length - 1) / BlockSize >= 1000000) { http.Dispose(); return null; }
            string? etag = response.Headers.ETag is { IsWeak: false } tag ? tag.ToString() : null;
            var modified = response.Content.Headers.LastModified;
            if (etag == null && modified == null) { http.Dispose(); return null; }
            string identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url + "\n" + length + "\n" +
                etag + "\n" + modified + "\n" + string.Join("\n", options.OrderBy(p => p.Key)))));
            return new(http, url, length, etag, modified, identity, folder);
        }
        catch { http.Dispose(); throw; }
    }

    static IEnumerable<string> SplitHeaders(string value)
    {
        var field = new StringBuilder();
        bool escaped = false;
        foreach (char c in value)
        {
            if (escaped) { field.Append(c); escaped = false; }
            else if (c == '\\') escaped = true;
            else if (c == ',') { yield return field.ToString(); field.Clear(); }
            else field.Append(c);
        }
        if (escaped) field.Append('\\');
        yield return field.ToString();
    }

    long BlockLength(int index) => Math.Min(BlockSize, Length - (long)index * BlockSize);
    string BlockPath(int index) => Path.Combine(_folder, index + ".block");
    bool HasBlock(int index) => File.Exists(BlockPath(index)) && new FileInfo(BlockPath(index)).Length == BlockLength(index);

    public DownloadSnapshot Snapshot(VideoDownloadControl control, double speed = 0) => new(Length,
        Enumerable.Range(0, Count).Sum(i => HasBlock(i) ? BlockLength(i) : _received.GetValueOrDefault(i)), speed,
        control.IsPaused, Enumerable.Range(0, Count).Select(i => new DownloadChunk((long)i * BlockSize,
            BlockLength(i), HasBlock(i) ? BlockLength(i) : _received.GetValueOrDefault(i), _seeded.Contains(i) || (!_downloadStarted && HasBlock(i)),
            HasBlock(i) ? "Complete" : _states.GetValueOrDefault(i, "Waiting"))).ToArray());

    public async Task<string> GetBlockAsync(int index, VideoDownloadControl? control, CancellationToken token)
    {
        if (index < 0 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        var gate = _locks.GetOrAdd(index, _ => new(1));
        await gate.WaitAsync(token);
        try
        {
            if (HasBlock(index)) return BlockPath(index);
            long start = (long)index * BlockSize, size = BlockLength(index);
            for (int attempt = 0; ; attempt++)
            {
                string partial = BlockPath(index) + ".tmp";
                try
                {
                    if (control != null) await control.WaitAsync(token);
                    _received[index] = 0;
                    _states[index] = attempt == 0 ? "Receiving" : "Retrying";
                    using var request = new HttpRequestMessage(HttpMethod.Get, _url);
                    request.Headers.Range = new RangeHeaderValue(start, start + size - 1);
                    if (_etag != null) request.Headers.IfRange = new RangeConditionHeaderValue(EntityTagHeaderValue.Parse(_etag));
                    else if (_modified != null) request.Headers.IfRange = new RangeConditionHeaderValue(_modified.Value);
                    using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
                    response.EnsureSuccessStatusCode();
                    var range = response.Content.Headers.ContentRange;
                    if (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start ||
                        range.To != start + size - 1 || range.Length != Length ||
                        response.Content.Headers.ContentEncoding.Count != 0 ||
                        (_etag != null && response.Headers.ETag?.ToString() != _etag) ||
                        (_etag == null && _modified != null && response.Content.Headers.LastModified != _modified))
                        throw new InvalidDataException(_("The server changed the file or returned an invalid byte range. Open the video again."));
                    await using (var input = await response.Content.ReadAsStreamAsync(token))
                    await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
                    {
                        byte[] buffer = new byte[65536];
                        long read = 0;
                        while (read < size)
                        {
                            if (control != null) await control.WaitAsync(token);
                            int requested = (int)Math.Min(buffer.Length, size - read);
                            if (control?.BytesPerSecond > 0)
                                requested = (int)Math.Min(requested, Math.Max(1, control.BytesPerSecond / Math.Max(1, control.Connections) / 10));
                            if (control != null) await control.ThrottleAsync(requested, token);
                            int count = await input.ReadAsync(buffer.AsMemory(0, requested), token);
                            if (count == 0) throw new IOException(_("The connection ended before the chunk was complete."));
                            await output.WriteAsync(buffer.AsMemory(0, count), token);
                            read += count; _received[index] = read;
                            Interlocked.Add(ref _networkBytes, count);
                        }
                        if (await input.ReadAsync(buffer.AsMemory(0, 1), token) != 0)
                            throw new InvalidDataException(_("The server returned extra bytes outside the requested range."));
                    }
                    File.Move(partial, BlockPath(index), true);
                    _states[index] = "Complete";
                    return BlockPath(index);
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException &&
                    ex is not InvalidDataException && !token.IsCancellationRequested && attempt < 3)
                {
                    _states[index] = "Retrying";
                    await Task.Delay(500 * (attempt + 1), token);
                }
                finally { if (File.Exists(partial)) File.Delete(partial); }
            }
        }
        finally { gate.Release(); }
    }

    public async Task DownloadAsync(string parts, VideoDownloadControl control,
        IProgress<DownloadSnapshot>? progress, CancellationToken token)
    {
        _downloadStarted = true;
        Directory.CreateDirectory(parts);
        string manifest = Path.Combine(parts, "identity");
        bool reuse = File.Exists(manifest) && await File.ReadAllTextAsync(manifest, token) == _identity;
        // A new identity gets a new directory; stale data cannot silently contaminate the output.
        string blocks = Path.Combine(parts, _identity);
        Directory.CreateDirectory(blocks);
        await File.WriteAllTextAsync(manifest, _identity, token);
        for (int i = 0; i < Count; i++)
        {
            string saved = Path.Combine(blocks, i + ".block");
            if (reuse && File.Exists(saved) && new FileInfo(saved).Length == BlockLength(i) && !HasBlock(i))
                File.Copy(saved, BlockPath(i), true);
            if (HasBlock(i)) _seeded.Add(i);
        }
        control.Connections = Math.Clamp(control.Connections, 1, 32);
        using var ended = CancellationTokenSource.CreateLinkedTokenSource(token);
        long previous = Interlocked.Read(ref _networkBytes);
        var timer = Stopwatch.StartNew();
        async Task Report()
        {
            try
            {
                while (true)
                {
                    long now = Interlocked.Read(ref _networkBytes);
                    double speed = (now - previous) / Math.Max(0.001, timer.Elapsed.TotalSeconds);
                    previous = now; timer.Restart();
                    progress?.Report(Snapshot(control, speed));
                    await Task.Delay(250, ended.Token);
                }
            }
            catch (OperationCanceledException) when (ended.IsCancellationRequested) { }
        }
        var reporting = Report();
        try
        {
            await Parallel.ForEachAsync(Enumerable.Range(0, Count), new ParallelOptions {
                MaxDegreeOfParallelism = control.Connections, CancellationToken = token
            }, async (i, ct) => {
                string file = await GetBlockAsync(i, control, ct);
                string saved = Path.Combine(blocks, i + ".block");
                if (!File.Exists(saved))
                {
                    File.Copy(file, saved + ".tmp", true);
                    File.Move(saved + ".tmp", saved, true);
                }
            });
        }
        finally { ended.Cancel(); await reporting; progress?.Report(Snapshot(control)); }
    }

    public async Task AssembleAsync(string filename, CancellationToken token)
    {
        await using var output = new FileStream(filename, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
        for (int i = 0; i < Count; i++)
        {
            if (!HasBlock(i)) throw new IOException(_("A required download chunk is missing."));
            await using var input = File.OpenRead(BlockPath(i));
            await input.CopyToAsync(output, token);
        }
        if (output.Length != Length) throw new IOException(_("The downloaded file size is incorrect."));
    }

    public void Dispose() => _http.Dispose();
}
