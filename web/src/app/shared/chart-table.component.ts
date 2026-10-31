import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

export interface ChartTableRow { key: string; label: string; cells: string[] }

/** Keyboard- and screen-reader-friendly twin of a clickable chart: the same figures as a table, each row with a button that opens the related expenses. */
@Component({
  selector: 'app-chart-table', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <details class="alt">
      <summary>Ver como tabla</summary>
      <div class="scroll">
        <table class="mini">
          <caption class="sr">{{ caption() }}</caption>
          <thead><tr><th scope="col">{{ head() }}</th>@for (c of columns(); track c) { <th scope="col" class="r">{{ c }}</th> }<th scope="col" class="r"><span class="sr">Acción</span></th></tr></thead>
          <tbody>
            @for (r of rows(); track r.key) {
              <tr><th scope="row">{{ r.label }}</th>@for (c of r.cells; track $index) { <td class="r">{{ c }}</td> }
                <td class="r"><button type="button" class="btn sm" (click)="open.emit(r.key)" [attr.aria-label]="'Ver gastos de ' + r.label">Ver gastos</button></td></tr>
            }
          </tbody>
        </table>
      </div>
    </details>`,
  styles: [`
    .alt { margin-top: 10px; font-size: 12.5px; }
    summary { cursor: pointer; color: var(--muted); width: fit-content; border-radius: 6px; padding: 2px 4px; &:hover { color: var(--text); } &:focus-visible { outline: 2px solid var(--accent); outline-offset: 2px; } }
    .scroll { max-height: 280px; overflow: auto; margin-top: 6px; }
    .mini { width: 100%; border-collapse: collapse; }
    th, td { padding: 6px 8px; border-top: 1px solid var(--border); text-align: left; font-weight: 400; font-variant-numeric: tabular-nums; }
    thead th { color: var(--muted); font-size: 11.5px; text-transform: uppercase; letter-spacing: .05em; border-top: 0; position: sticky; top: 0; background: var(--surface); }
    .r { text-align: right; } tbody th { font-weight: 500; }
    .sr { position: absolute; width: 1px; height: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
  `],
})
export class ChartTableComponent {
  caption = input.required<string>();
  head = input('Elemento');
  columns = input<string[]>([]);
  rows = input<ChartTableRow[]>([]);
  open = output<string>();
}
