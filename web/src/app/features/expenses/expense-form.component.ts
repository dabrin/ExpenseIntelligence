import { ChangeDetectionStrategy, Component, OnInit, inject, input, output, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { ApiService, asProblem } from '../../core/api.service';
import { Expense, ExpenseInput, Facets } from '../../core/models';
import { ToastService, fmt } from '../../core/state';
import { IconComponent } from '../../shared/icon.component';

@Component({
  selector: 'app-expense-form', standalone: true, imports: [ReactiveFormsModule, IconComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <div class="backdrop" (mousedown)="$event.target === $event.currentTarget ? cancelled.emit() : null">
    <form class="modal" [formGroup]="form" (ngSubmit)="submit()" role="dialog" aria-modal="true" [attr.aria-label]="expense() ? 'Editar gasto' : 'Nuevo gasto'" novalidate>
      <div class="m-head"><h2 style="font-size:18px">{{ expense() ? 'Editar gasto #' + expense()!.id : 'Nuevo gasto' }}</h2>
        <button type="button" class="btn ghost icon" (click)="cancelled.emit()" aria-label="Cerrar"><app-icon name="x" /></button></div>
      <div class="m-body">
        @if (conflict()) {
          <div class="banner warn" role="alert" style="margin-bottom:12px"><app-icon name="alert" /><div><strong>Conflicto de edición.</strong> Alguien modificó este gasto mientras lo editabas (versión actual {{ conflict() }}).</div>
            <span class="spacer"></span><button type="button" class="btn sm" (click)="reloadLatest()">Cargar versión actual</button></div>
        }
        @if (formError()) { <div class="banner bad" role="alert" style="margin-bottom:12px"><app-icon name="alert" /><span>{{ formError() }}</span></div> }
        @if (review(); as r) {
          <div class="review" role="alert">
            <h3>Revisa estos cambios antes de guardar</h3>
            @for (l of r.alerts; track l.text) {
              <div class="banner" [class.bad]="l.level === 'danger'" [class.warn]="l.level === 'warn'"><app-icon [name]="l.level === 'info' ? 'info' : 'alert'" /><span>{{ l.text }}</span></div>
            }
            @if (r.others.length) { <p class="muted">También cambia: {{ r.others.join(' · ') }}</p> }
          </div>
        } @else {
        <div class="grid g2">
          <label class="field span">Descripción
            <input formControlName="descripcion" maxlength="200" [class.invalid]="bad('descripcion')" placeholder="Ej. Licencias anuales de diseño" />
            @if (bad('descripcion')) { <span class="err">{{ msg('descripcion') }}</span> }</label>
          <label class="field">Monto
            <input formControlName="monto" type="number" step="0.01" min="0.01" inputmode="decimal" (keydown)="onlyDigits($event)" (paste)="cleanPaste($event)" [class.invalid]="bad('monto')" />
            @if (bad('monto')) { <span class="err">{{ msg('monto') }}</span> }</label>
          <label class="field">Moneda
            <select formControlName="moneda" [class.invalid]="bad('moneda')"><option value="">Seleccione…</option>@for (m of currencies; track m) { <option [value]="m">{{ m }}</option> }</select>
            @if (bad('moneda')) { <span class="err">{{ msg('moneda') }}</span> }</label>
          <label class="field">Departamento
            <input formControlName="departamento" list="dl-dep" [class.invalid]="bad('departamento')" />
            <datalist id="dl-dep">@for (o of facets()?.departamentos ?? []; track o) { <option [value]="o"></option> }</datalist>
            @if (bad('departamento')) { <span class="err">{{ msg('departamento') }}</span> }</label>
          <label class="field">Categoría
            <input formControlName="categoria" list="dl-cat" [class.invalid]="bad('categoria')" />
            <datalist id="dl-cat">@for (o of facets()?.categorias ?? []; track o) { <option [value]="o"></option> }</datalist>
            @if (bad('categoria')) { <span class="err">{{ msg('categoria') }}</span> }</label>
          <label class="field">Método de pago
            <input formControlName="metodoPago" list="dl-pay" [class.invalid]="bad('metodoPago')" />
            <datalist id="dl-pay">@for (o of facets()?.metodosPago ?? []; track o) { <option [value]="o"></option> }</datalist>
            @if (bad('metodoPago')) { <span class="err">{{ msg('metodoPago') }}</span> }</label>
          <label class="field">Fecha
            <input formControlName="fecha" type="date" [max]="today" [class.invalid]="bad('fecha')" />
            @if (bad('fecha')) { <span class="err">{{ msg('fecha') }}</span> }</label>
          <label class="field">Código de factura
            <input formControlName="codigoFactura" placeholder="FAC-12345" [class.invalid]="bad('codigoFactura')" />
            @if (bad('codigoFactura')) { <span class="err">{{ msg('codigoFactura') }}</span> }</label>
        </div>
        }
      </div>
      <div class="m-foot">
        @if (review()) {
          <button type="button" class="btn" (click)="review.set(null)">Volver a editar</button>
          <button type="button" class="btn primary" [disabled]="saving()" (click)="send()">{{ saving() ? 'Guardando…' : 'Confirmar y guardar' }}</button>
        } @else {
          <button type="button" class="btn" (click)="cancelled.emit()">Cancelar</button>
          <button type="submit" class="btn primary" [disabled]="saving()">{{ saving() ? 'Guardando…' : expense() ? 'Guardar cambios' : 'Crear gasto' }}</button>
        }
      </div>
    </form>
  </div>`,
  styles: ['.review h3{margin:0 0 12px;font-size:15px}.review .banner{margin-bottom:8px}input:disabled,select:disabled{opacity:.6;cursor:not-allowed}.g2{display:grid;grid-template-columns:1fr 1fr;gap:14px}.span{grid-column:1/-1}.chk span{display:flex;gap:9px;align-items:center;color:var(--text)}@media(max-width:560px){.g2{grid-template-columns:1fr}}'],
})
export class ExpenseFormComponent implements OnInit {
  private api = inject(ApiService);
  private toast = inject(ToastService);
  private fb = inject(FormBuilder);
  expense = input<Expense | null>(null);
  facets = input<Facets | null>(null);
  saved = output<Expense>();
  cancelled = output<void>();

  currencies = ['CRC', 'USD', 'EUR'];
  today = new Date().toISOString().slice(0, 10);
  saving = signal(false);
  formError = signal<string | null>(null);
  conflict = signal<number | null>(null);
  review = signal<{ alerts: { level: 'danger' | 'warn' | 'info'; text: string }[]; others: string[] } | null>(null);
  private original: Expense | null = null;
  private version = 1;

  form = this.fb.nonNullable.group({
    descripcion: ['', [Validators.required, Validators.minLength(3), Validators.maxLength(200)]],
    monto: [null as number | null, [Validators.required, Validators.min(0.01), Validators.max(1_000_000_000)]],
    moneda: ['', Validators.required], departamento: ['', [Validators.required, Validators.maxLength(80)]], categoria: ['', [Validators.required, Validators.maxLength(80)]],
    metodoPago: ['', [Validators.required, Validators.maxLength(80)]], fecha: [this.today, Validators.required],
    codigoFactura: ['', [Validators.required, Validators.pattern(/^[A-Za-z0-9][A-Za-z0-9\-_/]{2,29}$/)]],
  });

  ngOnInit() { const e = this.expense(); if (e) this.fill(e); }
  private fill(e: Expense) {
    this.version = e.version;
    this.original = e;
    this.form.patchValue({ ...e });
  }

  bad(c: string) { const x = this.form.get(c)!; return x.invalid && (x.touched || x.dirty); }
  msg(c: string) {
    const x = this.form.get(c)!; const e = x.errors ?? {};
    if (e['server']) return e['server'] as string;
    if (e['required']) return 'Campo obligatorio.';
    if (e['min']) return 'Debe ser mayor que cero.';
    if (e['max']) return 'Valor demasiado alto.';
    if (e['minlength']) return 'Muy corto (mínimo 3).';
    if (e['pattern']) return 'Formato inválido (p. ej. FAC-12345).';
    return 'Valor inválido.';
  }

  /** type=number accepts e, E, + and -: an amount is plain digits with an optional decimal point. */
  onlyDigits(ev: KeyboardEvent) { if (/^[eE+-]$/.test(ev.key)) ev.preventDefault(); }
  cleanPaste(ev: ClipboardEvent) { if (/[eE+-]/.test(ev.clipboardData?.getData('text') ?? '')) ev.preventDefault(); }

  reloadLatest() {
    const id = this.expense()!.id;
    this.api.list({ from: '', to: '', departamentos: [], categorias: [], monedas: [], metodosPago: [], estado: '', ids: [id], q: '' }, 'id', false, 1, 5).subscribe(r => {
      const e = r.items.find(i => i.id === id); if (e) { this.fill(e); this.conflict.set(null); this.formError.set(null); }
    });
  }

  /** Edits to amount, currency, date or invoice are confirmed against a plain-language summary; text-only edits save directly. */
  private assess(body: ExpenseInput) {
    const o = this.original!;
    const alerts: { level: 'danger' | 'warn' | 'info'; text: string }[] = [];
    const others: string[] = [];
    const cur = o.moneda;
    if (body.monto !== null && body.monto !== o.monto) {
      const pct = o.monto ? ((body.monto - o.monto) / o.monto) * 100 : 0;
      alerts.push({ level: Math.abs(pct) >= 50 ? 'danger' : 'warn', text: `Cambias el monto de ${fmt.money(o.monto, cur)} a ${fmt.money(body.monto, cur)} (${pct >= 0 ? '+' : ''}${pct.toFixed(1)}%).${Math.abs(pct) >= 50 ? ' Es un cambio grande: verifica que no sea un error de digitación.' : ''}` });
    }
    if (body.moneda !== o.moneda) alerts.push({ level: 'danger', text: `Cambias la moneda de ${o.moneda} a ${body.moneda}. El monto no se convierte: solo cambia la moneda en que se interpreta.` });
    if (body.fecha !== o.fecha) alerts.push({ level: body.fecha.slice(0, 7) !== o.fecha.slice(0, 7) ? 'warn' : 'info', text: `La fecha pasa de ${o.fecha} a ${body.fecha}${body.fecha.slice(0, 7) !== o.fecha.slice(0, 7) ? ': el gasto cambia de mes en las gráficas.' : '.'}` });
    if (body.codigoFactura !== o.codigoFactura) alerts.push({ level: 'warn', text: `Cambia el código de factura de ${o.codigoFactura} a ${body.codigoFactura}.` });
    for (const [k, label] of [['descripcion', 'Descripción'], ['departamento', 'Departamento'], ['categoria', 'Categoría'], ['metodoPago', 'Método de pago']] as const)
      if ((body[k] ?? '') !== (o[k] ?? '')) others.push(`${label} (${o[k]} → ${body[k]})`);
    return { alerts, others, any: alerts.length > 0 || others.length > 0 };
  }

  submit() {
    this.formError.set(null);
    this.form.markAllAsTouched();
    if (this.form.invalid) return;
    if (this.original) {
      const a = this.assess(this.body());
      if (!a.any) { this.formError.set('No hiciste ningún cambio.'); return; }
      if (a.alerts.length) { this.review.set({ alerts: a.alerts, others: a.others }); return; }
    }
    this.send();
  }

  private body(): ExpenseInput {
    const v = this.form.getRawValue();
    // approval is never set from this form: it is changed from the expense panel (approve / reject / back to pending)
    return { ...v, monto: v.monto, descripcion: v.descripcion.trim(), aprobado: false };
  }

  send() {
    this.formError.set(null);
    const body = this.body();
    this.saving.set(true);
    const call = this.expense() ? this.api.update(this.expense()!.id, this.version, body) : this.api.create(body);
    call.subscribe({
      next: r => {
        this.saving.set(false);
        this.toast.show('ok', this.expense() ? `Gasto #${r.expense.id} actualizado.` : `Gasto #${r.expense.id} creado.`);
        for (const w of r.warnings) this.toast.show('info', w, undefined, 8000);
        this.saved.emit(r.expense);
      },
      error: err => {
        this.saving.set(false);
        this.review.set(null);
        const p = asProblem(err);
        if (p.status === 409) { this.conflict.set(p.currentVersion ?? null); return; }
        if (p.status === 400 && p.errors) {
          for (const [k, m] of Object.entries(p.errors)) this.form.get(k)?.setErrors({ server: m[0] });
          this.form.markAllAsTouched();
          this.formError.set('Revisa los campos marcados.');
        } else this.formError.set(p.status === 503 ? `${p.title} Intenta de nuevo.` : p.title);
      },
    });
  }
}
