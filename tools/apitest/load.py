"""
Test di carico: utenti virtuali (thread) che inviano file in continuo, a uno o più livelli di
concorrenza ("gradini"). Ogni richiesta diventa una riga di requests.csv; da lì si calcolano i KPI.
"""
from __future__ import annotations

import csv
import math
import random
import statistics
import threading
import time
from dataclasses import asdict, dataclass, field, fields
from pathlib import Path
from typing import Any, Callable, Dict, List, Optional

from checks import ext_of
from wmapi import Multipart, WatermarkClient, options_to_fields, parse_time


@dataclass
class Sample:
    name: str
    ext: str
    data: bytes
    weight: float = 1.0


@dataclass
class Row:
    """Una richiesta (sincrona) o un lavoro completo (asincrono)."""
    t: float                     # secondi dall'inizio del test
    step: int                    # indice del gradino
    concurrency: int
    vu: int
    mode: str
    file: str
    ext: str
    bytes_in: int
    bytes_out: int = 0
    status: int = 0
    ok: bool = False
    error: str = ""              # categoria dell'errore
    detail: str = ""
    latency: float = 0.0         # sincrona: richiesta completa; asincrona: dall'invio al file scaricato
    upload: float = 0.0
    server: float = 0.0          # sincrona: attesa della risposta; asincrona: elaborazione sul server
    download: float = 0.0
    queue_wait: float = 0.0      # solo asincrona: attesa in coda sul server
    polls: int = 0


CSV_FIELDS = [f.name for f in fields(Row)]


def load_samples(folder: Path, mix: Optional[Dict[str, float]], native: List[str]) -> List[Sample]:
    """File del corpus, filtrati sui formati supportati e pesati secondo --mix (per estensione)."""
    out = []
    for p in sorted(folder.rglob("*")):
        if not p.is_file() or p.name.startswith("."):
            continue
        ext = p.suffix.lower()
        if native and ext not in native:
            continue
        key = ext[1:]
        if mix is not None:
            if key not in mix and p.name not in mix:
                continue
            w = mix.get(p.name, mix.get(key, 0))
        else:
            w = 1.0
        if w > 0:
            out.append(Sample(p.name, ext, p.read_bytes(), w))
    # Il peso è per formato: va diviso tra i file dello stesso formato.
    if mix is not None:
        per_ext: Dict[str, int] = {}
        for s in out:
            if s.name not in mix:
                per_ext[s.ext] = per_ext.get(s.ext, 0) + 1
        for s in out:
            if s.name not in mix:
                s.weight /= per_ext[s.ext]
    return out


def classify(status: int, exc: Optional[BaseException] = None) -> str:
    if exc is not None:
        if isinstance(exc, TimeoutError) or "timed out" in str(exc).lower():
            return "timeout"
        return "connection"
    if status == 429:
        return "rate_limited"
    if status == 503:
        return "busy_503"
    if status == 413:
        return "too_large"
    if status in (415, 422):
        return "unprocessable"
    if 400 <= status < 500:
        return f"client_{status}"
    if status >= 500:
        return f"server_{status}"
    return ""


@dataclass
class LoadConfig:
    url: str
    token: str
    mode: str = "sync"                       # sync | async
    steps: List[int] = field(default_factory=lambda: [4])
    step_duration: float = 60
    ramp: float = 5
    think: float = 0
    poll: float = 0.5
    timeout: float = 900
    insecure: bool = False
    options: Dict[str, Any] = field(default_factory=dict)
    respect_retry_after: bool = True
    seed: int = 1
    max_requests: int = 0                    # 0 = senza limite (vale per gradino)


