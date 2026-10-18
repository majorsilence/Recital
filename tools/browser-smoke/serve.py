# Serves a published browser build for run.sh: index.html is replaced by a probe that reports, through
# /ready, how long the runtime took to start and what it put in the page; /hold.png is answered only then,
# so headless Firefox takes its screenshot after start-up.
import http.server, threading, sys, os
ready = threading.Event()
class H(http.server.SimpleHTTPRequestHandler):
    extensions_map = {**http.server.SimpleHTTPRequestHandler.extensions_map, '.wasm': 'application/wasm', '.js': 'text/javascript', '.mjs': 'text/javascript', '.json': 'application/json', '.dat': 'application/octet-stream'}
    def do_GET(self):
        if self.path.startswith('/ready'):
            print('READY', self.path, flush=True); ready.set()
            self.send_response(204); self.end_headers(); return
        if self.path.startswith('/hold.png'):
            ready.wait(170)
            self.send_response(200); self.send_header('Content-Type','image/gif'); self.end_headers()
            self.wfile.write(bytes.fromhex('47494638396101000100800000000000ffffff21f90401000000002c00000000010001000002024401003b')); return
        return super().do_GET()
    def log_message(self, *a): pass
os.chdir(sys.argv[1])
http.server.ThreadingHTTPServer(('127.0.0.1', int(sys.argv[2])), H).serve_forever()
