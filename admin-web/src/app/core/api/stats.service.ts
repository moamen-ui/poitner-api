import { inject, Injectable } from '@angular/core';
import { Api } from './api';
import { StatsResponse } from './models';

@Injectable({ providedIn: 'root' })
export class StatsService {
  private api = inject(Api);
  get() { return this.api.get<StatsResponse>('/api/admin/stats'); }
}
