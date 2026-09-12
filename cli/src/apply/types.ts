export type PickedActionDto = {
  text: string;
  prompt: string;
};

export type AiRuleApplyDto = {
  title: string;
  prompt: string;
  scope: string; // "Workspace" | "Project" | "Personal"
  priority: number; // 1 = Workspace, 2 = Project, 3 = Personal
  isPersonal: boolean;
};

export type ApplyPageDto = {
  url?: string | null;
  route?: string | null;
  title?: string | null;
  viewport?: string | null;
  device?: string | null;
  dpr?: number | null;
  uaRef?: string | null;
};

export type PageContextEntry = {
  level?: string;
  message: string;
  stack?: string;
  count?: number;
  occurredAt?: string;
};

export type NetworkContextEntry = {
  method: string;
  url: string;
  statusCode: number;
  durationMs?: number;
  occurredAt?: string;
};

export type PageContextDto = {
  id?: number;
  route?: string | null;
  environment?: number;
  lastEventAt?: string;
  consoleEntries?: PageContextEntry[];
  networkEntries?: NetworkContextEntry[];
};

export type QueueItemElement = {
  pageRef?: string | null;
  selector?: string | null;
  snapshot?: string | null;
  sourcePath?: string | null;
  screenshotUrl?: string | null;
  classes?: any;
  computedStyles?: any;
  appliedCssRules?: any;
  parent?: any;
  parentInfo?: any;
  route?: string | null;
  pageUrl?: string | null;
  pageTitle?: string | null;
  viewportWidth?: number | null;
  viewportHeight?: number | null;
  deviceType?: string | null;
};

export type QueueItemReply = {
  id?: number;
  authorId?: string;
  authorName?: string | null;
  body: string;
  createdAt?: string;
  isAi?: boolean;
};

export type QueueItem = {
  id: number;
  status: number | string;
  environment: number | string;
  body: string;
  authorName: string | null;
  createdAt: string;
  element: QueueItemElement;
  replies: QueueItemReply[];
  pickedActions: PickedActionDto[];
  aiRules: AiRuleApplyDto[];
  isBugReport: boolean;
  pageContextId?: number | null;
  page?: ApplyPageDto;
  pageContext?: PageContextDto;
};

export type UntrustedString = {
  value: string;
  untrusted: true;
};

export type AiElementView = {
  appliedCssRules: any;
  classes: any;
  deviceType: string | null;
  pageTitle: string | null;
  pageUrl: string | null;
  parentInfo: any;
  route: string | null;
  selector: string | null;
  snapshot: string | null;
  sourcePath: string | null;
  viewportHeight: number | null;
  viewportWidth: number | null;
};

export type AiReplyView = {
  authorName: string | null;
  body: UntrustedString;
  isAi: boolean;
};

export type AiPickedActionView = {
  prompt: string;
  text: string;
};

export type AiCommentView = {
  appliedAt: string | null;
  appliedByLabel: string | null;
  authorName: string | null;
  body: UntrustedString;
  commitUrl: string | null;
  createdAt: string;
  element: AiElementView;
  environment: number | string;
  id: number;
  isBugReport: boolean;
  pickedActions: AiPickedActionView[];
  replies: AiReplyView[];
  status: number | string;
};

export type ProjectStack = {
  frontend?: string[];
  backend?: string[] | null;
  aiTools?: string[];
  design?: any;
};

export type ApplyProjectContext = {
  productName: string;
  projectName: string;
  projectKey: string;
  commitStyle: 'Single' | 'Separate';
  stack: ProjectStack;
  aiRules?: AiRuleApplyDto[];
};

export type ApplyPromptOptions = {
  plan?: boolean;
};

export type ApplyClientContext = {
  server: string;
  project: string;
  token?: string;
  apiKey?: string;
  cwd: string;
};

export type ApplyRunResult = {
  prompt: string;
  items: QueueItem[];
  context: ApplyProjectContext;
};
