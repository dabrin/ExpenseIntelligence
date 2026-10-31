import { HttpClient, HttpErrorResponse, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import {
  AssistantAnswer, AssistantStatus, ChatTurn, AuditEntry, Benchmark, Diagnostics, DoctorReport, Expense, ExpenseInput, Facets, Filters, Insight, IntegrityReport, Paged, Summary, ApiProblem, ImportPreview, ImportResult, Timeline, HistoryItem, DatasetSummary, CompareResponse, ExpenseState, BulkRequest, BulkPreview, BulkResult,
} from './models';

export function toParams(f: Filters, extra: Record<string, string | number | boolean | undefined> = {}): HttpParams {
  let p = new HttpParams();
  if (f.from) p = p.set('from', f.from);
  if (f.to) p = p.set('to', f.to);
  for (const [k, v] of [['departamentos', f.departamentos], ['categorias', f.categorias], ['monedas', f.monedas], ['metodosPago', f.metodosPago]] as const)
    for (const item of v) p = p.append(k, item);
  if (f.estado) p = p.set('estado', f.estado);
  for (const id of f.ids) p = p.append('ids', id);
  if (f.q.trim()) p = p.set('q', f.q.trim());
  for (const [k, v] of Object.entries(extra)) if (v !== undefined) p = p.set(k, String(v));
  return p;
}

export function asProblem(err: unknown): ApiProblem {
  if (err instanceof HttpErrorResponse) {
    if (err.status === 0) return { status: 0, title: 'No hay conexión con el servidor.' };
    const b = err.error as Partial<ApiProblem> | null;
    return { status: err.status, title: b?.title ?? err.statusText ?? 'Error inesperado', errors: b?.errors, currentVersion: b?.currentVersion, conflicts: b?.conflicts, conflictCount: b?.conflictCount };
  }
  return { status: -1, title: 'Error inesperado' };
}

@Injectable({ providedIn: 'root' })
export class ApiService {
  private http = inject(HttpClient);
  private actor = { headers: { 'X-Actor': 'web-ui' } };

  facets() { return this.http.get<Facets>('/api/facets'); }
  list(f: Filters, sort: string, desc: boolean, page: number, pageSize: number, deleted = false): Observable<Paged<Expense>> {
    return this.http.get<Paged<Expense>>('/api/expenses', { params: toParams(f, { sort, desc, page, pageSize, eliminados: deleted || undefined }) });
  }
  create(i: ExpenseInput) { return this.http.post<{ expense: Expense; warnings: string[] }>('/api/expenses', i, this.actor); }
  update(id: number, version: number, data: ExpenseInput) { return this.http.put<{ expense: Expense; warnings: string[] }>(`/api/expenses/${id}`, { version, data }, this.actor); }
  remove(id: number, version: number) { return this.http.delete<Expense>(`/api/expenses/${id}`, { params: { version }, ...this.actor }); }
  setState(id: number, version: number, estado: ExpenseState, reason?: string) { return this.http.post<Expense>(`/api/expenses/${id}/state`, { estado, version, reason }, this.actor); }
  restore(id: number) { return this.http.post<Expense>(`/api/expenses/${id}/restore`, {}, this.actor); }
  bulkLimits() { return this.http.get<{ maxItems: number; datasetId: string }>('/api/expenses/bulk/limits'); }
  bulkPreview(op: 'approve' | 'reject', body: BulkRequest) { return this.http.post<BulkPreview>(`/api/expenses/bulk/${op}/preview`, body); }
  bulkRun(op: 'approve' | 'reject', body: BulkRequest) { return this.http.post<BulkResult>(`/api/expenses/bulk/${op}`, body, this.actor); }
  summary(f: Filters, granularity: string) { return this.http.get<Summary>('/api/analytics/summary', { params: toParams(f, { granularity }) }); }
  timeline(f: Filters) { return this.http.get<Timeline>('/api/analytics/timeline', { params: toParams(f) }); }
  insights(f: Filters) { return this.http.get<{ revision: number; insights: Insight[] }>('/api/insights', { params: toParams(f) }); }
  doctor() { return this.http.get<DoctorReport>('/api/data-doctor'); }
  doctorFix(code: string) { return this.http.post<{ code: string; fixedRecords: number }>(`/api/data-doctor/fix/${code}`, {}, this.actor); }
  diagnostics() { return this.http.get<Diagnostics>('/api/diagnostics'); }
  integrity() { return this.http.post<IntegrityReport>('/api/diagnostics/integrity-check', {}); }
  benchmark() { return this.http.post<Benchmark>('/api/diagnostics/benchmark', {}); }
  history(take = 150, tipo = 'all') { return this.http.get<HistoryItem[]>('/api/history', { params: { take, tipo } }); }
  audit(take = 30) { return this.http.get<AuditEntry[]>('/api/audit', { params: { take } }); }
  ask(question: string, lastIntent?: string, lastQuestion?: string, history?: ChatTurn[]) { return this.http.post<AssistantAnswer>('/api/assistant/query', { question, lastIntent, lastQuestion, history }); }
  assistantStatus() { return this.http.get<AssistantStatus>('/api/assistant/status'); }
  importPreview(file: File) { const f = new FormData(); f.append('file', file, file.name); return this.http.post<ImportPreview>('/api/import/preview', f); }
  importCommit(token: string, mode: 'new' | 'merge' | 'switch' | 'reset', datasetId?: string) { return this.http.post<ImportResult>('/api/import/commit', { token, mode, datasetId }, this.actor); }
  datasets() { return this.http.get<DatasetSummary[]>('/api/datasets'); }
  activateDataset(id: string, reset: boolean) { return this.http.post<DatasetSummary>(`/api/datasets/${id}/activate`, { reset }, this.actor); }
  deleteDataset(id: string) { return this.http.delete<void>(`/api/datasets/${id}`, this.actor); }
  factoryReset(confirm: string) { return this.http.post<{ ok: boolean }>('/api/datasets/factory-reset', { confirm }, this.actor); }
  compare(q: { a: string; aVariant: string; b: string; bVariant: string; key: string; kind: string; q: string; page: number }) {
    const params: Record<string, string | number> = { a: q.a, aVariant: q.aVariant, b: q.b, bVariant: q.bVariant, key: q.key, page: q.page, pageSize: 20 };
    if (q.kind) params['kind'] = q.kind; if (q.q.trim()) params['q'] = q.q.trim();
    return this.http.get<CompareResponse>('/api/datasets/compare', { params });
  }
  examples() { return this.http.get<string[]>('/api/assistant/examples'); }
}
