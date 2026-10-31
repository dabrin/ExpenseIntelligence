import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
  { path: 'dashboard', title: 'Dashboard · Insta Expenses', loadComponent: () => import('./features/dashboard/dashboard.component').then(m => m.DashboardComponent) },
  { path: 'maquina-del-tiempo', title: 'Máquina del tiempo · Insta Expenses', loadComponent: () => import('./features/timeline/timeline.component').then(m => m.TimelineComponent) },
  { path: 'gastos', title: 'Gastos · Insta Expenses', loadComponent: () => import('./features/expenses/expenses.component').then(m => m.ExpensesComponent) },
  { path: 'historial', title: 'Historial · Insta Expenses', loadComponent: () => import('./features/history/history.component').then(m => m.HistoryComponent) },
  { path: 'archivos', title: 'Archivos · Insta Expenses', loadComponent: () => import('./features/files/files.component').then(m => m.FilesComponent) },
  { path: 'importar', title: 'Importar · Insta Expenses', loadComponent: () => import('./features/import/import.component').then(m => m.ImportComponent) },
  { path: 'data-doctor', title: 'Data Doctor · Insta Expenses', loadComponent: () => import('./features/doctor/doctor.component').then(m => m.DoctorComponent) },
  { path: 'asistente', title: 'Asistente · Insta Expenses', loadComponent: () => import('./features/assistant/assistant.component').then(m => m.AssistantComponent) },
  { path: 'diagnostico', title: 'Diagnóstico · Insta Expenses', loadComponent: () => import('./features/diagnostics/diagnostics.component').then(m => m.DiagnosticsComponent) },
  { path: '**', redirectTo: 'dashboard' },
];
