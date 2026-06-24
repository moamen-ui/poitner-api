import { Component, inject, OnInit, signal, TemplateRef, ViewChild } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { MatButtonModule } from '@angular/material/button';
import { MatCheckboxModule } from '@angular/material/checkbox';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatIconModule } from '@angular/material/icon';
import { MatInputModule } from '@angular/material/input';
import { MatSlideToggleModule } from '@angular/material/slide-toggle';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatTableModule } from '@angular/material/table';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
import { RoleResponse } from '../../core/api/models';
import { RolesService } from '../../core/api/roles.service';

@Component({
  selector: 'app-roles',
  standalone: true,
  imports: [
    FormsModule,
    MatTableModule,
    MatSlideToggleModule,
    MatButtonModule,
    MatInputModule,
    MatFormFieldModule,
    MatCheckboxModule,
    MatIconModule,
    MatDialogModule,
    TranslocoModule,
  ],
  template: `
    <div class="roles-page">
      <div class="page-head">
        <h2>{{ 'roles.title' | transloco }}</h2>
        <button mat-flat-button color="primary" (click)="openAdd()">
          <mat-icon>add</mat-icon> {{ 'roles.addRole' | transloco }}
        </button>
      </div>

      <!-- Roles table -->
      @if (roles().length > 0) {
        <table mat-table [dataSource]="roles()" class="mat-elevation-z2 roles-table">
          <!-- Name column -->
          <ng-container matColumnDef="name">
            <th mat-header-cell *matHeaderCellDef>{{ 'roles.name' | transloco }}</th>
            <td mat-cell *matCellDef="let role">
              {{ role.name }}
              @if (role.isSystem) {
                <span class="chip chip-neutral system-chip">{{ 'roles.system' | transloco }}</span>
              }
            </td>
          </ng-container>

          <!-- Grants admin column -->
          <ng-container matColumnDef="grantsAdmin">
            <th mat-header-cell *matHeaderCellDef>{{ 'roles.grantsAdmin' | transloco }}</th>
            <td mat-cell *matCellDef="let role">
              <mat-slide-toggle
                [checked]="role.grantsAdmin"
                [disabled]="role.isSystem"
                (change)="toggleGrantsAdmin(role, $event.checked)"
              />
            </td>
          </ng-container>

          <!-- Status column -->
          <ng-container matColumnDef="status">
            <th mat-header-cell *matHeaderCellDef>{{ 'roles.status' | transloco }}</th>
            <td mat-cell *matCellDef="let role">
              <span class="chip" [class.chip-active]="role.isActive" [class.chip-disabled]="!role.isActive">
                {{ (role.isActive ? 'common.active' : 'common.disabled') | transloco }}
              </span>
            </td>
          </ng-container>

          <!-- Actions column -->
          <ng-container matColumnDef="actions">
            <th mat-header-cell *matHeaderCellDef>{{ 'roles.actions' | transloco }}</th>
            <td mat-cell *matCellDef="let role">
              @if (!role.isSystem) {
                <button mat-stroked-button (click)="renameRole(role)" style="margin-inline-end:8px">
                  <mat-icon>edit</mat-icon> {{ 'common.rename' | transloco }}
                </button>
                <button
                  mat-stroked-button
                  [color]="role.isActive ? 'warn' : 'primary'"
                  (click)="toggleActive(role)"
                >
                  <mat-icon>{{ role.isActive ? 'block' : 'check_circle' }}</mat-icon>
                  {{ role.isActive ? ('common.disable' | transloco) : ('common.enable' | transloco) }}
                </button>
              }
            </td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="displayedColumns"></tr>
          <tr mat-row *matRowDef="let row; columns: displayedColumns;"></tr>
        </table>
      }
    </div>

    <!-- Add role dialog -->
    <ng-template #addDialog>
      <h2 mat-dialog-title>{{ 'roles.addRole' | transloco }}</h2>
      <mat-dialog-content>
        <div class="dialog-form">
          <mat-form-field appearance="outline">
            <mat-label>{{ 'roles.name' | transloco }}</mat-label>
            <input matInput [(ngModel)]="newName" (keydown.enter)="addRole()" />
          </mat-form-field>
          <mat-checkbox [(ngModel)]="newGrantsAdmin">{{ 'roles.grantsAdmin' | transloco }}</mat-checkbox>
        </div>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button mat-dialog-close>{{ 'common.cancel' | transloco }}</button>
        <button mat-flat-button color="primary" [disabled]="!newName.trim()" (click)="addRole()">
          <mat-icon>add</mat-icon> {{ 'roles.addRole' | transloco }}
        </button>
      </mat-dialog-actions>
    </ng-template>
  `,
  styles: [`
    .roles-page { padding: 24px; }
    .page-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 16px; gap: 12px; }
    .page-head h2 { margin: 0; }
    .roles-table { width: 100%; }
    .system-chip { margin-inline-start: 8px; font-size: 10px; }
    .dialog-form { display: flex; flex-direction: column; gap: 16px; min-width: 320px; padding-top: 8px; }
  `],
})
export class RolesComponent implements OnInit {
  private rolesService = inject(RolesService);
  private snack = inject(MatSnackBar);
  private transloco = inject(TranslocoService);
  private dialog = inject(MatDialog);

  @ViewChild('addDialog') addDialog!: TemplateRef<unknown>;
  private dialogRef?: MatDialogRef<unknown>;

  roles = signal<RoleResponse[]>([]);
  displayedColumns = ['name', 'grantsAdmin', 'status', 'actions'];

  newName = '';
  newGrantsAdmin = false;

  ngOnInit() {
    this.load();
  }

  openAdd() {
    this.newName = '';
    this.newGrantsAdmin = false;
    this.dialogRef = this.dialog.open(this.addDialog, { width: '440px' });
  }

  load() {
    this.rolesService.list().subscribe({
      next: (data) => this.roles.set(data),
      error: (e) => this.snack.open(e.message || 'Failed to load roles', 'OK', { duration: 4000 }),
    });
  }

  addRole() {
    const name = this.newName.trim();
    if (!name) return;
    this.rolesService.create({ name, grantsAdmin: this.newGrantsAdmin }).subscribe({
      next: () => {
        this.dialogRef?.close();
        this.newName = '';
        this.newGrantsAdmin = false;
        this.load();
      },
      error: (e) => this.snack.open(e.message || 'Failed to create role', 'OK', { duration: 4000 }),
    });
  }

  toggleGrantsAdmin(role: RoleResponse, grantsAdmin: boolean) {
    this.rolesService.update(role.id, { grantsAdmin }).subscribe({
      next: () => this.load(),
      error: (e) => this.snack.open(e.message || 'Failed to update role', 'OK', { duration: 4000 }),
    });
  }

  renameRole(role: RoleResponse) {
    const name = window.prompt('New name for role:', role.name);
    if (!name || !name.trim() || name.trim() === role.name) return;
    this.rolesService.update(role.id, { name: name.trim() }).subscribe({
      next: () => this.load(),
      error: (e) => this.snack.open(e.message || 'Failed to rename role', 'OK', { duration: 4000 }),
    });
  }

  toggleActive(role: RoleResponse) {
    if (role.isActive && !confirm(this.transloco.translate('common.confirmDisable', { name: role.name }))) return;
    this.rolesService.update(role.id, { isActive: !role.isActive }).subscribe({
      next: () => this.load(),
      error: (e) => this.snack.open(e.message || 'Failed to update role', 'OK', { duration: 4000 }),
    });
  }
}
