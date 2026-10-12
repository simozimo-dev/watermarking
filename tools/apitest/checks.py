"""
Controlli sul file restituito dal servizio: il formato è valido e il watermark c'è.

Ogni controllo restituisce un elenco di problemi (vuoto = tutto a posto). Dove il testo del watermark
resta leggibile nel file (Word, PowerPoint, OpenDocument, Visio) si cerca il testo di prova;
negli altri formati il watermark è grafica, quindi si verifica che il file sia valido e modificato.
"""
from __future__ import annotations

import email
import email.policy
import io
import struct
import zipfile
from typing import List, Optional, Tuple
from xml.sax.saxutils import escape

# Formati in cui il testo del watermark è cercabile, e parti del pacchetto in cui si trova.
TEXT_PARTS = {
    ".docx": ("word/header",),
    ".docm": ("word/header",),
    ".pptx": ("ppt/slideMasters/", "ppt/slideLayouts/"),
    ".pptm": ("ppt/slideMasters/", "ppt/slideLayouts/"),
    ".odt": ("styles.xml", "content.xml"),
    ".ods": ("styles.xml", "content.xml"),
    ".odp": ("styles.xml", "content.xml"),
    ".vsdx": ("visio/",),
}
ZIP_BASED = {".docx", ".docm", ".dotx", ".xlsx", ".xlsm", ".pptx", ".pptm", ".ppsx", ".odt", ".ods", ".odp",
             ".ott", ".ots", ".otp", ".vsdx", ".vsdm", ".zip", ".xps", ".oxps"}


def ext_of(name: str) -> str:
    name = name.lower()
    return name[name.rfind("."):] if "." in name else ""


def check_output(src_name: str, src: bytes, out_name: str, out: bytes, marker: Optional[str] = None,
                 expect_pdf: bool = False) -> List[str]:
    """Controlla il file prodotto. `marker` è un testo unico usato come watermark (se il formato lo conserva)."""
    problems: List[str] = []
    if not out:
        return ["the response is empty"]
    if out == src:
        problems.append("the output is identical to the input")

    ext = ".pdf" if expect_pdf else ext_of(out_name)
    if expect_pdf and ext_of(out_name) != ".pdf":
        problems.append(f"expected a PDF, got '{out_name}'")

    if ext == ".pdf":
        problems += _pdf(out)
    elif ext in ZIP_BASED:
        problems += _zip(ext, src, out, marker)
    elif ext == ".png":
        problems += _same_image_size(src, out, _png_size)
    elif ext in (".jpg", ".jpeg"):
        problems += _same_image_size(src, out, _jpeg_size)
    elif ext in (".mp4", ".m4v", ".mov"):
        if out[4:8] != b"ftyp":
            problems.append("not an MP4/MOV file (missing 'ftyp' box)")
    elif ext == ".eml":
        problems += _eml(src, out)
    return problems


def contains_text(out_name: str, out: bytes, text: str) -> Optional[bool]:
    """True/False se il formato conserva il testo del watermark, None se non è verificabile."""
    ext = ext_of(out_name)
    parts = TEXT_PARTS.get(ext)
    if not parts:
        return None
    needles = {text.encode("utf-8"), escape(text, {'"': "&quot;"}).encode("utf-8")}
    try:
        with zipfile.ZipFile(io.BytesIO(out)) as z:
            for n in z.namelist():
                if n.startswith(parts) and n.endswith(".xml"):
                    data = z.read(n)
                    if any(x in data for x in needles):
                        return True
    except zipfile.BadZipFile:
        return False
    return False


# ---------------------------------------------------------------------- formati

def _pdf(out: bytes) -> List[str]:
    p = []
    if not out.startswith(b"%PDF-"):
        p.append("not a PDF (missing %PDF- header)")
    if b"%%EOF" not in out[-2048:]:
        p.append("PDF without %%EOF trailer (truncated?)")
    return p


def _zip(ext: str, src: bytes, out: bytes, marker: Optional[str]) -> List[str]:
    p = []
    try:
        zout = zipfile.ZipFile(io.BytesIO(out))
        bad = zout.testzip()
        if bad:
            return [f"corrupted entry in the package: {bad}"]
    except zipfile.BadZipFile:
        return ["the output is not a valid ZIP/OPC package"]

    names = set(zout.namelist())
    try:
        zsrc = zipfile.ZipFile(io.BytesIO(src))
        missing = [n for n in zsrc.namelist() if n not in names and not n.endswith("/")]
        if ext == ".zip" and missing:
            p.append(f"entries missing from the archive: {', '.join(missing[:5])}")
        if ext == ".zip":
            same = [n for n in zsrc.namelist() if n in names and not n.endswith("/") and zsrc.read(n) == zout.read(n)]
            if same:
                p.append(f"entries not watermarked: {', '.join(same[:5])}")
    except zipfile.BadZipFile:
        pass

    if ext in (".docx", ".docm") and not any(n.startswith("word/header") for n in names):
        p.append("no header part: the Word watermark lives in the headers")
    if ext in (".xlsx", ".xlsm") and not any(n.startswith("xl/media/") for n in names):
        p.append("no image in xl/media: the Excel watermark is a background/header image")
    if marker and ext in TEXT_PARTS:
        found = contains_text("x" + ext, out, marker)
        if found is False:
            p.append(f"watermark text '{marker}' not found in the package")
    return p


def _same_image_size(src: bytes, out: bytes, reader) -> List[str]:
    a, b = reader(src), reader(out)
    if b is None:
        return ["the output is not a valid image of the expected type"]
    if a is not None and a != b:
        return [f"image size changed: {a[0]}x{a[1]} -> {b[0]}x{b[1]}"]
    return []


def _png_size(data: bytes) -> Optional[Tuple[int, int]]:
    if data[:8] != b"\x89PNG\r\n\x1a\n" or data[12:16] != b"IHDR":
        return None
    return struct.unpack(">II", data[16:24])


def _jpeg_size(data: bytes) -> Optional[Tuple[int, int]]:
    if data[:2] != b"\xff\xd8":
        return None
    i = 2
    while i + 9 < len(data):
        if data[i] != 0xFF:
            i += 1
            continue
        marker = data[i + 1]
        if marker in (0xC0, 0xC1, 0xC2, 0xC3, 0xC5, 0xC6, 0xC7, 0xC9, 0xCA, 0xCB, 0xCD, 0xCE, 0xCF):
            h, w = struct.unpack(">HH", data[i + 5:i + 9])
            return w, h
        length = struct.unpack(">H", data[i + 2:i + 4])[0]
        i += 2 + length
    return None


def _eml(src: bytes, out: bytes) -> List[str]:
    def attachments(raw: bytes):
        msg = email.message_from_bytes(raw, policy=email.policy.default)
        return {part.get_filename(): part.get_content() for part in msg.iter_attachments() if part.get_filename()}

    try:
        a, b = attachments(src), attachments(out)
    except Exception as e:  # noqa: BLE001 - qualsiasi errore di parsing è un problema del file
        return [f"the output is not a valid e-mail: {e}"]
    p = []
    for name, content in a.items():
        if name not in b:
            p.append(f"attachment '{name}' missing")
        elif b[name] == content:
            p.append(f"attachment '{name}' not watermarked")
    return p
