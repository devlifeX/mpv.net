using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace MpvNet;

// Loopback adapter retains original bytes while mpv keeps its original playlist URL.
// Only direct, seekable media with transport options we can preserve is redirected.
internal sealed class PlaybackByteCache : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource _stop = new();
    readonly ConcurrentDictionary<string, VideoRangeSource> _sources = new();
    readonly string _root = Path.Combine(Path.GetTempPath(), "mpvnet-byte-cache", Guid.NewGuid().ToString("N"));
    public PlaybackByteCache() { _listener.Start(); var listening = ListenAsync(); }

    public static bool IsDirectMedia(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme is "http" or "https" && new[] { ".mkv", ".mp4", ".webm", ".mov", ".m4v", ".avi", ".m4a" }
            .Contains(Path.GetExtension(uri.AbsolutePath).ToLowerInvariant());

    public VideoRangeSource? Find(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        _sources.TryGetValue(uri.AbsolutePath, out var source) ? source : null;

    public async Task<string?> AddAsync(string url, Dictionary<string, string> options)
    {
        if (!IsDirectMedia(url)) return null;
        string key = "/" + Guid.NewGuid().ToString("N") + Path.GetExtension(new Uri(url).AbsolutePath);
        var source = await VideoRangeSource.OpenAsync(url, options, Path.Combine(_root, Guid.NewGuid().ToString("N")), _stop.Token);
        if (source == null) return null;
        _sources[key] = source;
        return "http://127.0.0.1:" + ((IPEndPoint)_listener.LocalEndpoint).Port + key;
    }

    async Task ListenAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                var serving = ServeAsync(client);
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested) { }
    }

    async Task ServeAsync(TcpClient client)
    {
        using (client)
        using (var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            requestTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var token = requestTimeout.Token;
            try
            {
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
                string[] request = (await reader.ReadLineAsync(token) ?? "").Split(' ');
                if (request.Length != 3 || request[0] is not ("GET" or "HEAD")) return;
                long start = 0, end;
                long? requestedEnd = null;
                bool partial = false;
                int headerSize = 0;
                while (await reader.ReadLineAsync(token) is { Length: > 0 } line)
                {
                    headerSize += line.Length;
                    if (headerSize > 32768) return;
                    if (line.StartsWith("Range: bytes=", StringComparison.OrdinalIgnoreCase))
                    {
                        var range = line[13..].Split('-');
                        if (range.Length != 2 || !long.TryParse(range[0], out start)) return;
                        if (range[1] != "")
                        {
                            if (!long.TryParse(range[1], out long last)) return;
                            requestedEnd = last;
                        }
                        partial = true;
                    }
                }
                if (!_sources.TryGetValue(request[1], out var source)) return;
                // mpv requests open-ended ranges. Serve through EOF, preserving random seek support.
                end = Math.Min(requestedEnd ?? long.MaxValue, source.Length - 1);
                if (start < 0 || start > end)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */" +
                        source.Length + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), token);
                    return;
                }
                string headers = partial ? "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + start + "-" + end + "/" + source.Length + "\r\n" : "HTTP/1.1 200 OK\r\n";
                headers += "Accept-Ranges: bytes\r\nContent-Type: application/octet-stream\r\nContent-Length: " +
                    (end - start + 1) + "\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), token);
                if (request[0] == "HEAD") return;
                // Streaming can last longer than request parsing, but each upstream read has its own timeout.
                requestTimeout.CancelAfter(Timeout.InfiniteTimeSpan);
                byte[] buffer = new byte[65536];
                while (start <= end)
                {
                    int block = (int)(start / VideoRangeSource.BlockSize);
                    string file = await source.GetBlockAsync(block, null, token);
                    await using var input = File.OpenRead(file);
                    input.Position = start % VideoRangeSource.BlockSize;
                    int read;
                    while (start <= end && (read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - start + 1)), token)) > 0)
                    {
                        await stream.WriteAsync(buffer.AsMemory(0, read), token);
                        start += read;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or System.Net.Http.HttpRequestException)
            { /* The downstream closes on seek; failed upstream transfers remain incomplete. */ }
        }
    }

    public void Dispose()
    {
        _stop.Cancel(); _listener.Stop();
        foreach (var source in _sources.Values) source.Dispose();
        // Readers may still be unwinding; leave cleanup to the next process if files are busy.
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch (IOException) { }
    }
}
