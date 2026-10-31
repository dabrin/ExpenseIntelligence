import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { fmt } from '../core/state';

export interface RaceItem { key: string; value: number; count: number; color: string }

/** Ranked horizontal bars. Every key keeps one DOM node, so a change in rank animates as a slide instead of a re-render. */
@Component({
  selector: 'app-race-bars', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <ol class="race" [style.height.px]="shown() * rowH" [attr.aria-label]="label()">
      @for (r of rows(); track r.item.key) {
        <li [style.transform]="'translateY(' + r.rank * rowH + 'px)'" [class.hidden]="r.rank >= limit()" [class.zero]="r.item.value <= 0">
          <span class="name" [title]="r.item.key">{{ r.item.key }}</span>
          <span class="track"><span class="fill" [style.width.%]="r.pct" [style.background]="r.item.color"></span></span>
          <span class="val">{{ money(r.item.value) }}</span>
        </li>
      }
    </ol>`,
  styles: [`
    .race { position: relative; list-style: none; margin: 0; padding: 0; transition: height .4s ease; }
    li { position: absolute; left: 0; right: 0; top: 0; height: 36px; display: grid; grid-template-columns: minmax(90px, 170px) 1fr minmax(78px, auto); align-items: center; gap: 10px;
      transition: transform .55s cubic-bezier(.3,.7,.2,1), opacity .35s; }
    li.hidden { opacity: 0; pointer-events: none; }
    li.zero .name { color: var(--faint); }
    .name { font-weight: 600; font-size: 13px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .track { height: 22px; border-radius: 7px; background: var(--bg-2); overflow: hidden; }
    .fill { display: block; height: 100%; min-width: 3px; border-radius: 7px; transition: width .55s cubic-bezier(.3,.7,.2,1); }
    .val { font-variant-numeric: tabular-nums; font-size: 12.5px; text-align: right; color: var(--muted); }
    @media (max-width: 560px) { li { grid-template-columns: 76px 1fr 70px; gap: 6px; } }
  `],
})
export class RaceBarsComponent {
  items = input.required<RaceItem[]>();
  currency = input.required<string>();
  limit = input(10);
  max = input.required<number>();
  label = input('Ranking');
  readonly rowH = 36;

  shown = computed(() => Math.min(this.items().length, this.limit()));
  rows = computed(() => {
    const items = this.items(), max = this.max() || 1;
    const order = [...items].sort((a, b) => b.value - a.value || a.key.localeCompare(b.key));
    const rank = new Map(order.map((it, i) => [it.key, i]));
    return items.map(item => ({ item, rank: rank.get(item.key) ?? 0, pct: Math.max(0, Math.min(100, (item.value / max) * 100)) }));
  });
  money(v: number) { return fmt.money(v, this.currency(), true); }
}
