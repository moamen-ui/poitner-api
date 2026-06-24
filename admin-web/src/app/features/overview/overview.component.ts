import { Component, inject, signal, OnInit, ViewChild } from '@angular/core';
import { DatePipe } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatTableModule, MatTableDataSource } from '@angular/material/table';
import { MatButtonModule } from '@angular/material/button';
import { MatProgressBarModule } from '@angular/material/progress-bar';
import { MatSnackBar } from '@angular/material/snack-bar';
import { MatIconModule } from '@angular/material/icon';
import { MatSortModule, MatSort } from '@angular/material/sort';
import { MatMenuModule } from '@angular/material/menu';
import { MatFormFieldModule } from '@angular/material/form-field';
import { MatSelectModule } from '@angular/material/select';
import { FormsModule } from '@angular/forms';
import { TranslocoModule } from '@jsverse/transloco';
import { StatsService } from '../../core/api/stats.service';
import { UsersService } from '../../core/api/users.service';
import { RolesService } from '../../core/api/roles.service';
import { StatsResponse, ProjectStats, UserResponse, RoleResponse } from '../../core/api/models';

@Component({
  selector: 'app-overview',
  standalone: true,
  imports: [
    MatCardModule,
    MatTableModule,
    MatButtonModule,
    MatProgressBarModule,
    MatIconModule,
    MatSortModule,
    MatMenuModule,
    MatFormFieldModule,
    MatSelectModule,
    FormsModule,
    DatePipe,
    TranslocoModule,
  ],
  template: `
    @if (loading()) {
      <mat-progress-bar mode="indeterminate" class="progress-bar"></mat-progress-bar>
    }

    @if (stats()) {
      <div class="stat-grid">
        <mat-card class="stat-card" appearance="outlined">
          <mat-card-content>
            <div class="stat-icon icon-slate"><mat-icon>folder</mat-icon></div>
            <div class="stat-text"><div class="stat-value">{{ stats()!.totals.projects }}</div><div class="stat-label">{{ 'overview.projects' | transloco }}</div></div>
          </mat-card-content>
        </mat-card>
        <mat-card class="stat-card" appearance="outlined">
          <mat-card-content>
            <div class="stat-icon icon-slate"><mat-icon>group</mat-icon></div>
            <div class="stat-text"><div class="stat-value">{{ stats()!.totals.users }}</div><div class="stat-label">{{ 'overview.users' | transloco }}</div></div>
          </mat-card-content>
        </mat-card>
        <mat-card class="stat-card" appearance="outlined">
          <mat-card-content>
            <div class="stat-icon icon-slate"><mat-icon>chat_bubble_outline</mat-icon></div>
            <div class="stat-text">
              <div class="stat-value">{{ stats()!.totals.comments }}</div>
              <div class="stat-label">{{ 'overview.comments' | transloco }}</div>
              @if (stats()!.totals.privateComments > 0) {
                <div class="stat-subnote">{{ 'overview.privateHidden' | transloco: { count: stats()!.totals.privateComments } }}</div>
              }
            </div>
          </mat-card-content>
        </mat-card>
        <mat-card class="stat-card accent-blue" appearance="outlined">
          <mat-card-content>
            <div class="stat-icon icon-blue"><mat-icon>radio_button_unchecked</mat-icon></div>
            <div class="stat-text"><div class="stat-value">{{ stats()!.totals.open }}</div><div class="stat-label">{{ 'overview.open' | transloco }}</div></div>
          </mat-card-content>
        </mat-card>
        <mat-card class="stat-card accent-amber" appearance="outlined">
          <mat-card-content>
            <div class="stat-icon icon-amber"><mat-icon>schedule</mat-icon></div>
            <div class="stat-text"><div class="stat-value">{{ stats()!.totals.pending }}</div><div class="stat-label">{{ 'overview.pending' | transloco }}</div></div>
          </mat-card-content>
        </mat-card>
        <mat-card class="stat-card accent-green" appearance="outlined">
          <mat-card-content>
            <div class="stat-icon icon-green"><mat-icon>check_circle</mat-icon></div>
            <div class="stat-text"><div class="stat-value">{{ stats()!.totals.completed }}</div><div class="stat-label">{{ 'overview.completed' | transloco }}</div></div>
          </mat-card-content>
        </mat-card>
      </div>

      <mat-card class="pending-card" appearance="outlined">
        <mat-card-header>
          <mat-card-title class="pending-title">
            <mat-icon>how_to_reg</mat-icon>
            {{ 'overview.pendingApprovals' | transloco }}
            <span class="count-badge">{{ stats()!.totals.pendingUsers }}</span>
          </mat-card-title>
        </mat-card-header>
        <mat-card-content>
          @if (pendingUsers().length === 0) {
            <p class="pending-empty">{{ 'overview.noPending' | transloco }}</p>
          } @else {
            <div class="pending-list">
              @for (u of pendingUsers(); track u.id) {
                <div class="pending-row">
                  <div class="pending-info">
                    <div class="pending-name">{{ u.displayName }}</div>
                    <div class="pending-meta">
                      <span>{{ u.email }}</span>
                      <span class="chip chip-neutral">{{ u.roleName }}</span>
                      @if (u.createdAt) {
                        <span class="pending-date">{{ u.createdAt | date:'mediumDate' }}</span>
                      }
                    </div>
                  </div>
                  <div class="pending-actions">
                    <button mat-flat-button color="primary" [matMenuTriggerFor]="approveMenu"
                      (menuOpened)="approveSelection[u.id] = u.roleId" [disabled]="busy()">
                      {{ 'overview.approve' | transloco }}
                    </button>
                    <mat-menu #approveMenu="matMenu" class="approve-menu">
                      <div class="approve-panel" (click)="$event.stopPropagation()">
                        <mat-form-field appearance="outline" subscriptSizing="dynamic" class="approve-field">
                          <mat-label>{{ 'overview.approveAs' | transloco }}</mat-label>
                          <mat-select [(value)]="approveSelection[u.id]">
                            @for (r of activeRoles(); track r.id) {
                              <mat-option [value]="r.id">{{ r.name }}</mat-option>
                            }
                          </mat-select>
                        </mat-form-field>
                        <button mat-flat-button color="primary" class="approve-confirm"
                          (click)="approve(u)" [disabled]="busy()">
                          {{ 'overview.confirm' | transloco }}
                        </button>
                      </div>
                    </mat-menu>
                    <button mat-stroked-button color="warn" (click)="reject(u)" [disabled]="busy()">
                      {{ 'overview.reject' | transloco }}
                    </button>
                  </div>
                </div>
              }
            </div>
          }
        </mat-card-content>
      </mat-card>

      <div class="table-header">
        <h2>{{ 'overview.breakdown' | transloco }}</h2>
        <button mat-stroked-button (click)="load()" [disabled]="loading()">
          <mat-icon>refresh</mat-icon> {{ 'common.refresh' | transloco }}
        </button>
      </div>

      <div class="table-container">
        <table mat-table [dataSource]="tableDataSource" matSort class="mat-elevation-z1">
          <ng-container matColumnDef="key">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>{{ 'overview.key' | transloco }}</th>
            <td mat-cell *matCellDef="let row"><code>{{ row.key }}</code></td>
          </ng-container>
          <ng-container matColumnDef="name">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>{{ 'overview.name' | transloco }}</th>
            <td mat-cell *matCellDef="let row">{{ row.name }}</td>
          </ng-container>
          <ng-container matColumnDef="comments">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>{{ 'overview.comments' | transloco }}</th>
            <td mat-cell *matCellDef="let row">{{ row.comments }}</td>
          </ng-container>
          <ng-container matColumnDef="privateComments">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>{{ 'overview.private' | transloco }}</th>
            <td mat-cell *matCellDef="let row">
              @if (row.privateComments > 0) {
                <span class="chip chip-private" [title]="'overview.privateHiddenTooltip' | transloco">
                  <mat-icon class="chip-icon">lock</mat-icon>{{ row.privateComments }}
                </span>
              } @else {
                <span class="muted-dash">—</span>
              }
            </td>
          </ng-container>
          <ng-container matColumnDef="open">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>{{ 'overview.open' | transloco }}</th>
            <td mat-cell *matCellDef="let row" class="col-blue">{{ row.open }}</td>
          </ng-container>
          <ng-container matColumnDef="pending">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>{{ 'overview.pending' | transloco }}</th>
            <td mat-cell *matCellDef="let row" class="col-amber">{{ row.pending }}</td>
          </ng-container>
          <ng-container matColumnDef="completed">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>{{ 'overview.completed' | transloco }}</th>
            <td mat-cell *matCellDef="let row" class="col-green">{{ row.completed }}</td>
          </ng-container>
          <ng-container matColumnDef="status">
            <th mat-header-cell *matHeaderCellDef mat-sort-header>{{ 'overview.status' | transloco }}</th>
            <td mat-cell *matCellDef="let row">
              <span [class]="row.isActive ? 'chip chip-active' : 'chip chip-disabled'">
                {{ (row.isActive ? 'common.active' : 'common.disabled') | transloco }}
              </span>
            </td>
          </ng-container>

          <tr mat-header-row *matHeaderRowDef="displayedColumns"></tr>
          <tr mat-row *matRowDef="let row; columns: displayedColumns;"></tr>
        </table>
      </div>
    } @else if (!loading()) {
      <div class="empty-state">
        <p>No data available.</p>
        <button mat-stroked-button (click)="load()">{{ 'common.refresh' | transloco }}</button>
      </div>
    }
  `,
  styles: [`
    .progress-bar { position: fixed; top: 0; inset-inline: 0; z-index: 1000; }
    .stat-grid {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(170px, 1fr));
      gap: 16px;
      margin-bottom: 32px;
    }
    .stat-card { background: var(--panel-bg); color: var(--ink); border-radius: 14px; }
    .stat-card mat-card-content { display: flex; align-items: center; gap: 14px; padding: 16px; }
    .stat-icon { width: 44px; height: 44px; border-radius: 12px; display: flex; align-items: center; justify-content: center; flex-shrink: 0; }
    .stat-icon mat-icon { font-size: 24px; width: 24px; height: 24px; }
    .icon-slate { background: #eef2f7; color: #475569; }
    .icon-blue { background: #e8f0fe; color: #1976d2; }
    .icon-amber { background: #fff4e5; color: #f57c00; }
    .icon-green { background: #e8f5e9; color: #388e3c; }
    .stat-text { display: flex; flex-direction: column; }
    .stat-value { font-size: 1.7rem; font-weight: 700; line-height: 1.1; }
    .stat-label { font-size: 0.72rem; color: var(--muted); text-transform: uppercase; letter-spacing: 0.04em; margin-top: 2px; }
    .stat-subnote { display: inline-flex; align-items: center; gap: 3px; font-size: 0.7rem; color: var(--muted); margin-top: 4px; }
    .accent-blue .stat-value { color: #1976d2; }
    .accent-amber .stat-value { color: #f57c00; }
    .accent-green .stat-value { color: #388e3c; }
    .pending-card { background: var(--panel-bg); color: var(--ink); border-radius: 14px; margin-bottom: 32px; }
    .pending-title { display: flex; align-items: center; gap: 8px; font-size: 1.05rem; }
    .pending-title mat-icon { color: #f57c00; }
    .count-badge { background: #fff4e5; color: #f57c00; font-size: 0.78rem; font-weight: 700; min-width: 22px; height: 22px; padding: 0 7px; border-radius: 11px; display: inline-flex; align-items: center; justify-content: center; }
    .pending-empty { color: var(--muted); margin: 8px 0 0; }
    .pending-list { display: flex; flex-direction: column; }
    .pending-row { display: flex; align-items: center; justify-content: space-between; gap: 16px; padding: 12px 0; border-top: 1px solid var(--border, rgba(128,128,128,0.18)); flex-wrap: wrap; }
    .pending-row:first-child { border-top: none; }
    .pending-name { font-weight: 600; }
    .pending-meta { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; font-size: 0.85rem; color: var(--muted); margin-top: 2px; }
    .pending-date { font-size: 0.8rem; }
    .pending-actions { display: flex; align-items: center; gap: 8px; }
    .approve-panel { display: flex; flex-direction: column; gap: 10px; padding: 12px; min-width: 200px; }
    .approve-field { width: 100%; }
    .approve-confirm { width: 100%; }
    .table-header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 12px; }
    .table-header h2 { margin: 0; font-size: 1.1rem; }
    .table-container { overflow-x: auto; }
    table { width: 100%; }
    .col-blue { color: #1976d2; font-weight: 500; }
    .col-amber { color: #f57c00; font-weight: 500; }
    .col-green { color: #388e3c; font-weight: 500; }
    /* status chip styles are global (theme-aware) — see styles.scss */
    .chip-private { display: inline-flex; align-items: center; gap: 3px; background: #eef2f7; color: #475569; font-size: 0.78rem; font-weight: 600; padding: 1px 8px; border-radius: 11px; }
    .chip-icon { font-size: 14px; width: 14px; height: 14px; }
    .muted-dash { color: var(--muted); }
    .empty-state { text-align: center; padding: 48px; }
  `],
})
export class OverviewComponent implements OnInit {
  private statsService = inject(StatsService);
  private usersService = inject(UsersService);
  private rolesService = inject(RolesService);
  private snack = inject(MatSnackBar);

