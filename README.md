# Insta Expenses

Plataforma analítica + CRUD sobre el histórico de 10 000 gastos corporativos (`gastos_desordenados.json`).
Identidad visual: azul marino y verde (paleta inspirada en Instacredit), modo claro por defecto y modo oscuro con los mismos tonos. **.NET 8 (ASP.NET Core minimal API) + Angular 17 + Chart.js.** Persistencia real en JSON, sin base de datos.

> El archivo del reto trae una inconsistencia (JSON malformado). Se diagnosticó, se documentó (`docs/DATA_FORENSICS.md`) y la app la
> procesa con una reparación determinista y auditada **sin tocar el original**.

## Ejecutar

Requisitos: .NET SDK 8, Node 20+ (probado con Node 24 y Angular CLI 17).

```bash
./run.sh                 # compila el frontend (1.ª vez) y levanta todo en http://localhost:5080
```

Manual:

```bash
cd web && npm ci && npx ng build          # emite a src/ExpenseIntelligence.Api/wwwroot
cd .. && dotnet run --project src/ExpenseIntelligence.Api
```

- App: http://localhost:5080 · Swagger: http://localhost:5080/swagger
- Desarrollo con recarga: `dotnet run --project src/ExpenseIntelligence.Api` + `cd web && npx ng serve` (proxy `/api` → 5080, http://localhost:4200)
- Pruebas: `dotnet test` (105 pruebas)
- Guía de uso paso a paso: [`docs/TUTORIAL.md`](docs/TUTORIAL.md)

## Qué incluye

| Área | Detalle |
|---|---|
| **Dashboard** | KPIs por moneda (nunca se suman monedas), % aprobado/pendiente, evolución temporal (mensual/semanal), promedio y total por departamento, top categorías, métodos de pago. Filtros combinables: fechas, departamentos y categorías múltiples, moneda, método de pago, estado. |
| **Gastos aprobados protegidos** | Un gasto aprobado no permite cambiar el monto ni la moneda (el resto de campos sí). Para modificarlos hay que quitar primero la aprobación y guardar; ambos pasos quedan en el audit. La regla se valida en el servidor dentro del mismo bloqueo de escritura (no se puede saltar aprobando y cambiando el monto en una sola petición) y el formulario bloquea los campos con una explicación. |
| **Archivos (A/B)** | Cada archivo cargado queda registrado con su original intacto (solo lectura) y sus propios cambios (`/archivos`). Cambiar de archivo guarda el estado del actual y carga el otro: **las ediciones de cada archivo persisten**. Se puede volver un archivo a su original (descarta sus cambios), eliminar los inactivos o **dejar el sistema limpio** (vuelve al original del sistema y borra archivos cargados, cambios, papelera, historial y respaldos; exige escribir `LIMPIAR`). |
| **Archivo repetido / versiones** | Al subir, se compara el contenido (SHA-256): si ya existe se ofrece volver a él (conserva sus cambios) o reiniciarlo; si hay otro con el mismo nombre pero contenido distinto se avisa que es una versión nueva. |
| **Comparativas** | Tabla comparativa entre dos versiones cualesquiera (archivo A vs B, versión anterior con el mismo nombre, o un archivo contra su propio original para ver qué cambiaste): registros, % aprobados, totales por moneda (nunca mezcladas) y diferencias registro por registro, emparejando por ID o por código de factura. `GET /api/datasets/compare`. |
| **Aprobación / rechazo masivo** | En Gastos, casillas por fila y por página, "seleccionar los N resultados del filtro" (se resuelve en el servidor), barra flotante y confirmación con totales **por moneda**. Rechazar exige un motivo. Estado nuevo **Rechazado** (`rechazado` + `motivo_rechazo` opcionales en el JSON; `aprobado` no cambia y los `aprobado:false` históricos siguen siendo Pendiente). Todo el lote se valida y se guarda con **una sola escritura** del archivo: si algo cambió (versión, archivo activo, cantidad de resultados) no se modifica nada (409 con el detalle). Solo se procesan gastos pendientes; los demás se reportan como omitidos. Límite configurable `Bulk:MaxItems` (5000). Cada cambio queda en la auditoría con un id de operación común. `POST /api/expenses/bulk/{approve\|reject}[/preview]`. |
| **Papelera e Historial** | Los gastos eliminados se ven y se restauran desde la pestaña Papelera de Gastos. `/historial` muestra en lenguaje claro cada edición (campo, antes → después), eliminación, restauración, alta y carga de archivos, con botón de restaurar mientras siga eliminado. |
| **Alertas al editar** | Antes de guardar cambios en aprobación, monto, moneda, fecha o factura se muestra un resumen de lo que cambia (con % de variación del monto y aviso reforzado si supera el 50%). Eliminar un gasto aprobado también advierte. |
| **Máquina del tiempo** | Reproduce el gasto mes a mes (`/maquina-del-tiempo`): play/pausa, velocidad, slider, modo acumulado o mensual y ranking animado de departamentos y categorías con una línea de narración (p. ej. cambios de liderazgo) construida solo con las cifras en pantalla. Respeta los filtros y se mide en una moneda a la vez. Endpoint: `GET /api/analytics/timeline`. |
| **Importar archivo** | Sube un JSON desde la UI (`/importar`). Primero se **analiza sin modificar nada** (lectura tolerante, reparaciones, cuarentena, calidad de datos, comparación de registros). Al confirmar puedes **ignorar los datos anteriores** (reemplazar, atómico y auditado) o **conservar y agregar** (IDs ya existentes se omiten sin sobrescribir); el archivo subido se guarda intacto (solo lectura) en `data/working/imports/` como nuevo original. Dashboard, Data Doctor, insights y Diagnóstico salen de ese archivo. |
| **Gastos (CRUD)** | Tabla con paginación, orden y búsqueda **en servidor**; detalle; alta/edición con validación en frontend y backend; control de concurrencia optimista (409); eliminación lógica con confirmación y *Deshacer*; auditoría. |
| **Data Doctor** *(diferenciador A)* | Anomalías por severidad con causa, recomendación y registros afectados; correcciones seguras y auditadas (espacios, variantes de catálogo). Muestra el *diff* de la reparación de ingesta. |
| **Smart Insights** *(B)* | Crecimiento por departamento (90 d vs 90 d), cambio mensual, concentración (HHI), atípicos (>3σ), posibles duplicados, pendientes antiguos. Todo determinista; cada tarjeta muestra los números. |
| **Asistente** *(C, opcional)* | **No es un LLM**: intenciones por reglas que llaman las mismas consultas de lectura. No inventa cifras ni puede escribir. Etiquetado así en la UI. |
| **Diagnóstico** *(D)* | Estado de almacenamiento, última persistencia, tiempos p50/p95 medidos en servidor, GC, verificación de integridad (memoria vs disco vs hash del original), benchmark de solo lectura, auditoría. |

Obligatorio = Dashboard + Gastos + persistencia. Opcional/diferenciador = Data Doctor, Insights, Asistente, Diagnóstico.

## Hallazgos del diagnóstico de datos (detalle y evidencia: [`docs/DATA_FORENSICS.md`](docs/DATA_FORENSICS.md))

| # | Hallazgo | Severidad | Tratamiento |
|---|---|---|---|
| 1 | **JSON malformado**: línea 3 `id": 1407,` (falta la comilla de apertura). Ningún parser estándar lee el archivo. | Alta | Reparación determinista (solo comillas de clave) al leer; reportada; original intacto |
| 2 | 506 códigos de factura repetidos (1 041 registros con datos distintos) | Media | No se corrige; visible en Data Doctor; la API avisa al crear/editar |
| 3 | Montos CRC con la magnitud de USD/EUR (mediana ≈ 7 800 en las tres) | Info | No se convierte; totales siempre por moneda |
| – | 10 000 registros exactos (la consigna dice «más de 10 000»), IDs únicos 1–10000, sin fechas/montos inválidos, sin variantes de catálogo | – | – |

Reproducir: `python3 -I tools/forensics.py`.

## Arquitectura

```
src/
  ExpenseIntelligence.Domain          Expense (record inmutable), excepciones de dominio
  ExpenseIntelligence.Application     Filtros, consultas paginadas, validación, agregaciones, insights, Data Doctor, asistente, puerto IExpenseStore
  ExpenseIntelligence.Infrastructure  JsonExpenseStore (repositorio), RecoveryReader (ingesta tolerante), escritor streaming
  ExpenseIntelligence.Api             Minimal API, ProblemDetails, métricas de latencia, logging JSON, Swagger, hosting del SPA
web/                                  Angular 17 standalone + signals, rutas lazy, Chart.js
tests/ExpenseIntelligence.Tests       xUnit + WebApplicationFactory (105 pruebas)
data/original/                        Original intacto (solo lectura, SHA-256 en SHA256.txt)
data/working/                         (generado) expenses.json, .bak, ingest-report.json, quarantine.json, audit.jsonl
```

Dependencias en un solo sentido (Api → Infrastructure → Application → Domain). La persistencia se esconde tras `IExpenseStore`; no hay más abstracciones que esa.

### Persistencia y concurrencia (modelo soportado: **un proceso API por directorio de datos**)

1. **Ingesta** (`RecoveryReader`): recorre el arreglo objeto por objeto (consciente de strings; el salto de línea reinicia el estado para que una comilla suelta no desincronice el resto). Cada registro termina *cargado*, *cargado tras reparación documentada* o *en cuarentena con su texto crudo*; IDs duplicados: gana el primero y el resto va a cuarentena; archivo truncado: se recuperan los completos y se marca la cola. Nada se descarta en silencio.
2. **Copia de trabajo**: el original nunca se escribe. Al primer arranque se genera `data/working/expenses.json` (JSON válido) y las operaciones CRUD lo reescriben.
3. **Lecturas**: contra un `DatasetSnapshot` inmutable en memoria, sin bloqueos y sin volver a deserializar el archivo. El snapshot se **reemplaza solo después** de una escritura exitosa.
4. **Escrituras**: `SemaphoreSlim` (una a la vez) → streaming a `expenses.json.tmp` → `fsync` → `File.Replace` atómico (deja `.bak` con la generación anterior) → recién entonces se publica el snapshot nuevo y se anexa a `audit.jsonl`. Si algo falla: `PersistenceException` → HTTP 503, memoria sin cambios, el frontend no muestra el cambio como confirmado.
5. **Actualizaciones perdidas**: cada registro tiene `version`; `PUT/DELETE` exigen la versión leída. Versión distinta → 409 con la versión actual, y el formulario ofrece recargar.
6. **IDs únicos**: los asigna el servidor bajo el semáforo (`max+1`); la eliminación es **lógica**, por lo que un ID nunca se reutiliza.
7. **Recuperación al arrancar**: `.tmp` huérfano se descarta; archivo de trabajo dañado → se guarda una copia `expenses.corrupt-*.json` y se usa `.bak`.
8. **Multiproceso**: un semáforo local no protege a varios procesos, así que el archivo `.store.lock` (acceso exclusivo) hace que una segunda instancia **falle al arrancar** con un mensaje claro. Para escalar horizontalmente habría que pasar a una base de datos o a un único escritor.

### Decisiones (resumidas)

- **Minimal API + records** en vez de controllers/MediatR/AutoMapper: menos ceremonia, el mismo contrato en Swagger.
- **Snapshot en memoria** (10 k filas ≈ pocos MB): cada filtro/agregación es un recorrido lineal medido en milisegundos; un índice o caché sería complejidad sin beneficio medido.
- **Sin conversión de moneda**: no hay tipo de cambio en los datos; inventar uno falsearía los totales. Se agrupa por moneda y la UI elige la moneda de análisis.
- **Eliminación lógica + auditoría** (`audit.jsonl` con antes/después) en lugar de borrado físico: reversible y trazable.
- **Chart.js** directo (sin wrapper de terceros) y Angular signals + `toObservable/switchMap` para cancelar peticiones obsoletas.
- **Logging estructurado** con el formateador JSON de `Microsoft.Extensions.Logging` (sin dependencias extra).

## Evidencia de verificación

`dotnet test` → **105 pasadas, 0 fallidas**, incluyendo: validaciones, IDs duplicados, JSON corrupto/truncado/BOM, CRUD completo (servicio y HTTP), cada filtro y todos combinados, agregaciones verificadas a mano, paginación y orden, 100 creaciones concurrentes (IDs únicos y todas en el archivo), 40 actualizaciones concurrentes del mismo registro (exactamente 1 ganador), fallo de disco simulado (memoria intacta y recuperación posterior), `.tmp` huérfano, archivo dañado → `.bak`, segunda instancia rechazada, persistencia tras reinicio y lectura consistente durante escrituras. Las pruebas usan directorios temporales aislados: nunca tocan `data/`.

Mediciones reales sobre el dataset de 10 000 registros (máquina de desarrollo, Apple Silicon, .NET 8.0.133; salida de `PerformanceTests`):

| Operación | Resultado |
|---|---|
| Carga en frío + creación de la copia de trabajo | 195 ms |
| Consulta paginada con filtros y orden (p95) | 2,55 ms |
| Resumen del dashboard sin filtro, 10 000 filas (p95) | 27,4 ms |
| Análisis completo de Data Doctor (p95) | 80,9 ms |
| 100 escrituras concurrentes duraderas (`fsync`) | 1 408 ms en total (14,1 ms/escritura) |

Estos números son de una corrida y varían con la máquina; los umbrales de las pruebas son holgados a propósito.

## Limitaciones conocidas

- **Un solo proceso** por directorio de datos (ver arriba). Sin autenticación/autorización: el `actor` de auditoría es un encabezado informativo (`X-Actor`).
- Cada escritura **reescribe el archivo completo** (~3,4 MB): 14 ms/escritura medidos, pero crece linealmente con el volumen. En la prueba de 100 escrituras se observaron 10 recolecciones gen2 (el snapshot nuevo reconstruye un diccionario de 10 k entradas); es aceptable aquí, pero sería lo primero a optimizar (snapshot incremental).
- Los registros reparados/cuarentena se evalúan solo al cargar; el Data Doctor sobre datos en vivo no repite el diagnóstico de ingesta.
- El **asistente no es un LLM**: solo reconoce las intenciones listadas. «Este mes» usa el mes calendario actual y, si no tiene datos, el último mes con datos (lo indica en la respuesta). El último mes de los datos (2026-10) está incompleto (llega al 13).
- No hay pruebas automatizadas del frontend (unitarias o E2E); el frontend se verificó compilando y manualmente en el navegador integrado. Las rutas que usa están cubiertas por las pruebas de API.
- Montos CRC con escala sospechosa (ver hallazgo 3): se reportan, no se corrigen.

## Resumen de cumplimiento

| Requisito de la consigna | Estado |
|---|---|
| Validar integridad del archivo antes de procesar | ✅ Diagnóstico + ingesta tolerante + reporte |
| JSON como fuente primaria, repositorio, escritura segura y concurrente | ✅ `JsonExpenseStore` |
| KPIs: total por moneda, promedio por departamento, top categorías, % aprobado vs pendiente | ✅ Dashboard |
| Filtros combinables y reactivos (fechas, deptos, categorías, moneda, pago, estado) | ✅ Cliente + servidor, probados |
| Paginación en servidor | ✅ |
| CRUD con validación, ID único, eliminación con confirmación, reflejo inmediato en el JSON | ✅ |
| Arquitectura limpia, streaming/GC, concurrencia, excepciones y logging estructurado | ✅ (ver limitaciones sobre GC) |
| Uso creativo de IA | Data Doctor, Smart Insights, asistente (por reglas), desarrollo asistido por IA |
