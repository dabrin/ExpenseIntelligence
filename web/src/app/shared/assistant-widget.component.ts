import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink } from '@angular/router';
import { filter, map } from 'rxjs';
import { AssistantChat } from '../core/assistant-chat.service';
import { ChatPanelComponent } from './chat-panel.component';
import { IconComponent } from './icon.component';
import { MascotComponent } from './mascot.component';
import { PrestamitoAvatarComponent } from './prestamito-avatar.component';

/** Floating chat available on every screen. It hides on /asistente, where the same conversation already fills the page. */
@Component({
  selector: 'app-assistant-widget', standalone: true, imports: [ChatPanelComponent, IconComponent, RouterLink, PrestamitoAvatarComponent, MascotComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (!onAssistantPage()) {
      @if (chat.open()) {
        <section class="panel card" role="dialog" aria-label="Chat con Prestamito" (keydown.escape)="chat.open.set(false)">
          <header>
            <app-prestamito-avatar [size]="34" />
            <div class="ttl"><strong>Prestamito</strong><span class="muted">Pregunta o pídeme ir a una pantalla</span></div>
            <button type="button" class="btn sm ghost" (click)="chat.reset()" title="Empezar una conversación nueva">Nueva</button>
            <a class="btn sm ghost icon" routerLink="/asistente" (click)="chat.open.set(false)" aria-label="Abrir en pantalla completa" title="Pantalla completa"><app-icon name="list" [size]="14" /></a>
            <button type="button" class="btn sm ghost icon" (click)="chat.open.set(false)" aria-label="Cerrar el chat"><app-icon name="x" [size]="14" /></button>
          </header>
          <app-chat-panel placeholder="Pregunta o pide ir a una pantalla…" />
        </section>
      }
      <div class="dock">
        <button type="button" class="mascot" (click)="chat.toggle()" tabindex="-1" aria-hidden="true"><app-mascot pose="saludo" [height]="96" /></button>
        <button type="button" class="fab" (click)="chat.toggle()" [attr.aria-expanded]="chat.open()" [attr.aria-label]="chat.open() ? 'Cerrar el chat con Prestamito' : 'Abrir el chat con Prestamito'">
          <app-icon [name]="chat.open() ? 'x' : 'chat'" [size]="24" />
        </button>
      </div>
    }`,
  styles: [`
    :host { display: contents; }
    .dock { position: fixed; right: 18px; bottom: 14px; z-index: 70; display: flex; align-items: flex-end; gap: 4px; pointer-events: none; > * { pointer-events: auto; } }
    .mascot { border: 0; background: none; padding: 0; cursor: pointer; filter: drop-shadow(0 8px 10px rgba(0,0,0,.28)); transition: transform .2s; transform-origin: bottom center; &:hover { transform: rotate(-4deg) scale(1.04); } }
    .fab { display: inline-flex; align-items: center; justify-content: center; width: 48px; height: 48px; padding: 0; margin-bottom: 8px; border-radius: 99px; border: 0; cursor: pointer; color: #fff; font: inherit; font-weight: 600; font-size: 15px;
      background: linear-gradient(135deg, var(--green), var(--green-2)); box-shadow: 0 12px 30px -8px rgba(0,0,0,.55); transition: transform .15s;
      &:hover { transform: translateY(-2px); } &:focus-visible { outline: 3px solid var(--text); outline-offset: 3px; } }
    .panel { position: fixed; right: 20px; bottom: 132px; z-index: 70; width: min(420px, calc(100vw - 32px)); height: min(640px, calc(100vh - 150px)); padding: 0; overflow: hidden; display: grid; grid-template-rows: auto minmax(0, 1fr); box-shadow: 0 24px 60px -16px rgba(0,0,0,.65); }
    header { display: flex; align-items: center; gap: 8px; padding: 10px 12px; border-bottom: 1px solid var(--border); background: var(--bg-2); }
    .ttl { flex: 1; min-width: 0; display: grid; line-height: 1.2; span { font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; } }
    @media (max-width: 600px) { .panel { right: 8px; left: 8px; width: auto; bottom: 120px; height: calc(100vh - 140px); } .dock { right: 10px; bottom: 8px; } app-mascot { zoom: .8; } }
  `],
})
export class AssistantWidgetComponent {
  chat = inject(AssistantChat);
  private router = inject(Router);
  onAssistantPage = toSignal(this.router.events.pipe(filter(e => e instanceof NavigationEnd), map(() => this.router.url.startsWith('/asistente'))), { initialValue: this.router.url.startsWith('/asistente') });
}
