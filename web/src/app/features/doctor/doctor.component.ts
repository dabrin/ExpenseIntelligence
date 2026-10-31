import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, of, switchMap, tap } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { Anomaly, DoctorReport } from '../../core/models';
import { DataVersion, ToastService, fmt } from '../../core/state';
import { IconComponent } from '../../shared/icon.component';

@Component({
  selector: 'app-doctor', standalone: true, imports: [IconComponent, RouterLink], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <header class="head rise"><div><h1>Data Doctor</h1><p class="muted">Centro de calidad de datos: detecta, explica y corrige de forma segura y auditada. El archivo original nunca se modifica.</p></div>
    <button class="btn" type="button" (click)="dv.bump()"><app-icon name="refresh" /> Re-analizar</button></header>

  @if (error(); as e) { <div class="banner bad" role="alert"><app-icon name="alert" /><span>{{ e }}</span></div> }
  @if (loading() && !report()) { <div class="grid" style="grid-template-columns:repeat(auto-fit,minmax(220px,1fr))">@for (i of [1,2,3]; track i) { <div class="card"><div class="skeleton" style="height:90px"></div></div> }</div> }

  @if (report(); as r) {
    <section class="grid top rise">
      <div class="card score">
        <svg viewBox="0 0 120 120" width="120" height="120" role="img" [attr.aria-label]="'Calidad de datos ' + r.qualityScore + ' de 100'">
          <circle cx="60" cy="60" r="52" fill="none" stroke="var(--bg-2)" stroke-width="11"/>
          <circle cx="60" cy="60" r="52" fill="none" [attr.stroke]="r.qualityScore >= 95 ? 'var(--ok)' : r.qualityScore >= 80 ? 'var(--warn)' : 'var(--bad)'" stroke-width="11" stroke-linecap="round"
            [attr.stroke-dasharray]="(r.qualityScore / 100 * 326.7) + ' 326.7'" transform="rotate(-90 60 60)" style="transition: stroke-dasharray .8s"/>
          <text x="60" y="66" text-anchor="middle" fill="var(--text)" font-size="26" font-weight="700">{{ r.qualityScore }}</text></svg>
        <div><h2>Puntaje de calidad</h2><p class="muted">{{ fmt.num(r.affectedRecords) }} de {{ fmt.num(r.records) }} registros con hallazgos de severidad error/advertencia.</p></div>
      </div>
      <div class="card">
        <h2>Ingesta del archivo original</h2>
        <dl class="kv" style="margin-top:12px">
          <dt>Objetos leídos</dt><dd>{{ fmt.num(r.ingest.objectsFound) }}</dd><dt>Cargados</dt><dd>{{ fmt.num(r.ingest.loaded) }}</dd>
          <dt>Reparados</dt><dd>{{ r.ingest.repaired }}</dd><dt>En cuarentena</dt><dd>{{ r.ingest.quarantined }}</dd><dt>IDs duplicados</dt><dd>{{ r.ingest.duplicateIds }}</dd>
          <dt>SHA-256</dt><dd class="mono" style="font-size:11.5px">{{ r.ingest.sha256.slice(0, 24) }}…</dd></dl>
      </div>
      @if (r.ingest.repairs.length) {
        <div class="card">
          <h2>Reparación determinista aplicada al leer</h2>
          @for (x of r.ingest.repairs; track x.line) {
            <p class="muted" style="margin:8px 0 6px">Línea {{ x.line }} · registro #{{ x.recordId }} · {{ x.description }}</p>
            <pre class="diff"><span class="rm">- {{ x.before }}</span>
<span class="add">+ {{ x.after }}</span></pre>
          }
          <p class="faint" style="font-size:12px;margin:8px 0 0">Solo se corrige una comilla faltante en una clave; el original queda intacto en <code>data/original</code>.</p>
        </div>
      }
    </section>

    <div class="row" style="margin:22px 0 12px">
      <h2 style="font-size:17px">Anomalías</h2>
      <div class="seg" role="group" aria-label="Filtrar por severidad">
        @for (s of sevs; track s.k) { <button type="button" [class.on]="sev() === s.k" (click)="sev.set(s.k)">{{ s.l }} ({{ count(s.k) }})</button> }
      </div>
    </div>

    @if (visible().length === 0) { <div class="card empty"><app-icon name="check" [size]="38" /><strong>Sin hallazgos en esta categoría</strong></div> }
    <div class="list">
      @for (a of visible(); track a.code) {
        <article class="card an rise" [class]="a.severity">
          <div class="row">
            <span class="badge" [class.bad]="a.severity === 'error'" [class.warn]="a.severity === 'warning'">{{ a.severity === 'error' ? 'Error' : a.severity === 'warning' ? 'Advertencia' : 'Info' }}</span>
            <h3 style="font-size:14.5px">{{ a.title }}</h3><span class="spacer"></span>
            <span class="mono muted">{{ fmt.num(a.affectedCount) }} afectados</span>
            <span class="mono faint">{{ a.code }}</span>
          </div>
          <dl class="kv" style="margin-top:10px"><dt>Causa</dt><dd>{{ a.cause }}</dd><dt>Recomendación</dt><dd>{{ a.recommendation }}</dd></dl>
          @if (a.recordIds.length) {
            <div class="row ids"><span class="faint">Registros:</span>
              @for (id of a.recordIds.slice(0, expanded().has(a.code) ? 60 : 8); track id) { <a class="chip mono" routerLink="/gastos" [queryParams]="{ q: id }">#{{ id }}</a> }
              @if (a.recordIds.length > 8) { <button type="button" class="btn sm ghost" (click)="toggle(a.code)">{{ expanded().has(a.code) ? 'Ver menos' : 'Ver más' }}</button> }
            </div>
          }
          @if (a.autoFixable) {
            <div class="row" style="margin-top:12px"><button class="btn primary sm" type="button" (click)="confirmFix.set(a)"><app-icon name="shield" [size]="14" /> Aplicar corrección segura</button></div>
          }
        </article>
      }
    </div>
  }

  @if (confirmFix(); as a) {
    <div class="backdrop" (mousedown)="$event.target === $event.currentTarget ? confirmFix.set(null) : null">
      <div class="modal" style="width:min(480px,100%)" role="alertdialog" aria-modal="true">
        <div class="m-head"><h2 style="font-size:18px">Aplicar corrección: {{ a.title }}</h2></div>
        <div class="m-body"><p class="muted" style="margin:0">Se modificarán {{ fmt.num(a.affectedCount) }} registros. Cada cambio queda en la auditoría con su valor anterior y se puede revisar en Diagnóstico.</p></div>
        <div class="m-foot"><button class="btn" type="button" (click)="confirmFix.set(null)">Cancelar</button><button class="btn primary" type="button" (click)="fix(a)" [disabled]="fixing()">{{ fixing() ? 'Aplicando…' : 'Confirmar' }}</button></div>
      </div></div>
  }`,
  styles: [`
    .head { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px; flex-wrap: wrap; margin-bottom: 18px; p { margin: 4px 0 0; max-width: 70ch; } }
    .top { grid-template-columns: repeat(auto-fit, minmax(300px, 1fr)); }
    .score { display: flex; gap: 18px; align-items: center; p { margin: 4px 0 0; font-size: 13px; } }
    .diff { margin: 0; padding: 10px 12px; background: var(--bg-2); border-radius: 8px; font: 12.5px/1.6 var(--mono); overflow: auto; .rm { color: var(--bad); } .add { color: var(--ok); } }
    .list { display: grid; gap: 12px; } .an.error { border-left: 3px solid var(--bad); } .an.warning { border-left: 3px solid var(--warn); } .an.info { border-left: 3px solid var(--accent); }
    .ids { gap: 6px; margin-top: 10px; }
  `],
})
export class DoctorComponent {
  private api = inject(ApiService);
  private toast = inject(ToastService);
  dv = inject(DataVersion);
  fmt = fmt;
  report = signal<DoctorReport | null>(null);
  loading = signal(true);
  error = signal<string | null>(null);
  sev = signal<'all' | 'error' | 'warning' | 'info'>('all');
  sevs = [{ k: 'all', l: 'Todas' }, { k: 'error', l: 'Errores' }, { k: 'warning', l: 'Advertencias' }, { k: 'info', l: 'Info' }] as const;
  expanded = signal(new Set<string>());
  confirmFix = signal<Anomaly | null>(null);
  fixing = signal(false);
  visible = computed(() => (this.report()?.anomalies ?? []).filter(a => this.sev() === 'all' || a.severity === this.sev()));

  constructor() {
    toObservable(this.dv.n).pipe(tap(() => { this.loading.set(true); this.error.set(null); }),
      switchMap(() => this.api.doctor().pipe(catchError(e => { this.error.set(asProblem(e).title); return of(null); }))), takeUntilDestroyed())
      .subscribe(r => { if (r) this.report.set(r); this.loading.set(false); });
  }
  count(k: string) { const a = this.report()?.anomalies ?? []; return k === 'all' ? a.length : a.filter(x => x.severity === k).length; }
  toggle(code: string) { this.expanded.update(s => { const n = new Set(s); n.has(code) ? n.delete(code) : n.add(code); return n; }); }
  fix(a: Anomaly) {
    this.fixing.set(true);
    this.api.doctorFix(a.code).subscribe({
      next: r => { this.fixing.set(false); this.confirmFix.set(null); this.toast.show('ok', `${r.fixedRecords} registros corregidos.`); this.dv.bump(); },
      error: e => { this.fixing.set(false); this.confirmFix.set(null); this.toast.show('error', asProblem(e).title); },
    });
  }
}
