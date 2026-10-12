"""
Report HTML autonomo (nessuna risorsa esterna): KPI, SLO, grafici SVG e tabelle.
Si apre con un doppio clic e si può allegare a una mail o a un ticket.
"""
from __future__ import annotations

import html
import math
from datetime import datetime
from typing import Any, Dict, List, Optional, Sequence, Tuple

from load import Row

CSS = """
:root{color-scheme:light;--bg:#f3f4f2;--surface:#fcfcfb;--ink:#0b0b0b;--ink2:#52514e;--muted:#7a7974;--rule:#dcdbd5;
--grid:#ebeae5;--s1:#2a78d6;--s1soft:rgba(42,120,214,.10);--band:#f0efea;--good:#0ca30c;--warn:#b07800;--bad:#d03b3b;
--font:"Segoe UI Variable Text","Segoe UI",system-ui,-apple-system,Roboto,sans-serif}
@media (prefers-color-scheme:dark){:root{color-scheme:dark;--bg:#121211;--surface:#1a1a19;--ink:#f0efec;--ink2:#c3c2b7;
--muted:#8f8e86;--rule:#33332f;--grid:#262624;--s1:#3987e5;--s1soft:rgba(57,135,229,.16);--band:#20201e;--good:#0ca30c;
--warn:#fab219;--bad:#e66767}}
*{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--ink);font:14px/1.5 var(--font)}
main{max-width:1180px;margin:0 auto;padding:28px 20px 60px}
h1{font-size:26px;margin:0 0 4px;font-weight:650;letter-spacing:-.01em}h2{font-size:18px;margin:36px 0 12px;font-weight:650}
h3{font-size:14px;margin:0 0 8px;font-weight:600;color:var(--ink2)}
.meta{color:var(--ink2);margin:0 0 20px}.meta code{font-size:12.5px}
.tiles{display:grid;grid-template-columns:repeat(auto-fit,minmax(160px,1fr));gap:12px}
.tile{background:var(--surface);border:1px solid var(--rule);border-radius:8px;padding:14px 16px}
.tile .label{color:var(--ink2);font-size:12.5px}.tile .value{font-size:26px;font-weight:650;font-variant-numeric:tabular-nums;line-height:1.2}
.tile .sub{color:var(--muted);font-size:12px}
.panel{background:var(--surface);border:1px solid var(--rule);border-radius:8px;padding:16px 18px;margin-top:12px}
.grid2{display:grid;grid-template-columns:1fr 1fr;gap:12px}
table{width:100%;border-collapse:collapse;font-variant-numeric:tabular-nums}
th{text-align:left;font-size:12px;font-weight:600;color:var(--ink2);padding:8px 10px;border-bottom:1px solid var(--rule);white-space:nowrap}
td{padding:7px 10px;border-top:1px solid var(--grid);vertical-align:top}td.n,th.n{text-align:right;white-space:nowrap}
.status{font-weight:600;white-space:nowrap}.status.pass{color:var(--good)}.status.fail{color:var(--bad)}
.status.warn{color:var(--warn)}.status.skip{color:var(--muted)}
.legend{display:flex;gap:16px;color:var(--ink2);font-size:12.5px;margin:4px 0 6px}
.legend i{display:inline-block;width:10px;height:10px;border-radius:50%;margin-right:6px;vertical-align:-1px}
svg{display:block;width:100%;height:auto}svg text{font:11px var(--font);fill:var(--muted)}
.axis line,.axis path{stroke:var(--rule)}.gridline{stroke:var(--grid)}
.note{color:var(--ink2);font-size:13px}.wrap{overflow-x:auto}
details summary{cursor:pointer;color:var(--ink2)}
.func td:first-child{white-space:nowrap}
@media (max-width:760px){.grid2{grid-template-columns:1fr}}
"""

STATUS_LABEL = {"pass": "✓ Pass", "fail": "✕ Fail", "warn": "! Warning", "skip": "– Skipped"}


def esc(v: Any) -> str:
    return html.escape(str(v))


