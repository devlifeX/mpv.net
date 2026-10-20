"""Crash/restart and offline/retry tests with real libmpv; see README.md."""
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import http.server
import time

project = Path(__file__).resolve().parent / "PlaybackRecovery.csproj"
dotnet = os.environ.get("DOTNET", "dotnet")
subprocess.run([dotnet, "build", str(project), "--nologo", "-v:q"], check=True)

with tempfile.TemporaryDirectory(prefix="mpvnet-continue-test-") as directory:
    root = Path(directory)
    source = root / "source.mp4"
    subtitle = root / "captions فارسی.srt"
    subtitle.write_text("1\n00:00:00,000 --> 00:00:29,000\nRecovery subtitle\n", encoding="utf-8")
    subprocess.run([
        "ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24",
        "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "30",
        "-c:v", "libx264", "-preset", "ultrafast", "-g", "24", "-c:a", "aac",
        "-movflags", "+faststart", str(source),
    ], check=True)
    online = True
    requests = []

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            requests.append((online, self.headers.get("X-Recovery"), self.path))
            if not online:
                self.send_error(503)
                return
            if self.headers.get("X-Recovery") != "checkpoint-test":
                self.send_error(403)
                return
            data = source.read_bytes()
            start, end = 0, len(data) - 1
            partial = self.headers.get("Range", "")
            if partial:
                first, last = partial.removeprefix("bytes=").split("-", 1)
                start = int(first)
                end = int(last) if last else end
                self.send_response(206)
                self.send_header("Content-Range", f"bytes {start}-{end}/{len(data)}")
            else:
                self.send_response(200)
            self.send_header("Accept-Ranges", "bytes")
            self.send_header("Content-Type", "video/mp4")
            self.send_header("Content-Length", str(end - start + 1))
            self.end_headers()
            try:
                for offset in range(start, end + 1, 4096):
                    if not online:
                        return
                    self.wfile.write(data[offset:min(offset + 4096, end + 1)])
                    time.sleep(0.02)
            except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
                pass

        def log_message(self, *args):
            pass

    server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
    thread = threading.Thread(target=server.serve_forever, daemon=True)
    thread.start()
    processes = []

    def launch(mode, config, *extra):
        process = subprocess.Popen([
            dotnet, str(project.parent / "bin/Debug/net10.0/PlaybackRecovery.dll"),
            mode, str(config), *map(str, extra),
        ], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        processes.append(process)
        watchdog = threading.Timer(45, process.kill)
        watchdog.daemon = True
        watchdog.start()
        return process

    try:
        config = root / "record"
        config.mkdir()
        (config / "mpv.conf").write_text("vo=null\nao=null\nhwdec=no\nresume-playback=no\nnetwork-timeout=2\n")
        url = f"http://127.0.0.1:{server.server_port}/source.mp4?token=a,b&title=movie"
        recorder = launch("record", config, url, subtitle)
        ready = False
        for line in recorder.stdout:
            print(line.rstrip(), flush=True)
            if "CHECKPOINT_READY" in line:
                ready = True
                recorder.kill()
                recorder.wait(timeout=5)
                break
        assert ready, "Recorder failed before its durable checkpoint"
        state = json.loads((config / "continue.json").read_text())
        assert abs(state["Position"] - 9.125) < 0.1
        print("PASS: Original process killed without quit or shutdown handlers", flush=True)
        for mode in os.environ.get("RECOVERY_TEST_MODES", "restore,retry,disconnect,missing").split(","):
            restored_config = root / mode
            shutil.copytree(config, restored_config)
            if mode == "disconnect":
                with (restored_config / "mpv.conf").open("a") as options:
                    options.write("cache=no\ndemuxer-readahead-secs=1\ndemuxer-max-bytes=1MiB\n")
            if mode == "missing":
                broken = json.loads((restored_config / "continue.json").read_text())
                broken["ExternalTracks"][0]["Filename"] = str(root / "missing-subtitle.srt")
                (restored_config / "continue.json").write_text(json.dumps(broken))
            online = mode != "retry"
            first_request = len(requests)
            process = launch(mode, restored_config, url, subtitle) if mode == "disconnect" else launch(mode, restored_config)
            for line in process.stdout:
                print(line.rstrip(), flush=True)
                if "DISCONNECT_READY" in line:
                    online = False
                    process.stdin.write("offline\n")
                    process.stdin.flush()
                if "RETRY_FAILED" in line:
                    online = True
                    process.stdin.write("online\n")
                    process.stdin.flush()
            assert process.wait(timeout=5) == 0, f"{mode} checks failed"
            assert any(entry[0] and entry[1] == "checkpoint-test" and entry[2] == "/source.mp4?token=a,b&title=movie"
                       for entry in requests[first_request:]), "Original URL and headers must be used to reconnect"
        print("PASS: Crash restart and failed-network retry restore position/settings using the original URL")
    finally:
        for process in processes:
            if process.poll() is None:
                process.kill()
                process.wait()
        server.shutdown()
        server.server_close()
        thread.join()
