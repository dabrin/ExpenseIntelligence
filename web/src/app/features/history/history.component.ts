import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap, tap } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { HistoryItem } from '../../core/models';
import { DataVersion, ToastService, fmt } from '../../core/state';
import { IconComponent } from '../../shared/icon.component';

const GROUPS: { key: string; label: string }[] = [
  { key: 'all', label: 'Todo' }, { key: 'edits', label: 'Ediciones' }, { key: 'deletes', label: 'Eliminaciones' },
  { key: 'restores', label: 'Restauraciones' }, { key: 'creates', label: 'Creaciones' }, { key: 'files', label: 'Archivos' },
];

@Component({
  selector: 'app-history', standalone: true, imports: [IconComponent, DatePipe], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="head rise">
      <div><h1>Historial de cambios</h1>
        <p class="muted">Todo lo que se hizo en el sistema: ediciones, eliminaciones, restauraciones, creaciones de gastos, cambios de estado y cargas de archivos. Los últimos 500 eventos.</p></div>
    </header>

    <div class="seg" role="group" aria-label="Tipo de evento" style="margin-bottom:14px">
      @for (g of groups; track g.key) { <button type="button" [class.on]="group() === g.key" (click)="group.set(g.key)">{{ g.label }}</button> }
    </div>

    @if (error(); as e) { <div class="banner bad" role="alert"><app-icon name="alert" /><span>{{ e }}</span></div> }

    @if (loading() && items().length === 0) {
      <div class="card"><div class="skeleton" style="height:200px"></div></div>
    } @else if (items().length === 0) {
      <div class="card empty"><app-icon name="inbox" [size]="40" /><strong>Sin eventos todavía</strong><span>Cuando edites, elimines o cargues archivos, aparecerá aquí.</span></div>
    } @else {
      <ol class="feed" [class.stale]="loading()">
        @for (i of items(); track $index) {
          <li class="card item" [attr.data-group]="i.group">
            <div class="top">
              <span class="tag" [class]="'tag ' + i.group">{{ tagLabel(i.group) }}</span>
              <strong>{{ i.title }}</strong>
              <span class="spacer"></span>
              <span class="muted when">{{ i.at | date: 'dd/MM/yyyy HH:mm:ss' }} · {{ i.actor }}</span>
            </div>
            @if (i.detail) { <div class="muted detail">{{ i.detail }}@if (i.amount !== null && i.currency) { · <strong>{{ money(i.amount, i.currency) }}</strong> }</div> }
            @if (i.changes.length) {
              <ul class="changes">
                @for (c of i.changes; track c.field) { <li><span class="muted">{{ c.label }}</span> <s>{{ shown(i, c.field, c.before) }}</s> <app-icon name="down" [size]="12" class="arrow" /> <strong>{{ shown(i, c.field, c.after) }}</strong></li> }
              </ul>
            }
            @if (i.canRestore) { <div><button type="button" class="btn sm" (click)="restore(i)"><app-icon name="undo" /> Restaurar este gasto</button></div> }
          </li>
        }
      </ol>
    }`,
  styles: [`
    .head { margin-bottom: 18px; h1 { margin: 0 0 4px; } p { margin: 0; } }
    .feed { list-style: none; margin: 0; padding: 0; display: grid; gap: 10px; }
    .stale { opacity: .7; }
    .item { display: grid; gap: 8px; padding: 14px 16px; }
    .top { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 10px; }
    .when { font-size: 12px; }
    .detail { font-size: 13px; }
    .tag { font-size: 11px; font-weight: 700; text-transform: uppercase; letter-spacing: .04em; padding: 2px 8px; border-radius: 99px; background: var(--surface-2); border: 1px solid var(--border-2); color: var(--muted); }
    .tag.deletes { color: var(--bad); border-color: var(--bad); } .tag.restores, .tag.creates { color: var(--ok); border-color: var(--ok); }
    .tag.edits { color: var(--warn); border-color: var(--warn); } .tag.files { color: var(--accent); border-color: var(--accent); }
    .changes { list-style: none; margin: 0; padding: 0; display: grid; gap: 4px; font-size: 13px; li { display: flex; flex-wrap: wrap; align-items: center; gap: 8px; } s { color: var(--faint); } .arrow { transform: rotate(-90deg); color: var(--faint); } }
  `],
})
export class HistoryComponent {
  private api = inject(ApiService);
  private dv = inject(DataVersion);
  private toast = inject(ToastService);
  readonly groups = GROUPS;
  group = signal('all');
  items = signal<HistoryItem[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);

  constructor() {
    const trigger = computed(() => ({ g: this.group(), v: this.dv.n() }));
    toObservable(trigger).pipe(
      tap(() => { this.loading.set(true); this.error.set(null); }),
      switchMap(({ g }) => this.api.history(200, g).pipe(catchError(err => { this.error.set(asProblem(err).title); return of(null); }))),
      takeUntilDestroyed(),
    ).subscribe(r => { if (r) this.items.set(r); this.loading.set(false); });
  }

  private readonly tags: Record<string, string> = { edits: 'Edición', deletes: 'Eliminación', restores: 'Restauración', creates: 'Creación', files: 'Archivo', bulk: 'Corrección' };
  tagLabel(g: string) { return this.tags[g] ?? 'Sistema'; }
  money(v: number, cur: string) { return fmt.money(v, cur); }
  shown(i: HistoryItem, field: string, v: string | null) { return field === 'monto' && v !== null && i.currency ? fmt.money(Number(v), i.currency) : v; }

  restore(i: HistoryItem) {
    if (i.expenseId === null) return;
    this.api.restore(i.expenseId).subscribe({
      next: () => { this.toast.show('ok', `Gasto #${i.expenseId} restaurado.`); this.dv.bump(); },
      error: e => this.toast.show('error', asProblem(e).title),
    });
  }
}