class LoadRunner:
    def __init__(self, cfg: LoadConfig, samples: List[Sample], log: Callable[[str], None] = print):
        self.cfg = cfg
        self.samples = samples
        self.log = log
        self.rows: List[Row] = []
        self.lock = threading.Lock()
        self.stop_all = threading.Event()
        self.t0 = 0.0
        self.step_windows: List[Dict[str, Any]] = []
        # Corpi multipart preparati una volta sola: il client non deve diventare il collo di bottiglia.
        fields_ = options_to_fields(**cfg.options)
        self.bodies = {s.name: Multipart.build(fields_, s.name, s.data) for s in samples}
        self.weights = [s.weight for s in samples]

    def now(self) -> float:
        return time.perf_counter() - self.t0

    def run(self) -> List[Row]:
        self.t0 = time.perf_counter()
        try:
            for i, conc in enumerate(self.cfg.steps):
                if self.stop_all.is_set():
                    break
                self.run_step(i, conc)
        except KeyboardInterrupt:
            self.log("\nInterrupted: waiting for the requests in progress, then writing the results...")
            self.stop_all.set()
        return self.rows

    def run_step(self, index: int, conc: int) -> None:
        cfg = self.cfg
        start = self.now()
        end_at = start + cfg.step_duration
        stop = threading.Event()
        counter = {"n": 0}
        self.log(f"Step {index + 1}/{len(cfg.steps)}: {conc} virtual users for {cfg.step_duration:.0f}s ({cfg.mode})")

        def vu(n: int) -> None:
            rnd = random.Random(cfg.seed * 1000 + index * 100 + n)
            client = WatermarkClient(cfg.url, cfg.token, timeout=cfg.timeout, insecure=cfg.insecure)
            # Avvio graduale: gli utenti partono distribuiti sul tempo di ramp-up.
            delay = cfg.ramp * n / max(1, conc)
            if stop.wait(delay):
                return
            while not stop.is_set() and not self.stop_all.is_set() and self.now() < end_at:
                if cfg.max_requests:
                    with self.lock:
                        if counter["n"] >= cfg.max_requests:
                            break
                        counter["n"] += 1
                sample = rnd.choices(self.samples, weights=self.weights)[0]
                row = self.one_sync(client, sample, index, conc, n) if cfg.mode == "sync" \
                    else self.one_async(client, sample, index, conc, n, end_at + cfg.timeout)
                with self.lock:
                    self.rows.append(row)
                if row.error == "rate_limited" and cfg.respect_retry_after:
                    stop.wait(min(60.0, max(1.0, row.server)))
                elif cfg.think:
                    stop.wait(rnd.expovariate(1 / cfg.think))
            client.close()

        threads = [threading.Thread(target=vu, args=(n,), daemon=True, name=f"vu-{n}") for n in range(conc)]
        for t in threads:
            t.start()

        last = time.monotonic()
        try:
            while any(t.is_alive() for t in threads):
                time.sleep(0.5)
                if self.now() >= end_at:
                    stop.set()
                if time.monotonic() - last >= 5:
                    last = time.monotonic()
                    self.log("  " + self.progress(index, start))
        except KeyboardInterrupt:
            stop.set()
            self.stop_all.set()
            self.log("\nInterrupted: waiting for the requests in progress...")
            for t in threads:
                t.join()
        stop.set()
        self.step_windows.append({"step": index, "concurrency": conc, "start": start, "end": self.now()})
        self.log("  " + self.progress(index, start) + "  (step done)")

    def progress(self, index: int, start: float) -> str:
        with self.lock:
            rows = [r for r in self.rows if r.step == index]
        ok = [r for r in rows if r.ok]
        elapsed = max(0.001, self.now() - start)
        lat = sorted(r.latency for r in ok)
        p95 = percentile(lat, 95) if lat else 0
        return (f"{len(rows)} done, {len(ok)} ok, {len(rows) - len(ok)} errors, "
                f"{len(ok) / elapsed:.2f} files/s, p95 {p95:.2f}s")

    # ------------------------------------------------------------------ richieste

    def one_sync(self, client: WatermarkClient, s: Sample, step: int, conc: int, vu: int) -> Row:
        mp = self.bodies[s.name]
        row = Row(t=self.now(), step=step, concurrency=conc, vu=vu, mode="sync", file=s.name, ext=s.ext,
                  bytes_in=len(s.data))
        try:
            resp = client.request("POST", "/api/v1/watermark", mp.body, mp.content_type)
        except Exception as e:  # noqa: BLE001
            row.latency = self.now() - row.t
            row.error, row.detail = classify(0, e), f"{type(e).__name__}: {e}"
            return row
        tm = resp.timing
        row.status, row.latency, row.upload, row.server, row.download = \
            resp.status, tm.done, tm.upload, tm.server, tm.download
        if resp.status == 200:
            row.ok, row.bytes_out = True, len(resp.body)
        else:
            row.error = classify(resp.status)
            row.detail = (resp.problem().get("detail") or resp.reason)[:200]
            if resp.status == 429:
                row.server = float(resp.header("retry-after") or 5)  # usato per attendere
        return row

    def one_async(self, client: WatermarkClient, s: Sample, step: int, conc: int, vu: int, deadline: float) -> Row:
        mp = self.bodies[s.name]
        row = Row(t=self.now(), step=step, concurrency=conc, vu=vu, mode="async", file=s.name, ext=s.ext,
                  bytes_in=len(s.data))
        start = time.perf_counter()
        job_id = None
        try:
            resp = client.request("POST", "/api/v1/jobs", mp.body, mp.content_type)
            row.upload = resp.timing.done
            if resp.status != 202:
                row.status, row.error = resp.status, classify(resp.status)
                row.detail = (resp.problem().get("detail") or resp.reason)[:200]
                if resp.status == 429:
                    row.server = float(resp.header("retry-after") or 5)
                row.latency = time.perf_counter() - start
                return row
            job_id = resp.json()["id"]

            while True:
                if self.now() > deadline:
                    raise TimeoutError(f"job still running after the timeout")
                time.sleep(self.cfg.poll)
                st = client.request("GET", f"/api/v1/jobs/{job_id}")
                row.polls += 1
                if st.status != 200:
                    row.status, row.error = st.status, classify(st.status)
                    row.latency = time.perf_counter() - start
                    return row
                job = st.json()
                if job["status"] in ("succeeded", "failed"):
                    break

            created, started, completed = (parse_time(job.get(k)) for k in ("createdAt", "startedAt", "completedAt"))
            if created and started:
                row.queue_wait = (started - created).total_seconds()
            if started and completed:
                row.server = (completed - started).total_seconds()

            if job["status"] == "failed":
                err = job.get("error") or {}
                row.status = int(err.get("status") or 500)
                row.error = classify(row.status)
                row.detail = f"{err.get('title')}: {err.get('detail')}"[:200]
                row.latency = time.perf_counter() - start
                return row

            res = client.request("GET", f"/api/v1/jobs/{job_id}/result")
            row.download = res.timing.done
            row.status = res.status
            row.latency = time.perf_counter() - start
            if res.status == 200:
                row.ok, row.bytes_out = True, len(res.body)
            else:
                row.error = classify(res.status)
            return row
        except Exception as e:  # noqa: BLE001
            row.latency = time.perf_counter() - start
            row.error, row.detail = classify(0, e), f"{type(e).__name__}: {e}"
            return row
        finally:
            if job_id:
                try:
                    client.request("DELETE", f"/api/v1/jobs/{job_id}")
                except Exception:  # noqa: BLE001 - la pulizia non deve falsare il test
                    pass


