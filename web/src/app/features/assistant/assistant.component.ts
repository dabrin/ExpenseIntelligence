import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { ChartConfiguration } from 'chart.js';
import { AssistantAnswer, AssistantVisual } from '../../core/models';
import { fmt } from '../../core/state';
import { ChartComponent } from '../../shared/chart.component';
import { IconComponent } from '../../shared/icon.component';

interface Msg { id: number; who: 'me' | 'bot'; full: string; shown: string; answer?: AssistantAnswer; error?: boolean; done: boolean }

@Component({
  selector: 'app-assistant', standalone: true, imports: [FormsModule, IconComponent, ChartComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <header class="head rise"><div class="row"><span class="avatar"><app-icon name="chat" [size]="18" /></span><div><h1>Prestamito</h1>
    <p class="muted">Pregúntame con tus palabras: <em>«¿cuánto gastó Ventas en USD en marzo?»</em>. @if (status().llm) { Respondo con IA (<span class="mono">{{ status().model }}</span>) usando solo los datos de este sistema: no invento cifras y rechazo lo que no tenga que ver con los gastos. } @else { Entiendo departamentos, categorías, monedas y meses, y recuerdo la pregunta anterior. Respondo con reglas: consulto tus datos y no invento cifras. }</p></div></div></header>

  <section class="card chat">
    <div class="log" #log aria-live="polite">
      @for (m of msgs(); track m.id) {
        <div class="msg rise" [class.me]="m.who === 'me'" [class.err]="m.error">
          @if (m.who === 'bot') { <span class="avatar sm"><app-icon name="chat" [size]="14" /></span> }
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
                  @else { <app-chart [config]="chartConfig(v)" [height]="v.kind === 'pie' ? 280 : 250" [label]="v.title" /> }
                </div>
              }
              @if (a.source !== 'n/a') { <div class="meta"><span class="badge plain">Periodo: {{ a.period }}</span><span class="badge plain mono">Fuente: {{ a.source }}</span></div> }
              @if (a.suggestions.length && $last) { <div class="chips rise">@for (s of a.suggestions; track s) { <button type="button" class="chip" (click)="ask(s)">{{ s }}</button> }</div> }
            }
          </div>
        </div>
      }
      @if (busy()) { <div class="msg"><span class="avatar sm"><app-icon name="chat" [size]="14" /></span><div class="bubble dots"><i></i><i></i><i></i></div></div> }
    </div>
    <form class="compose" (ngSubmit)="ask(q)">
      <input name="q" [(ngModel)]="q" placeholder="Escribe tu pregunta…  ej. gastos de Marketing en EUR este año" aria-label="Pregunta" autocomplete="off" />
      <button class="btn primary" type="submit" [disabled]="busy() || !q.trim()"><app-icon name="send" /> Preguntar</button>
    </form>
  </section>`,
  styles: [`
    .head { margin-bottom: 18px; p { margin: 4px 0 0; } }
    .avatar { width: 40px; height: 40px; border-radius: 12px; display: grid; place-items: center; color: #fff; background: linear-gradient(135deg, var(--green), var(--green-2)); flex: none; &.sm { width: 28px; height: 28px; border-radius: 9px; margin-top: 2px; } }
    .chat { display: grid; grid-template-rows: 1fr auto; min-height: 62vh; padding: 0; overflow: hidden; }
    .log { padding: 20px; display: grid; gap: 16px; align-content: start; overflow: auto; max-height: 66vh; }
    .msg { display: flex; gap: 10px; align-items: flex-start; max-width: 100%; &.me { justify-content: flex-end; .bubble { background: var(--accent); color: #fff; border-color: transparent; } } &.err .bubble { border-color: var(--bad); } }
    .col { display: grid; gap: 8px; justify-items: start; min-width: 0; }
    .bubble { padding: 10px 14px; border-radius: 14px; background: var(--surface-2); border: 1px solid var(--border); max-width: min(740px, 100%); white-space: pre-wrap; }
    .caret { display: inline-block; width: 2px; height: 1em; background: var(--accent); margin-left: 2px; vertical-align: -2px; animation: blink 1s steps(1) infinite; }
    @keyframes blink { 50% { opacity: 0; } }
    .dots { display: inline-flex; gap: 5px; padding: 14px; i { width: 7px; height: 7px; border-radius: 50%; background: var(--muted); animation: bounce 1.1s infinite; &:nth-child(2) { animation-delay: .15s; } &:nth-child(3) { animation-delay: .3s; } } }
    @keyframes bounce { 0%, 60%, 100% { transform: none; opacity: .5; } 30% { transform: translateY(-5px); opacity: 1; } }
    .visual { width: min(740px, 100%); padding: 14px; background: var(--surface); border: 1px solid var(--border); border-radius: 12px; strong { display: block; margin-bottom: 8px; } }
    .col .table-wrap { max-width: 100%; } .meta, .chips { display: flex; gap: 6px; flex-wrap: wrap; }
    .chips { margin-top: 2px; }
    .compose { display: flex; gap: 10px; padding: 14px; border-top: 1px solid var(--border); background: var(--bg-2); input { flex: 1; height: 40px; } }
  `],
})
export class AssistantComponent implements OnDestroy {
  private api = inject(ApiService);
  private log = viewChild<ElementRef<HTMLElement>>('log');
  examples = toSignal(this.api.examples().pipe(catchError(() => of([] as string[]))), { initialValue: [] as string[] });
  status = toSignal(this.api.assistantStatus().pipe(catchError(() => of({ llm: false, model: null }))), { initialValue: { llm: false, model: null as string | null } });
  msgs = signal<Msg[]>([]);
  busy = signal(false);
  q = '';
  private n = 0;
  private timer?: ReturnType<typeof setInterval>;
  private lastIntent?: string;
  private lastQuestion?: string;

  constructor() {
    // the assistant opens the conversation instead of showing an empty box
    this.deliver({
      mode: 'rules', intent: 'greeting', answer: '¡Hola! Soy Prestamito, el asistente de Insta Expenses. Pregúntame por gastos, pendientes, rankings o duplicados, y si quieres acota por departamento, categoría, moneda o mes. Por ejemplo:',
      columns: [], rows: [], period: '—', source: 'n/a', scope: [],
      suggestions: ['¿Qué departamento gastó más este mes?', '¿Qué gastos siguen pendientes?', '¿Cuál es el total gastado por moneda?', '¿Existen transacciones potencialmente duplicadas?', '¿Cuáles son los gastos más grandes?'],
    });
  }
  ngOnDestroy() { clearInterval(this.timer); }

  private cfgCache = new WeakMap<AssistantVisual, ChartConfiguration>();
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

  ask(text: string) {
    const t = text.trim(); if (!t || this.busy()) return;
    // Refusals (and the question that caused them) stay out of the history: otherwise the model copies its own refusal on the next turn.
    const log = this.msgs().filter(m => m.done && m.full && !m.error);
    const history = log.filter((m, i) => m.answer?.intent !== 'off-topic' && !(m.who === 'me' && log[i + 1]?.answer?.intent === 'off-topic'))
      .slice(-8).map(m => ({ role: m.who === 'me' ? 'user' as const : 'assistant' as const, content: m.full }));
    this.q = ''; this.busy.set(true);
    this.msgs.update(m => [...m, { id: ++this.n, who: 'me', full: t, shown: t, done: true }]);
    this.scroll();
    const started = Date.now();
    this.api.ask(t, this.lastIntent, this.lastQuestion, history).subscribe({
      next: a => setTimeout(() => { this.busy.set(false); if (!['greeting', 'help', 'thanks', 'empty', 'llm', 'chat', 'off-topic'].includes(a.intent)) { this.lastIntent = a.intent; this.lastQuestion = t; } this.deliver(a); }, Math.max(0, 550 - (Date.now() - started))),
      error: e => { this.busy.set(false); this.msgs.update(m => [...m, { id: ++this.n, who: 'bot', full: asProblem(e).title, shown: asProblem(e).title, error: true, done: true }]); },
    });
  }

  /** Typewriter reveal: the answer's table/chips only appear once the text has been "said". */
  private deliver(a: AssistantAnswer) {
    clearInterval(this.timer);
    const id = ++this.n;
    this.msgs.update(m => [...m, { id, who: 'bot', full: a.answer, shown: '', answer: a, done: false }]);
    let i = 0;
    this.timer = setInterval(() => {
      i = Math.min(a.answer.length, i + 3);
      this.msgs.update(ms => ms.map(x => (x.id === id ? { ...x, shown: a.answer.slice(0, i), done: i >= a.answer.length } : x)));
      this.scroll();
      if (i >= a.answer.length) clearInterval(this.timer);
    }, 14);
  }

  private scroll() { setTimeout(() => { const el = this.log()?.nativeElement; if (el) el.scrollTop = el.scrollHeight; }); }
}
