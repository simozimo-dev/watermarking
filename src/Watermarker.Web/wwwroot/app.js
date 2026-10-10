"use strict";

// ------------------------------------------------------------------ stato

const $ = (id) => document.getElementById(id);
const els = {
  form: $("form"), file: $("file"), drop: $("drop"), dropTitle: $("drop-title"), dropHint: $("drop-hint"),
  fileList: $("file-list"), fileSummary: $("file-summary"), clearFiles: $("clear-files"),
  text: $("text"), angle: $("angle"), size: $("size"), opacity: $("opacity"),
  color: $("color"), font: $("font"), tile: $("tile"), tileRow: $("tile-row"), slides: $("pptxSlides"),
  slidesRow: $("slides-row"), submit: $("submit"), progress: $("progress"), bar: $("progress-bar"),
  status: $("status"), canvas: $("canvas"), caption: $("preview-caption"), user: $("user"),
};

let caps = { native: [], pdf: [], converterAvailable: false, maxUploadBytes: 100 * 1024 * 1024, fonts: ["Arial"], maxFiles: 20 };
let me = { authEnabled: false };

/** File selezionati: { id, file, status, warnings, error, result: { blob, name } } */
let files = [];
let selectedId = null;
let previewImage = null;
let busy = false;
let nextId = 1;

