using System.Security.Cryptography;
using System.Runtime.InteropServices;
using MpvNet;
using static MpvNet.Global;

Translator.Current = new IdentityTranslator();
void Check(bool condition, string description)
{
    if (!condition) throw new Exception(description);
    Console.WriteLine("PASS: " + description);
}

string url = args[0], root = args[1], mode = args[2];
Directory.CreateDirectory(root);
if (mode == "media")
{
    string config = Path.Combine(root, "config");
    Directory.CreateDirectory(config);
    Environment.SetEnvironmentVariable("MPVNET_HOME", config);
    File.WriteAllText(Path.Combine(config, "mpv.conf"), "vo=null\nao=null\npause=yes\ndemuxer-max-bytes=512KiB\ndemuxer-max-back-bytes=512KiB\n");
    NativeLibrary.SetDllImportResolver(typeof(MpvClient).Assembly, (name, assembly, path) =>
        name == "libmpv-2.dll" ? NativeLibrary.Load(Environment.GetEnvironmentVariable("MPVNET_TEST_LIBMPV")!) : IntPtr.Zero);
    Player.Init(IntPtr.Zero, false);
    var loop = Task.Run(Player.EventLoop);
    try
    {
        Player.CommandV("loadfile", url);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (Player.GetPropertyDouble("duration") <= 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        var selection = BufferedVideo.Capture(Player, Player.PlaybackGeneration);
        Check(selection.ByteCache != null, "Playback retains the original HTTP byte chunks");
        var initial = selection.ByteCache!.Snapshot(new());
        Check(initial.Chunks.Any(c => c.Received == c.Length) && initial.Received < initial.Total,
            "The real player has both cached and missing byte chunks");
        Check(selection.Tracks.All(t => t.Source == url), "Captured stream URLs remain the original source");
        string destination = Path.Combine(root, "media.mkv");
        await BufferedVideo.SaveAsync(Player, destination, selection, control: new() { Connections = 8 }).WaitAsync(TimeSpan.FromSeconds(45));
        Check(new FileInfo(destination).Length > 1000, "Partial playback cache completes into a verified MKV");
        Check(!Directory.Exists(destination + ".mpvnet-parts"), "Successful completion removes the resume files");
        Check(Player.GetPropertyBool("pause"), "Parallel exporting preserves the foreground pause state");
    }
    finally { Player.CommandV("quit"); await loop.WaitAsync(TimeSpan.FromSeconds(5)); Player.Destroy(); }
    return;
}
var options = new Dictionary<string, string> { ["http-header-fields"] = "X-Test: secret\\,value", ["user-agent"] = "mpvnet-download-test" };
using var canceled = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var token = canceled.Token;
using var source = await VideoRangeSource.OpenAsync(url, options, Path.Combine(root, "cache"), token);
if (mode is "no-range" or "encoded" or "no-validator")
{
    Check(source == null, "Unsupported byte ranges safely fall back");
    return;
}
Check(source != null, "HTTP byte-range support detected");
var control = new VideoDownloadControl { Connections = 8 };
string parts = Path.Combine(root, "parts");
if (mode == "invalid" || mode == "changed")
{
    try
    {
        await source!.DownloadAsync(parts, control, null, token);
        throw new Exception("Unsafe response was accepted");
    }
    catch (InvalidDataException) { Check(true, "Invalid ranges and changed entities cannot complete"); }
    return;
}
if (mode == "pause")
{
    control.Pause();
    var task = source!.DownloadAsync(parts, control, null, token);
    await Task.Delay(400, token);
    Check(source.Snapshot(control).Received == 0, "Paused download makes no chunk requests");
    control.Resume();
    while (source.Snapshot(control).Received < 8 * 65536) await Task.Delay(15, token);
    control.Pause();
    await Task.Delay(200, token);
    long paused = source.Snapshot(control).Received;
    await Task.Delay(400, token);
    Check(source.Snapshot(control).Received == paused && !task.IsCompleted, "All eight connections stop reading while paused");
    control.Resume();
    await task;
}
else if (mode == "restart")
{
    // Seed one complete playback block. Cancellation must keep it and discard partial blocks.
    await source!.GetBlockAsync(0, null, token);
    using var stop = new CancellationTokenSource();
    var task = source.DownloadAsync(parts, control, null, stop.Token);
    await Task.Delay(100, token);
    stop.Cancel();
    try { await task; throw new Exception("Cancellation was ignored"); } catch (OperationCanceledException) { }
    Check(Directory.GetFiles(parts, "*.block", SearchOption.AllDirectories).Length >= 1, "Cancellation keeps complete chunks on disk");
    using var resumed = await VideoRangeSource.OpenAsync(url, options, Path.Combine(root, "new-process-cache"), token);
    await resumed!.DownloadAsync(parts, control, null, token);
    Check(resumed.Snapshot(control).Chunks[0].Cached, "A new session reuses persisted chunks");
    await resumed.AssembleAsync(Path.Combine(root, "output.bin"), token);
}
else if (mode == "speed")
{
    control.BytesPerSecond = 128 * 1024;
    using var stop = new CancellationTokenSource();
    var task = source!.DownloadAsync(parts, control, null, stop.Token);
    await Task.Delay(1200, token);
    long received = source.Snapshot(control).Received;
    Check(received is > 40 * 1024 and < 200 * 1024, "The speed limit applies to the combined eight connections");
    stop.Cancel();
    try { await task; } catch (OperationCanceledException) { }
    return;
}
else
{
    await source!.GetBlockAsync(0, null, token);
    await source.DownloadAsync(parts, control, null, token);
    Check(source.Snapshot(control).Chunks[0].Cached, "Playback bytes appear as already complete");
}
if (mode != "restart") await source!.AssembleAsync(Path.Combine(root, "output.bin"), token);
byte[] expected = new byte[12 * VideoRangeSource.BlockSize + 123];
for (int i = 0; i < expected.Length; i++) expected[i] = (byte)(i % 251);
Check(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(root, "output.bin"), token)).SequenceEqual(SHA256.HashData(expected)),
    "Every byte including the final short chunk matches the source");

sealed class IdentityTranslator : ITranslator
{
    public string Gettext(string text) => text;
    public string GetParticularString(string context, string text) => text;
}
