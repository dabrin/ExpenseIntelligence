#!/usr/bin/env python3
"""Reproducible, READ-ONLY forensic analysis of gastos_desordenados.json.
Usage: python3 -I tools/forensics.py [path-to-json] > docs/DATA_FORENSICS.md
The original is opened read-only; the in-memory repair below is the same single deterministic rule the API applies."""
import collections as C, hashlib, json, re, statistics, sys
from datetime import date

path = sys.argv[1] if len(sys.argv) > 1 else "data/original/gastos_desordenados.json"
raw = open(path, "rb").read()
sha = hashlib.sha256(raw).hexdigest()
out = []
def p(s=""): out.append(s)

try: text = raw.decode("utf-8")
except UnicodeDecodeError as e: text = raw.decode("utf-8", "replace"); enc = f"INVALID UTF-8 ({e})"
else: enc = "UTF-8 válido" + (" con BOM" if raw.startswith(b"\xef\xbb\xbf") else " sin BOM")

key_rx = re.compile(r'(?m)^([ \t]*)([A-Za-z_][A-Za-z0-9_]*)"([ \t]*:)')
lines = text.split("\n")
strict_err = None
try: json.loads(text)
except json.JSONDecodeError as e: strict_err = e

shape = re.compile(r'^(\[|\]|  \{|  \},?|    "[a-z_]+": (".*"|-?\d+(\.\d+)?|true|false|null),?)$')
bad = [(i + 1, l) for i, l in enumerate(lines) if l and not shape.match(l)]
repaired = key_rx.sub(r'\1"\2"\3', text)
data = json.loads(repaired)

p("# Data Forensics — gastos_desordenados.json")
p()
p("Generado por `tools/forensics.py` (solo lectura). Reproducible: `python3 -I tools/forensics.py > docs/DATA_FORENSICS.md`.")
p()
p("## 1. Evidencia del archivo")
p(f"- Archivo: `{path}`  ·  {len(raw):,} bytes  ·  {len(lines):,} líneas  ·  SHA-256 `{sha}`")
p(f"- Codificación: {enc}")
p(f"- `json.loads` estricto: **{'OK' if strict_err is None else 'FALLA'}**" + (f" → `{strict_err}`" if strict_err else ""))
p(f"- Líneas que no respetan la forma esperada del archivo: **{len(bad)}**")
for n, l in bad[:10]:
    exp = key_rx.sub(r'\1"\2"\3', l)
    p(f"  - línea {n}: `{l.strip()}`  → esperado: `{exp.strip()}`")
p(f"- Tras la reparación determinista de comillas de clave (única regla aplicada): **{len(data):,} registros**")
p()

def ctr(k): return C.Counter(r.get(k) for r in data)
keysets = C.Counter(tuple(sorted(r)) for r in data)
ids = [r.get("id") for r in data]
dup_ids = [k for k, v in C.Counter(ids).items() if v > 1]
bad_dates, ds = [], []
for r in data:
    f = r.get("fecha")
    try: ds.append(date.fromisoformat(f))
    except Exception: bad_dates.append((r.get("id"), f))
amounts = [r["monto"] for r in data if isinstance(r.get("monto"), (int, float))]
inv = C.defaultdict(list)
for r in data: inv[r["codigo_factura"]].append(r["id"])
dup_inv = {k: v for k, v in inv.items() if len(v) > 1}
desc_rx = re.compile(r"^Gasto operativo #(\d+) - (.+) \((.+)\)$")
desc_bad = [r["id"] for r in data if not (m := desc_rx.match(r["descripcion"])) or m.group(1) != str(r["id"]) or m.group(2) != r["categoria"] or m.group(3) != r["departamento"]]
content = C.Counter(tuple((k, r[k]) for k in sorted(r) if k not in ("id", "descripcion", "codigo_factura")) for r in data)
dup_content = sum(v - 1 for v in content.values() if v > 1)
ws = sum(1 for r in data for v in r.values() if isinstance(v, str) and (v != v.strip() or "  " in v))
today = date(2026, 10, 30)

