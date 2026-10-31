# Expense Data Integrity Test Suite

Archivos derivados de `gastos_desordenados.json`. **El original no se modificó.**

- `00_repaired_baseline.json`: versión con la comilla faltante del primer `id` restaurada. No supone que los datos estén libres de anomalías semánticas.
- `01` a `08`: JSON sintácticamente válido con anomalías introducidas.
- `09` a `11`: JSON sintácticamente inválido; debe rechazarse sin sobrescribir datos.
- `test_manifest.json`: localización de errores inyectados, SHA-256 y resultados esperados de sintaxis.

Los índices de registro son **base cero**. Los errores inyectados son adicionales a cualquier anomalía preexistente en el dataset. Los valores no canónicos y los montos extremos pueden requerir reglas de negocio para clasificarlos.

Para pruebas destructivas o de concurrencia, usar siempre copias aisladas. No ejecutar contra el original.
