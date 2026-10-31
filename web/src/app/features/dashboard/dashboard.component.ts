import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { Router, RouterLink } from '@angular/router';
import { ChartConfiguration } from 'chart.js';
import { catchError, debounceTime, forkJoin, of, switchMap, tap } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { Filters, Insight, InsightCategory, Summary, TimePoint } from '../../core/models';
import { DataVersion, FilterStore, ThemeService, fmt } from '../../core/state';
import { ChartTableComponent, ChartTableRow } from '../../shared/chart-table.component';
import { ChartComponent } from '../../shared/chart.component';
import { ExportMenuComponent } from '../../shared/export-menu.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { IconComponent } from '../../shared/icon.component';
import { MascotComponent } from '../../shared/mascot.component';

const CUR_VAR: Record<string, string> = { CRC: '--c-crc', USD: '--c-usd', EUR: '--c-eur' };

export type Section = 'kpis' | 'evolucion' | 'estado' | 'departamentos' | 'categorias' | 'pagos' | 'insights';
export const SECTIONS: { key: Section; label: string }[] = [
  { key: 'kpis', label: 'Indicadores clave' }, { key: 'evolucion', label: 'Evolución temporal' }, { key: 'estado', label: 'Estado de los gastos' },
  { key: 'departamentos', label: 'Departamentos' }, { key: 'categorias', label: 'Top categorías' }, { key: 'pagos', label: 'Métodos de pago' }, { key: 'insights', label: 'Smart Insights' },
];
type Mode = 'line' | 'bar';
type Gran = 'month' | 'week';
interface Prefs { hidden: Section[]; mode: Mode; granularity: Gran }
const PREFS_KEY = 'ei-dashboard-prefs';
const DEFAULT_PREFS: Prefs = { hidden: [], mode: 'line', granularity: 'month' };

/** Local, per-browser visual preferences only; the data and the filters never go through here. */
function loadPrefs(): Prefs {
  try {
    const p = JSON.parse(localStorage.getItem(PREFS_KEY) ?? 'null') as Partial<Prefs> | null;
    return {
      hidden: Array.isArray(p?.hidden) ? p.hidden.filter(h => SECTIONS.some(s => s.key === h)) : [],
      mode: p?.mode === 'bar' ? 'bar' : 'line', granularity: p?.granularity === 'week' ? 'week' : 'month',
    };
  } catch { return { ...DEFAULT_PREFS }; }
}

type DimRow = { key: string; moneda: string; count: number; total: number; average: number };
export const INSIGHT_LIMIT = 5;
const TABS: { key: 'todos' | InsightCategory; label: string }[] = [
  { key: 'todos', label: 'Todos' }, { key: 'alertas', label: 'Alertas' }, { key: 'tendencias', label: 'Tendencias' }, { key: 'distribucion', label: 'Distribución' },
];