# ---------------------------------------------------------------------- KPI

def percentile(sorted_values: List[float], p: float) -> float:
    if not sorted_values:
        return 0.0
    k = (len(sorted_values) - 1) * p / 100
    lo, hi = math.floor(k), math.ceil(k)
    if lo == hi:
        return sorted_values[int(k)]
    return sorted_values[lo] + (sorted_values[hi] - sorted_values[lo]) * (k - lo)


def dist(values: List[float]) -> Dict[str, float]:
    v = sorted(values)
    if not v:
        return {"n": 0}
    return {"n": len(v), "mean": statistics.fmean(v), "min": v[0], "p50": percentile(v, 50),
            "p90": percentile(v, 90), "p95": percentile(v, 95), "p99": percentile(v, 99), "max": v[-1]}


def group_kpis(rows: List[Row], duration: float, apdex_t: float) -> Dict[str, Any]:
    ok = [r for r in rows if r.ok]
    n = len(rows)
    errors: Dict[str, int] = {}
    for r in rows:
        if not r.ok:
            errors[r.error or "other"] = errors.get(r.error or "other", 0) + 1
    satisfied = sum(1 for r in ok if r.latency <= apdex_t)
    tolerating = sum(1 for r in ok if apdex_t < r.latency <= 4 * apdex_t)
    mb_in = sum(r.bytes_in for r in ok) / 1048576
    mb_out = sum(r.bytes_out for r in ok) / 1048576
    server_mb_s = [r.bytes_in / 1048576 / r.server for r in ok if r.server > 0]
    d = max(duration, 0.001)
    return {
        "requests": n,
        "ok": len(ok),
        "errors": n - len(ok),
        "errorRate": round(100 * (n - len(ok)) / n, 2) if n else 0.0,
        "errorsByType": errors,
        "throughput": len(ok) / d,                  # file elaborati al secondo
        "filesPerMinute": 60 * len(ok) / d,
        "mbInPerSec": mb_in / d,
        "mbOutPerSec": mb_out / d,
        "mbIn": mb_in,
        "mbOut": mb_out,
        "latency": dist([r.latency for r in ok]),
        "upload": dist([r.upload for r in ok]),
        "server": dist([r.server for r in ok]),
        "download": dist([r.download for r in ok]),
        "queueWait": dist([r.queue_wait for r in ok if r.mode == "async"]),
        "serverMBps": dist(server_mb_s),
        "apdex": round((satisfied + tolerating / 2) / n, 3) if n else 0.0,
        "duration": duration,
    }


