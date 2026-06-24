import { Component, inject, OnInit, signal, TemplateRef, ViewChild } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { DatePipe } from '@angular/common';
import { MatTableModule } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatButtonToggleModule } from '@angular/material/button-toggle';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatSelectModule } from '@angular/material/select';
import { MatIconModule } from '@angular/material/icon';
import { MatMenuModule } from '@angular/material/menu';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatDialog, MatDialogModule, MatDialogRef } from '@angular/material/dialog';
import { TranslocoModule, TranslocoService } from '@jsverse/transloco';
import { UsersService } from '../../core/api/users.service';
import { RolesService } from '../../core/api/roles.service';
import { ApprovalStatus, UserResponse, RoleResponse } from '../../core/api/models';

@Component({
  selector: 'app-users',
  standalone: true,
  imports: [
    ReactiveFormsModule,
    DatePipe,
    MatTableModule,
    MatButtonModule,
    MatButtonToggleModule,
    MatFormFieldModule,
    MatInputModule,
    MatSelectModule,
    MatProgressBarModule,
    MatIconModule,
    MatMenuModule,
    MatDialogModule,
    TranslocoModule,
  ],
  template: `
    <div class="users-container">
      <div class="page-head">
        <h2>{{ 'users.title' | transloco }}</h2>
        <button mat-flat-button color="primary" (click)="openAdd()">
          <mat-icon>add</mat-icon> {{ 'users.addUser' | transloco }}
        </button>
      </div>

      @if (loading()) {
        <mat-progress-bar mode="indeterminate"></mat-progress-bar>
      }

      <div class="filter-bar">
        <span class="filter-label">{{ 'users.filter' | transloco }}</span>
        <mat-button-toggle-group [value]="filter()" (change)="setFilter($event.value)" hideSingleSelectionIndicator>
          <mat-button-toggle value="Approved">{{ 'users.filterApproved' | transloco }}</mat-button-toggle>
          <mat-button-toggle value="Pending">
            {{ 'users.filterPending' | transloco }}
            @if (pendingCount()) { <span class="count-badge">{{ pendingCount() }}</span> }
          </mat-button-toggle>
          <mat-button-toggle value="Rejected">{{ 'users.filterRejected' | transloco }}</mat-button-toggle>
        </mat-button-toggle-group>
      </div>

      @if (users().length === 0 && !loading()) {
        <p class="empty">{{ 'users.empty' | transloco }}</p>
      } @else {
        <table mat-table [dataSource]="users()" class="users-table mat-elevation-z2">
          <ng-container matColumnDef="email">
            <th mat-header-cell *matHeaderCellDef>{{ 'users.email' | transloco }}</th>
            <td mat-cell *matCellDef="let user">{{ user.email }}</td>
          </ng-container>

          <ng-container matColumnDef="displayName">
            <th mat-header-cell *matHeaderCellDef>{{ 'users.name' | transloco }}</th>
            <td mat-cell *matCellDef="let user">{{ user.displayName }}</td>
          </ng-container>

          <ng-container matColumnDef="role">
            <th mat-header-cell *matHeaderCellDef>{{ 'users.role' | transloco }}</th>
            <td mat-cell *matCellDef="let user">
              @if (filter() === 'Approved') {
                <mat-select
                  [value]="user.roleId"
                  (selectionChange)="changeRole(user, $event.value)"
                  style="min-width:120px"
                >
                  @for (role of rolesForUser(user); track role.id) {
                    <mat-option [value]="role.id">{{ role.name }}</mat-option>
                  }
                </mat-select>
              } @else {
                <span>{{ user.roleName }}</span>
              }
            </td>
          </ng-container>

          <ng-container matColumnDef="requested">
            <th mat-header-cell *matHeaderCellDef>{{ 'overview.requested' | transloco }}</th>
            <td mat-cell *matCellDef="let user">{{ user.createdAt ? (user.createdAt | date:'mediumDate') : '—' }}</td>
          </ng-container>

          <ng-container matColumnDef="status">
            <th mat-header-cell *matHeaderCellDef>{{ 'users.status' | transloco }}</th>
            <td mat-cell *matCellDef="let user">
              <span class="chip" [class.chip-active]="user.isActive" [class.chip-disabled]="!user.isActive">
                {{ user.isActive ? ('common.active' | transloco) : ('common.disabled' | transloco) }}
              </span>
            </td>
          </ng-container>

          <ng-container matColumnDef="actions">
            <th mat-header-cell *matHeaderCellDef>{{ 'users.actions' | transloco }}</th>
            <td mat-cell *matCellDef="let user">
              @if (filter() === 'Approved') {
                <button mat-stroked-button [color]="user.isActive ? 'warn' : 'primary'"
                  (click)="toggleActive(user)" [disabled]="loading()">
                  <mat-icon>{{ user.isActive ? 'block' : 'check_circle' }}</mat-icon>
                  {{ user.isActive ? ('common.disable' | transloco) : ('common.enable' | transloco) }}
                </button>
              } @else {
                <div class="row-actions">
                  <button mat-flat-button color="primary" [matMenuTriggerFor]="approveMenu"
                    (menuOpened)="approveSelection[user.id] = user.roleId" [disabled]="loading()">
                    <mat-icon>how_to_reg</mat-icon> {{ 'users.approve' | transloco }}
                  </button>
                  <mat-menu #approveMenu="matMenu">
                    <div class="approve-panel" (click)="$event.stopPropagation()">
                      <mat-form-field appearance="outline" subscriptSizing="dynamic" class="approve-field">
                        <mat-label>{{ 'users.approveAs' | transloco }}</mat-label>
                        <mat-select [(value)]="approveSelection[user.id]">
                          @for (r of activeRoles(); track r.id) {
                            <mat-option [value]="r.id">{{ r.name }}</mat-option>
                          }
                        </mat-select>
                      </mat-form-field>
                      <button mat-flat-button color="primary" class="approve-confirm"
                        (click)="approve(user)" [disabled]="loading()">
                        {{ 'users.confirm' | transloco }}
                      </button>
                    </div>
                  </mat-menu>
                  @if (filter() === 'Pending') {
                    <button mat-stroked-button color="warn" (click)="reject(user)" [disabled]="loading()">
                      <mat-icon>block</mat-icon> {{ 'users.reject' | transloco }}
                    </button>
                  }
                </div>
              }
            </td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="displayedColumns()"></tr>
          <tr mat-row *matRowDef="let row; columns: displayedColumns();"></tr>
        </table>
      }
    </div>

    <!-- Add user dialog -->
    <ng-template #addDialog>
      <h2 mat-dialog-title>{{ 'users.addUser' | transloco }}</h2>
      <mat-dialog-content>
        <form [formGroup]="addForm" (ngSubmit)="addUser()" class="dialog-form">
          <mat-form-field appearance="outline">
            <mat-label>{{ 'users.email' | transloco }}</mat-label>
            <input matInput type="email" formControlName="email" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>{{ 'users.displayName' | transloco }}</mat-label>
            <input matInput formControlName="displayName" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>{{ 'users.password' | transloco }}</mat-label>
            <input matInput type="password" formControlName="password" />
          </mat-form-field>
          <mat-form-field appearance="outline">
            <mat-label>{{ 'users.role' | transloco }}</mat-label>
            <mat-select formControlName="roleId">
              @for (role of activeRoles(); track role.id) {
                <mat-option [value]="role.id">{{ role.name }}</mat-option>
              }
            </mat-select>
          </mat-form-field>
        </form>
      </mat-dialog-content>
      <mat-dialog-actions align="end">
        <button mat-button mat-dialog-close>{{ 'common.cancel' | transloco }}</button>
        <button mat-flat-button color="primary" (click)="addUser()" [disabled]="addForm.invalid || loading()">
          <mat-icon>add</mat-icon> {{ 'users.addUser' | transloco }}
        </button>
      </mat-dialog-actions>
    </ng-template>
  `,
  styles: [`
    .users-container { padding: 24px; }
    .page-head { display: flex; align-items: center; justify-content: space-between; margin-bottom: 16px; gap: 12px; }
    .page-head h2 { margin: 0; }
    .filter-bar { display: flex; align-items: center; gap: 12px; margin-bottom: 16px; flex-wrap: wrap; }
    .filter-label { color: var(--muted); font-size: 0.85rem; }
    .count-badge { background: #fff4e5; color: #f57c00; font-size: 0.72rem; font-weight: 700; min-width: 18px; height: 18px; padding: 0 5px; border-radius: 9px; display: inline-flex; align-items: center; justify-content: center; margin-inline-start: 6px; }
    .users-table { width: 100%; }
    .row-actions { display: flex; align-items: center; gap: 8px; }
    .approve-panel { display: flex; flex-direction: column; gap: 10px; padding: 12px; min-width: 200px; }
    .approve-field { width: 100%; }
    .approve-confirm { width: 100%; }
    .empty { color: var(--muted); padding: 24px 0; }
    .dialog-form { display: flex; flex-direction: column; gap: 12px; min-width: 320px; padding-top: 8px; }
    /* status chip styles are global (theme-aware) — see styles.scss */
  `],
})
export class UsersComponent implements OnInit {
  private usersService = inject(UsersService);
  private rolesService = inject(RolesService);
  private snack = inject(MatSnackBar);
  private fb = inject(FormBuilder);
  private transloco = inject(TranslocoService);
  private dialog = inject(MatDialog);

