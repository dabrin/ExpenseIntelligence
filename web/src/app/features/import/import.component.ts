import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService, asProblem } from '../../core/api.service';
import { DatePipe } from '@angular/common';
import { RouterLink } from '@angular/router';
import { ImportPreview } from '../../core/models';
import { DataVersion, FilterStore, ToastService, fmt } from '../../core/state';
import { IconComponent } from '../../shared/icon.component';

@Component({
  selector: 'app-import', standalone: true, imports: [IconComponent, DatePipe, RouterLink], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <header class="head rise"><h1>Importar archivo</h1>
    <p class="muted">Sube un JSON de gastos. Primero se <strong>analiza sin modificar nada</strong> (integridad, reparaciones, cuarentena y calidad); solo al confirmar se carga. Cada archivo se conserva intacto y el anterior <strong>no se pierde</strong>: queda guardado con sus cambios y puedes volver a él desde <a routerLink="/archivos">Archivos</a>.</p></header>

  @if (!preview()) {
    <label class="drop" [class.over]="over()" (dragover)="$event.preventDefault(); over.set(true)" (dragleave)="over.set(false)" (drop)="onDrop($event)">
      <input type="file" accept=".json,application/json" class="sr-only" (change)="onPick($event)" [disabled]="busy()" />
      <app-icon name="upload" [size]="34" />
      @if (busy()) { <strong>Analizando archivo…</strong><span class="skeleton" style="width:200px;height:10px"></span> }
      @else { <strong>Arrastra tu archivo .json aquí o haz clic para elegirlo</strong><span class="muted">Formato: arreglo JSON con id, descripcion, monto, moneda, categoria, departamento, metodo_pago, fecha, aprobado, codigo_factura · máx. 100 MB</span> }
    </label>
    @if (error()) { <div class="banner bad" role="alert" style="margin-top:14px"><app-icon name="alert" /><span>{{ error() }}</span></div> }
  } @else {
    @if (preview(); as p) {
      <section class="card rise" style="margin-bottom:16px">
        <div class="row"><app-icon name="inbox" [size]="22" /><div><h2 style="font-size:17px">{{ p.fileName }}</h2><span class="muted">{{ (p.bytes / 1048576).toFixed(2) }} MB · SHA-256 <span class="mono">{{ p.sha256.slice(0, 16) }}…</span></span></div><span class="spacer"></span>
          <button class="btn" type="button" (click)="reset()">Elegir otro</button></div>
        @if (p.duplicate; as d) {
          <div class="banner warn" style="margin-top:14px"><app-icon name="alert" />
            <div><strong>Este archivo ya existe en el sistema.</strong>
              <div class="muted">Es «{{ d.name }}», cargado el {{ d.addedAt | date: 'dd/MM/yyyy HH:mm' }}. @if (d.isActive) { Es el archivo que estás usando ahora. }
                @if (d.hasEdits) { Tiene cambios guardados: {{ d.changed }} editados, {{ d.added }} agregados, {{ d.removed }} eliminados. } @else { No tiene cambios respecto al original. }</div></div></div>
          <div class="row" style="margin-top:12px">
            @if (!d.isActive) { <button class="btn primary" type="button" (click)="useExisting(p, 'switch')" [disabled]="busy()">Volver a ese archivo (conserva sus cambios)</button> }
            @if (d.hasEdits) { <button class="btn" type="button" (click)="useExisting(p, 'reset')" [disabled]="busy()">Empezar de nuevo con el original (descarta sus cambios)</button> }
            @if (d.isActive && !d.hasEdits) { <span class="muted">No hay nada que cargar: ya estás usando este archivo tal cual.</span> }
          </div>
        } @else {
        @if (p.sameName.length) {
          <div class="banner info" style="margin-top:14px"><app-icon name="info" />
            <div><strong>Ya hay {{ p.sameName.length === 1 ? 'una versión anterior' : p.sameName.length + ' versiones anteriores' }} con este nombre.</strong>
              <div class="muted">Es contenido distinto al de {{ p.sameName[0].addedAt | date: 'dd/MM/yyyy HH:mm' }}. Al cargarlo se guardan ambos y podrás compararlos en <a routerLink="/archivos">Archivos</a>.</div></div></div>
        }
        <div class="row" style="margin-top:14px">
          <div class="seg" role="group" aria-label="Qué hacer con los datos actuales">
            <button type="button" [class.on]="replace()" (click)="replace.set(true)">Cambiar a este archivo</button>
            <button type="button" [class.on]="!replace()" (click)="replace.set(false)">Conservar y agregar</button></div>
          <span class="muted">@if (replace()) { Los {{ fmt.num(p.summary.currentRecords) }} registros actuales quedan guardados con sus cambios en el archivo anterior; puedes volver a él cuando quieras. }
            @else { Se suman los {{ fmt.num(p.summary.records - p.summary.idCollisions) }} registros con ID nuevo; {{ p.summary.idCollisions }} con ID ya existente se omiten sin sobrescribir. }</span>
          <span class="spacer"></span>
          <button class="btn primary" type="button" (click)="commit(p)" [disabled]="!p.canImport || busy()">{{ busy() ? 'Importando…' : replace() ? 'Cargar archivo (' + fmt.num(p.summary.currentRecords) + ' → ' + fmt.num(p.summary.records) + ')' : 'Agregar al dataset' }}</button></div>
        @if (p.blocker) { <div class="banner bad" role="alert" style="margin-top:12px"><app-icon name="alert" /><span>{{ p.blocker }}</span></div> }
        }
        @if (error()) { <div class="banner bad" role="alert" style="margin-top:12px"><app-icon name="alert" /><span>{{ error() }}</span></div> }
      </section>

      <section class="grid k rise">
        <div class="card stat"><span class="muted">Registros cargados</span><b>{{ fmt.num(p.report.loaded) }}</b><small class="muted">de {{ fmt.num(p.report.objectsFound) }} objetos leídos</small></div>
        <div class="card stat"><span class="muted">Reparados</span><b [style.color]="p.report.repaired ? 'var(--warn)' : 'inherit'">{{ p.report.repaired }}</b><small class="muted">reparación determinista y auditada</small></div>
        <div class="card stat"><span class="muted">En cuarentena</span><b [style.color]="p.report.quarantined ? 'var(--bad)' : 'var(--ok)'">{{ p.report.quarantined }}</b><small class="muted">{{ p.report.duplicateIds }} IDs duplicados{{ p.report.truncated ? ' · archivo truncado' : '' }}</small></div>
        <div class="card stat"><span class="muted">Calidad de datos</span><b>{{ p.doctor.qualityScore }}</b><small class="muted">{{ p.summary.minFecha }} → {{ p.summary.maxFecha }}</small></div>
      </section>

      @if (p.report.repairs.length) {
        <section class="card" style="margin-top:16px"><h2>Reparaciones que se aplicarán</h2>
          @for (x of p.report.repairs; track x.line) {
            <p class="muted" style="margin:10px 0 6px">Línea {{ x.line }} · registro #{{ x.recordId }} · {{ x.description }}</p>
            <pre class="diff"><span class="rm">- {{ x.before }}</span>
<span class="add">+ {{ x.after }}</span></pre> }</section>
      }
      @if (p.report.issues.length) {
        <section class="card" style="margin-top:16px"><h2>Registros que NO se importarán (cuarentena)</h2>
          <p class="muted" style="margin:4px 0 10px">No se descarta nada en silencio: aquí está el motivo y el texto original.</p>
          @for (i of p.report.issues.slice(0, 10); track i.index) {
            <details class="iss"><summary><span class="badge bad plain">línea {{ i.line }}</span> {{ i.reason }}</summary><pre class="diff">{{ i.raw }}</pre></details> }
          @if (p.report.issues.length > 10) { <p class="faint">… y {{ p.report.issues.length - 10 }} más (se guardan completos en quarantine.json al importar).</p> }</section>
      }

      <section class="card" style="margin-top:16px"><h2>Hallazgos de calidad de este archivo</h2>
        @if (p.doctor.anomalies.length === 0) { <p class="muted">Sin hallazgos.</p> }
        @for (a of p.doctor.anomalies; track a.code) {
          <div class="an"><span class="badge" [class.bad]="a.severity === 'error'" [class.warn]="a.severity === 'warning'">{{ a.severity === 'error' ? 'Error' : a.severity === 'warning' ? 'Advertencia' : 'Info' }}</span>
            <div><strong>{{ a.title }}</strong> <span class="mono muted">· {{ fmt.num(a.affectedCount) }} afectados</span><div class="muted">{{ a.cause }}</div></div></div> }</section>
    }
  }`,
  styles: [`
    .head { margin-bottom: 18px; p { margin: 4px 0 0; } }
    .drop { display: grid; place-items: center; gap: 10px; text-align: center; padding: 56px 20px; border: 2px dashed var(--border-2); border-radius: 18px; background: var(--surface); cursor: pointer; transition: all .15s;
      &:hover, &.over { border-color: var(--accent); background: var(--accent-bg); } app-icon { color: var(--accent-2); } }
    .k { grid-template-columns: repeat(auto-fit, minmax(210px, 1fr)); } .stat { display: grid; gap: 4px; b { font-size: 24px; letter-spacing: -.02em; } small { font-size: 12px; } }
    .diff { margin: 6px 0 0; padding: 10px 12px; background: var(--bg-2); border-radius: 8px; font: 12px/1.6 var(--mono); overflow: auto; white-space: pre-wrap; .rm { color: var(--bad); } .add { color: var(--ok); } }
    .iss { padding: 8px 0; border-top: 1px solid var(--border); summary { cursor: pointer; } }
    .an { display: flex; gap: 12px; align-items: flex-start; padding: 10px 0; border-top: 1px solid var(--border); }
  `],
})
export class ImportComponent {
  private api = inject(ApiService);
  private dv = inject(DataVersion);
  private filters = inject(FilterStore);
  private toast = inject(ToastService);
  private router = inject(Router);
  fmt = fmt;
  preview = signal<ImportPreview | null>(null);
  busy = signal(false);
  over = signal(false);
  replace = signal(true);
  error = signal<string | null>(null);

  onPick(e: Event) { const f = (e.target as HTMLInputElement).files?.[0]; if (f) this.analyze(f); (e.target as HTMLInputElement).value = ''; }
  onDrop(e: DragEvent) { e.preventDefault(); this.over.set(false); const f = e.dataTransfer?.files?.[0]; if (f) this.analyze(f); }
  reset() { this.preview.set(null); this.error.set(null); }

  private analyze(f: File) {
    this.error.set(null); this.busy.set(true);
    this.api.importPreview(f).subscribe({
      next: p => { this.preview.set(p); this.busy.set(false); },
      error: e => { const pr = asProblem(e); this.error.set(pr.errors?.['file']?.[0] ?? pr.title); this.busy.set(false); },
    });
  }

  useExisting(p: ImportPreview, mode: 'switch' | 'reset') {
    this.busy.set(true); this.error.set(null);
    this.api.importCommit(p.token, mode, p.duplicate?.id).subscribe({
      next: () => {
        this.busy.set(false); this.filters.reset(); this.dv.bump();
        this.toast.show('ok', mode === 'switch' ? `Volviste a «${p.duplicate?.name}» con todos sus cambios.` : `«${p.duplicate?.name}» volvió a su estado original.`, undefined, 7000);
        this.router.navigateByUrl('/dashboard');
      },
      error: e => { this.busy.set(false); this.error.set(asProblem(e).title); },
    });
  }

  commit(p: ImportPreview) {
    this.busy.set(true); this.error.set(null);
    this.api.importCommit(p.token, this.replace() ? 'new' : 'merge').subscribe({
      next: r => {
        this.busy.set(false); this.filters.reset(); this.dv.bump();
        this.toast.show('ok', (r.mode === 'merge' ? `Agregados ${r.records} registros (${r.skipped} omitidos por ID existente).` : `Importados ${r.records} registros (${r.repaired} reparados, ${r.quarantined} en cuarentena).`), undefined, 7000);
        this.router.navigateByUrl('/dashboard');
      },
      error: e => { const pr = asProblem(e); this.busy.set(false); this.error.set(pr.errors?.['file']?.[0] ?? pr.title); },
    });
  }
}
