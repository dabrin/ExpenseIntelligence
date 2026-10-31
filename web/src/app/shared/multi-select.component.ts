import { ChangeDetectionStrategy, Component, ElementRef, HostListener, computed, inject, input, output, signal } from '@angular/core';
import { IconComponent } from './icon.component';

@Component({
  selector: 'app-multi-select', standalone: true, imports: [IconComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button type="button" class="trigger" [class.has]="selected().length" (click)="open.set(!open())" [attr.aria-expanded]="open()" aria-haspopup="listbox">
      <span class="lbl">{{ label() }}</span>
      <span class="val">{{ summary() }}</span>
      <app-icon name="chevron" [size]="14" />
    </button>
    @if (open()) {
      <div class="pop rise" role="listbox" [attr.aria-label]="label()" aria-multiselectable="true">
        @for (o of options(); track o) {
          <label class="opt"><input type="checkbox" [checked]="selected().includes(o)" (change)="toggle(o)" /> <span>{{ o }}</span></label>
        } @empty { <div class="muted" style="padding:8px">Sin opciones</div> }
        @if (selected().length) { <button type="button" class="btn sm ghost clear" (click)="changed.emit([])">Limpiar</button> }
      </div>
    }`,
  styles: [`
    :host { position: relative; display: block; min-width: 0; }
    .trigger { width: 100%; height: 36px; display: flex; align-items: center; gap: 8px; padding: 0 10px; background: var(--bg-2); border: 1px solid var(--border-2); border-radius: 9px; text-align: left;
      &:hover, &.has { border-color: var(--accent); } &.has { background: var(--accent-bg); } }
    .lbl { color: var(--muted); font-size: 12px; } .val { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; font-weight: 500; }
    .pop { position: absolute; z-index: 30; top: 40px; left: 0; min-width: 100%; width: max-content; max-width: 280px; max-height: 300px; overflow: auto; background: var(--surface); border: 1px solid var(--border-2); border-radius: 12px; padding: 6px; box-shadow: 0 20px 40px -12px rgba(0,0,0,.55); }
    .opt { display: flex; gap: 9px; align-items: center; padding: 7px 9px; border-radius: 8px; cursor: pointer; &:hover { background: var(--accent-bg); } }
    .clear { width: 100%; justify-content: center; margin-top: 4px; }
  `],
})
export class MultiSelectComponent {
  label = input.required<string>();
  options = input.required<string[]>();
  selected = input<string[]>([]);
  changed = output<string[]>();
  open = signal(false);
  private host = inject(ElementRef<HTMLElement>);
  summary = computed(() => { const s = this.selected(); return s.length === 0 ? 'Todos' : s.length === 1 ? s[0] : `${s.length} seleccionados`; });
  toggle(o: string) { const s = this.selected(); this.changed.emit(s.includes(o) ? s.filter(x => x !== o) : [...s, o]); }
  @HostListener('document:click', ['$event']) outside(e: Event) { if (!this.host.nativeElement.contains(e.target)) this.open.set(false); }
  @HostListener('keydown.escape') esc() { this.open.set(false); }
}
