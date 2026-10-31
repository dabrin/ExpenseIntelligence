# Tutorial de uso: Insta Expenses

Guía paso a paso para instalar, ejecutar y usar el proyecto. Para el detalle técnico (arquitectura, concurrencia, decisiones) ver el [README](../README.md); para la inconsistencia del archivo original, [DATA_FORENSICS.md](DATA_FORENSICS.md).

## 1. Instalación y arranque

Requisitos: **.NET SDK 8** y **Node 20+**.

```bash
git clone https://github.com/dabrin/ExpenseIntelligence.git && cd ExpenseIntelligence
./install.sh   # verifica o instala .NET 8 y Node 20+, paquetes npm y NuGet, y compila el frontend
./run.sh
```

`install.sh` es seguro de repetir (omite lo ya instalado). `run.sh` compila el frontend la primera vez y levanta todo en **http://localhost:5080** (Swagger en `/swagger`).

La primera vez la app crea su copia de trabajo en `data/working/` a partir del archivo original `data/original/gastos_desordenados.json`. El original **nunca se modifica**.

Pruebas automáticas:

```bash
dotnet test
```

Reiniciar el servidor: detén el proceso (Ctrl+C) y vuelve a ejecutar `./run.sh`. Solo puede haber **una instancia por carpeta de datos**.

## 2. Recorrido por las pantallas

| Menú | Para qué sirve |
|---|---|
| **Dashboard** | KPIs y gráficos: total por moneda, promedio por departamento, top categorías, aprobados / pendientes / rechazados, serie en el tiempo e insights. |
| **Máquina del tiempo** | Reproduce el gasto mes a mes (velocidades 0,25x a 4x) por moneda, departamento y categoría. |
| **Importar archivo** | Carga otro `.json` de gastos (previsualiza, repara lo reparable y avisa de duplicados). |
| **Archivos** | Gestiona los archivos cargados: cambiar entre ellos, comparar, restablecer, eliminar, dejar el sistema limpio. |
| **Gastos** | Tabla con filtros, CRUD, papelera y aprobación/rechazo masivo. |
| **Historial** | Lista legible de cada cambio (qué campo, antes → después, quién). |
| **Data Doctor** | Detecta anomalías de calidad y corrige las que se pueden corregir automáticamente. |
| **Asistente** | Preguntas en lenguaje natural sobre los datos (reglas deterministas, no es un LLM). |
| **Diagnóstico** | Estado del almacenamiento, verificación de integridad, rendimiento y auditoría. |

> Los montos **nunca se suman entre monedas distintas**: todo se muestra por moneda (CRC, USD, EUR).

## 3. Filtrar gastos

La barra de filtros es común al Dashboard, Gastos y Máquina del tiempo. Se combinan entre sí (Y entre campos, O dentro de un mismo campo):

- Rango de fechas y atajos (últimos 30/90 días, 12 meses, todo el histórico).
- Departamento, categoría, moneda y método de pago (selección múltiple).
- Estado: **Todos / Aprobados / Pendientes / Rechazados**.
- Búsqueda por descripción, código de factura o ID exacto.

"Restablecer" limpia todo.

## 4. Crear, editar y eliminar gastos

1. En **Gastos**, pulsa **Nuevo gasto**. Campos obligatorios; el monto debe ser positivo (solo dígitos y punto decimal) y la fecha válida. El código de factura se valida y avisa si ya está en uso.
2. Para editar, usa el lápiz de la fila o haz clic en ella y elige **Editar**.
   - Un gasto **aprobado no permite cambiar monto ni moneda**: quita primero la aprobación y guarda.
   - Si el cambio es relevante (estado o monto) la app muestra una **alerta de revisión** antes de guardar.
   - Si otra persona modificó el gasto mientras lo editabas, aparece un aviso de conflicto con opción de recargar.
3. Eliminar envía el gasto a la **Papelera** (eliminación lógica): sale de las métricas pero se conserva. Tienes un botón **Deshacer** en el aviso, y desde la pestaña **Papelera** puedes **Restaurar** cuando quieras.

## 5. Aprobación y rechazo masivo

1. En **Gastos** marca las casillas de las filas (solo se pueden seleccionar gastos **pendientes**) o la casilla de la cabecera para seleccionar los pendientes de la página.
2. Para abarcar más de una página pulsa **Seleccionar los N resultados** (el servidor resuelve el filtro; los que no estén pendientes se omiten).
3. En la barra flotante elige **Aprobar seleccionados** o **Rechazar seleccionados**.
4. En el modal revisa cuántos se procesarán y los totales **por moneda**. Para rechazar, el **motivo es obligatorio**.
5. Confirma. El lote se guarda **completo o nada**; al terminar verás el resultado y un id de operación que también queda en el Historial.

