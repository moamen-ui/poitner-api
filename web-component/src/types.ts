// Shared types for the <pointer-feedback> component.

export type StatusStr = 'open' | 'pending-apply' | 'applied' | 'archived';

export interface Reply {
  authorName?: string;
  authorLabel?: string;
  body?: string;
  text?: string;
  isAi?: boolean;
}

export interface ElementCapture {
  selector?: string;
  snapshot?: string;
  classes?: string;
  computedStyles?: string;
  appliedCssRules?: string;
  sourcePath?: string | null;
  parentInfo?: string;
  screenshotUrl?: string;
  pageUrl?: string;
  route?: string;
  pageTitle?: string;
  /** Viewport width in CSS px at capture time (window.innerWidth). */
  viewportWidth?: number;
  /** Viewport height in CSS px at capture time (window.innerHeight). */
  viewportHeight?: number;
  /** Device class derived from the viewport width: mobile | tablet | desktop. */
  deviceType?: string;
  /** window.devicePixelRatio at capture time (distinguishes retina/HiDPI). */
  devicePixelRatio?: number;
}

export interface Comment {
  id: number | string;
  status: StatusStr;
  environment?: number;
  body?: string;
  text?: string;
  isPrivate?: boolean;
  authorId?: string;
  authorName?: string;
  createdAt?: string;
  editedAt?: string;
  appliedByLabel?: string | null;
  element?: ElementCapture;
  replies?: Reply[];
  _mine?: boolean;
}

export interface User {
  id?: string;
  displayName?: string;
  email?: string;
  roleName?: string;
}

export interface RoleOption {
  id: string;
  name: string;
}

export interface AuthorOption {
  id: string;
  name: string;
}

/** Metadata captured for the clicked element (the `_`-prefixed fields are display-only). */
export interface Meta {
  _tag: string;
  _sourcePath: string | null;
  _snapshotPreview: string;
  selector: string;
  snapshot: string;
  classes: string;
  computedStyles: string;
  appliedCssRules: string;
  sourcePath: string | null;
  parentInfo: string;
}

/** The API's response envelope: { isSuccess, message, data }. */
export interface Envelope<T = unknown> {
  isSuccess?: boolean;
  isNotFound?: boolean;
  isConflict?: boolean;
  message?: string | null;
  data?: T;
}

/**
 * The subset of the custom element that the extracted UI modules (auth-ui) rely
 * on. The element class satisfies this structurally, so modules depend on this
 * interface instead of importing the class (avoids a circular dependency).
 */
export interface PointerHost {
  project: string;
  root: HTMLElement;
  apiLogin(email: string, password: string): Promise<Response>;
  apiRoles(): Promise<RoleOption[]>;
  apiRegister(body: Record<string, unknown>): Promise<Response>;
  saveAuth(token: string, user: User | null): void;
  init(): Promise<void> | void;
  renderChrome(): void;
  afterLogin: (() => void) | null;
}
