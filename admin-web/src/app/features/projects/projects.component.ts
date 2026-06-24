import { Component, inject, OnInit, signal, TemplateRef, ViewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatIconModule } from '@angular/material/icon';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
import { ProjectsService } from '../../core/api/projects.service';
import { ProjectResponse } from '../../core/api/models';

@Component({
  selector: 'app-projects',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    MatTableModule,
    MatButtonModule,
    MatFormFieldModule,
    MatInputModule,
    MatProgressBarModule,
    MatIconModule,
    MatDialogModule,
    TranslocoModule,
  ],
  template: `
    <div class="projects-container">
      <div class="page-head">
        <h2>{{ 'projects.title' | transloco }}</h2>
        <button mat-flat-button color="primary" (click)="openAdd()">
          <mat-icon>add</mat-icon> {{ 'projects.addProject' | transloco }}
        </button>
      </div>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate"></mat-progress-bar>
      }

      <table mat-table [dataSource]="projects()" class="projects-table mat-elevation-z2">
        <ng-container matColumnDef="key">
          <th mat-header-cell *matHeaderCellDef>{{ 'projects.key' | transloco }}</th>
          <td mat-cell *matCellDef="let project"><code>{{ project.key }}</code></td>
        </ng-container>

        <ng-container matColumnDef="name">
          <th mat-header-cell *matHeaderCellDef>{{ 'projects.name' | transloco }}</th>
          <td mat-cell *matCellDef="let project">{{ project.name }}</td>
        </ng-container>

        <ng-container matColumnDef="status">
          <th mat-header-cell *matHeaderCellDef>{{ 'projects.status' | transloco }}</th>
          <td mat-cell *matCellDef="let project">
            <span class="chip" [class.chip-active]="project.isActive" [class.chip-disabled]="!project.isActive">
              {{ project.isActive ? ('common.active' | transloco) : ('common.disabled' | transloco) }}
            </span>
          </td>
        </ng-container>

        <ng-container matColumnDef="actions">
          <th mat-header-cell *matHeaderCellDef>{{ 'projects.actions' | transloco }}</th>
          <td mat-cell *matCellDef="let project">
            <button mat-stroked-button [color]="project.isActive ? 'warn' : 'primary'"
              (click)="toggleActive(project)" [disabled]="loading()">
              <mat-icon>{{ project.isActive ? 'block' : 'check_circle' }}</mat-icon>
              {{ project.isActive ? ('common.disable' | transloco) : ('common.enable' | transloco) }}
            </button>
          </td>
        </ng-container>

        <tr mat-header-row *matHeaderRowDef="displayedColumns"></tr>
        <tr mat-row *matRowDef="let row; columns: displayedColumns;"></tr>
      </table>
    </div>

    <!-- Add project dialog -->
    <ng-template #addDialog>
      <h2 mat-dialog-title>{{ 'projects.addProject' | transloco }}</h2>
      <mat-dialog-content>
        <form [formGroup]="addForm" (ngSubmit)="addProject()" class="dialog-form">
          <mat-form-field appearance="outline">
            <mat-label>{{ 'projects.key' | transloco }}</mat-label>
            <input matInput formControlName="key" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>{{ 'projects.name' | transloco }}</mat-label>
            <input matInput formControlName="name" />
          </mat-form-field>
        </form>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button mat-dialog-close>{{ 'common.cancel' | transloco }}</button>
        <button mat-flat-button color="primary" (click)="addProject()" [disabled]="addForm.invalid || loading()">
          <mat-icon>add</mat-icon> {{ 'projects.addProject' | transloco }}
        </button>
      </mat-dialog-actions>
    </ng-template>
  `,
  styles: [`
    .projects-container { padding: 24px; }
    .page-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 16px; gap: 12px; }
    .page-head h2 { margin: 0; }
    .projects-table { width: 100%; }
    .dialog-form { display: flex; flex-direction: column; gap: 12px; min-width: 320px; padding-top: 8px; }
    /* status chip styles are global (theme-aware) — see styles.scss */
  `],
})
export class ProjectsComponent implements OnInit {
  private projectsService = inject(ProjectsService);
  private snack = inject(MatSnackBar);
  private fb = inject(FormBuilder);
  private transloco = inject(TranslocoService);
  private dialog = inject(MatDialog);

  @ViewChild('addDialog') addDialog!: TemplateRef<unknown>;
  private dialogRef?: MatDialogRef<unknown>;

  projects = signal<ProjectResponse[]>([]);
  loading = signal(false);

  displayedColumns = ['key', 'name', 'status', 'actions'];

  addForm = this.fb.nonNullable.group({
    key: ['', Validators.required],
    name: ['', Validators.required],
  });

  ngOnInit() {
    this.load();
  }

  openAdd() {
    this.addForm.reset({ key: '', name: '' });
    this.dialogRef = this.dialog.open(this.addDialog, { width: '440px' });
  }

  private load() {
    this.loading.set(true);
    this.projectsService.list().subscribe({
      next: (projects) => { this.projects.set(projects); this.loading.set(false); },
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to load projects', 'OK', { duration: 4000 }); },
    });
  }

  addProject() {
    if (this.addForm.invalid) return;
    this.loading.set(true);
    const val = this.addForm.getRawValue();
    this.projectsService.create({ key: val.key, name: val.name }).subscribe({
      next: () => {
        this.dialogRef?.close();
        this.addForm.reset();
        this.load();
      },
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to create project', 'OK', { duration: 4000 }); },
    });
  }

  toggleActive(project: ProjectResponse) {
    if (project.isActive && !confirm(this.transloco.translate('common.confirmDisable', { name: project.key }))) return;
    this.loading.set(true);
    this.projectsService.update(project.id, { isActive: !project.isActive }).subscribe({
      next: () => this.load(),
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to update project', 'OK', { duration: 4000 }); },
    });
  }
}
