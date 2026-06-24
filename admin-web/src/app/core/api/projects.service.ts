import { inject, Injectable } from '@angular/core';
import { Api } from './api';
import { CreateProjectRequest, ProjectResponse, UpdateProjectRequest } from './models';

@Injectable({ providedIn: 'root' })
export class ProjectsService {
  private api = inject(Api);
  list() { return this.api.get<ProjectResponse[]>('/api/admin/projects'); }
  create(p: CreateProjectRequest) { return this.api.post<ProjectResponse>('/api/admin/projects', p); }
  update(id: number, p: UpdateProjectRequest) { return this.api.patch<ProjectResponse>(`/api/admin/projects/${id}`, p); }
}