  @ViewChild('addDialog') addDialog!: TemplateRef<unknown>;
  private dialogRef?: MatDialogRef<unknown>;

  users = signal<UserResponse[]>([]);
  roles = signal<RoleResponse[]>([]);
  loading = signal(false);
  filter = signal<ApprovalStatus>('Approved');
  pendingCount = signal(0);

  // Per-user selected role for the approve role-confirm dropdown (keyed by user id).
  approveSelection: Record<number, number> = {};

  displayedColumns() {
    return this.filter() === 'Approved'
      ? ['email', 'displayName', 'role', 'status', 'actions']
      : ['email', 'displayName', 'role', 'requested', 'status', 'actions'];
  }

  addForm = this.fb.nonNullable.group({
    email: ['', [Validators.required, Validators.email]],
    displayName: ['', Validators.required],
    password: ['', Validators.required],
    roleId: [0 as number, [Validators.required, Validators.min(1)]],
  });

  activeRoles() {
    return this.roles().filter(r => r.isActive);
  }

  rolesForUser(user: UserResponse): RoleResponse[] {
    const active = this.roles().filter(r => r.isActive);
    const current = this.roles().find(r => r.id === user.roleId);
    if (current && !current.isActive) {
      return [current, ...active];
    }
    return active;
  }

  ngOnInit() {
    this.loadAll();
  }

