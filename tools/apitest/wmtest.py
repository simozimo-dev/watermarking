#!/usr/bin/env python3
"""
wmtest - client, test funzionali e test di carico per le API del servizio di watermark.

Richiede solo Python 3.9+ (nessun pacchetto da installare).

URL e token si passano con --url / --token oppure con le variabili WATERMARK_URL e WATERMARK_TOKEN
(consigliato: il token non resta nella cronologia della shell). --token-file legge il token da un file.

Esempi:
  python wmtest.py caps
  python wmtest.py watermark report.docx --text "CONFIDENTIAL" -o out.docx
  python wmtest.py job video.mp4 --text "{user} {date}" -o out.mp4
  python wmtest.py functional
  python wmtest.py load --steps 1,2,4,8 --duration 60
  python wmtest.py all --steps 1,2,4 --duration 45
"""
from __future__ import annotations

import argparse
import csv
import json
import os
import platform
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Any, Dict, List, Optional

HERE = Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))

from functional import FunctionalSuite, junit_xml, summarize  # noqa: E402
from load import LoadConfig, LoadRunner, compute_kpis, load_samples, read_rows, write_rows  # noqa: E402
from report import render  # noqa: E402
from wmapi import ApiError, WatermarkClient, options_to_fields  # noqa: E402

VERSION = "1.0"

try:  # Console di Windows: caratteri non ASCII nei messaggi
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[attr-defined]
except Exception:  # noqa: BLE001
    pass


# ---------------------------------------------------------------------- argomenti

def add_connection(p: argparse.ArgumentParser) -> None:
    g = p.add_argument_group("connection")
    g.add_argument("--url", default=os.environ.get("WATERMARK_URL"), help="Service URL (or WATERMARK_URL)")
    g.add_argument("--token", default=os.environ.get("WATERMARK_TOKEN"), help="API token (or WATERMARK_TOKEN)")
    g.add_argument("--token-file", help="Read the token from this file")
    g.add_argument("--insecure", action="store_true", help="Do not verify the TLS certificate")
    g.add_argument("--timeout", type=float, default=900, help="Timeout of a single request, seconds (900)")


def add_watermark_options(p: argparse.ArgumentParser) -> None:
    g = p.add_argument_group("watermark")
    g.add_argument("--text", help="Watermark text (\\n for a new line; placeholders {date} {time} {filename} {user} {email})")
    g.add_argument("--angle", type=float)
    g.add_argument("--size", type=float, help="Font size in points")
    g.add_argument("--color", help="#RRGGBB")
    g.add_argument("--opacity", type=float, help="0.05 - 1")
    g.add_argument("--font")
    g.add_argument("--pdf", action="store_true", help="Convert to PDF first")
    g.add_argument("--tile", action="store_true", help="Repeat across the page")
    g.add_argument("--slides", action="store_true", help="PowerPoint: also on top of each slide")
    g.add_argument("--user", help="End user for {user} (only if the token allows it)")
    g.add_argument("--email", help="End user's email for {email}")


def wm_options(a: argparse.Namespace) -> Dict[str, Any]:
    text = a.text.replace("\\n", "\n") if a.text else None
    return options_to_fields(text=text, angle=a.angle, size=a.size, color=a.color, opacity=a.opacity, font=a.font,
                             output="pdf" if a.pdf else None, tile=True if a.tile else None,
                             pptx_slides=True if a.slides else None, user=a.user, email=a.email)


def resolve(a: argparse.Namespace) -> None:
    if getattr(a, "token_file", None):
        a.token = Path(a.token_file).read_text(encoding="utf-8").strip()
    if not a.url:
        sys.exit("Missing service URL: use --url or set WATERMARK_URL.")
    if not a.token:
        sys.exit("Missing API token: use --token, --token-file or set WATERMARK_TOKEN.")


def client(a: argparse.Namespace) -> WatermarkClient:
    return WatermarkClient(a.url, a.token, timeout=a.timeout, insecure=a.insecure)


def out_dir(a: argparse.Namespace, kind: str) -> Path:
    name = f"{datetime.now():%Y%m%d-%H%M%S}-{kind}" + (f"-{a.name}" if getattr(a, "name", None) else "")
    d = Path(a.out) / name
    d.mkdir(parents=True, exist_ok=True)
    return d


# ---------------------------------------------------------------------- comandi del client

def cmd_caps(a: argparse.Namespace) -> int:
    print(json.dumps(client(a).capabilities(), indent=2, ensure_ascii=False))
    return 0


