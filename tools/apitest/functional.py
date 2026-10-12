"""
Test funzionali delle API: autenticazione, tutti i formati, opzioni, validazione, robustezza,
lavori asincroni e limiti del token. Ogni test produce pass / fail / warn / skip con un messaggio.
"""
from __future__ import annotations

import json
import os
import random
import re
import time
import traceback
from dataclasses import asdict, dataclass, field
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from checks import check_output, contains_text, ext_of
from wmapi import Multipart, Response, WatermarkClient, options_to_fields, parse_time

PASS, FAIL, WARN, SKIP = "pass", "fail", "warn", "skip"


@dataclass
class CaseResult:
    id: str
    category: str
    name: str
    status: str = PASS
    message: str = ""
    duration: float = 0.0
    http_status: Optional[int] = None
    details: List[str] = field(default_factory=list)


class Skip(Exception):
    pass


class Fail(Exception):
    pass


class Warn(Exception):
    pass


class FunctionalSuite:
    def __init__(self, url: str, token: str, samples: Path, token2: Optional[str] = None,
                 insecure: bool = False, include_limits: bool = False, max_generate_mb: int = 64,
                 timeout: float = 600, log: Callable[[str], None] = print):
        self.url, self.token, self.token2 = url, token, token2
        self.insecure = insecure
        self.c = WatermarkClient(url, token, timeout=timeout, insecure=insecure)
        self.samples_dir = samples
        self.include_limits = include_limits
        self.max_generate_mb = max_generate_mb
        self.log = log
        self.results: List[CaseResult] = []
        self.caps: Dict[str, Any] = {}
        self.marker = "WMTEST-" + "".join(random.choice("ABCDEFGHJKLMNPQRSTUVWXYZ23456789") for _ in range(6))
        self.samples: Dict[str, bytes] = {}

    # ------------------------------------------------------------------ infrastruttura

    def client(self, **kw: Any) -> WatermarkClient:
        return WatermarkClient(self.url, kw.pop("token", self.token), insecure=self.insecure, **kw)

    def sample(self, name: str) -> bytes:
        if name not in self.samples:
            p = self.samples_dir / name
            if not p.exists():
                raise Skip(f"sample '{name}' not found in {self.samples_dir}")
            self.samples[name] = p.read_bytes()
        return self.samples[name]

    def case(self, cid: str, category: str, name: str, fn: Callable[[CaseResult], Optional[str]]) -> None:
        r = CaseResult(cid, category, name)
        t0 = time.perf_counter()
        try:
            msg = fn(r)
            r.message = msg or r.message
        except Skip as e:
            r.status, r.message = SKIP, str(e)
        except Warn as e:
            r.status, r.message = WARN, str(e)
        except Fail as e:
            r.status, r.message = FAIL, str(e)
        except Exception as e:  # noqa: BLE001 - un errore imprevisto è un test fallito
            r.status, r.message = FAIL, f"{type(e).__name__}: {e}"
            r.details.append(traceback.format_exc(limit=3))
        r.duration = time.perf_counter() - t0
        self.results.append(r)
        icon = {PASS: "PASS", FAIL: "FAIL", WARN: "WARN", SKIP: "skip"}[r.status]
        self.log(f"  [{icon}] {cid:<10} {name}" + (f" - {r.message}" if r.message else ""))

    @property
    def opts(self) -> Dict[str, Any]:
        return {"text": self.marker, "opacity": 0.3}

    @property
    def native(self) -> List[str]:
        return [e.lower() for e in self.caps.get("native", [])]

    @property
    def tok(self) -> Dict[str, Any]:
        return self.caps.get("token", {})

    def need_sync(self) -> None:
        if not self.tok.get("allowSync", True):
            raise Skip("synchronous processing not allowed for this token")

    def need_async(self) -> None:
        if not self.tok.get("allowAsync", True):
            raise Skip("asynchronous jobs not allowed for this token")

    def need_free_text(self) -> None:
        if self.tok.get("fixedText"):
            raise Skip("the token has a fixed text: the text sent is ignored")

    @staticmethod
    def expect(resp: Response, r: CaseResult, *statuses: int) -> Response:
        r.http_status = resp.status
        if resp.status not in statuses:
            p = resp.problem()
            extra = f" ({p.get('title')}: {p.get('detail')})" if p else ""
            raise Fail(f"expected HTTP {'/'.join(map(str, statuses))}, got {resp.status}{extra}")
        return resp

    def sync(self, name: str, data: bytes, **options: Any) -> Response:
        return self.c.watermark(name, data, **options)

    # ------------------------------------------------------------------ esecuzione

    def run(self) -> List[CaseResult]:
        self.log(f"Functional tests against {self.url} (marker {self.marker})")
        self.log("Service")
        self.case("SVC-01", "service", "OpenAPI description is public", self.t_openapi)
        self.case("SVC-02", "service", "Capabilities with the token", self.t_caps)
        if not self.caps:
            self.log("Cannot read the capabilities: the other tests are skipped.")
            return self.results

        self.log("Authentication")
        self.case("AUTH-01", "auth", "Request without token is rejected (401)", self.t_no_token)
        self.case("AUTH-02", "auth", "Malformed token is rejected (401)", self.t_bad_token)
        self.case("AUTH-03", "auth", "Valid id with wrong secret is rejected (401)", self.t_wrong_secret)
        self.case("AUTH-04", "auth", "X-Api-Key header is accepted", self.t_api_key)
        self.case("AUTH-05", "auth", "API token cannot use the admin API", self.t_admin_forbidden)
        self.case("AUTH-06", "auth", "Jobs are not visible to another token", self.t_isolation)

        self.log("Formats (synchronous, original format)")
        for p in sorted(self.samples_dir.iterdir()):
            if p.is_file() and not p.name.startswith("."):
                self.case(f"FMT-{p.stem[:8].upper()}", "formats", f"{p.name}", lambda r, p=p: self.t_format(r, p))

        self.log("Conversion to PDF")
        for name in ["memo.docx", "legacy.doc", "budget.xlsx", "deck.pptx", "letter.odt"]:
            self.case(f"PDF-{ext_of(name)[1:].upper()}", "conversion", f"{name} -> PDF",
                      lambda r, n=name: self.t_to_pdf(r, n))

        self.log("Watermark options")
        self.case("OPT-01", "options", "Multi-line text, every line present", self.t_multiline)
        self.case("OPT-02", "options", "Placeholders {filename} and {date}", self.t_placeholders)
        self.case("OPT-03", "options", "Unicode text", self.t_unicode)
        self.case("OPT-04", "options", "Every available font", self.t_fonts)
        self.case("OPT-05", "options", "Extreme angle, size, opacity values", self.t_extremes)
        self.case("OPT-06", "options", "Tiled watermark (PDF, image, Word)", self.t_tile)
        self.case("OPT-07", "options", "PowerPoint: watermark on each slide", self.t_pptx_slides)
        self.case("OPT-08", "options", "Colour formats (#RRGGBB, #RRGGBBAA)", self.t_colors)

        self.log("Input validation (expect 400)")
        invalid = [
            ("VAL-01", "angle out of range", {"angle": 999}),
            ("VAL-02", "size out of range", {"size": 1}),
            ("VAL-03", "opacity out of range", {"opacity": 2}),
            ("VAL-04", "invalid colour", {"color": "not-a-colour"}),
            ("VAL-05", "font not in the list", {"font": "No Such Font"}),
            ("VAL-06", "invalid output", {"output": "docx"}),
        ]
        for cid, name, extra in invalid:
            self.case(cid, "validation", name, lambda r, e=extra: self.t_invalid(r, e))
        self.case("VAL-07", "validation", "empty text", lambda r: self.t_invalid(r, {"text": "   "}, free_text=True))
        self.case("VAL-08", "validation", "too many lines",
                  lambda r: self.t_invalid(r, {"text": "\n".join(f"L{i}" for i in range(self.caps.get("maxLines", 10) + 1))}, free_text=True))
        self.case("VAL-09", "validation", "text too long",
                  lambda r: self.t_invalid(r, {"text": "X" * (self.caps.get("maxTextLength", 200) + 1)}, free_text=True))
        self.case("VAL-10", "validation", "no file field", self.t_no_file)
        self.case("VAL-11", "validation", "body is not multipart", self.t_not_multipart)
        self.case("VAL-12", "validation", "empty file", self.t_empty_file)

        self.log("End user and fixed text")
        self.case("USR-01", "user", "Caller-provided user and email", self.t_user_override)
        self.case("USR-02", "user", "Default {user} is the token name", self.t_default_user)
        self.case("USR-03", "user", "Fixed text replaces the text sent", self.t_fixed_text)

        self.log("Robustness")
        self.case("ROB-01", "robustness", "Random bytes are rejected (415)", self.t_random)
        self.case("ROB-02", "robustness", "Truncated Word file fails cleanly", self.t_truncated)
        self.case("ROB-03", "robustness", "PDF disguised as .docx is detected", self.t_disguised)
        self.case("ROB-04", "robustness", "Path traversal in the file name is neutralised", self.t_traversal)
        self.case("ROB-05", "robustness", "Very long file name", self.t_long_name)
        self.case("ROB-06", "robustness", "Service still healthy", self.t_caps)

        self.log("Asynchronous jobs")
        self.case("ASY-01", "async", "Job lifecycle: queue, status, result, delete", self.t_job_lifecycle)
        self.case("ASY-02", "async", "Job with an unsupported file fails with details", self.t_job_failure)
        self.case("ASY-03", "async", "Result requested before completion", self.t_job_not_ready)
        self.case("ASY-04", "async", "Unknown job id (404)", self.t_job_unknown)
        self.case("ASY-05", "async", "Video processed as a job", self.t_job_video)
        self.case("ASY-06", "async", "Several jobs in parallel", self.t_job_parallel)

        self.log("Token limits")
        self.case("LIM-01", "limits", "Modes not allowed are rejected (403)", self.t_modes)
        self.case("LIM-02", "limits", "File above the size limit (413)", self.t_oversize)
        self.case("LIM-03", "limits", "Requests per minute (429)", self.t_rate_limit)
        self.case("LIM-04", "limits", "Active jobs limit (429)", self.t_active_jobs)
        return self.results

    # ------------------------------------------------------------------ servizio e autenticazione

    def t_openapi(self, r: CaseResult) -> str:
        resp = self.expect(self.c.request("GET", "/api/v1/openapi.yaml", auth=False), r, 200)
        if b"openapi:" not in resp.body:
            raise Fail("the response is not an OpenAPI document")
        return f"{len(resp.body):,} bytes"

    def t_caps(self, r: CaseResult) -> str:
        resp = self.expect(self.c.request("GET", "/api/v1/capabilities"), r, 200)
        self.caps = resp.json()
        t = self.tok
        return (f"token '{t.get('name')}', {len(self.native)} native formats, converter "
                f"{'on' if self.caps.get('converterAvailable') else 'off'}, "
                f"{t.get('requestsPerMinute')}/min, max {t.get('maxUploadBytes', 0) // 1048576} MB")

    def t_no_token(self, r: CaseResult) -> None:
        resp = self.expect(self.c.request("GET", "/api/v1/capabilities", auth=False), r, 401)
        if "bearer" not in resp.header("www-authenticate").lower():
            raise Warn("401 without a WWW-Authenticate: Bearer header")

    def t_bad_token(self, r: CaseResult) -> None:
        self.expect(self.c.request("GET", "/api/v1/capabilities", token="wmk_not-a-real-token"), r, 401)

    def t_wrong_secret(self, r: CaseResult) -> None:
        if not re.fullmatch(r"wmk_[0-9a-f]{16}_[0-9a-f]{64}", self.token):
            raise Skip("token format not recognised")
        last = self.token[-1]
        wrong = self.token[:-1] + ("0" if last != "0" else "1")
        self.expect(self.c.request("GET", "/api/v1/capabilities", token=wrong), r, 401)

    def t_api_key(self, r: CaseResult) -> None:
        c = self.client(api_key_header=True)
        self.expect(c.request("GET", "/api/v1/capabilities"), r, 200)

    def t_admin_forbidden(self, r: CaseResult) -> None:
        resp = self.c.request("GET", "/api/admin/tokens")
        r.http_status = resp.status
        if resp.status == 200:
            raise Fail("the admin API answered 200 to an API token")
        return None

    def t_isolation(self, r: CaseResult) -> str:
        if not self.token2:
            raise Skip("needs a second token (--token2)")
        self.need_async()
        job = self.c.post_file("/api/v1/jobs", "memo.docx", self.sample("memo.docx"), **self.opts)
        self.expect(job, r, 202)
        jid = job.json()["id"]
        other = self.client(token=self.token2)
        try:
            for path in (f"/api/v1/jobs/{jid}", f"/api/v1/jobs/{jid}/result"):
                resp = other.request("GET", path)
                if resp.status != 404:
                    raise Fail(f"{path} answered {resp.status} to another token")
            if other.request("DELETE", f"/api/v1/jobs/{jid}").status != 404:
                raise Fail("another token could delete the job")
        finally:
            self.c.wait_job(jid, poll=0.5, timeout=300)
            self.c.delete_job(jid)
        return "GET, result and DELETE answer 404 to the other token"

    # ------------------------------------------------------------------ formati

    def t_format(self, r: CaseResult, path: Path) -> str:
        self.need_sync()
        ext = path.suffix.lower()
        data = self.sample(path.name)
        resp = self.sync(path.name, data, **self.opts)
        r.http_status = resp.status

        if ext not in self.native:
            if resp.status == 415:
                hint = "convertible to PDF" if ext in [e.lower() for e in self.caps.get("pdf", [])] else "not supported"
                return f"original format not kept, as expected ({hint})"
            raise Fail(f"{ext} is not a native format: expected 415, got {resp.status}")

        self.expect(resp, r, 200)
        out_name = resp.filename or ""
        if not out_name.endswith("_watermark" + ext):
            r.details.append(f"output name: {out_name}")
        problems = check_output(path.name, data, out_name or path.name, resp.body, marker=self.marker)
        if problems:
            raise Fail("; ".join(problems))
        found = contains_text(out_name or path.name, resp.body, self.marker)
        verified = "text found" if found else "valid and modified"
        warns = resp.warnings
        return (f"{len(data):,} -> {len(resp.body):,} bytes in {resp.timing.done:.2f}s, {verified}"
                + (f", warnings: {'; '.join(warns)}" if warns else ""))

    def t_to_pdf(self, r: CaseResult, name: str) -> str:
        self.need_sync()
        if not self.caps.get("converterAvailable"):
            raise Skip("no PDF converter on the server")
        ext = ext_of(name)
        if ext not in [e.lower() for e in self.caps.get("pdf", [])]:
            raise Skip(f"{ext} cannot be converted on this server")
        data = self.sample(name)
        resp = self.expect(self.sync(name, data, output="pdf", **self.opts), r, 200)
        problems = check_output(name, data, resp.filename or "x.pdf", resp.body, expect_pdf=True)
        if problems:
            raise Fail("; ".join(problems))
        return f"{len(resp.body):,} bytes in {resp.timing.done:.2f}s"

    # ------------------------------------------------------------------ opzioni

    def _docx_with(self, r: CaseResult, text: str, name: str = "memo.docx", **extra: Any) -> bytes:
        self.need_sync()
        resp = self.expect(self.sync(name, self.sample("memo.docx"), text=text, **extra), r, 200)
        return resp.body

    def t_multiline(self, r: CaseResult) -> str:
        self.need_free_text()
        lines = [f"{self.marker}-L{i}" for i in range(1, 4)]
        out = self._docx_with(r, "\n".join(lines))
        missing = [l for l in lines if not contains_text("x.docx", out, l)]
        if missing:
            raise Fail(f"lines not found: {', '.join(missing)}")
        return "3 lines found in the headers"

    def t_placeholders(self, r: CaseResult) -> str:
        self.need_free_text()
        out = self._docx_with(r, f"{self.marker} {{filename}} {{date}}", name="placeholder-test.docx")
        if not contains_text("x.docx", out, "placeholder-test.docx"):
            raise Fail("{filename} was not replaced with the file name")
        import zipfile, io  # noqa: E401
        with zipfile.ZipFile(io.BytesIO(out)) as z:
            xml = b"".join(z.read(n) for n in z.namelist() if n.startswith("word/header"))
        if not re.search(rb"\d{2}/\d{2}/\d{4}", xml):
            raise Fail("{date} was not replaced with a date")
        return "file name and date found"

    def t_unicode(self, r: CaseResult) -> str:
        self.need_free_text()
        text = f"{self.marker} Riservato – àèìòù ß ✓"
        out = self._docx_with(r, text)
        if not contains_text("x.docx", out, text):
            raise Fail("the Unicode text was not preserved")
        return "preserved"

    def t_fonts(self, r: CaseResult) -> str:
        self.need_sync()
        fonts = self.caps.get("fonts", [])
        if not fonts:
            raise Skip("no fonts listed")
        bad = []
        for f in fonts:
            resp = self.sync("onepage.pdf", self.sample("onepage.pdf"), font=f, **self.opts)
            if resp.status != 200:
                bad.append(f"{f} ({resp.status})")
        if bad:
            raise Fail("failed: " + ", ".join(bad))
        return f"{len(fonts)} fonts OK"

    def t_extremes(self, r: CaseResult) -> str:
        self.need_sync()
        variants = [{"angle": -90}, {"angle": 0}, {"angle": 90}, {"angle": 360}, {"size": 4}, {"size": 400},
                    {"opacity": 0.05}, {"opacity": 1}]
        bad = []
        for v in variants:
            opts = {**self.opts, **v}
            for name in ("onepage.pdf", "scan.png"):
                resp = self.sync(name, self.sample(name), **opts)
                if resp.status != 200:
                    bad.append(f"{name} {v} -> {resp.status}")
        if bad:
            raise Fail("; ".join(bad))
        return f"{len(variants) * 2} combinations OK"

    def t_tile(self, r: CaseResult) -> str:
        self.need_sync()
        for name in ("onepage.pdf", "scan.png", "memo.docx"):
            data = self.sample(name)
            resp = self.expect(self.sync(name, data, tile=True, **self.opts), r, 200)
            problems = check_output(name, data, resp.filename or name, resp.body)
            if problems:
                raise Fail(f"{name}: {'; '.join(problems)}")
        return "PDF, PNG and Word OK"

    def t_pptx_slides(self, r: CaseResult) -> str:
        self.need_sync()
        if ".pptx" not in self.native:
            raise Skip("PowerPoint not supported")
        self.need_free_text()
        resp = self.expect(self.sync("deck.pptx", self.sample("deck.pptx"), pptx_slides=True, **self.opts), r, 200)
        import zipfile, io  # noqa: E401
        with zipfile.ZipFile(io.BytesIO(resp.body)) as z:
            slides = [n for n in z.namelist() if re.fullmatch(r"ppt/slides/slide\d+\.xml", n)]
            marked = [n for n in slides if self.marker.encode() in z.read(n)]
        if len(marked) != len(slides):
            raise Fail(f"watermark on {len(marked)} of {len(slides)} slides")
        return f"{len(slides)} slides marked"

    def t_colors(self, r: CaseResult) -> str:
        self.need_sync()
        for color in ("#C00000", "#1F4E8C", "#80808080"):
            resp = self.sync("onepage.pdf", self.sample("onepage.pdf"), color=color, **self.opts)
            if resp.status != 200:
                raise Fail(f"{color} -> {resp.status}")
        return "3 formats accepted"

    # ------------------------------------------------------------------ validazione

    def t_invalid(self, r: CaseResult, extra: Dict[str, Any], free_text: bool = False) -> str:
        self.need_sync()
        if free_text:
            self.need_free_text()
        opts = {**self.opts, **extra}
        resp = self.expect(self.sync("onepage.pdf", self.sample("onepage.pdf"), **opts), r, 400)
        if not resp.is_problem:
            raise Warn("400 without application/problem+json")
        return resp.problem().get("detail", "")

    def t_no_file(self, r: CaseResult) -> str:
        self.need_sync()
        mp = Multipart.build(options_to_fields(**self.opts))
        resp = self.expect(self.c.request("POST", "/api/v1/watermark", mp.body, mp.content_type), r, 400)
        return resp.problem().get("detail", "")

    def t_not_multipart(self, r: CaseResult) -> str:
        self.need_sync()
        resp = self.c.request("POST", "/api/v1/watermark", json.dumps({"text": "x"}).encode(), "application/json")
        self.expect(resp, r, 400, 415)
        return f"HTTP {resp.status}"

    def t_empty_file(self, r: CaseResult) -> str:
        self.need_sync()
        resp = self.expect(self.sync("empty.pdf", b"", **self.opts), r, 400)
        return resp.problem().get("detail", "")

    # ------------------------------------------------------------------ utente e testo fisso

    def t_user_override(self, r: CaseResult) -> str:
        self.need_sync()
        if not self.tok.get("allowUserOverride"):
            resp = self.expect(self.sync("memo.docx", self.sample("memo.docx"), user="Someone Else", **self.opts), r, 403)
            return "rejected as expected (token cannot set the user): " + resp.problem().get("detail", "")
        self.need_free_text()
        out = self._docx_with(r, f"{self.marker} {{user}} | {{email}}", user="Test User", email="test.user@example.com")
        if not contains_text("x.docx", out, f"{self.marker} Test User | test.user@example.com"):
            raise Fail("user and email not found in the watermark")
        return "user and email shown in the watermark"

    def t_default_user(self, r: CaseResult) -> str:
        self.need_free_text()
        name = self.tok.get("name") or ""
        out = self._docx_with(r, f"{self.marker} {{user}}")
        if not contains_text("x.docx", out, f"{self.marker} {name}"):
            raise Fail(f"{{user}} is not the token name '{name}'")
        return f"{{user}} = '{name}'"

    def t_fixed_text(self, r: CaseResult) -> str:
        if not self.tok.get("fixedText"):
            raise Skip("the token has no fixed text")
        out = self._docx_with(r, "SHOULD-NOT-APPEAR")
        if contains_text("x.docx", out, "SHOULD-NOT-APPEAR"):
            raise Fail("the text sent by the caller was used instead of the fixed text")
        return "the caller's text was ignored"

    # ------------------------------------------------------------------ robustezza

    def t_random(self, r: CaseResult) -> str:
        self.need_sync()
        resp = self.expect(self.sync("random.bin", os.urandom(64 * 1024), **self.opts), r, 415)
        return resp.problem().get("detail", "")

    def t_truncated(self, r: CaseResult) -> str:
        self.need_sync()
        data = self.sample("report.docx")
        resp = self.sync("truncated.docx", data[: len(data) // 2], **self.opts)
        r.http_status = resp.status
        if resp.status in (400, 415, 422):
            return f"HTTP {resp.status}: {resp.problem().get('detail', '')}"
        if resp.status == 500:
            raise Warn("HTTP 500: handled, but a 4xx would tell the caller the file is the problem")
        raise Fail(f"unexpected HTTP {resp.status}")

    def t_disguised(self, r: CaseResult) -> str:
        self.need_sync()
        resp = self.expect(self.sync("disguised.docx", self.sample("onepage.pdf"), **self.opts), r, 200)
        name = resp.filename or ""
        if not name.endswith(".pdf"):
            raise Fail(f"output name '{name}': the real format (PDF) was not detected")
        if not any("extension" in w.lower() for w in resp.warnings):
            raise Warn("processed as PDF, but no warning about the extension")
        return f"processed as PDF ({name}) with a warning"

    def t_traversal(self, r: CaseResult) -> str:
        self.need_sync()
        resp = self.expect(self.sync("../../etc/evil.docx", self.sample("memo.docx"), **self.opts), r, 200)
        name = resp.filename or ""
        if "/" in name or "\\" in name or ".." in name:
            raise Fail(f"unsafe output name: {name}")
        return f"output name: {name}"

    def t_long_name(self, r: CaseResult) -> str:
        self.need_sync()
        resp = self.sync("a" * 230 + ".pdf", self.sample("onepage.pdf"), **self.opts)
        r.http_status = resp.status
        if resp.status == 200 or 400 <= resp.status < 500:
            return f"HTTP {resp.status}"
        raise Fail(f"HTTP {resp.status}")

    # ------------------------------------------------------------------ lavori asincroni

    def _create(self, r: CaseResult, name: str, data: bytes, **opts: Any) -> Dict[str, Any]:
        resp = self.c.post_file("/api/v1/jobs", name, data, **{**self.opts, **opts})
        self.expect(resp, r, 202)
        if not resp.header("location"):
            raise Fail("202 without Location header")
        return resp.json()

    def t_job_lifecycle(self, r: CaseResult) -> str:
        self.need_async()
        data = self.sample("memo.docx")
        job = self._create(r, "memo.docx", data)
        if job.get("status") not in ("queued", "running", "succeeded"):
            raise Fail(f"initial status '{job.get('status')}'")
        jid = job["id"]
        t0 = time.perf_counter()
        job = self.c.wait_job(jid, poll=0.25, timeout=600)
        if job["status"] != "succeeded":
            raise Fail(f"job {job['status']}: {job.get('error')}")

        res = self.c.job_result(jid)
        problems = check_output("memo.docx", data, res.filename or "memo_watermark.docx", res.body, marker=self.marker)
        if problems:
            raise Fail("; ".join(problems))
        if self.c.job_result(jid).body != res.body:
            raise Fail("the second download differs from the first")
        if jid not in [j["id"] for j in self.c.list_jobs()]:
            raise Fail("the job is missing from GET /api/v1/jobs")

        # Coerenza dei tempi
        def ts(k: str):
            return parse_time(job[k])
        if not (ts("createdAt") <= ts("startedAt") <= ts("completedAt") < ts("expiresAt")):
            raise Fail("timestamps out of order")
        retention = (ts("expiresAt") - ts("completedAt")).total_seconds() / 3600
        expected = self.caps.get("resultRetentionHours")
        if expected and abs(retention - expected) > 0.1:
            raise Fail(f"result kept {retention:.1f}h, expected {expected}h")

        if self.c.delete_job(jid).status != 204:
            raise Fail("DELETE did not answer 204")
        if self.c.request("GET", f"/api/v1/jobs/{jid}").status != 404:
            raise Fail("the job still exists after DELETE")
        if self.c.request("GET", f"/api/v1/jobs/{jid}/result").status != 404:
            raise Fail("the result still exists after DELETE")
        wait = (ts("startedAt") - ts("createdAt")).total_seconds()
        work = (ts("completedAt") - ts("startedAt")).total_seconds()
        return f"done in {time.perf_counter() - t0:.2f}s (queue {wait:.2f}s, processing {work:.2f}s), deleted"

    def t_job_failure(self, r: CaseResult) -> str:
        self.need_async()
        job = self._create(r, "random.bin", os.urandom(32 * 1024))
        job = self.c.wait_job(job["id"], poll=0.25, timeout=300)
        try:
            if job["status"] != "failed":
                raise Fail(f"status '{job['status']}', expected 'failed'")
            err = job.get("error") or {}
            if err.get("status") != 415:
                raise Fail(f"error status {err.get('status')}, expected 415")
            res = self.c.request("GET", f"/api/v1/jobs/{job['id']}/result")
            if res.status != 409:
                raise Fail(f"result of a failed job answered {res.status}, expected 409")
            return f"failed with: {err.get('title')} - {err.get('detail')}"
        finally:
            self.c.delete_job(job["id"])

    def t_job_not_ready(self, r: CaseResult) -> str:
        self.need_async()
        name = "clip.mp4" if ".mp4" in self.native else "manual.pdf"
        job = self._create(r, name, self.sample(name))
        resp = self.c.request("GET", f"/api/v1/jobs/{job['id']}/result")
        try:
            if resp.status == 409:
                if not resp.header("retry-after"):
                    raise Warn("409 without Retry-After")
                return "409 Not ready, with Retry-After"
            if resp.status == 200:
                return "the job was already finished (200): too fast to observe 409"
            raise Fail(f"HTTP {resp.status}")
        finally:
            self.c.wait_job(job["id"], poll=0.5, timeout=900)
            self.c.delete_job(job["id"])

    def t_job_unknown(self, r: CaseResult) -> None:
        fake = "0" * 32
        self.expect(self.c.request("GET", f"/api/v1/jobs/{fake}"), r, 404)
        self.expect(self.c.request("GET", f"/api/v1/jobs/{fake}/result"), r, 404)
        self.expect(self.c.request("DELETE", f"/api/v1/jobs/{fake}"), r, 404)
        self.expect(self.c.request("GET", "/api/v1/jobs/..%2F..%2Fetc"), r, 404)

    def t_job_video(self, r: CaseResult) -> str:
        self.need_async()
        if ".mp4" not in self.native:
            raise Skip("video not supported (FFmpeg missing on the server)")
        data = self.sample("clip.mp4")
        job = self.c.wait_job(self._create(r, "clip.mp4", data)["id"], poll=0.5, timeout=1800)
        try:
            if job["status"] != "succeeded":
                raise Fail(f"{job['status']}: {job.get('error')}")
            res = self.c.job_result(job["id"])
            problems = check_output("clip.mp4", data, res.filename or "clip_watermark.mp4", res.body)
            if problems:
                raise Fail("; ".join(problems))
            return f"{len(res.body):,} bytes"
        finally:
            self.c.delete_job(job["id"])

    def t_job_parallel(self, r: CaseResult) -> str:
        self.need_async()
        limit = int(self.tok.get("maxActiveJobs") or 5)
        n = max(1, min(4, limit))
        names = ["memo.docx", "onepage.pdf", "scan.png", "budget.xlsx"][:n]
        ids = [self._create(r, nm, self.sample(nm))["id"] for nm in names]
        failed = []
        for jid in ids:
            job = self.c.wait_job(jid, poll=0.25, timeout=900)
            if job["status"] != "succeeded":
                failed.append(f"{job['fileName']}: {job.get('error')}")
            self.c.delete_job(jid)
        if failed:
            raise Fail("; ".join(failed))
        return f"{n} jobs completed"

    # ------------------------------------------------------------------ limiti

    def t_modes(self, r: CaseResult) -> str:
        checks = []
        if not self.tok.get("allowSync", True):
            self.expect(self.sync("onepage.pdf", self.sample("onepage.pdf"), **self.opts), r, 403)
            checks.append("sync 403")
        if not self.tok.get("allowAsync", True):
            resp = self.c.post_file("/api/v1/jobs", "onepage.pdf", self.sample("onepage.pdf"), **self.opts)
            self.expect(resp, r, 403)
            checks.append("async 403")
        if not checks:
            raise Skip("the token allows both modes")
        return ", ".join(checks)

    def t_oversize(self, r: CaseResult) -> str:
        limit = int(self.tok.get("maxUploadBytes") or 0)
        if not limit:
            raise Skip("upload limit unknown")
        if limit > self.max_generate_mb * 1048576:
            raise Skip(f"limit {limit // 1048576} MB above --max-generate-mb {self.max_generate_mb}")
        data = self.sample("onepage.pdf") + b"\0" * (limit + 1024 * 1024)
        path = "/api/v1/watermark" if self.tok.get("allowSync", True) else "/api/v1/jobs"
        try:
            resp = self.c.post_file(path, "big.pdf", data, **self.opts)
        except (ConnectionError, OSError) as e:
            raise Warn(f"connection closed by the server instead of a 413 ({type(e).__name__})")
        self.expect(resp, r, 413)
        return f"{len(data) // 1048576} MB rejected: {resp.problem().get('detail', '')}"

    def t_rate_limit(self, r: CaseResult) -> str:
        if not self.include_limits:
            raise Skip("disruptive: run with --include-limits")
        self.need_sync()
        rpm = int(self.tok.get("requestsPerMinute") or 60)
        if rpm > 200:
            raise Skip(f"{rpm}/min is too high to test quickly")
        # I test precedenti hanno già consumato parte del minuto: si invia finché arriva il 429.
        for i in range(rpm + 1):
            resp = self.sync("onepage.pdf", self.sample("onepage.pdf"), **self.opts)
            if resp.status == 429:
                r.http_status = 429
                if not resp.header("retry-after"):
                    raise Warn("429 without Retry-After")
                wait = int(resp.header("retry-after"))
                self.log(f"             rate limit reached: waiting {wait}s for the window to reset")
                time.sleep(wait + 1)
                return f"429 after {i + 1} requests, Retry-After {wait}s"
            if resp.status != 200:
                raise Fail(f"HTTP {resp.status} before the limit")
        raise Fail(f"no 429 after {rpm + 1} requests")

    def t_active_jobs(self, r: CaseResult) -> str:
        if not self.include_limits:
            raise Skip("disruptive: run with --include-limits")
        self.need_async()
        limit = int(self.tok.get("maxActiveJobs") or 5)
        if limit > 20:
            raise Skip(f"limit {limit} too high to test quickly")
        name = "clip.mp4" if ".mp4" in self.native else "manual.pdf"
        ids = []
        try:
            for i in range(limit + 3):
                resp = self.c.post_file("/api/v1/jobs", name, self.sample(name), **self.opts)
                if resp.status == 429:
                    r.http_status = 429
                    return f"429 after {len(ids)} active jobs (limit {limit})"
                self.expect(resp, r, 202)
                ids.append(resp.json()["id"])
            raise Warn(f"no 429 after {len(ids)} jobs: they finished faster than they were queued")
        finally:
            for jid in ids:
                try:
                    self.c.wait_job(jid, poll=0.5, timeout=900)
                finally:
                    self.c.delete_job(jid)


def summarize(results: List[CaseResult]) -> Dict[str, Any]:
    counts = {s: sum(1 for r in results if r.status == s) for s in (PASS, FAIL, WARN, SKIP)}
    executed = counts[PASS] + counts[FAIL] + counts[WARN]
    return {
        "total": len(results),
        **counts,
        "passRate": round(100 * (counts[PASS] + counts[WARN]) / executed, 1) if executed else 0.0,
        "duration": round(sum(r.duration for r in results), 2),
        "cases": [asdict(r) for r in results],
    }


def junit_xml(results: List[CaseResult], suite_name: str = "watermark-api") -> str:
    from xml.sax.saxutils import escape, quoteattr
    fails = sum(1 for r in results if r.status == FAIL)
    skips = sum(1 for r in results if r.status == SKIP)
    out = [f'<?xml version="1.0" encoding="UTF-8"?>',
           f'<testsuite name={quoteattr(suite_name)} tests="{len(results)}" failures="{fails}" skipped="{skips}" '
           f'time="{sum(r.duration for r in results):.3f}">']
    for r in results:
        out.append(f'  <testcase classname={quoteattr(r.category)} name={quoteattr(f"{r.id} {r.name}")} time="{r.duration:.3f}">')
        if r.status == FAIL:
            out.append(f'    <failure message={quoteattr(r.message)}>{escape(chr(10).join(r.details))}</failure>')
        elif r.status == SKIP:
            out.append(f'    <skipped message={quoteattr(r.message)}/>')
        elif r.message:
            out.append(f'    <system-out>{escape(r.status.upper() + ": " + r.message)}</system-out>')
        out.append("  </testcase>")
    out.append("</testsuite>")
    return "\n".join(out)
