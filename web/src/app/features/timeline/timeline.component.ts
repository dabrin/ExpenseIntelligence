import { ChangeDetectionStrategy, Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { catchError, debounceTime, of, switchMap, tap } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { Timeline } from '../../core/models';
import { DataVersion, FilterStore, fmt } from '../../core/state';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { IconComponent } from '../../shared/icon.component';
import { RaceBarsComponent, RaceItem } from '../../shared/race-bars.component';

type Mode = 'acum' | 'mes';
interface Series { key: string; value: number[]; count: number[] }

const MONTH_MS = 900;
const monthName = new Intl.DateTimeFormat('es-CR', { month: 'long', year: 'numeric' });
function label(period: string) {
  if (!period) return '';
  const [y, m] = period.split('-').map(Number);
  const t = monthName.format(new Date(y, m - 1, 1));
  return t.charAt(0).toUpperCase() + t.slice(1);
}

@Component({
  selector: 'app-timeline', standalone: true, imports: [FilterBarComponent, IconComponent, RaceBarsComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="head rise">
      <div>
        <h1>Máquina del tiempo</h1>
        <p class="muted">Reproduce el gasto mes a mes y mira cómo cambia el ranking de departamentos y categorías. Son los datos reales del archivo; las monedas nunca se suman entre sí.</p>
      </div>
    </header>

    <app-filter-bar />

    @if (error(); as e) {
      <div class="banner bad" role="alert" style="margin-top:16px"><app-icon name="alert" /><div><strong>No se pudo cargar la línea de tiempo.</strong><div class="muted">{{ e }}</div></div></div>
    }

    @if (loading() && !data()) {
      <div class="card" style="margin-top:16px"><div class="skeleton" style="height:360px"></div></div>
    } @else {
     @if (data(); as d) {
      @if (d.frames.length === 0) {
        <div class="card empty" style="margin-top:16px"><app-icon name="inbox" [size]="40" /><strong>Ningún gasto coincide con los filtros</strong><span>Ajusta o restablece los filtros para reproducir la historia.</span></div>
      } @else {
        <section class="card stage" [class.stale]="loading()">
          <div class="controls">
            <button type="button" class="btn primary play" (click)="toggle()" [attr.aria-label]="playing() ? 'Pausar' : 'Reproducir'">
              <app-icon [name]="playing() ? 'pause' : 'play'" /> {{ playing() ? 'Pausar' : atEnd() ? 'Repetir' : 'Reproducir' }}
            </button>
            <button type="button" class="btn icon" (click)="restart()" aria-label="Volver al inicio" title="Volver al inicio"><app-icon name="refresh" /></button>
            <div class="seg" role="group" aria-label="Moneda">
              @for (c of d.currencies; track c) { <button type="button" [class.on]="cur() === c" (click)="curChoice.set(c)">{{ c }}</button> }
            </div>
            <div class="seg" role="group" aria-label="Qué se muestra">
              <button type="button" [class.on]="mode() === 'acum'" (click)="mode.set('acum')">Acumulado</button>
              <button type="button" [class.on]="mode() === 'mes'" (click)="mode.set('mes')">Mes a mes</button>
            </div>
            <div class="seg" role="group" aria-label="Velocidad">
              @for (s of speeds; track s) { <button type="button" [class.on]="speed() === s" (click)="setSpeed(s)">{{ s }}x</button> }
            </div>
          </div>

          <div class="when">
            <div class="period" aria-live="polite">{{ periodLabel() }}</div>
            <div class="kpis">
              <div><span class="muted">{{ mode() === 'acum' ? 'Gastado hasta ahora' : 'Gastado en el mes' }}</span><strong>{{ money(kpi().total) }}</strong></div>
              <div><span class="muted">Gastos</span><strong>{{ num(kpi().count) }}</strong></div>
              <div><span class="muted">Aprobados</span><strong>{{ kpi().count ? pct(kpi().approvedPct) : '—' }}</strong></div>
            </div>
          </div>

          <p class="story" aria-live="polite">{{ story() }}</p>

          <div class="slider">
            <input type="range" min="0" [max]="last()" step="1" [value]="idx()" (input)="seek($any($event.target).valueAsNumber)" aria-label="Mes de la línea de tiempo" />
            <div class="ends"><span>{{ firstLabel() }}</span><span>{{ lastLabel() }}</span></div>
          </div>
        </section>

        <section class="races">
          <article class="card">
            <div class="card-head"><div><h2>Departamentos</h2><p>{{ mode() === 'acum' ? 'Gasto acumulado' : 'Gasto del mes' }} en {{ cur() }}</p></div></div>
            <app-race-bars [items]="depItems()" [currency]="cur()" [max]="depMax()" [limit]="8" label="Ranking de departamentos" />
          </article>
          <article class="card">
            <div class="card-head"><div><h2>Categorías</h2><p>{{ mode() === 'acum' ? 'Gasto acumulado' : 'Gasto del mes' }} en {{ cur() }}</p></div></div>
            <app-race-bars [items]="catItems()" [currency]="cur()" [max]="catMax()" [limit]="8" label="Ranking de categorías" />
          </article>
        </section>
      }
     }
    }`,
  styles: [`
    :host { display: block; }
    .head { margin-bottom: 18px; h1 { margin: 0 0 4px; } p { margin: 0; } }
    .stage { margin-top: 16px; display: grid; gap: 16px; }
    .controls { display: flex; flex-wrap: wrap; align-items: center; gap: 10px; }
    .play { min-width: 138px; justify-content: center; }
    .when { display: flex; flex-wrap: wrap; align-items: flex-end; justify-content: space-between; gap: 14px 28px; }
    .period { font-size: clamp(30px, 5vw, 52px); font-weight: 800; letter-spacing: -.03em; line-height: 1; }
    .kpis { display: flex; flex-wrap: wrap; gap: 8px 30px; div { display: grid; gap: 2px; } span { font-size: 12px; } strong { font-size: 20px; font-variant-numeric: tabular-nums; } }
    .story { margin: 0; min-height: 22px; color: var(--muted); }
    .slider input { width: 100%; accent-color: var(--accent); cursor: pointer; }
    .ends { display: flex; justify-content: space-between; font-size: 12px; color: var(--faint); }
    .races { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(100%, 420px), 1fr)); gap: 16px; margin-top: 16px; h2 { margin: 0; font-size: 16px; } }
    .stale { opacity: .7; transition: opacity .2s; }
  `],
})
export class TimelineComponent {
  private api = inject(ApiService);
  private store = inject(FilterStore);
  private dv = inject(DataVersion);
  private destroyRef = inject(DestroyRef);
  readonly speeds = [0.25, 0.5, 0.75, 1, 2, 4];

  data = signal<Timeline | null>(null);
  loading = signal(true);
  error = signal<string | null>(null);
  curChoice = signal<string | null>(null);
  mode = signal<Mode>('acum');
  idx = signal(0);
  playing = signal(false);
  speed = signal(1);
  private timer: ReturnType<typeof setInterval> | null = null;

  last = computed(() => Math.max(0, (this.data()?.frames.length ?? 1) - 1));
  atEnd = computed(() => this.idx() >= this.last());
  cur = computed(() => {
    const d = this.data(), c = this.curChoice();
    if (!d) return 'USD';
    if (c && d.currencies.includes(c)) return c;
    const n = (m: string) => d.frames.reduce((a, f) => a + (f.totals.find(t => t.moneda === m)?.count ?? 0), 0);
    return [...d.currencies].sort((a, b) => n(b) - n(a))[0] ?? 'USD';
  });

  private dep = computed(() => this.build('byDepartment', this.data()?.departments ?? []));
  private cat = computed(() => this.build('byCategory', this.data()?.categories ?? []));
  /** Per-month totals for the selected currency, plus running totals; prefix sums make every frame O(1) to read. */
  private month = computed(() => {
    const d = this.data(), c = this.cur();
    const rows = (d?.frames ?? []).map(f => f.totals.find(t => t.moneda === c));
    const out = { total: [] as number[], count: [] as number[], approved: [] as number[] };
    rows.forEach(r => { out.total.push(r?.total ?? 0); out.count.push(r?.count ?? 0); out.approved.push(r?.approved ?? 0); });
    return out;
  });

  periodLabel = computed(() => label(this.data()?.frames[this.idx()]?.period ?? ''));
  firstLabel = computed(() => label(this.data()?.frames[0]?.period ?? ''));
  lastLabel = computed(() => label(this.data()?.frames[this.last()]?.period ?? ''));

  kpi = computed(() => {
    const m = this.month(), i = this.idx(), from = this.mode() === 'acum' ? 0 : i;
    let total = 0, count = 0, approved = 0;
    for (let k = from; k <= i; k++) { total += m.total[k] ?? 0; count += m.count[k] ?? 0; approved += m.approved[k] ?? 0; }
    return { total, count, approvedPct: count ? (100 * approved) / count : 0 };
  });

  depItems = computed(() => this.itemsAt(this.dep(), this.idx(), this.mode()));
  catItems = computed(() => this.itemsAt(this.cat(), this.idx(), this.mode()));
  depMax = computed(() => this.scale(this.dep(), this.depItems()));
  catMax = computed(() => this.scale(this.cat(), this.catItems()));

  /** One factual sentence per frame, built only from the numbers on screen. */
  story = computed(() => {
    const i = this.idx(), mode = this.mode(), s = this.dep();
    const now = this.leader(this.itemsAt(s, i, mode));
    if (!now) return mode === 'mes' ? 'Este mes no hubo gastos con los filtros actuales.' : '';
    const share = this.kpi().total > 0 ? ` (${fmt.pct((100 * now.value) / this.kpi().total)} del total)` : '';
    const before = i > 0 ? this.leader(this.itemsAt(s, i - 1, mode)) : null;
    const lead = `${now.key} lidera con ${this.money(now.value)}${share}.`;
    return before && before.key !== now.key ? `Cambio de liderazgo: ${now.key} supera a ${before.key}. ${lead}` : lead;
  });

  constructor() {
    const trigger = computed(() => ({ f: this.store.filters(), v: this.dv.n() }));
    toObservable(trigger).pipe(
      debounceTime(220),
      tap(() => { this.loading.set(true); this.error.set(null); }),
      switchMap(({ f }) => this.api.timeline(f).pipe(catchError(err => { this.error.set(asProblem(err).title); return of(null); }))),
      takeUntilDestroyed(),
    ).subscribe(t => {
      if (t) { this.pause(); this.data.set(t); this.idx.set(Math.max(0, t.frames.length - 1)); }
      this.loading.set(false);
    });
    this.destroyRef.onDestroy(() => this.pause());
  }

  money(v: number) { return fmt.money(v, this.cur(), true); }
  num = fmt.num;
  pct = fmt.pct;

  toggle() { this.playing() ? this.pause() : this.play(); }
  restart() { this.pause(); this.idx.set(0); }
  seek(i: number) { this.pause(); this.idx.set(Math.min(this.last(), Math.max(0, i))); }
  setSpeed(s: number) { this.speed.set(s); if (this.playing()) { this.clear(); this.start(); } }

  private play() {
    if (this.atEnd()) this.idx.set(0);
    this.playing.set(true);
    this.start();
  }
  private start() {
    this.timer = setInterval(() => {
      if (this.idx() >= this.last()) { this.pause(); return; }
      this.idx.update(i => i + 1);
    }, MONTH_MS / this.speed());
  }
  private clear() { if (this.timer) { clearInterval(this.timer); this.timer = null; } }
  private pause() { this.clear(); this.playing.set(false); }

  private build(dim: 'byDepartment' | 'byCategory', keys: string[]): Series[] {
    const d = this.data(), c = this.cur();
    const frames = d?.frames ?? [];
    return keys.map(key => ({
      key,
      value: frames.map(f => f[dim].find(x => x.key === key && x.moneda === c)?.total ?? 0),
      count: frames.map(f => f[dim].find(x => x.key === key && x.moneda === c)?.count ?? 0),
    }));
  }

  private itemsAt(series: Series[], i: number, mode: Mode): RaceItem[] {
    return series.map((s, n) => {
      let value = 0, count = 0;
      for (let k = mode === 'acum' ? 0 : i; k <= i; k++) { value += s.value[k] ?? 0; count += s.count[k] ?? 0; }
      return { key: s.key, value, count, color: `var(--s${(n % 10) + 1})` };
    });
  }

  private leader(items: RaceItem[]): RaceItem | null {
    let best: RaceItem | null = null;
    for (const it of items) if (it.value > 0 && (!best || it.value > best.value || (it.value === best.value && it.key < best.key))) best = it;
    return best;
  }

  /** Cumulative bars rescale to the current leader (so early frames still fill the track); monthly bars share one scale so months compare. */
  private scale(series: Series[], items: RaceItem[]): number {
    if (this.mode() === 'acum') return Math.max(1, ...items.map(i => i.value));
    return Math.max(1, ...series.flatMap(s => s.value));
  }
}