const KINDS = {
  slide: [".pptx", ".pptm", ".potx", ".potm", ".ppsx", ".ppsm", ".ppt", ".pot", ".pps", ".odp", ".otp"],
  image: [".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".webp", ".tif", ".tiff"],
  video: [".mp4", ".m4v", ".mov", ".mkv", ".webm", ".avi"],
  sheet: [".xlsx", ".xlsm", ".xltx", ".xltm", ".xls", ".xlt", ".ods", ".ots", ".csv"],
  tileable: [".pdf", ".xps", ".oxps"],
};
const STATUS_LABELS = {
  ready: "Ready", queued: "Waiting", uploading: "Uploading", processing: "Processing",
  done: "Done", error: "Failed", skipped: "Skipped",
};
const SETTINGS_KEY = "watermark-settings-v2";

// ------------------------------------------------------------------ avvio

init();

async function init() {
  restoreSettings();
  bindEvents();

  const [capsRes, meRes] = await Promise.allSettled([fetch("api/capabilities"), fetch("api/me")]);
  if (capsRes.status === "fulfilled" && capsRes.value.status === 401) return sessionExpired();
  if (capsRes.status === "fulfilled" && capsRes.value.ok) caps = { ...caps, ...(await capsRes.value.json()) };
  if (meRes.status === "fulfilled" && meRes.value.ok) me = await meRes.value.json();

  renderUser();
  els.font.replaceChildren(...caps.fonts.map((f) => new Option(f, f)));
  const saved = readSettings();
  if (saved?.font && caps.fonts.includes(saved.font)) els.font.value = saved.font;
  els.text.maxLength = caps.maxTextLength || 200;
  els.file.accept = [...new Set([...caps.native, ...caps.pdf])].join(",");
  els.dropHint.textContent =
    `Documents, PDFs, images, ZIP archives, emails${caps.native.includes(".mp4") ? " and videos" : ""}` +
    ` · up to ${caps.maxFiles} files, ${formatBytes(caps.maxUploadBytes)} each`;
  if (!caps.converterAvailable) {
    const pdf = els.form.querySelector('input[name="output"][value="pdf"]');
    pdf.disabled = true;
    pdf.closest(".choice").title = "PDF conversion is not enabled on this server";
  }

  updateOutputs();
  refresh();
}

function bindEvents() {
  for (const input of [els.text, els.angle, els.size, els.opacity, els.font, els.tile]) {
    input.addEventListener("input", () => { updateOutputs(); draw(); saveSettings(); });
  }
  els.color.addEventListener("input", () => { selectSwatch(els.color.value); draw(); saveSettings(); });

  document.querySelectorAll(".swatch[data-color]").forEach((b) =>
    b.addEventListener("click", () => { els.color.value = b.dataset.color; selectSwatch(b.dataset.color); draw(); saveSettings(); }));

  document.querySelectorAll(".token").forEach((b) => b.addEventListener("click", () => insertToken(b.dataset.token)));
  els.form.querySelectorAll('input[name="output"]').forEach((r) => r.addEventListener("change", refresh));

  els.file.addEventListener("change", () => { addFiles(els.file.files); els.file.value = ""; });
  for (const type of ["dragenter", "dragover"]) {
    els.drop.addEventListener(type, (e) => { e.preventDefault(); els.drop.classList.add("over"); });
  }
  for (const type of ["dragleave", "drop"]) {
    els.drop.addEventListener(type, () => els.drop.classList.remove("over"));
  }
  els.drop.addEventListener("drop", (e) => {
    e.preventDefault();
    if (e.dataTransfer?.files?.length) addFiles(e.dataTransfer.files);
  });

  els.clearFiles.addEventListener("click", () => {
    if (busy) return;
    files = [];
    selectedId = null;
    setStatus("");
    refresh();
  });

  els.form.addEventListener("submit", (e) => { e.preventDefault(); submit(); });
  window.matchMedia("(prefers-color-scheme: dark)").addEventListener?.("change", draw);
}

// ------------------------------------------------------------------ utente

function renderUser() {
  const box = els.user;
  box.replaceChildren();

  if (!me.authEnabled) {
    const badge = document.createElement("p");
    badge.className = "user-badge";
    badge.textContent = "Sign-in is disabled on this server (development mode)";
    box.append(badge);
    box.hidden = false;
    return;
  }

  // Segnaposto legati all'identità: disponibili solo con il login attivo.
  document.querySelectorAll(".token-auth").forEach((t) => { t.hidden = false; });

  const avatar = document.createElement("span");
  avatar.className = "avatar";
  avatar.setAttribute("aria-hidden", "true");
  avatar.textContent = initials(me.name || me.username || "?");

  const info = document.createElement("div");
  info.className = "user-info";

  const name = document.createElement("p");
  name.className = "user-name";
  name.textContent = me.name || me.username;

  const mail = document.createElement("p");
  mail.className = "user-mail";
  mail.textContent = me.username || "";

  const meta = document.createElement("p");
  meta.className = "user-meta";
  const parts = [];
  if (me.roles?.length) parts.push(`Roles: ${me.roles.join(", ")}`);
  if (me.signedInAt) parts.push(`Signed in at ${formatTime(me.signedInAt)}`);
  meta.textContent = parts.join(" · ");

  const out = document.createElement("a");
  out.className = "signout";
  out.href = me.signOutUrl || "auth/signout";
  out.textContent = "Sign out";

  info.append(name, mail);
  if (parts.length) info.append(meta);
  box.append(avatar, info, out);
  box.hidden = false;
}

function initials(text) {
  const words = text.replace(/@.*/, "").split(/[\s._-]+/).filter(Boolean);
  return ((words[0]?.[0] ?? "?") + (words.length > 1 ? words[words.length - 1][0] : "")).toUpperCase();
}

function sessionExpired() {
  busy = false;
  els.status.replaceChildren();
  const p = document.createElement("p");
  p.className = "error";
  p.textContent = "Your session has expired. ";
  const a = document.createElement("a");
  a.href = location.pathname;
  a.textContent = "Sign in again";
  p.append(a);
  els.status.append(p);
}

// ------------------------------------------------------------------ file

function extensionOf(name) {
  const i = name.lastIndexOf(".");
  return i < 0 ? "" : name.slice(i).toLowerCase();
}

function addFiles(list) {
  if (busy) return;
  const max = caps.maxFiles || 20;
  let skipped = 0;

  // Se i file precedenti sono già stati elaborati, una nuova selezione riparte da zero.
  if (files.length && files.every((f) => f.status === "done" || f.status === "error")) files = [];

  for (const file of list) {
    const duplicate = files.some((f) => f.file.name === file.name && f.file.size === file.size && f.file.lastModified === file.lastModified);
    if (duplicate) continue;
    if (files.length >= max) { skipped++; continue; }
    files.push({ id: nextId++, file, status: "ready", warnings: [], error: null, result: null });
  }

  if (!files.some((f) => f.id === selectedId)) selectedId = files[0]?.id ?? null;
  setStatus(skipped ? `Only ${max} files can be processed at once: ${skipped} were not added.` : "", skipped ? "error" : undefined);
  refresh();
}

function removeFile(id) {
  if (busy) return;
  files = files.filter((f) => f.id !== id);
  if (selectedId === id) selectedId = files[0]?.id ?? null;
  refresh();
}

function selectedEntry() {
  return files.find((f) => f.id === selectedId) ?? null;
}

function selectedOutput() {
  return els.form.querySelector('input[name="output"]:checked')?.value ?? "original";
}

/**
 * Come verrà elaborato un file, in base al formato e alla scelta "stesso formato / PDF".
 * Restituisce { mode: "original" | "pdf" | null, note, warn }.
 */
function planFor(entry) {
  const ext = extensionOf(entry.file.name);
  const native = caps.native.includes(ext);
  const pdf = caps.pdf.includes(ext) && caps.converterAvailable || ext === ".pdf";
  const wanted = selectedOutput();

  if (entry.file.size > caps.maxUploadBytes) return { mode: null, note: `Larger than ${formatBytes(caps.maxUploadBytes)}`, warn: true };
  if (!ext) return { mode: wanted, note: "Format detected from content", warn: false };

  if (wanted === "pdf") {
    if (pdf) return { mode: "pdf", note: ext === ".pdf" ? "" : "Converted to PDF", warn: false };
    if (native) return { mode: "original", note: "Cannot be converted: format kept", warn: false };
  } else {
    if (native) return { mode: "original", note: "", warn: false };
    if (pdf) return { mode: "pdf", note: "Format not editable: converted to PDF", warn: false };
  }
  return { mode: null, note: `${ext} files are not supported`, warn: true };
}

/** Aggiorna lista, opzioni disponibili, pulsante e anteprima. */
function refresh() {
  renderFiles();

  const exts = files.map((f) => extensionOf(f.file.name));
  const output = selectedOutput();
  const anyTileable = exts.some((e) => KINDS.tileable.includes(e) || KINDS.image.includes(e) || KINDS.video.includes(e));
  els.tileRow.hidden = !(files.length === 0 || output === "pdf" || anyTileable);
  els.slidesRow.hidden = !(output === "original" && exts.some((e) => KINDS.slide.includes(e) && caps.native.includes(e)));

  const runnable = files.filter((f) => f.status !== "done" && planFor(f).mode);
  const retry = runnable.length > 0 && runnable.every((f) => f.status === "error");
  els.submit.disabled = busy || runnable.length === 0;
  els.submit.textContent = busy ? "Processing…"
    : retry ? `Retry ${runnable.length === 1 ? "the failed file" : `${runnable.length} failed files`}`
    : runnable.length > 1 ? `Apply watermark to ${runnable.length} files`
    : "Apply watermark and download";

  loadPreviewImage();
  draw();
}

function renderFiles() {
  const list = els.fileList;
  list.replaceChildren();
  list.hidden = files.length === 0;
  els.clearFiles.hidden = files.length === 0 || busy;
  els.drop.classList.toggle("has-file", files.length > 0);
  els.dropTitle.textContent = files.length ? "Add more files" : "Drop files here or choose them from your computer";

  const total = files.reduce((s, f) => s + f.file.size, 0);
  els.fileSummary.hidden = files.length === 0;
  els.fileSummary.textContent = files.length
    ? `${files.length} ${files.length === 1 ? "file" : "files"} · ${formatBytes(total)}`
    : "";

  for (const entry of files) {
    const plan = planFor(entry);
    const li = document.createElement("li");
    li.className = "file-row";
    li.classList.toggle("selected", entry.id === selectedId);
    li.dataset.status = entry.status;

    const pick = document.createElement("button");
    pick.type = "button";
    pick.className = "file-pick";
    pick.title = "Show in preview";
    pick.setAttribute("aria-pressed", String(entry.id === selectedId));
    pick.addEventListener("click", () => { selectedId = entry.id; refresh(); });

    const name = document.createElement("span");
    name.className = "file-name";
    name.textContent = entry.file.name;

    const meta = document.createElement("span");
    meta.className = "file-meta";
    const detail = entry.status === "error" ? entry.error
      : entry.status === "done" && entry.warnings.length ? `${entry.warnings.length} warning${entry.warnings.length > 1 ? "s" : ""}`
      : plan.note;
    meta.textContent = [formatBytes(entry.file.size), detail].filter(Boolean).join(" · ");
    meta.classList.toggle("warn", entry.status === "error" || (plan.warn && entry.status === "ready"));
    if (entry.warnings.length) meta.title = entry.warnings.join("\n");
    pick.append(name, meta);

    const state = document.createElement("span");
    state.className = "file-status";
    const status = !plan.mode && entry.status === "ready" ? "skipped" : entry.status;
    state.dataset.status = status;
    state.textContent = STATUS_LABELS[status];

    const actions = document.createElement("span");
    actions.className = "file-actions";
    if (entry.result) {
      const dl = document.createElement("button");
      dl.type = "button";
      dl.className = "link";
      dl.textContent = "Download";
      dl.addEventListener("click", () => download(entry.result.blob, entry.result.name));
      actions.append(dl);
    } else if (!busy) {
      const rm = document.createElement("button");
      rm.type = "button";
      rm.className = "remove";
      rm.setAttribute("aria-label", `Remove ${entry.file.name}`);
      rm.textContent = "×";
      rm.addEventListener("click", () => removeFile(entry.id));
      actions.append(rm);
    }

    li.append(pick, state, actions);
    list.append(li);
  }
}

// ------------------------------------------------------------------ anteprima

let previewFor = null;
function loadPreviewImage() {
  const entry = selectedEntry();
  if (entry?.file === previewFor) return;
  previewFor = entry?.file ?? null;
  previewImage = null;
  if (entry && KINDS.image.includes(extensionOf(entry.file.name)) && entry.file.size < 40 * 1024 * 1024) {
    const img = new Image();
    img.onload = () => { if (previewFor === entry.file) { previewImage = img; draw(); } };
    img.src = URL.createObjectURL(entry.file);
  }
}

function expandedText(fileName) {
  const now = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  return els.text.value
    .replace(/\{(data|date)\}/gi, `${pad(now.getDate())}/${pad(now.getMonth() + 1)}/${now.getFullYear()}`)
    .replace(/\{(ora|time)\}/gi, `${pad(now.getHours())}:${pad(now.getMinutes())}`)
    .replace(/\{(nomefile|filename)\}/gi, fileName ?? selectedEntry()?.file.name ?? "document.pdf")
    .replace(/\{(utente|user)\}/gi, me.authEnabled ? (me.name ?? "") : "")
    .replace(/\{(email|upn)\}/gi, me.authEnabled ? (me.username ?? "") : "")
    .trim();
}

/** Righe del watermark, come le calcola il server: a capo come separatore, righe vuote ignorate. */
function textLines() {
  return expandedText().split(/\r?\n/).map((l) => l.trim()).filter((l) => l.length > 0);
}

/** Formato del foglio in anteprima, in punti tipografici. */
function sheetFormat() {
  const entry = selectedEntry();
  const ext = entry ? extensionOf(entry.file.name) : "";
  const mode = entry ? planFor(entry).mode : selectedOutput();
  if (previewImage) return { w: previewImage.naturalWidth, h: previewImage.naturalHeight, kind: "image" };
  if (mode !== "pdf") {
    if (KINDS.slide.includes(ext)) return { w: 960, h: 540, kind: "slide" };
    if (KINDS.video.includes(ext)) return { w: 960, h: 540, kind: "video" };
    if (KINDS.sheet.includes(ext)) return { w: 842, h: 595, kind: "sheet" };
  }
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
  const lines = textLines();
  if (lines.length) {
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

    const lineGap = fontPx * 1.2;
    const drawBlock = (x, y) => lines.forEach((line, i) => g.fillText(line, x, y + (i - (lines.length - 1) / 2) * lineGap));

    if (!els.tileRow.hidden && els.tile.checked) {
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

  const entry = selectedEntry();
  const labels = { page: "an A4 page", slide: "a 16:9 slide", sheet: "a landscape sheet", video: "a video frame", image: "the image" };
  const target = fmt.kind === "image" ? "Preview on the image" : `Approximate preview on ${labels[fmt.kind]}`;
  els.caption.textContent = entry && files.length > 1 ? `${target} · ${entry.file.name}` : target;
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

// ------------------------------------------------------------------ elaborazione

async function submit() {
  if (busy) return;
  if (!expandedText()) { setStatus("Enter the watermark text.", "error"); els.text.focus(); return; }
  if (textLines().length > (caps.maxLines || 10)) {
    setStatus(`The watermark can have at most ${caps.maxLines || 10} lines.`, "error"); els.text.focus(); return;
  }

  const queue = files.filter((f) => f.status !== "done" && planFor(f).mode);
  if (!queue.length) return;

  busy = true;
  for (const f of queue) { f.status = "queued"; f.error = null; f.warnings = []; }
  els.progress.hidden = false;
  refresh();

  let completed = 0;
  for (const entry of queue) {
    setStatus(queue.length > 1 ? `Processing file ${completed + 1} of ${queue.length}…` : "Processing…");
    const outcome = await processOne(entry, planFor(entry).mode, (fraction) => {
      els.bar.style.width = `${Math.round(((completed + fraction) / queue.length) * 100)}%`;
    });
    completed++;
    if (outcome === "unauthorized") {
      for (const f of queue) if (f.status === "queued") f.status = "ready";
      busy = false;
      els.progress.hidden = true;
      refresh();
      return sessionExpired();
    }
    refresh();
  }

  busy = false;
  els.progress.hidden = true;
  els.bar.style.width = "0";
  refresh();
  await deliver(queue);
}

/** Invia un file e attende il risultato. Restituisce "ok", "error" o "unauthorized". */
function processOne(entry, mode, onProgress) {
  return new Promise((resolve) => {
    const data = new FormData();
    data.append("file", entry.file, entry.file.name);
    data.append("text", els.text.value);
    data.append("angle", els.angle.value);
    data.append("size", els.size.value);
    data.append("opacity", String(Number(els.opacity.value) / 100));
    data.append("color", els.color.value);
    data.append("font", els.font.value);
    data.append("output", mode);
    data.append("tile", String(!els.tileRow.hidden && els.tile.checked));
    data.append("pptxSlides", String(!els.slidesRow.hidden && els.slides.checked));

    const xhr = new XMLHttpRequest();
    xhr.open("POST", "api/watermark");
    xhr.setRequestHeader("X-Requested-With", "watermarker");
    xhr.responseType = "blob";

    entry.status = "uploading";
    renderFiles();
    xhr.upload.onprogress = (e) => { if (e.lengthComputable) onProgress(0.8 * e.loaded / e.total); };
    xhr.upload.onload = () => { entry.status = "processing"; onProgress(0.8); renderFiles(); };

    xhr.onerror = () => {
      entry.status = "error";
      entry.error = "The server is not responding";
      resolve("error");
    };
    xhr.onload = async () => {
      if (xhr.status === 401) { entry.status = "ready"; return resolve("unauthorized"); }
      if (xhr.status === 200) {
        entry.status = "done";
        entry.result = {
          blob: xhr.response,
          name: fileNameFrom(xhr.getResponseHeader("Content-Disposition")) ?? `${entry.file.name}_watermark`,
        };
        entry.warnings = decodeWarnings(xhr.getResponseHeader("X-Watermark-Warnings"));
        onProgress(1);
        return resolve("ok");
      }
      const problem = await readProblem(xhr.response);
      const fallback = { 403: "Access denied", 413: "File too large for this server", 503: "Server busy, try again shortly" };
      entry.status = "error";
      entry.error = problem?.detail || fallback[xhr.status] || `Error ${xhr.status}`;
      resolve("error");
    };
    xhr.send(data);
  });
}

/** Un file: download diretto. Più file: un unico ZIP, più i link di ogni file nella lista. */
async function deliver(queue) {
  const ok = queue.filter((f) => f.result);
  const failed = queue.length - ok.length;
  const warnings = ok.reduce((n, f) => n + f.warnings.length, 0);

  if (ok.length === 1) {
    download(ok[0].result.blob, ok[0].result.name);
  } else if (ok.length > 1) {
    setStatus("Preparing the ZIP archive…");
    const zip = await makeZip(uniqueNames(ok.map((f) => ({ name: f.result.name, blob: f.result.blob }))));
    download(zip, `watermarked_${timestamp()}.zip`);
  }

  els.status.replaceChildren();
  const p = document.createElement("p");
  p.className = failed ? (ok.length ? "partial" : "error") : "done";
  p.textContent = ok.length === 0 ? "No file could be processed."
    : `${ok.length} ${ok.length === 1 ? "file" : "files"} watermarked` +
      (ok.length > 1 ? " and downloaded as a ZIP archive" : " and downloaded") +
      (failed ? `, ${failed} failed` : "") + ".";
  els.status.append(p);

  const details = queue.flatMap((f) => [
    ...(f.error ? [`${f.file.name}: ${f.error}`] : []),
    ...f.warnings.map((w) => `${f.file.name}: ${w}`),
  ]);
  if (details.length) {
    const ul = document.createElement("ul");
    for (const d of details) {
      const li = document.createElement("li");
      li.textContent = d;
      ul.append(li);
    }
    els.status.append(ul);
  } else if (warnings === 0 && ok.length > 1) {
    const hint = document.createElement("p");
    hint.className = "hint";
    hint.textContent = "Each file can also be downloaded on its own from the list.";
    els.status.append(hint);
  }
}

function uniqueNames(entries) {
  const seen = new Map();
  return entries.map((e) => {
    const n = seen.get(e.name) ?? 0;
    seen.set(e.name, n + 1);
    if (!n) return e;
    const dot = e.name.lastIndexOf(".");
    const name = dot > 0 ? `${e.name.slice(0, dot)} (${n + 1})${e.name.slice(dot)}` : `${e.name} (${n + 1})`;
    return { ...e, name };
  });
}

// ------------------------------------------------------------------ ZIP (senza compressione)

// I documenti sono già compressi: l'archivio li contiene così come sono ("store"),
// il che lo rende immediato da creare anche con file grandi.
const CRC_TABLE = (() => {
  const t = new Uint32Array(256);
  for (let n = 0; n < 256; n++) {
    let c = n;
    for (let k = 0; k < 8; k++) c = c & 1 ? 0xedb88320 ^ (c >>> 1) : c >>> 1;
    t[n] = c >>> 0;
  }
  return t;
})();

function crc32(bytes) {
  let c = 0xffffffff;
  for (let i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xff] ^ (c >>> 8);
  return (c ^ 0xffffffff) >>> 0;
}

async function makeZip(entries) {
  const enc = new TextEncoder();
  const now = new Date();
  const dosTime = (now.getHours() << 11) | (now.getMinutes() << 5) | (now.getSeconds() >> 1);
  const dosDate = ((now.getFullYear() - 1980) << 9) | ((now.getMonth() + 1) << 5) | now.getDate();
  const body = [], central = [];
  let offset = 0;

  for (const e of entries) {
    const data = new Uint8Array(await e.blob.arrayBuffer());
    const name = enc.encode(e.name);
    const crc = crc32(data);

    const local = new DataView(new ArrayBuffer(30));
    local.setUint32(0, 0x04034b50, true);
    local.setUint16(4, 20, true);
    local.setUint16(6, 0x0800, true);          // nomi in UTF-8
    local.setUint16(8, 0, true);               // nessuna compressione
    local.setUint16(10, dosTime, true);
    local.setUint16(12, dosDate, true);
    local.setUint32(14, crc, true);
    local.setUint32(18, data.length, true);
    local.setUint32(22, data.length, true);
    local.setUint16(26, name.length, true);
    local.setUint16(28, 0, true);
    body.push(local.buffer, name, data);

    const dir = new DataView(new ArrayBuffer(46));
    dir.setUint32(0, 0x02014b50, true);
    dir.setUint16(4, 20, true);
    dir.setUint16(6, 20, true);
    dir.setUint16(8, 0x0800, true);
    dir.setUint16(10, 0, true);
    dir.setUint16(12, dosTime, true);
    dir.setUint16(14, dosDate, true);
    dir.setUint32(16, crc, true);
    dir.setUint32(20, data.length, true);
    dir.setUint32(24, data.length, true);
    dir.setUint16(28, name.length, true);
    dir.setUint32(42, offset, true);
    central.push(dir.buffer, name);

    offset += 30 + name.length + data.length;
  }

  const centralSize = central.reduce((s, p) => s + p.byteLength, 0);
  const end = new DataView(new ArrayBuffer(22));
  end.setUint32(0, 0x06054b50, true);
  end.setUint16(8, entries.length, true);
  end.setUint16(10, entries.length, true);
  end.setUint32(12, centralSize, true);
  end.setUint32(16, offset, true);

  return new Blob([...body, ...central, end.buffer], { type: "application/zip" });
}

// ------------------------------------------------------------------ utilità

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
  updateOutputs();
  draw();
  saveSettings();
}

function formatBytes(n) {
  if (n >= 1024 * 1024 * 1024) return `${(n / 1024 / 1024 / 1024).toFixed(1)} GB`;
  if (n >= 1024 * 1024) return `${Math.round(n / 1024 / 1024)} MB`;
  return `${Math.max(1, Math.round(n / 1024))} KB`;
}

function formatTime(iso) {
  const d = new Date(iso);
  return isNaN(d) ? "" : d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
}

function timestamp() {
  const d = new Date();
  const pad = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}${pad(d.getMonth() + 1)}${pad(d.getDate())}-${pad(d.getHours())}${pad(d.getMinutes())}`;
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
