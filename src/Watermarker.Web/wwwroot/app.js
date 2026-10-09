"use strict";

// ------------------------------------------------------------------ stato

const $ = (id) => document.getElementById(id);
const els = {
  form: $("form"), file: $("file"), drop: $("drop"), dropTitle: $("drop-title"), dropHint: $("drop-hint"),
  formatNote: $("format-note"), text: $("text"), angle: $("angle"), size: $("size"), opacity: $("opacity"),
  color: $("color"), font: $("font"), tile: $("tile"), tileRow: $("tile-row"), slides: $("pptxSlides"),
  slidesRow: $("slides-row"), submit: $("submit"), progress: $("progress"), bar: $("progress-bar"),
  status: $("status"), canvas: $("canvas"), caption: $("preview-caption"),
};

let caps = { native: [], pdf: [], converterAvailable: false, maxUploadBytes: 100 * 1024 * 1024, fonts: ["Arial"] };
let currentFile = null;
let previewImage = null;
let busy = false;

const KINDS = {
  slide: [".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".ppt", ".pot", ".pps", ".odp", ".otp"],
  image: [".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".webp", ".tif", ".tiff"],
  video: [".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi"],
  sheet: [".xlsx", ".xlsm", ".xltx", ".xltm", ".xls", ".xlt", ".ods", ".ots", ".csv"],
  tileable: [".pdf", ".xps", ".oxps"],
};
const SETTINGS_KEY = "watermark-settings-v2";

// ------------------------------------------------------------------ avvio

init();

async function init() {
  restoreSettings();
  bindEvents();
  try {
    const res = await fetch("api/capabilities");
    if (res.ok) caps = await res.json();
  } catch { /* il portale funziona comunque, il server segnalerà i formati non gestiti */ }

  els.font.replaceChildren(...caps.fonts.map((f) => new Option(f, f)));
  const saved = readSettings();
  if (saved?.font && caps.fonts.includes(saved.font)) els.font.value = saved.font;
  els.text.maxLength = caps.maxTextLength || 200;
  els.file.accept = [...new Set([...caps.native, ...caps.pdf])].join(",");
  els.dropHint.textContent = `Documents, PDFs, images, ZIP archives, emails${caps.native.includes(".mp4") ? " and videos" : ""} · up to ${formatBytes(caps.maxUploadBytes)}`;

  updateOutputs();
  updateAvailability();
  draw();
}

function bindEvents() {
  for (const input of [els.text, els.angle, els.size, els.opacity, els.font, els.tile]) {
    input.addEventListener("input", () => { updateOutputs(); draw(); saveSettings(); });
  }
  els.color.addEventListener("input", () => { selectSwatch(els.color.value); draw(); saveSettings(); });

  document.querySelectorAll(".swatch[data-color]").forEach((b) =>
    b.addEventListener("click", () => { els.color.value = b.dataset.color; selectSwatch(b.dataset.color); draw(); saveSettings(); }));

  document.querySelectorAll(".token").forEach((b) => b.addEventListener("click", () => insertToken(b.dataset.token)));

  els.form.querySelectorAll('input[name="output"]').forEach((r) => r.addEventListener("change", () => { updateAvailability(); draw(); }));

  els.file.addEventListener("change", () => setFile(els.file.files[0] ?? null));
  for (const type of ["dragenter", "dragover"]) {
    els.drop.addEventListener(type, (e) => { e.preventDefault(); els.drop.classList.add("over"); });
  }
  for (const type of ["dragleave", "drop"]) {
    els.drop.addEventListener(type, () => els.drop.classList.remove("over"));
  }
  els.drop.addEventListener("drop", (e) => {
    e.preventDefault();
    const f = e.dataTransfer?.files?.[0];
    if (f) {
      const dt = new DataTransfer();
      dt.items.add(f);
      els.file.files = dt.files;
      setFile(f);
    }
  });

  els.form.addEventListener("submit", (e) => { e.preventDefault(); submit(); });
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener?.("change", draw);
}

// ------------------------------------------------------------------ file e formati

function extensionOf(name) {
  const i = name.lastIndexOf(".");
  return i < 0 ? "" : name.slice(i).toLowerCase();
}

function setFile(file) {
  currentFile = file;
  previewImage = null;
  setStatus("");

  if (!file) {
    els.drop.classList.remove("has-file");
    els.dropTitle.textContent = "Drop a file here or choose one from your computer";
    updateAvailability();
    draw();
    return;
  }

  els.drop.classList.add("has-file");
  els.dropTitle.textContent = file.name;
  els.dropHint.textContent = `${formatBytes(file.size)} · drop another file to replace it`;

  if (KINDS.image.includes(extensionOf(file.name)) && file.size < 40 * 1024 * 1024) {
    const img = new Image();
    img.onload = () => { previewImage = img; draw(); };
    img.src = URL.createObjectURL(file);
  }

  updateAvailability();
  draw();
}

/** Abilita le opzioni in base al formato del file scelto e a ciò che il server sa fare. */
function updateAvailability() {
  const ext = currentFile ? extensionOf(currentFile.name) : "";
  const native = caps.native.includes(ext);
  const pdf = caps.pdf.includes(ext);
  const original = els.form.querySelector('input[name="output"][value="original"]');
  const toPdf = els.form.querySelector('input[name="output"][value="pdf"]');

  original.disabled = !!currentFile && !native;
  toPdf.disabled = !!currentFile && !pdf;
  if (original.disabled && !toPdf.disabled) toPdf.checked = true;
  if (toPdf.disabled && !original.disabled) original.checked = true;

  let note = "", warn = false;
  if (currentFile) {
    if (!native && !pdf) {
      note = ext
        ? `${ext} files are not supported${caps.converterAvailable ? "" : " (PDF conversion is not enabled on this server)"}.`
        : "The file has no extension: its format will be detected from its content.";
      warn = !!ext;
    } else if (!native) {
      note = "This format cannot be watermarked directly: it will be converted to PDF.";
    } else if (!pdf && ext !== ".pdf") {
      note = "This format is kept as it is.";
    }
    if (currentFile.size > caps.maxUploadBytes) {
      note = `The file exceeds the ${formatBytes(caps.maxUploadBytes)} limit.`;
      warn = true;
    }
  }
  els.formatNote.hidden = !note;
  els.formatNote.textContent = note;
  els.formatNote.classList.toggle("warn", warn);

  const output = selectedOutput();
  const tileable = output === "pdf" || KINDS.tileable.includes(ext) || KINDS.image.includes(ext) || KINDS.video.includes(ext) || !currentFile;
  els.tileRow.hidden = !tileable;
  els.slidesRow.hidden = !(output === "original" && KINDS.slide.includes(ext) && native);

  els.submit.disabled = busy || !currentFile || warn || (original.disabled && toPdf.disabled && !!ext);
}

function selectedOutput() {
  return els.form.querySelector('input[name="output"]:checked')?.value ?? "original";
}

// ------------------------------------------------------------------ anteprima

function expandedText() {
  const now = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  return els.text.value
    .replace(/\{(data|date)\}/gi, `${pad(now.getDate())}/${pad(now.getMonth() + 1)}/${now.getFullYear()}`)
    .replace(/\{(ora|time)\}/gi, `${pad(now.getHours())}:${pad(now.getMinutes())}`)
    .replace(/\{(nomefile|filename)\}/gi, currentFile?.name ?? "document.pdf")
    .replace(/\{(utente|user)\}/gi, "")
    .trim();
}

/** Righe del watermark, come le calcola il server: a capo come separatore, righe vuote ignorate. */
function textLines() {
  return expandedText().split(/\r?\n/).map((l) => l.trim()).filter((l) => l.length > 0);
}

/** Formato del foglio in anteprima, in punti tipografici. */
function sheetFormat() {
  const ext = currentFile ? extensionOf(currentFile.name) : "";
  if (previewImage) return { w: previewImage.naturalWidth, h: previewImage.naturalHeight, kind: "image" };
  if (selectedOutput() !== "pdf" || !currentFile) {
    if (KINDS.slide.includes(ext)) return { w: 960, h: 540, kind: "slide" };
    if (KINDS.video.includes(ext)) return { w: 960, h: 540, kind: "video" };
  }
  if (KINDS.sheet.includes(ext) && selectedOutput() !== "pdf") return { w: 842, h: 595, kind: "sheet" };
  return { w: 595, h: 842, kind: "page" };
}

function draw() {
  const c = els.canvas;
  const fmt = sheetFormat();
  const longSide = 900;
  const scale = longSide / Math.max(fmt.w, fmt.h);
  const margin = 28; // spazio per i segni di taglio
  const W = Math.round(fmt.w * scale), H = Math.round(fmt.h * scale);
  const dpr = Math.min(window.devicePixelRatio || 1, 2);

  c.width = (W + margin * 2) * dpr;
  c.height = (H + margin * 2) * dpr;
  c.style.aspectRatio = `${W + margin * 2} / ${H + margin * 2}`;
  const g = c.getContext("2d");
  g.setTransform(dpr, 0, 0, dpr, 0, 0);
  g.clearRect(0, 0, W + margin * 2, H + margin * 2);
  g.translate(margin, margin);

  // Foglio
  g.save();
  g.shadowColor = "rgba(22, 35, 47, 0.18)";
  g.shadowBlur = 14;
  g.shadowOffsetY = 3;
  g.fillStyle = fmt.kind === "video" ? "#20303c" : "#ffffff";
  g.fillRect(0, 0, W, H);
  g.restore();

  if (previewImage) g.drawImage(previewImage, 0, 0, W, H);
  else drawPlaceholderContent(g, fmt, W, H);

  drawCropMarks(g, W, H);

  // Watermark: stessa proporzione del server (dimensione in punti su lato lungo A4 per immagini e video).
  const text = expandedText();
  if (text) {
    const pxPerPt = (fmt.kind === "image" || fmt.kind === "video") ? Math.max(W, H) / 842 : scale;
    const fontPx = Math.max(4, Number(els.size.value) * pxPerPt);
    g.save();
    g.beginPath();
    g.rect(0, 0, W, H);
    g.clip();
    g.font = `700 ${fontPx}px "${els.font.value || "Arial"}", Arial, sans-serif`;
    g.fillStyle = els.color.value;
    g.globalAlpha = Number(els.opacity.value) / 100;
    g.textAlign = "center";
    g.textBaseline = "middle";
    g.translate(W / 2, H / 2);
    g.rotate((-Number(els.angle.value) * Math.PI) / 180);

    const lines = textLines();
    const lineGap = fontPx * 1.2;
    const drawBlock = (x, y) => lines.forEach((line, i) => g.fillText(line, x, y + (i - (lines.length - 1) / 2) * lineGap));

    const tileable = !els.tileRow.hidden && els.tile.checked;
    if (tileable) {
      const tw = Math.max(...lines.map((l) => g.measureText(l).width));
      const blockH = fontPx + (lines.length - 1) * lineGap;
      const stepX = tw + fontPx * 1.5, stepY = blockH + fontPx * 3;
      const r = Math.hypot(W, H) / 2 + Math.max(stepX, stepY);
      let row = 0;
      for (let y = -r; y <= r; y += stepY, row++) {
        for (let x = -r + (row % 2 ? stepX / 2 : 0); x <= r; x += stepX) drawBlock(x, y);
      }
    } else {
      drawBlock(0, 0);
    }
    g.restore();
  }

  const labels = { page: "an A4 page", slide: "a 16:9 slide", sheet: "a landscape sheet", video: "a video frame", image: "the uploaded image" };
  els.caption.textContent = fmt.kind === "image"
    ? "Preview on the uploaded image"
    : `Approximate preview on ${labels[fmt.kind]}`;
}

/** Righe di testo finte, per giudicare la leggibilità del watermark sopra il contenuto. */
function drawPlaceholderContent(g, fmt, W, H) {
  g.save();
  if (fmt.kind === "video") {
    g.fillStyle = "#2c4252";
    g.beginPath();
    g.moveTo(W / 2 - 22, H / 2 - 28); g.lineTo(W / 2 + 30, H / 2); g.lineTo(W / 2 - 22, H / 2 + 28);
    g.fill();
  } else if (fmt.kind === "sheet") {
    g.strokeStyle = "#dfe5ea";
    g.lineWidth = 1;
    const cw = W / 12, rh = H / 26;
    for (let x = 0; x <= W; x += cw) { g.beginPath(); g.moveTo(x, 0); g.lineTo(x, H); g.stroke(); }
    for (let y = 0; y <= H; y += rh) { g.beginPath(); g.moveTo(0, y); g.lineTo(W, y); g.stroke(); }
    g.fillStyle = "#b9c4ce";
    for (let r = 1; r < 18; r++) for (let col = 0; col < 8; col++) {
      if ((r * 7 + col * 3) % 5 === 0) continue;
      g.fillRect(col * cw + cw * 0.15, r * rh + rh * 0.35, cw * (0.4 + ((r + col) % 4) * 0.12), rh * 0.3);
    }
  } else {
    const m = fmt.kind === "slide" ? W * 0.08 : W * 0.12;
    g.fillStyle = "#3b4b59";
    g.fillRect(m, m * 0.9, W * (fmt.kind === "slide" ? 0.5 : 0.45), H * 0.022);
    g.fillStyle = "#c3ccd4";
    const lineH = H * (fmt.kind === "slide" ? 0.045 : 0.021);
    let y = m * 0.9 + H * 0.06;
    let i = 0;
    while (y < H - m) {
      const w = (i % 6 === 5) ? 0.55 : 0.92 - ((i * 37) % 9) * 0.015;
      g.fillRect(m, y, (W - 2 * m) * w, lineH * 0.42);
      y += (i % 6 === 5) ? lineH * 1.9 : lineH;
      i++;
      if (fmt.kind === "slide" && i > 6) break;
    }
  }
  g.restore();
}

/** Segni di taglio agli angoli, come su una prova di stampa. */
function drawCropMarks(g, W, H) {
  const dark = window.matchMedia("(prefers-color-scheme: dark)").matches;
  g.save();
  g.strokeStyle = dark ? "#9fb0bf" : "#566676";
  g.lineWidth = 1;
  const gap = 6, len = 16;
  for (const [x, y, sx, sy] of [[0, 0, -1, -1], [W, 0, 1, -1], [0, H, -1, 1], [W, H, 1, 1]]) {
    g.beginPath();
    g.moveTo(x + sx * gap, y + 0.5); g.lineTo(x + sx * (gap + len), y + 0.5);
    g.moveTo(x + 0.5, y + sy * gap); g.lineTo(x + 0.5, y + sy * (gap + len));
    g.stroke();
  }
  g.restore();
}

// ------------------------------------------------------------------ invio

function submit() {
  if (!currentFile || busy) return;
  if (!expandedText()) { setStatus("Enter the watermark text.", "error"); els.text.focus(); return; }
  if (textLines().length > (caps.maxLines || 10)) {
    setStatus(`The watermark can have at most ${caps.maxLines || 10} lines.`, "error"); els.text.focus(); return;
  }

  const data = new FormData();
  data.append("file", currentFile, currentFile.name);
  data.append("text", els.text.value);
  data.append("angle", els.angle.value);
  data.append("size", els.size.value);
  data.append("opacity", String(Number(els.opacity.value) / 100));
  data.append("color", els.color.value);
  data.append("font", els.font.value);
  data.append("output", selectedOutput());
  data.append("tile", String(!els.tileRow.hidden && els.tile.checked));
  data.append("pptxSlides", String(!els.slidesRow.hidden && els.slides.checked));

  const xhr = new XMLHttpRequest();
  xhr.open("POST", "api/watermark");
  xhr.responseType = "blob";

  setBusy(true);
  setStatus("Uploading the file…");
  xhr.upload.onprogress = (e) => {
    if (e.lengthComputable) els.bar.style.width = `${Math.round((e.loaded / e.total) * 100)}%`;
  };
  xhr.upload.onload = () => {
    els.progress.classList.add("busy");
    setStatus("Applying the watermark…");
  };
  xhr.onerror = () => { setBusy(false); setStatus("The server is not responding. Check your connection and try again.", "error"); };
  xhr.onload = async () => {
    setBusy(false);
    if (xhr.status === 200) {
      const name = fileNameFrom(xhr.getResponseHeader("Content-Disposition")) ?? "document_watermark";
      download(xhr.response, name);
      showDone(name, decodeWarnings(xhr.getResponseHeader("X-Watermark-Warnings")));
    } else {
      showError(xhr.status, await readProblem(xhr.response));
    }
  };
  xhr.send(data);
}

function setBusy(value) {
  busy = value;
  els.progress.hidden = !value;
  els.progress.classList.remove("busy");
  els.bar.style.width = "0";
  els.submit.textContent = value ? "Processing…" : "Apply watermark and download";
  updateAvailability();
}

function download(blob, name) {
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url;
  a.download = name;
  document.body.append(a);
  a.click();
  a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 60_000);
}

function showDone(name, warnings) {
  els.status.replaceChildren();
  const p = document.createElement("p");
  p.className = "done";
  p.textContent = `Downloaded ${name}`;
  els.status.append(p);
  if (warnings.length) {
    const ul = document.createElement("ul");
    for (const w of warnings) {
      const li = document.createElement("li");
      li.textContent = w;
      ul.append(li);
    }
    els.status.append(ul);
  }
}

function showError(status, problem) {
  const fallback = {
    413: "The file is too large for this server.",
    503: "The server is busy with other files. Try again in a moment.",
  };
  const detail = problem?.detail || fallback[status] || `Error ${status}.`;
  setStatus(problem?.title ? `${problem.title}. ${detail}` : detail, "error");
}

function setStatus(message, kind) {
  els.status.replaceChildren();
  if (!message) return;
  const p = document.createElement("p");
  if (kind) p.className = kind;
  p.textContent = message;
  els.status.append(p);
}

async function readProblem(blob) {
  try { return JSON.parse(await blob.text()); } catch { return null; }
}

function decodeWarnings(header) {
  if (!header) return [];
  try {
    const bytes = Uint8Array.from(atob(header), (ch) => ch.charCodeAt(0));
    return JSON.parse(new TextDecoder().decode(bytes));
  } catch { return []; }
}

function fileNameFrom(disposition) {
  if (!disposition) return null;
  const star = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
  if (star) return decodeURIComponent(star[1]);
  const plain = /filename="?([^";]+)"?/i.exec(disposition);
  return plain ? plain[1] : null;
}

