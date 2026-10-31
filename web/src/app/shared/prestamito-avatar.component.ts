import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import { IconComponent } from './icon.component';

/** Becomes false after the first failed load, so a missing file costs one request and every avatar falls back to the chat icon. */
const faceAvailable = signal(true);

/** Prestamito's face, used wherever he "speaks" (chat messages, headers). */
@Component({
  selector: 'app-prestamito-avatar', standalone: true, imports: [IconComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (ok()) { <img src="assets/mascot/cara.webp" alt="" [style.width.px]="size()" [style.height.px]="size()" (error)="fail()" /> }
    @else { <span class="fb" [style.width.px]="size()" [style.height.px]="size()"><app-icon name="chat" [size]="size() * .5" /></span> }`,
  styles: [`
    :host { display: inline-flex; flex: none; }
    img { display: block; object-fit: contain; }
    .fb { display: grid; place-items: center; color: #fff; border-radius: 30%; background: linear-gradient(135deg, var(--green), var(--green-2)); }
  `],
})
export class PrestamitoAvatarComponent {
  size = input(28);
  ok = faceAvailable;
  fail() { faceAvailable.set(false); }
}
