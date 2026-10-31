import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, debounceTime, of, switchMap, tap } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { CompareResponse, DatasetSummary, DiffRow } from '../../core/models';
import { DataVersion, FilterStore, ToastService, fmt } from '../../core/state';
import { IconComponent } from '../../shared/icon.component';

type Confirm = { kind: 'reset' | 'delete'; ds: DatasetSummary } | { kind: 'factory' } | null;
const KINDS: { key: string; label: string }[] = [{ key: '', label: 'Todas' }, { key: 'changed', label: 'Cambiadas' }, { key: 'added', label: 'Solo en B' }, { key: 'removed', label: 'Solo en A' }];

@Component({
  selector: 'app-files', standalone: true, imports: [IconComponent, DatePipe, RouterLink], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <header class="head rise">
    <div><h1>Archivos</h1>
      <p class="muted">Cada archivo que cargas queda guardado con su original intacto y sus propios cambios. Puedes alternar entre ellos sin perder nada, volver un archivo a su estado original, compararlos entre sí o dejar el sistema como si nunca hubieras subido nada.</p></div>
    <a class="btn primary" routerLink="/importar"><app-icon name="upload" /> Cargar archivo</a>
  </header>

  @if (error(); as e) { <div class="banner bad" role="alert"><app-icon name="alert" /><span>{{ e }}</span></div> }

  <section class="list" aria-label="Archivos cargados">
    @for (d of datasets(); track d.id) {
      <article class="card file" [class.active]="d.isActive">
        <div class="top">
          <app-icon name="folder" [size]="20" />
          <div class="name"><strong>{{ d.name }}</strong>
            <span class="muted">{{ d.isSeed ? 'Original del sistema' : 'Cargado el ' + (d.addedAt | date: 'dd/MM/yyyy HH:mm') }} · {{ (d.bytes / 1048576).toFixed(2) }} MB · <span class="mono">{{ d.sha256.slice(0, 10) }}</span></span></div>
          @if (d.isActive) { <span class="badge ok">En uso</span> }
        </div>
        <div class="nums">
          <div><b>{{ fmt.num(d.records) }}</b><span class="muted">registros ahora</span></div>
          <div><b>{{ fmt.num(d.originalRecords) }}</b><span class="muted">en el original</span></div>
          <div><b [style.color]="d.hasEdits ? 'var(--warn)' : 'inherit'">{{ d.hasEdits ? d.changed + d.added + d.removed : 0 }}</b>
            <span class="muted">{{ d.hasEdits ? d.changed + ' editados · ' + d.added + ' nuevos · ' + d.removed + ' eliminados' : 'sin cambios' }}</span></div>
        </div>
        <div class="row actions">
          @if (!d.isActive) { <button type="button" class="btn primary sm" (click)="use(d)" [disabled]="busy()">Usar este archivo</button> }
          <button type="button" class="btn sm" (click)="compareWith(d)"><app-icon name="compare" /> {{ d.isActive ? '¿Qué cambié?' : 'Comparar con el activo' }}</button>
          @if (d.hasEdits) { <button type="button" class="btn sm" (click)="confirm.set({ kind: 'reset', ds: d })">Volver al original</button> }
          @if (!d.isActive && !d.isSeed) { <button type="button" class="btn sm danger" (click)="confirm.set({ kind: 'delete', ds: d })">Eliminar</button> }
        </div>
      </article>
    } @empty { <div class="card"><div class="skeleton" style="height:120px"></div></div> }
  </section>

  <section class="card cmp" id="comparar">
    <div class="card-head"><div><h2>Comparar</h2><p>Elige dos versiones. Se emparejan por ID (o por código de factura) y se muestran las diferencias registro por registro. Las monedas nunca se suman entre sí.</p></div></div>
    <div class="pickers">
      <label class="field">A (referencia)<select (change)="setA($any($event.target).value)">
        @for (o of options(); track o.value) { <option [value]="o.value" [selected]="o.value === a()">{{ o.label }}</option> }</select></label>
      <label class="field">B (a comparar)<select (change)="setB($any($event.target).value)">
        @for (o of options(); track o.value) { <option [value]="o.value" [selected]="o.value === b()">{{ o.label }}</option> }</select></label>
      <label class="field">Emparejar por<select (change)="setKey($any($event.target).value)">
        <option value="id" [selected]="key() === 'id'">ID del gasto</option><option value="factura" [selected]="key() === 'factura'">Código de factura</option></select></label>
    </div>
    @if (presets().length) {
      <div class="row" style="margin-top:10px"><span class="muted">Atajos:</span>
        @for (p of presets(); track p.label) { <button type="button" class="chip" (click)="applyPreset(p)">{{ p.label }}</button> }</div>
    }

    @if (cmpError(); as e) { <div class="banner warn" style="margin-top:14px"><app-icon name="alert" /><span>{{ e }}</span></div> }
    @if (result(); as r) {
      <p class="legend"><span><b>A</b> {{ r.labelA }}</span><span><b>B</b> {{ r.labelB }}</span></p>
      <div class="table-wrap"><table class="t sum" aria-label="Resumen comparativo">
        <thead><tr><th></th><th class="r">A</th><th class="r">B</th><th class="r">B − A</th></tr></thead>
        <tbody>
          <tr><td>Registros</td><td class="r">{{ fmt.num(r.a.records) }}</td><td class="r">{{ fmt.num(r.b.records) }}</td><td class="r" [class]="'r ' + sign(r.b.records - r.a.records)">{{ signed(r.b.records - r.a.records) }}</td></tr>
          <tr><td>Aprobados</td><td class="r">{{ fmt.pct(r.a.approvedPct) }}</td><td class="r">{{ fmt.pct(r.b.approvedPct) }}</td><td class="r">{{ signed(r.b.approvedPct - r.a.approvedPct, 1) }} pts</td></tr>
          @for (t of r.totals; track t.moneda) {
            <tr><td>Total {{ t.moneda }} <span class="muted">({{ fmt.num(t.countA) }} → {{ fmt.num(t.countB) }} gastos)</span></td>
              <td class="r">{{ fmt.money(t.totalA, t.moneda, true) }}</td><td class="r">{{ fmt.money(t.totalB, t.moneda, true) }}</td>
              <td class="r" [class]="'r ' + sign(t.totalB - t.totalA)">{{ signed(t.totalB - t.totalA, 0, t.moneda) }}@if (t.totalA) { <span class="muted"> ({{ signed(((t.totalB - t.totalA) / t.totalA) * 100, 1) }}%)</span> }</td></tr>
          }
        </tbody></table></div>

      <div class="counts">
        <button type="button" class="count" [class.on]="kind() === ''" (click)="setKind('')"><b>{{ fmt.num(r.counts.changed + r.counts.added + r.counts.removed) }}</b><span>diferencias</span></button>
        <button type="button" class="count warn" [class.on]="kind() === 'changed'" (click)="setKind('changed')"><b>{{ fmt.num(r.counts.changed) }}</b><span>cambiadas</span></button>
        <button type="button" class="count ok" [class.on]="kind() === 'added'" (click)="setKind('added')"><b>{{ fmt.num(r.counts.added) }}</b><span>solo en B</span></button>
        <button type="button" class="count bad" [class.on]="kind() === 'removed'" (click)="setKind('removed')"><b>{{ fmt.num(r.counts.removed) }}</b><span>solo en A</span></button>
        <div class="count static"><b>{{ fmt.num(r.counts.same) }}</b><span>idénticas</span></div>
      </div>
      @if (fieldList(r).length) { <p class="muted" style="margin:10px 0 0">Campos que más cambian: @for (f of fieldList(r); track f[0]) { <span class="chip static">{{ f[0] }} · {{ fmt.num(f[1]) }}</span> }</p> }
      @if (r.counts.ignoredDuplicates) { <p class="muted" style="margin:8px 0 0">{{ r.counts.ignoredDuplicates }} registros con clave repetida se ignoraron al emparejar (se usa el primero).</p> }

      <div class="row" style="margin:14px 0 8px"><input type="search" placeholder="Buscar por descripción, factura, departamento o clave" [value]="q()" (input)="setQ($any($event.target).value)" style="max-width:380px" /></div>
      <div class="table-wrap" [class.stale]="loading()"><table class="t" aria-label="Diferencias">
        <thead><tr><th>Tipo</th><th>{{ key() === 'factura' ? 'Factura' : 'ID' }}</th><th>Detalle</th></tr></thead>
        <tbody>
          @for (row of r.rows.items; track row.kind + row.key) {
            <tr>
              <td><span class="badge" [class.warn]="row.kind === 'changed'" [class.ok]="row.kind === 'added'" [class.bad]="row.kind === 'removed'">{{ kindLabel(row) }}</span></td>
              <td class="mono">{{ row.key }}</td>
              <td class="detail">
                @if (row.kind === 'changed') {
                  <div class="muted small">{{ row.b?.descripcion }}</div>
                  @for (c of row.changes; track c.field) { <div><span class="muted">{{ c.label }}:</span> <s>{{ c.before }}</s> → <strong>{{ c.after }}</strong></div> }
                } @else {
                  <div>{{ subj(row)?.descripcion }}</div><div class="muted small">{{ subj(row)?.departamento }} · {{ subj(row)?.categoria }} · {{ money(subj(row)) }} · {{ subj(row)?.aprobado ? 'Aprobado' : 'Pendiente' }}</div>
                }
              </td>
            </tr>
          } @empty { <tr><td colspan="3" class="muted" style="padding:20px">{{ r.counts.changed + r.counts.added + r.counts.removed === 0 ? 'Las dos versiones son idénticas.' : 'Ninguna diferencia coincide con el filtro.' }}</td></tr> }
        </tbody></table></div>
      <nav class="pager" aria-label="Paginación de diferencias">
        <span class="muted">{{ fmt.num(r.rows.total) }} resultados · página {{ r.rows.page }} de {{ r.rows.totalPages }}</span><span class="spacer"></span>
        <button class="btn sm" type="button" (click)="page.set(r.rows.page - 1)" [disabled]="r.rows.page <= 1">Anterior</button>
        <button class="btn sm" type="button" (click)="page.set(r.rows.page + 1)" [disabled]="r.rows.page >= r.rows.totalPages">Siguiente</button>
      </nav>
    } @else if (loading() && !cmpError()) { <div class="skeleton" style="height:140px;margin-top:16px"></div> }
  </section>

  <section class="card danger-zone">
    <div class="card-head"><div><h2>Dejar el sistema limpio</h2><p>Vuelve al archivo original del sistema como si nunca hubieras subido nada. Es irreversible.</p></div>
      <button type="button" class="btn danger" (click)="confirm.set({ kind: 'factory' })">Dejar el sistema limpio…</button></div>
  </section>

  @if (confirm(); as c) {
    <div class="backdrop" (mousedown)="$event.target === $event.currentTarget ? confirm.set(null) : null">
      <div class="modal" style="width:min(520px,100%)" role="alertdialog" aria-modal="true">
        @switch (c.kind) {
          @case ('reset') {
            <div class="m-head"><h2 style="font-size:18px">¿Volver «{{ asDs(c).name }}» a su original?</h2></div>
            <div class="m-body"><p style="margin:0">Se descartan sus cambios guardados: <strong>{{ asDs(c).changed }} editados, {{ asDs(c).added }} nuevos y {{ asDs(c).removed }} eliminados</strong>. Los {{ fmt.num(asDs(c).originalRecords) }} registros del archivo original vuelven tal cual se cargaron.</p>
              <p class="muted">Esta acción no se puede deshacer, pero quedará anotada en el Historial.</p></div>
            <div class="m-foot"><button class="btn" type="button" (click)="confirm.set(null)">Cancelar</button><button class="btn danger" type="button" (click)="doReset(asDs(c))" [disabled]="busy()">Sí, volver al original</button></div>
          }
          @case ('delete') {
            <div class="m-head"><h2 style="font-size:18px">¿Eliminar «{{ asDs(c).name }}» del sistema?</h2></div>
            <div class="m-body"><p style="margin:0">Se borra el archivo original guardado y todos sus cambios. No afecta al archivo que estás usando.</p><p class="muted">Podrás volver a subirlo cuando quieras.</p></div>
            <div class="m-foot"><button class="btn" type="button" (click)="confirm.set(null)">Cancelar</button><button class="btn danger" type="button" (click)="doDelete(asDs(c))" [disabled]="busy()">Sí, eliminar</button></div>
          }
          @case ('factory') {
            <div class="m-head"><h2 style="font-size:18px">Dejar el sistema limpio</h2></div>
            <div class="m-body"><p style="margin:0">Se va a borrar <strong>todo lo que se haya cargado o cambiado</strong>:</p>
              <ul class="muted"><li>{{ datasets().length - 1 }} archivo(s) cargado(s) y su original guardado</li><li>Todos los cambios guardados de cada archivo, incluida la papelera</li><li>El historial de cambios</li><li>Cargas pendientes y respaldos</li></ul>
              <p>Los datos vuelven al archivo original del sistema. Para confirmar escribe <strong>LIMPIAR</strong>:</p>
              <input [value]="typed()" (input)="typed.set($any($event.target).value)" placeholder="LIMPIAR" aria-label="Escribe LIMPIAR para confirmar" autocomplete="off" /></div>
            <div class="m-foot"><button class="btn" type="button" (click)="confirm.set(null)">Cancelar</button><button class="btn danger" type="button" (click)="doFactory()" [disabled]="busy() || typed().trim() !== 'LIMPIAR'">Borrar todo y empezar de cero</button></div>
          }
        }
      </div>
    </div>
  }`,
  styles: [`
    .head { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px; flex-wrap: wrap; margin-bottom: 18px; h1 { margin: 0 0 4px; } p { margin: 0; } }
    .list { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 380px), 1fr)); gap: 14px; margin-bottom: 18px; }
    .file { display: grid; gap: 14px; &.active { border-color: var(--accent); box-shadow: 0 0 0 1px var(--accent) inset, var(--shadow); } }
    .top { display: flex; align-items: flex-start; gap: 10px; app-icon { color: var(--accent-2); margin-top: 2px; } .name { display: grid; gap: 2px; flex: 1; min-width: 0; strong { overflow-wrap: anywhere; } span { font-size: 12px; } } }
    .nums { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; div { display: grid; gap: 2px; } b { font-size: 20px; font-variant-numeric: tabular-nums; } span { font-size: 12px; } }
    .actions { flex-wrap: wrap; gap: 8px; }
    .cmp { margin-top: 4px; } .cmp h2, .danger-zone h2 { margin: 0; font-size: 17px; }
    .pickers { display: grid; grid-template-columns: repeat(auto-fit, minmax(240px, 1fr)); gap: 12px; }
    .legend { display: grid; gap: 4px; margin: 16px 0 10px; font-size: 13px; b { display: inline-grid; place-items: center; width: 20px; height: 20px; margin-right: 8px; border-radius: 6px; background: var(--accent-bg); color: var(--accent-2); } }
    .sum { min-width: 0 !important; } .sum td { white-space: normal; } .sum td:first-child { white-space: normal; } .sum .r, .sum th.r { text-align: right; } .sum th { white-space: normal; }
    .up { color: var(--ok); } .down { color: var(--bad); }
    .counts { display: flex; flex-wrap: wrap; gap: 10px; margin-top: 16px; }
    .count { display: grid; gap: 1px; min-width: 112px; padding: 9px 14px; text-align: left; border-radius: 12px; border: 1px solid var(--border-2); background: var(--surface-2); b { font-size: 20px; font-variant-numeric: tabular-nums; } span { font-size: 12px; color: var(--muted); }
      &:not(.static) { cursor: pointer; &:hover { border-color: var(--accent); } } &.on { border-color: var(--accent); background: var(--accent-bg); } &.warn b { color: var(--warn); } &.ok b { color: var(--ok); } &.bad b { color: var(--bad); } }
    .chip.static { cursor: default; margin-right: 4px; }
    .detail { white-space: normal !important; min-width: 280px; s { color: var(--faint); } .small { font-size: 12px; } }
    .pager { display: flex; align-items: center; gap: 8px; margin-top: 12px; }
    .stale { opacity: .65; }
    .danger-zone { margin-top: 18px; border-color: var(--bad); .card-head { margin: 0; align-items: center; } }
  `],
})
export class FilesComponent {
  private api = inject(ApiService);
  private dv = inject(DataVersion);
  private filters = inject(FilterStore);
  private toast = inject(ToastService);
  fmt = fmt;
  readonly kinds = KINDS;

  datasets = signal<DatasetSummary[]>([]);
  error = signal<string | null>(null);
  busy = signal(false);
  confirm = signal<Confirm>(null);
  typed = signal('');

  // comparison state: a / b are "datasetId|variant"
  a = signal(''); b = signal(''); key = signal('id'); kind = signal(''); q = signal(''); page = signal(1);
  result = signal<CompareResponse | null>(null);
  cmpError = signal<string | null>(null);
  loading = signal(false);

  options = computed(() => this.datasets().flatMap(d => {
    const clash = this.datasets().filter(x => x.name === d.name).length > 1;
    const base = `${d.name}${clash ? ' (' + new Date(d.addedAt).toLocaleString('es-CR', { dateStyle: 'short', timeStyle: 'short' }) + ')' : ''}${d.isActive ? ' · en uso' : ''}`;
    return [{ value: `${d.id}|actual`, label: `${base} — con cambios` }, { value: `${d.id}|original`, label: `${base} — original` }];
  }));

  presets = computed(() => {
    const ds = this.datasets(), active = ds.find(d => d.isActive);
    if (!active) return [];
    const out: { label: string; a: string; b: string }[] = [{ label: 'Mis cambios vs el original', a: `${active.id}|original`, b: `${active.id}|actual` }];
    const prev = ds.find(d => d.id !== active.id && d.name === active.name);
    if (prev) out.push({ label: 'Versión anterior con el mismo nombre', a: `${prev.id}|actual`, b: `${active.id}|actual` });
    const other = ds.find(d => d.id !== active.id);
    if (other) out.push({ label: 'Archivo A vs archivo B', a: `${other.id}|actual`, b: `${active.id}|actual` });
    return out;
  });

  constructor() {
    toObservable(this.dv.n).pipe(
      switchMap(() => this.api.datasets().pipe(catchError(err => { this.error.set(asProblem(err).title); return of(null); }))),
      takeUntilDestroyed(),
    ).subscribe(list => {
      if (!list) return;
      this.error.set(null); this.datasets.set(list);
      const valid = new Set(list.flatMap(d => [`${d.id}|actual`, `${d.id}|original`]));
      if (!valid.has(this.a()) || !valid.has(this.b())) {
        const p = this.presets();
        const first = p.find(x => x.label.startsWith('Versión')) ?? p.find(x => x.label.startsWith('Archivo')) ?? p[0];
        if (first) { this.a.set(first.a); this.b.set(first.b); }
      }
    });

    const trigger = computed(() => ({ a: this.a(), b: this.b(), key: this.key(), kind: this.kind(), q: this.q(), page: this.page(), v: this.dv.n() }));
    toObservable(trigger).pipe(
      debounceTime(200),
      tap(() => { this.loading.set(true); this.cmpError.set(null); }),
      switchMap(t => {
        if (!t.a || !t.b) return of(null);
        const [ai, av] = t.a.split('|'), [bi, bv] = t.b.split('|');
        return this.api.compare({ a: ai, aVariant: av, b: bi, bVariant: bv, key: t.key, kind: t.kind, q: t.q, page: t.page }).pipe(
          catchError(err => { this.cmpError.set(asProblem(err).title); return of(null); }));
      }),
      takeUntilDestroyed(),
    ).subscribe(r => { this.result.set(r); this.loading.set(false); });
  }

  subj(r: DiffRow) { return r.b ?? r.a; }
  money(e: { monto: number; moneda: string } | null | undefined) { return e ? fmt.money(e.monto, e.moneda) : ''; }
  asDs(c: Confirm) { return (c as { ds: DatasetSummary }).ds; }
  setA(v: string) { this.a.set(v); this.page.set(1); }
  setB(v: string) { this.b.set(v); this.page.set(1); }
  setKey(v: string) { this.key.set(v); this.page.set(1); }
  setKind(v: string) { this.kind.set(v); this.page.set(1); }
  setQ(v: string) { this.q.set(v); this.page.set(1); }
  applyPreset(p: { a: string; b: string }) { this.a.set(p.a); this.b.set(p.b); this.page.set(1); this.kind.set(''); }
  fieldList(r: CompareResponse) { return Object.entries(r.counts.fieldChanges).slice(0, 6); }
  kindLabel(r: DiffRow) { return r.kind === 'changed' ? 'Cambiado' : r.kind === 'added' ? 'Solo en B' : 'Solo en A'; }
  sign(n: number) { return n > 0 ? 'up' : n < 0 ? 'down' : ''; }
  signed(n: number, digits = 0, cur?: string) {
    const abs = Math.abs(n), body = cur ? fmt.money(abs, cur, true) : abs.toLocaleString('es-CR', { maximumFractionDigits: digits, minimumFractionDigits: digits });
    return `${n > 0 ? '+' : n < 0 ? '−' : ''}${body}`;
  }

  compareWith(d: DatasetSummary) {
    const active = this.datasets().find(x => x.isActive);
    if (d.isActive || !active) this.applyPreset({ a: `${d.id}|original`, b: `${d.id}|actual` });
    else this.applyPreset({ a: `${d.id}|actual`, b: `${active.id}|actual` });
    document.getElementById('comparar')?.scrollIntoView({ behavior: 'smooth' });
  }

  private done(msg: string) { this.busy.set(false); this.confirm.set(null); this.typed.set(''); this.filters.reset(); this.dv.bump(); this.toast.show('ok', msg, undefined, 7000); }
  private fail(err: unknown) { this.busy.set(false); this.toast.show('error', asProblem(err).title); }

  use(d: DatasetSummary) {
    const from = this.datasets().find(x => x.isActive);
    this.busy.set(true);
    this.api.activateDataset(d.id, false).subscribe({
      next: () => this.done(`Ahora usas «${d.name}»${d.hasEdits ? ' con sus cambios' : ''}.${from ? ` Lo que hiciste en «${from.name}» quedó guardado.` : ''}`),
      error: e => this.fail(e),
    });
  }
  doReset(d: DatasetSummary) { this.busy.set(true); this.api.activateDataset(d.id, true).subscribe({ next: () => this.done(`«${d.name}» volvió a su estado original.`), error: e => this.fail(e) }); }
  doDelete(d: DatasetSummary) { this.busy.set(true); this.api.deleteDataset(d.id).subscribe({ next: () => this.done(`«${d.name}» se eliminó del sistema.`), error: e => this.fail(e) }); }
  doFactory() { this.busy.set(true); this.api.factoryReset(this.typed().trim()).subscribe({ next: () => this.done('Sistema limpio: se restauró el archivo original y se borró todo lo demás.'), error: e => this.fail(e) }); }
}
