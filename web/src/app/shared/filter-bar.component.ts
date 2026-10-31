import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { toObservable, toSignal } from '@angular/core/rxjs-interop';
import { catchError, of, switchMap } from 'rxjs';
import { ApiService } from '../core/api.service';
import { DataVersion, FilterStore } from '../core/state';
import { Facets } from '../core/models';
import { IconComponent } from './icon.component';
import { MultiSelectComponent } from './multi-select.component';

@Component({
  selector: 'app-filter-bar', standalone: true, imports: [MultiSelectComponent, IconComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="card bar" aria-label="Filtros">
      <div class="row top">
        <strong class="ttl"><app-icon name="filter" /> Filtros</strong>
        @if (store.activeCount() > 0) { <span class="badge plain">{{ store.activeCount() }} activos</span> }
        <span class="spacer"></span>
        <div class="seg" role="group" aria-label="Estado de aprobación">
          <button type="button" [class.on]="f().estado === ''" (click)="store.patch({ estado: '' })">Todos</button>
          <button type="button" [class.on]="f().estado === 'aprobado'" (click)="store.patch({ estado: 'aprobado' })">Aprobados</button>
          <button type="button" [class.on]="f().estado === 'pendiente'" (click)="store.patch({ estado: 'pendiente' })">Pendientes</button>
          <button type="button" [class.on]="f().estado === 'rechazado'" (click)="store.patch({ estado: 'rechazado' })">Rechazados</button>
        </div>
        <button type="button" class="btn sm ghost" (click)="store.reset()" [disabled]="store.activeCount() === 0"><app-icon name="refresh" [size]="14" /> Restablecer</button>
      </div>
      <div class="fields">
        <label class="field">Desde <input type="date" [value]="f().from" [min]="facets()?.minFecha ?? ''" [max]="f().to || facets()?.maxFecha || ''" (change)="store.patch({ from: val($event) })" /></label>
        <label class="field">Hasta <input type="date" [value]="f().to" [min]="f().from || facets()?.minFecha || ''" [max]="facets()?.maxFecha ?? ''" (change)="store.patch({ to: val($event) })" /></label>
        <app-multi-select label="Departamento" [options]="facets()?.departamentos ?? []" [selected]="f().departamentos" (changed)="store.patch({ departamentos: $event })" />
        <app-multi-select label="Categoría" [options]="facets()?.categorias ?? []" [selected]="f().categorias" (changed)="store.patch({ categorias: $event })" />
        <app-multi-select label="Moneda" [options]="facets()?.monedas ?? []" [selected]="f().monedas" (changed)="store.patch({ monedas: $event })" />
        <app-multi-select label="Pago" [options]="facets()?.metodosPago ?? []" [selected]="f().metodosPago" (changed)="store.patch({ metodosPago: $event })" />
      </div>
      <div class="row presets">
        <span class="faint">Rango rápido</span>
        <button type="button" class="chip" (click)="preset(30)">Últimos 30 días</button>
        <button type="button" class="chip" (click)="preset(90)">Últimos 90 días</button>
        <button type="button" class="chip" (click)="preset(365)">Últimos 12 meses</button>
        <button type="button" class="chip" (click)="store.patch({ from: '', to: '' })">Todo el histórico</button>
        @if (showSearch()) {
          <span class="spacer"></span>
          <label class="search"><app-icon name="search" /><input type="search" placeholder="Buscar descripción, factura o ID" [value]="f().q" (input)="store.patch({ q: val($event) })" aria-label="Buscar" /></label>
        }
      </div>
    </section>`,
  styles: [`
    .bar { display: grid; gap: 12px; padding: 14px 16px; }
    .ttl { display: inline-flex; gap: 8px; align-items: center; font-size: 13px; }
    .fields { display: grid; grid-template-columns: repeat(auto-fit, minmax(150px, 1fr)); gap: 10px; align-items: end; }
    .presets { font-size: 12.5px; }
    .search { position: relative; display: block; width: min(320px, 100%); app-icon { position: absolute; left: 11px; top: 10px; color: var(--faint); } input { padding-left: 33px; } }
  `],
})
export class FilterBarComponent {
  store = inject(FilterStore);
  private api = inject(ApiService);
  private dv = inject(DataVersion);
  showSearch = input(false);
  f = this.store.filters;
  facets = toSignal(toObservable(this.dv.n).pipe(switchMap(() => this.api.facets().pipe(catchError(() => of(null as Facets | null))))), { initialValue: null as Facets | null });
  val(e: Event) { return (e.target as HTMLInputElement).value; }
  preset(days: number) {
    const max = this.facets()?.maxFecha; if (!max) return;
    const d = new Date(max + 'T00:00:00'); d.setDate(d.getDate() - (days - 1));
    this.store.patch({ from: d.toISOString().slice(0, 10), to: max });
  }
}
