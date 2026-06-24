export interface Envelope<T> { isSuccess: boolean; message: string | null; data: T; }

export interface MeResponse { id: string; email: string; displayName: string; roleId: number; roleName: string; isAdmin: boolean; language?: 'ar' | 'en' | null; theme?: 'light' | 'dark' | null; }
export interface LoginResponse { token: string; user: MeResponse; }
export interface UpdatePreferencesRequest { language?: 'ar' | 'en'; theme?: 'light' | 'dark'; }

export interface RoleResponse { id: number; name: string; grantsAdmin: boolean; isSystem: boolean; isActive: boolean; }
export interface CreateRoleRequest { name: string; grantsAdmin: boolean; }
export interface UpdateRoleRequest { name?: string; grantsAdmin?: boolean; isActive?: boolean; }

export type ApprovalStatus = 'Approved' | 'Pending' | 'Rejected';
export interface UserResponse { id: number; publicId: string; email: string; displayName: string; roleId: number; roleName: string; isAdmin: boolean; isActive: boolean; approvalStatus: ApprovalStatus; createdAt?: string | null; }
export interface ApproveUserRequest { roleId: number; }
export interface CreateUserRequest { email: string; password: string; displayName: string; roleId: number; }
export interface UpdateUserRequest { roleId?: number; isActive?: boolean; password?: string; }

export interface ProjectResponse { id: number; key: string; name: string; isActive: boolean; }
export interface CreateProjectRequest { key: string; name: string; }
export interface UpdateProjectRequest { name?: string; isActive?: boolean; }

export interface StatsTotals { projects: number; users: number; comments: number; privateComments: number; open: number; pending: number; completed: number; pendingUsers: number; }
export interface ProjectStats { projectId: number; key: string; name: string; isActive: boolean; comments: number; privateComments: number; open: number; pending: number; completed: number; }
export interface StatsResponse { totals: StatsTotals; projects: ProjectStats[]; }
