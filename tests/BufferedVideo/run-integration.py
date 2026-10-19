"""Run with a .NET 10 SDK, ffmpeg, ffprobe, and libmpv (see README.md)."""
import functools
import http.server
import json
import os
from pathlib import Path
import subprocess
import tempfile
import threading

project = Path(__file__).resolve().parent / "BufferedVideo.csproj"
dotnet = os.environ.get("DOTNET", "dotnet")
subprocess.run([dotnet, "build", str(project), "--nologo", "-v:q"], check=True)


def probe(path):
    return json.loads(subprocess.check_output([
        "ffprobe", "-v", "error",
        "-show_entries", "stream=codec_type,codec_name:format=duration",
        "-of", "json", str(path),
    ]))


def video_frames(path):
    data = subprocess.check_output([
        "ffmpeg", "-v", "error", "-i", str(path), "-map", "0:v:0", "-f", "framemd5", "-",
    ])
    return [line.split(b",")[-1].strip() for line in data.splitlines() if not line.startswith(b"#")]


def audio_packets(path):
    # Normalize AAC transport headers, which change when TS is remuxed to MKV.
    data = subprocess.check_output([
        "ffmpeg", "-v", "error", "-i", str(path), "-map", "0:a:0", "-c:a", "copy", "-f", "adts", "-",
    ])
    packets = []
    while data:
        length = ((data[3] & 3) << 11) | (data[4] << 3) | (data[5] >> 5)
        header = 7 if data[1] & 1 else 9
        assert header < length <= len(data), "Invalid ADTS frame"
        packets.append(data[header:length])
        data = data[length:]
    return packets


with tempfile.TemporaryDirectory(prefix="mpvnet-cache-test-") as directory:
    root = Path(directory)
    source = root / "source.mp4"
    subprocess.run([
        "ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=160x90:rate=24",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "4",
        "-c:v", "libx264", "-preset", "ultrafast", "-g", "24", "-c:a", "aac",
        "-movflags", "+faststart", str(source),
    ], check=True)
    transport = root / "source.ts"
    subprocess.run([
        "ffmpeg", "-v", "error", "-i", str(source), "-c", "copy", "-f", "mpegts", str(transport),
    ], check=True)

    for fixture in [source, transport]:
        requests = []

        class Handler(http.server.SimpleHTTPRequestHandler):
            def do_GET(self):
                requests.append(self.path)
                super().do_GET()

            def log_message(self, *args):
                pass

        handler = functools.partial(Handler, directory=str(root))
        server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        output = root / fixture.suffix[1:]
        process = subprocess.Popen([
            dotnet, "run", "--no-build", "--project", str(project), "--",
            f"http://127.0.0.1:{server.server_port}/{fixture.name}", str(output),
        ], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True,
            env={**os.environ, "MPVNET_TEST_OFFLINE": "1"})
        offline = False
        try:
            for line in process.stdout:
                print(line.rstrip(), flush=True)
                if "PASS: The complete watched network video remains cached" in line:
                    server.shutdown()
                    server.server_close()
                    thread.join()
                    offline = True
                    request_count = len(requests)
                    process.stdin.write("offline\n")
                    process.stdin.flush()
            assert process.wait(timeout=10) == 0, "C# integration checks failed"
            assert offline and len(requests) == request_count, "Export must work after HTTP is shut down"
        finally:
            if process.poll() is None:
                process.kill()
                process.wait()
            if not offline:
                server.shutdown()
                server.server_close()
                thread.join()

        saved = output / "saved فیلم with spaces.mkv"
        original, exported = probe(fixture), probe(saved)
        assert original["streams"] == exported["streams"], "Audio/video codecs must be preserved"
        assert abs(float(original["format"]["duration"]) - float(exported["format"]["duration"])) < 0.1
        assert video_frames(fixture) == video_frames(saved), "Every decoded video frame must match"
        before, after = audio_packets(fixture), audio_packets(saved)
        # mpv's demuxer can omit this fixture's initial AAC priming packet.
        assert after == before or after == before[1:], "AAC payloads must match without re-encoding"
        print(f"PASS: {fixture.suffix} video frames and AAC payloads preserved; export succeeded with HTTP offline")
