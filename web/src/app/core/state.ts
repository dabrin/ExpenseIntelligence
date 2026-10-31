import { Injectable, computed, effect, signal } from '@angular/core';
import { EMPTY_FILTERS, Filters } from './models';

/** Single source of truth for the combinable filters; shared by the dashboard and the expenses table. */
@Injectable({ providedIn: 'root' })
export class FilterStore {
  readonly filters = signal<Filters>({ ...EMPTY_FILTERS });
  readonly activeCount = computed(() => {
    const f = this.filters();
    return (f.from || f.to ? 1 : 0) + (f.departamentos.length ? 1 : 0) + (f.categorias.length ? 1 : 0) + (f.monedas.length ? 1 : 0)
      + (f.metodosPago.length ? 1 : 0) + (f.estado ? 1 : 0) + (f.q.trim() ? 1 : 0) + (f.ids.length ? 1 : 0);
  });
  patch(p: Partial<Filters>) { this.filters.update(f => ({ ...f, ...p })); }
  reset() { this.filters.set({ ...EMPTY_FILTERS }); }
}

@Injectable({ providedIn: 'root' })
export class ThemeService {
  readonly theme = signal<'dark' | 'light'>((document.documentElement.dataset['theme'] as 'dark' | 'light') ?? 'dark');
  constructor() {
    effect(() => {
      const t = this.theme();
      document.documentElement.dataset['theme'] = t;
      try { localStorage.setItem('ei-theme', t); } catch { /* storage unavailable */ }
    });
  }
  toggle() { this.theme.update(t => (t === 'dark' ? 'light' : 'dark')); }
}

export interface Toast { id: number; kind: 'ok' | 'error' | 'info'; text: string; action?: { label: string; run: () => void } }

@Injectable({ providedIn: 'root' })
export class ToastService {
  readonly toasts = signal<Toast[]>([]);
  private n = 0;
  show(kind: Toast['kind'], text: string, action?: Toast['action'], ms = 5000) {
    const id = ++this.n;
    this.toasts.update(t => [...t, { id, kind, text, action }]);
    setTimeout(() => this.dismiss(id), ms);
  }
  dismiss(id: number) { this.toasts.update(t => t.filter(x => x.id !== id)); }
}

/** Bumped after every confirmed write so views reload (dashboard, table, doctor...). */
@Injectable({ providedIn: 'root' })
export class DataVersion {
  readonly n = signal(0);
  bump() { this.n.update(v => v + 1); }
}

const MONTHS = ['ene', 'feb', 'mar', 'abr', 'may', 'jun', 'jul', 'ago', 'sep', 'oct', 'nov', 'dic'];
const PCT = new Intl.NumberFormat('es-CR', { minimumFractionDigits: 1, maximumFractionDigits: 1 });
const iso = (s: string) => { const [y, m, d] = s.split('-').map(Number); return { y, m, d }; };

/** The single place where numbers, money, percentages and dates are formatted (es-CR). Money is always shown with its own currency; currencies are never combined. */
export const fmt = {
  /** Never throws: imported files can carry currency codes Intl rejects (e.g. "???"); those are shown as plain "CODE amount". */
  money(v: number, cur: string, compact = false) {
    try {
      return new Intl.NumberFormat('es-CR', { style: 'currency', currency: cur, notation: compact ? 'compact' : 'standard', maximumFractionDigits: compact ? 1 : 2 }).format(v);
    } catch {
      const n = new Intl.NumberFormat('es-CR', { notation: compact ? 'compact' : 'standard', maximumFractionDigits: compact ? 1 : 2 }).format(v);
      return `${cur || '—'} ${n}`;
    }
  },
  num(v: number) { return new Intl.NumberFormat('es-CR').format(v); },
  /** Percentages always carry one decimal and Spanish separators: "62,5 %". */
  pct(v: number) { return `${PCT.format(v)} %`; },
  /** "2026-03" -> "mar 2026"; week/day "2026-03-02" -> "2 mar 2026". */
  period(p: string, granularity: string) {
    const { y, m, d } = iso(p);
    return granularity === 'month' ? `${MONTHS[m - 1]} ${y}` : `${d} ${MONTHS[m - 1]} ${y}`;
  },
  /** Calendar range of a bucket for tooltips and tables: "mar 2026" for a month, "2 – 8 mar 2026" for a week. */
  range(start: string, end: string, granularity: string) {
    if (granularity === 'month') return fmt.period(start.slice(0, 7), 'month');
    const a = iso(start), b = iso(end);
    return a.m === b.m && a.y === b.y ? `${a.d} – ${b.d} ${MONTHS[b.m - 1]} ${b.y}` : `${a.d} ${MONTHS[a.m - 1]} – ${b.d} ${MONTHS[b.m - 1]} ${b.y}`;
  },
};
