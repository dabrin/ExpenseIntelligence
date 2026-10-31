import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

const PATHS: Record<string, string> = {
  dashboard: 'M3 13h8V3H3zM13 21h8V11h-8zM3 21h8v-6H3zM13 3v6h8V3z',
  list: 'M8 6h13M8 12h13M8 18h13M3 6h.01M3 12h.01M3 18h.01',
  doctor: 'M12 21s-7-4.5-9.5-9A5.5 5.5 0 0 1 12 6a5.5 5.5 0 0 1 9.5 6c-2.5 4.5-9.5 9-9.5 9zM12 9v6M9 12h6',
  play: 'M7 4l13 8-13 8z', pause: 'M8 4v16M16 4v16', clock: 'M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 7v5l3 2', skip: 'M5 4l10 8-10 8zM19 4v16',
  folder: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2z', compare: 'M8 3H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h3M16 3h3a2 2 0 0 1 2 2v14a2 2 0 0 1-2 2h-3M12 2v20',
  pulse: 'M3 12h4l3-8 4 16 3-8h4',
  chat: 'M21 12a8 8 0 0 1-11.6 7.1L3 21l1.9-5.4A8 8 0 1 1 21 12z',
  plus: 'M12 5v14M5 12h14', edit: 'M4 20h4L19 9l-4-4L4 16zM14 6l4 4', trash: 'M4 7h16M10 11v6M14 11v6M6 7l1 13h10l1-13M9 7V4h6v3',
  x: 'M6 6l12 12M18 6L6 18', check: 'M5 12l5 5 9-10', chevron: 'M6 9l6 6 6-6', sun: 'M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6L7 7M17 17l1.4 1.4M5.6 18.4L7 17M17 7l1.4-1.4M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8z',
  moon: 'M20 14.5A8 8 0 1 1 9.5 4 6.5 6.5 0 0 0 20 14.5z', search: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14zM21 21l-4.5-4.5', filter: 'M3 5h18l-7 8v6l-4 2v-8z',
  up: 'M12 19V5M5 12l7-7 7 7', down: 'M12 5v14M5 12l7 7 7-7', refresh: 'M20 11a8 8 0 0 0-14.6-4M4 4v4h4M4 13a8 8 0 0 0 14.6 4M20 20v-4h-4',
  alert: 'M12 3l10 18H2zM12 10v5M12 18h.01', info: 'M12 22a10 10 0 1 0 0-20 10 10 0 0 0 0 20zM12 11v6M12 7.5h.01', undo: 'M9 14L4 9l5-5M4 9h10a6 6 0 0 1 0 12h-3',
  send: 'M22 2L11 13M22 2l-7 20-4-9-9-4z', inbox: 'M3 13l3-8h12l3 8v6H3zM3 13h5l1 3h6l1-3h5', shield: 'M12 3l8 3v6c0 5-3.5 8-8 9-4.5-1-8-4-8-9V6z', menu: 'M4 7h16M4 12h16M4 17h16', upload: 'M12 16V4M7 9l5-5 5 5M4 20h16',
};

@Component({
  selector: 'app-icon', standalone: true, changeDetection: ChangeDetectionStrategy.OnPush,
  template: `<svg [attr.width]="size()" [attr.height]="size()" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><path [attr.d]="d()"/></svg>`,
  styles: [':host{display:inline-flex}'],
})
export class IconComponent {
  name = input.required<string>();
  size = input(16);
  d = computed(() => PATHS[this.name()] ?? '');
}
