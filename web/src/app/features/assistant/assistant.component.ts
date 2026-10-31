import { ChangeDetectionStrategy, Component, ElementRef, OnDestroy, inject, signal, viewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';
import { ApiService, asProblem } from '../../core/api.service';
import { AssistantAnswer } from '../../core/models';
import { IconComponent } from '../../shared/icon.component';

interface Msg { id: number; who: 'me' | 'bot'; full: string; shown: string; answer?: AssistantAnswer; error?: boolean; done: boolean }

@Component({
  selector: 'app-assistant', standalone: true, imports: [FormsModule, IconComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <header class="head rise"><div class="row"><span class="avatar"><app-icon name="chat" [size]="18" /></span><div><h1>Asistente Insta</h1>
    <p class="muted">Pregúntame con tus palabras: <em>«¿cuánto gastó Ventas en USD en marzo?»</em>. Entiendo departamentos, categorías, monedas y meses, y recuerdo la pregunta anterior. No uso IA generativa: consulto tus datos y no invento cifras.</p></div></div></header>

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
    .head { margin-bottom: 18px; p { margin: 4px 0 0; max-width: 80ch; } }
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
    .col .table-wrap { max-width: 100%; } .meta, .chips { display: flex; gap: 6px; flex-wrap: wrap; }
    .chips { margin-top: 2px; }
    .compose { display: flex; gap: 10px; padding: 14px; border-top: 1px solid var(--border); background: var(--bg-2); input { flex: 1; height: 40px; } }
  `],
})
export class AssistantComponent implements OnDestroy {
  private api = inject(ApiService);
  private log = viewChild<ElementRef<HTMLElement>>('log');
  examples = toSignal(this.api.examples().pipe(catchError(() => of([] as string[]))), { initialValue: [] as string[] });
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
      mode: 'rules', intent: 'greeting', answer: '¡Hola! Soy el asistente de Insta Expenses. Pregúntame por gastos, pendientes, rankings o duplicados, y si quieres acota por departamento, categoría, moneda o mes. Por ejemplo:',
      columns: [], rows: [], period: '—', source: 'n/a', scope: [],
      suggestions: ['¿Qué departamento gastó más este mes?', '¿Qué gastos siguen pendientes?', '¿Cuál es el total gastado por moneda?', '¿Existen transacciones potencialmente duplicadas?', '¿Cuáles son los gastos más grandes?'],
    });
  }
  ngOnDestroy() { clearInterval(this.timer); }

  ask(text: string) {
    const t = text.trim(); if (!t || this.busy()) return;
    this.q = ''; this.busy.set(true);
    this.msgs.update(m => [...m, { id: ++this.n, who: 'me', full: t, shown: t, done: true }]);
    this.scroll();
    const started = Date.now();
    this.api.ask(t, this.lastIntent, this.lastQuestion).subscribe({
      next: a => setTimeout(() => { this.busy.set(false); if (!['greeting', 'help', 'thanks', 'empty'].includes(a.intent)) { this.lastIntent = a.intent; this.lastQuestion = t; } this.deliver(a); }, Math.max(0, 550 - (Date.now() - started))),
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
