import { ChangeDetectionStrategy, Component, inject, input, signal } from '@angular/core';
import { toParams } from '../core/api.service';
import { FilterStore, ThemeService, ToastService } from '../core/state';
import { IconComponent } from './icon.component';

/** Exports what the shared filters currently select. Excel / CSV are produced by the server; the PDF is the browser's "save as PDF" of the printable dashboard. */
@Component({
  selector: 'app-export-menu', standalone: true, imports: [IconComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="wrap">
      <button type="button" class="btn" (click)="toggle($event)" [attr.aria-expanded]="open()" aria-haspopup="menu"><app-icon name="download" /> Exportar</button>
      @if (open()) {
        <div class="shield" (mousedown)="open.set(false)"></div>
        <div class="menu card" [class.end]="alignEnd()" role="menu">
          <p class="muted small">Se exporta lo que filtra el panel de filtros ({{ store.activeCount() ? store.activeCount() + ' filtros activos' : 'sin filtros: todos los gastos' }}).</p>
          <button type="button" role="menuitem" (click)="download('xlsx')"><strong>Excel (.xlsx)</strong><span>Hoja de datos con filtros y hoja de resumen por moneda</span></button>
          <button type="button" role="menuitem" (click)="download('csv')"><strong>CSV</strong><span>Datos planos, abre bien en Excel</span></button>
          @if (pdf()) { <button type="button" role="menuitem" (click)="printPdf()"><strong>PDF con gráficos</strong><span>Imprime el dashboard; elige «Guardar como PDF»</span></button> }
        </div>
      }
    </div>`,
  styles: [`
    .wrap { position: relative; } .small { font-size: 12.5px; margin: 0 0 8px; }
    .shield { position: fixed; inset: 0; z-index: 30; }
    .menu { position: absolute; left: 0; &.end { left: auto; right: 0; } top: calc(100% + 6px); z-index: 31; width: min(320px, 86vw); padding: 12px; display: grid; gap: 4px; }
    .menu button { display: grid; gap: 2px; text-align: left; padding: 9px 10px; border-radius: 10px; border: 0; background: transparent; color: var(--text); cursor: pointer; font: inherit;
      span { font-size: 12.5px; color: var(--muted); } &:hover { background: var(--surface-2); } }
  `],
})
export class ExportMenuComponent {
  store = inject(FilterStore);
  private toast = inject(ToastService);
  private theme = inject(ThemeService);
  pdf = input(false);
  open = signal(false);
  alignEnd = signal(false);
  /** Open towards the side that has room: the button sits at the left on narrow screens and at the right on wide ones. */
  toggle(ev: MouseEvent) {
    const r = (ev.currentTarget as HTMLElement).getBoundingClientRect();
    this.alignEnd.set(r.right - 330 > 0 && r.left > window.innerWidth / 2);
    this.open.update(o => !o);
  }

  download(format: 'xlsx' | 'csv') {
    this.open.set(false);
    const a = document.createElement('a');
    a.href = '/api/expenses/export?' + toParams(this.store.filters(), { format }).toString();
    document.body.appendChild(a); a.click(); a.remove();
    this.toast.show('info', 'Preparando la exportación con los filtros actuales…');
  }

  /** Paper is white: print in the light theme (charts re-render with it), then restore the user's theme. */
  printPdf() {
    this.open.set(false);
    const prev = this.theme.theme();
    const restore = () => { window.removeEventListener('afterprint', restore); if (this.theme.theme() !== prev) this.theme.theme.set(prev); };
    window.addEventListener('afterprint', restore);
    if (prev !== 'light') this.theme.theme.set('light');
    setTimeout(() => window.print(), prev !== 'light' ? 700 : 100);
  }
}
