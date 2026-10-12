"""
Client Python per le API del servizio di watermark (/api/v1).

Solo libreria standard: funziona con qualsiasi Python 3.9+ senza installare nulla.

    from wmapi import WatermarkClient
    c = WatermarkClient("https://watermark.example.ts.net", token)
    c.watermark_file("report.docx", text="CONFIDENTIAL", save_to="report_watermark.docx")
    job = c.create_job("video.mp4", text="{user} {date}")
    job = c.wait_job(job["id"])
    c.job_result(job["id"], save_to="video_watermark.mp4")
"""
from __future__ import annotations

import base64
import http.client
import json
import mimetypes
import os
import re
import ssl
import time
import uuid
from dataclasses import dataclass, field
from typing import Any, Dict, List, Optional
from urllib.parse import quote, unquote, urlsplit

USER_AGENT = "wmtest/1.0"


class ApiError(Exception):
    """Risposta di errore (ProblemDetails) o errore di rete."""

    def __init__(self, status: int, title: str, detail: str = "", response: Optional["Response"] = None):
        super().__init__(f"{status} {title}: {detail}" if detail else f"{status} {title}")
        self.status = status
        self.title = title
        self.detail = detail
        self.response = response


@dataclass
class Timing:
    """Tempi di una richiesta, in secondi dall'inizio."""
    connect: float = 0.0     # apertura connessione (0 se riusata)
    sent: float = 0.0        # corpo della richiesta inviato (fine upload)
    headers: float = 0.0     # intestazioni della risposta ricevute
    done: float = 0.0        # risposta completa

    @property
    def upload(self) -> float:
        return self.sent

    @property
    def server(self) -> float:
        """Attesa tra fine upload e inizio risposta: elaborazione sul server + rete."""
        return max(0.0, self.headers - self.sent)

    @property
    def download(self) -> float:
        return max(0.0, self.done - self.headers)


@dataclass
class Response:
    status: int
    reason: str
    headers: Dict[str, str]
    body: bytes
    timing: Timing
    bytes_sent: int = 0

    def header(self, name: str, default: str = "") -> str:
        return self.headers.get(name.lower(), default)

    def json(self) -> Any:
        return json.loads(self.body.decode("utf-8")) if self.body else None

    @property
    def is_problem(self) -> bool:
        return self.header("content-type").startswith("application/problem+json")

    def problem(self) -> Dict[str, Any]:
        try:
            data = self.json()
            return data if isinstance(data, dict) else {}
        except ValueError:
            return {}

    @property
    def filename(self) -> Optional[str]:
        """Nome del file dalla Content-Disposition (filename* ha la precedenza)."""
        cd = self.header("content-disposition")
        m = re.search(r"filename\*=UTF-8''([^;]+)", cd, re.I)
        if m:
            return unquote(m.group(1))
        m = re.search(r'filename="?([^";]+)"?', cd, re.I)
        return m.group(1) if m else None

    @property
    def warnings(self) -> List[str]:
        raw = self.header("x-watermark-warnings")
        if not raw:
            return []
        return json.loads(base64.b64decode(raw).decode("utf-8"))

    def raise_for_status(self) -> "Response":
        if self.status >= 400:
            p = self.problem()
            raise ApiError(self.status, p.get("title") or self.reason, p.get("detail") or "", self)
        return self


@dataclass
class Multipart:
    """Corpo multipart/form-data già codificato: si può riusare per molte richieste (test di carico)."""
    body: bytes
    content_type: str
    file_name: str = ""
    file_size: int = 0

    @staticmethod
    def build(fields: Dict[str, Any], file_name: Optional[str] = None, file_bytes: Optional[bytes] = None,
              file_field: str = "file") -> "Multipart":
        boundary = "----wmtest" + uuid.uuid4().hex
        out = bytearray()
        for k, v in fields.items():
            if v is None:
                continue
            if isinstance(v, bool):
                v = "true" if v else "false"
            out += f"--{boundary}\r\nContent-Disposition: form-data; name=\"{k}\"\r\n\r\n".encode()
            out += str(v).encode("utf-8") + b"\r\n"
        if file_name is not None and file_bytes is not None:
            ctype = mimetypes.guess_type(file_name)[0] or "application/octet-stream"
            safe = file_name.replace('"', "%22")
            out += (f"--{boundary}\r\nContent-Disposition: form-data; name=\"{file_field}\"; "
                    f"filename=\"{safe}\"\r\nContent-Type: {ctype}\r\n\r\n").encode("utf-8")
            out += file_bytes + b"\r\n"
        out += f"--{boundary}--\r\n".encode()
        return Multipart(bytes(out), f"multipart/form-data; boundary={boundary}",
                         file_name or "", len(file_bytes or b""))


