import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { DatePipe } from '@angular/common';
import { catchError, forkJoin, of, switchMap } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { AuditEntry, Benchmark, Diagnostics, IntegrityReport } from '../../core/models';
import { fmt, ToastService } from '../../core/state';
import { IconComponent } from '../../shared/icon.component';

@Component({
  selector: 'app-diagnostics', standalone: true, imports: [IconComponent, DatePipe], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <header class="head rise"><div><h1>Diagnóstico técnico</h1><p class="muted">Estado real del almacenamiento, tiempos medidos y verificaciones de integridad.</p></div>
    <button class="btn" type="button" (click)="tick.set(tick() + 1)"><app-icon name="refresh" /> Actualizar</button></header>
  @if (error(); as e) { <div class="banner bad" role="alert"><app-icon name="alert" /><span>{{ e }}</span></div> }
  @if (!d()) { <div class="grid k">@for (i of [1,2,3,4]; track i) { <div class="card"><div class="skeleton" style="height:60px"></div></div> }</div> }
  @if (d(); as x) {
    <section class="grid k rise">
      <div class="card stat"><span class="muted">Registros activos</span><b>{{ fmt.num(x.storage.activeRecords) }}</b><small class="muted">{{ x.storage.deletedRecords }} eliminados lógicamente · revisión {{ x.storage.revision }}</small></div>
      <div class="card stat"><span class="muted">Última persistencia</span><b>{{ x.storage.lastPersistedAt ? (x.storage.lastPersistedAt | date: 'mediumTime') : 'Sin escrituras' }}</b><small class="muted">{{ x.storage.lastPersistMs ?? '—' }} ms · {{ x.storage.writes }} escrituras · {{ x.storage.writeFailures }} fallos</small></div>
      <div class="card stat"><span class="muted">Archivo de trabajo</span><b>{{ (x.storage.workingFileBytes / 1048576).toFixed(2) }} MB</b><small class="muted">backup .bak: {{ x.storage.backupExists ? 'sí' : 'aún no' }} · auditoría fallida: {{ x.storage.auditFailures }}</small></div>
      <div class="card stat"><span class="muted">Archivo de origen</span><b [style.color]="x.storage.seedUnchanged ? 'var(--ok)' : 'var(--bad)'">{{ x.storage.seedUnchanged ? 'Intacto' : 'MODIFICADO' }}</b><small class="muted mono">{{ x.storage.seedSha256.slice(0, 20) }}…</small></div>
    </section>

    <section class="grid two" style="margin-top:16px">
      <div class="card">
        <div class="card-head"><div><h2>Verificación de integridad</h2><p>Compara memoria vs archivo en disco, unicidad de IDs y hash del original.</p></div>
          <button class="btn primary sm" type="button" (click)="runIntegrity()" [disabled]="busy() === 'int'">{{ busy() === 'int' ? 'Verificando…' : 'Ejecutar' }}</button></div>
        @if (integrity(); as i) {
          <div class="banner" [class.ok]="i.ok" [class.bad]="!i.ok"><app-icon [name]="i.ok ? 'check' : 'alert'" /><div><strong>{{ i.ok ? 'Integridad verificada' : 'Se encontraron problemas' }}</strong> en {{ i.elapsedMs }} ms
            <div class="muted">Memoria {{ fmt.num(i.recordsInMemory) }} · disco {{ fmt.num(i.recordsOnDisk) }} · IDs únicos: {{ i.idsUnique ? 'sí' : 'no' }} · coincide: {{ i.memoryMatchesDisk ? 'sí' : 'no' }}</div>
            @for (p of i.problems; track p) { <div>{{ p }}</div> }</div></div>
        } @else { <p class="muted">Aún no ejecutada en esta sesión.</p> }
        <dl class="kv" style="margin-top:14px"><dt>Modelo</dt><dd>{{ x.storage.concurrencyModel }}</dd><dt>Archivo</dt><dd class="mono" style="font-size:12px">{{ x.storage.workingFile }}</dd></dl>
      </div>
      <div class="card">
        <div class="card-head"><div><h2>Benchmark de lectura</h2><p>Consultas reales en memoria (solo lectura, no toca datos).</p></div>
          <button class="btn primary sm" type="button" (click)="runBench()" [disabled]="busy() === 'bench'">{{ busy() === 'bench' ? 'Midiendo…' : 'Medir' }}</button></div>
        @if (bench(); as b) {
          <table class="mini"><thead><tr><th>Operación</th><th class="r">p50</th><th class="r">p95</th><th class="r">máx</th></tr></thead><tbody>
            @for (r of b.results; track r.name) { <tr><td>{{ r.name }}</td><td class="r">{{ r.p50Ms }} ms</td><td class="r">{{ r.p95Ms }} ms</td><td class="r">{{ r.maxMs }} ms</td></tr> }</tbody></table>
          <p class="faint" style="font-size:12px;margin:8px 0 0">{{ fmt.num(b.records) }} registros · {{ b.results[0].iterations }} iteraciones por caso tras calentamiento.</p>
        } @else { <p class="muted">Pulsa «Medir» para ejecutar.</p> }
      </div>
    </section>

    <section class="card" style="margin-top:16px">
      <div class="card-head"><div><h2>Tiempos de respuesta de la API</h2><p>Medidos en servidor sobre las últimas 500 peticiones por ruta.</p></div></div>
      <div class="table-wrap" style="border:0"><table class="mini full"><thead><tr><th>Ruta</th><th class="r">Peticiones</th><th class="r">p50</th><th class="r">p95</th><th class="r">máx</th><th class="r">media</th></tr></thead><tbody>
        @for (t of x.timings; track t.route) { <tr><td class="mono">{{ t.route }}</td><td class="r">{{ t.count }}</td><td class="r">{{ t.p50Ms }} ms</td><td class="r">{{ t.p95Ms }} ms</td><td class="r">{{ t.maxMs }} ms</td><td class="r">{{ t.avgMs }} ms</td></tr> }</tbody></table></div>
    </section>

    <section class="grid two" style="margin-top:16px">
      <div class="card"><h2>Proceso .NET</h2><dl class="kv" style="margin-top:12px">
        <dt>Runtime</dt><dd>{{ x.process.runtime }}</dd><dt>Uptime</dt><dd>{{ x.process.uptimeSeconds }} s</dd><dt>Working set</dt><dd>{{ x.process.workingSetMb }} MB</dd><dt>Heap GC</dt><dd>{{ x.process.gcHeapMb }} MB</dd>
        <dt>Colecciones GC</dt><dd>gen0 {{ x.process.gen0 }} · gen1 {{ x.process.gen1 }} · gen2 {{ x.process.gen2 }}</dd><dt>Tiempo en pausa</dt><dd>{{ x.process.pauseTimePercent }}%</dd></dl></div>
      <div class="card"><h2>Carga inicial del archivo</h2><dl class="kv" style="margin-top:12px">
        <dt>Estrategia</dt><dd>{{ x.storage.loadReport.strategy }}</dd><dt>Tamaño</dt><dd>{{ (x.storage.loadReport.bytes / 1048576).toFixed(2) }} MB</dd><dt>Tiempo</dt><dd>{{ x.storage.loadReport.elapsedMs }} ms</dd>
        <dt>Objetos / cargados</dt><dd>{{ x.storage.loadReport.objectsFound }} / {{ x.storage.loadReport.loaded }}</dd><dt>Reparados / cuarentena</dt><dd>{{ x.storage.loadReport.repaired }} / {{ x.storage.loadReport.quarantined }}</dd></dl></div>
    </section>

    <section class="card" style="margin-top:16px">
      <div class="card-head"><div><h2>Auditoría</h2><p>Últimas operaciones de escritura (con valores anterior y posterior en el log).</p></div></div>
      @if (audit().length === 0) { <p class="muted">Sin operaciones registradas todavía.</p> }
      @else { <div class="table-wrap" style="border:0"><table class="mini full"><thead><tr><th>Cuándo</th><th>Acción</th><th class="r">Gasto</th><th>Actor</th><th class="r">Revisión</th></tr></thead><tbody>
        @for (a of audit(); track a.at + a.expenseId) { <tr><td>{{ a.at | date: 'medium' }}</td><td><span class="badge plain" [class.bad]="a.action === 'delete'" [class.ok]="a.action === 'create'">{{ a.action }}</span></td><td class="r mono">{{ a.expenseId ? '#' + a.expenseId : '—' }}</td><td>{{ a.actor }}</td><td class="r mono">{{ a.revision }}</td></tr> }</tbody></table></div> }
    </section>
  }`,
  styles: [`
    .head { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px; flex-wrap: wrap; margin-bottom: 18px; p { margin: 4px 0 0; } }
    .k { grid-template-columns: repeat(auto-fit, minmax(230px, 1fr)); } .two { grid-template-columns: repeat(auto-fit, minmax(340px, 1fr)); }
    .stat { display: grid; gap: 4px; b { font-size: 22px; letter-spacing: -.02em; } small { font-size: 12px; } }
    .mini { width: 100%; border-collapse: collapse; th { text-align: left; color: var(--muted); font-size: 11.5px; text-transform: uppercase; letter-spacing: .05em; padding: 7px 10px; &.r { text-align: right; } } td { padding: 8px 10px; border-top: 1px solid var(--border); &.r { text-align: right; font-variant-numeric: tabular-nums; } } }
    .full { min-width: 640px; }
  `],
})
export class DiagnosticsComponent {
  private api = inject(ApiService);
  private toast = inject(ToastService);
  fmt = fmt;
  tick = signal(0);
  d = signal<Diagnostics | null>(null);
  audit = signal<AuditEntry[]>([]);
  integrity = signal<IntegrityReport | null>(null);
  bench = signal<Benchmark | null>(null);
  busy = signal<'int' | 'bench' | null>(null);
  error = signal<string | null>(null);

  constructor() {
    toObservable(this.tick).pipe(switchMap(() => forkJoin({ d: this.api.diagnostics(), a: this.api.audit(30) }).pipe(catchError(e => { this.error.set(asProblem(e).title); return of(null); }))), takeUntilDestroyed())
      .subscribe(r => { if (r) { this.d.set(r.d); this.audit.set(r.a); this.error.set(null); } });
  }
  runIntegrity() { this.busy.set('int'); this.api.integrity().subscribe({ next: r => { this.integrity.set(r); this.busy.set(null); }, error: e => { this.busy.set(null); this.toast.show('error', asProblem(e).title); } }); }
  runBench() { this.busy.set('bench'); this.api.benchmark().subscribe({ next: r => { this.bench.set(r); this.busy.set(null); this.tick.update(v => v + 1); }, error: e => { this.busy.set(null); this.toast.show('error', asProblem(e).title); } }); }
}