def fmt_s(v: Optional[float]) -> str:
    if v is None:
        return "–"
    return f"{v * 1000:.0f} ms" if v < 1 else f"{v:.2f} s"


def nice_max(v: float) -> float:
    if v <= 0:
        return 1
    e = 10 ** math.floor(math.log10(v))
    for m in (1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10):
        if v <= m * e:
            return m * e
    return 10 * e


def nice_step(maxv: float, n: int = 5) -> float:
    """Passo "tondo" (1, 2, 2.5, 5 × 10^k) per avere al massimo n intervalli."""
    if maxv <= 0:
        return 1
    raw = maxv / n
    e = 10 ** math.floor(math.log10(raw))
    for m in (1, 2, 2.5, 5, 10):
        if raw <= m * e:
            return m * e
    return 10 * e


def ticks(maxv: float, n: int = 5) -> List[float]:
    step = nice_step(maxv, n)
    count = int(math.floor(maxv / step + 1e-9))
    return [step * i for i in range(count + 1)]


def axis_max(v: float, n: int = 5) -> float:
    """Massimo dell'asse allineato all'ultimo tick."""
    step = nice_step(v, n)
    return step * math.ceil(v / step - 1e-9) if v > 0 else 1


def tick_label(v: float, unit: str = "") -> str:
    s = f"{v:.0f}" if v >= 10 or v == 0 else f"{v:.1f}" if v >= 1 else f"{v:.2f}"
    return s + unit


# ---------------------------------------------------------------------- grafici

class Plot:
    """Area di disegno con margini e scala lineare."""

    def __init__(self, w: int, h: int, xmax: float, ymax: float, left: int = 48, bottom: int = 28, top: int = 10,
                 right: int = 12, xmin: float = 0):
        self.w, self.h, self.l, self.b, self.t, self.r = w, h, left, bottom, top, right
        self.xmin, self.xmax, self.ymax = xmin, xmax or 1, ymax or 1
        self.parts: List[str] = []

    def x(self, v: float) -> float:
        return self.l + (v - self.xmin) / (self.xmax - self.xmin or 1) * (self.w - self.l - self.r)

    def y(self, v: float) -> float:
        return self.h - self.b - v / self.ymax * (self.h - self.b - self.t)

    def axes(self, yunit: str = "", xunit: str = "", xticks: Optional[Sequence[Tuple[float, str]]] = None) -> None:
        for v in ticks(self.ymax):
            y = self.y(v)
            self.parts.append(f'<line class="gridline" x1="{self.l}" x2="{self.w - self.r}" y1="{y:.1f}" y2="{y:.1f}"/>')
            self.parts.append(f'<text x="{self.l - 6}" y="{y + 4:.1f}" text-anchor="end">{tick_label(v, yunit)}</text>')
        base = self.h - self.b
        self.parts.append(f'<g class="axis"><line x1="{self.l}" x2="{self.w - self.r}" y1="{base}" y2="{base}"/></g>')
        if xticks is None:
            xticks = [(v, tick_label(v, xunit)) for v in ticks(self.xmax, 8)]
        for v, label in xticks:
            self.parts.append(f'<text x="{self.x(v):.1f}" y="{base + 17}" text-anchor="middle">{esc(label)}</text>')

    def svg(self, label: str) -> str:
        return (f'<svg viewBox="0 0 {self.w} {self.h}" role="img" aria-label="{esc(label)}">'
                + "".join(self.parts) + "</svg>")


