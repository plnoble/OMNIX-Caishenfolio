from __future__ import annotations

import argparse
import json
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Any
from urllib.parse import urlparse

from caishenfolio_core.server.app import AnalyticsApp, dispatch, validate_bind_host


def create_handler(app: AnalyticsApp):
    class Handler(BaseHTTPRequestHandler):
        def log_message(self, format: str, *args: Any) -> None:  # noqa: A003
            return

        def _read_json(self) -> dict[str, Any]:
            length = int(self.headers.get("Content-Length", "0"))
            if length <= 0:
                return {}
            raw = self.rfile.read(length)
            if not raw:
                return {}
            payload = json.loads(raw.decode("utf-8"))
            if not isinstance(payload, dict):
                raise ValueError("JSON body must be an object.")
            return payload

        def _write(self, status: int, payload: dict[str, object]) -> None:
            body = json.dumps(payload, ensure_ascii=False).encode("utf-8")
            self.send_response(status)
            self.send_header("Content-Type", "application/json; charset=utf-8")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self) -> None:  # noqa: N802
            parsed = urlparse(self.path)
            status, payload = dispatch(app, "GET", parsed.path, parsed.query)
            self._write(status, payload)

        def do_POST(self) -> None:  # noqa: N802
            parsed = urlparse(self.path)
            try:
                body = self._read_json()
            except (ValueError, json.JSONDecodeError) as exc:
                self._write(400, {"error": str(exc)})
                return
            status, payload = dispatch(app, "POST", parsed.path, parsed.query, body)
            self._write(status, payload)

    return Handler


def serve(host: str = "127.0.0.1", port: int = 8765) -> None:
    host = validate_bind_host(host)
    app = AnalyticsApp()
    health = app.health()
    server = ThreadingHTTPServer((host, port), create_handler(app))
    print(
        json.dumps(
            {
                "event": "listening",
                "host": host,
                "port": port,
                "product": "Caishenfolio",
                "phase": health.get("phase"),
                "market_provider": health.get("market_provider"),
                "market_provider_ready": health.get("market_provider_ready"),
                "market_data_synthetic": health.get("market_data_synthetic"),
            },
            ensure_ascii=False,
        )
    )
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


def main(argv: list[str] | None = None) -> None:
    parser = argparse.ArgumentParser(description="Caishenfolio Analytics Core (stdlib HTTP)")
    parser.add_argument("--host", default="127.0.0.1")
    parser.add_argument("--port", type=int, default=8765)
    args = parser.parse_args(argv)
    serve(args.host, args.port)


if __name__ == "__main__":
    main()