def compute_kpis(rows: List[Row], windows: List[Dict[str, Any]], cfg: Dict[str, Any], slo: Dict[str, float],
                 apdex_t: float) -> Dict[str, Any]:
    total_duration = sum(w["end"] - w["start"] for w in windows) or (max((r.t + r.latency for r in rows), default=0))
    overall = group_kpis(rows, total_duration, apdex_t)

    steps = []
    for w in windows:
        rs = [r for r in rows if r.step == w["step"]]
        k = group_kpis(rs, w["end"] - w["start"], apdex_t)
        k.update({"step": w["step"] + 1, "concurrency": w["concurrency"]})
        k["meetsSlo"] = slo_ok(k, slo)
        steps.append(k)

    formats = []
    for ext in sorted({r.ext for r in rows}):
        rs = [r for r in rows if r.ext == ext]
        k = group_kpis(rs, total_duration, apdex_t)
        k["format"] = ext
        k["avgSizeKB"] = statistics.fmean(r.bytes_in for r in rs) / 1024 if rs else 0
        formats.append(k)

    # Saturazione: il gradino migliore che rispetta gli SLO, e il punto in cui la capacità smette di crescere.
    passing = [s for s in steps if s["meetsSlo"]]
    best = max(passing, key=lambda s: s["throughput"]) if passing else None
    knee = None
    for prev, cur in zip(steps, steps[1:]):
        if prev["throughput"] > 0 and cur["throughput"] < prev["throughput"] * 1.10:
            knee = prev
            break
    peak = max(steps, key=lambda s: s["throughput"]) if steps else None

    return {
        "config": cfg,
        "slo": slo,
        "apdexT": apdex_t,
        "overall": overall,
        "steps": steps,
        "formats": formats,
        "capacity": {
            "peakThroughput": peak["throughput"] if peak else 0,
            "peakConcurrency": peak["concurrency"] if peak else 0,
            "maxSustainableConcurrency": best["concurrency"] if best else None,
            "maxSustainableThroughput": best["throughput"] if best else None,
            "kneeConcurrency": knee["concurrency"] if knee else None,
        },
        "sloResults": slo_table(overall, slo),
    }


def slo_ok(k: Dict[str, Any], slo: Dict[str, float]) -> bool:
    return all(row["pass"] for row in slo_table(k, slo))


def slo_table(k: Dict[str, Any], slo: Dict[str, float]) -> List[Dict[str, Any]]:
    out = []
    if slo.get("p95") is not None:
        v = k["latency"].get("p95", 0)
        out.append({"name": "Latency p95", "target": f"≤ {slo['p95']:g} s", "value": f"{v:.2f} s",
                    "pass": k["ok"] > 0 and v <= slo["p95"]})
    if slo.get("errorRate") is not None:
        v = k["errorRate"]
        out.append({"name": "Error rate", "target": f"≤ {slo['errorRate']:g} %", "value": f"{v:.2f} %",
                    "pass": v <= slo["errorRate"]})
    if slo.get("throughput"):
        v = k["throughput"]
        out.append({"name": "Throughput", "target": f"≥ {slo['throughput']:g} files/s", "value": f"{v:.2f} files/s",
                    "pass": v >= slo["throughput"]})
    return out


# ---------------------------------------------------------------------- file

def write_rows(rows: List[Row], path: Path) -> None:
    with path.open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=CSV_FIELDS)
        w.writeheader()
        for r in rows:
            d = asdict(r)
            for k in ("t", "latency", "upload", "server", "download", "queue_wait"):
                d[k] = round(d[k], 4)
            w.writerow(d)


def read_rows(path: Path) -> List[Row]:
    types = {f.name: f.type for f in fields(Row)}
    out = []
    with path.open(encoding="utf-8") as f:
        for d in csv.DictReader(f):
            kw: Dict[str, Any] = {}
            for k, v in d.items():
                t = types.get(k)
                if t in ("int", int):
                    kw[k] = int(v or 0)
                elif t in ("float", float):
                    kw[k] = float(v or 0)
                elif t in ("bool", bool):
                    kw[k] = v == "True"
                else:
                    kw[k] = v
            out.append(Row(**kw))
    return out
