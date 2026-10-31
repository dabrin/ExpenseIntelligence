import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { catchError, of } from 'rxjs';
import { ApiService } from '../../core/api.service';
import { AssistantChat } from '../../core/assistant-chat.service';
import { ChatPanelComponent } from '../../shared/chat-panel.component';
import { MascotComponent } from '../../shared/mascot.component';

@Component({
  selector: 'app-assistant', standalone: true, imports: [MascotComponent, ChatPanelComponent], changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
  <header class="head rise hd"><div class="row"><app-mascot pose="saludo" [height]="96" /><div><h1>Prestamito</h1>
    <p class="muted">Pregúntame con tus palabras: <em>«¿cuánto gastó Ventas en USD en marzo?»</em> o pídeme ir a una pantalla: <em>«llévame a los gastos pendientes»</em>. @if (status().llm) { Respondo con IA (<span class="mono">{{ status().model }}</span>) usando solo los datos de este sistema: no invento cifras y rechazo lo que no tenga que ver con los gastos. } @else { Entiendo departamentos, categorías, monedas y meses, y recuerdo la pregunta anterior. Respondo con reglas: consulto tus datos y no invento cifras. }
    La conversación te acompaña en toda la aplicación desde el botón flotante.</p></div></div>
    <span class="spacer"></span><button type="button" class="btn sm" (click)="chat.reset()">Nueva conversación</button></header>
  <section class="card chat"><app-chat-panel /></section>`,
  styles: [`
    .hd { display: flex; align-items: flex-start; justify-content: space-between; gap: 12px; flex-wrap: wrap; margin-bottom: 18px; p { margin: 4px 0 0; } .row { flex-wrap: nowrap; align-items: center; flex: 1 1 420px; min-width: 0; } .spacer { display: none; } .row > div { min-width: 0; } }
    .chat { padding: 0; overflow: hidden; height: min(66vh, 720px); min-height: 420px; display: grid; }
  `],
})
export class AssistantComponent {
  private api = inject(ApiService);
  chat = inject(AssistantChat);
  status = toSignal(this.api.assistantStatus().pipe(catchError(() => of({ llm: false, model: null }))), { initialValue: { llm: false, model: null as string | null } });
}