def cmd_watermark(a: argparse.Namespace) -> int:
    c = client(a)
    src = Path(a.file)
    resp = c.watermark(src.name, src.read_bytes(), **wm_options(a))
    if resp.status != 200:
        p = resp.problem()
        print(f"HTTP {resp.status} {p.get('title', resp.reason)}: {p.get('detail', '')}", file=sys.stderr)
        return 1
    out = Path(a.output) if a.output else src.with_name(resp.filename or f"{src.stem}_watermark{src.suffix}")
    out.write_bytes(resp.body)
    t = resp.timing
    print(f"{out}  ({len(resp.body):,} bytes)  total {t.done:.2f}s = upload {t.upload:.2f}s + "
          f"server {t.server:.2f}s + download {t.download:.2f}s")
    for w in resp.warnings:
        print(f"warning: {w}")
    print(f"id: {resp.header('x-watermark-id')}  format: {resp.header('x-watermark-format')}")
    return 0


def cmd_job(a: argparse.Namespace) -> int:
    c = client(a)
    src = Path(a.file)
    job = c.create_job(str(src), **wm_options(a))
    print(f"job {job['id']} {job['status']}")
    if a.no_wait:
        return 0
    t0 = time.perf_counter()
    last = ""
    while True:
        job = c.get_job(job["id"])
        if job["status"] != last:
            print(f"  {time.perf_counter() - t0:6.1f}s  {job['status']}")
            last = job["status"]
        if job["status"] in ("succeeded", "failed"):
            break
        time.sleep(a.poll)
    if job["status"] == "failed":
        e = job.get("error") or {}
        print(f"failed: {e.get('status')} {e.get('title')}: {e.get('detail')}", file=sys.stderr)
        return 1
    out = Path(a.output) if a.output else src.with_name(job["result"]["fileName"])
    c.job_result(job["id"], save_to=str(out))
    print(f"{out}  ({out.stat().st_size:,} bytes)")
    for w in job.get("warnings", []):
        print(f"warning: {w}")
    if not a.keep:
        c.delete_job(job["id"])
    return 0


def cmd_jobs(a: argparse.Namespace) -> int:
    for j in client(a).list_jobs():
        print(f"{j['id']}  {j['status']:<10} {j['createdAt'][:19]}  {j['fileName']}")
    return 0


def cmd_status(a: argparse.Namespace) -> int:
    print(json.dumps(client(a).get_job(a.id), indent=2, ensure_ascii=False))
    return 0


def cmd_result(a: argparse.Namespace) -> int:
    c = client(a)
    job = c.get_job(a.id)
    out = a.output or (job.get("result") or {}).get("fileName") or f"{a.id}.bin"
    c.job_result(a.id, save_to=out)
    print(out)
    return 0


def cmd_delete(a: argparse.Namespace) -> int:
    r = client(a).delete_job(a.id)
    print(f"HTTP {r.status}")
    return 0 if r.status == 204 else 1


# ---------------------------------------------------------------------- test

def run_functional(a: argparse.Namespace) -> Dict[str, Any]:
    suite = FunctionalSuite(a.url, a.token, Path(a.samples), token2=a.token2, insecure=a.insecure,
                            include_limits=a.include_limits, max_generate_mb=a.max_generate_mb, timeout=a.timeout)
    results = suite.run()
    s = summarize(results)
    s["capabilities"] = suite.caps
    print(f"\n{s['pass']} passed, {s['fail']} failed, {s['warn']} warnings, {s['skip']} skipped "
          f"- pass rate {s['passRate']:.1f}% in {s['duration']:.1f}s")
    return s


def cmd_functional(a: argparse.Namespace) -> int:
    d = out_dir(a, "functional")
    s = run_functional(a)
    (d / "functional.json").write_text(json.dumps(s, indent=2, ensure_ascii=False, default=str), encoding="utf-8")
    (d / "junit.xml").write_text(junit_xml_from(s), encoding="utf-8")
    (d / "report.html").write_text(render("Watermark API - functional tests", meta(a, s.get("capabilities")),
                                          functional=s), encoding="utf-8")
    print(f"Results: {d / 'report.html'}")
    return 1 if s["fail"] else 0


def junit_xml_from(s: Dict[str, Any]) -> str:
    from functional import CaseResult
    return junit_xml([CaseResult(**c) for c in s["cases"]])


def parse_mix(value: Optional[str]) -> Optional[Dict[str, float]]:
    if not value:
        return None
    out = {}
    for part in value.split(","):
        k, _, w = part.partition("=")
        out[k.strip().lstrip(".").lower() if "." not in k.strip()[1:] else k.strip()] = float(w or 1)
    return out