// ------------------------------------------------------------------ utilità

function updateOutputs() {
  const n = textLines().length;
  const max = caps.maxLines || 10;
  $("lines-out").textContent = n > 1 ? `${n} lines${n > max ? ` (max ${max})` : ""}` : "";
  $("lines-out").classList.toggle("over", n > max);
  $("angle-out").textContent = `${els.angle.value}°`;
  $("size-out").textContent = `${els.size.value} pt`;
  $("opacity-out").textContent = `${els.opacity.value}%`;
}

function selectSwatch(color) {
  document.querySelectorAll(".swatch").forEach((s) => {
    const match = s.dataset.color?.toLowerCase() === color.toLowerCase();
    s.setAttribute("role", "radio");
    s.setAttribute("aria-checked", String(match || (s.classList.contains("custom") && !isPreset(color))));
  });
}

function isPreset(color) {
  return [...document.querySelectorAll(".swatch[data-color]")].some((s) => s.dataset.color.toLowerCase() === color.toLowerCase());
}

function insertToken(token) {
  const t = els.text;
  const start = t.selectionStart ?? t.value.length, end = t.selectionEnd ?? t.value.length;
  const before = t.value.slice(0, start), after = t.value.slice(end);
  const spacer = before && !/\s$/.test(before) ? " " : "";
  t.value = `${before}${spacer}${token}${after}`;
  t.focus();
  const pos = (before + spacer + token).length;
  t.setSelectionRange(pos, pos);
  draw();
  saveSettings();
}

