# Auditoría de seguridad – Insta Expenses

Auditoría hecha con la skill `security-audit` (perfil `standard`) sobre el commit `9ac048b` (los únicos cambios sin commitear eran documentación).

## Estado y alcance

- **Cobertura:** 17 unidades de código, 3 oleadas de cazadores (h01–h17), con críticos de cobertura y un crítico final distinto.
- **Candidatos:** 9, cada uno revisado por un verificador independiente (v01–v09).
- **Auditoría incompleta, a propósito.** Se detuvo por falta de presupuesto antes de:
  - el veredicto del candidato v03 (cardinalidad de claves de métricas), que queda sin resolver;
  - la fase 5 (segunda verificación independiente de cada registro) y los validadores finales.
- Por eso solo hay **un hallazgo confirmado**. El resto son `needs_validation`: el código lo respalda, pero falta un dato fuera del repo, como la configuración de Railway, o la reproducción de extremo a extremo.
- **Límites del entorno:**
  - No se pudo ejecutar `dotnet test` dentro del sandbox. Las reproducciones usaron harnesses de consola.
  - No se probó nada contra Railway.
  - El crítico final no leyó `prestamito-avatar.component.ts` ni `main.ts`.

## Contexto de amenaza

Hay un solo principal: el usuario de Basic auth compartido (`APP_PASSWORD`), o acceso anónimo si no está definido. Ese principal ya tiene CRUD total, importar y cambiar de dataset. Por eso casi todo lo encontrado es de **integridad o disponibilidad dentro del mismo principal**, no escalada de privilegios.

## Hallazgo confirmado

### 1. El modo PUBLIC_DEMO no bloquea el reset de dataset — severidad baja
- **Qué pasa:** con `PUBLIC_DEMO=true` se bloquea con 403 `factory-reset` y `DELETE /api/datasets*`. Pero `POST /api/datasets/{id}/activate` con `{"reset":true}` pasa. Esa llamada descarta las ediciones guardadas del dataset y borra su carpeta de estado. La variante `mode=reset` del commit de importación llega al mismo código (verificado leyendo el código, no ejecutado).
- **Reproducción local (independiente, dos veces):** total 10.002 → 10.000, `factory-reset` y `DELETE` 403, `activate` con `reset=true` 200, la auditoría registra `dataset-reset`.
- **Impacto:** solo devuelve los datos de demo a su estado original, y el mismo usuario ya puede editar y borrar todo. Aplica solo si Railway tiene `PUBLIC_DEMO=true`.
- **Arreglo propuesto:** denegar `reset=true` en `activate` y `mode=reset` en el commit bajo `PUBLIC_DEMO`, o aplicar la regla dentro de `JsonExpenseStore.ActivateAsync`.

## Estado de los arreglos

Aplicados después de la auditoría. Compila y pasan los 137 tests. No se agregaron pruebas nuevas ni se repitieron las reproducciones, así que están sin verificar contra los casos originales.

| Hallazgo | Estado | Cambio |
|---|---|---|
| 1 PUBLIC_DEMO reset | Corregido | `Program.cs`: 403 en `activate` con `reset=true` y en el commit con `mode=reset` bajo `PUBLIC_DEMO` |
| 2 Amplificación al importar | Mitigado | `RecoveryReader`: `Raw` recortado a 2.000 caracteres e informe con las primeras 200 incidencias (el contador total sigue completo). No hay tope de objetos por subida |
| 3 Valores sin validar | Mitigado | `RecoveryReader`: se rechaza (cuarentena) un monto con valor absoluto mayor a 1e12 o un año fuera de 1900–2100 |
| 4 Auditoría sin límite | Corregido | `ReadAudit` lee solo los últimos 2 MB |
| 5 Archivos huérfanos del merge | Corregido | `ImportService` borra la copia guardada si el merge no agrega nada |
| 6, 7, 8, 9 | Aceptado / sin resolver | Quedan como notas |