def run_load(a: argparse.Namespace, d: Path) -> Dict[str, Any]:
    caps = client(a).capabilities()
    tok = caps.get("token", {})
    native = [e.lower() for e in caps.get("native", [])]
    samples = load_samples(Path(a.samples), parse_mix(a.mix), native)
    if not samples:
        sys.exit("No usable files: check --samples and --mix (formats must be supported by the server).")

    steps = [int(x) for x in a.steps.split(",")] if a.steps else [a.concurrency]
    mode = a.mode
    allowed = tok.get("allowSync" if mode == "sync" else "allowAsync", True)
    if not allowed:
        sys.exit(f"The token does not allow {mode} processing.")
    rpm = tok.get("requestsPerMinute") or 0
    print(f"Token '{tok.get('name')}': {rpm} requests/min, {tok.get('maxActiveJobs')} active jobs, "
          f"max {int(tok.get('maxUploadBytes', 0)) // 1048576} MB")
    if rpm and rpm < 600:
        print(f"  note: {rpm}/min may cap the throughput (429). For load tests use a token with a high limit.")
    print(f"Corpus: {len(samples)} files - " + ", ".join(f"{s.name} ({s.weight:.2g})" for s in samples))

    options = wm_options(a)
    options.setdefault("text", "LOAD TEST {date} {time}")
    cfg = LoadConfig(url=a.url, token=a.token, mode=mode, steps=steps, step_duration=a.duration, ramp=a.ramp,
                     think=a.think, poll=a.poll, timeout=a.timeout, insecure=a.insecure, options=options,
                     respect_retry_after=not a.no_retry_wait, seed=a.seed, max_requests=a.max_requests)
    runner = LoadRunner(cfg, samples)
    rows = runner.run()
    write_rows(rows, d / "requests.csv")

    slo = {"p95": a.slo_p95, "errorRate": a.slo_errors, "throughput": a.slo_throughput}
    cfg_view = {"mode": mode, "steps": ",".join(map(str, steps)), "stepDuration": a.duration, "rampUp": a.ramp,
                "think": a.think, "files": len(samples), "mix": a.mix or "all formats, same weight",
                "options": json.dumps({k: v for k, v in options.items()}, ensure_ascii=False),
                "url": a.url, "token": tok.get("name"), "client": f"{platform.node()} ({platform.system()})",
                "clientVersion": f"wmtest {VERSION}, Python {platform.python_version()}"}
    k = compute_kpis(rows, runner.step_windows, cfg_view, slo, a.apdex)
    k["windows"] = runner.step_windows
    k["capabilities"] = caps
    (d / "summary.json").write_text(json.dumps(k, indent=2, ensure_ascii=False, default=str), encoding="utf-8")
    append_history(Path(a.out) / "history.csv", d.name, k)
    print_kpis(k)
    return k


def cmd_load(a: argparse.Namespace) -> int:
    d = out_dir(a, "load")
    k = run_load(a, d)
    rows = read_rows(d / "requests.csv")
    (d / "report.html").write_text(render("Watermark API - load test", meta(a, k.get("capabilities")), kpis=k, rows=rows,
                                          windows=k["windows"]), encoding="utf-8")
    print(f"Results: {d / 'report.html'}")
    return 0 if all(s["pass"] for s in k["sloResults"]) else 2


def cmd_all(a: argparse.Namespace) -> int:
    d = out_dir(a, "full")
    print("=" * 60 + "\nFUNCTIONAL TESTS\n" + "=" * 60)
    s = run_functional(a)
    (d / "functional.json").write_text(json.dumps(s, indent=2, ensure_ascii=False, default=str), encoding="utf-8")
    (d / "junit.xml").write_text(junit_xml_from(s), encoding="utf-8")
    print("\n" + "=" * 60 + "\nLOAD TEST\n" + "=" * 60)
    k = run_load(a, d)
    rows = read_rows(d / "requests.csv")
    (d / "report.html").write_text(render("Watermark API - test report", meta(a, k.get("capabilities")), functional=s,
                                          kpis=k, rows=rows, windows=k["windows"]), encoding="utf-8")
    print(f"Results: {d / 'report.html'}")
    return 1 if s["fail"] else (0 if all(x["pass"] for x in k["sloResults"]) else 2)