@Component({
  selector: 'app-dashboard', standalone: true, imports: [FilterBarComponent, ChartComponent, ChartTableComponent, IconComponent, RouterLink, ExportMenuComponent, MascotComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.component.html', styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  private api = inject(ApiService);
  private store = inject(FilterStore);
  private dv = inject(DataVersion);
  private theme = inject(ThemeService);
  private router = inject(Router);
  activeCount = this.store.activeCount;
  printedAt = new Date().toLocaleString('es');
  /** Plain-language list of the active filters, printed at the top of the PDF so the report says what it covers. */
  filterText = computed(() => {
    const f = this.store.filters(); const p: string[] = [];
    if (f.from || f.to) p.push(`fechas ${f.from || 'inicio'} a ${f.to || 'hoy'}`);
    if (f.departamentos.length) p.push('departamento: ' + f.departamentos.join(', '));
    if (f.categorias.length) p.push('categoría: ' + f.categorias.join(', '));
    if (f.monedas.length) p.push('moneda: ' + f.monedas.join(', '));
    if (f.metodosPago.length) p.push('pago: ' + f.metodosPago.join(', '));
    if (f.estado) p.push('estado: ' + f.estado);
    if (f.q.trim()) p.push('búsqueda: «' + f.q.trim() + '»');
    if (f.ids.length) p.push('gastos: ' + f.ids.slice(0, 6).map(i => '#' + i).join(', '));
    return p.join(' · ');
  });
  fmt = fmt;
  sections = SECTIONS;
  tabs = TABS;

  summary = signal<Summary | null>(null);
  insights = signal<Insight[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  curChoice = signal<string | null>(null);

  // ---- light personalisation (localStorage) ----
  prefs = signal<Prefs>(loadPrefs());
  viewOpen = signal(false);
  granularity = computed(() => this.prefs().granularity);
  mode = computed(() => this.prefs().mode);
  show = (s: Section) => !this.prefs().hidden.includes(s);
  isCustom = computed(() => this.prefs().hidden.length > 0);
  private savePrefs(p: Prefs) { this.prefs.set(p); try { localStorage.setItem(PREFS_KEY, JSON.stringify(p)); } catch { /* storage unavailable: the choice lasts for this visit */ } }
  setGranularity(g: Gran) { this.savePrefs({ ...this.prefs(), granularity: g }); }
  setMode(m: Mode) { this.savePrefs({ ...this.prefs(), mode: m }); }
  toggleSection(s: Section) { const h = this.prefs().hidden; this.savePrefs({ ...this.prefs(), hidden: h.includes(s) ? h.filter(x => x !== s) : [...h, s] }); }
  resetView() { this.savePrefs({ ...DEFAULT_PREFS }); }

  currencies = computed(() => this.summary()?.totals.map(t => t.moneda) ?? []);
  cur = computed(() => { const c = this.curChoice(), all = this.currencies(); return c && all.includes(c) ? c : all[0] ?? 'USD'; });

  constructor() {
    const trigger = computed(() => ({ f: this.store.filters(), g: this.granularity(), v: this.dv.n() }));
    toObservable(trigger).pipe(
      debounceTime(220),
      tap(() => { this.loading.set(true); this.error.set(null); }),
      switchMap(({ f, g }) => forkJoin({ s: this.api.summary(f, g), i: this.api.insights(f) }).pipe(
        catchError(err => { this.error.set(asProblem(err).title); return of(null); }))),
      takeUntilDestroyed(),
    ).subscribe(r => {
      if (r) { this.summary.set(r.s); this.insights.set(r.i.insights); }
      this.loading.set(false);
    });
  }

  reload() { this.dv.bump(); }
  curVar(m: string) { return `var(${CUR_VAR[m] ?? '--accent'})`; }
  private css(v: string) { this.theme.theme(); return getComputedStyle(document.documentElement).getPropertyValue(v).trim(); }
  curColor = computed(() => this.css(CUR_VAR[this.cur()] ?? '--accent'));
  hasData = computed(() => (this.summary()?.totalRecords ?? 0) > 0);

  // ---- drill-down: the charts only write into the shared FilterStore and open /gastos, which already reads it (server-side paging included) ----
  /** Adds to the filters the user already has (chart clicks); `fresh` starts from a clean slate (insight links promise an exact set). */
  private go(p: Partial<Filters>, fresh = false) {
    if (fresh) this.store.reset();
    this.store.patch(p);
    void this.router.navigateByUrl('/gastos');
  }
  drillDepartment(key: string) { this.go({ departamentos: [key], monedas: [this.cur()] }); }
  drillCategory(key: string) { this.go({ categorias: [key], monedas: [this.cur()] }); }
  drillPayment(key: string) { this.go({ metodosPago: [key], monedas: [this.cur()] }); }
  drillState(estado: Filters['estado']) { this.go({ estado }); }
  drillPeriod(p: TimePoint) {
    const f = this.store.filters();
    this.go({ from: f.from && f.from > p.start ? f.from : p.start, to: f.to && f.to < p.end ? f.to : p.end, monedas: [this.cur()] });
  }

  // ---- chart styling ----
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private base(): any {
    const text = this.css('--muted'), grid = this.css('--grid');
    return {
      animation: { duration: 500 },
      plugins: { legend: { labels: { color: text, usePointStyle: true, boxWidth: 8, font: { family: 'Inter', size: 12 } } },
        tooltip: { backgroundColor: this.css('--surface-2'), titleColor: this.css('--text'), bodyColor: this.css('--text'), footerColor: this.css('--muted'), borderColor: this.css('--border-2'), borderWidth: 1, padding: 10, cornerRadius: 10 } },
      scales: { x: { ticks: { color: text, maxRotation: 0, autoSkipPadding: 14 }, grid: { display: false }, border: { display: false } },
        y: { beginAtZero: true, ticks: { color: text, callback: (v: unknown) => fmt.money(Number(v), this.cur(), true) }, grid: { color: grid }, border: { display: false } } },
    };
  }
  private seriesColors() { return ['--s1', '--s2', '--s3', '--s4', '--s5', '--s6', '--s7', '--s8', '--s9', '--s10'].map(v => this.css(v)); }
  private moneyTip = () => ({ callbacks: { label: (c: { dataset: { label?: string }; raw: unknown }) => ` ${c.dataset.label ?? ''}: ${fmt.money(Number(c.raw), this.cur())}` } });
  private hover(): { hoverBorderColor: string; hoverBorderWidth: number } { return { hoverBorderColor: this.css('--text'), hoverBorderWidth: 2 }; }
  /** Diagonal stripes: the bar of an incomplete period, so it is not read as a real drop. */
  private hatch(color: string): CanvasPattern | string {
    const c = document.createElement('canvas'); c.width = c.height = 8;
    const x = c.getContext('2d'); if (!x) return color + '66';
    x.fillStyle = color + '33'; x.fillRect(0, 0, 8, 8); x.strokeStyle = color; x.lineWidth = 1.6;
    x.beginPath(); x.moveTo(-2, 10); x.lineTo(10, -2); x.stroke();
    return x.createPattern(c, 'repeat') ?? color + '66';
  }

  // ---- temporal evolution ----
  points = computed(() => (this.summary()?.series ?? []).filter(p => p.moneda === this.cur()));
  hasRejected = computed(() => this.points().some(p => p.rejectedCount > 0));
  hasPartial = computed(() => this.points().some(p => p.partial));
  gLabel = computed(() => (this.granularity() === 'month' ? 'mensual' : 'semanal'));

  timeChart = computed<ChartConfiguration>(() => {
    const pts = this.points(); const cur = this.cur(); const g = this.granularity(); const bar = this.mode() === 'bar';
    // same state palette as the doughnut and the KPI footers: approved = accent, pending = warning, rejected = bad
    const base = this.base(); const ok = this.css('--accent'), warn = this.css('--warn'), bad = this.css('--bad');
    const partial = pts.map(p => p.partial);
    const tip = {
      ...base.plugins.tooltip, mode: 'index', intersect: false,
      callbacks: {
        title: (items: { dataIndex: number }[]) => { const p = pts[items[0].dataIndex]; return fmt.range(p.start, p.end, g) + (p.partial ? ' · incompleto' : ''); },
        label: (c: { dataIndex: number; dataset: { label?: string } }) => {
          const p = pts[c.dataIndex]; const l = c.dataset.label;
          const [t, n] = l === 'Aprobado' ? [p.approvedTotal, p.approvedCount] : l === 'Pendiente' ? [p.pendingTotal, p.pendingCount] : [p.rejectedTotal, p.rejectedCount];
          return ` ${l}: ${fmt.money(t, cur)} · ${fmt.num(n)} ${n === 1 ? 'gasto' : 'gastos'}`;
        },
        footer: (items: { dataIndex: number }[]) => {
          const p = pts[items[0].dataIndex];
          return [`Total${this.hasRejected() ? ' (con rechazados)' : ''}: ${fmt.money(p.total, cur)} · ${fmt.num(p.count)} gastos`,
            ...(p.partial ? ['Periodo incompleto: los datos no cubren todo el periodo.'] : [])];
        },
      },
    };
    const datasets = bar
      ? [
          { label: 'Aprobado', data: pts.map(p => p.approvedTotal), stack: 'gasto', backgroundColor: pts.map(p => (p.partial ? this.hatch(ok) : ok)), borderColor: ok, borderWidth: 1, borderRadius: 4, ...this.hover() },
          { label: 'Pendiente', data: pts.map(p => p.pendingTotal), stack: 'gasto', backgroundColor: pts.map(p => (p.partial ? this.hatch(warn) : warn)), borderColor: warn, borderWidth: 1, borderRadius: 4, ...this.hover() },
          ...(this.hasRejected() ? [{ label: 'Rechazado', data: pts.map(p => p.rejectedTotal), stack: 'rechazado', backgroundColor: pts.map(p => (p.partial ? this.hatch(bad) : bad)), borderColor: bad, borderWidth: 1, borderRadius: 4, ...this.hover() }] : []),
        ]
      : [
          { label: 'Aprobado', data: pts.map(p => p.approvedTotal), stack: 'gasto', borderColor: ok, backgroundColor: ok + '55', fill: true, tension: .35, pointRadius: pts.length > 40 ? 0 : 3, pointHoverRadius: 6, borderWidth: 2, segment: { borderDash: (c: { p1DataIndex: number }) => (partial[c.p1DataIndex] ? [5, 4] : undefined) } },
          { label: 'Pendiente', data: pts.map(p => p.pendingTotal), stack: 'gasto', borderColor: warn, backgroundColor: warn + '33', fill: true, tension: .35, pointRadius: pts.length > 40 ? 0 : 3, pointHoverRadius: 6, borderWidth: 1.5, segment: { borderDash: (c: { p1DataIndex: number }) => (partial[c.p1DataIndex] ? [5, 4] : undefined) } },
          ...(this.hasRejected() ? [{ label: 'Rechazado', data: pts.map(p => p.rejectedTotal), stack: 'rechazado', borderColor: bad, backgroundColor: bad + '22', fill: false, tension: .35, pointRadius: pts.length > 40 ? 0 : 3, pointHoverRadius: 6, borderWidth: 1.5, borderDash: [2, 3] }] : []),
        ];
    return {
      type: bar ? 'bar' : 'line',
      data: { labels: pts.map(p => fmt.period(p.period, g) + (p.partial ? ' *' : '')), datasets: datasets as never },
      options: { ...base, interaction: { mode: 'index', intersect: false }, scales: { ...base.scales, x: { ...base.scales['x'], stacked: true }, y: { ...base.scales['y'], stacked: true } },
        plugins: { ...base.plugins, tooltip: tip } },
    };
  });
  pickPeriod(e: { index: number }) { const p = this.points()[e.index]; if (p) this.drillPeriod(p); }
  periodRows = computed<ChartTableRow[]>(() => this.points().map(p => ({
    key: p.period, label: fmt.range(p.start, p.end, this.granularity()) + (p.partial ? ' (incompleto)' : ''),
    cells: [fmt.money(p.approvedTotal, p.moneda), fmt.money(p.pendingTotal, p.moneda), fmt.money(p.rejectedTotal, p.moneda), fmt.money(p.total, p.moneda), fmt.num(p.count)],
  })));
  openPeriod(key: string) { const p = this.points().find(x => x.period === key); if (p) this.drillPeriod(p); }

  // ---- dimensions ----
  private dimension(stats: DimRow[] | undefined, field: 'total' | 'average', limit = 10): DimRow[] {
    return (stats ?? []).filter(x => x.moneda === this.cur()).sort((a, b) => b[field] - a[field]).slice(0, limit);
  }
  deptAvg = computed(() => this.dimension(this.summary()?.byDepartment, 'average'));
  deptTotal = computed(() => this.dimension(this.summary()?.byDepartment, 'total'));
  cats = computed(() => this.dimension(this.summary()?.byCategory, 'total', 8));
  pays = computed(() => this.dimension(this.summary()?.byPaymentMethod, 'total'));

  avgDeptChart = computed<ChartConfiguration>(() => {
    const d = this.deptAvg(); const base = this.base();
    return { type: 'bar', data: { labels: d.map(x => x.key), datasets: [{ label: 'Promedio por gasto', data: d.map(x => x.average), backgroundColor: this.curColor(), borderRadius: 7, maxBarThickness: 44, ...this.hover() }] },
      options: { ...base, plugins: { ...base.plugins, legend: { display: false }, tooltip: { ...base.plugins.tooltip, ...this.moneyTip() } },
        scales: { ...base.scales, x: { ...base.scales['x'], ticks: { color: this.css('--muted'), autoSkip: false, maxRotation: 35, minRotation: 0 } } } } };
  });

  deptTotalChart = computed<ChartConfiguration>(() => {
    const d = this.deptTotal(); const base = this.base();
    return { type: 'bar', data: { labels: d.map(x => x.key), datasets: [{ label: 'Total', data: d.map(x => x.total), backgroundColor: this.seriesColors().slice(0, d.length), borderRadius: 7, maxBarThickness: 26, ...this.hover() }] },
      options: { ...base, indexAxis: 'y', plugins: { ...base.plugins, legend: { display: false }, tooltip: { ...base.plugins.tooltip, ...this.moneyTip() } },
        scales: { x: { ...base.scales['y'], ticks: { color: this.css('--muted'), callback: (v: unknown) => fmt.money(Number(v), this.cur(), true) } }, y: { ...base.scales['x'], ticks: { color: this.css('--text') } } } } };
  });

  categoryChart = computed<ChartConfiguration>(() => {
    const d = this.cats(); const base = this.base();
    return { type: 'bar', data: { labels: d.map(x => x.key), datasets: [{ label: 'Total', data: d.map(x => x.total), backgroundColor: this.curColor(), borderRadius: 7, maxBarThickness: 22, ...this.hover() }] },
      options: { ...base, indexAxis: 'y', plugins: { ...base.plugins, legend: { display: false }, tooltip: { ...base.plugins.tooltip, ...this.moneyTip() } },
        scales: { x: { ...base.scales['y'], ticks: { color: this.css('--muted'), callback: (v: unknown) => fmt.money(Number(v), this.cur(), true) } }, y: { ...base.scales['x'], ticks: { color: this.css('--text') } } } } };
  });

  approvalChart = computed<ChartConfiguration>(() => {
    const a = this.summary()?.approval; const base = this.base();
    return { type: 'doughnut', data: { labels: ['Aprobados', 'Pendientes', 'Rechazados'], datasets: [{ data: [a?.approved ?? 0, a?.pending ?? 0, a?.rejected ?? 0], backgroundColor: [this.css('--accent'), this.css('--warn'), this.css('--bad')], borderColor: this.css('--surface'), borderWidth: 3, hoverOffset: 10 }] },
      options: { animation: base.animation, cutout: '72%', plugins: { legend: { position: 'bottom', labels: { color: this.css('--muted'), usePointStyle: true, boxWidth: 8 } },
        tooltip: { ...base.plugins.tooltip, callbacks: { label: (c: { label: string; raw: unknown }) => ` ${c.label}: ${fmt.num(Number(c.raw))} gastos` } } } } };
  });

  paymentChart = computed<ChartConfiguration>(() => {
    const d = this.pays(); const base = this.base();
    return { type: 'doughnut', data: { labels: d.map(x => x.key), datasets: [{ label: 'Total', data: d.map(x => x.total), backgroundColor: this.seriesColors().slice(0, d.length), borderColor: this.css('--surface'), borderWidth: 3, hoverOffset: 10 }] },
      options: { animation: base.animation, cutout: '62%', plugins: { legend: { position: 'bottom', labels: { color: this.css('--muted'), usePointStyle: true, boxWidth: 8 } },
        tooltip: { ...base.plugins.tooltip, callbacks: { label: (c: { label: string; raw: unknown }) => ` ${c.label}: ${fmt.money(Number(c.raw), this.cur())}` } } } } };
  });

  pickDeptAvg(e: { index: number }) { const d = this.deptAvg()[e.index]; if (d) this.drillDepartment(d.key); }
  pickDeptTotal(e: { index: number }) { const d = this.deptTotal()[e.index]; if (d) this.drillDepartment(d.key); }
  pickCategory(e: { index: number }) { const d = this.cats()[e.index]; if (d) this.drillCategory(d.key); }
  pickPayment(e: { index: number }) { const d = this.pays()[e.index]; if (d) this.drillPayment(d.key); }
  pickState(e: { index: number }) { this.drillState((['aprobado', 'pendiente', 'rechazado'] as const)[e.index]); }

  private rowsOf(d: DimRow[], cells: (x: DimRow) => string[]): ChartTableRow[] { return d.map(x => ({ key: x.key, label: x.key, cells: cells(x) })); }
  deptAvgRows = computed(() => this.rowsOf(this.deptAvg(), x => [fmt.money(x.average, x.moneda), fmt.num(x.count)]));
  deptTotalRows = computed(() => this.rowsOf(this.deptTotal(), x => [fmt.money(x.total, x.moneda), fmt.num(x.count)]));
  catRows = computed(() => this.rowsOf(this.cats(), x => [fmt.money(x.total, x.moneda), fmt.num(x.count)]));
  stateRows = computed<ChartTableRow[]>(() => { const a = this.summary()?.approval; return [
    { key: 'aprobado', label: 'Aprobados', cells: [fmt.num(a?.approved ?? 0), fmt.pct(a?.approvedPct ?? 0)] },
    { key: 'pendiente', label: 'Pendientes', cells: [fmt.num(a?.pending ?? 0), fmt.pct(a?.pendingPct ?? 0)] },
    { key: 'rechazado', label: 'Rechazados', cells: [fmt.num(a?.rejected ?? 0), fmt.pct(a?.rejectedPct ?? 0)] }]; });
  openState(key: string) { this.drillState(key as Filters['estado']); }

  // ---- Smart Insights: the server classifies and ranks; here we only choose what to show ----
  insightTab = signal<'todos' | InsightCategory>('todos');
  insightsExpanded = signal(false);
  /** "All clear" results (no outliers, no duplicates) say the same thing as each other: they collapse into one line instead of one card each. */
  private isClear = (i: Insight) => i.data['count'] === 0 || i.data['groups'] === 0;
  clearInsights = computed(() => this.inScope().filter(this.isClear));
  /** Insights come one per currency; by default only the currency of the charts is listed (the same card three times is noise). "Todas las monedas" brings back the rest. */
  allCurrencies = signal(false);
  private inScope = computed(() => this.insights().filter(i => this.allCurrencies() || typeof i.data['moneda'] !== 'string' || i.data['moneda'] === this.cur()));
  private active = computed(() => this.inScope().filter(i => !this.isClear(i)));
  tabCount = (t: 'todos' | InsightCategory) => (t === 'todos' ? this.active().length : this.active().filter(i => i.category === t).length);
  tabInsights = computed(() => this.active().filter(i => this.insightTab() === 'todos' || i.category === this.insightTab()));
  shownInsights = computed(() => (this.insightsExpanded() ? this.tabInsights() : this.tabInsights().slice(0, INSIGHT_LIMIT)));
  hiddenCount = computed(() => Math.max(0, this.tabInsights().length - INSIGHT_LIMIT));
  setTab(t: 'todos' | InsightCategory) { this.insightTab.set(t); this.insightsExpanded.set(false); }
  toggleAllCurrencies() { this.allCurrencies.update(v => !v); this.insightsExpanded.set(false); }

  ranking(i: Insight) { return (i.data['ranking'] as { departamento: string; actual: number; previo: number; cambioPct: number }[] | undefined) ?? []; }
  shares(i: Insight) { return (i.data['shares'] as { clave: string; porcentaje: number }[] | undefined)?.slice(0, 4) ?? []; }
  insightIds(i: Insight): number[] {
    const d = i.data; const ids = (d['recordIds'] ?? d['oldestIds'] ?? (d['examples'] as number[][] | undefined)?.[0]) as number[] | undefined;
    return ids?.slice(0, 6) ?? [];
  }
  signedPct(v: number) { return (v > 0 ? '+' : '') + fmt.pct(v); }
  private s(i: Insight, k: string) { const v = i.data[k]; return typeof v === 'string' ? v : ''; }
  /** Growth rows open the department in the same 90-day window the figure was computed on. */
  openGrowth(i: Insight, departamento: string) { this.go({ departamentos: [departamento], monedas: [this.s(i, 'moneda')], from: this.s(i, 'desde'), to: this.s(i, 'hasta') }, true); }
  openShare(i: Insight, clave: string) {
    this.go({ [this.s(i, 'dimension') === 'departamento' ? 'departamentos' : 'categorias']: [clave], monedas: [this.s(i, 'moneda')] }, true);
  }
  openMonth(i: Insight) { this.go({ monedas: [this.s(i, 'moneda')], from: this.s(i, 'desde'), to: this.s(i, 'hasta') }, true); }
}
