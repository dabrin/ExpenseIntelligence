import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, effect, input, output, viewChild } from '@angular/core';
import { Chart, ChartConfiguration, ChartOptions, registerables } from 'chart.js';

Chart.register(...registerables);

/** Thin wrapper: creates the chart once, then updates data/options in place so transitions stay smooth.
 *  With `interactive`, a click on a bar / slice / point emits `pick` and the cursor turns into a pointer over clickable elements. */
@Component({
  selector: 'app-chart', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="box" [style.height.px]="height()"><canvas #c role="img" [attr.aria-label]="label()"></canvas></div>`,
  styles: ['.box{position:relative;width:100%}canvas{max-width:100%}'],
})
export class ChartComponent implements OnDestroy {
  config = input.required<ChartConfiguration>();
  height = input(260);
  label = input('Gráfica');
  interactive = input(false);
  pick = output<{ index: number; datasetIndex: number }>();
  private canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('c');
  private chart?: Chart;

  constructor() {
    effect(() => {
      const cfg = this.config();
      const el = this.canvas().nativeElement;
      if (this.chart && (this.chart.config as { type?: string }).type === (cfg as { type?: string }).type) {
        this.chart.data = cfg.data;
        this.chart.options = this.options(cfg);
        this.chart.update();
      } else {
        this.chart?.destroy();
        this.chart = new Chart(el, { ...cfg, options: this.options(cfg) });
      }
    });
  }

  private options(cfg: ChartConfiguration): ChartOptions {
    const o = { responsive: true, maintainAspectRatio: false, ...cfg.options } as ChartOptions;
    if (!this.interactive()) return o;
    o.onClick = (_e, els) => { if (els.length) this.pick.emit({ index: els[0].index, datasetIndex: els[0].datasetIndex }); };
    o.onHover = (e, els) => { const t = e.native?.target as HTMLElement | undefined; if (t) t.style.cursor = els.length ? 'pointer' : 'default'; };
    return o;
  }
  ngOnDestroy() { this.chart?.destroy(); }
}
