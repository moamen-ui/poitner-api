import { inject, Injectable } from '@angular/core';
import { Api } from './api';
import { CreateRoleRequest, RoleResponse, UpdateRoleRequest } from './models';

@Injectable({ providedIn: 'root' })
export class RolesService {
  private api = inject(Api);
  list() { return this.api.get<RoleResponse[]>('/api/admin/roles'); }
  create(r: CreateRoleRequest) { return this.api.post<RoleResponse>('/api/admin/roles', r); }
  update(id: number, r: UpdateRoleRequest) { return this.api.patch<RoleResponse>(`/api/admin/roles/${id}`, r); }
}