Si algo cambió mientras seleccionabas (otro cambio, otra cantidad de resultados, otro archivo activo) la app no modifica nada y te pide recargar y volver a seleccionar. La selección se vacía sola al cambiar filtros, la vista o el archivo activo. Límite por operación: 5 000 (configurable en `Bulk:MaxItems`).

Un gasto rechazado deja de contar como pendiente. Para volver a aprobarlo, edítalo y marca "Gasto aprobado".

## 6. Trabajar con varios archivos (A / B)

1. **Importar archivo**: arrastra el `.json`, revisa la previsualización y elige:
   - **Cargar como archivo nuevo** (queda activo y el anterior conserva sus cambios), o
   - **Agregar a los actuales** (suma solo los IDs que no existen).
2. Si el archivo ya fue cargado (mismo contenido) la app lo avisa y te ofrece volver a él en lugar de duplicarlo. Si el nombre coincide con otro, lo trata como una versión.
3. En **Archivos** puedes:
   - **Activar** otro archivo: cada uno **conserva sus propias ediciones**.
   - **Restablecer** un archivo a su original (descarta sus cambios).
   - **Comparar** dos archivos (A vs B) o un archivo contra su original: tabla de diferencias y totales por moneda.
   - **Eliminar** archivos inactivos.
   - **Dejar el sistema limpio**: vuelve al original del sistema y borra archivos cargados, cambios, papelera, historial y respaldos. Es irreversible y exige escribir `LIMPIAR`.

## 7. Data Doctor, Asistente y Diagnóstico

- **Data Doctor**: muestra anomalías (duplicados, fechas, formatos, etc.) con causa y recomendación. Las marcadas como autocorregibles se aplican con un clic y quedan en el Historial.
- **Asistente**: prueba preguntas como "¿cuál es el total gastado por moneda?" o "¿qué gastos siguen pendientes?". Respeta los filtros activos.
- **Diagnóstico**: ejecuta el chequeo de integridad (memoria vs disco), un benchmark y muestra la auditoría reciente.

## 8. Dónde quedan los datos

| Ruta | Contenido |
|---|---|
| `data/original/gastos_desordenados.json` | Original del reto. Solo lectura. |
| `data/working/expenses.json` | Copia de trabajo (la que cambian las operaciones). |
| `data/working/audit.jsonl` | Auditoría: una línea por cambio. |
| `data/working/datasets/` | Estado guardado de cada archivo cargado. |

`data/working/` no se sube al repositorio (está en `.gitignore`).

## 9. Problemas frecuentes

| Síntoma | Qué hacer |
|---|---|
| "Another process already owns the data directory" | Hay otra instancia usando `data/working`. Ciérrala. |
| La página no carga tras actualizar el código | Recarga con Ctrl/Cmd+Shift+R; si falta el frontend, `cd web && npx ng build`. |
| No puedo editar el monto de un gasto | Está aprobado: quita la aprobación y guarda. |
| El lote masivo respondió 409 | Los datos cambiaron: recarga y vuelve a seleccionar. |
| Quiero empezar de cero | Archivos → Dejar el sistema limpio. |

## 10. Docker y Railway

El `Dockerfile` compila el Angular, publica la API y empaqueta el original en `/app/seed`. Los datos vivos van a `/data`, que **debe ser un volumen persistente**: sin volumen, cada despliegue empieza de cero.

```bash
docker build -t insta-expenses .
docker run --rm -p 8080:8080 -v expenses-data:/data insta-expenses   # http://localhost:8080
```

En Railway:

1. New Project → Deploy from GitHub repo → `dabrin/ExpenseIntelligence` (usa `railway.json` y el `Dockerfile`).
2. En el servicio: Settings → Volumes → añade un volumen con mount path `/data`.
3. Settings → Networking → Generate Domain.
4. Mantén **1 réplica**: la app solo admite una instancia por carpeta de datos (`railway.json` ya lo fija).

Railway inyecta `PORT`; el contenedor lo usa automáticamente. Comprobación de salud: `/api/health`.

> La app no tiene autenticación: cualquiera con la URL puede editar, eliminar y ejecutar "Dejar el sistema limpio".
