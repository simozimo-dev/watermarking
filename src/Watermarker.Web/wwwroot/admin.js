"use strict";

// Gestione dei token delle API (pagina riservata al ruolo amministrativo).

const $ = (id) => document.getElementById(id);
const els = {
  summary: $("summary"), notice: $("notice"), rows: $("rows"), empty: $("empty"), newToken: $("new-token"),
  editor: $("editor"), form: $("token-form"), title: $("editor-title"), save: $("save"), cancel: $("cancel"),
  formError: $("form-error"), name: $("f-name"), description: $("f-description"), contact: $("f-contact"),
  expiry: $("f-expiry"), date: $("f-date"), dateField: $("custom-date-field"), enabledRow: $("enabled-row"),
  enabled: $("f-enabled"), sync: $("f-sync"), async: $("f-async"), maxMb: $("f-maxmb"), rpm: $("f-rpm"),
  jobs: $("f-jobs"), text: $("f-text"), user: $("f-user"), networks: $("f-networks"),
  secret: $("secret"), secretValue: $("secret-value"), secretFor: $("secret-for"), copy: $("copy"), secretDone: $("secret-done"),
};

const state = { tokens: [], defaults: null, editing: null };
const DAY = 24 * 60 * 60 * 1000;

init();

async function init() {
  writeExamples();
  els.newToken.addEventListener("click", () => openEditor(null));
  els.cancel.addEventListener("click", () => els.editor.close());
  els.form.addEventListener("submit", onSave);
  els.expiry.addEventListener("change", syncExpiry);
  els.copy.addEventListener("click", copySecret);
  els.secretDone.addEventListener("click", () => els.secret.close());
  els.rows.addEventListener("click", onRowAction);
  await load();
}

// ------------------------------------------------------------------ dati

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: {
      "X-Requested-With": "watermarker",
      ...(body ? { "Content-Type": "application/json" } : {}),
    },
    body: body ? JSON.stringify(body) : undefined,
    credentials: "same-origin",
  });

  if (res.status === 401) {
    // Sessione scaduta: il login riparte ricaricando la pagina.
    showNotice("Your session has expired. Reload the page to sign in again.", true);
    throw new Error("unauthorized");
  }
  if (res.status === 204) return null;

  const data = await res.json().catch(() => null);
  if (!res.ok) {
    const err = new Error(data?.detail || data?.title || `Request failed (${res.status})`);
    err.status = res.status;
    throw err;
  }
  return data;
}

async function load() {
  try {
    const data = await api("GET", "api/admin/tokens");
    state.tokens = data.tokens;
    state.defaults = data.defaults;
    els.newToken.disabled = false;
    if (!data.persistent)
      showNotice("Tokens are kept in memory only: they will be lost when the server restarts. Configure Api:DataPath on a persistent volume.", true);
    render();
  } catch (e) {
    if (e.message === "unauthorized") return;
    els.summary.textContent = "";
    showNotice(e.status === 403 ? e.message : `Could not load the tokens: ${e.message}`, true);
  }
}

// ------------------------------------------------------------------ elenco

function render() {
  const t = state.tokens;
  const active = t.filter((x) => x.status === "active").length;
  els.summary.textContent = t.length
    ? `${t.length} token${t.length === 1 ? "" : "s"}, ${active} active`
    : "";
  els.empty.hidden = t.length > 0;
  els.rows.replaceChildren(...t.map(row));
}

