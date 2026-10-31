import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable, toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { DatePipe } from '@angular/common';
import { catchError, debounceTime, of, switchMap, tap } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { BulkFilterDto, BulkRequest, BulkResult, Expense, ExpenseState, Facets, Filters, Paged } from '../../core/models';
import { DataVersion, FilterStore, ToastService, fmt } from '../../core/state';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { ExportMenuComponent } from '../../shared/export-menu.component';
import { IconComponent } from '../../shared/icon.component';
import { BulkDialogComponent } from './bulk-dialog.component';
import { ExpenseFormComponent } from './expense-form.component';

@Component({
  selector: 'app-expenses', standalone: true, imports: [FilterBarComponent, IconComponent, ExpenseFormComponent, BulkDialogComponent, ExportMenuComponent, DatePipe], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <header class="head rise">
    <div><h1>Gestión de gastos</h1>
      <p class="muted">@if (data(); as d) { {{ fmt.num(d.total) }} resultados · página {{ d.page }} de {{ d.totalPages }} } @else { Cargando… }</p></div>
    <div class="row">
      <div class="seg" role="group" aria-label="Vista">
        <button type="button" [class.on]="!trash()" (click)="trash.set(false)">Activos</button>
        <button type="button" [class.on]="trash()" (click)="trash.set(true)">Papelera @if ((facets()?.deletedCount ?? 0) > 0) { <span class="count">{{ facets()!.deletedCount }}</span> }</button>
      </div>
      @if (!trash()) { <app-export-menu /> }
      @if (!trash()) { <button type="button" class="btn primary" (click)="openForm(null)"><app-icon name="plus" /> Nuevo gasto</button> }
    </div>
  </header>
  @if (trash()) {
    <div class="banner info" style="margin-bottom:12px"><app-icon name="info" /><span>La papelera muestra los gastos eliminados. No cuentan en las métricas y puedes restaurarlos cuando quieras. Los cambios y eliminaciones quedan registrados en el Historial.</span></div>
  }
  <app-filter-bar />

  @if (error(); as e) { <div class="banner bad" role="alert" style="margin-top:16px"><app-icon name="alert" /><span>{{ e }}</span><span class="spacer"></span><button class="btn sm" type="button" (click)="dv.bump()">Reintentar</button></div> }

  @if (!trash() && selBanner(); as b) {
    <div class="banner info sel-banner" role="status" style="margin-top:16px"><app-icon name="info" />
      <span>@if (b.kind === 'all') { Seleccionaste <strong>los {{ fmt.num(b.total) }} resultados</strong> de este filtro (los que no estén pendientes se omitirán). }
        @else { Seleccionaste los <strong>{{ b.onPage }}</strong> pendientes de esta página. }</span>
      <span class="spacer"></span>
      @if (b.kind === 'page' && b.canAll) { <button type="button" class="btn sm" (click)="selectAllFiltered()">Seleccionar los {{ fmt.num(b.total) }} resultados</button> }
      @if (b.kind === 'page' && b.tooMany) { <span class="muted small">Máximo {{ fmt.num(maxItems()) }} por operación: acota el filtro para seleccionar todos.</span> }
      <button type="button" class="btn sm ghost" (click)="clearSel()">Limpiar</button></div>
  }

  <div class="table-wrap rise" style="margin-top:16px" [class.stale]="loading()">
    <table class="t pin-last" aria-label="Gastos">
      <thead><tr>
        @if (!trash()) {
          <th class="chk"><input type="checkbox" [checked]="pageAllSelected()" [indeterminate]="pageSomeSelected()" [disabled]="selectablePage().length === 0 || loading()"
            (change)="togglePage()" aria-label="Seleccionar los pendientes de esta página" /></th>
        }
        @for (c of cols; track c.key) {
          <th [class.r]="c.right" [attr.aria-sort]="sort() === c.key ? (desc() ? 'descending' : 'ascending') : 'none'">
            <button type="button" (click)="setSort(c.key)">{{ c.label }} @if (sort() === c.key) { <app-icon [name]="desc() ? 'down' : 'up'" [size]="12" /> }</button></th>
        }
        <th></th>
      </tr></thead>
      <tbody>
        @if (loading() && !data()) { @for (i of [1,2,3,4,5,6,7,8]; track i) { <tr><td colspan="11"><div class="skeleton" style="height:16px"></div></td></tr> } }
        @for (e of data()?.items ?? []; track e.id) {
          <tr (click)="trash() ? null : openDetail(e)" tabindex="0" (keydown.enter)="trash() ? null : openDetail(e)" [class.sel]="isSel(e)">
            @if (!trash()) {
              <td class="chk" (click)="$event.stopPropagation()">
                <input type="checkbox" [checked]="isSel(e)" [disabled]="e.estado !== 'pendiente' || allMode()" (change)="toggle(e)"
                  [attr.aria-label]="'Seleccionar gasto ' + e.id" [title]="e.estado === 'pendiente' ? '' : 'Solo se pueden seleccionar gastos pendientes'" />
              </td>
            }
            <td class="mono muted">#{{ e.id }}</td><td>{{ e.fecha }}</td><td class="desc" [title]="e.descripcion">{{ e.descripcion }}</td>
            <td>{{ e.departamento }}</td><td>{{ e.categoria }}</td><td>{{ e.metodoPago }}</td><td class="mono">{{ e.codigoFactura }}</td>
            <td class="r"><strong>{{ fmt.money(e.monto, e.moneda) }}</strong></td>
            <td>@switch (e.estado) {
              @case ('aprobado') { <span class="badge ok">Aprobado</span> }
              @case ('rechazado') { <span class="badge bad" [title]="e.motivoRechazo ?? ''">Rechazado</span> }
              @default { <span class="badge warn">Pendiente</span> } }</td>
            <td style="white-space:nowrap" (click)="$event.stopPropagation()">
              @if (trash()) {
                <button type="button" class="btn sm" (click)="restoreOne(e)" [attr.aria-label]="'Restaurar gasto ' + e.id"><app-icon name="undo" /> Restaurar</button>
              } @else {
                <button type="button" class="btn ghost icon danger" (click)="confirmDelete.set(e)" [attr.aria-label]="'Eliminar gasto ' + e.id"><app-icon name="trash" /></button>
              }
            </td>
          </tr>
        }
      </tbody>
    </table>
    @if (!loading() && data() && data()!.items.length === 0) {
      <div class="empty"><app-icon name="inbox" [size]="40" /><strong>{{ trash() ? 'La papelera está vacía' : 'No hay gastos con estos filtros' }}</strong><span>{{ trash() ? 'Los gastos que elimines aparecerán aquí para que puedas restaurarlos.' : 'Prueba con otro rango de fechas o restablece los filtros.' }}</span></div>
    }
  </div>

  @if (data(); as d) {
    <nav class="pager" aria-label="Paginación">
      <span class="muted">Mostrando {{ d.items.length ? (d.page - 1) * d.pageSize + 1 : 0 }}–{{ (d.page - 1) * d.pageSize + d.items.length }} de {{ fmt.num(d.total) }}</span>
      <span class="spacer"></span>
      <label class="row muted">Filas <select [value]="pageSize()" (change)="setPageSize($event)" style="width:76px"><option>10</option><option>25</option><option>50</option><option>100</option></select></label>
      <button class="btn sm" type="button" (click)="goto(1)" [disabled]="d.page === 1">«</button>
      <button class="btn sm" type="button" (click)="goto(d.page - 1)" [disabled]="d.page === 1">Anterior</button>
      <span class="muted">{{ d.page }} / {{ d.totalPages }}</span>
      <button class="btn sm" type="button" (click)="goto(d.page + 1)" [disabled]="d.page >= d.totalPages">Siguiente</button>
      <button class="btn sm" type="button" (click)="goto(d.totalPages)" [disabled]="d.page >= d.totalPages">»</button>
    </nav>
  }

  @if (detail(); as e) {
    <div class="backdrop" style="place-items:stretch end;padding:0" (mousedown)="$event.target === $event.currentTarget ? detail.set(null) : null"></div>
    <aside class="drawer" role="dialog" aria-modal="true" aria-label="Detalle del gasto">
      <div class="row"><h2 style="font-size:18px">Gasto #{{ e.id }}</h2><span class="spacer"></span><button class="btn ghost icon" type="button" (click)="detail.set(null)" aria-label="Cerrar"><app-icon name="x" /></button></div>
      <div class="d-amt">{{ fmt.money(e.monto, e.moneda) }}</div>
      @switch (e.estado) {
        @case ('aprobado') { <span class="badge ok">Aprobado</span> }
        @case ('rechazado') { <span class="badge bad">Rechazado</span> }
        @default { <span class="badge warn">Pendiente de aprobación</span> } }
      @if (e.estado === 'rechazado') { <div class="banner bad" style="margin-top:12px"><app-icon name="alert" /><span><strong>Motivo:</strong> {{ e.motivoRechazo || 'Sin motivo registrado.' }}</span></div> }
      <dl class="kv" style="margin-top:20px">
        <dt>Descripción</dt><dd>{{ e.descripcion }}</dd><dt>Departamento</dt><dd>{{ e.departamento }}</dd><dt>Categoría</dt><dd>{{ e.categoria }}</dd>
        <dt>Método de pago</dt><dd>{{ e.metodoPago }}</dd><dt>Fecha</dt><dd>{{ e.fecha }}</dd><dt>Factura</dt><dd class="mono">{{ e.codigoFactura }}</dd>
        <dt>Versión</dt><dd class="mono">{{ e.version }}</dd><dt>Actualizado</dt><dd>{{ e.actualizadoEn ? (e.actualizadoEn | date: 'medium') : 'Sin cambios desde la carga' }}</dd>
      </dl>
      @if (stateAction(); as a) {
        <div class="act-panel" role="group" aria-label="Confirmar cambio de estado">
          @switch (a) {
            @case ('aprobado') { <div class="banner warn"><app-icon name="alert" /><span>Al aprobar, el gasto queda <strong>bloqueado</strong>: no se podrá editar hasta volverlo a pendiente. Cuenta como aprobado en las métricas.</span></div> }
            @case ('rechazado') {
              <label class="field">Motivo del rechazo (obligatorio)
                <textarea rows="3" maxlength="500" [value]="stateReason()" (input)="stateReason.set(textVal($event))" placeholder="Ej.: factura ilegible, gasto duplicado…" style="height:auto;padding:8px 10px"></textarea>
                <span class="muted small">Se guarda en el gasto y en el Historial. {{ stateReason().trim().length }}/500</span></label> }
            @default { <div class="banner info"><app-icon name="info" /><span>El gasto vuelve a <strong>pendiente</strong>: dejará de contar como {{ e.estado }} y podrá editarse de nuevo.</span></div> }
          }
          @if (stateError()) { <div class="banner bad" role="alert"><app-icon name="alert" /><span>{{ stateError() }}</span></div> }
          <div class="row" style="justify-content:flex-end">
            <button class="btn" type="button" (click)="cancelState()" [disabled]="stateBusy()">Cancelar</button>
            <button class="btn" [class.primary]="a !== 'rechazado'" [class.danger]="a === 'rechazado'" type="button" (click)="confirmState(e)" [disabled]="stateBusy() || (a === 'rechazado' && stateReason().trim().length < 3)">
              {{ stateBusy() ? 'Guardando…' : a === 'aprobado' ? 'Confirmar aprobación' : a === 'rechazado' ? 'Confirmar rechazo' : 'Volver a pendiente' }}</button>
          </div>
        </div>
      } @else {
        <div class="row act" style="margin-top:24px">
          @if (e.estado === 'pendiente') {
            <button class="btn primary" type="button" (click)="startState('aprobado')"><app-icon name="check" /> Aprobar</button>
            <button class="btn danger" type="button" (click)="startState('rechazado')"><app-icon name="x" /> Rechazar</button>
          } @else {
            <button class="btn" type="button" (click)="startState('pendiente')"><app-icon name="undo" /> Volver a pendiente</button>
          }
        </div>
        <div class="row act" style="margin-top:10px">
          <button class="btn" type="button" (click)="openForm(e)" [disabled]="e.estado !== 'pendiente'"><app-icon name="edit" /> Editar</button>
          <button class="btn danger" type="button" (click)="confirmDelete.set(e)"><app-icon name="trash" /> Eliminar</button>
        </div>
        @if (e.estado !== 'pendiente') { <p class="muted small" style="margin:10px 0 0">Solo los gastos <strong>pendientes</strong> se pueden editar. Vuelve este gasto a pendiente para modificarlo.</p> }
      }
    </aside>
  }

  @if (!trash() && selCount() > 0) {
    <div class="fab" role="region" aria-label="Acciones sobre la selección">
      <span class="fab-n"><strong>{{ fmt.num(selCount()) }}</strong> {{ selCount() === 1 ? 'seleccionado' : 'seleccionados' }}@if (allMode()) { <span class="muted"> · todos los resultados</span> }</span>
      <button type="button" class="btn primary sm" (click)="startBulk('approve')"><app-icon name="check" /> Aprobar seleccionados</button>
      <button type="button" class="btn danger sm" (click)="startBulk('reject')"><app-icon name="x" /> Rechazar seleccionados</button>
      <button type="button" class="btn ghost sm" (click)="clearSel()">Limpiar selección</button>
    </div>
  }
  @if (bulk(); as b) { <app-bulk-dialog [op]="b.op" [request]="b.req" (closed)="bulk.set(null)" (done)="onBulkDone($event)" (refresh)="bulkRefresh()" /> }

  @if (formOpen()) { <app-expense-form [expense]="editing()" [facets]="facets()" (saved)="onSaved()" (cancelled)="formOpen.set(false)" /> }

  @if (confirmDelete(); as e) {
    <div class="backdrop" (mousedown)="$event.target === $event.currentTarget ? confirmDelete.set(null) : null">
      <div class="modal" style="width:min(460px,100%)" role="alertdialog" aria-modal="true" aria-labelledby="del-t">
        <div class="m-head"><h2 id="del-t" style="font-size:18px">¿Eliminar el gasto #{{ e.id }}?</h2></div>
        <div class="m-body"><p class="muted" style="margin:0">{{ e.descripcion }}<br><strong style="color:var(--text)">{{ fmt.money(e.monto, e.moneda) }}</strong> · {{ e.fecha }}</p>
          @if (e.aprobado) { <div class="banner warn" style="margin:12px 0"><app-icon name="alert" /><span>Este gasto está <strong>aprobado</strong>. Al eliminarlo dejará de contar en los totales aprobados.</span></div> }
          <p class="muted">Pasa a la <strong>Papelera</strong>: sale de las métricas pero queda en el archivo y en el Historial, y puedes restaurarlo cuando quieras.</p>
          @if (deleteError()) { <div class="banner bad" role="alert"><app-icon name="alert" /><span>{{ deleteError() }}</span></div> }</div>
        <div class="m-foot"><button class="btn" type="button" (click)="confirmDelete.set(null)">Cancelar</button><button class="btn danger" type="button" (click)="doDelete(e)" [disabled]="deleting()">{{ deleting() ? 'Eliminando…' : 'Sí, eliminar' }}</button></div>
      </div>
    </div>
  }`,
  styles: [`
    .head { display: flex; align-items: flex-end; justify-content: space-between; gap: 16px; flex-wrap: wrap; margin-bottom: 18px; p { margin: 4px 0 0; } }
    .count { display: inline-grid; place-items: center; min-width: 18px; height: 18px; padding: 0 5px; margin-left: 4px; border-radius: 99px; background: var(--accent); color: #fff; font-size: 11px; font-weight: 700; }
    .stale { opacity: .65; transition: opacity .15s; } td { white-space: nowrap; }
    .chk { width: 40px; padding-right: 0; } .small { font-size: 12.5px; } tr.sel td, tr.sel td:last-child { background: color-mix(in srgb, var(--accent) 9%, var(--surface)); }
    .sel-banner { align-items: center; flex-wrap: wrap; }
    .fab { position: fixed; z-index: 40; left: 50%; bottom: 20px; translate: -50% 0; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; justify-content: center;
      width: max-content; max-width: calc(100vw - 24px); padding: 10px 14px; background: var(--surface); border: 1px solid var(--border-2); border-radius: 14px; box-shadow: 0 18px 50px -12px rgba(0,0,0,.55); animation: rise .2s both; }
    .fab-n { margin-right: 6px; }
    @media (max-width: 640px) { .fab { bottom: 12px; } .fab .btn { flex: 1 1 auto; justify-content: center; } .fab-n { flex: 1 0 100%; text-align: center; margin: 0; } }
    .pager { display: flex; align-items: center; gap: 8px; margin-top: 14px; flex-wrap: wrap; }
    .act { flex-wrap: wrap; } .act-panel { display: grid; gap: 12px; margin-top: 22px; padding-top: 16px; border-top: 1px solid var(--border); }
    .d-amt { font-size: 30px; font-weight: 700; letter-spacing: -.02em; margin: 14px 0 8px; font-variant-numeric: tabular-nums; }
  `],
})
export class ExpensesComponent {
  private api = inject(ApiService);
  private store = inject(FilterStore);
  private toast = inject(ToastService);
  dv = inject(DataVersion);
  fmt = fmt;

  cols = [
    { key: 'id', label: 'ID', right: false }, { key: 'fecha', label: 'Fecha', right: false }, { key: 'descripcion', label: 'Descripción', right: false },
    { key: 'departamento', label: 'Departamento', right: false }, { key: 'categoria', label: 'Categoría', right: false }, { key: 'metodoPago', label: 'Pago', right: false }, { key: 'codigoFactura', label: 'Factura', right: false },
    { key: 'monto', label: 'Monto', right: true }, { key: 'aprobado', label: 'Estado', right: false },
  ];
  sort = signal('fecha'); desc = signal(true); pageSize = signal(25);
  private pageState = signal({ key: '', page: 1 });
  private filterKey = computed(() => JSON.stringify(this.store.filters()) + this.sort() + this.desc() + this.pageSize() + this.trash());
  page = computed(() => (this.pageState().key === this.filterKey() ? this.pageState().page : 1));

  data = signal<Paged<Expense> | null>(null);
  loading = signal(true);
  error = signal<string | null>(null);
  trash = signal(false);
  detail = signal<Expense | null>(null);
  openDetail(e: Expense) { this.stateAction.set(null); this.stateError.set(null); this.detail.set(e); }
  formOpen = signal(false);
  editing = signal<Expense | null>(null);
  confirmDelete = signal<Expense | null>(null);
  deleting = signal(false);
  deleteError = signal<string | null>(null);
  // ---- bulk selection. It belongs to (active file, filters, view): if any of them changes it is treated as empty, so nothing invisible stays selected.
  private selScope = computed(() => JSON.stringify(this.store.filters()) + '|' + this.trash() + '|' + this.datasetId());
  private selState = signal<{ scope: string; items: ReadonlyMap<number, number>; all: number | null }>({ scope: '', items: new Map(), all: null });
  private cur = computed(() => (this.selState().scope === this.selScope() ? this.selState() : { scope: '', items: new Map<number, number>(), all: null as number | null }));
  datasetId = signal('');
  maxItems = signal(5000);
  allMode = computed(() => this.cur().all !== null);
  selCount = computed(() => this.cur().all ?? this.cur().items.size);
  selectablePage = computed(() => (this.data()?.items ?? []).filter(e => e.estado === 'pendiente'));
  pageAllSelected = computed(() => this.selectablePage().length > 0 && this.selectablePage().every(e => this.isSel(e)));
  pageSomeSelected = computed(() => !this.pageAllSelected() && this.selectablePage().some(e => this.isSel(e)));
  selBanner = computed(() => {
    const d = this.data(); if (!d) return null;
    if (this.allMode()) return { kind: 'all' as const, total: d.total, onPage: 0, canAll: false, tooMany: false };
    if (this.pageAllSelected() && this.selectablePage().length > 0)
      return { kind: 'page' as const, total: d.total, onPage: this.selectablePage().length, canAll: d.total > d.items.length && d.total <= this.maxItems(), tooMany: d.total > this.maxItems() };
    return null;
  });
  bulk = signal<{ op: 'approve' | 'reject'; req: BulkRequest } | null>(null);

  isSel(e: Expense) { return this.allMode() || this.cur().items.has(e.id); }
  private put(items: Map<number, number>, all: number | null = null) { this.selState.set({ scope: this.selScope(), items, all }); }
  toggle(e: Expense) {
    const m = new Map(this.cur().items);
    if (m.has(e.id)) m.delete(e.id); else m.set(e.id, e.version);
    this.put(m);
  }
  togglePage() {
    const m = new Map(this.cur().items);
    if (this.pageAllSelected()) for (const e of this.selectablePage()) m.delete(e.id);
    else for (const e of this.selectablePage()) m.set(e.id, e.version);
    this.put(m);
  }
  selectAllFiltered() { const d = this.data(); if (d) this.put(new Map(), d.total); }
  clearSel() { this.put(new Map()); }

  startBulk(op: 'approve' | 'reject') {
    const ds = this.datasetId(); if (!ds || this.selCount() === 0) return;
    const c = this.cur();
    const req: BulkRequest = c.all !== null
      ? { datasetId: ds, filter: this.filterDto(this.store.filters()), expectedCount: c.all }
      : { datasetId: ds, items: [...c.items].map(([id, version]) => ({ id, version })) };
    this.bulk.set({ op, req });
  }
  private filterDto(f: Filters): BulkFilterDto {
    return { from: f.from || null, to: f.to || null, departamentos: f.departamentos, categorias: f.categorias, monedas: f.monedas, metodosPago: f.metodosPago,
      aprobado: null, search: f.q.trim() || null, estado: f.estado || null, ids: f.ids };
  }
  onBulkDone(r: BulkResult) {
    this.clearSel(); this.dv.bump();
    this.toast.show('ok', r.applied > 0 ? `${r.applied} gastos ${r.operation === 'approve' ? 'aprobados' : 'rechazados'}.` : 'No se modificó ningún gasto.');
  }
  bulkRefresh() { this.bulk.set(null); this.clearSel(); this.dv.bump(); }

  // ---- single-expense state change from the detail panel
  stateAction = signal<ExpenseState | null>(null);
  stateReason = signal(''); stateBusy = signal(false); stateError = signal<string | null>(null);
  textVal(ev: Event) { return (ev.target as HTMLTextAreaElement).value; }
  startState(s: ExpenseState) { this.stateAction.set(s); this.stateReason.set(''); this.stateError.set(null); }
  cancelState() { this.stateAction.set(null); this.stateError.set(null); }
  confirmState(e: Expense) {
    const target = this.stateAction(); if (!target) return;
    this.stateBusy.set(true); this.stateError.set(null);
    this.api.setState(e.id, e.version, target, target === 'rechazado' ? this.stateReason().trim() : undefined).subscribe({
      next: upd => {
        this.stateBusy.set(false); this.stateAction.set(null); this.detail.set(upd); this.dv.bump();
        this.toast.show('ok', target === 'aprobado' ? `Gasto #${e.id} aprobado.` : target === 'rechazado' ? `Gasto #${e.id} rechazado.` : `Gasto #${e.id} volvió a pendiente.`);
      },
      error: err => {
        this.stateBusy.set(false); const p = asProblem(err);
        this.stateError.set(p.status === 409 ? 'El gasto cambió mientras lo revisabas. Cierra este panel y vuelve a abrirlo.' : (p.errors ? Object.values(p.errors).flat().join(' ') : p.title));
      },
    });
  }

  facets = toSignal(toObservable(this.dv.n).pipe(switchMap(() => this.api.facets().pipe(catchError(() => of(null as Facets | null))))), { initialValue: null as Facets | null });

  constructor() {
    // Links from other pages (insight cards) arrive with an exact set of filters: start from a clean slate so the result is what the link promised.
    const qp = inject(ActivatedRoute).snapshot.queryParamMap;
    const link: Partial<Filters> = {};
    if (qp.get('q')) link.q = qp.get('q')!;
    if (qp.getAll('ids').length) link.ids = qp.getAll('ids').map(Number).filter(Number.isInteger);
    if (qp.get('estado') === 'pendiente' || qp.get('estado') === 'aprobado' || qp.get('estado') === 'rechazado') link.estado = qp.get('estado') as Filters['estado'];
    if (qp.getAll('moneda').length) link.monedas = qp.getAll('moneda');
    if (qp.get('hasta')) link.to = qp.get('hasta')!;
    if (Object.keys(link).length) { this.store.reset(); this.store.patch(link); }
    // which file is live + the per-operation cap; re-read whenever data reloads (a file switch bumps the version)
    toObservable(this.dv.n).pipe(switchMap(() => this.api.bulkLimits().pipe(catchError(() => of(null)))), takeUntilDestroyed())
      .subscribe(l => { if (l) { this.datasetId.set(l.datasetId); this.maxItems.set(l.maxItems); } });
    const trigger = computed(() => ({ f: this.store.filters(), s: this.sort(), d: this.desc(), p: this.page(), n: this.pageSize(), t: this.trash(), v: this.dv.n() }));
    toObservable(trigger).pipe(
      debounceTime(200),
      tap(() => { this.loading.set(true); this.error.set(null); }),
      switchMap(t => this.api.list(t.f, t.s, t.d, t.p, t.n, t.t).pipe(catchError(err => { this.error.set(asProblem(err).title); return of(null); }))),
      takeUntilDestroyed(),
    ).subscribe(r => { if (r) this.data.set(r); this.loading.set(false); });
  }

  setSort(key: string) { if (this.sort() === key) this.desc.update(d => !d); else { this.sort.set(key); this.desc.set(key === 'fecha' || key === 'monto'); } }
  setPageSize(e: Event) { this.pageSize.set(Number((e.target as HTMLSelectElement).value)); }
  goto(p: number) { this.pageState.set({ key: this.filterKey(), page: Math.max(1, p) }); }
  openForm(e: Expense | null) { this.editing.set(e); this.detail.set(null); this.formOpen.set(true); }
  restoreOne(e: Expense) {
    this.api.restore(e.id).subscribe({ next: () => { this.dv.bump(); this.toast.show('ok', `Gasto #${e.id} restaurado.`); }, error: er => this.toast.show('error', asProblem(er).title) });
  }
  onSaved() { this.formOpen.set(false); this.dv.bump(); }

  doDelete(e: Expense) {
    this.deleting.set(true); this.deleteError.set(null);
    this.api.remove(e.id, e.version).subscribe({
      next: () => {
        this.deleting.set(false); this.confirmDelete.set(null); this.detail.set(null); this.dv.bump();
        this.toast.show('ok', `Gasto #${e.id} eliminado. Quedó en la Papelera.`, { label: 'Deshacer', run: () => this.api.restore(e.id).subscribe({ next: () => { this.dv.bump(); this.toast.show('ok', `Gasto #${e.id} restaurado.`); }, error: er => this.toast.show('error', asProblem(er).title) }) }, 9000);
      },
      error: err => { this.deleting.set(false); const p = asProblem(err); this.deleteError.set(p.status === 409 ? 'El gasto cambió mientras lo revisabas. Cierra y vuelve a abrirlo.' : p.title); },
    });
  }
}
