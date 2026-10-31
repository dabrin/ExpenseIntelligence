import { Injectable, inject, signal } from '@angular/core';
import { Router } from '@angular/router';
import { ApiService, asProblem } from './api.service';
import { AssistantAction, AssistantAnswer, Filters } from './models';
import { FilterStore } from './state';

export interface Msg { id: number; who: 'me' | 'bot'; full: string; shown: string; answer?: AssistantAnswer; error?: boolean; done: boolean }

const KEY = 'ei-assistant-chat';
const MAX_SAVED = 30;
const NO_FOLLOW_UP = ['greeting', 'help', 'thanks', 'empty', 'llm', 'chat', 'off-topic'];
const GREETING: AssistantAnswer = {
  mode: 'rules', intent: 'greeting', answer: '¡Hola! Soy Prestamito, el asistente de Insta Expenses. Pregúntame por tus gastos o dime a dónde quieres ir y te llevo. Por ejemplo:',
  columns: [], rows: [], period: '—', source: 'n/a', scope: [],
  suggestions: ['¿Qué departamento gastó más este mes?', '¿Qué gastos siguen pendientes?', 'Llévame a los gastos pendientes', '¿Cómo creo un gasto nuevo?', '¿Existen transacciones potencialmente duplicadas?'],
};

/** The conversation lives here (not in a screen), so it survives navigation and page reloads within the tab, and the floating chat and the /asistente page show the same one. */
@Injectable({ providedIn: 'root' })
export class AssistantChat {
  private api = inject(ApiService);
  private router = inject(Router);
  private store = inject(FilterStore);

  readonly msgs = signal<Msg[]>([]);
  readonly busy = signal(false);
  /** Floating panel visibility. */
  readonly open = signal(false);
  private n = 0;
  private timer?: ReturnType<typeof setInterval>;
  private lastIntent?: string;
  private lastQuestion?: string;

  constructor() {
    const saved = this.load();
    if (saved.length) { this.msgs.set(saved); this.n = Math.max(...saved.map(m => m.id)); }
    else this.deliver(GREETING);
  }

  toggle() { this.open.update(o => !o); }

  reset() {
    clearInterval(this.timer); this.busy.set(false); this.lastIntent = this.lastQuestion = undefined; this.msgs.set([]); this.n = 0;
    this.deliver(GREETING); this.save();
  }

  ask(text: string) {
    const t = text.trim(); if (!t || this.busy()) return;
    // Refusals (and the question that caused them) stay out of the history: otherwise the model copies its own refusal on the next turn.
    const log = this.msgs().filter(m => m.done && m.full && !m.error);
    const history = log.filter((m, i) => m.answer?.intent !== 'off-topic' && !(m.who === 'me' && log[i + 1]?.answer?.intent === 'off-topic'))
      .slice(-8).map(m => ({ role: m.who === 'me' ? 'user' as const : 'assistant' as const, content: m.full }));
    this.busy.set(true);
    this.msgs.update(m => [...m, { id: ++this.n, who: 'me', full: t, shown: t, done: true }]);
    this.save();
    const started = Date.now();
    const page = this.router.url.split(/[?#]/)[0];
    this.api.ask(t, this.lastIntent, this.lastQuestion, history, page).subscribe({
      next: a => setTimeout(() => {
        this.busy.set(false);
        if (!NO_FOLLOW_UP.includes(a.intent)) { this.lastIntent = a.intent; this.lastQuestion = t; }
        this.deliver(a);
        const auto = a.actions?.find(x => x.auto);
        if (auto) setTimeout(() => this.go(auto), 700); // let the user read the sentence before the screen changes
      }, Math.max(0, 550 - (Date.now() - started))),
      error: e => { this.busy.set(false); this.msgs.update(m => [...m, { id: ++this.n, who: 'bot', full: asProblem(e).title, shown: asProblem(e).title, error: true, done: true }]); this.save(); },
    });
  }

  /** Opens a screen the assistant offered. Filters go through the same shared FilterStore as the filter bar; the server only ever names the app's own routes. */
  go(a: AssistantAction) {
    if (!a.path.startsWith('/')) return;
    if (a.filters) {
      const f = a.filters; const p: Partial<Filters> = {};
      if (f.from) p.from = f.from;
      if (f.to) p.to = f.to;
      if (f.departamentos?.length) p.departamentos = f.departamentos;
      if (f.categorias?.length) p.categorias = f.categorias;
      if (f.monedas?.length) p.monedas = f.monedas;
      if (f.metodosPago?.length) p.metodosPago = f.metodosPago;
      if (f.estado === 'pendiente' || f.estado === 'aprobado' || f.estado === 'rechazado') p.estado = f.estado;
      if (f.search) p.q = f.search;
      this.store.reset(); this.store.patch(p); // the screen shows exactly what was promised
    }
    void this.router.navigateByUrl(a.path);
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
      if (i >= a.answer.length) { clearInterval(this.timer); this.save(); }
    }, 14);
  }

  private save() {
    try {
      const done = this.msgs().filter(m => m.done).slice(-MAX_SAVED).map(m => ({ ...m, shown: m.full }));
      sessionStorage.setItem(KEY, JSON.stringify(done));
    } catch { /* storage unavailable or full: the chat still works, it just will not survive a reload */ }
  }

  private load(): Msg[] {
    try {
      const raw = JSON.parse(sessionStorage.getItem(KEY) ?? '[]') as Msg[];
      return Array.isArray(raw) ? raw.filter(m => m && typeof m.id === 'number' && typeof m.full === 'string').map(m => ({ ...m, shown: m.full, done: true })) : [];
    } catch { return []; }
  }
}