def chart_latency_over_time(rows: List[Row], windows: List[Dict[str, Any]], slo_p95: Optional[float]) -> str:
    if not rows:
        return ""
    xmax = max(max((r.t + r.latency for r in rows), default=1), max((w["end"] for w in windows), default=1))
    lat = sorted(r.latency for r in rows)
    cap = lat[min(len(lat) - 1, int(len(lat) * 0.995))] * 1.15
    ymax = axis_max(max(cap, (slo_p95 or 0) * 1.1))
    p = Plot(1100, 300, xmax, ymax)
    for i, w in enumerate(windows):
        if i % 2 == 0:
            p.parts.append(f'<rect x="{p.x(w["start"]):.1f}" y="{p.t}" width="{p.x(w["end"]) - p.x(w["start"]):.1f}" '
                           f'height="{p.h - p.b - p.t}" fill="var(--band)"/>')
        p.parts.append(f'<text x="{(p.x(w["start"]) + p.x(w["end"])) / 2:.1f}" y="{p.t + 12}" text-anchor="middle">'
                       f'{w["concurrency"]} VU</text>')
    p.axes(yunit=" s", xunit=" s")
    if slo_p95:
        y = p.y(slo_p95)
        p.parts.append(f'<line x1="{p.l}" x2="{p.w - p.r}" y1="{y:.1f}" y2="{y:.1f}" stroke="var(--ink2)" '
                       f'stroke-dasharray="4 4" stroke-width="1"/><text x="{p.l + 6}" y="{y + 14:.1f}" '
                       f'style="fill:var(--ink2)">SLO p95 {slo_p95:g} s</text>')
    pts = []
    for r in rows:
        x, yv = p.x(r.t + r.latency), min(r.latency, ymax)
        tip = (f"{r.file} · {fmt_s(r.latency)} · HTTP {r.status or '—'}"
               + (f" · {r.error}" if r.error else "") + f" · {r.concurrency} VU")
        if r.ok:
            pts.append(f'<circle cx="{x:.1f}" cy="{p.y(yv):.1f}" r="3" fill="var(--s1)" fill-opacity=".55">'
                       f'<title>{esc(tip)}</title></circle>')
        else:
            y = p.y(yv)
            pts.append(f'<path d="M{x - 4:.1f} {y - 4:.1f}l8 8m0 -8l-8 8" stroke="var(--bad)" stroke-width="2">'
                       f'<title>{esc(tip)}</title></path>')
    p.parts += pts
    return p.svg("Latency of each request over time")