## Hallazgos pendientes de validación

| # | Hallazgo | Qué falta para confirmar | Arreglo propuesto |
|---|----------|--------------------------|-------------------|
| 2 | **Amplificación al importar un archivo malformado** (`RecoveryReader`). Con 50.000 objetos vacíos (150 KB) se obtuvieron 50.000 incidencias, 18,5 MB retenidos y un informe de 5,5 MB (36,6 veces la entrada). Medido en dos corridas independientes. | A 25 MB solo hay extrapolación (~3,3 GB, ~960 MB de respuesta, ~115 s de CPU). Depende del límite de memoria de Railway. | Guardar y devolver solo las primeras 200 incidencias más un contador, recortar `Raw` a ~2.000 caracteres y poner un tope de objetos por subida. |
| 3 | **Valores importados sin validar.** La importación no aplica las reglas de crear/editar (fecha 2000..hoy, monto hasta 1e9). Dos filas con `monto = decimal.MaxValue` hacen fallar con overflow el resumen, la línea de tiempo y los insights. Una fila fechada 9999-12-31 rompe solo el resumen. Probado solo en la capa de analítica, con datos de prueba. | No se probó el camino real de importar y consultar por HTTP. Se arregla borrando la fila. | Validar rango de fecha y monto al importar, o usar aritmética acotada en las agregaciones. |
| 4 | **Lectura completa del archivo de auditoría** (`audit.jsonl`), que crece sin límite. | Cuánto tarda con un archivo grande. | Leer solo la parte final o con tope. |
| 5 | **Archivos huérfanos en el merge de importación.** Cada merge guarda una copia del archivo aunque no aplique nada. | Ritmo de acumulación frente al volumen de Railway. | Borrar el archivo guardado cuando el merge no aplica registros. |
| 6 | **Ediciones de un solo gasto sin id de dataset.** Una pestaña desactualizada, tras cambiar de dataset, podría modificar un registro del otro dataset con el mismo id y la misma versión. | Si los datasets reales comparten ids con igual versión. Es integridad, no un límite de seguridad. | Añadir `datasetId` a los contratos de edición, borrado y cambio de estado, como ya hace el bulk. |
| 7 | **Dataset activo no cambia de forma atómica.** Solo se desincroniza si hay un crash a mitad del cambio. | Reproducir el crash. | Persistir el cambio de forma atómica y recuperar al arrancar. |
| 8 | **DataDoctor: arreglo sin cambios.** Un arreglo que no cambia nada igual sube la revisión y sobrescribe el `.bak`. | Reproducción en ejecución. | No escribir ni subir la revisión si no hay cambios. |
| 9 | **v03:** cardinalidad de claves de métricas. | Sin veredicto, la verificación se detuvo. | — |

## Notas de endurecimiento (sin candidato)

- El contenedor corre como root: añadir `USER`.
- Swagger está activo salvo con `PUBLIC_DEMO=true`.
- No hay CSP, HSTS ni cabeceras de seguridad.
- Basic auth no tiene bloqueo por intentos (solo 500 ms de retraso).
- La clave de OpenAI debe vivir solo en variables de Railway. La que se expuso antes hay que revocarla.
- Se pueden borrar gastos ya aprobados.
- Los fallos de escritura de auditoría se ignoran.
- Hay un `src/JsonWriter.cs` suelto.

## Qué sí se revisó y salió limpio

Despliegue, criptografía y secretos, asistente LLM (herramientas de solo lectura y lista blanca de `open_page`), exportación e inyección, componentes Angular, comparación de datasets y scripts de arranque.

## Para el evaluador

Es una auditoría **parcial**, y dice dónde se detuvo. Se priorizó que lo confirmado esté reproducido y que lo no confirmado esté marcado como tal. Falta cerrar la fase 5 y v03, y aplicar los arreglos. El estado de cada arreglo (corregido o aceptado) se anota en este documento cuando se aplique.