function formatBytes(n) {
  if (n >= 1024 * 1024 * 1024) return `${(n / 1024 / 1024 / 1024).toFixed(1)} GB`;
  if (n >= 1024 * 1024) return `${Math.round(n / 1024 / 1024)} MB`;
  return `${Math.max(1, Math.round(n / 1024))} KB`;
}

function readSettings() {
  try { return JSON.parse(localStorage.getItem(SETTINGS_KEY) ?? "null"); } catch { return null; }
}

function saveSettings() {
  try {
    localStorage.setItem(SETTINGS_KEY, JSON.stringify({
      text: els.text.value, angle: els.angle.value, size: els.size.value, opacity: els.opacity.value,
      color: els.color.value, font: els.font.value, tile: els.tile.checked,
    }));
  } catch { /* archiviazione non disponibile: si riparte dai valori predefiniti */ }
}

function restoreSettings() {
  // Il colore dei campioni è impostato qui: la Content-Security-Policy non permette stili inline nell'HTML.
  document.querySelectorAll(".swatch[data-color]").forEach((b) => { b.style.backgroundColor = b.dataset.color; });
  const s = readSettings();
  if (s) {
    if (typeof s.text === "string") els.text.value = s.text;
    for (const k of ["angle", "size", "opacity"]) if (s[k] != null) els[k].value = s[k];
    if (/^#[0-9a-f]{6}$/i.test(s.color ?? "")) els.color.value = s.color;
    els.tile.checked = !!s.tile;
  }
  selectSwatch(els.color.value);
}