def chart_throughput_over_time(rows: List[Row], bucket: float = 5) -> str:
    done = [r.t + r.latency for r in rows if r.ok]
    if not done:
        return ""
    # Solo intervalli completi: l'ultimo, parziale, sembrerebbe un calo di prestazioni.
    n = max(1, int(max(done) // bucket))
    counts = [0] * n
    for t in done:
        if int(t // bucket) < n:
            counts[int(t // bucket)] += 1
    rates = [c / bucket for c in counts]
    p = Plot(1100, 220, n * bucket, axis_max(max(rates) * 1.05))
    p.axes(xunit=" s")
    pts = " ".join(f"{p.x((i + 0.5) * bucket):.1f},{p.y(v):.1f}" for i, v in enumerate(rates))
    p.parts.append(f'<polyline points="{pts}" fill="none" stroke="var(--s1)" stroke-width="2" stroke-linejoin="round"/>')
    for i, v in enumerate(rates):
        p.parts.append(f'<circle cx="{p.x((i + 0.5) * bucket):.1f}" cy="{p.y(v):.1f}" r="7" fill="transparent">'
                       f'<title>{i * bucket:.0f}–{(i + 1) * bucket:.0f} s: {v:.2f} files/s</title></circle>')
    return p.svg("Completed files per second over time")


def chart_bars(labels: List[str], values: List[float], unit: str, label: str, fmt=lambda v: f"{v:.2f}",
               ref: Optional[float] = None, ref_label: str = "") -> str:
    if not values:
        return ""
    ymax = axis_max(max(values + ([ref * 1.1] if ref else [])) * 1.08)
    p = Plot(540, 240, len(values), ymax, left=52)
    slot = (p.w - p.l - p.r) / len(values)
    p.axes(yunit=unit, xticks=[(i + 0.5, lab) for i, lab in enumerate(labels)])
    for i, v in enumerate(values):
        bw = min(48, slot * 0.6)
        x = p.l + slot * i + (slot - bw) / 2
        y = p.y(v)
        h = p.h - p.b - y
        r = min(4, h / 2)
        # Barra con estremità superiore arrotondata, base squadrata sull'asse.
        p.parts.append(f'<path d="M{x:.1f} {p.h - p.b}V{y + r:.1f}q0 -{r:.1f} {r:.1f} -{r:.1f}h{bw - 2 * r:.1f}'
                       f'q{r:.1f} 0 {r:.1f} {r:.1f}V{p.h - p.b}z" fill="var(--s1)"><title>{esc(labels[i])}: '
                       f'{fmt(v)}{unit}</title></path>')
        p.parts.append(f'<text x="{x + bw / 2:.1f}" y="{y - 5:.1f}" text-anchor="middle" style="fill:var(--ink2)">'
                       f'{fmt(v)}</text>')
    if ref:
        y = p.y(ref)
        p.parts.append(f'<line x1="{p.l}" x2="{p.w - p.r}" y1="{y:.1f}" y2="{y:.1f}" stroke="var(--ink2)" '
                       f'stroke-dasharray="4 4"/><text x="{p.l + 6}" y="{y - 5:.1f}" style="fill:var(--ink2)">{esc(ref_label)}</text>')
    return p.svg(label)


# ---------------------------------------------------------------------- pagina

def tile(label: str, value: str, sub: str = "") -> str:
    return (f'<div class="tile"><div class="label">{esc(label)}</div><div class="value">{esc(value)}</div>'
            + (f'<div class="sub">{esc(sub)}</div>' if sub else "") + "</div>")


def status_cell(ok: bool) -> str:
    s = "pass" if ok else "fail"
    return f'<td class="status {s}">{STATUS_LABEL[s]}</td>'


def functional_section(func: Dict[str, Any]) -> str:
    rows = []
    for c in func["cases"]:
        rows.append(f'<tr><td>{esc(c["id"])}</td><td>{esc(c["category"])}</td><td>{esc(c["name"])}</td>'
                    f'<td class="status {c["status"]}">{STATUS_LABEL[c["status"]]}</td>'
                    f'<td class="n">{c["http_status"] or ""}</td><td class="n">{fmt_s(c["duration"])}</td>'
                    f'<td>{esc(c["message"])}</td></tr>')
    tiles = "".join([
        tile("Pass rate", f'{func["passRate"]:.1f} %', "pass + warning on executed tests"),
        tile("Passed", str(func["pass"])), tile("Failed", str(func["fail"])),
        tile("Warnings", str(func["warn"])), tile("Skipped", str(func["skip"])),
        tile("Duration", fmt_s(func["duration"])),
    ])
    return (f'<h2>Functional tests</h2><div class="tiles">{tiles}</div>'
            f'<div class="panel wrap"><table class="func"><thead><tr><th>ID</th><th>Area</th><th>Test</th><th>Result</th>'
            f'<th class="n">HTTP</th><th class="n">Time</th><th>Details</th></tr></thead><tbody>{"".join(rows)}'
            f'</tbody></table></div>')


def dist_cells(d: Dict[str, float], keys=("p50", "p90", "p95", "p99", "max")) -> str:
    return "".join(f'<td class="n">{fmt_s(d.get(k)) if d.get("n") else "–"}</td>' for k in keys)


def load_section(k: Dict[str, Any], rows: List[Row], windows: List[Dict[str, Any]]) -> str:
    o, cap, cfg = k["overall"], k["capacity"], k["config"]
    slo = k["slo"]
    tiles = "".join([
        tile("Throughput", f'{o["throughput"]:.2f} files/s', f'{o["filesPerMinute"]:.0f} files/min overall'),
        tile("Peak throughput", f'{cap["peakThroughput"]:.2f} files/s', f'at {cap["peakConcurrency"]} VU'),
        tile("Latency p95", fmt_s(o["latency"].get("p95")), f'p50 {fmt_s(o["latency"].get("p50"))}'),
        tile("Error rate", f'{o["errorRate"]:.2f} %', f'{o["errors"]} of {o["requests"]} requests'),
        tile("Apdex", f'{o["apdex"]:.2f}', f'T = {k["apdexT"]:g} s'),
        tile("Data processed", f'{o["mbIn"]:.1f} MB', f'{o["mbInPerSec"]:.2f} MB/s in'),
    ])
    sus = cap["maxSustainableConcurrency"]
    capacity = (f'<p class="note">Highest load that meets the SLOs: <strong>{sus} virtual users, '
                f'{cap["maxSustainableThroughput"]:.2f} files/s</strong>.' if sus else
                '<p class="note">No step met all the SLOs.')
    if cap["kneeConcurrency"]:
        capacity += (f' Throughput stops growing (less than +10%) after <strong>{cap["kneeConcurrency"]} '
                     f'virtual users</strong>: beyond that, more users only add waiting time.')
    capacity += "</p>"

    slo_rows = "".join(f'<tr><td>{esc(s["name"])}</td><td>{esc(s["target"])}</td><td class="n">{esc(s["value"])}</td>'
                       f'{status_cell(s["pass"])}</tr>' for s in k["sloResults"])

    step_rows = "".join(
        f'<tr><td class="n">{s["concurrency"]}</td><td class="n">{s["requests"]}</td><td class="n">{s["throughput"]:.2f}</td>'
        f'<td class="n">{s["mbInPerSec"]:.2f}</td>{dist_cells(s["latency"], ("p50", "p95", "p99"))}'
        f'<td class="n">{s["errorRate"]:.2f} %</td><td class="n">{s["apdex"]:.2f}</td>{status_cell(s["meetsSlo"])}</tr>'
        for s in k["steps"])

    fmt_rows = "".join(
        f'<tr><td>{esc(f["format"])}</td><td class="n">{f["requests"]}</td><td class="n">{f["avgSizeKB"]:.0f} KB</td>'
        f'{dist_cells(f["latency"], ("p50", "p95", "max"))}<td class="n">{fmt_s(f["server"].get("p50")) if f["server"].get("n") else "–"}</td>'
        f'<td class="n">{f["serverMBps"].get("p50", 0):.2f}</td><td class="n">{f["errorRate"]:.2f} %</td></tr>'
        for f in k["formats"])

    errs = o["errorsByType"]
    err_rows = "".join(f'<tr><td>{esc(e)}</td><td class="n">{n}</td><td class="n">{100 * n / o["requests"]:.2f} %</td></tr>'
                       for e, n in sorted(errs.items(), key=lambda x: -x[1]))
    samples = {}
    for r in rows:
        if not r.ok and r.error not in samples:
            samples[r.error] = f"HTTP {r.status or '—'} {r.file}: {r.detail}"
    err_samples = "".join(f"<li><strong>{esc(e)}</strong>: {esc(d)}</li>" for e, d in samples.items())

    phases = ""
    if o["latency"].get("n"):
        names = [("Upload", "upload"), ("Server", "server"), ("Download", "download")]
        if cfg.get("mode") == "async":
            names.insert(1, ("Queue wait", "queueWait"))
        phases = "".join(f'<tr><td>{n}</td>{dist_cells(o[key], ("p50", "p95", "max"))}</tr>' for n, key in names)

    labels = [f'{s["concurrency"]} VU' for s in k["steps"]]
    slo_label = f'SLO p95 {slo["p95"]:g} s' if slo.get("p95") else ""
    step_charts = ""
    if len(k["steps"]) > 1:
        step_charts = (
            '<div class="grid2">'
            f'<div class="panel"><h3>Throughput by concurrency (files/s)</h3>'
            f'{chart_bars(labels, [s["throughput"] for s in k["steps"]], "", "Throughput by concurrency")}</div>'
            f'<div class="panel"><h3>Latency p95 by concurrency (s)</h3>'
            f'{chart_bars(labels, [s["latency"].get("p95", 0) for s in k["steps"]], "", "Latency p95 by concurrency", ref=slo.get("p95"), ref_label=slo_label)}</div>'
            '</div>')

    fmt_labels = [f["format"] for f in k["formats"] if f["latency"].get("n")]
    fmt_p95 = [f["latency"]["p95"] for f in k["formats"] if f["latency"].get("n")]
    fmt_speed = [f["serverMBps"].get("p50", 0) for f in k["formats"] if f["latency"].get("n")]

    return f"""
<h2>Load test</h2>
<div class="tiles">{tiles}</div>
<div class="panel">{capacity}
<table><thead><tr><th>Objective (SLO)</th><th>Target</th><th class="n">Measured</th><th>Result</th></tr></thead><tbody>{slo_rows}</tbody></table></div>

<div class="panel"><h3>Latency of each request</h3>
<div class="legend"><span><i style="background:var(--s1)"></i>Completed</span><span><i style="background:var(--bad)"></i>Error (✕)</span><span>Shaded bands: load steps</span></div>
{chart_latency_over_time(rows, windows, slo.get("p95"))}</div>

<div class="panel"><h3>Completed files per second</h3>{chart_throughput_over_time(rows)}</div>
{step_charts}

<h2>By load step</h2>
<div class="panel wrap"><table><thead><tr><th class="n">Virtual users</th><th class="n">Requests</th><th class="n">Files/s</th>
<th class="n">MB/s in</th><th class="n">p50</th><th class="n">p95</th><th class="n">p99</th><th class="n">Errors</th><th class="n">Apdex</th><th>SLO</th></tr></thead>
<tbody>{step_rows}</tbody></table></div>

<h2>By format</h2>
<div class="panel wrap"><table><thead><tr><th>Format</th><th class="n">Requests</th><th class="n">Avg size</th>
<th class="n">p50</th><th class="n">p95</th><th class="n">Max</th><th class="n">Server p50</th><th class="n">MB/s server</th><th class="n">Errors</th></tr></thead>
<tbody>{fmt_rows}</tbody></table></div>
<div class="grid2"><div class="panel"><h3>Latency p95 by format (s)</h3>{chart_bars(fmt_labels, fmt_p95, "", "Latency p95 by format")}</div>
<div class="panel"><h3>Server speed by format (MB/s, median)</h3>{chart_bars(fmt_labels, fmt_speed, "", "Server speed by format")}</div></div>

<h2>Where the time goes</h2>
<div class="panel wrap"><p class="note">Upload: sending the file. Server: {"processing on the server (from the job timestamps)" if cfg.get("mode") == "async" else "from the end of the upload to the first byte of the answer (processing + queue)"}. Download: receiving the result.</p>
<table><thead><tr><th>Phase</th><th class="n">p50</th><th class="n">p95</th><th class="n">Max</th></tr></thead><tbody>{phases}</tbody></table></div>

<h2>Errors</h2>
<div class="panel wrap">{"<table><thead><tr><th>Type</th><th class='n'>Count</th><th class='n'>Share</th></tr></thead><tbody>" + err_rows + "</tbody></table><ul>" + err_samples + "</ul>" if errs else '<p class="note">No errors.</p>'}
<p class="note">rate_limited = 429 (token limit), busy_503 = server queue full, timeout/connection = no answer from the server.</p></div>
"""


def render(title: str, meta: Dict[str, Any], functional: Optional[Dict[str, Any]] = None,
           kpis: Optional[Dict[str, Any]] = None, rows: Optional[List[Row]] = None,
           windows: Optional[List[Dict[str, Any]]] = None) -> str:
    meta_html = " · ".join(f"{esc(k)}: <code>{esc(v)}</code>" for k, v in meta.items() if v not in (None, ""))
    body = ""
    if functional:
        body += functional_section(functional)
    if kpis:
        body += load_section(kpis, rows or [], windows or [])
        cfg = kpis["config"]
        body += ('<h2>Configuration</h2><div class="panel wrap"><table><tbody>'
                 + "".join(f"<tr><td>{esc(k)}</td><td><code>{esc(v)}</code></td></tr>" for k, v in cfg.items())
                 + "</tbody></table></div>")
    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>{esc(title)}</title><style>{CSS}</style></head>
<body><main><h1>{esc(title)}</h1><p class="meta">{meta_html}</p>{body}
<p class="note" style="margin-top:40px">Generated by wmtest on {datetime.now():%Y-%m-%d %H:%M}.</p></main></body></html>"""
