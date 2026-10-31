# Archivos de prueba

Archivos derivados de `gastos_desordenados.json` (el original no se modifica) para comprobar que la aplicación tolera datos sucios y archivos dañados sin perder ni corromper información. Son 10 000 registros cada uno, salvo 09 y 10 (100 registros).

## Cómo usarlos

1. Arranca la app y abre **Importar archivo**.
2. Arrastra uno de los `.json` de esta carpeta. La previsualización muestra qué se cargaría, qué se repara y qué queda en cuarentena, sin tocar nada hasta que confirmes.
3. Elige **Cargar como archivo nuevo**: queda aislado y puedes volver al archivo anterior desde **Archivos**. Revisa también **Data Doctor** para ver las anomalías detectadas.

## Resultado de la importación (medido con la previsualización)

| Archivo | Qué contiene | Cargados | Cuarentena | Otros |
|---|---|---|---|---|
| `00_repaired_baseline.json` | Original con la comilla faltante restaurada | 10 000 | 0 | Línea base |
| `01_duplicate_ids.json` | 3 IDs duplicados | 9 997 | 3 | 3 duplicados detectados |
| `02_invalid_dates.json` | Fechas imposibles (`2026-02-30`, `not-a-date`, `2026/13/99`, vacía) | 9 996 | 4 | |
| `03_invalid_amounts.json` | Montos `-100`, `0`, `null` y `"cien"` | 9 998 | 2 | |
| `04_inconsistent_types.json` | Tipos incorrectos en `id`, `aprobado`, `monto` y `categoria` | 9 996 | 4 | |
| `05_missing_fields.json` | Faltan `id`, `moneda`, `fecha` y `departamento` | 9 996 | 4 | |
| `06_inconsistent_values.json` | Valores no canónicos: moneda `usd` y `XYZ`, departamento con espacios, método de pago `Crypto` | 10 000 | 0 | Visibles en Data Doctor |
| `07_edge_cases.json` | Casos límite en descripción, monto y código de factura | 10 000 | 0 | |
| `08_chaos_mixed.json` | Un error distinto en cada campo (id, fecha, monto, moneda, aprobado, categoría, departamento) | 9 996 | 4 | 1 duplicado |
| `09_truncated_json.json` | JSON cortado a la mitad | 99 | 1 | Detectado como truncado |
| `10_invalid_json_comma.json` | Coma inválida en el JSON | 99 | 1 | |
| `11_original_syntax_error.json` | Original del reto, con la comilla faltante | 10 000 | 0 | 1 registro reparado |

Ningún archivo bloquea la app: lo recuperable se carga, lo irrecuperable va a cuarentena con su motivo, y el archivo activo anterior no se sobrescribe.

## Otros archivos

- `test_manifest.json`: posición (base cero) de cada error inyectado, SHA-256 de cada archivo y resultado esperado de sintaxis.
- `README_origen.md`: nota original que acompañaba la suite.

Para pruebas destructivas o de concurrencia usa siempre copias; no ejecutes nada contra `data/original/`.