def options_to_fields(text: Optional[str] = None, angle: Optional[float] = None, size: Optional[float] = None,
                      color: Optional[str] = None, opacity: Optional[float] = None, font: Optional[str] = None,
                      output: Optional[str] = None, tile: Optional[bool] = None, pptx_slides: Optional[bool] = None,
                      user: Optional[str] = None, email: Optional[str] = None, **extra: Any) -> Dict[str, Any]:
    fields = {"text": text, "angle": angle, "size": size, "color": color, "opacity": opacity, "font": font,
              "output": output, "tile": tile, "pptxSlides": pptx_slides, "user": user, "email": email}
    fields.update(extra)
    return {k: v for k, v in fields.items() if v is not None}


def parse_time(value: Optional[str]):
    """Data ISO 8601 del servizio (.NET usa 7 cifre decimali, Python < 3.11 ne accetta al massimo 6)."""
    from datetime import datetime
    if not value:
        return None
    v = value.replace("Z", "+00:00")
    m = re.match(r"^(.*?\.\d{1,6})\d*(.*)$", v)
    if m:
        v = m.group(1) + m.group(2)
    return datetime.fromisoformat(v)


class WatermarkClient:
    """
    Client per /api/v1. Una istanza tiene aperta una connessione (keep-alive):
    nei test di carico ogni thread usa la propria istanza.
    """

    def __init__(self, base_url: str, token: Optional[str], timeout: float = 600, insecure: bool = False,
                 api_key_header: bool = False):
        u = urlsplit(base_url.rstrip("/"))
        if u.scheme not in ("http", "https"):
            raise ValueError("The URL must start with http:// or https://")
        self.scheme, self.host, self.port = u.scheme, u.hostname, u.port
        self.prefix = u.path.rstrip("/")
        self.token = token
        self.timeout = timeout
        self.api_key_header = api_key_header
        self._ssl = ssl._create_unverified_context() if insecure else ssl.create_default_context()
        self._conn: Optional[http.client.HTTPConnection] = None

    # ------------------------------------------------------------------ trasporto

    def close(self) -> None:
        if self._conn is not None:
            self._conn.close()
            self._conn = None

    def _connection(self) -> http.client.HTTPConnection:
        if self._conn is None:
            if self.scheme == "https":
                self._conn = http.client.HTTPSConnection(self.host, self.port, timeout=self.timeout, context=self._ssl)
            else:
                self._conn = http.client.HTTPConnection(self.host, self.port, timeout=self.timeout)
        return self._conn

    def auth_headers(self, token: Optional[str] = None) -> Dict[str, str]:
        token = self.token if token is None else token
        if not token:
            return {}
        return {"X-Api-Key": token} if self.api_key_header else {"Authorization": f"Bearer {token}"}

    def request(self, method: str, path: str, body: Optional[bytes] = None, content_type: Optional[str] = None,
                headers: Optional[Dict[str, str]] = None, auth: bool = True, token: Optional[str] = None,
                save_to: Optional[str] = None) -> Response:
        """Esegue la richiesta e misura i tempi. Non solleva eccezioni per gli stati HTTP di errore."""
        h = {"User-Agent": USER_AGENT, "Accept": "*/*"}
        if auth:
            h.update(self.auth_headers(token))
        if content_type:
            h["Content-Type"] = content_type
        if headers:
            h.update(headers)
        url = self.prefix + path

        last_error: Optional[Exception] = None
        for attempt in range(2):  # un nuovo tentativo solo se la connessione keep-alive era stata chiusa
            reused = self._conn is not None
            t0 = time.perf_counter()
            timing = Timing()
            try:
                conn = self._connection()
                if conn.sock is None:
                    conn.connect()
                    timing.connect = time.perf_counter() - t0
                conn.putrequest(method, url, skip_accept_encoding=True)
                for k, v in h.items():
                    conn.putheader(k, v)
                conn.putheader("Content-Length", str(len(body or b"")))
                conn.endheaders()
                if body:
                    view = memoryview(body)
                    for i in range(0, len(view), 256 * 1024):
                        conn.send(view[i:i + 256 * 1024])
                timing.sent = time.perf_counter() - t0
                resp = conn.getresponse()
                timing.headers = time.perf_counter() - t0
                if save_to:
                    data = b""
                    with open(save_to, "wb") as f:
                        while True:
                            chunk = resp.read(256 * 1024)
                            if not chunk:
                                break
                            f.write(chunk)
                else:
                    data = resp.read()
                timing.done = time.perf_counter() - t0
                headers = {k.lower(): v for k, v in resp.getheaders()}
                if headers.get("connection", "").lower() == "close":
                    self.close()
                return Response(resp.status, resp.reason, headers, data, timing, len(body or b""))
            except (http.client.RemoteDisconnected, BrokenPipeError, ConnectionResetError) as e:
                self.close()
                last_error = e
                if not reused or attempt == 1:
                    raise
            except Exception:
                self.close()
                raise
        raise last_error  # pragma: no cover

    # ------------------------------------------------------------------ API

    def openapi(self) -> str:
        return self.request("GET", "/api/v1/openapi.yaml", auth=False).raise_for_status().body.decode("utf-8")

    def capabilities(self) -> Dict[str, Any]:
        return self.request("GET", "/api/v1/capabilities").raise_for_status().json()

    def post_file(self, path: str, file_name: str, file_bytes: bytes, **options: Any) -> Response:
        mp = Multipart.build(options_to_fields(**options), file_name, file_bytes)
        return self.request("POST", path, mp.body, mp.content_type)

    def watermark(self, file_name: str, file_bytes: bytes, **options: Any) -> Response:
        """Elaborazione sincrona. Restituisce la risposta (il file è in .body)."""
        return self.post_file("/api/v1/watermark", file_name, file_bytes, **options)

    def watermark_file(self, path: str, save_to: Optional[str] = None, **options: Any) -> Response:
        with open(path, "rb") as f:
            data = f.read()
        resp = self.watermark(os.path.basename(path), data, **options).raise_for_status()
        if save_to:
            with open(save_to, "wb") as f:
                f.write(resp.body)
        return resp

    def create_job(self, path: str, **options: Any) -> Dict[str, Any]:
        with open(path, "rb") as f:
            data = f.read()
        return self.post_file("/api/v1/jobs", os.path.basename(path), data, **options).raise_for_status().json()

    def get_job(self, job_id: str) -> Dict[str, Any]:
        return self.request("GET", f"/api/v1/jobs/{quote(job_id)}").raise_for_status().json()

    def list_jobs(self) -> List[Dict[str, Any]]:
        return self.request("GET", "/api/v1/jobs").raise_for_status().json()

    def wait_job(self, job_id: str, poll: float = 1.0, timeout: float = 3600) -> Dict[str, Any]:
        """Attende la fine del lavoro (succeeded o failed)."""
        deadline = time.monotonic() + timeout
        while True:
            job = self.get_job(job_id)
            if job["status"] in ("succeeded", "failed"):
                return job
            if time.monotonic() > deadline:
                raise TimeoutError(f"Job {job_id} still {job['status']} after {timeout:.0f}s")
            time.sleep(poll)

    def job_result(self, job_id: str, save_to: Optional[str] = None) -> Response:
        return self.request("GET", f"/api/v1/jobs/{quote(job_id)}/result", save_to=save_to).raise_for_status()

    def delete_job(self, job_id: str) -> Response:
        return self.request("DELETE", f"/api/v1/jobs/{quote(job_id)}")
