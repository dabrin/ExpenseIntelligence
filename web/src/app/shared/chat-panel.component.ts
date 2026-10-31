import { ChangeDetectionStrategy, Component, ElementRef, effect, inject, input, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ChartConfiguration } from 'chart.js';
import { AssistantChat } from '../core/assistant-chat.service';
import { AssistantVisual } from '../core/models';
import { fmt } from '../core/state';
import { ChartComponent } from './chart.component';
import { IconComponent } from './icon.component';
import { PrestamitoAvatarComponent } from './prestamito-avatar.component';

/** The conversation itself (messages, charts, tables, suggestions, navigation buttons and the input). Used by the floating chat and by the /asistente page. */
@Component({
  selector: 'app-chat-panel', standalone: true, imports: [FormsModule, IconComponent, ChartComponent, PrestamitoAvatarComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="log" #log role="log" aria-live="polite" aria-label="Conversación con Prestamito">
      @for (m of chat.msgs(); track m.id) {
        <div class="msg rise" [class.me]="m.who === 'me'" [class.err]="m.error">
          @if (m.who === 'bot') { <app-prestamito-avatar [size]="30" /> }
          <div class="col">
            <div class="bubble">{{ m.shown }}@if (!m.done) { <span class="caret"></span> }</div>
            @if (m.done && m.answer; as a) {
              @if (a.scope.length) { <div class="meta">@for (s of a.scope; track s) { <span class="badge plain">{{ s }}</span> }</div> }
              @if (a.rows.length) { <div class="table-wrap rise"><table class="t" style="min-width:0"><thead><tr>@for (c of a.columns; track c) { <th>{{ c }}</th> }</tr></thead><tbody>
                @for (r of a.rows; track $index) { <tr style="cursor:default">@for (c of r; track $index) { <td>{{ c }}</td> }</tr> }</tbody></table></div> }
              @for (v of a.visuals ?? []; track $index) {
                <div class="visual rise"><strong>{{ v.title }}</strong>
                  @if (v.kind === 'table') { <div class="table-wrap"><table class="t" style="min-width:0"><thead><tr>@for (c of v.columns; track c) { <th>{{ c }}</th> }</tr></thead><tbody>
                    @for (r of v.rows; track $index) { <tr style="cursor:default">@for (c of r; track $index) { <td>{{ c }}</td> }</tr> }</tbody></table></div> }
                  @else { <app-chart [config]="chartConfig(v)" [height]="v.kind === 'pie' ? 240 : 210" [label]="v.title" /> }
                </div>
              }
              @for (act of a.actions ?? []; track $index) {
                <button type="button" class="btn primary sm go rise" (click)="chat.go(act)"><app-icon name="send" [size]="14" /> Abrir {{ act.label }}</button>
              }
              @if (a.source !== 'n/a') { <div class="meta"><span class="badge plain">Periodo: {{ a.period }}</span><span class="badge plain mono">Fuente: {{ a.source }}</span></div> }
              @if (a.suggestions.length && $last) { <div class="chips rise">@for (s of a.suggestions; track s) { <button type="button" class="chip" (click)="chat.ask(s)">{{ s }}</button> }</div> }
            }
          </div>
        </div>
      }
      @if (chat.busy()) { <div class="msg"><app-prestamito-avatar [size]="30" /><div class="bubble dots" aria-label="Prestamito está escribiendo"><i></i><i></i><i></i></div></div> }
    </div>
    <form class="compose" (ngSubmit)="send()">
      <input name="q" [(ngModel)]="q" [placeholder]="placeholder()" aria-label="Pregunta" autocomplete="off" />
      <button class="btn primary" type="submit" [disabled]="chat.busy() || !q.trim()" aria-label="Enviar"><app-icon name="send" /> <span class="lbl">Preguntar</span></button>
    </form>`,
  styles: [`
    :host { display: grid; grid-template-rows: minmax(0, 1fr) auto; min-height: 0; height: 100%; }
    .log { padding: 16px; display: grid; gap: 14px; align-content: start; overflow: auto; min-height: 0; }
    .msg { display: flex; gap: 10px; align-items: flex-start; max-width: 100%; &.me { justify-content: flex-end; .bubble { background: var(--accent); color: #fff; border-color: transparent; } } &.err .bubble { border-color: var(--bad); } }
    .col { display: grid; gap: 8px; justify-items: start; min-width: 0; }
    .bubble { padding: 10px 14px; border-radius: 14px; background: var(--surface-2); border: 1px solid var(--border); max-width: min(740px, 100%); white-space: pre-wrap; overflow-wrap: anywhere; }
    .caret { display: inline-block; width: 2px; height: 1em; background: var(--accent); margin-left: 2px; vertical-align: -2px; animation: blink 1s steps(1) infinite; }
    @keyframes blink { 50% { opacity: 0; } }
    .dots { display: inline-flex; gap: 5px; padding: 14px; i { width: 7px; height: 7px; border-radius: 50%; background: var(--muted); animation: bounce 1.1s infinite; &:nth-child(2) { animation-delay: .15s; } &:nth-child(3) { animation-delay: .3s; } } }
    @keyframes bounce { 0%, 60%, 100% { transform: none; opacity: .5; } 30% { transform: translateY(-5px); opacity: 1; } }
    .visual { width: min(740px, 100%); padding: 12px; background: var(--surface); border: 1px solid var(--border); border-radius: 12px; strong { display: block; margin-bottom: 8px; } }
    .col .table-wrap { max-width: 100%; } .meta, .chips { display: flex; gap: 6px; flex-wrap: wrap; }
    .chips { margin-top: 2px; } .go { width: fit-content; }
    .compose { display: flex; gap: 10px; padding: 12px; border-top: 1px solid var(--border); background: var(--bg-2); input { flex: 1; min-width: 0; height: 40px; } }
    @media (max-width: 480px) { .lbl { display: none; } }
  `],
})
export class ChatPanelComponent {
  chat = inject(AssistantChat);
  placeholder = input('Escribe tu pregunta…  ej. gastos de Marketing en EUR este año');
  q = '';
  private log = viewChild<ElementRef<HTMLElement>>('log');
  private cfgCache = new WeakMap<AssistantVisual, ChartConfiguration>();

  constructor() {
    // follow the conversation: new messages and the typewriter keep the latest line in view
    effect(() => { this.chat.msgs(); this.chat.busy(); setTimeout(() => { const el = this.log()?.nativeElement; if (el) el.scrollTop = el.scrollHeight; }); });
  }

  send() { const t = this.q; this.q = ''; this.chat.ask(t); }

  private css(v: string) { return getComputedStyle(document.documentElement).getPropertyValue(v).trim(); }

  /** Builds (once per visual) the Chart.js config for a chart the assistant chose to show; the numbers come from the server. */
  chartConfig(v: AssistantVisual): ChartConfiguration {
    const cached = this.cfgCache.get(v); if (cached) return cached;
    const palette = ['--s1', '--s2', '--s3', '--s4', '--s5', '--s6', '--s7', '--s8', '--s9', '--s10'].map(x => this.css(x));
    const text = this.css('--muted'), grid = this.css('--border');
    const show = (n: number) => v.currency ? fmt.money(n, v.currency, true) : fmt.num(n);
    const full = (n: number) => v.currency ? fmt.money(n, v.currency) : fmt.num(n);
    const data = v.series[0]?.values ?? [], name = v.series[0]?.name ?? '';
    const tooltip = { callbacks: { label: (c: { raw: unknown; label?: string }) => ` ${c.label ?? name}: ${full(Number(c.raw))}` } };
    let cfg: ChartConfiguration;
    if (v.kind === 'pie') {
      cfg = { type: 'doughnut', data: { labels: v.labels, datasets: [{ data, backgroundColor: palette.slice(0, data.length), borderWidth: 0 }] },
        options: { plugins: { legend: { position: 'bottom', labels: { color: text } }, tooltip } } };
    } else if (v.kind === 'line') {
      cfg = { type: 'line', data: { labels: v.labels, datasets: [{ label: name, data, borderColor: palette[0], backgroundColor: palette[0] + '33', fill: true, tension: .3, pointRadius: 3 }] },
        options: { plugins: { legend: { display: false }, tooltip }, scales: { x: { ticks: { color: text, maxRotation: 0, autoSkipPadding: 14 }, grid: { display: false } }, y: { beginAtZero: true, ticks: { color: text, callback: (n: unknown) => show(Number(n)) }, grid: { color: grid } } } } };
    } else {
      cfg = { type: 'bar', data: { labels: v.labels, datasets: [{ label: name, data, backgroundColor: palette[0], borderRadius: 7, maxBarThickness: 40 }] },
        options: { plugins: { legend: { display: false }, tooltip }, scales: { x: { ticks: { color: text, maxRotation: 0, autoSkipPadding: 8 }, grid: { display: false } }, y: { beginAtZero: true, ticks: { color: text, callback: (n: unknown) => show(Number(n)) }, grid: { color: grid } } } } };
    }
    this.cfgCache.set(v, cfg);
    return cfg;
  }
}