  // Setter ViewChild: the table lives inside @if (stats()), so MatSort doesn't
  // exist at ngAfterViewInit time. This fires when the table enters the DOM
  // (once stats() is truthy), wiring sort correctly.
  @ViewChild(MatSort) set sort(value: MatSort) {
    if (value) this.tableDataSource.sort = value;
  }

  stats = signal<StatsResponse | null>(null);
  loading = signal(false);
  busy = signal(false);
  pendingUsers = signal<UserResponse[]>([]);
  roles = signal<RoleResponse[]>([]);
  // Per-user selected role for the approve role-confirm dropdown (keyed by user id).
  approveSelection: Record<number, number> = {};
  tableDataSource = new MatTableDataSource<ProjectStats>([]);

  displayedColumns = ['key', 'name', 'comments', 'privateComments', 'open', 'pending', 'completed', 'status'];

  activeRoles() { return this.roles().filter(r => r.isActive); }

  ngOnInit() { this.load(); this.loadPending(); }

  load() {
    this.loading.set(true);
    this.statsService.get().subscribe({
      next: (data) => {
        this.stats.set(data);
        this.tableDataSource.data = data.projects;
        this.loading.set(false);
      },
      error: (e) => { this.loading.set(false); this.snack.open(e.message || 'Failed to load stats', 'OK', { duration: 4000 }); },
    });
  }

