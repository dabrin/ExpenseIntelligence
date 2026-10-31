# Data Forensics — gastos_desordenados.json

Generado por `tools/forensics.py` (solo lectura). Reproducible: `python3 -I tools/forensics.py > docs/DATA_FORENSICS.md`.

## 1. Evidencia del archivo
- Archivo: `data/original/gastos_desordenados.json`  ·  3,357,954 bytes  ·  120,002 líneas  ·  SHA-256 `41cd64c068881428191eb72f31f0bb38c8cc44e4ec251a4f0dc84c82fcfbc494`
- Codificación: UTF-8 válido sin BOM
- `json.loads` estricto: **FALLA** → `Expecting property name enclosed in double quotes: line 3 column 5 (char 10)`
- Líneas que no respetan la forma esperada del archivo: **1**
  - línea 3: `id": 1407,`  → esperado: `"id": 1407,`
- Tras la reparación determinista de comillas de clave (única regla aplicada): **10,000 registros**

## 2. Resumen
| Métrica | Valor |
|---|---|
| Registros encontrados (objetos) | 10,000 |
| Registros con estructura de campos completa y tipos correctos | 10,000 |
| Esquemas distintos | 1 |
| IDs únicos / duplicados / rango | 10,000 / 0 / 1–10000 |
| Fechas inválidas | 0 |
| Rango de fechas | 2025-01-01 → 2026-10-13 (futuras respecto a 2026-10-30: 0) |
| Montos ≤ 0 / nulos / no numéricos | 0 / 0 / 0 |
| Rango de montos | 9.81 → 15,447.58 |
| Texto con espacios sobrantes | 0 |
| Descripción inconsistente con campos | 0 |
| Registros idénticos salvo ID/descripción/factura | 0 |
| Códigos de factura repetidos | 506 códigos / 1,041 registros |

## 3. Catálogos (detección de duplicados semánticos)
- **moneda**: USD (3,430), EUR (3,326), CRC (3,244)
- **departamento**: Recursos Humanos (1,735), Tecnología (1,717), Marketing (1,658), Operaciones (1,651), Finanzas (1,632), Ventas (1,607)
- **categoria**: Suscripciones SaaS (1,056), Marketing Digital (1,027), Licencias de Software (1,026), Servidores (1,009), Viáticos (1,004), Capacitación (997), Telefonía e Internet (987), Mantenimiento IT (985), Hardware (968), Infraestructura Cloud (941)
- **metodo_pago**: Tarjeta Corporativa (2,046), Caja Chica (2,015), Factura a Crédito (2,001), Transferencia Bancaria (1,983), PayPal (1,955)
- **aprobado**: True (5,071), False (4,929)

## 4. Montos por moneda (¿escala coherente?)
| Moneda | n | mediana | p95 | máx |
|---|---|---|---|---|
| CRC | 3,244 | 7,791.77 | 14,671.69 | 15,447.45 |
| EUR | 3,326 | 7,931.10 | 14,609.26 | 15,447.58 |
| USD | 3,430 | 7,520.04 | 14,672.03 | 15,445.35 |

## 5. Anomalías, severidad, impacto y estrategia
| # | Anomalía | Severidad | Impacto | Estrategia aplicada |
|---|---|---|---|---|
| 1 | JSON malformado: línea 3 (`id": 1407,`) — clave `id` sin comilla de apertura | **Alta** (el archivo completo no parsea con ningún parser estándar) | Bloquea el 100 % de la ingesta | Lectura por objetos con reparación determinista y auditada (solo comilla de clave faltante). Nada inventado; el original queda intacto. Se reporta en Data Doctor e `ingest-report.json`. |
| 2 | Códigos de factura repetidos (506 códigos, 1,041 registros) con datos distintos | Media | Posible doble registro/cobro; con 5 dígitos y 10 000 filas son colisiones esperables por azar | No se corrige: se expone en Data Doctor para decisión humana. La API avisa (no bloquea) al crear/editar con una factura ya usada. |
| 3 | Montos en CRC con la misma magnitud que USD/EUR (mediana CRC ≈ USD ≈ EUR) | Baja/Info | Con ≈500 CRC/USD se esperaría ~100× más; posible etiquetado de moneda erróneo | No se convierte ni se altera. Todos los totales se muestran por moneda; nunca se suman monedas distintas. |
| 4 | Sin duplicados de ID, sin fechas inválidas, sin montos ≤ 0, sin variantes de catálogo, sin problemas de codificación | — | — | Las reglas siguen activas en Data Doctor y en la validación de escritura. |

## 6. Corrección por la aplicación
1. Al primer arranque se lee `data/original/gastos_desordenados.json` (de solo lectura, SHA-256 verificado en Diagnóstico).
2. Se materializa `data/working/expenses.json` (JSON limpio) — es el archivo que las operaciones CRUD reescriben de forma atómica.
3. `data/working/ingest-report.json` documenta registros leídos/reparados/en cuarentena; `quarantine.json` contendría cualquier texto crudo no recuperable (hoy: vacío).
