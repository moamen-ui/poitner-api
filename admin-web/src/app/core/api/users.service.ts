import { inject, Injectable } from '@angular/core';
import { Api } from './api';
import { ApprovalStatus, CreateUserRequest, UpdateUserRequest, UserResponse } from './models';

@Injectable({ providedIn: 'root' })
export class UsersService {
  private api = inject(Api);
  list(status?: ApprovalStatus) {
    const q = status ? `?status=${status.toLowerCase()}` : '';
    return this.api.get<UserResponse[]>(`/api/admin/users${q}`);
  }
  create(u: CreateUserRequest) { return this.api.post<UserResponse>('/api/admin/users', u); }
  update(id: number, u: UpdateUserRequest) { return this.api.patch<UserResponse>(`/api/admin/users/${id}`, u); }
  approve(id: number, roleId: number) { return this.api.post<UserResponse>(`/api/admin/users/${id}/approve`, { roleId }); }
  reject(id: number) { return this.api.post<UserResponse>(`/api/admin/users/${id}/reject`, {}); }
}
