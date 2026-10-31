import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { ChartConfiguration } from 'chart.js';
import { catchError, debounceTime, forkJoin, of, switchMap, tap } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { Insight, Summary } from '../../core/models';
import { DataVersion, FilterStore, ThemeService, fmt } from '../../core/state';
import { ChartComponent } from '../../shared/chart.component';
import { ExportMenuComponent } from '../../shared/export-menu.component';
import { FilterBarComponent } from '../../shared/filter-bar.component';
import { IconComponent } from '../../shared/icon.component';

const CUR_VAR: Record<string, string> = { CRC: '--c-crc', USD: '--c-usd', EUR: '--c-eur' };

@Component({
  selector: 'app-dashboard', standalone: true, imports: [FilterBarComponent, ChartComponent, IconComponent, RouterLink, ExportMenuComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './dashboard.component.html', styleUrl: './dashboard.component.scss',
})
export class DashboardComponent {
  private api = inject(ApiService);
  private store = inject(FilterStore);
  private dv = inject(DataVersion);
  private theme = inject(ThemeService);
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

  summary = signal<Summary | null>(null);
  insights = signal<Insight[]>([]);
  loading = signal(true);
  error = signal<string | null>(null);
  granularity = signal<'month' | 'week'>('month');
  curChoice = signal<string | null>(null);

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
  approvalClass(p: number) { return p >= 50 ? 'ok' : 'warn'; }

  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  private base(): any {
    const text = this.css('--muted'), grid = this.css('--grid');
    return {
      animation: { duration: 500 },
      plugins: { legend: { labels: { color: text, usePointStyle: true, boxWidth: 8, font: { family: 'Inter', size: 12 } } },
        tooltip: { backgroundColor: this.css('--surface-2'), titleColor: this.css('--text'), bodyColor: this.css('--text'), borderColor: this.css('--border-2'), borderWidth: 1, padding: 10, cornerRadius: 10 } },
      scales: { x: { ticks: { color: text, maxRotation: 0, autoSkipPadding: 14 }, grid: { display: false }, border: { display: false } },
        y: { beginAtZero: true, ticks: { color: text, callback: (v: unknown) => fmt.money(Number(v), this.cur(), true) }, grid: { color: grid }, border: { display: false } } },
    };
  }
  private seriesColors() { return ['--s1', '--s2', '--s3', '--s4', '--s5', '--s6', '--s7', '--s8', '--s9', '--s10'].map(v => this.css(v)); }
  private moneyTip = () => ({ callbacks: { label: (c: { dataset: { label?: string }; parsed: { y: number; x: number }; raw: unknown }) => ` ${c.dataset.label ?? ''}: ${fmt.money(Number(c.raw), this.cur())}` } });

  lineChart = computed<ChartConfiguration>(() => {
    const s = this.summary(); const cur = this.cur();
    const pts = (s?.series ?? []).filter(p => p.moneda === cur);
    const color = this.curColor();
    const base = this.base();
    return {
      type: 'line',
      data: { labels: pts.map(p => p.period), datasets: [
        { label: 'Aprobado', data: pts.map(p => p.approvedTotal), borderColor: color, backgroundColor: color + '55', fill: true, tension: .35, pointRadius: 0, pointHoverRadius: 5, borderWidth: 2 },
        { label: 'Pendiente', data: pts.map(p => p.pendingTotal), borderColor: this.css('--muted'), backgroundColor: this.css('--muted') + '33', fill: true, tension: .35, pointRadius: 0, pointHoverRadius: 5, borderWidth: 1.5, borderDash: [5, 4] },
      ] },
      options: { ...base, interaction: { mode: 'index', intersect: false }, scales: { ...base.scales, y: { ...base.scales['y'], stacked: true } },
        plugins: { ...base.plugins, tooltip: { ...base.plugins.tooltip, ...this.moneyTip() } } },
    };
  });

  private dimension(stats: { key: string; moneda: string; total: number; average: number }[] | undefined, field: 'total' | 'average', limit = 10) {
    return (stats ?? []).filter(x => x.moneda === this.cur()).sort((a, b) => b[field] - a[field]).slice(0, limit);
  }

  avgDeptChart = computed<ChartConfiguration>(() => {
    const d = this.dimension(this.summary()?.byDepartment, 'average'); const base = this.base();
    return { type: 'bar', data: { labels: d.map(x => x.key), datasets: [{ label: 'Promedio por gasto', data: d.map(x => x.average), backgroundColor: this.curColor(), borderRadius: 7, maxBarThickness: 44 }] },
      options: { ...base, plugins: { ...base.plugins, legend: { display: false }, tooltip: { ...base.plugins.tooltip, ...this.moneyTip() } },
        scales: { ...base.scales, x: { ...base.scales['x'], ticks: { color: this.css('--muted'), autoSkip: false, maxRotation: 35, minRotation: 0 } } } } };
  });

  deptTotalChart = computed<ChartConfiguration>(() => {
    const d = this.dimension(this.summary()?.byDepartment, 'total'); const base = this.base();
    return { type: 'bar', data: { labels: d.map(x => x.key), datasets: [{ label: 'Total', data: d.map(x => x.total), backgroundColor: this.seriesColors().slice(0, d.length), borderRadius: 7, maxBarThickness: 26 }] },
      options: { ...base, indexAxis: 'y', plugins: { ...base.plugins, legend: { display: false }, tooltip: { ...base.plugins.tooltip, ...this.moneyTip() } },
        scales: { x: { ...base.scales['y'], ticks: { color: this.css('--muted'), callback: (v: unknown) => fmt.money(Number(v), this.cur(), true) } }, y: { ...base.scales['x'], ticks: { color: this.css('--text') } } } } };
  });

  categoryChart = computed<ChartConfiguration>(() => {
    const d = this.dimension(this.summary()?.byCategory, 'total', 8); const base = this.base();
    return { type: 'bar', data: { labels: d.map(x => x.key), datasets: [{ label: 'Total', data: d.map(x => x.total), backgroundColor: this.curColor(), borderRadius: 7, maxBarThickness: 22 }] },
      options: { ...base, indexAxis: 'y', plugins: { ...base.plugins, legend: { display: false }, tooltip: { ...base.plugins.tooltip, ...this.moneyTip() } },
        scales: { x: { ...base.scales['y'], ticks: { color: this.css('--muted'), callback: (v: unknown) => fmt.money(Number(v), this.cur(), true) } }, y: { ...base.scales['x'], ticks: { color: this.css('--text') } } } } };
  });

  approvalChart = computed<ChartConfiguration>(() => {
    const a = this.summary()?.approval; const base = this.base();
    return { type: 'doughnut', data: { labels: ['Aprobados', 'Pendientes', 'Rechazados'], datasets: [{ data: [a?.approved ?? 0, a?.pending ?? 0, a?.rejected ?? 0], backgroundColor: [this.css('--accent'), this.css('--warn'), this.css('--bad')], borderColor: this.css('--surface'), borderWidth: 3, hoverOffset: 6 }] },
      options: { animation: base.animation, cutout: '72%', plugins: { legend: { position: 'bottom', labels: { color: this.css('--muted'), usePointStyle: true, boxWidth: 8 } }, tooltip: base.plugins.tooltip } } };
  });

  paymentChart = computed<ChartConfiguration>(() => {
    const d = this.dimension(this.summary()?.byPaymentMethod, 'total'); const base = this.base();
    return { type: 'doughnut', data: { labels: d.map(x => x.key), datasets: [{ label: 'Total', data: d.map(x => x.total), backgroundColor: this.seriesColors().slice(0, d.length), borderColor: this.css('--surface'), borderWidth: 3, hoverOffset: 6 }] },
      options: { animation: base.animation, cutout: '62%', plugins: { legend: { position: 'bottom', labels: { color: this.css('--muted'), usePointStyle: true, boxWidth: 8 } },
        tooltip: { ...base.plugins.tooltip, callbacks: { label: (c: { label: string; raw: unknown }) => ` ${c.label}: ${fmt.money(Number(c.raw), this.cur())}` } } } } };
  });

  ranking(i: Insight) { return (i.data['ranking'] as { departamento: string; actual: number; previo: number; cambioPct: number }[] | undefined) ?? []; }
  shares(i: Insight) { return (i.data['shares'] as { clave: string; porcentaje: number }[] | undefined)?.slice(0, 4) ?? []; }
  insightIds(i: Insight): number[] {
    const d = i.data; const ids = (d['recordIds'] ?? d['oldestIds'] ?? (d['examples'] as number[][] | undefined)?.[0]) as number[] | undefined;
    return ids?.slice(0, 6) ?? [];
  }
}
