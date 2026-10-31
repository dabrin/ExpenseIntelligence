import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export type Pose = 'saludo' | 'analisis' | 'revision' | 'tiempo';

/** Prestamito in one of his poses (decorative: the surrounding text carries the meaning). */
@Component({
  selector: 'app-mascot', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<img [src]="'assets/mascot/' + pose() + '.webp'" alt="" [style.height.px]="height()" loading="lazy" decoding="async" />`,
  styles: [`:host { display: inline-flex; flex: none; } img { display: block; width: auto; max-width: 100%; user-select: none; -webkit-user-drag: none; }`],
})
export class MascotComponent {
  pose = input<Pose>('saludo');
  height = input(96);
}
