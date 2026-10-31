import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, effect, input, viewChild } from '@angular/core';
import { Chart, ChartConfiguration, registerables } from 'chart.js';

Chart.register(...registerables);

/** Thin wrapper: creates the chart once, then updates data/options in place so transitions stay smooth. */
@Component({
  selector: 'app-chart', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<div class="box" [style.height.px]="height()"><canvas #c role="img" [attr.aria-label]="label()"></canvas></div>`,
  styles: ['.box{position:relative;width:100%}canvas{max-width:100%}'],
})
export class ChartComponent implements OnDestroy {
  config = input.required<ChartConfiguration>();
  height = input(260);
  label = input('Gráfica');
  private canvas = viewChild.required<ElementRef<HTMLCanvasElement>>('c');
  private chart?: Chart;

  constructor() {
    effect(() => {
      const cfg = this.config();
      const el = this.canvas().nativeElement;
      if (this.chart && (this.chart.config as { type?: string }).type === (cfg as { type?: string }).type) {
        this.chart.data = cfg.data;
        this.chart.options = cfg.options ?? {};
        this.chart.update();
      } else {
        this.chart?.destroy();
        this.chart = new Chart(el, { ...cfg, options: { responsive: true, maintainAspectRatio: false, ...cfg.options } });
      }
    });
  }
  ngOnDestroy() { this.chart?.destroy(); }
}
