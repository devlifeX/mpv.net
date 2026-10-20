"""Real Windows sockets: parallelism, pause, restart, retries and hostile range responses."""
import http.server
import os
from pathlib import Path
import subprocess
import tempfile
import threading
import time

project = Path(__file__).resolve().parent / "VideoDownload.csproj"
dotnet = os.environ.get("DOTNET", "dotnet")
subprocess.run([dotnet, "build", str(project), "--nologo", "-v:q"], check=True)
data = bytes(range(251)) * ((12 * 1048576 + 123) // 251 + 1)
data = data[:12 * 1048576 + 123]
lock = threading.Lock()
active, maximum, requests, failures = 0, 0, [], set()
mode = ""


class Handler(http.server.BaseHTTPRequestHandler):
    def do_GET(self):
        global active, maximum
        if self.headers.get("X-Test") != "secret,value" or self.headers.get("User-Agent") != "mpvnet-download-test":
            self.send_error(403)
            return
        start, end = self.headers["Range"][6:].split("-")
        start, end = int(start), int(end)
        probe = end == start == 0
        with lock:
            requests.append((start, end))
            if not probe:
                active += 1
                maximum = max(maximum, active)
        try:
            if mode == "retry" and not probe and start not in failures:
                failures.add(start)
                self.send_error(503)
                return
            if mode == "no-range":
                self.send_response(200)
                self.send_header("Content-Length", str(len(data)))
                self.end_headers()
                return
            self.send_response(206)
            self.send_header("Content-Range", f"bytes {start + (1 if mode == 'invalid' and not probe else 0)}-{end}/{len(data)}")
            self.send_header("Content-Length", str(end-start+1))
            if mode != "no-validator":
                self.send_header("ETag", '"changed"' if mode == "changed" and not probe else '"fixture-v1"')
            if mode == "encoded":
                self.send_header("Content-Encoding", "gzip")
            self.end_headers()
            for offset in range(start, end + 1, 16384):
                self.wfile.write(data[offset:min(offset+16384, end+1)])
                self.wfile.flush()
                if not probe:
                    time.sleep(0.004)
        except (BrokenPipeError, ConnectionResetError, ConnectionAbortedError):
            pass
        finally:
            if not probe:
                with lock:
                    active -= 1

    def log_message(self, *args):
        pass


http.server.ThreadingHTTPServer.request_queue_size = 64
server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
threading.Thread(target=server.serve_forever, daemon=True).start()
try:
    with tempfile.TemporaryDirectory(prefix="mpvnet-ranges-") as directory:
        for mode in ["normal", "pause", "restart", "retry", "speed", "invalid", "changed", "no-range", "encoded", "no-validator"]:
            requests, failures, maximum = [], set(), 0
            subprocess.run([dotnet, str(project.parent / "bin/Debug/net10.0/VideoDownload.dll"),
                            f"http://127.0.0.1:{server.server_port}/fixture.mp4", str(Path(directory) / mode), mode],
                           check=True, timeout=40)
            if mode in ["normal", "pause", "restart", "retry"]:
                assert maximum >= 8, f"Expected eight real concurrent requests, got {maximum}"
                assert sum(start == 0 and end > 0 for start, end in requests) == (2 if mode == "retry" else 1), "Cached prefix must not be downloaded twice"
                print(f"PASS: {mode}: {maximum} simultaneous HTTP connections; cached prefix was reused", flush=True)
            while active:
                time.sleep(0.02)
finally:
    server.shutdown()
    server.server_close()