def cmd_report(a: argparse.Namespace) -> int:
    d = Path(a.dir)
    k = json.loads((d / "summary.json").read_text(encoding="utf-8")) if (d / "summary.json").exists() else None
    f = json.loads((d / "functional.json").read_text(encoding="utf-8")) if (d / "functional.json").exists() else None
    rows = read_rows(d / "requests.csv") if (d / "requests.csv").exists() else []
    if k is not None and (a.apdex is not None or a.slo_p95 is not None):
        slo = dict(k["slo"])
        if a.slo_p95 is not None:
            slo["p95"] = a.slo_p95
        k2 = compute_kpis(rows, k["windows"], k["config"], slo, a.apdex if a.apdex is not None else k["apdexT"])
        k2["windows"], k2["capabilities"] = k["windows"], k.get("capabilities")
        k = k2
    html = render("Watermark API - test report", {"results": d.name, **({"url": k["config"].get("url")} if k else {})},
                  functional=f, kpis=k, rows=rows, windows=(k or {}).get("windows"))
    (d / "report.html").write_text(html, encoding="utf-8")
    print(d / "report.html")
    return 0


# ---------------------------------------------------------------------- output

def meta(a: argparse.Namespace, caps: Optional[Dict[str, Any]]) -> Dict[str, Any]:
    tok = (caps or {}).get("token", {})
    return {"service": a.url, "token": tok.get("name"), "date": f"{datetime.now():%Y-%m-%d %H:%M}",
            "converter": "yes" if (caps or {}).get("converterAvailable") else "no",
            "client": platform.node()}


def print_kpis(k: Dict[str, Any]) -> None:
    o, cap = k["overall"], k["capacity"]
    lat = o["latency"]
    print("\nKPI")
    print(f"  requests         {o['requests']}  ok {o['ok']}  errors {o['errors']} ({o['errorRate']:.2f}%)")
    print(f"  throughput       {o['throughput']:.2f} files/s  ({o['filesPerMinute']:.0f}/min, {o['mbInPerSec']:.2f} MB/s in)")
    if lat.get("n"):
        print(f"  latency          p50 {lat['p50']:.2f}s  p90 {lat['p90']:.2f}s  p95 {lat['p95']:.2f}s  "
              f"p99 {lat['p99']:.2f}s  max {lat['max']:.2f}s")
    print(f"  apdex (T={k['apdexT']:g}s)   {o['apdex']:.2f}")
    if o["errorsByType"]:
        print("  errors           " + ", ".join(f"{e} {n}" for e, n in o["errorsByType"].items()))
    for s in k["steps"]:
        l = s["latency"]
        print(f"  step {s['concurrency']:>3} VU      {s['throughput']:.2f} files/s  p95 "
              f"{l.get('p95', 0):.2f}s  errors {s['errorRate']:.2f}%  {'SLO ok' if s['meetsSlo'] else 'SLO KO'}")
    if cap["maxSustainableConcurrency"]:
        print(f"  capacity         {cap['maxSustainableThroughput']:.2f} files/s at {cap['maxSustainableConcurrency']} VU within SLO")
    for s in k["sloResults"]:
        print(f"  SLO {s['name']:<14} {s['value']:>12}  target {s['target']:<14} {'PASS' if s['pass'] else 'FAIL'}")


def append_history(path: Path, run: str, k: Dict[str, Any]) -> None:
    o, cap = k["overall"], k["capacity"]
    row = {
        "run": run, "date": f"{datetime.now():%Y-%m-%d %H:%M}", "mode": k["config"]["mode"],
        "steps": k["config"]["steps"], "requests": o["requests"], "errorRate": o["errorRate"],
        "throughput": round(o["throughput"], 3), "p50": round(o["latency"].get("p50", 0), 3),
        "p95": round(o["latency"].get("p95", 0), 3), "p99": round(o["latency"].get("p99", 0), 3),
        "apdex": o["apdex"], "peakThroughput": round(cap["peakThroughput"], 3),
        "sustainableConcurrency": cap["maxSustainableConcurrency"] or "",
        "sloPass": all(s["pass"] for s in k["sloResults"]),
    }
    new = not path.exists()
    with path.open("a", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(row))
        if new:
            w.writeheader()
        w.writerow(row)


# ---------------------------------------------------------------------- main