function row(tok) {
  const tr = document.createElement("tr");
  tr.dataset.id = tok.id;

  const app = cell();
  app.append(text("strong", tok.name), text("code", tok.hint, "hint"));
  if (tok.description) app.append(text("span", tok.description, "sub"));
  if (tok.contact) app.append(text("span", `Contact: ${tok.contact}`, "sub"));

  const status = cell();
  const badge = text("span", statusLabel(tok), "badge");
  badge.dataset.status = soonExpiring(tok) ? "expiring" : tok.status;
  status.append(badge);

  const expires = cell();
  expires.append(text("span", tok.expiresAt ? formatDate(tok.expiresAt) : "Never"));
  if (tok.expiresAt && tok.status === "active") expires.append(text("span", relativeDays(tok.expiresAt), "sub"));

  const used = cell();
  if (tok.lastUsedAt) {
    used.append(text("span", formatDateTime(tok.lastUsedAt)));
    used.append(text("span", `${tok.usageCount.toLocaleString()} request${tok.usageCount === 1 ? "" : "s"}${tok.lastUsedFrom ? ` · ${tok.lastUsedFrom}` : ""}`, "sub"));
  } else {
    used.append(text("span", "Never", "sub"));
  }

  const access = cell();
  access.append(text("span", accessSummary(tok)));
  const limits = limitsSummary(tok);
  if (limits) access.append(text("span", limits, "sub"));

  const actions = cell("row-actions");
  actions.append(
    button("Edit", "edit"),
    button(tok.enabled ? "Disable" : "Enable", "toggle"),
    button("Rotate", "rotate"),
    button("Delete", "delete", "danger"),
  );

  tr.append(app, status, expires, used, access, actions);
  return tr;
}

function statusLabel(tok) {
  if (tok.status === "disabled") return "Disabled";
  if (tok.status === "expired") return "Expired";
  return soonExpiring(tok) ? "Expires soon" : "Active";
}

function soonExpiring(tok) {
  return tok.status === "active" && tok.expiresAt && new Date(tok.expiresAt) - Date.now() < 14 * DAY;
}

function accessSummary(tok) {
  const s = tok.settings;
  const modes = [s.allowSync && "Sync", s.allowAsync && "Async"].filter(Boolean).join(" + ");
  const extra = [];
  if (s.fixedText) extra.push("fixed text");
  if (s.allowUserOverride) extra.push("sets user");
  if (s.allowedNetworks?.length) extra.push(`${s.allowedNetworks.length} network${s.allowedNetworks.length === 1 ? "" : "s"}`);
  return extra.length ? `${modes} · ${extra.join(", ")}` : modes;
}

function limitsSummary(tok) {
  const s = tok.settings;
  const parts = [];
  if (s.maxUploadMb) parts.push(`${s.maxUploadMb} MB`);
  if (s.requestsPerMinute) parts.push(`${s.requestsPerMinute}/min`);
  if (s.maxActiveJobs) parts.push(`${s.maxActiveJobs} jobs`);
  return parts.join(" · ");
}

async function onRowAction(e) {
  const btn = e.target.closest("button[data-action]");
  if (!btn) return;
  const id = btn.closest("tr").dataset.id;
  const tok = state.tokens.find((t) => t.id === id);
  if (!tok) return;

  try {
    switch (btn.dataset.action) {
      case "edit":
        openEditor(tok);
        return;
      case "toggle": {
        const body = inputFrom(tok);
        body.enabled = !tok.enabled;
        await api("PUT", `api/admin/tokens/${id}`, body);
        showNotice(`${tok.name}: token ${body.enabled ? "enabled" : "disabled"}.`);
        break;
      }
      case "rotate": {
        if (!confirm(`Replace the secret of "${tok.name}"?\n\nThe current token stops working immediately: the application must be updated with the new one.`)) return;
        const res = await api("POST", `api/admin/tokens/${id}/rotate`);
        showSecret(res.value, res.token.name);
        showNotice(`${tok.name}: new secret created. The previous one no longer works.`);
        break;
      }
      case "delete":
        if (!confirm(`Delete the token "${tok.name}"?\n\nThe application using it will stop working. This cannot be undone.`)) return;
        await api("DELETE", `api/admin/tokens/${id}`);
        showNotice(`${tok.name}: token deleted.`);
        break;
    }
    await load();
  } catch (err) {
    if (err.message !== "unauthorized") showNotice(err.message, true);
  }
}

