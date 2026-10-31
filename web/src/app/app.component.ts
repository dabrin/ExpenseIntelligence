import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ThemeService, ToastService } from './core/state';
import { AssistantWidgetComponent } from './shared/assistant-widget.component';
import { IconComponent } from './shared/icon.component';

@Component({
  selector: 'app-root', standalone: true, imports: [RouterOutlet, RouterLink, RouterLinkActive, IconComponent, AssistantWidgetComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <a class="skip" href="#main">Saltar al contenido</a>
    <div class="shell">
      <aside class="side" [class.open]="menu()">
        <div class="brand"><svg class="mark" viewBox="0 0 40 40" width="38" height="38" aria-hidden="true"><path d="M17 6 5 20l12 14" fill="none" stroke="#5bbd3f" stroke-width="6.5" stroke-linecap="square" stroke-linejoin="miter"/><path d="M23 6l12 14-12 14" fill="none" stroke="#ffffff" stroke-width="6.5" stroke-linecap="square" stroke-linejoin="miter"/></svg><div><strong>Insta</strong><span>Expenses</span></div></div>
        <nav aria-label="Principal" (click)="menu.set(false)">
<a routerLink="/dashboard" routerLinkActive="on"><app-icon name="dashboard" /> Dashboard</a>
          <a routerLink="/maquina-del-tiempo" routerLinkActive="on"><app-icon name="clock" /> Máquina del tiempo</a>
          <a routerLink="/gastos" routerLinkActive="on"><app-icon name="list" /> Gastos</a>
          <a routerLink="/historial" routerLinkActive="on"><app-icon name="undo" /> Historial</a>
          <a routerLink="/asistente" routerLinkActive="on"><app-icon name="chat" /> Asistente</a>
          <hr class="nav-sep" />
          <a routerLink="/data-doctor" routerLinkActive="on"><app-icon name="doctor" /> Data Doctor</a>
          <a routerLink="/archivos" routerLinkActive="on"><app-icon name="folder" /> Archivos</a>
          <a routerLink="/diagnostico" routerLinkActive="on"><app-icon name="shield" /> Diagnóstico</a>
          <a routerLink="/importar" routerLinkActive="on"><app-icon name="upload" /> Importar archivo</a>
        </nav>
        <div class="foot">
          <button type="button" class="btn sm" (click)="theme.toggle()" [attr.aria-label]="'Cambiar a modo ' + (theme.theme() === 'dark' ? 'claro' : 'oscuro')">
            <app-icon [name]="theme.theme() === 'dark' ? 'sun' : 'moon'" /> {{ theme.theme() === 'dark' ? 'Modo claro' : 'Modo oscuro' }}
          </button>
        </div>
      </aside>
      <div class="main">
        <header class="mobile-bar">
          <button type="button" class="btn icon" (click)="menu.set(!menu())" aria-label="Menú"><app-icon name="menu" /></button>
          <strong>Insta <span style="color:var(--accent)">Expenses</span></strong>
        </header>
        <main id="main" tabindex="-1"><router-outlet /></main>
        <footer class="site-foot">
          <div class="ft-text">
            <strong>Desarrollado por Dabrin Nájera</strong>
            <span>Prueba técnica creada para demostrar conocimientos en desarrollo de software asistido por IA.</span>
          </div>
          <div class="ft-links">
            <a class="btn sm" routerLink="/dashboard"><app-icon name="dashboard" [size]="14" /> Ir al Dashboard</a>
            <a class="btn sm ghost" href="https://github.com/dabrin/ExpenseIntelligence" target="_blank" rel="noopener noreferrer">Repositorio</a>
          </div>
        </footer>
      </div>
    </div>
    <app-assistant-widget />
    <div class="toasts" aria-live="polite">
      @for (t of toast.toasts(); track t.id) {
        <div class="toast rise" [class]="t.kind">
          <app-icon [name]="t.kind === 'error' ? 'alert' : t.kind === 'ok' ? 'check' : 'info'" />
          <span>{{ t.text }}</span>
          @if (t.action) { <button type="button" class="btn sm" (click)="t.action.run(); toast.dismiss(t.id)">{{ t.action.label }}</button> }
          <button type="button" class="btn sm ghost icon" (click)="toast.dismiss(t.id)" aria-label="Cerrar"><app-icon name="x" [size]="14" /></button>
        </div>
      }
    </div>`,
  styles: [`
    .skip { position: absolute; left: -999px; &:focus { left: 8px; top: 8px; z-index: 100; background: var(--accent); color: #fff; padding: 8px 12px; border-radius: 8px; } }
    .shell { display: grid; grid-template-columns: 232px 1fr; min-height: 100vh; }
    .side { position: sticky; top: 0; height: 100vh; display: flex; flex-direction: column; gap: 22px; padding: 20px 14px; background: var(--nav-bg); border-right: 0; color: var(--nav-text); }
    .brand { display: flex; align-items: center; gap: 11px; padding: 0 8px; line-height: 1.15; strong { display: block; font-size: 17px; color: #fff; letter-spacing: -.02em; } span:not(.logo) { color: #7ad35f; font-weight: 700; font-size: 14px; letter-spacing: .01em; } }
    .mark { flex: none; }
    .logo { width: 36px; height: 36px; display: grid; place-items: center; color: #fff; border-radius: 11px; background: linear-gradient(135deg, #5bbd3f, #3b8a24); box-shadow: 0 8px 20px -8px #5bbd3f; }
    .nav-sep { border: 0; border-top: 1px solid rgba(255,255,255,.14); margin: 8px 6px; width: auto; }
    nav { display: grid; gap: 3px; a { display: flex; align-items: center; gap: 11px; padding: 9px 11px; border-radius: 10px; color: var(--nav-text); font-weight: 500; transition: background .15s, color .15s;
      &:hover { color: #fff; background: var(--nav-active); } &.on { color: #fff; background: var(--nav-active); box-shadow: inset 3px 0 0 #5bbd3f; } } }
    .foot .btn { background: rgba(255,255,255,.08); border-color: rgba(255,255,255,.18); color: #fff; &:hover:not(:disabled) { background: rgba(255,255,255,.16); border-color: #5bbd3f; } }
    .foot { margin-top: auto; }
    .main { min-width: 0; }
    main { padding: 26px clamp(16px, 3vw, 36px) 36px; max-width: 1560px; margin: 0 auto; outline: none; }
    .site-foot { display: flex; align-items: center; justify-content: space-between; gap: 12px 24px; flex-wrap: wrap; max-width: 1560px; margin: 0 auto; padding: 18px clamp(16px, 3vw, 36px) 28px; border-top: 1px solid var(--border); font-size: 13px; color: var(--muted); }
    .ft-text { display: grid; gap: 2px; strong { color: var(--text); font-weight: 600; } }
    .ft-links { display: flex; gap: 8px; flex-wrap: wrap; }
    .mobile-bar { display: none; }
    .toasts { position: fixed; right: 16px; bottom: 132px; display: grid; gap: 8px; z-index: 80; width: min(420px, calc(100vw - 32px)); }
    .toast { display: flex; align-items: center; gap: 10px; padding: 11px 13px; border-radius: 12px; background: var(--surface); border: 1px solid var(--border-2); box-shadow: 0 16px 40px -12px rgba(0,0,0,.6); span { flex: 1; }
      &.ok app-icon:first-child { color: var(--ok); } &.error { border-color: var(--bad); app-icon:first-child { color: var(--bad); } } }
    @media (max-width: 900px) {
      .shell { grid-template-columns: 1fr; }
      .side { position: fixed; z-index: 60; width: 250px; transform: translateX(-105%); transition: transform .22s; &.open { transform: none; box-shadow: 30px 0 60px rgba(0,0,0,.5); } }
      .mobile-bar { display: flex; align-items: center; gap: 12px; padding: 10px 14px; border-bottom: 1px solid var(--border); position: sticky; top: 0; z-index: 40; background: var(--bg); }
    }
  `],
})
export class AppComponent {
  theme = inject(ThemeService);
  toast = inject(ToastService);
  menu = signal(false);
}