  setFilter(status: ApprovalStatus) {
    this.filter.set(status);
    this.loadUsers();
  }

  openAdd() {
    const firstRole = this.activeRoles()[0]?.id ?? 0;
    this.addForm.reset({ email: '', displayName: '', password: '', roleId: firstRole });
    this.dialogRef = this.dialog.open(this.addDialog, { width: '440px' });
  }

  private loadAll() {
    this.loading.set(true);
    this.rolesService.list().subscribe({
      next: (roles) => {
        this.roles.set(roles);
        this.loadUsers();
        this.refreshPendingCount();
      },
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to load roles', 'OK', { duration: 4000 }); },
    });
  }

  private loadUsers() {
    this.loading.set(true);
    this.usersService.list(this.filter()).subscribe({
      next: (users) => { this.users.set(users); this.loading.set(false); },
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to load users', 'OK', { duration: 4000 }); },
    });
  }

  private refreshPendingCount() {
    this.usersService.list('Pending').subscribe({
      next: (users) => this.pendingCount.set(users.length),
      error: () => { /* badge is best-effort */ },
    });
  }

  addUser() {
    if (this.addForm.invalid) return;
    this.loading.set(true);
    const val = this.addForm.getRawValue();
    this.usersService.create({ email: val.email, displayName: val.displayName, password: val.password, roleId: val.roleId }).subscribe({
      next: () => {
        this.dialogRef?.close();
        this.addForm.reset();
        this.loadAll();
      },
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to create user', 'OK', { duration: 4000 }); },
    });
  }

  changeRole(user: UserResponse, roleId: number) {
    this.loading.set(true);
    this.usersService.update(user.id, { roleId }).subscribe({
      next: () => this.loadUsers(),
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to update role', 'OK', { duration: 4000 }); this.loadUsers(); },
    });
  }

  toggleActive(user: UserResponse) {
    if (user.isActive && !confirm(this.transloco.translate('common.confirmDisable', { name: user.email }))) return;
    this.loading.set(true);
    this.usersService.update(user.id, { isActive: !user.isActive }).subscribe({
      next: () => this.loadUsers(),
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to update user', 'OK', { duration: 4000 }); },
    });
  }

  approve(user: UserResponse) {
    const roleId = this.approveSelection[user.id] ?? user.roleId;
    this.loading.set(true);
    this.usersService.approve(user.id, roleId).subscribe({
      next: () => {
        this.users.update(list => list.filter(u => u.id !== user.id));
        this.refreshPendingCount();
        this.loading.set(false);
      },
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to approve user', 'OK', { duration: 4000 }); },
    });
  }

  reject(user: UserResponse) {
    if (!confirm(this.transloco.translate('users.confirmReject', { name: user.email }))) return;
    this.loading.set(true);
    this.usersService.reject(user.id).subscribe({
      next: () => {
        this.users.update(list => list.filter(u => u.id !== user.id));
        this.refreshPendingCount();
        this.loading.set(false);
      },
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to reject user', 'OK', { duration: 4000 }); },
    });
  }
}