// ------------------------------------------------------------------ creazione e modifica

function openEditor(tok) {
  state.editing = tok;
  els.title.textContent = tok ? `Edit ${tok.name}` : "New token";
  els.save.textContent = tok ? "Save changes" : "Create token";
  els.formError.hidden = true;

  const s = tok?.settings ?? {};
  els.name.value = tok?.name ?? "";
  els.description.value = tok?.description ?? "";
  els.contact.value = tok?.contact ?? "";
  els.enabledRow.hidden = !tok;
  els.enabled.checked = tok ? tok.enabled : true;
  els.sync.checked = s.allowSync ?? true;
  els.async.checked = s.allowAsync ?? true;
  els.maxMb.value = s.maxUploadMb ?? "";
  els.rpm.value = s.requestsPerMinute ?? "";
  els.jobs.value = s.maxActiveJobs ?? "";
  els.text.value = s.fixedText ?? "";
  els.user.checked = s.allowUserOverride ?? false;
  els.networks.value = (s.allowedNetworks ?? []).join("\n");

  const d = state.defaults;
  if (d) {
    els.maxMb.max = d.maxUploadMb;
    els.maxMb.placeholder = `${d.maxUploadMb}`;
    els.rpm.placeholder = `${d.requestsPerMinute}`;
    els.jobs.placeholder = `${d.maxActiveJobs}`;
    els.text.maxLength = d.maxTextLength;
  }

  fillExpiry(tok);
  els.editor.showModal();
  els.name.focus();
}

function fillExpiry(tok) {
  const opts = [];
  if (tok) opts.push(["keep", tok.expiresAt ? `Keep: ${formatDate(tok.expiresAt)}` : "Keep: never"]);
  opts.push(["30", "In 30 days"], ["90", "In 90 days"], ["180", "In 6 months"], ["365", "In 1 year"],
            ["never", "Never"], ["custom", "On a date…"]);
  els.expiry.replaceChildren(...opts.map(([v, label]) => {
    const o = document.createElement("option");
    o.value = v;
    o.textContent = label;
    return o;
  }));
  els.expiry.value = tok ? "keep" : "90";

  const tomorrow = new Date(Date.now() + DAY);
  els.date.min = isoDate(tomorrow);
  els.date.value = isoDate(new Date(Date.now() + 90 * DAY));
  syncExpiry();
}

function syncExpiry() {
  els.dateField.hidden = els.expiry.value !== "custom";
}

function expiryValue() {
  const v = els.expiry.value;
  if (v === "keep") return state.editing.expiresAt ?? null;
  if (v === "never") return null;
  if (v === "custom") {
    if (!els.date.value) throw new Error("Choose the expiry date.");
    // Fine della giornata scelta, nell'ora locale di chi crea il token.
    return new Date(`${els.date.value}T23:59:59`).toISOString();
  }
  return new Date(Date.now() + Number(v) * DAY).toISOString();
}

function numberOrNull(input) {
  const v = input.value.trim();
  return v === "" ? null : Number(v);
}

async function onSave(e) {
  e.preventDefault();
  els.formError.hidden = true;

  let body;
  try {
    if (!els.name.value.trim()) throw new Error("Enter the application name.");
    if (!els.sync.checked && !els.async.checked) throw new Error("Allow at least one mode.");
    body = {
      name: els.name.value.trim(),
      description: els.description.value.trim() || null,
      contact: els.contact.value.trim() || null,
      enabled: state.editing ? els.enabled.checked : true,
      expiresAt: expiryValue(),
      settings: {
        allowSync: els.sync.checked,
        allowAsync: els.async.checked,
        maxUploadMb: numberOrNull(els.maxMb),
        requestsPerMinute: numberOrNull(els.rpm),
        maxActiveJobs: numberOrNull(els.jobs),
        fixedText: els.text.value.trim() || null,
        allowUserOverride: els.user.checked,
        allowedNetworks: els.networks.value.split(/[\s,;]+/).map((x) => x.trim()).filter(Boolean),
      },
    };
  } catch (err) {
    return formError(err.message);
  }

  els.save.disabled = true;
  try {
    if (state.editing) {
      await api("PUT", `api/admin/tokens/${state.editing.id}`, body);
      els.editor.close();
      showNotice(`${body.name}: changes saved.`);
    } else {
      const res = await api("POST", "api/admin/tokens", body);
      els.editor.close();
      showSecret(res.value, res.token.name);
      showNotice(`${body.name}: token created.`);
    }
    await load();
  } catch (err) {
    if (err.message !== "unauthorized") formError(err.message);
  } finally {
    els.save.disabled = false;
  }
}