  private loadPending() {
    this.rolesService.list().subscribe({
      next: (roles) => this.roles.set(roles),
      error: () => { /* roles are non-critical for display */ },
    });
    this.usersService.list('Pending').subscribe({
      next: (users) => this.pendingUsers.set(users),
      error: (e) => this.snack.open(e.message || 'Failed to load pending users', 'OK', { duration: 4000 }),
    });
  }

  // Decrement the pending badge locally so the count updates without a reload.
  private bumpPendingCount(delta: number) {
    const s = this.stats();
    if (!s) return;
    this.stats.set({ ...s, totals: { ...s.totals, pendingUsers: Math.max(0, s.totals.pendingUsers + delta) } });
  }

  approve(user: UserResponse) {
    const roleId = this.approveSelection[user.id] ?? user.roleId;
    this.busy.set(true);
    this.usersService.approve(user.id, roleId).subscribe({
      next: () => {
        this.pendingUsers.update(list => list.filter(u => u.id !== user.id));
        this.bumpPendingCount(-1);
        this.busy.set(false);
      },
      error: (e) => { this.busy.set(false); this.snack.open(e.message || 'Failed to approve user', 'OK', { duration: 4000 }); },
    });
  }

  reject(user: UserResponse) {
    this.busy.set(true);
    this.usersService.reject(user.id).subscribe({
      next: () => {
        this.pendingUsers.update(list => list.filter(u => u.id !== user.id));
        this.bumpPendingCount(-1);
        this.busy.set(false);
      },
      error: (e) => { this.busy.set(false); this.snack.open(e.message || 'Failed to reject user', 'OK', { duration: 4000 }); },
    });
  }
}