def main(argv: Optional[List[str]] = None) -> int:
    ap = argparse.ArgumentParser(prog="wmtest", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--version", action="version", version=f"wmtest {VERSION}")
    sub = ap.add_subparsers(dest="cmd", required=True)

    p = sub.add_parser("caps", help="Formats, fonts and limits of the token")
    add_connection(p); p.set_defaults(fn=cmd_caps)

    p = sub.add_parser("watermark", help="Watermark a file (synchronous)")
    p.add_argument("file"); p.add_argument("-o", "--output")
    add_connection(p); add_watermark_options(p); p.set_defaults(fn=cmd_watermark)

    p = sub.add_parser("job", help="Watermark a file as an asynchronous job")
    p.add_argument("file"); p.add_argument("-o", "--output")
    p.add_argument("--no-wait", action="store_true", help="Queue the job and exit")
    p.add_argument("--keep", action="store_true", help="Do not delete the job after downloading")
    p.add_argument("--poll", type=float, default=1.0)
    add_connection(p); add_watermark_options(p); p.set_defaults(fn=cmd_job)

    for name, fn, help_ in [("jobs", cmd_jobs, "List the token's jobs")]:
        p = sub.add_parser(name, help=help_); add_connection(p); p.set_defaults(fn=fn)
    for name, fn, help_ in [("status", cmd_status, "Job status"), ("result", cmd_result, "Download a job result"),
                            ("delete", cmd_delete, "Delete a job")]:
        p = sub.add_parser(name, help=help_); p.add_argument("id")
        if name == "result":
            p.add_argument("-o", "--output")
        add_connection(p); p.set_defaults(fn=fn)

    def add_common_test(p: argparse.ArgumentParser) -> None:
        p.add_argument("--samples", default=str(HERE / "samples"), help="Folder with the test files (default: samples/)")
        p.add_argument("--out", default=str(HERE / "results"), help="Results folder (default: results/)")
        p.add_argument("--name", help="Label added to the results folder name")

    def add_functional(p: argparse.ArgumentParser) -> None:
        g = p.add_argument_group("functional tests")
        g.add_argument("--token2", default=os.environ.get("WATERMARK_TOKEN2"),
                       help="Second token, to check that jobs are isolated (or WATERMARK_TOKEN2)")
        g.add_argument("--include-limits", action="store_true",
                       help="Also hit the rate and active-job limits (the token is throttled for about a minute)")
        g.add_argument("--max-generate-mb", type=int, default=64, help="Largest file generated for the 413 test (64)")

    def add_load(p: argparse.ArgumentParser) -> None:
        g = p.add_argument_group("load test")
        g.add_argument("--mode", choices=["sync", "async"], default="sync")
        g.add_argument("--concurrency", "-c", type=int, default=4, help="Virtual users (single step)")
        g.add_argument("--steps", help="Stepped test, e.g. 1,2,4,8: one step per concurrency level")
        g.add_argument("--duration", "-d", type=float, default=60, help="Seconds per step (60)")
        g.add_argument("--ramp", type=float, default=5, help="Seconds to start all users of a step (5)")
        g.add_argument("--think", type=float, default=0, help="Average pause between requests of a user, seconds")
        g.add_argument("--mix", help="Weights by format or file, e.g. docx=4,pdf=3,xlsx=1,report.docx=2")
        g.add_argument("--max-requests", type=int, default=0, help="Stop a step after N requests")
        g.add_argument("--poll", type=float, default=0.5, help="Async: seconds between status checks")
        g.add_argument("--no-retry-wait", action="store_true", help="Do not wait Retry-After after a 429")
        g.add_argument("--seed", type=int, default=1)
        g.add_argument("--slo-p95", type=float, default=10, help="Objective: latency p95 in seconds (10)")
        g.add_argument("--slo-errors", type=float, default=1, help="Objective: error rate in %% (1)")
        g.add_argument("--slo-throughput", type=float, default=0, help="Objective: files/s (0 = none)")
        g.add_argument("--apdex", type=float, default=5, help="Apdex threshold T in seconds (5)")

    p = sub.add_parser("functional", help="Run the functional test suite")
    add_connection(p); add_common_test(p); add_functional(p); p.set_defaults(fn=cmd_functional)

    p = sub.add_parser("load", help="Run a load test and compute the KPIs")
    add_connection(p); add_common_test(p); add_load(p); add_watermark_options(p); p.set_defaults(fn=cmd_load)

    p = sub.add_parser("all", help="Functional tests, then load test, in one report")
    add_connection(p); add_common_test(p); add_functional(p); add_load(p); add_watermark_options(p)
    p.set_defaults(fn=cmd_all)

    p = sub.add_parser("report", help="Rebuild report.html from a results folder (optionally with other SLOs)")
    p.add_argument("dir")
    p.add_argument("--slo-p95", type=float); p.add_argument("--apdex", type=float)
    p.set_defaults(fn=cmd_report)

    a = ap.parse_args(argv)
    if a.cmd != "report":
        resolve(a)
    try:
        return a.fn(a)
    except ApiError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    except (ConnectionError, OSError) as e:
        print(f"connection error: {e}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
