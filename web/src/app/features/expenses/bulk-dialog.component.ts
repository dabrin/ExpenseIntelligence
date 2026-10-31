import { ChangeDetectionStrategy, Component, EventEmitter, Input, OnInit, Output, computed, inject, signal } from '@angular/core';
import { ApiService, asProblem } from '../../core/api.service';
import { ApiProblem, BulkPreview, BulkRequest, BulkResult } from '../../core/models';
import { fmt } from '../../core/state';
import { IconComponent } from '../../shared/icon.component';

const REASONS: Record<string, string> = { 'already-approved': 'ya aprobados', 'already-rejected': 'ya rechazados', deleted: 'eliminados' };

/** Review → confirm → processing → result for a bulk approve/reject. All numbers come from the server (preview), never recomputed here. */
@Component({
  selector: 'app-bulk-dialog', standalone: true, imports: [IconComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <div class="backdrop" (mousedown)="$event.target === $event.currentTarget && phase() !== 'running' ? close() : null">
    <div class="modal" style="width:min(560px,100%)" role="dialog" aria-modal="true" aria-labelledby="bk-t">
      <div class="m-head"><h2 id="bk-t" style="font-size:18px">{{ title() }}</h2></div>
      <div class="m-body">
        @switch (phase()) {
          @case ('loading') { <div class="skeleton" style="height:90px"></div> }
          @case ('review') {
            @if (preview(); as p) {
              <p style="margin:0 0 12px">
                @if (op === 'approve') { Se aprobarán <strong>{{ fmt.num(p.eligible) }}</strong> gastos pendientes. }
                @else { Se rechazarán <strong>{{ fmt.num(p.eligible) }}</strong> gastos pendientes. }
              </p>
              @if (p.eligible > 0) {
                <table class="t sum" aria-label="Totales por moneda">
                  <thead><tr><th>Moneda</th><th class="r">Gastos</th><th class="r">Total</th></tr></thead>
                  <tbody>@for (t of p.eligibleTotals; track t.moneda) { <tr><td>{{ t.moneda }}</td><td class="r">{{ fmt.num(t.count) }}</td><td class="r"><strong>{{ fmt.money(t.total, t.moneda) }}</strong></td></tr> }</tbody>
                </table>
                <p class="muted small" style="margin:6px 0 0">Los montos nunca se suman entre monedas distintas.</p>
              }
              @if (p.skipped > 0) {
                <div class="banner info" style="margin-top:12px"><app-icon name="info" /><span><strong>{{ fmt.num(p.skipped) }}</strong> no se modificarán: {{ skippedText(p.skippedByReason) }}.</span></div>
              }
              @if (p.eligible === 0) { <div class="banner warn" style="margin-top:12px"><app-icon name="alert" /><span>Ninguno de los gastos seleccionados está pendiente, así que no hay nada que hacer.</span></div> }
              @if (op === 'reject' && p.eligible > 0) {
                <label class="field" style="margin-top:14px">Motivo del rechazo (obligatorio)
                  <textarea rows="3" maxlength="500" [value]="reason()" (input)="reason.set(val($event))" [class.invalid]="touched() && !reasonOk()" placeholder="Ej.: factura ilegible, gasto duplicado…" style="height:auto;padding:8px 10px"></textarea>
                  <span class="muted small">Se guarda en cada gasto y en el Historial. {{ reason().trim().length }}/500</span>
                  @if (touched() && !reasonOk()) { <span class="err">Escribe al menos 3 caracteres.</span> }
                </label>
                <div class="banner warn" style="margin-top:12px"><app-icon name="alert" /><span>Un gasto rechazado deja de contar como pendiente. Podrás aprobarlo después editándolo.</span></div>
              }
            }
          }
          @case ('running') {
            <div class="busy" role="status" aria-live="polite"><span class="spin"></span><div><strong>Procesando {{ fmt.num(preview()?.eligible ?? 0) }} gastos…</strong><div class="muted small">Se guardan todos juntos o ninguno. No cierres esta ventana.</div></div></div>
            <div class="bar"><div></div></div>
          }
          @case ('done') {
            @if (result(); as r) {
              <div class="banner ok"><app-icon name="check" /><span>
                @if (r.applied > 0) { <strong>{{ fmt.num(r.applied) }}</strong> gastos {{ op === 'approve' ? 'aprobados' : 'rechazados' }} en {{ r.elapsedMs }} ms. }
                @else { No se modificó ningún gasto. }
              </span></div>
              @if (r.applied > 0) {
                <table class="t sum" style="margin-top:12px" aria-label="Totales aplicados">
                  <thead><tr><th>Moneda</th><th class="r">Gastos</th><th class="r">Total</th></tr></thead>
                  <tbody>@for (t of r.appliedTotals; track t.moneda) { <tr><td>{{ t.moneda }}</td><td class="r">{{ fmt.num(t.count) }}</td><td class="r"><strong>{{ fmt.money(t.total, t.moneda) }}</strong></td></tr> }</tbody>
                </table>
              }
              @if (r.skipped > 0) { <p class="muted" style="margin:12px 0 0"><strong>{{ fmt.num(r.skipped) }}</strong> omitidos: {{ skippedText(r.skippedByReason) }}.</p> }
              <p class="muted small" style="margin:12px 0 0">Operación <span class="mono">{{ r.operationId }}</span>: cada cambio quedó en el Historial.</p>
            }
          }
          @case ('error') {
            @if (problem(); as e) {
              <div class="banner bad" role="alert"><app-icon name="alert" /><span><strong>{{ e.status === 409 ? 'No se modificó nada.' : 'No se pudo completar.' }}</strong> {{ e.title }}
                @if (e.errors) { @for (kv of errorList(e); track kv) { <br>{{ kv }} } }</span></div>
              @if (e.conflicts?.length) {
                <ul class="conf">@for (c of e.conflicts!.slice(0, 6); track $index) { <li>{{ c.message }}</li> }
                  @if ((e.conflictCount ?? 0) > 6) { <li class="muted">… y {{ (e.conflictCount ?? 0) - 6 }} más</li> }</ul>
              }
            }
          }
        }
      </div>
      <div class="m-foot">
        @switch (phase()) {
          @case ('review') {
            <button class="btn" type="button" (click)="close()">Cancelar</button>
            <button class="btn" [class.primary]="op === 'approve'" [class.danger]="op === 'reject'" type="button" (click)="confirm()" [disabled]="!canConfirm()">
              {{ op === 'approve' ? 'Aprobar ' : 'Rechazar ' }}{{ fmt.num(preview()?.eligible ?? 0) }}</button>
          }
          @case ('error') { <button class="btn" type="button" (click)="close()">Cerrar</button><button class="btn primary" type="button" (click)="refresh.emit()">Recargar y volver a seleccionar</button> }
          @case ('done') { <button class="btn primary" type="button" (click)="finish()">Listo</button> }
          @default { <button class="btn" type="button" disabled>{{ phase() === 'running' ? 'Procesando…' : 'Cargando…' }}</button> }
        }
      </div>
    </div>
  </div>`,
  styles: [`
    .sum { margin: 0; min-width: 0; width: 100%; } .small { font-size: 12.5px; } .conf { margin: 10px 0 0; padding-left: 18px; font-size: 13px; }
    .busy { display: flex; gap: 12px; align-items: center; margin-bottom: 14px; }
    .spin { width: 22px; height: 22px; border-radius: 50%; border: 3px solid var(--border-2); border-top-color: var(--accent); animation: sp .8s linear infinite; flex: none; }
    .bar { height: 6px; border-radius: 99px; background: var(--surface-2); overflow: hidden; div { height: 100%; width: 40%; background: var(--accent); border-radius: inherit; animation: ind 1.1s ease-in-out infinite; } }
    @keyframes sp { to { transform: rotate(360deg); } } @keyframes ind { 0% { margin-left: -40%; } 100% { margin-left: 100%; } }
    @media (prefers-reduced-motion: reduce) { .spin, .bar div { animation: none; } }
  `],
})
export class BulkDialogComponent implements OnInit {
  private api = inject(ApiService);
  fmt = fmt;
  @Input({ required: true }) op!: 'approve' | 'reject';
  @Input({ required: true }) request!: BulkRequest;
  @Output() closed = new EventEmitter<void>();
  /** Emitted after a successful run so the list reloads and the selection is cleared. */
  @Output() done = new EventEmitter<BulkResult>();
  /** The server refused the batch because the data moved: reload and start over. */
  @Output() refresh = new EventEmitter<void>();

  phase = signal<'loading' | 'review' | 'running' | 'done' | 'error'>('loading');
  preview = signal<BulkPreview | null>(null);
  result = signal<BulkResult | null>(null);
  problem = signal<ApiProblem | null>(null);
  reason = signal(''); touched = signal(false);
  title = computed(() => (this.op === 'approve' ? 'Aprobar gastos seleccionados' : 'Rechazar gastos seleccionados'));
  reasonOk = computed(() => this.reason().trim().length >= 3);
  canConfirm = computed(() => (this.preview()?.eligible ?? 0) > 0 && (this.op === 'approve' || this.reasonOk()));

  ngOnInit() {
    this.api.bulkPreview(this.op, this.request).subscribe({
      next: p => { this.preview.set(p); this.phase.set('review'); },
      error: e => { this.problem.set(asProblem(e)); this.phase.set('error'); },
    });
  }

  val(e: Event) { return (e.target as HTMLTextAreaElement).value; }
  skippedText(m: Record<string, number>) { return Object.entries(m).map(([k, n]) => `${n} ${REASONS[k] ?? k}`).join(', '); }
  errorList(p: ApiProblem) { return Object.values(p.errors ?? {}).flat(); }

  confirm() {
    this.touched.set(true);
    if (!this.canConfirm()) return;
    this.phase.set('running');
    this.api.bulkRun(this.op, { ...this.request, reason: this.op === 'reject' ? this.reason().trim() : undefined }).subscribe({
      next: r => { this.result.set(r); this.phase.set('done'); },
      error: e => { this.problem.set(asProblem(e)); this.phase.set('error'); },
    });
  }
  finish() { const r = this.result(); if (r) this.done.emit(r); this.closed.emit(); }
  close() { if (this.phase() !== 'running') this.closed.emit(); }
}