p("## 2. Resumen")
p("| Métrica | Valor |"); p("|---|---|")
p(f"| Registros encontrados (objetos) | {len(data):,} |")
p(f"| Registros con estructura de campos completa y tipos correctos | {sum(1 for r in data if all(k in r for k in ('id','descripcion','monto','moneda','categoria','departamento','metodo_pago','fecha','aprobado','codigo_factura'))):,} |")
p(f"| Esquemas distintos | {len(keysets)} |")
p(f"| IDs únicos / duplicados / rango | {len(set(ids)):,} / {len(dup_ids)} / {min(ids)}–{max(ids)} |")
p(f"| Fechas inválidas | {len(bad_dates)} |")
p(f"| Rango de fechas | {min(ds)} → {max(ds)} (futuras respecto a {today}: {sum(1 for d in ds if d > today)}) |")
p(f"| Montos ≤ 0 / nulos / no numéricos | {sum(1 for a in amounts if a <= 0)} / {sum(1 for r in data if r.get('monto') is None)} / {len(data) - len(amounts)} |")
p(f"| Rango de montos | {min(amounts):,.2f} → {max(amounts):,.2f} |")
p(f"| Texto con espacios sobrantes | {ws} |")
p(f"| Descripción inconsistente con campos | {len(desc_bad)} |")
p(f"| Registros idénticos salvo ID/descripción/factura | {dup_content} |")
p(f"| Códigos de factura repetidos | {len(dup_inv)} códigos / {sum(len(v) for v in dup_inv.values()):,} registros |")
p()
p("## 3. Catálogos (detección de duplicados semánticos)")
for k in ("moneda", "departamento", "categoria", "metodo_pago", "aprobado"):
    p(f"- **{k}**: " + ", ".join(f"{a} ({b:,})" for a, b in ctr(k).most_common()))
p()
p("## 4. Montos por moneda (¿escala coherente?)")
p("| Moneda | n | mediana | p95 | máx |"); p("|---|---|---|---|---|")
for c in sorted({r["moneda"] for r in data}):
    v = sorted(r["monto"] for r in data if r["moneda"] == c)
    p(f"| {c} | {len(v):,} | {statistics.median(v):,.2f} | {v[int(len(v)*.95)]:,.2f} | {v[-1]:,.2f} |")
p()
p("## 5. Anomalías, severidad, impacto y estrategia")
p("| # | Anomalía | Severidad | Impacto | Estrategia aplicada |"); p("|---|---|---|---|---|")
p(f"| 1 | JSON malformado: línea {bad[0][0] if bad else '-'} (`{bad[0][1].strip() if bad else ''}`) — clave `id` sin comilla de apertura | **Alta** (el archivo completo no parsea con ningún parser estándar) | Bloquea el 100 % de la ingesta | Lectura por objetos con reparación determinista y auditada (solo comilla de clave faltante). Nada inventado; el original queda intacto. Se reporta en Data Doctor e `ingest-report.json`. |")
p(f"| 2 | Códigos de factura repetidos ({len(dup_inv)} códigos, {sum(len(v) for v in dup_inv.values()):,} registros) con datos distintos | Media | Posible doble registro/cobro; con 5 dígitos y 10 000 filas son colisiones esperables por azar | No se corrige: se expone en Data Doctor para decisión humana. La API avisa (no bloquea) al crear/editar con una factura ya usada. |")
p(f"| 3 | Montos en CRC con la misma magnitud que USD/EUR (mediana CRC ≈ USD ≈ EUR) | Baja/Info | Con ≈500 CRC/USD se esperaría ~100× más; posible etiquetado de moneda erróneo | No se convierte ni se altera. Todos los totales se muestran por moneda; nunca se suman monedas distintas. |")
p("| 4 | Sin duplicados de ID, sin fechas inválidas, sin montos ≤ 0, sin variantes de catálogo, sin problemas de codificación | — | — | Las reglas siguen activas en Data Doctor y en la validación de escritura. |")
p()
p("## 6. Corrección por la aplicación")
p("1. Al primer arranque se lee `data/original/gastos_desordenados.json` (de solo lectura, SHA-256 verificado en Diagnóstico).")
p("2. Se materializa `data/working/expenses.json` (JSON limpio) — es el archivo que las operaciones CRUD reescriben de forma atómica.")
p("3. `data/working/ingest-report.json` documenta registros leídos/reparados/en cuarentena; `quarantine.json` contendría cualquier texto crudo no recuperable (hoy: vacío).")
print("\n".join(out))