function formError(message) {
  els.formError.textContent = message;
  els.formError.hidden = false;
}

function inputFrom(tok) {
  return {
    name: tok.name,
    description: tok.description,
    contact: tok.contact,
    enabled: tok.enabled,
    expiresAt: tok.expiresAt,
    settings: { ...tok.settings },
  };
}

// ------------------------------------------------------------------ token appena creato

function showSecret(value, name) {
  els.secretValue.textContent = value;
  els.secretFor.textContent = `Token for ${name}.`;
  els.copy.textContent = "Copy";
  els.secret.showModal();
}

async function copySecret() {
  try {
    await navigator.clipboard.writeText(els.secretValue.textContent);
    els.copy.textContent = "Copied";
  } catch {
    // Senza permesso per gli appunti: seleziona il testo per la copia manuale.
    const range = document.createRange();
    range.selectNodeContents(els.secretValue);
    const sel = getSelection();
    sel.removeAllRanges();
    sel.addRange(range);
    els.copy.textContent = "Press Ctrl+C";
  }
}

// ------------------------------------------------------------------ esempi

function writeExamples() {
  const base = `${location.origin}${location.pathname.replace(/admin\.html$/, "")}`.replace(/\/$/, "");
  $("example-sync").textContent =
`curl -H "Authorization: Bearer $WATERMARK_TOKEN" \\
     -F file=@report.docx -F "text=CONFIDENTIAL" \\
     -o report_watermarked.docx \\
     ${base}/api/v1/watermark`;
  $("example-async").textContent =
`curl -H "Authorization: Bearer $WATERMARK_TOKEN" -F file=@video.mp4 -F "text={user} · {date}" \\
     ${base}/api/v1/jobs
# → 202 {"id":"…","status":"queued",…}

curl -H "Authorization: Bearer $WATERMARK_TOKEN" ${base}/api/v1/jobs/<id>
curl -H "Authorization: Bearer $WATERMARK_TOKEN" -OJ ${base}/api/v1/jobs/<id>/result`;
}

// ------------------------------------------------------------------ utilità

function showNotice(message, warn = false) {
  els.notice.textContent = message;
  els.notice.classList.toggle("warn", warn);
  els.notice.hidden = false;
}

function cell(cls) {
  const td = document.createElement("td");
  if (cls) td.className = cls;
  return td;
}

function text(tag, value, cls) {
  const el = document.createElement(tag);
  el.textContent = value;
  if (cls) el.className = cls;
  return el;
}

function button(label, action, cls) {
  const b = document.createElement("button");
  b.type = "button";
  b.className = cls ? `link ${cls}` : "link";
  b.dataset.action = action;
  b.textContent = label;
  return b;
}

function isoDate(d) {
  const p = (n) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

function formatDate(v) {
  return new Date(v).toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

function formatDateTime(v) {
  return new Date(v).toLocaleString(undefined, { year: "numeric", month: "short", day: "numeric", hour: "2-digit", minute: "2-digit" });
}

function relativeDays(v) {
  const days = Math.ceil((new Date(v) - Date.now()) / DAY);
  return days <= 1 ? "in less than a day" : `in ${days} days`;
}
