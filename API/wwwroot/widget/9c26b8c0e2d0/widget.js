/* GENERATED from web-component/src — DO NOT EDIT. Run `npm run build` in web-component/. */
"use strict";
(() => {
  // src/pagecontext.ts
  var consoleEntries = [], networkEntries = [], started = !1, recording = !1, originalConsoleError = null, originalConsoleWarn = null, originalFetch = null, originalXhrOpen = null, originalXhrSend = null, onWindowError = null, onUnhandledRejection = null;
  function now() {
    return (/* @__PURE__ */ new Date()).toISOString();
  }
  function trim(list, maxAgeGetter) {
    let cutoff = Date.now() - 18e5;
    for (; list.length && new Date(maxAgeGetter(list[0])).getTime() < cutoff; ) list.shift();
    for (; list.length > 20; ) list.shift();
  }
  function stringifyArg(arg) {
    if (typeof arg == "string") return arg;
    if (arg instanceof Error) return arg.message;
    try {
      return JSON.stringify(arg);
    } catch {
      return String(arg);
    }
  }
  function extractStack(args) {
    var _a2;
    let err = args.find((a) => a instanceof Error);
    return (_a2 = err == null ? void 0 : err.stack) == null ? void 0 : _a2.slice(0, 4e3);
  }
  function recordConsole(level, args) {
    pushConsole(level, args.map(stringifyArg).join(" "), extractStack(args));
  }
  function pushConsole(level, rawMessage, stack) {
    if (!recording) {
      recording = !0;
      try {
        let message = rawMessage.slice(0, 2e3);
        if (message.startsWith("[pointer-feedback]")) return;
        let last = consoleEntries[consoleEntries.length - 1];
        last && last.level === level && last.message === message ? (last.count += 1, last.occurredAt = now()) : consoleEntries.push({ level, message, stack, count: 1, occurredAt: now() }), trim(consoleEntries, (e) => e.occurredAt);
      } catch {
      } finally {
        recording = !1;
      }
    }
  }
  function stripQuery(url) {
    let cut = url.search(/[?#]/);
    return cut >= 0 ? url.slice(0, cut) : url;
  }
  function shouldRecord(statusCode, durationMs) {
    return statusCode === null || statusCode === 0 || statusCode >= 400 ? !0 : durationMs >= 3e3;
  }
  function recordNetwork(method, url, statusCode, durationMs) {
    try {
      networkEntries.push({ method, url: stripQuery(url), statusCode, durationMs, occurredAt: now() }), trim(networkEntries, (e) => e.occurredAt);
    } catch {
    }
  }
  function rawFetch(url, opts) {
    return (originalFetch != null ? originalFetch : window.fetch).call(window, url, opts);
  }
  function patchFetch() {
    let original = window.fetch;
    originalFetch = original, window.fetch = (...args) => {
      var _a2, _b;
      let url = typeof args[0] == "string" ? args[0] : args[0] instanceof URL ? args[0].href : args[0].url, method = (((_a2 = args[1]) == null ? void 0 : _a2.method) || ((_b = args[0]) == null ? void 0 : _b.method) || "GET").toUpperCase(), start = Date.now();
      return original.apply(window, args).then(
        (response) => {
          let durationMs = Date.now() - start;
          return shouldRecord(response.status, durationMs) && recordNetwork(method, url, response.status, durationMs), response;
        },
        (err) => {
          throw recordNetwork(method, url, null, Date.now() - start), err;
        }
      );
    };
  }
  function patchXhr() {
    if (typeof XMLHttpRequest == "undefined") return;
    let proto = XMLHttpRequest.prototype;
    originalXhrOpen = proto.open, originalXhrSend = proto.send, proto.open = function(method, url, ...rest) {
      try {
        this.__pfMethod = String(method || "GET").toUpperCase(), this.__pfUrl = typeof url == "string" ? url : String(url);
      } catch {
      }
      return originalXhrOpen.call(this, method, url, ...rest);
    }, proto.send = function(body) {
      try {
        let start = Date.now(), method = this.__pfMethod || "GET", url = this.__pfUrl || "";
        this.addEventListener("loadend", () => {
          let durationMs = Date.now() - start, status = this.status;
          shouldRecord(status === 0 ? null : status, durationMs) && recordNetwork(method, url, status === 0 ? null : status, durationMs);
        });
      } catch {
      }
      return originalXhrSend.call(this, body);
    };
  }
  function unpatchXhr() {
    typeof XMLHttpRequest != "undefined" && (originalXhrOpen && (XMLHttpRequest.prototype.open = originalXhrOpen), originalXhrSend && (XMLHttpRequest.prototype.send = originalXhrSend), originalXhrOpen = null, originalXhrSend = null);
  }
  function startPageContextCapture(_server, _scriptOrigin) {
    started || (started = !0, originalConsoleError = console.error.bind(console), originalConsoleWarn = console.warn.bind(console), console.error = (...args) => {
      recordConsole("error", args), originalConsoleError(...args);
    }, console.warn = (...args) => {
      recordConsole("warn", args), originalConsoleWarn(...args);
    }, onWindowError = (e) => {
      var _a2;
      let err = e.error instanceof Error ? e.error : void 0, where = e.filename ? ` (${e.filename}:${e.lineno}:${e.colno})` : "";
      pushConsole("error", `Uncaught ${e.message || err && err.message || "error"}${where}`, (_a2 = err == null ? void 0 : err.stack) == null ? void 0 : _a2.slice(0, 4e3));
    }, onUnhandledRejection = (e) => {
      var _a2;
      let reason = e.reason, err = reason instanceof Error ? reason : void 0;
      pushConsole("error", `Unhandled promise rejection: ${err ? err.message : stringifyArg(reason)}`, (_a2 = err == null ? void 0 : err.stack) == null ? void 0 : _a2.slice(0, 4e3));
    }, window.addEventListener("error", onWindowError), window.addEventListener("unhandledrejection", onUnhandledRejection), patchFetch(), patchXhr());
  }
  function stopPageContextCapture() {
    started && (originalConsoleError && (console.error = originalConsoleError), originalConsoleWarn && (console.warn = originalConsoleWarn), originalFetch && (window.fetch = originalFetch), onWindowError && window.removeEventListener("error", onWindowError), onUnhandledRejection && window.removeEventListener("unhandledrejection", onUnhandledRejection), unpatchXhr(), originalConsoleError = null, originalConsoleWarn = null, originalFetch = null, onWindowError = null, onUnhandledRejection = null, started = !1);
  }
  function getOrCreateSessionId() {
    let KEY = "pointer_page_session_id";
    try {
      let id = sessionStorage.getItem(KEY);
      return id || (id = typeof crypto != "undefined" && crypto.randomUUID ? crypto.randomUUID() : `${Date.now()}-${Math.random().toString(36).slice(2)}`, sessionStorage.setItem(KEY, id)), id;
    } catch {
      return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
    }
  }
  function getPageContextPayload() {
    return started ? {
      sessionId: getOrCreateSessionId(),
      consoleEntries: consoleEntries.slice(),
      networkEntries: networkEntries.slice()
    } : null;
  }

  // src/constants.ts
  var HL_CLASS = "pointer-feedback-hl", BACKDROP_SELECTOR = [
    ".cdk-overlay-backdrop",
    // Angular CDK / Angular Material
    ".modal-backdrop",
    // Bootstrap
    ".MuiBackdrop-root",
    // MUI
    ".ant-modal-mask",
    // Ant Design (modal)
    ".ant-drawer-mask",
    // Ant Design (drawer)
    ".v-overlay__scrim",
    // Vuetify
    ".el-overlay"
    // Element Plus
  ].join(", "), DIALOG_CONTENT_SELECTOR = [
    ".cdk-overlay-pane",
    // Angular CDK / Angular Material (dialogs, menus, autocomplete, …)
    ".modal-content",
    // Bootstrap
    ".MuiDialog-paper",
    // MUI
    ".MuiPopover-paper",
    // MUI (menus/popovers)
    ".ant-modal-content",
    // Ant Design (modal)
    ".ant-drawer-content",
    // Ant Design (drawer)
    ".v-overlay__content",
    // Vuetify
    ".el-overlay-dialog"
    // Element Plus
  ].join(", "), ENV_MAP = { unknown: 0, local: 1, staging: 2, production: 3 }, ENV_NAME = { 0: "unknown", 1: "local", 2: "staging", 3: "production" }, STATUS_STR = {
    1: "open",
    2: "pending-apply",
    3: "applied",
    4: "archived"
  }, STATUS_INT = {
    open: 1,
    "pending-apply": 2,
    applied: 3,
    archived: 4
  };
  var STATUS_FALLBACK = [
    { value: 1, name: "Open", label: "Open", color: "#2563eb", order: 1 },
    { value: 2, name: "ReadyToApply", label: "Ready", color: "#d97706", order: 2 },
    { value: 3, name: "Applied", label: "Completed", color: "#16a34a", order: 3 },
    { value: 4, name: "Archived", label: "Archived", color: "#6b7280", order: 4 }
  ];
  function pfFetch(url, opts) {
    let t2 = typeof window != "undefined" ? window.__POINTER_FETCH__ : void 0;
    return t2 ? t2(url, opts) : rawFetch(url, opts);
  }
  var _catalog = STATUS_FALLBACK;
  async function loadStatusCatalog(server) {
    var _a2;
    try {
      let res = await pfFetch(`${server.replace(/\/$/, "")}/api/statuses`);
      if (!res.ok) return;
      let body = await res.json(), data = (_a2 = body == null ? void 0 : body.data) != null ? _a2 : body;
      Array.isArray(data) && data.length && (_catalog = data.slice().sort((a, b) => a.order - b.order));
    } catch {
    }
  }
  function catalogToFilters() {
    let chips = [
      { key: "all", label: "All", color: "" }
    ];
    for (let item of _catalog) {
      let key = STATUS_STR[item.value];
      key && chips.push({ key, label: item.label, color: item.color });
    }
    return chips;
  }
  var _brandName = "Pointer";
  function getBrandName() {
    return _brandName;
  }
  async function loadBranding(server) {
    var _a2;
    try {
      let res = await pfFetch(`${server.replace(/\/$/, "")}/api/branding`);
      if (!res.ok) return;
      let body = await res.json(), data = (_a2 = body == null ? void 0 : body.data) != null ? _a2 : body;
      data && typeof data.productName == "string" && data.productName.trim() && (_brandName = data.productName.trim());
    } catch {
    }
  }
  var POSITIONS = ["top-start", "top-end", "bottom-start", "bottom-end"], SHOT_MAX_WIDTH = 1280, SHOT_HIGHLIGHT = "#2563eb", _a, SCRIPT_SRC = ((_a = document.currentScript) == null ? void 0 : _a.src) || "", CSS_INTEGRITY = "sha384-BsBdp0Hg2I1wOjjTY5JdNtVmybK9YL2jUo+qumbkGQIVrJHI7Ujxj9zcjV0qRhOE";
  function resolveCssUrl(scriptSrc) {
    var _a2;
    if (!scriptSrc) return "widget.css";
    try {
      let base = typeof window != "undefined" && ((_a2 = window.location) != null && _a2.href) ? window.location.href : "http://localhost", parsedScript = new URL(scriptSrc, base), css = new URL("widget.css", parsedScript), v = parsedScript.searchParams.get("v");
      return v && css.searchParams.set("v", v), css.href;
    } catch {
      return "widget.css";
    }
  }
  var CSS_URL = resolveCssUrl(SCRIPT_SRC), SNAPDOM_URL = SCRIPT_SRC ? new URL("vendor/snapdom.js", SCRIPT_SRC).href : "vendor/snapdom.js";

  // src/i18n.ts
  var currentLang = "en";
  function setLang(lang) {
    let next = lang === "ar" ? "ar" : "en";
    return next === currentLang ? !1 : (currentLang = next, !0);
  }
  function getLang() {
    return currentLang;
  }
  var ARABIC_URDU_ONLY = /[ٹڈڑںےھ]/, ARABIC_PASHTO_ONLY = /[ټډړږښڼ]/, ARABIC_PERSIAN_ONLY = /[پچژگ]/, ARABIC_PERSIAN_KEYBOARD = /[کی]/, ARABIC_SCRIPT = /[؀-ۿݐ-ݿࢠ-ࣿﭐ-﷿ﹰ-﻿]/, HEBREW_SCRIPT = /[֐-׿]/, HANGUL_SCRIPT = /[가-힯ᄀ-ᇿ]/, KANA_SCRIPT = /[぀-ヿ]/, HAN_SCRIPT = /[一-鿿]/, CYRILLIC_SCRIPT = /[Ѐ-ӿ]/, CYRILLIC_UKRAINIAN_ONLY = /[їєґ]/, LETTER_RE = /\p{L}/gu, NON_ASCII_LETTER_RE = /(?![\x00-\x7F])\p{L}/u, ENGLISH_STOPWORDS = /* @__PURE__ */ new Set([
    "the",
    "and",
    "this",
    "that",
    "should",
    "with",
    "when",
    "please",
    "button",
    "click",
    "text",
    "page",
    "not",
    "but",
    "from",
    "are",
    "was",
    "have",
    "has",
    "will",
    "can"
  ]);
  function detectTextLanguage(text) {
    let stripped = (text || "").replace(/`[^`]*`/g, " ").replace(/```[\s\S]*?```/g, " ").replace(/https?:\/\/\S+/gi, " ").replace(/\d+/g, " ");
    if ((stripped.match(LETTER_RE) || []).length < 20) return "unknown";
    if (ARABIC_SCRIPT.test(stripped))
      return ARABIC_URDU_ONLY.test(stripped) ? "ur" : ARABIC_PASHTO_ONLY.test(stripped) ? "ps" : ARABIC_PERSIAN_ONLY.test(stripped) ? "fa" : ARABIC_PERSIAN_KEYBOARD.test(stripped) ? "unknown" : "ar";
    if (HEBREW_SCRIPT.test(stripped)) return "he";
    if (HANGUL_SCRIPT.test(stripped)) return "ko";
    if (KANA_SCRIPT.test(stripped)) return "ja";
    if (HAN_SCRIPT.test(stripped)) return "zh";
    if (CYRILLIC_SCRIPT.test(stripped))
      return CYRILLIC_UKRAINIAN_ONLY.test(stripped) ? "uk" : "unknown";
    if (NON_ASCII_LETTER_RE.test(stripped)) return "unknown";
    let words = stripped.toLowerCase().match(/[a-z]+/g) || [];
    return new Set(words.filter((w) => ENGLISH_STOPWORDS.has(w))).size >= 3 ? "en" : "unknown";
  }
  async function detectTextLanguageAsync(text) {
    let fallback = detectTextLanguage(text);
    try {
      let ctor = self.LanguageDetector;
      if (!ctor || await ctor.availability() !== "available") return fallback;
      let detector = await ctor.create(), timeout = new Promise((resolve) => setTimeout(() => resolve(null), 300)), results = await Promise.race([detector.detect(text), timeout]);
      if (!results || !results.length) return fallback;
      let best = results[0];
      return best.confidence >= 0.8 && best.detectedLanguage ? best.detectedLanguage : fallback;
    } catch {
      return fallback;
    }
  }
  function t(key, vars) {
    var _a2, _b;
    let raw = (_b = (_a2 = STRINGS[currentLang][key]) != null ? _a2 : STRINGS.en[key]) != null ? _b : key;
    return vars ? raw.replace(/\{(\w+)\}/g, (_, k) => k in vars ? String(vars[k]) : `{${k}}`) : raw;
  }
  function unreadSuffix(count) {
    return count > 0 ? t("toolbar.unreadSuffix", { count: count > 99 ? t("toolbar.countCap") : count }) : "";
  }
  var STRINGS = {
    en: {
      // --- auth (login/signup modal) ---
      "auth.leaveFeedbackOn": "Leave feedback on",
      "auth.skipForNow": "Skip for now",
      "auth.email": "Email",
      "auth.password": "Password",
      "auth.signIn": "Sign in",
      "auth.chooseRoleToRequestAgain": "Choose a role to request again",
      "auth.requestAgain": "Request again",
      "auth.noAccount": "No account?",
      "auth.createAccount": "Create account",
      "auth.name": "Name",
      "auth.role": "Role",
      "auth.alreadyHaveAccount": "Already have an account?",
      "auth.backToSignIn": "Back to sign in",
      "auth.loadingRoles": "Loading roles…",
      "auth.noRolesAvailable": "No roles available",
      "auth.couldNotLoadRoles": "Could not load roles.",
      "auth.pleaseEnterEmail": "Please enter your email.",
      "auth.pleaseEnterPassword": "Please enter your password.",
      "auth.pendingApproval": "Your request is awaiting admin approval.",
      "auth.accountDisabled": "Your account is disabled.",
      "auth.requestRejected": "Your request was rejected.",
      "auth.invalidCredentials": "Invalid email or password.",
      "auth.networkError": "Network error. Please try again.",
      "auth.signingIn": "Signing in…",
      "auth.pleaseChooseRole": "Please choose a role.",
      "auth.enterEmailPasswordToRequestAgain": "Enter your email and password to request again.",
      "auth.submitting": "Submitting…",
      "auth.couldNotSubmitRequest": "Could not submit your request.",
      "auth.requestSubmittedMsg": "Request submitted — an admin will review it.",
      "auth.pleaseEnterName": "Please enter your name.",
      "auth.pleaseChoosePassword": "Please choose a password.",
      "auth.couldNotCreateAccount": "Could not create your account.",
      "auth.requestSubmittedBtn": "Request submitted",
      // --- toolbar ---
      "toolbar.dragToReposition": "Drag to reposition",
      "toolbar.commentOnElement": "Comment on an element",
      "toolbar.commentOnElementShortcut": "Comment on an element, shortcut {label}",
      "toolbar.cancel": "Cancel",
      "toolbar.viewCommentsList": "View comments list",
      "toolbar.comments": "Comments",
      "toolbar.projectHeading": "{project}",
      "toolbar.recentActivityUpdates": "Recent activity &amp; updates",
      "toolbar.updates": "Updates",
      // Suffix appended to the Updates button's aria-label ("Updates, 5 unread") and to
      // updateNotifyBadges()'s own rebuild of the same label — {count} is either a plain number or
      // toolbar.countCap once it passes 99.
      "toolbar.unreadSuffix": ", {count} unread",
      "toolbar.countCap": "99+",
      "toolbar.signedInAs": "Signed in as",
      "toolbar.account": "Account",
      "toolbar.hideBrand": "Hide {brand}",
      "toolbar.resetToolbarPosition": "Reset toolbar position",
      "toolbar.refreshComments": "Refresh comments",
      "toolbar.close": "Close",
      "toolbar.envFixedTitle": "Environment — fixed for this install",
      "toolbar.envSwitchTitle": "Environment — comments are scoped per environment",
      "toolbar.envAll": "All",
      "toolbar.envLocal": "local",
      "toolbar.envStaging": "staging",
      "toolbar.envProduction": "production",
      "toolbar.environment": "Environment",
      "toolbar.commitStyle": "Commit style",
      "toolbar.commitStyleTitle": "How the AI apply flow commits applied comments",
      "toolbar.oneCommit": "One commit",
      "toolbar.separateCommits": "Separate commits",
      // --- user menu ---
      "menu.addComment": "Add comment",
      "menu.clickThenPressKeyCombo": "Click, then press a new key combo",
      "menu.resetToDefault": "Reset to default",
      "menu.theme": "Theme",
      "menu.light": "Light",
      "menu.lightTheme": "Light theme",
      "menu.dark": "Dark",
      "menu.darkTheme": "Dark theme",
      "menu.language": "Language",
      "menu.extensionSignedInNote": "Signed in via the browser extension — sign out from its popup.",
      "menu.signOut": "Sign out",
      "menu.pressKeysToCancel": "Press keys… (Esc to cancel)",
      "menu.addModifierKey": "Add a modifier key (Alt/Shift/Ctrl/⌘)…",
      "menu.saving": "Saving…",
      "menu.resetting": "Resetting…",
      "menu.shortcutUpdated": "Shortcut updated",
      "menu.failedToSaveTryAgain": "Failed to save — try again",
      "menu.shortcutResetToDefault": "Shortcut reset to default",
      "menu.failedToResetTryAgain": "Failed to reset — try again",
      // --- launcher ---
      "launcher.openFeedbackFor": "Open {brand} feedback",
      // --- sidebar / filters ---
      "sidebar.mineOnly": "Mine only",
      "sidebar.showOnlyMyComments": "Show only my comments",
      "sidebar.status": "Status",
      "sidebar.filterByStatus": "Filter by status",
      "sidebar.filterByUser": "Filter by user",
      "sidebar.user": "User",
      "sidebar.showFilters": "Show filters",
      "sidebar.hideFilters": "Hide filters",
      "sidebar.allUsers": "All users",
      "sidebar.noCommentsYet": "No comments on this project yet.<br/>Click the inspect icon, then click an element.",
      "sidebar.noOwnComments": "You haven't left any comments yet.",
      "sidebar.noFilteredComments": 'No comments in "{label}"{suffix}.',
      "sidebar.ofYours": " of yours",
      // --- comment card ---
      "card.deployedIn": "Deployed in {sha}",
      "card.live": "live",
      "card.completed": "completed",
      "card.pending": "pending",
      "card.archived": "archived",
      "card.verified": "Verified",
      "card.looksRight": "Looks right",
      "card.notFixed": "Not fixed",
      "card.explainNotFixed": "Explain what is still not fixed…",
      "card.submit": "Submit",
      "card.viewCommit": "View commit",
      "card.commit": "commit",
      "card.containsSecretPayload": "contains a secret/payload?",
      "card.defaultReplyAuthor": "User",
      "card.automatedReply": "Automated reply",
      "card.aiVia": "via {name}",
      "card.edited": "edited",
      "card.jumpToPin": "Flash this comment's pin on the page",
      "card.reply": "Reply",
      "card.replyPlaceholder": "Reply…",
      "card.markedReadyClickToUnmark": "Marked ready — click to unmark",
      "card.markReadyToApply": "Mark ready to apply",
      "card.ready": "Ready",
      "card.reopen": "Re-open",
      "card.archive": "Archive",
      "card.edit": "Edit",
      "card.delete": "Delete",
      "card.moreActions": "More actions",
      "card.copyApplyPrompt": "Copy apply prompt",
      "card.complete": "Complete",
      "card.envLocal": "Local",
      "card.envStaging": "Staging",
      "card.envProduction": "Production",
      "card.privateClickToMakePublic": "Private — click to make public",
      "card.makePrivateOnlyYou": "Make private (only you)",
      "card.makePublic": "Make public",
      "card.makePrivate": "Make private",
      "card.removeImage": "Remove image",
      "card.save": "Save",
      "card.deleteThisComment": "Delete this comment?",
      "card.deleteThisReply": "Delete this reply?",
      "card.confirmDelete": "Confirm delete",
      "card.readMore": "Read more",
      "card.readLess": "Read less",
      "card.openFullScreenshot": "Open full screenshot",
      "card.elementScreenshot": "Element screenshot",
      // --- comment popover ---
      "popover.selectParentElement": "Select parent element",
      "popover.selectFirstChildElement": "Select first child element",
      "popover.commentOn": "Comment on",
      "popover.whatShouldChange": "What should change here?",
      "popover.predefinedPrompts": "Predefined prompts",
      "popover.searchPrompts": "Search prompts…",
      "popover.searchPredefinedPrompts": "Search predefined prompts",
      "popover.noMatches": "No matches",
      "popover.remove": "Remove",
      "popover.attachScreenshot": "Attach screenshot",
      "popover.reportBugTitle": "Attaches any console errors/warnings and failed or slow network requests seen on this page",
      "popover.reportAsABug": "Report as a bug",
      "popover.add": "Add",
      "popover.commentCannotBeEmpty": "Comment cannot be empty",
      "popover.clickAnyElementToComment": "Click any element to comment on it — or press Esc to cancel",
      "popover.cancelled": "Cancelled",
      // --- pins ---
      "pin.ready": "Ready",
      "pin.applied": "Applied",
      "pin.archived": "Archived",
      "pin.open": "Open",
      "pin.commentHash": "Comment #{n}",
      "pin.byAuthor": " by {author}",
      "pin.reply": "reply",
      "pin.replies": "replies",
      "pin.overlappingComments": "{n} overlapping comments at this location",
      // --- notifications ---
      "notifications.updates": "Updates",
      "notifications.noUpdatesYet": "No updates yet",
      "notifications.update": "Update",
      "notifications.applied": "Applied",
      "notifications.reopened": "Reopened",
      "notifications.newReply": "New reply",
      "notifications.commit": "Commit",
      // --- toasts ---
      "toast.failedToVerifyComment": "Failed to verify comment",
      "toast.commentVerified": "Comment verified",
      "toast.commentReopened": "Comment re-opened",
      "toast.signedOut": "Signed out",
      "toast.hiddenClickToReopen": "{brand} hidden — click the button to reopen",
      "toast.couldNotReachServer": "Could not reach {brand} server",
      "toast.retry": "Retry",
      "toast.refreshed": "Refreshed",
      "toast.pinElementNotFound": "This comment's element isn't visible right now (hidden, removed, or temporary)",
      "toast.applyPromptCopied": "Apply prompt copied — paste it into your AI tool",
      "toast.copyFailed": "Could not copy to clipboard",
      "toast.dismissNotification": "Dismiss notification",
      "toast.commitStyleUpdated": "Commit style updated",
      "toast.updateFailed": "Update failed",
      "toast.updated": "Updated",
      "toast.actionNoLongerAvailable": "That action is no longer available — please choose another and try again.",
      "toast.commentsNotAllowedFromAddress": "Comments are not allowed from this address",
      "toast.tooManyCommentsRetryIn": "Too many comments — try again in {n} second{s}.",
      "toast.tooManyCommentsWait": "Too many comments — please wait a moment and try again.",
      "toast.screenshotUploadFailed": "Screenshot upload failed — saving without it",
      "toast.commentAdded": "Comment added",
      "toast.undo": "Undo",
      "toast.failedToSaveComment": "Failed to save comment",
      "toast.failedToReply": "Failed to reply",
      "toast.markedForApply": "Marked for apply",
      "toast.unmarked": "Unmarked",
      "toast.markedPrivate": "Marked private",
      "toast.madePublic": "Made public",
      "toast.markedCompleted": "Marked completed",
      "toast.deleted": "Deleted",
      "toast.deleteFailed": "Delete failed",
      "toast.commentUpdated": "Comment updated",
      "toast.failedToUpdateComment": "Failed to update comment",
      "toast.reopenedMsg": "Re-opened",
      "toast.archivedMsg": "Archived",
      "toast.pleaseProvideNoteNotFixed": "Please provide a note explaining what is not fixed",
      "toast.notifications": "Notifications",
      "toast.inviteLinkInvalid": "This invite link is invalid or expired — ask for a new one.",
      "fields.more": "Add more fields",
      "fields.fewer": "Fewer fields",
      "fields.extra": "Extra fields",
      "fields.edit": "Extra fields",
      "fields.save": "Save fields",
      "fields.cancel": "Cancel",
      "fields.none": "None",
      "fields.invalidUrl": "Must be a valid URL",
      "fields.invalidOption": "Pick one of the listed options",
      "fields.hostNotAllowed": "Must be a link on {hosts}",
      "fields.tooLong": "Value is too long",
      "fields.saved": "Fields saved",
      "fields.serverRejected": "Server rejected:",
      "fields.seeMore": "See more",
      "fields.seeLess": "See less"
    },
    ar: {
      // --- auth (login/signup modal) ---
      "auth.leaveFeedbackOn": "قدّم ملاحظاتك على",
      "auth.skipForNow": "تخطَّ هذا الآن",
      "auth.email": "البريد الإلكتروني",
      "auth.password": "كلمة المرور",
      "auth.signIn": "تسجيل الدخول",
      "auth.chooseRoleToRequestAgain": "اختر دورًا لإعادة الطلب",
      "auth.requestAgain": "إعادة الطلب",
      "auth.noAccount": "ليس لديك حساب؟",
      "auth.createAccount": "إنشاء حساب",
      "auth.name": "الاسم",
      "auth.role": "الدور",
      "auth.alreadyHaveAccount": "لديك حساب بالفعل؟",
      "auth.backToSignIn": "العودة لتسجيل الدخول",
      "auth.loadingRoles": "جارٍ تحميل الأدوار…",
      "auth.noRolesAvailable": "لا توجد أدوار متاحة",
      "auth.couldNotLoadRoles": "تعذّر تحميل الأدوار.",
      "auth.pleaseEnterEmail": "يرجى إدخال بريدك الإلكتروني.",
      "auth.pleaseEnterPassword": "يرجى إدخال كلمة المرور.",
      "auth.pendingApproval": "طلبك بانتظار موافقة المسؤول.",
      "auth.accountDisabled": "حسابك معطّل.",
      "auth.requestRejected": "تم رفض طلبك.",
      "auth.invalidCredentials": "البريد الإلكتروني أو كلمة المرور غير صحيحة.",
      "auth.networkError": "خطأ في الشبكة. يرجى المحاولة مرة أخرى.",
      "auth.signingIn": "جارٍ تسجيل الدخول…",
      "auth.pleaseChooseRole": "يرجى اختيار دور.",
      "auth.enterEmailPasswordToRequestAgain": "أدخل بريدك الإلكتروني وكلمة المرور لإعادة الطلب.",
      "auth.submitting": "جارٍ الإرسال…",
      "auth.couldNotSubmitRequest": "تعذّر إرسال طلبك.",
      "auth.requestSubmittedMsg": "تم إرسال الطلب — سيراجعه أحد المسؤولين.",
      "auth.pleaseEnterName": "يرجى إدخال اسمك.",
      "auth.pleaseChoosePassword": "يرجى اختيار كلمة مرور.",
      "auth.couldNotCreateAccount": "تعذّر إنشاء حسابك.",
      "auth.requestSubmittedBtn": "تم إرسال الطلب",
      // --- toolbar ---
      "toolbar.dragToReposition": "اسحب لتغيير الموضع",
      "toolbar.commentOnElement": "أضف تعليقًا على عنصر",
      "toolbar.commentOnElementShortcut": "أضف تعليقًا على عنصر، الاختصار {label}",
      "toolbar.cancel": "إلغاء",
      "toolbar.viewCommentsList": "عرض قائمة التعليقات",
      "toolbar.comments": "التعليقات",
      "toolbar.projectHeading": "{project}",
      "toolbar.recentActivityUpdates": "النشاط الأخير والتحديثات",
      "toolbar.updates": "التحديثات",
      "toolbar.unreadSuffix": "، {count} غير مقروءة",
      "toolbar.countCap": "99+",
      "toolbar.signedInAs": "مسجّل الدخول باسم",
      "toolbar.account": "الحساب",
      "toolbar.hideBrand": "إخفاء {brand}",
      "toolbar.resetToolbarPosition": "إعادة ضبط موضع شريط الأدوات",
      "toolbar.refreshComments": "تحديث التعليقات",
      "toolbar.close": "إغلاق",
      "toolbar.envFixedTitle": "البيئة — ثابتة لهذا التثبيت",
      "toolbar.envSwitchTitle": "البيئة — التعليقات مرتبطة بكل بيئة على حدة",
      "toolbar.envAll": "الكل",
      "toolbar.envLocal": "محلي",
      "toolbar.envStaging": "الاختبار",
      "toolbar.envProduction": "الإنتاج",
      "toolbar.environment": "البيئة",
      "toolbar.commitStyle": "أسلوب الالتزام",
      "toolbar.commitStyleTitle": "كيفية التزام التعليقات المطبَّقة عبر مسار تطبيق الذكاء الاصطناعي",
      "toolbar.oneCommit": "التزام واحد",
      "toolbar.separateCommits": "التزامات منفصلة",
      // --- user menu ---
      "menu.addComment": "إضافة تعليق",
      "menu.clickThenPressKeyCombo": "انقر، ثم اضغط تركيبة مفاتيح جديدة",
      "menu.resetToDefault": "إعادة إلى الافتراضي",
      "menu.theme": "المظهر",
      "menu.light": "فاتح",
      "menu.lightTheme": "المظهر الفاتح",
      "menu.dark": "داكن",
      "menu.darkTheme": "المظهر الداكن",
      "menu.language": "اللغة",
      "menu.extensionSignedInNote": "تم تسجيل الدخول عبر إضافة المتصفح — سجّل الخروج من نافذتها المنبثقة.",
      "menu.signOut": "تسجيل الخروج",
      "menu.pressKeysToCancel": "اضغط المفاتيح… (Esc للإلغاء)",
      "menu.addModifierKey": "أضف مفتاح تعديل (Alt/Shift/Ctrl/⌘)…",
      "menu.saving": "جارٍ الحفظ…",
      "menu.resetting": "جارٍ إعادة الضبط…",
      "menu.shortcutUpdated": "تم تحديث الاختصار",
      "menu.failedToSaveTryAgain": "فشل الحفظ — حاول مرة أخرى",
      "menu.shortcutResetToDefault": "تمت إعادة الاختصار إلى الافتراضي",
      "menu.failedToResetTryAgain": "فشلت إعادة الضبط — حاول مرة أخرى",
      // --- launcher ---
      "launcher.openFeedbackFor": "فتح ملاحظات {brand}",
      // --- sidebar / filters ---
      "sidebar.mineOnly": "تعليقاتي فقط",
      "sidebar.showOnlyMyComments": "عرض تعليقاتي فقط",
      "sidebar.status": "الحالة",
      "sidebar.filterByStatus": "تصفية حسب الحالة",
      "sidebar.filterByUser": "تصفية حسب المستخدم",
      "sidebar.user": "المستخدم",
      "sidebar.showFilters": "إظهار الفلاتر",
      "sidebar.hideFilters": "إخفاء الفلاتر",
      "sidebar.allUsers": "جميع المستخدمين",
      "sidebar.noCommentsYet": "لا توجد تعليقات على هذا المشروع بعد.<br/>انقر على أيقونة الفحص، ثم انقر على عنصر.",
      "sidebar.noOwnComments": "لم تترك أي تعليقات بعد.",
      "sidebar.noFilteredComments": 'لا توجد تعليقات ضمن "{label}"{suffix}.',
      "sidebar.ofYours": " الخاصة بك",
      // --- comment card ---
      "card.deployedIn": "تم النشر في {sha}",
      "card.live": "مباشر",
      "card.completed": "مكتمل",
      "card.pending": "قيد الانتظار",
      "card.archived": "مؤرشف",
      "card.verified": "تم التحقق",
      "card.looksRight": "يبدو صحيحًا",
      "card.notFixed": "لم يُصلح",
      "card.explainNotFixed": "اشرح ما لم يتم إصلاحه بعد…",
      "card.submit": "إرسال",
      "card.viewCommit": "عرض الالتزام",
      "card.commit": "التزام",
      "card.containsSecretPayload": "قد يحتوي على بيانات سرية؟",
      "card.defaultReplyAuthor": "مستخدم",
      "card.automatedReply": "رد آلي",
      "card.aiVia": "بواسطة {name}",
      "card.edited": "مُعدَّل",
      "card.jumpToPin": "إظهار دبوس هذا التعليق على الصفحة",
      "card.reply": "رد",
      "card.replyPlaceholder": "رد…",
      "card.markedReadyClickToUnmark": "وُضع علامة جاهز — انقر لإلغائها",
      "card.markReadyToApply": "وضع علامة جاهز للتطبيق",
      "card.ready": "جاهز",
      "card.reopen": "إعادة الفتح",
      "card.archive": "أرشفة",
      "card.edit": "تعديل",
      "card.delete": "حذف",
      "card.moreActions": "المزيد من الإجراءات",
      "card.copyApplyPrompt": "نسخ تعليمة التطبيق",
      "card.complete": "إكمال",
      "card.envLocal": "محلي",
      "card.envStaging": "الاختبار",
      "card.envProduction": "الإنتاج",
      "card.privateClickToMakePublic": "خاص — انقر لجعله عامًا",
      "card.makePrivateOnlyYou": "اجعله خاصًا (أنت فقط)",
      "card.makePublic": "اجعله عامًا",
      "card.makePrivate": "اجعله خاصًا",
      "card.removeImage": "إزالة الصورة",
      "card.save": "حفظ",
      "card.deleteThisComment": "هل تريد حذف هذا التعليق؟",
      "card.deleteThisReply": "هل تريد حذف هذا الرد؟",
      "card.confirmDelete": "تأكيد الحذف",
      "card.readMore": "قراءة المزيد",
      "card.readLess": "قراءة أقل",
      "card.openFullScreenshot": "فتح لقطة الشاشة كاملة",
      "card.elementScreenshot": "لقطة شاشة العنصر",
      // --- comment popover ---
      "popover.selectParentElement": "اختر العنصر الأصل",
      "popover.selectFirstChildElement": "اختر العنصر الفرعي الأول",
      "popover.commentOn": "تعليق على",
      "popover.whatShouldChange": "ما الذي يجب تغييره هنا؟",
      "popover.predefinedPrompts": "اقتراحات جاهزة",
      "popover.searchPrompts": "ابحث في الاقتراحات…",
      "popover.searchPredefinedPrompts": "البحث في الاقتراحات الجاهزة",
      "popover.noMatches": "لا توجد نتائج",
      "popover.remove": "إزالة",
      "popover.attachScreenshot": "إرفاق لقطة شاشة",
      "popover.reportBugTitle": "يُرفق أي أخطاء/تحذيرات في وحدة التحكم وطلبات الشبكة الفاشلة أو البطيئة في هذه الصفحة",
      "popover.reportAsABug": "الإبلاغ كخلل",
      "popover.add": "إضافة",
      "popover.commentCannotBeEmpty": "لا يمكن أن يكون التعليق فارغًا",
      "popover.clickAnyElementToComment": "انقر على أي عنصر للتعليق عليه — أو اضغط Esc للإلغاء",
      "popover.cancelled": "تم الإلغاء",
      // --- pins ---
      "pin.ready": "جاهز",
      "pin.applied": "مطبَّق",
      "pin.archived": "مؤرشف",
      "pin.open": "مفتوح",
      "pin.commentHash": "تعليق رقم {n}",
      "pin.byAuthor": " بواسطة {author}",
      "pin.reply": "رد واحد",
      "pin.replies": "{n} ردود",
      "pin.overlappingComments": "{n} تعليقات متداخلة في هذا الموضع",
      // --- notifications ---
      "notifications.updates": "التحديثات",
      "notifications.noUpdatesYet": "لا توجد تحديثات بعد",
      "notifications.update": "تحديث",
      "notifications.applied": "تم التطبيق",
      "notifications.reopened": "أُعيد فتحه",
      "notifications.newReply": "رد جديد",
      "notifications.commit": "الالتزام",
      // --- toasts ---
      "toast.failedToVerifyComment": "فشل التحقق من التعليق",
      "toast.commentVerified": "تم التحقق من التعليق",
      "toast.commentReopened": "أُعيد فتح التعليق",
      "toast.signedOut": "تم تسجيل الخروج",
      "toast.hiddenClickToReopen": "تم إخفاء {brand} — انقر على الزر لإعادة فتحه",
      "toast.couldNotReachServer": "تعذّر الوصول إلى خادم {brand}",
      "toast.retry": "إعادة المحاولة",
      "toast.refreshed": "تم التحديث",
      "toast.pinElementNotFound": "عنصر هذا التعليق غير ظاهر حاليًا (مخفي أو محذوف أو مؤقت)",
      "toast.applyPromptCopied": "تم نسخ تعليمة التطبيق — الصقها في أداة الذكاء الاصطناعي",
      "toast.copyFailed": "تعذر النسخ إلى الحافظة",
      "toast.dismissNotification": "إغلاق الإشعار",
      "toast.commitStyleUpdated": "تم تحديث أسلوب الالتزام",
      "toast.updateFailed": "فشل التحديث",
      "toast.updated": "تم التحديث",
      "toast.actionNoLongerAvailable": "لم يعد هذا الإجراء متاحًا — يرجى اختيار إجراء آخر والمحاولة مرة أخرى.",
      "toast.commentsNotAllowedFromAddress": "التعليقات غير مسموح بها من هذا العنوان",
      "toast.tooManyCommentsRetryIn": "عدد كبير جدًا من التعليقات — حاول مرة أخرى بعد {n} ثانية.",
      "toast.tooManyCommentsWait": "عدد كبير جدًا من التعليقات — يرجى الانتظار قليلًا والمحاولة مرة أخرى.",
      "toast.screenshotUploadFailed": "فشل رفع لقطة الشاشة — سيُحفظ التعليق دونها",
      "toast.commentAdded": "تمت إضافة التعليق",
      "toast.undo": "تراجع",
      "toast.failedToSaveComment": "فشل حفظ التعليق",
      "toast.failedToReply": "فشل إرسال الرد",
      "toast.markedForApply": "وُضعت علامة للتطبيق",
      "toast.unmarked": "تم إلغاء العلامة",
      "toast.markedPrivate": "وُضعت علامة خاص",
      "toast.madePublic": "أصبح عامًا",
      "toast.markedCompleted": "وُضعت علامة مكتمل",
      "toast.deleted": "تم الحذف",
      "toast.deleteFailed": "فشل الحذف",
      "toast.commentUpdated": "تم تحديث التعليق",
      "toast.failedToUpdateComment": "فشل تحديث التعليق",
      "toast.reopenedMsg": "أُعيد فتحه",
      "toast.archivedMsg": "تمت الأرشفة",
      "toast.pleaseProvideNoteNotFixed": "يرجى كتابة ملاحظة تشرح ما لم يتم إصلاحه",
      "toast.notifications": "الإشعارات",
      "toast.inviteLinkInvalid": "رابط الدعوة هذا غير صالح أو منتهي الصلاحية — اطلب رابطًا جديدًا.",
      "fields.more": "إضافة المزيد من الحقول",
      "fields.fewer": "حقول أقل",
      "fields.extra": "حقول إضافية",
      "fields.edit": "حقول إضافية",
      "fields.save": "حفظ الحقول",
      "fields.cancel": "إلغاء",
      "fields.none": "لا شيء",
      "fields.invalidUrl": "يجب أن يكون رابطاً صالحاً",
      "fields.invalidOption": "اختر أحد الخيارات المدرجة",
      "fields.hostNotAllowed": "يجب أن يكون الرابط من {hosts}",
      "fields.tooLong": "القيمة طويلة جداً",
      "fields.saved": "تم حفظ الحقول",
      "fields.serverRejected": "رفض الخادم:",
      "fields.seeMore": "عرض المزيد",
      "fields.seeLess": "عرض أقل"
    }
  };

  // src/dom.ts
  var escapeHtml = (s) => String(s == null ? "" : s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;").replace(/'/g, "&#39;"), initials = (name) => {
    let parts = (name || "").trim().split(/\s+/).filter(Boolean);
    return parts.length === 0 ? "?" : parts.length === 1 ? parts[0].slice(0, 2).toUpperCase() : (parts[0][0] + parts[parts.length - 1][0]).toUpperCase();
  }, timeAgo = (iso) => {
    if (!iso) return "";
    let then = new Date(iso).getTime();
    if (Number.isNaN(then)) return "";
    let seconds = Math.round((Date.now() - then) / 1e3), rtf = typeof Intl.RelativeTimeFormat == "function" ? new Intl.RelativeTimeFormat(getLang(), { numeric: "auto" }) : null;
    if (!rtf) return new Date(iso).toLocaleDateString();
    if (seconds < 45) return rtf.format(0, "second");
    let minutes = Math.round(seconds / 60);
    if (minutes < 60) return rtf.format(-minutes, "minute");
    let hours = Math.round(minutes / 60);
    if (hours < 24) return rtf.format(-hours, "hour");
    let days = Math.round(hours / 24);
    return days < 30 ? rtf.format(-days, "day") : new Date(iso).toLocaleDateString();
  }, buildClipPathWithHoles = (rects, refBox) => {
    let w = refBox.width, h = refBox.height, d = `M0 0H${w}V${h}H0Z`;
    for (let r of rects) {
      let x1 = Math.max(0, r.left - refBox.left - 2), y1 = Math.max(0, r.top - refBox.top - 2), x2 = Math.min(w, r.right - refBox.left + 2), y2 = Math.min(h, r.bottom - refBox.top + 2);
      x2 <= x1 || y2 <= y1 || (d += ` M${x1} ${y1}H${x2}V${y2}H${x1}Z`);
    }
    return `path(evenodd, "${d}")`;
  }, ensureHighlightStyle = () => {
    if (document.getElementById("pointer-feedback-hl-style")) return;
    let css = `
.${HL_CLASS}{
  outline:2px dashed #0969da!important;
  outline-offset:1px!important;
  cursor:crosshair!important;
  box-shadow:0 0 0 0 rgba(9,105,218,.3)!important;
  animation:pointer-feedback-hl-pulse 2.4s cubic-bezier(.25,1,.5,1) infinite!important;
}
@keyframes pointer-feedback-hl-pulse{
  0%,100%{outline-color:#0969da;box-shadow:0 0 0 0 rgba(9,105,218,.3);}
  50%{outline-color:#7c3aed;box-shadow:0 0 10px 2px rgba(124,58,237,.3);}
}
@media (prefers-reduced-motion: reduce){
  .${HL_CLASS}{animation:none!important;outline-color:#0969da!important;box-shadow:none!important;}
}`;
    try {
      if ("adoptedStyleSheets" in Document.prototype && typeof CSSStyleSheet != "undefined") {
        let sheet = new CSSStyleSheet();
        sheet.replaceSync(css), document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet];
        let marker = document.createElement("meta");
        marker.id = "pointer-feedback-hl-style", document.head.appendChild(marker);
        return;
      }
    } catch {
    }
    let s = document.createElement("style");
    s.id = "pointer-feedback-hl-style", s.textContent = css, document.head.appendChild(s);
  }, generateSelector = (el) => {
    if (el === document.documentElement) return "html";
    if (el === document.body) return "body";
    if (el.id)
      try {
        if (document.querySelector("#" + CSS.escape(el.id)) === el) return "#" + el.id;
      } catch {
      }
    let parts = [], cur = el;
    for (; cur && cur !== document.body && cur !== document.documentElement; ) {
      let selector = cur.tagName.toLowerCase();
      if (cur.id) {
        selector += "#" + cur.id, parts.unshift(selector), cur = null;
        break;
      }
      let nth = 1, sib = cur.previousElementSibling;
      for (; sib; )
        sib.tagName.toLowerCase() === cur.tagName.toLowerCase() && nth++, sib = sib.previousElementSibling;
      nth > 1 && (selector += `:nth-of-type(${nth})`), parts.unshift(selector), cur = cur.parentElement;
    }
    return cur === document.body ? parts.unshift("body") : cur === document.documentElement && parts.unshift("html"), parts.join(" > ");
  }, isCurrentPage = (comment) => {
    let url = comment.element && comment.element.pageUrl;
    if (!url) return !0;
    try {
      return new URL(url, window.location.href).pathname === window.location.pathname;
    } catch {
      return !0;
    }
  }, matchElement = (comment) => {
    let selector = comment.element && comment.element.selector, snapshot = comment.element && comment.element.snapshot;
    if (selector)
      try {
        let el = document.querySelector(selector);
        if (el) return el;
      } catch {
      }
    if (snapshot) {
      let all = document.querySelectorAll("*");
      for (let el of Array.from(all))
        if (el.outerHTML === snapshot) return el;
    }
    return null;
  }, pageIsRtl = () => {
    var _a2;
    try {
      let html = document.documentElement, attr = (html.getAttribute("dir") || ((_a2 = document.body) == null ? void 0 : _a2.getAttribute("dir")) || "").toLowerCase();
      return attr === "rtl" || attr === "ltr" ? attr === "rtl" : getComputedStyle(html).direction === "rtl";
    } catch {
      return !1;
    }
  };
  function applyDataPosition(root, selector) {
    root.querySelectorAll(selector).forEach((el) => {
      let left = el.dataset.fbkLeft, top = el.dataset.fbkTop;
      left !== void 0 && (el.style.left = `${left}px`), top !== void 0 && (el.style.top = `${top}px`);
    });
  }

  // src/icons.ts
  var ICON = {
    // Vertical "more actions" kebab — three stacked dots, same filled-circle style as `grip`.
    kebab: '<svg viewBox="0 0 16 16" width="14" height="14" fill="currentColor" stroke="none"><circle cx="8" cy="3.2" r="1.7"/><circle cx="8" cy="8" r="1.7"/><circle cx="8" cy="12.8" r="1.7"/></svg>',
    copy: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>',
    thumbsUp: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M14 9V5a3 3 0 0 0-3-3l-4 9v11h11.28a2 2 0 0 0 2-1.7l1.38-9a2 2 0 0 0-2-2.3z"/><path d="M7 22H4a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2h3"/></svg>',
    thumbsDown: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M10 15v4a3 3 0 0 0 3 3l4-9V2H5.72a2 2 0 0 0-2 1.7l-1.38 9a2 2 0 0 0 2 2.3z"/><path d="M17 2h2.67A2.31 2.31 0 0 1 22 4v7a2.31 2.31 0 0 1-2.33 2H17"/></svg>',
    flag: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 15s1-1 4-1 5 2 8 2 4-1 4-1V3s-1 1-4 1-5-2-8-2-4 1-4 1z"/><line x1="4" y1="22" x2="4" y2="15"/></svg>',
    check: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M22 11.08V12a10 10 0 1 1-5.93-9.14"/><polyline points="22 4 12 14.01 9 11.01"/></svg>',
    // Plain checkmark (no circle) — for compact confirm actions like "confirm delete".
    checkPlain: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>',
    trash: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"/><line x1="10" y1="11" x2="10" y2="17"/><line x1="14" y1="11" x2="14" y2="17"/></svg>',
    pencil: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 20h9"/><path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4z"/></svg>',
    reopen: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="1 4 1 10 7 10"/><path d="M3.51 15a9 9 0 1 0 2.13-9.36L1 10"/></svg>',
    archive: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="21 8 21 21 3 21 3 8"/><rect x="1" y="3" width="22" height="5"/><line x1="10" y1="12" x2="14" y2="12"/></svg>',
    // Eye + slash — the toolbar's Hide button.
    eyeOff: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" aria-hidden="true"><path d="M2 8s2.4-4.2 6-4.2S14 8 14 8s-2.4 4.2-6 4.2S2 8 2 8z"/><circle cx="8" cy="8" r="1.8"/><path d="M2.6 2.6l10.8 10.8"/></svg>',
    pin: '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 10c0 7-9 13-9 13s-9-6-9-13a9 9 0 0 1 18 0z"/><circle cx="12" cy="10" r="3"/></svg>',
    user: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2"/><circle cx="12" cy="7" r="4"/></svg>',
    lock: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 10 0v4"/></svg>',
    unlock: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"/><path d="M7 11V7a5 5 0 0 1 9.9-1"/></svg>',
    logout: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M9 21H5a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h4"/><polyline points="16 17 21 12 16 7"/><line x1="21" y1="12" x2="9" y2="12"/></svg>',
    // Chrome-inspect-style: dashed square + mouse pointer (lucide square-dashed-mouse-pointer).
    // Kept only for the notifications menu's "new reply" row — the toolbar's own "Comment on an
    // element" button uses `crosshair` below.
    inspect: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M5 3a2 2 0 0 0-2 2"/><path d="M19 3a2 2 0 0 1 2 2"/><path d="M5 21a2 2 0 0 1-2-2"/><path d="M9 3h1"/><path d="M9 21h2"/><path d="M14 3h1"/><path d="M3 9v1"/><path d="M21 9v2"/><path d="M3 14v1"/><path d="M12.034 12.681a.498.498 0 0 1 .647-.647l9 3.5a.5.5 0 0 1-.033.943l-3.444 1.068a1 1 0 0 0-.66.66l-1.067 3.443a.5.5 0 0 1-.943.033z"/></svg>',
    // Six-dot drag grip — the toolbar's drag handle.
    grip: '<svg viewBox="0 0 16 16" width="16" height="16" fill="currentColor" stroke="none"><circle cx="6" cy="3.6" r="1.1"/><circle cx="10" cy="3.6" r="1.1"/><circle cx="6" cy="8" r="1.1"/><circle cx="10" cy="8" r="1.1"/><circle cx="6" cy="12.4" r="1.1"/><circle cx="10" cy="12.4" r="1.1"/></svg>',
    close: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><line x1="18" y1="6" x2="6" y2="18"/><line x1="6" y1="6" x2="18" y2="18"/></svg>',
    // Target-reset — "reset toolbar to its default position".
    restore: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3 12a9 9 0 1 0 9-9 9.75 9.75 0 0 0-6.74 2.74L3 8"/><path d="M3 3v5h5"/></svg>',
    // Speech-bubble "feedback" mark — the launcher (22px) and the toolbar's Comments button (16px)
    // are drawn separately (bubble vs bubbleLg) rather than one path at two sizes.
    bubble: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round" aria-hidden="true"><path d="M2 3.6a1 1 0 0 1 1-1h10a1 1 0 0 1 1 1v6a1 1 0 0 1-1 1H6.4L3.2 13.6v-3H3a1 1 0 0 1-1-1z"/></svg>',
    bubbleLg: '<svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>',
    // Circle + 4 outward ticks — "Comment on an element" (pick mode), the toolbar's Inspect icon.
    crosshair: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" aria-hidden="true"><circle cx="8" cy="8" r="4.2"/><path d="M8 .9v3.2M8 11.9v3.2M.9 8h3.2M11.9 8h3.2"/></svg>',
    // Bell — the toolbar's Updates button.
    bell: '<svg viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" aria-hidden="true"><path d="M4.2 6.6a3.8 3.8 0 0 1 7.6 0c0 2.9 1.2 3.9 1.2 3.9H3s1.2-1 1.2-3.9z"/><path d="M6.6 12.8a1.6 1.6 0 0 0 2.8 0"/></svg>',
    // Warning triangle — the warn-variant toast icon.
    warnTriangle: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><path d="M10.29 3.86L1.82 18a2 2 0 0 0 1.71 3h16.94a2 2 0 0 0 1.71-3L13.71 3.86a2 2 0 0 0-3.42 0z"/><line x1="12" y1="9" x2="12" y2="13"/><line x1="12" y1="17" x2="12.01" y2="17"/></svg>',
    // Danger circle — the danger-variant toast icon.
    dangerCircle: '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="10"/><line x1="12" y1="8" x2="12" y2="12"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>',
    // Bold checkmark — the applied/verified pin (thicker stroke reads at 12px inside a 28px pin).
    checkBold: '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="3" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>',
    // Small reply-count bubble — the pin hover preview's "N replies" line.
    bubbleSm: '<svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z"/></svg>',
    // User menu's theme toggle — light/dark option icons.
    sun: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="4"/><path d="M12 2v2"/><path d="M12 20v2"/><path d="m4.93 4.93 1.41 1.41"/><path d="m17.66 17.66 1.41 1.41"/><path d="M2 12h2"/><path d="M20 12h2"/><path d="m6.34 17.66-1.41 1.41"/><path d="m19.07 4.93-1.41 1.41"/></svg>',
    moon: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M12 3a6 6 0 0 0 9 9 9 9 0 1 1-9-9Z"/></svg>',
    // Popover's element-navigation buttons — move the comment's target up to its parent or down to
    // its first child.
    chevronUp: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="18 15 12 9 6 15"/></svg>',
    chevronDown: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="6 9 12 15 18 9"/></svg>',
    chevronRight: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="9 6 15 12 9 18"/></svg>',
    // Funnel — the sidebar-head button that shows/hides the status/environment/author filters.
    filter: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3"/></svg>',
    extraFields: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M11 12H3"/><path d="M16 6H3"/><path d="M16 18H3"/><path d="M18 9v6"/><path d="M21 12h-6"/></svg>',
    camera: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M23 19a2 2 0 0 1-2 2H3a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4l2-3h6l2 3h4a2 2 0 0 1 2 2z"/><circle cx="12" cy="13" r="4"/></svg>',
    bug: '<svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="m8 2 1.88 1.88"/><path d="M14.12 3.88 16 2"/><path d="M9 7.13v-1a3.003 3.003 0 1 1 6 0v1"/><path d="M12 20c-3.3 0-6-2.7-6-6v-3a4 4 0 0 1 4-4h4a4 4 0 0 1 4 4v3c0 3.3-2.7 6-6 6"/><path d="M12 20v-9"/><path d="M6.53 9C4.6 8.8 3 7.1 3 5"/><path d="M6 13H2"/><path d="M3 21c0-2.1 1.7-3.9 3.8-4"/><path d="M20.97 5c0 2.1-1.6 3.8-3.5 4"/><path d="M22 13h-4"/><path d="M17.2 17c2.1.1 3.8 1.9 3.8 4"/></svg>'
  };

  // src/fields.ts
  function hostMatches(host, pattern) {
    if (host = host.toLowerCase(), pattern = pattern.toLowerCase(), host === pattern) return !0;
    if (pattern.startsWith("*.")) {
      let domain = pattern.substring(2);
      if (host === domain || host.endsWith("." + domain))
        return !0;
    }
    return !1;
  }
  function validateFieldValue(def, value) {
    if (value = value.trim(), !value) return null;
    let type = def.type, isText = type === 1 || type === "Text", isUrl = type === 2 || type === "Url", isSelect = type === 3 || type === "Select";
    if (isText) {
      if (value.length > 500) return "fields.tooLong";
    } else if (isUrl) {
      if (value.length > 2e3) return "fields.tooLong";
      try {
        let u = new URL(value);
        if (u.protocol !== "http:" && u.protocol !== "https:" || u.username || u.password || !u.hostname) return "fields.invalidUrl";
        if (def.allowedHosts && def.allowedHosts.length > 0 && !def.allowedHosts.some((h) => hostMatches(u.hostname, h))) return "fields.hostNotAllowed";
      } catch {
        return "fields.invalidUrl";
      }
    } else if (isSelect && def.options && !def.options.includes(value))
      return "fields.invalidOption";
    return null;
  }
  function renderFieldInputs(defs, values, idPrefix) {
    return !defs || defs.length === 0 ? "" : defs.map((def) => {
      let isUrl = def.type === 2 || def.type === "Url", isSelect = def.type === 3 || def.type === "Select", id = escapeHtml(`${idPrefix}-${def.key}`), name = escapeHtml(`fbk-cf-${def.key}`), val = values[def.key] || "", inputHtml = "";
      isSelect ? inputHtml = `<select id="${id}" name="${name}" class="fbk-input"><option value="">${escapeHtml(t("fields.none"))}</option>${(def.options || []).map((o) => `<option value="${escapeHtml(o)}"${o === val ? " selected" : ""}>${escapeHtml(o)}</option>`).join("")}</select>` : inputHtml = `<input type="${isUrl ? "url" : "text"}" id="${id}" name="${name}" class="fbk-input" value="${escapeHtml(val)}"${isUrl ? ' inputmode="url" maxlength="2000"' : ' maxlength="500"'}>`;
      let hintHtml = def.hint ? `<small class="fbk-field-hint" id="${id}-hint">${escapeHtml(def.hint)}</small>` : "";
      return `<div class="fbk-field"><label for="${id}">${escapeHtml(def.label)}</label>${inputHtml}${hintHtml}</div>`;
    }).join("");
  }
  function collectFieldValues(root) {
    let map = {};
    return root.querySelectorAll('[name^="fbk-cf-"]').forEach((el) => {
      let val = el.value.trim();
      val && (map[el.name.substring(7)] = val);
    }), map;
  }

  // src/templates.ts
  var TPL = {
    // The auth modal hosts two swappable bodies (sign-in / sign-up) inside one
    // shell. showLoginModal() renders the shell once and then swaps #fbk-auth-body
    // between loginBody and signupBody. The shell keeps the Skip control so
    // deferred-login dismissal works from either view.
    loginModal: (project) => `
  <div class="fbk-modal-overlay">
  <div class="fbk-modal">
  <h2>${escapeHtml(getBrandName())}</h2>
  <p>${t("auth.leaveFeedbackOn")} <b>${escapeHtml(project)}</b>.</p>
  <div id="fbk-auth-body"></div>
  <button class="fbk-btn fbk-link fbk-btn-block fbk-auth-skip" id="fbk-login-skip">${t("auth.skipForNow")}</button>
  </div>
  </div>`,
    // Sign-in body. After a "rejected" login it also renders an inline re-apply
    // block (role select + "Request again"); pass rejected=true to show it.
    loginBody: (rejected) => `
  <input class="fbk-input fbk-stack-gap" id="fbk-email" type="email" placeholder="${t("auth.email")}" />
  <input class="fbk-input fbk-stack-gap" id="fbk-password" type="password" placeholder="${t("auth.password")}" />
  <div class="fbk-modal-error" id="fbk-login-error"></div>
  <button class="fbk-btn primary fbk-btn-block" id="fbk-login-submit">${t("auth.signIn")}</button>
  ${rejected ? `
  <div class="fbk-reapply" id="fbk-reapply">
  <label class="fbk-field-label" for="fbk-reapply-role">${t("auth.chooseRoleToRequestAgain")}</label>
  <select class="fbk-input fbk-stack-gap" id="fbk-reapply-role"></select>
  <button class="fbk-btn primary fbk-btn-block" id="fbk-reapply-submit">${t("auth.requestAgain")}</button>
  </div>` : ""}
  <div class="fbk-auth-foot">
  ${t("auth.noAccount")} <button class="fbk-btn fbk-link fbk-link-inline" id="fbk-show-signup">${t("auth.createAccount")}</button>
  </div>`,
    // Sign-up body. The role <select> is populated at runtime from GET /api/roles.
    signupBody: () => `
  <input class="fbk-input fbk-stack-gap" id="fbk-su-name" type="text" placeholder="${t("auth.name")}" />
  <input class="fbk-input fbk-stack-gap" id="fbk-su-email" type="email" placeholder="${t("auth.email")}" />
  <input class="fbk-input fbk-stack-gap" id="fbk-su-password" type="password" placeholder="${t("auth.password")}" />
  <label class="fbk-field-label" for="fbk-su-role">${t("auth.role")}</label>
  <select class="fbk-input fbk-stack-gap" id="fbk-su-role"></select>
  <div class="fbk-modal-error" id="fbk-signup-error"></div>
  <div class="fbk-modal-success" id="fbk-signup-success"></div>
  <button class="fbk-btn primary fbk-btn-block" id="fbk-signup-submit">${t("auth.createAccount")}</button>
  <div class="fbk-auth-foot">
  ${t("auth.alreadyHaveAccount")} <button class="fbk-btn fbk-link fbk-link-inline" id="fbk-show-login">${t("auth.backToSignIn")}</button>
  </div>`,
    // `projectName`: embedded in the "{project}" heading so a visitor can immediately tell
    // which project this install is bound to — project keys aren't unique across a workspace, so two
    // different installs can easily look identical without this.
    // `avatarInitials`: 1-2 letters (see dom.ts's initials()) for the account button, already
    // safe to interpolate as-is — computed by the caller from the RAW display name (before
    // displayName below gets HTML-escaped), so a name containing "&"/"<" can't leak into it.
    // `ariaShortcut`: the ARIA-format string ("Control+Alt+Shift+C" — see shortcut.ts's
    // ariaKeyshortcuts), separate from `shortcutLabel` (the human-display form, "Ctrl+Alt+Shift+C"
    // or "⌃⌥⇧C" on Mac) since ARIA wants full, platform-independent modifier names.
    // The environment select/label used to live in this shell too; it's now rendered inside
    // #fbk-filters (see envFilterSelect), beside the status filter — both scoping controls together.
    // `filtersOpen`: #fbk-filters starts collapsed (see element.ts's filtersOpen field) so the
    // status/environment/author controls don't take up space until someone actually wants them —
    // the fbk-filters-toggle button in the head row reveals them on demand.
    chrome: (displayName, roleLabel, projectName = "", shortcutLabel = "", unreadNotifyCount = 0, avatarInitials = "", ariaShortcut = "", filtersOpen = !1) => `
  <aside class="fbk-toolbar" id="fbk-toolbar" role="toolbar" aria-label="${escapeHtml(getBrandName())}" part="toolbar">
  <span class="fbk-toolbar__grip" id="fbk-grip" data-fbk-drag data-toggle="tooltip" data-placement="top" title="${t("toolbar.dragToReposition")}" aria-hidden="true">${ICON.grip}</span>
  <span class="fbk-toolbar__divider" aria-hidden="true"></span>
  <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--primary fbk-toolbar-btn--icon fbk-toolbar-btn--brand" id="fbk-add" data-fbk-act="inspect" aria-pressed="false" data-toggle="tooltip" data-placement="top" title="${t("toolbar.commentOnElement")}${shortcutLabel ? ` (${escapeHtml(shortcutLabel)})` : ""}" aria-label="${t("toolbar.commentOnElement")}"${ariaShortcut ? ` aria-keyshortcuts="${escapeHtml(ariaShortcut)}"` : ""}><span class="fbk-toolbar-btn__icon">${ICON.crosshair}</span></button>
  <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--comments" id="fbk-toggle" data-fbk-act="comments" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="${t("toolbar.viewCommentsList")}" aria-label="${t("toolbar.comments")}"><span class="fbk-toolbar-btn__icon">${ICON.bubble}</span> <span class="fbk-toolbar-count" id="fbk-count" data-fbk-count>0</span></button>
  <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon fbk-hidden" id="fbk-updates" data-fbk-act="updates" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="${t("toolbar.recentActivityUpdates")}" aria-label="${t("toolbar.updates")}${unreadSuffix(unreadNotifyCount)}"><span class="fbk-toolbar-btn__icon">${ICON.bell}</span><span class="fbk-toolbar-dot${unreadNotifyCount > 0 ? "" : " fbk-hidden"}" id="fbk-notify-count" data-fbk-unread aria-hidden="true"></span></button>
  ${displayName ? `
  <span class="fbk-toolbar__divider" aria-hidden="true"></span>
  <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--avatar" id="fbk-user" data-fbk-act="account" aria-expanded="false" aria-haspopup="dialog" data-toggle="tooltip" data-placement="top" title="${t("toolbar.signedInAs")} ${displayName}${roleLabel ? " · " + roleLabel : ""}" aria-label="${t("toolbar.account")}, ${displayName}">${avatarInitials}</button>` : ""}
  <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon" id="fbk-hide" data-fbk-act="hide" data-toggle="tooltip" data-placement="top" title="${t("toolbar.hideBrand", { brand: escapeHtml(getBrandName()) })}" aria-label="${t("toolbar.hideBrand", { brand: escapeHtml(getBrandName()) })}"><span class="fbk-toolbar-btn__icon">${ICON.eyeOff}</span></button>
  <span class="fbk-toolbar__divider fbk-toolbar__divider--moved" aria-hidden="true"></span>
  <button type="button" class="fbk-toolbar-btn fbk-toolbar-btn--icon fbk-toolbar__reset" id="fbk-reset-pos" data-fbk-act="reset-position" data-toggle="tooltip" data-placement="top" title="${t("toolbar.resetToolbarPosition")}" aria-label="${t("toolbar.resetToolbarPosition")}"><span class="fbk-toolbar-btn__icon">${ICON.restore}</span></button>
  </aside>
  <div class="fbk-sidebar" id="fbk-sidebar">
  <button class="fbk-sidebar-close-arrow" id="fbk-close" title="${t("toolbar.close")}" aria-label="${t("toolbar.close")}">${ICON.chevronRight}</button>
  <div class="fbk-sidebar-head">
  <div class="fbk-sidebar-head-row">
  <h2 id="fbk-comments-heading">${t("toolbar.projectHeading", { project: escapeHtml(projectName) })}</h2>
  <span class="fbk-sidebar-head-actions">
  <button type="button" class="fbk-mini fbk-icon${filtersOpen ? " is-active" : ""}" id="fbk-filters-toggle" title="${filtersOpen ? t("sidebar.hideFilters") : t("sidebar.showFilters")}" aria-label="${filtersOpen ? t("sidebar.hideFilters") : t("sidebar.showFilters")}" aria-pressed="${filtersOpen ? "true" : "false"}" aria-expanded="${filtersOpen ? "true" : "false"}" aria-controls="fbk-filters">${ICON.filter}</button>
  <button class="fbk-mini fbk-icon" id="fbk-refresh" title="${t("toolbar.refreshComments")}" aria-label="${t("toolbar.refreshComments")}">&#8635;</button>
  </span>
  </div>
  <div class="fbk-commit-style fbk-hidden" id="fbk-commit-style"></div>
  </div>
  <div class="fbk-filters${filtersOpen ? "" : " fbk-hidden"}" id="fbk-filters"></div>
  <div class="fbk-sidebar-body" id="fbk-list"></div>
  </div>
  <div class="fbk-pins-layer" id="fbk-pins-layer"></div>
  <div id="fbk-popover-host"></div>
  <div id="fbk-menu-host"></div>`,
    // Commit-style control (see element.ts's fetchCaptureConfig/renderCommitStyleControl) — only
    // ever rendered when the CURRENT caller is authorized to change project settings
    // (CaptureConfigResponse.CanEditSettings, same gate as the PATCH itself). Lets whoever's looking
    // choose whether the AI apply flow bundles applied comments into one commit or commits each one
    // separately — read by skill.md's Step 1 the next time an agent applies.
    commitStyleControl: (commitStyle) => `
  <span class="fbk-caption">${t("toolbar.commitStyle")}</span>
  <select class="fbk-input fbk-commit-style-select" id="fbk-commit-style-select" title="${t("toolbar.commitStyleTitle")}">
  <option value="1" ${commitStyle === 1 ? "selected" : ""}>${t("toolbar.oneCommit")}</option>
  <option value="2" ${commitStyle === 2 ? "selected" : ""}>${t("toolbar.separateCommits")}</option>
  </select>`,
    // Dropdown under the user icon: shows identity, the per-user "add comment" shortcut
    // (click to rebind, ↺ to reset), theme + language toggles, and a Sign out action.
    userMenu: (displayName, roleLabel, shortcutLabel, authOwnedByHost, theme = "light", language = "en") => `
  <div class="fbk-menu" id="fbk-user-menu" role="menu">
  <div class="fbk-menu-id">
  <span>${displayName}</span>
  ${roleLabel ? `<span class="fbk-menu-role">${roleLabel}</span>` : ""}
  </div>
  <div class="fbk-menu-shortcut">
  <span class="fbk-menu-shortcut-label">${t("menu.addComment")}</span>
  <button type="button" id="fbk-shortcut-edit" class="fbk-mini" title="${t("menu.clickThenPressKeyCombo")}">${escapeHtml(shortcutLabel)}</button>
  <button type="button" id="fbk-shortcut-reset" class="fbk-mini fbk-icon" title="${t("menu.resetToDefault")}">&#8635;</button>
  </div>
  <div class="fbk-menu-shortcut">
  <span class="fbk-menu-shortcut-label">${t("menu.theme")}</span>
  <button type="button" id="fbk-theme-light" class="fbk-mini fbk-icon${theme === "light" ? " is-active" : ""}" title="${t("menu.light")}" aria-label="${t("menu.lightTheme")}" aria-pressed="${theme === "light"}">${ICON.sun}</button>
  <button type="button" id="fbk-theme-dark" class="fbk-mini fbk-icon${theme === "dark" ? " is-active" : ""}" title="${t("menu.dark")}" aria-label="${t("menu.darkTheme")}" aria-pressed="${theme === "dark"}">${ICON.moon}</button>
  </div>
  <div class="fbk-menu-shortcut">
  <span class="fbk-menu-shortcut-label">${t("menu.language")}</span>
  <button type="button" id="fbk-lang-en" class="fbk-mini${language === "en" ? " is-active" : ""}" aria-pressed="${language === "en"}">EN</button>
  <button type="button" id="fbk-lang-ar" class="fbk-mini${language === "ar" ? " is-active" : ""}" aria-pressed="${language === "ar"}">AR</button>
  </div>
  ${authOwnedByHost ? `<div class="fbk-menu-note fbk-caption">${t("menu.extensionSignedInNote")}</div>` : `<button class="fbk-menu-item" id="fbk-signout" role="menuitem">${ICON.logout}<span>${t("menu.signOut")}</span></button>`}
  </div>`,
    // Collapsed state: a small floating launcher that re-opens the overlay.
    // `rtl` makes start/end resolve against the host page direction (the shadow
    // UI is otherwise forced LTR), so e.g. `top-end` lands top-left on an RTL page.
    launcher: (count, position, rtl, unreadNotifyCount = 0) => {
      let hasUnread = unreadNotifyCount > 0, badgeCount = hasUnread ? unreadNotifyCount : count, openLabel = t("launcher.openFeedbackFor", { brand: escapeHtml(getBrandName()) });
      return `
  <button class="fbk-launcher fbk-pos-${position || "bottom-end"}${rtl ? " fbk-rtl" : ""}" id="fbk-launcher" title="${openLabel}" aria-label="${openLabel}">
  <span class="fbk-launcher-ring" aria-hidden="true"></span>
  ${ICON.bubbleLg}
  ${badgeCount ? `<span class="fbk-launcher-badge${hasUnread ? " fbk-notify-badge" : ""}">${badgeCount > 99 ? t("toolbar.countCap") : badgeCount}</span>` : ""}
  </button>`;
    },
    empty: (msg) => `<div class="fbk-empty">${msg}</div>`,
    // One toast card. `role="alert"` for danger (assertive — interrupts) vs `role="status"` for
    // success/warn (polite — announced without interrupting); the shared container around these
    // already carries `aria-live="polite"`, so screen readers pick either up without extra wiring.
    // `element.ts`'s toast() owns the click listeners (Undo/Retry callback + dismiss); this is a
    // pure string builder like the rest of TPL. `message`/`actionLabel` arrive already resolved
    // through t() by the caller — this function itself does no translation.
    toast: (variant, message, actionLabel) => {
      let icon = variant === "success" ? ICON.checkPlain : variant === "warn" ? ICON.warnTriangle : ICON.dangerCircle;
      return `
  <div class="fbk-toast fbk-toast-${variant}" role="${variant === "danger" ? "alert" : "status"}">
  <div class="fbk-toast-icon" aria-hidden="true">${icon}</div>
  <div class="fbk-toast-content"><span class="fbk-toast-message">${escapeHtml(message)}</span></div>
  ${actionLabel ? `<button type="button" class="fbk-toast-action">${escapeHtml(actionLabel)}</button>` : ""}
  <button type="button" class="fbk-toast-close" aria-label="${t("toast.dismissNotification")}">${ICON.close}</button>
  </div>`;
    },
    // Status filter as a dropdown (rather than a row of chip buttons) — keeps the filter bar compact.
    // Filter labels themselves are server-provided status-catalog text (customizable per project) —
    // never translated by the widget, which cannot know what language an admin wrote them in.
    // Wrapped in a <label> with a visible text label (not just the select's own title tooltip) —
    // sits beside envFilterSelect, which follows the same fbk-filter-field shape.
    statusFilterSelect: (filters, active, counts) => `<label class="fbk-filter-field">
  <span class="fbk-filter-field-label">${t("sidebar.status")}</span>
  <select class="fbk-status-select" id="fbk-status-filter" title="${t("sidebar.filterByStatus")}">
  ${filters.map((f) => {
      var _a2;
      return `<option value="${f.key}" ${f.key === active ? "selected" : ""}>${escapeHtml(f.label)} (${(_a2 = counts[f.key]) != null ? _a2 : 0})</option>`;
    }).join("")}
  </select>
  </label>`,
    // Environment filter — sits beside the status filter (see statusFilterSelect) rather than in the
    // sidebar head, so both scoping controls live together. `fixedEnvLabel` renders a read-only
    // value instead of a select when the install pinned the environment, or the project turned the
    // switcher off for everyone (see `showEnvironmentSelector`); null/undefined renders the switcher.
    envFilterSelect: (fixedEnvLabel, currentValue) => `<label class="fbk-filter-field">
  <span class="fbk-filter-field-label">${t("toolbar.environment")}</span>
  ${fixedEnvLabel ? `<span class="fbk-env-label fbk-caption" title="${t("toolbar.envFixedTitle")}">${escapeHtml(fixedEnvLabel)}</span>` : `<select class="fbk-input fbk-env-select" id="fbk-env" title="${t("toolbar.envSwitchTitle")}">
  <option value="all" ${currentValue === "all" ? "selected" : ""}>${t("toolbar.envAll")}</option>
  <option value="local" ${currentValue === "local" ? "selected" : ""}>${t("toolbar.envLocal")}</option>
  <option value="staging" ${currentValue === "staging" ? "selected" : ""}>${t("toolbar.envStaging")}</option>
  <option value="production" ${currentValue === "production" ? "selected" : ""}>${t("toolbar.envProduction")}</option>
  </select>`}
  </label>`,
    // "Mine only" — a real switch (track + thumb), not a filter chip, since it's a single on/off
    // setting rather than one choice among several. Lives inside #fbk-filters alongside the
    // status/environment/author fields (see renderSidebar) so it hides/shows with the rest of the
    // filter bar. Rendered only when a user is logged in (see renderSidebar's canMine).
    mineToggle: (active) => `<div class="fbk-toggle-row">
  <span class="fbk-toggle-row-label">&#x1f464; ${t("sidebar.mineOnly")}</span>
  <button type="button" class="fbk-toggle-switch${active ? " active" : ""}" id="fbk-mine-toggle" role="switch" aria-checked="${active ? "true" : "false"}" title="${t("sidebar.showOnlyMyComments")}" aria-label="${t("sidebar.showOnlyMyComments")}">
  <span class="fbk-toggle-switch-thumb"></span>
  </button>
  </div>`,
    // User filter — only rendered when the list has comments from >1 author. Wrapped in the same
    // labeled fbk-filter-field shape as statusFilterSelect/envFilterSelect (a visible label above
    // it, not just the select's own title tooltip) — it sits beside "Mine only" in the filter bar's
    // first row (see renderSidebar).
    authorFilter: (authors, selectedId) => `<label class="fbk-filter-field">
  <span class="fbk-filter-field-label">${t("sidebar.user")}</span>
  <select class="fbk-userfilter" id="fbk-author-filter" title="${t("sidebar.filterByUser")}">
  <option value="">&#x1f465; ${t("sidebar.allUsers")}</option>
  ${authors.map((a) => `<option value="${escapeHtml(a.id)}" ${a.id === selectedId ? "selected" : ""}>${escapeHtml(a.name)}</option>`).join("")}
  </select>
  </label>`,
    // Kebab-menu dropdown for a comment card's own actions — rendered into the shared portal host
    // (#fbk-menu-host), anchored under the card's kebab button by toggleCardMenu. Copy-prompt/
    // complete/reopen are workflow actions open to anyone who can see the card; visibility/edit are
    // owner-only; delete only while still open (matches the previous inline buttons' conditions
    // exactly, just relocated). `isQuickAccess` matches TPL.card's own gate on "Complete"/"Reopen" —
    // a quick-access (Client) account never gets to change its own feedback's status directly.
    cardMenu: (c, isQuickAccess, canEditFields = !1) => `
  <div class="fbk-card-menu" id="fbk-card-menu" role="menu">
  ${c.status === "open" || c.status === "pending-apply" ? `<button type="button" class="fbk-card-menu-item" data-menu-act="copy-apply-prompt" role="menuitem">${ICON.copy}<span>${t("card.copyApplyPrompt")}</span></button>` : ""}
  ${!isQuickAccess && (c.status === "open" || c.status === "pending-apply") ? `<button type="button" class="fbk-card-menu-item" data-menu-act="complete" role="menuitem">${ICON.check}<span>${t("card.complete")}</span></button>` : ""}
  ${!isQuickAccess && (c.status === "applied" || c.status === "archived") ? `<button type="button" class="fbk-card-menu-item" data-menu-act="reopen" role="menuitem">${ICON.reopen}<span>${t("card.reopen")}</span></button>` : ""}
  ${c._mine ? `<button type="button" class="fbk-card-menu-item" data-menu-act="visibility" data-private="${c.isPrivate ? "false" : "true"}" role="menuitem">${c.isPrivate ? ICON.unlock : ICON.lock}<span>${c.isPrivate ? t("card.makePublic") : t("card.makePrivate")}</span></button>` : ""}
  ${c._mine ? `<button type="button" class="fbk-card-menu-item" data-menu-act="edit" role="menuitem">${ICON.pencil}<span>${t("card.edit")}</span></button>` : ""}
  ${canEditFields ? `<button type="button" class="fbk-card-menu-item" data-menu-act="edit-fields" role="menuitem">${ICON.extraFields}<span>${t("fields.extra")}</span></button>` : ""}
  ${c.status === "open" ? `<button type="button" class="fbk-card-menu-item danger" data-menu-act="delete" role="menuitem">${ICON.trash}<span>${t("card.delete")}</span></button>` : ""}
  </div>`,
    // Kebab-menu dropdown for a single REPLY (see TPL.card's `kebab` above) — shares the same
    // #fbk-card-menu id/class/portal as the comment's own menu (only one can ever be open at once),
    // rendered by toggleReplyMenu. Copy-apply-prompt reuses the PARENT COMMENT's id — applying
    // still operates on the comment, `get --json` already returns every reply — so this is just a
    // more convenient place to reach it while reading a specific reply's context; gated on the same
    // comment status as the comment's own copy-apply-prompt item.
    replyMenu: (c, r) => `
  <div class="fbk-card-menu" id="fbk-card-menu" role="menu">
  ${c.status === "open" || c.status === "pending-apply" ? `<button type="button" class="fbk-card-menu-item" data-menu-act="copy-apply-prompt" role="menuitem">${ICON.copy}<span>${t("card.copyApplyPrompt")}</span></button>` : ""}
  ${r._mine ? `<button type="button" class="fbk-card-menu-item" data-menu-act="edit" role="menuitem">${ICON.pencil}<span>${t("card.edit")}</span></button>` : ""}
  ${r._mine ? `<button type="button" class="fbk-card-menu-item danger" data-menu-act="delete" role="menuitem">${ICON.trash}<span>${t("card.delete")}</span></button>` : ""}
  </div>`,
    card: (c, i, isQuickAccess) => {
      let cls = c.status === "pending-apply" ? "pending" : c.status === "applied" ? "applied" : c.status === "archived" ? "archived" : "", statusPill = c.status === "applied" && c.deployedAt ? `<span class="fbk-pill status-applied" title="${escapeHtml(t("card.deployedIn", { sha: (c.deployedSha || "").slice(0, 7) }))}">&#x2713; ${t("card.live")}</span>` : c.status === "applied" ? `<span class="fbk-pill status-applied">&#x2713; ${t("card.completed")}</span>` : c.status === "pending-apply" ? `<span class="fbk-pill status-pending">${t("card.pending")}</span>` : c.status === "archived" ? `<span class="fbk-pill status-archived">&#x1f4e6; ${t("card.archived")}</span>` : "", verifiedPill = c.status === "applied" && c.verifiedAt ? `<span class="fbk-pill verified">&#x2713; ${t("card.verified")}</span>` : "", verifyGroup = c.status === "applied" && !c.verifiedAt && c._canVerify ? `<span class="fbk-verify-group">
  <button class="fbk-mini fbk-verify-ok" data-act="verify-ok" data-id="${c.id}" title="${t("card.looksRight")}">${ICON.thumbsUp}<span>${t("card.looksRight")}</span></button>
  <button class="fbk-mini fbk-verify-reject" data-act="verify-reject" data-id="${c.id}" title="${t("card.notFixed")}">${ICON.thumbsDown}<span>${t("card.notFixed")}</span></button>
  </span>` : "", verifyBox = c.status === "applied" && !c.verifiedAt && c._canVerify ? `<div class="fbk-verify-box fbk-hidden" id="fbk-verify-box-${c.id}">
  <input class="fbk-input fbk-verify-note-input" id="fbk-verify-note-${c.id}" placeholder="${t("card.explainNotFixed")}" />
  <div class="fbk-verify-actions">
  <button class="fbk-mini primary" data-act="verify-submit" data-id="${c.id}">${t("card.submit")}</button>
  <button class="fbk-mini" data-act="verify-cancel" data-id="${c.id}">${t("toolbar.cancel")}</button>
  </div>
  </div>` : "", commitLink = c.status === "applied" && c.commitUrl ? `<a class="fbk-pill" href="${escapeHtml(c.commitUrl)}" target="_blank" rel="noopener noreferrer" title="${t("card.viewCommit")}">&#x1f517; ${t("card.commit")}</a>` : "", payloadPill = c.hasPayloadFlag ? `<span class="fbk-pill fbk-payload-flag" title="${escapeHtml((c.payloadFlags || []).join(", "))}">&#x26a0; ${t("card.containsSecretPayload")}</span>` : "", replies = (c.replies || []).map((r) => {
        var _a2, _b, _c;
        let body = escapeHtml(r.body || r.text || ""), authorName = escapeHtml(r.authorName || r.authorLabel || t("card.defaultReplyAuthor"));
        if (r.isAi) {
          let attributionParts = [];
          r.aiTool && attributionParts.push(escapeHtml(r.aiTool)), r.aiModel && attributionParts.push(escapeHtml(r.aiModel));
          let knownAuthorName = r.authorName || r.authorLabel;
          knownAuthorName && attributionParts.push(t("card.aiVia", { name: escapeHtml(knownAuthorName) }));
          let attribution = attributionParts.length > 0 ? `<span class="fbk-reply-ai-author">${attributionParts.join(" &middot; ")}</span>` : "";
          return `<details class="fbk-reply fbk-reply-ai" data-reply-id="${(_a2 = r.id) != null ? _a2 : ""}">
  <summary class="fbk-reply-ai-summary">&#x1f916; <b>${t("card.automatedReply")}</b> ${attribution}</summary>
  <div class="fbk-reply-main"><span class="fbk-reply-body">${body}</span></div>
  </details>`;
        }
        let kebab = r._mine || c.status === "open" || c.status === "pending-apply" ? `<button type="button" class="fbk-mini fbk-icon fbk-reply-kebab" data-act="reply-menu" data-comment-id="${c.id}" data-reply-id="${(_b = r.id) != null ? _b : ""}" title="${t("card.moreActions")}" aria-label="${t("card.moreActions")}" aria-haspopup="true" aria-expanded="false">${ICON.kebab}</button>` : "";
        return `<div class="fbk-reply" data-reply-id="${(_c = r.id) != null ? _c : ""}">
  <div class="fbk-reply-main"><b>${authorName}:</b> <span class="fbk-reply-body">${body}</span></div>
  ${kebab}
  </div>`;
      }).join(""), envInt = c.environment, envLabel = envInt === 1 ? t("card.envLocal") : envInt === 2 ? t("card.envStaging") : envInt === 3 ? t("card.envProduction") : envInt ? String(envInt) : "", authorLabel = c.authorName || "", pageUrl = c.element && c.element.pageUrl, pagePath = (() => {
        if (!pageUrl) return "";
        try {
          let u = new URL(pageUrl);
          return u.pathname + u.search;
        } catch {
          return pageUrl;
        }
      })(), shotUrl = c.element && c.element.screenshotUrl, shot = shotUrl ? `<a class="fbk-shot-link" href="${escapeHtml(shotUrl)}" target="_blank" rel="noopener noreferrer" title="${t("card.openFullScreenshot")}">
  <img class="fbk-shot" src="${escapeHtml(shotUrl)}" alt="${t("card.elementScreenshot")}" loading="lazy" />
  </a>` : "";
      return `
  <div class="fbk-card ${cls}" data-id="${c.id}">
  <div class="fbk-meta">
  <button type="button" class="fbk-badge" data-act="flash-pin" data-id="${c.id}" title="${t("card.jumpToPin")}">#${c.id}</button>
  ${envLabel ? `<span class="fbk-pill env">${escapeHtml(envLabel)}</span>` : ""}
  ${payloadPill}
  ${statusPill}
  ${verifiedPill}
  ${verifyGroup}
  ${commitLink}
  ${c._mine || c.status === "open" || c.status === "pending-apply" || !isQuickAccess && (c.status === "applied" || c.status === "archived") ? `<div class="fbk-actions-end">
  <button class="fbk-mini fbk-icon fbk-card-kebab" data-act="card-menu" data-id="${c.id}" title="${t("card.moreActions")}" aria-label="${t("card.moreActions")}" aria-haspopup="true" aria-expanded="false">${ICON.kebab}</button>
  </div>` : ""}
  </div>
  ${pagePath ? `<div class="fbk-caption fbk-card-page" title="${escapeHtml(pageUrl)}">&#x1f4cd; ${escapeHtml(pagePath)}</div>` : ""}
  <div class="fbk-text fbk-text-clamped" data-id="${c.id}"><button type="button" class="fbk-read-more-btn fbk-hidden" data-act="toggle-read-more" data-id="${c.id}">… ${t("card.readMore")}</button><span class="fbk-text-content">${escapeHtml(c.body || c.text || "")}</span></div>
  ${c.customFields && c.customFields.length > 0 ? `<div class="fbk-card-fields-wrapper">
  <dl class="fbk-card-fields">
  ${c.customFields.map((f) => {
        let valHtml = escapeHtml(f.value);
        if (f.type === 2 || f.type === "Url")
          try {
            let u = new URL(f.value);
            if (u.protocol === "http:" || u.protocol === "https:") {
              let full = u.host + u.pathname;
              valHtml = `<a href="${escapeHtml(f.value)}" target="_blank" rel="noopener noreferrer">${escapeHtml(full.length > 60 ? full.slice(0, 60) + "…" : full)}</a>`;
            }
          } catch {
          }
        return `<dt>${escapeHtml(f.label)}</dt><dd class="fbk-card-field-val"><span class="fbk-card-field-val-text">${valHtml}</span><button type="button" class="fbk-field-more-btn fbk-hidden" data-act="toggle-field-more">${t("fields.seeMore")}</button></dd>`;
      }).join("")}
  </dl>
  ${!isQuickAccess && (c._mine || c._canVerify) ? `<button type="button" class="fbk-card-fields-edit-btn" data-act="edit-fields" data-id="${c.id}" title="${t("fields.extra")}" aria-label="${t("fields.extra")}">${ICON.pencil}</button>` : ""}
  </div>` : ""}
  ${shot}
  <div class="fbk-sub">${escapeHtml(authorLabel)} &middot; ${c.createdAt ? new Date(c.createdAt).toLocaleDateString() : ""}${c.editedAt ? ` &middot; <span class="fbk-edited">${t("card.edited")}</span>` : ""}</div>
  ${verifyBox}
  ${replies ? `<div class="fbk-replies">${replies}</div>` : ""}
  <div class="fbk-reply-row">
  <button type="button" class="fbk-mini fbk-reply-toggle" data-act="reply-toggle" data-id="${c.id}">${ICON.bubbleSm}<span>${t("card.reply")}</span></button>
  <textarea class="fbk-textarea fbk-reply-input fbk-hidden" placeholder="${t("card.replyPlaceholder")}" data-id="${c.id}" rows="1"></textarea>
  </div>
  <div class="fbk-actions">
  ${isQuickAccess || c.status === "applied" || c.status === "archived" ? "" : `<button class="fbk-mini ${c.status === "pending-apply" ? "apply" : "ready"}" data-act="apply" data-id="${c.id}" title="${c.status === "pending-apply" ? t("card.markedReadyClickToUnmark") : t("card.markReadyToApply")}">
  ${ICON.flag}<span>${t("card.ready")}</span>
  </button>`}
  ${!isQuickAccess && c.status === "applied" ? `<button class="fbk-mini" data-act="archive" data-id="${c.id}" title="${t("card.archive")}">${ICON.archive}<span>${t("card.archive")}</span></button>` : ""}
  </div>
  </div>`;
    },
    // `bugReportEnabled`: only true when the project has page-context capture turned on — the
    // checkbox controls whether the console/network buffer already sitting in memory gets attached
    // to THIS comment; it never controls whether that buffer exists (see pagecontext.ts).
    popover: (meta, left, top, shotEnabled, actions = [], bugReportEnabled = !1, commentFields = []) => `
  <div class="fbk-popover" data-fbk-left="${left}" data-fbk-top="${top}">
  <div class="fbk-popover-nav">
  <button type="button" class="fbk-popover-nav-btn" id="fbk-target-up" data-toggle="tooltip" data-placement="top" title="${t("popover.selectParentElement")}" aria-label="${t("popover.selectParentElement")}">${ICON.chevronUp}</button>
  <button type="button" class="fbk-popover-nav-btn" id="fbk-target-down" data-toggle="tooltip" data-placement="top" title="${t("popover.selectFirstChildElement")}" aria-label="${t("popover.selectFirstChildElement")}">${ICON.chevronDown}</button>
  <button type="button" class="fbk-popover-private-toggle" id="fbk-comment-private" data-toggle="tooltip" data-placement="top" title="${t("card.makePrivateOnlyYou")}" aria-label="${t("card.makePrivate")}" aria-pressed="false">${ICON.unlock}</button>
  </div>
  <h3 id="fbk-popover-title">${t("popover.commentOn")} &lt;${escapeHtml(meta._tag)}&gt;</h3>
  <div class="fbk-snippet" id="fbk-popover-snippet">${escapeHtml(meta._snapshotPreview.slice(0, 200))}</div>
  <div class="fbk-src${meta._sourcePath ? "" : " fbk-hidden"}" id="fbk-popover-src">&#x26ec; <span id="fbk-popover-src-path">${escapeHtml(meta._sourcePath || "")}</span></div>
  <textarea class="fbk-textarea" id="fbk-comment-text" placeholder="${t("popover.whatShouldChange")}"></textarea>
  ${actions.length ? `<div class="fbk-field-label">${t("popover.predefinedPrompts")}</div>
  <div class="fbk-ms" id="fbk-action-ms">
  <div class="fbk-ms-control" id="fbk-action-ms-control">
  <div class="fbk-ms-chips" id="fbk-action-ms-chips"></div>
  <input type="text" class="fbk-ms-input" id="fbk-action-ms-input" placeholder="${t("popover.searchPrompts")}" autocomplete="off" role="combobox" aria-expanded="false" aria-haspopup="listbox" aria-label="${t("popover.searchPredefinedPrompts")}" />
  </div>
  <div class="fbk-ms-list" id="fbk-action-ms-list" role="listbox" hidden></div>
  </div>` : ""}
  ${commentFields.length > 0 ? `<div class="fbk-popover-more-fields-row"><button type="button" id="fbk-more-fields" class="fbk-popover-more-link" aria-expanded="false" aria-controls="fbk-extra-fields">${t("fields.extra")}</button></div>
  <div id="fbk-extra-fields" class="fbk-extra-fields" hidden>${renderFieldInputs(commentFields, {}, "cf")}</div>` : ""}
  ${shotEnabled || bugReportEnabled ? `<div class="fbk-popover-toggles">
  ${shotEnabled ? `<div class="fbk-popover-toggle-row"><button type="button" class="fbk-toggle-switch fbk-toggle-switch-sm" id="fbk-comment-shot" role="switch" aria-checked="false" aria-label="${t("popover.attachScreenshot")}" title="${t("popover.attachScreenshot")}"><span class="fbk-toggle-switch-thumb"></span></button><span class="fbk-popover-toggle-label">${ICON.camera} ${t("popover.attachScreenshot")}</span></div>` : ""}
  ${bugReportEnabled ? `<div class="fbk-popover-toggle-row"><button type="button" class="fbk-toggle-switch fbk-toggle-switch-sm" id="fbk-comment-bug" role="switch" aria-checked="false" aria-label="${t("popover.reportAsABug")}" title="${t("popover.reportBugTitle")}"><span class="fbk-toggle-switch-thumb"></span></button><span class="fbk-popover-toggle-label" title="${t("popover.reportBugTitle")}">${ICON.bug} ${t("popover.reportAsABug")}</span></div>` : ""}
  </div>` : ""}
  <div class="fbk-reply-row">
  <button class="fbk-btn primary fbk-btn-fill" id="fbk-submit">${t("popover.add")}</button>
  <button class="fbk-mini" id="fbk-cancel">${t("toolbar.cancel")}</button>
  </div>
  </div>`,
    // `isNew`: shows the attention ripple once, on the pin for the comment the viewer just added
    // this session (see element.ts's `_newPinId`) — never for a pin restored from a re-render.
    // `rect` only ever needs left/top (the pixel the pin's tip anchors to), not a full DOMRect —
    // element.ts's cluster grouping passes a plain centroid point, not a real element rect.
    // `tipSide`: 'top' (default) opens the hover tooltip above the pin, 'bottom' flips it below —
    // element.ts's renderPins() decides based on how close the pin sits to the viewport's top edge.
    // `tipAlign`: 'center' (default) centers the tooltip on the pin, 'start'/'end' anchor it to
    // that edge of the pin instead — same reasoning as tipSide, but for the left/right viewport edge.
    // The pin's own number is the comment's id (matches the card's id badge in the sidebar — see
    // card() below — so a viewer can tell which pin a given card refers to at a glance), not a
    // position index, which would drift out of sync with the badge as soon as the list is
    // filtered/sorted differently from the pin layer's own z-order.
    pin: (c, rect, isNew = !1, tipSide = "top", tipAlign = "center") => {
      let status = c.status === "pending-apply" ? "ready" : c.status === "applied" ? "applied" : c.status === "archived" ? "archived" : "open", statusLabel = status === "ready" ? t("pin.ready") : status === "applied" ? t("pin.applied") : status === "archived" ? t("pin.archived") : t("pin.open"), author = c.authorName || "", bodyText = c.body || c.text || "", replyCount = (c.replies || []).length, selector = c.element && c.element.selector || "", label = `${t("pin.commentHash", { n: c.id })}${author ? t("pin.byAuthor", { author }) : ""}${bodyText ? `: ${bodyText}` : ""}`, showMeta = !!(selector || replyCount);
      return `
  <div class="fbk-pin-wrapper" data-id="${c.id}" data-fbk-left="${rect.left}" data-fbk-top="${rect.top}" data-fbk-tip-side="${tipSide}" data-fbk-tip-align="${tipAlign}">
  <button type="button" class="fbk-pin fbk-pin-${status}" aria-label="${escapeHtml(label)}">
  ${isNew ? '<span class="fbk-pin-ring" aria-hidden="true"></span>' : ""}
  ${status === "applied" ? ICON.checkBold : `<span class="fbk-pin-number">${c.id}</span>`}
  </button>
  <div class="fbk-pin-tooltip" role="tooltip">
  <div class="fbk-pin-tooltip-header">
  <span class="fbk-pin-status-badge fbk-status-${status}">${statusLabel}</span>
  ${author ? `<span class="fbk-pin-author">${escapeHtml(author)}</span>` : ""}
  <span class="fbk-pin-time">${escapeHtml(timeAgo(c.createdAt))}</span>
  </div>
  ${bodyText ? `<p class="fbk-pin-tooltip-body">${escapeHtml(bodyText)}</p>` : ""}
  ${showMeta ? `
  <div class="fbk-pin-tooltip-meta">
  ${selector ? `<span class="fbk-pin-tag" title="${escapeHtml(selector)}">${escapeHtml(selector)}</span>` : "<span></span>"}
  ${replyCount ? `<span class="fbk-pin-reply-count">${ICON.bubbleSm} ${replyCount === 1 ? t("pin.reply") : t("pin.replies", { n: replyCount })}</span>` : ""}
  </div>` : ""}
  </div>
  </div>`;
    },
    // 2+ pins within 24px of each other (see element.ts's renderPins clustering) render as one
    // expandable "N+" badge instead of stacking indistinguishable pins on top of each other.
    // Clicking it opens pinClusterMenu below, listing each one individually.
    pinCluster: (comments, rect) => `
  <div class="fbk-pin-wrapper" data-ids="${comments.map((c) => c.id).join(",")}" data-fbk-left="${rect.left}" data-fbk-top="${rect.top}">
  <button type="button" class="fbk-pin fbk-pin-cluster" aria-label="${t("pin.overlappingComments", { n: comments.length })}" aria-expanded="false">
  <span class="fbk-pin-cluster-count">${comments.length}+</span>
  <span class="fbk-pin-cluster-dots" aria-hidden="true">&bull;&bull;&bull;</span>
  </button>
  </div>`,
    // Expanded cluster — one row per clustered comment, click-through to the same
    // scroll+highlight behavior as clicking a standalone pin.
    pinClusterMenu: (comments) => `
  <div class="fbk-pin-cluster-menu" id="fbk-pin-cluster-menu" role="menu">
  ${comments.map((c) => {
      let status = c.status === "pending-apply" ? "ready" : c.status === "applied" ? "applied" : c.status === "archived" ? "archived" : "open", statusLabel = status === "ready" ? t("pin.ready") : status === "applied" ? t("pin.applied") : status === "archived" ? t("pin.archived") : t("pin.open"), bodyText = c.body || c.text || "";
      return `
  <button type="button" class="fbk-pin-cluster-item" data-id="${c.id}" role="menuitem">
  <span class="fbk-pin-status-badge fbk-status-${status}">${statusLabel}</span>
  <span class="fbk-pin-cluster-item-text">${escapeHtml(bodyText)}</span>
  </button>`;
    }).join("")}
  </div>`,
    notificationsMenu: (items) => `
  <div class="fbk-notifications-menu" id="fbk-notifications-menu" role="menu">
  <div class="fbk-notifications-head">
  <h3>${t("notifications.updates")}</h3>
  </div>
  <div class="fbk-notifications-body">
  ${items.length === 0 ? `<div class="fbk-empty fbk-notifications-empty">${t("notifications.noUpdatesYet")}</div>` : items.map((item) => {
      var _a2, _b;
      let isUnread = !item.readAt, typeLabel = t("notifications.update"), icon = ICON.pin, detail = "", typeNum = typeof item.type == "string" ? item.type === "CommentApplied" ? 1 : item.type === "CommentReopened" ? 2 : item.type === "ReplyAdded" ? 3 : 0 : item.type;
      typeNum === 1 ? (typeLabel = t("notifications.applied"), icon = ICON.check, detail = (_a2 = item.payload) != null && _a2.commitUrl ? `<div class="fbk-notification-item-commit"><a class="fbk-pill" href="${escapeHtml(item.payload.commitUrl)}" target="_blank" rel="noopener noreferrer" onclick="event.stopPropagation()">&#x1f517; ${t("notifications.commit")}</a></div>` : "") : typeNum === 2 ? (typeLabel = t("notifications.reopened"), icon = ICON.reopen) : typeNum === 3 && (typeLabel = t("notifications.newReply"), icon = ICON.inspect, (_b = item.payload) != null && _b.replyExcerpt && (detail = `<div class="fbk-notification-reply fbk-caption">"${escapeHtml(item.payload.replyExcerpt)}"</div>`));
      let timeAgo2 = item.createdAt ? new Date(item.createdAt).toLocaleDateString() : "";
      return `
  <div class="fbk-notification-item${isUnread ? " unread" : ""}" data-id="${item.commentId}" role="menuitem">
  <div class="fbk-notification-item-head">
  <span class="fbk-notification-item-type">${icon} ${escapeHtml(typeLabel)}</span>
  <span class="fbk-notification-item-time">${escapeHtml(timeAgo2)}</span>
  </div>
  <div class="fbk-notification-item-body">
  ${escapeHtml(item.commentBodyExcerpt || "")}
  </div>
  ${detail}
  </div>`;
    }).join("")}
  </div>
  </div>`
  };

  // src/framework-source.ts
  function toPortablePath(raw) {
    if (/^https?:\/\//.test(raw))
      try {
        return new URL(raw).pathname.replace(/^\//, "");
      } catch {
        return raw;
      }
    let srcIdx = raw.lastIndexOf("/src/");
    return srcIdx >= 0 ? raw.slice(srcIdx + 1) : raw;
  }
  function reactStackToSourcePath(stack) {
    let lines = stack.split(`
`).slice(1);
    for (let line of lines) {
      if (/node_modules|react-dom|react_jsx|react-jsx/.test(line)) continue;
      let m = line.match(/\((https?:\/\/[^\s)]+):(\d+):(\d+)\)/) || line.match(/at (https?:\/\/[^\s)]+):(\d+):(\d+)/);
      if (m) return `${toPortablePath(m[1])}:${m[2]}`;
    }
    return null;
  }
  function detectReactSource(el) {
    let key = Object.getOwnPropertyNames(el).find((k) => k.startsWith("__reactFiber"));
    if (!key) return null;
    let fiber = el[key];
    if (!fiber) return null;
    let legacy = fiber._debugSource;
    if (legacy && legacy.fileName)
      return `${toPortablePath(legacy.fileName)}:${legacy.lineNumber || 0}`;
    let debugStack = fiber._debugStack;
    if (debugStack && typeof debugStack.stack == "string") {
      let found = reactStackToSourcePath(debugStack.stack);
      if (found) return found;
    }
    return null;
  }
  function detectVueSource(el) {
    let instance = el.__vueParentComponent, depth = 0;
    for (; instance && depth < 6; ) {
      let type = instance.type, file = type && type.__file || instance.__file;
      if (file) return toPortablePath(file);
      instance = instance.parent, depth++;
    }
    return null;
  }
  function detectFrameworkSourcePath(el) {
    try {
      let react = detectReactSource(el);
      if (react) return react;
    } catch {
    }
    try {
      let vue = detectVueSource(el);
      if (vue) return vue;
    } catch {
    }
    return null;
  }

  // src/capture.ts
  var snapdomPromise = null;
  function loadSnapdom() {
    return window.snapdom ? Promise.resolve(window.snapdom) : snapdomPromise || (snapdomPromise = new Promise((resolve, reject) => {
      var _a2;
      let s = document.createElement("script");
      s.src = ((_a2 = window.__POINTER_CONFIG__) == null ? void 0 : _a2.snapdomUrl) || SNAPDOM_URL, s.async = !0, s.onload = () => window.snapdom ? resolve(window.snapdom) : reject(new Error("snapdom loaded but window.snapdom missing")), s.onerror = () => reject(new Error("failed to load " + SNAPDOM_URL)), document.head.appendChild(s);
    }), snapdomPromise);
  }
  function canvasToBlob(canvas) {
    return new Promise((resolve) => {
      let done = (b) => resolve(b || null);
      try {
        canvas.toBlob((b) => {
          if (b) return done(b);
          canvas.toBlob(done, "image/jpeg", 0.6);
        }, "image/webp", 0.6);
      } catch {
        try {
          canvas.toBlob(done, "image/jpeg", 0.6);
        } catch {
          done(null);
        }
      }
    });
  }
  async function captureScreenshot(el) {
    try {
      let full = await (await loadSnapdom()).toCanvas(document.body, {
        backgroundColor: "#fff",
        // Skip our own shadow host entirely as a belt-and-braces measure.
        exclude: ["pointer-feedback"],
        fast: !0
      }), dpr = window.devicePixelRatio || 1, sx = Math.max(0, Math.round(window.scrollX * dpr)), sy = Math.max(0, Math.round(window.scrollY * dpr)), vw = Math.round(window.innerWidth * dpr), vh = Math.round(window.innerHeight * dpr), cw = Math.min(vw, full.width - sx), ch = Math.min(vh, full.height - sy), view = document.createElement("canvas");
      view.width = Math.max(1, cw), view.height = Math.max(1, ch);
      let vctx = view.getContext("2d");
      vctx.drawImage(full, sx, sy, view.width, view.height, 0, 0, view.width, view.height);
      let rect = el.getBoundingClientRect(), lineW = Math.max(2, Math.round(2 * dpr));
      vctx.strokeStyle = SHOT_HIGHLIGHT, vctx.lineWidth = lineW, vctx.strokeRect(
        Math.round(rect.left * dpr) + lineW / 2,
        Math.round(rect.top * dpr) + lineW / 2,
        Math.max(0, Math.round(rect.width * dpr) - lineW),
        Math.max(0, Math.round(rect.height * dpr) - lineW)
      );
      let out = view;
      if (view.width > SHOT_MAX_WIDTH) {
        let scale = SHOT_MAX_WIDTH / view.width, small = document.createElement("canvas");
        small.width = SHOT_MAX_WIDTH, small.height = Math.max(1, Math.round(view.height * scale)), small.getContext("2d").drawImage(view, 0, 0, small.width, small.height), out = small;
      }
      return await canvasToBlob(out);
    } catch (err) {
      return console.warn("[pointer-feedback] screenshot capture failed", err), null;
    }
  }
  var VOID_ELEMENTS = /^(area|base|br|col|embed|hr|img|input|link|meta|param|source|track|wbr)$/;
  function escapeAttr(val) {
    return val.replace(/"/g, "&quot;").replace(/</g, "&lt;");
  }
  function isMasked(el) {
    if (typeof el.closest == "function")
      return !!el.closest("[data-snapshot-mask]");
    let curr = el;
    for (; curr; ) {
      if (curr.hasAttribute && curr.hasAttribute("data-snapshot-mask")) return !0;
      curr = curr.parentElement;
    }
    return !1;
  }
  function isFormValueTag(tag) {
    return /^(input|textarea|select|option)$/i.test(tag);
  }
  function isSensitiveAttr(name) {
    let lower = name.toLowerCase();
    if (lower === "value" || lower === "authorization" || lower === "srcdoc")
      return !0;
    if (lower.startsWith("data-")) {
      let rest = lower.slice(5);
      if (rest === "value" || rest === "email" || rest === "token" || rest === "secret" || rest.startsWith("user"))
        return !0;
    }
    return !1;
  }
  function maskAttrValue(name, val) {
    let lower = name.toLowerCase();
    return lower === "id" || lower === "type" || lower === "role" || lower.startsWith("aria-") ? escapeAttr(val) : "•••";
  }
  function shallowSnapshot(el, captureText = !0) {
    let tag = el.tagName.toLowerCase(), masked = isMasked(el), isForm = isFormValueTag(tag), rawAttrs = Array.from(el.attributes).filter(
      (a) => a.name !== "class" && a.name !== "style"
    ), keptAttrs = [];
    for (let a of rawAttrs) {
      let name = a.name, lower = name.toLowerCase();
      if (isSensitiveAttr(lower) || tag === "input" && (lower === "data-snapshot-mask" || !(lower === "type" || lower === "name" || lower === "id" || lower === "placeholder" || lower.startsWith("aria-") || lower.startsWith("data-"))) || isForm && lower === "value") continue;
      let rawVal = (a.value || "").slice(0, 120);
      if (masked) {
        let maskedVal = maskAttrValue(name, rawVal);
        keptAttrs.push(rawVal ? `${name}="${maskedVal}"` : name);
      } else {
        let escaped = escapeAttr(rawVal);
        keptAttrs.push(rawVal ? `${name}="${escaped}"` : name);
      }
    }
    if (tag === "input") {
      let inputEl = el;
      typeof inputEl.value == "string" && inputEl.value !== "" && keptAttrs.push('value="•••"');
    }
    let attrs = keptAttrs.join(" "), open = attrs ? `<${tag} ${attrs}>` : `<${tag}>`;
    if (VOID_ELEMENTS.test(tag)) return attrs ? `<${tag} ${attrs}/>` : `<${tag}/>`;
    let text = "";
    if (!captureText)
      text = (el.textContent || "").replace(/\s+/g, " ").trim() ? "•••" : "";
    else if (masked)
      text = (el.textContent || "").replace(/\s+/g, " ").trim() ? "•••" : "";
    else if (tag === "option")
      text = "";
    else if (tag === "textarea") {
      let ta = el;
      text = (typeof ta.value == "string" && ta.value !== "" ? ta.value : el.textContent || "").trim() ? "•••" : "";
    } else if (tag === "select") {
      let sel = el, hasOpts = sel.options && sel.options.length > 0, raw = (el.textContent || "").trim();
      text = hasOpts || raw || sel.value ? "•••" : "";
    } else
      text = (el.textContent || "").replace(/\s+/g, " ").trim().slice(0, 160);
    return `${open}${text}</${tag}>`;
  }
  function skipSelector(sel, classSet) {
    let s = sel.trim();
    if (s.includes("*") || /::(before|after|backdrop|selection|placeholder|marker)/i.test(s) || !/[.#[]/.test(s)) return !0;
    if (!/[ >+~,]/.test(s) && s.startsWith(".")) {
      let cls = s.slice(1).replace(/\\/g, "");
      if (classSet.has(cls)) return !0;
    }
    return !1;
  }
  function collectAppliedRules(rules, el, out, cap, classSet) {
    for (let rule of Array.from(rules)) {
      if (out.length >= cap) return;
      let styleRule = rule;
      if (styleRule.selectorText) {
        let sel = styleRule.selectorText;
        if (skipSelector(sel, classSet)) continue;
        try {
          el.matches(sel) && out.push({ selector: sel.slice(0, 160), styles: (styleRule.style.cssText || "").slice(0, 200) });
        } catch {
        }
      } else {
        let grouped = rule.cssRules;
        grouped && collectAppliedRules(grouped, el, out, cap, classSet);
      }
    }
  }
  function captureMetadata(el, sourceAttr, options) {
    let captureText = (options == null ? void 0 : options.captureText) !== !1, selector = generateSelector(el), snapshot = shallowSnapshot(el, captureText), classes = el.className && typeof el.className == "string" ? el.className.split(/\s+/).filter(Boolean) : [], computed = {}, applied = [], cs = window.getComputedStyle(el);
    ["color", "background-color", "font-size", "font-weight", "margin", "padding", "border", "text-align", "display", "flex-direction"].forEach((p) => {
      let v = cs.getPropertyValue(p);
      v && (computed[p] = v.trim());
    });
    let inlineStyle = el.style;
    inlineStyle && inlineStyle.cssText && (computed["inline-style"] = inlineStyle.cssText);
    let APPLIED_RULES_CAP = 6, classSet = new Set(classes);
    for (let sheet of Array.from(document.styleSheets)) {
      if (applied.length >= APPLIED_RULES_CAP) break;
      let rules;
      try {
        rules = sheet.cssRules || sheet.rules;
      } catch {
        continue;
      }
      rules && collectAppliedRules(rules, el, applied, APPLIED_RULES_CAP, classSet);
    }
    let parent = {};
    if (el.parentElement) {
      let p = el.parentElement;
      parent = {
        tag: p.tagName.toLowerCase(),
        classes: p.className && typeof p.className == "string" ? p.className.split(/\s+/).filter(Boolean) : [],
        id: p.id || null
      };
    }
    let sourcePath = null, node = el;
    for (; node && node.getAttribute; ) {
      let v = node.getAttribute(sourceAttr);
      if (v) {
        sourcePath = v;
        break;
      }
      node = node.parentElement;
    }
    return sourcePath || (sourcePath = detectFrameworkSourcePath(el)), {
      // Internal display fields (not sent to API)
      _tag: el.tagName.toLowerCase(),
      _sourcePath: sourcePath,
      _snapshotPreview: snapshot,
      // API element shape (camelCase)
      selector,
      snapshot,
      classes: JSON.stringify(classes),
      computedStyles: JSON.stringify(computed),
      appliedCssRules: JSON.stringify(applied),
      sourcePath,
      parentInfo: JSON.stringify(parent)
    };
  }

  // src/shortcut.ts
  var DEFAULT_SHORTCUT = { code: "KeyC", alt: !0, shift: !0, ctrl: !0, meta: !1 }, MODIFIER_TOKENS = /* @__PURE__ */ new Set(["ctrl", "alt", "shift", "meta"]);
  function parseShortcut(raw) {
    if (!raw) return { ...DEFAULT_SHORTCUT };
    let parts = raw.split("+").map((p) => p.trim()).filter(Boolean), code = parts[parts.length - 1];
    if (!code || MODIFIER_TOKENS.has(code.toLowerCase())) return { ...DEFAULT_SHORTCUT };
    let mods = new Set(parts.slice(0, -1).map((p) => p.toLowerCase()));
    return {
      code,
      ctrl: mods.has("ctrl"),
      alt: mods.has("alt"),
      shift: mods.has("shift"),
      meta: mods.has("meta")
    };
  }
  function serializeShortcut(binding) {
    let parts = [];
    return binding.ctrl && parts.push("ctrl"), binding.alt && parts.push("alt"), binding.shift && parts.push("shift"), binding.meta && parts.push("meta"), parts.push(binding.code), parts.join("+");
  }
  function matchesShortcut(e, binding) {
    return e.code === binding.code && e.altKey === binding.alt && e.shiftKey === binding.shift && e.ctrlKey === binding.ctrl && e.metaKey === binding.meta;
  }
  function isMacPlatform() {
    return typeof navigator == "undefined" ? !1 : /Mac|iPhone|iPad|iPod/.test(navigator.platform || navigator.userAgent || "");
  }
  function codeToLabel(code) {
    return code.startsWith("Key") ? code.slice(3) : code.startsWith("Digit") ? code.slice(5) : code === "Space" ? "Space" : code === "Escape" ? "Esc" : code === "Enter" ? "Enter" : code;
  }
  function formatShortcut(binding, mac = isMacPlatform()) {
    let label = codeToLabel(binding.code), parts = [];
    return mac ? (binding.ctrl && parts.push("⌃"), binding.alt && parts.push("⌥"), binding.shift && parts.push("⇧"), binding.meta && parts.push("⌘"), parts.push(label), parts.join("")) : (binding.ctrl && parts.push("Ctrl"), binding.meta && parts.push("Win"), binding.alt && parts.push("Alt"), binding.shift && parts.push("Shift"), parts.push(label), parts.join("+"));
  }
  function ariaKeyshortcuts(binding) {
    let parts = [];
    return binding.ctrl && parts.push("Control"), binding.alt && parts.push("Alt"), binding.shift && parts.push("Shift"), binding.meta && parts.push("Meta"), parts.push(codeToLabel(binding.code)), parts.join("+");
  }

  // src/theme.ts
  function relativeLuminance(r, g, b) {
    let lin = (c) => {
      let s = c / 255;
      return s <= 0.03928 ? s / 12.92 : Math.pow((s + 0.055) / 1.055, 2.4);
    };
    return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
  }
  function parseRgba(color) {
    let m = color.match(/rgba?\(([^)]+)\)/i);
    if (!m) return null;
    let parts = m[1].split(",").map((s) => parseFloat(s.trim())), [r, g, b] = parts, a = parts[3];
    return r === void 0 || g === void 0 || b === void 0 || [r, g, b].some((n) => Number.isNaN(n)) ? null : [r, g, b, a === void 0 || Number.isNaN(a) ? 1 : a];
  }
  function detectSystemTheme() {
    var _a2;
    try {
      return typeof window != "undefined" && ((_a2 = window.matchMedia) != null && _a2.call(window, "(prefers-color-scheme: dark)").matches) ? "dark" : "light";
    } catch {
      return "light";
    }
  }
  function detectSiteTheme() {
    try {
      let candidates = [document.body, document.documentElement].filter(Boolean);
      for (let el of candidates) {
        let rgba = parseRgba(getComputedStyle(el).backgroundColor);
        if (rgba && rgba[3] > 0) return relativeLuminance(rgba[0], rgba[1], rgba[2]) < 0.5 ? "dark" : "light";
      }
    } catch {
    }
    return detectSystemTheme();
  }

  // src/auth-ui.ts
  function showLoginModal(host, afterLogin) {
    host.afterLogin = afterLogin || null, host.root.innerHTML = TPL.loginModal(host.project);
    let skipBtn = host.root.querySelector("#fbk-login-skip");
    skipBtn && skipBtn.addEventListener("click", () => {
      host.afterLogin = null, host.renderChrome();
    }), renderLoginView(host);
  }
  async function populateRoles(host, selectEl, errEl) {
    if (selectEl) {
      selectEl.disabled = !0, selectEl.innerHTML = `<option value="">${t("auth.loadingRoles")}</option>`;
      try {
        let roles = await host.apiRoles();
        if (!roles.length) {
          selectEl.innerHTML = `<option value="">${t("auth.noRolesAvailable")}</option>`;
          return;
        }
        selectEl.innerHTML = roles.map((r) => `<option value="${escapeHtml(r.id)}">${escapeHtml(r.name)}</option>`).join(""), selectEl.disabled = !1;
      } catch (e) {
        selectEl.innerHTML = `<option value="">${t("auth.couldNotLoadRoles")}</option>`, errEl && (errEl.textContent = e.message || t("auth.couldNotLoadRoles"));
      }
    }
  }
  function afterAuthOk(host, token, user) {
    if (host.saveAuth(token, user), host.root.innerHTML = "", host.afterLogin) {
      let cb = host.afterLogin;
      host.afterLogin = null, cb();
    } else
      host.init();
  }
  function renderLoginView(host, opts = {}) {
    let body = host.root.querySelector("#fbk-auth-body");
    if (!body) return;
    body.innerHTML = TPL.loginBody(!!opts.rejected);
    let emailEl = body.querySelector("#fbk-email"), passEl = body.querySelector("#fbk-password"), errEl = body.querySelector("#fbk-login-error"), submitBtn = body.querySelector("#fbk-login-submit"), doLogin = async () => {
      let email = emailEl.value.trim(), password = passEl.value;
      if (!email) {
        errEl.textContent = t("auth.pleaseEnterEmail");
        return;
      }
      if (!password) {
        errEl.textContent = t("auth.pleaseEnterPassword");
        return;
      }
      errEl.textContent = "", submitBtn.disabled = !0, submitBtn.textContent = t("auth.signingIn");
      let restore = () => {
        submitBtn.disabled = !1, submitBtn.textContent = t("auth.signIn");
      };
      try {
        let envelope = await (await host.apiLogin(email, password)).json(), data = envelope.data || null, status = data && data.status;
        if (status === "ok" && data.token) {
          afterAuthOk(host, data.token, data.user);
          return;
        }
        if (status === "pending") {
          errEl.textContent = envelope.message || t("auth.pendingApproval"), restore();
          return;
        }
        if (status === "disabled") {
          errEl.textContent = envelope.message || t("auth.accountDisabled"), restore();
          return;
        }
        if (status === "rejected") {
          renderLoginView(host, { rejected: !0 });
          let re = host.root.querySelector("#fbk-auth-body");
          re.querySelector("#fbk-email").value = email, re.querySelector("#fbk-password").value = password, re.querySelector("#fbk-login-error").textContent = envelope.message || t("auth.requestRejected");
          return;
        }
        errEl.textContent = envelope.message || t("auth.invalidCredentials"), restore();
      } catch {
        errEl.textContent = t("auth.networkError"), restore();
      }
    };
    if (submitBtn.addEventListener("click", doLogin), passEl.addEventListener("keydown", (e) => {
      e.key === "Enter" && doLogin();
    }), body.querySelector("#fbk-show-signup").addEventListener("click", () => renderSignupView(host)), opts.rejected) {
      let roleEl = body.querySelector("#fbk-reapply-role"), reBtn = body.querySelector("#fbk-reapply-submit");
      populateRoles(host, roleEl, errEl), reBtn.addEventListener("click", async () => {
        let email = emailEl.value.trim(), password = passEl.value, roleId = roleEl.value;
        if (!roleId) {
          errEl.textContent = t("auth.pleaseChooseRole");
          return;
        }
        if (!email || !password) {
          errEl.textContent = t("auth.enterEmailPasswordToRequestAgain");
          return;
        }
        errEl.textContent = "", reBtn.disabled = !0, reBtn.textContent = t("auth.submitting");
        try {
          let r = await host.apiRegister({ email, password, displayName: "", roleId }), envelope = await r.json();
          if (!r.ok || !envelope.isSuccess) {
            errEl.textContent = envelope.message || t("auth.couldNotSubmitRequest"), reBtn.disabled = !1, reBtn.textContent = t("auth.requestAgain");
            return;
          }
          renderLoginView(host);
          let reBody = host.root.querySelector("#fbk-auth-body");
          reBody.querySelector("#fbk-email").value = email, reBody.querySelector("#fbk-login-error").textContent = envelope.message || t("auth.requestSubmittedMsg");
        } catch {
          errEl.textContent = t("auth.networkError"), reBtn.disabled = !1, reBtn.textContent = t("auth.requestAgain");
        }
      });
    }
  }
  function renderSignupView(host) {
    let body = host.root.querySelector("#fbk-auth-body");
    if (!body) return;
    body.innerHTML = TPL.signupBody();
    let nameEl = body.querySelector("#fbk-su-name"), emailEl = body.querySelector("#fbk-su-email"), passEl = body.querySelector("#fbk-su-password"), roleEl = body.querySelector("#fbk-su-role"), errEl = body.querySelector("#fbk-signup-error"), okEl = body.querySelector("#fbk-signup-success"), submitBtn = body.querySelector("#fbk-signup-submit");
    populateRoles(host, roleEl, errEl), body.querySelector("#fbk-show-login").addEventListener("click", () => renderLoginView(host));
    let doSignup = async () => {
      let displayName = nameEl.value.trim(), email = emailEl.value.trim(), password = passEl.value, roleId = roleEl.value;
      if (errEl.textContent = "", okEl.textContent = "", !displayName) {
        errEl.textContent = t("auth.pleaseEnterName");
        return;
      }
      if (!email) {
        errEl.textContent = t("auth.pleaseEnterEmail");
        return;
      }
      if (!password) {
        errEl.textContent = t("auth.pleaseChoosePassword");
        return;
      }
      if (!roleId) {
        errEl.textContent = t("auth.pleaseChooseRole");
        return;
      }
      submitBtn.disabled = !0, submitBtn.textContent = t("auth.submitting");
      let restore = () => {
        submitBtn.disabled = !1, submitBtn.textContent = t("auth.createAccount");
      };
      try {
        let r = await host.apiRegister({ email, password, displayName, roleId }), envelope = await r.json();
        if (!r.ok || !envelope.isSuccess) {
          errEl.textContent = envelope.message || t("auth.couldNotCreateAccount"), restore();
          return;
        }
        okEl.textContent = envelope.message || t("auth.requestSubmittedMsg"), submitBtn.textContent = t("auth.requestSubmittedBtn"), submitBtn.disabled = !0, [nameEl, emailEl, passEl, roleEl].forEach((el) => {
          el.disabled = !0;
        });
      } catch {
        errEl.textContent = t("auth.networkError"), restore();
      }
    };
    submitBtn.addEventListener("click", doSignup), passEl.addEventListener("keydown", (e) => {
      e.key === "Enter" && doSignup();
    });
  }

  // src/element.ts
  var COMMIT_STYLE_CONTROL_ENABLED = !1, PIN_CLUSTER_RADIUS = 24, PIN_HALF_WIDTH = 16, PIN_HEIGHT = 30, PIN_TOOLTIP_HEIGHT_ESTIMATE = 150, PIN_TOOLTIP_WIDTH = 220, PIN_TOOLTIP_EDGE_MARGIN = 8, _PointerFeedback = class _PointerFeedback extends HTMLElement {
    constructor() {
      super(...arguments);
      this._mounted = !1;
      this.project = "";
      this.environmentAttr = "";
      this.sourceAttr = "data-component-source";
      this.screenshotEnabled = !0;
      this.launcherPosition = "bottom-end";
      this.server = "";
      this.environmentInt = 0;
      /** True when the page, the host config or a saved choice named an environment — the server's
       *  origin-resolved answer is then advisory and must not override it. */
      this.environmentExplicit = !1;
      /** True when the toolbar's environment select is on "All" — comments are fetched unfiltered
       *  (no `?environment=` query param) across every environment. Independent of environmentInt/
       *  environmentAttr, which keep tracking the actual (resolved or last-picked) environment so a
       *  NEW comment composed while viewing "All" still gets tagged with a real environment, not "all". */
      this.viewAllEnvironments = !1;
      this.comments = [];
      this.statusFilter = "all";
      this.mineOnly = !1;
      this.authorFilter = null;
      // Whether #fbk-filters (status/environment/author) is currently revealed — collapsed by
      // default so the filter bar isn't taking up space for visitors who never touch it; toggled by
      // the fbk-filters-toggle button (see renderChrome/TPL.chrome).
      this.filtersOpen = !1;
      this.hiddenPrivateCount = 0;
      this._collapsed = !0;
      this._disabled = !1;
      this.picking = !1;
      /** A magic-link token stripped from the URL, awaiting redemption in _boot(). */
      this._pendingInviteToken = null;
      this.sidebarOpen = !1;
      this.hovered = null;
      this.token = null;
      this.user = null;
      this.afterLogin = null;
      this.predefinedActions = [];
      // Whether switching is hard-locked off regardless of role (an explicit `fixed-environment="true"`
      // attribute, or host-injected config like the browser extension) — when true, the toolbar shows a
      // read-only label instead of a switcher, no matter what /capture-config's role check says. Plain
      // `environment="..."` alone no longer implies this — it only seeds the starting value now, so a
      // normal install (which always sets `environment` from *_POINTER_ENV) doesn't silently defeat the
      // role-gated switcher below.
      this.hasFixedEnvironment = !1;
      // Per-project, per-role: whether THIS logged-in caller may switch environments at all (vs a
      // read-only label). Defaults true (matches pre-existing behavior) until /capture-config
      // resolves post-login and possibly turns it off (e.g. for a Client/QuickAccess role by default,
      // or any role the project owner excluded). See ProjectService.ShowEnvironmentSelectorFor.
      this.showEnvironmentSelector = !0;
      // Project-level opt-in (default off), read once at init via /capture-config. Gates both whether
      // the widget buffers console/network events at all and whether "Report as a bug" is shown.
      this.pageContextCaptureEnabled = !1;
      this.commentFields = [];
      this.expandedCommentIds = /* @__PURE__ */ new Set();
      // Per-project text capture toggle (default true until /capture-config resolves).
      // When false, the widget emits no text content in the DOM snapshot and masks pageTitle.
      this.captureTextContent = !0;
      // Whether the AI apply flow (skill.md) bundles applied comments into one commit or commits each
      // one separately — 1=Single, 2=Separate (backend CommitStyle enum, read as-is like
      // environmentInt already is). Changeable via a small widget control, but only rendered when
      // canEditSettings is true (admin or the project's creator — same gate as the PATCH itself).
      this.commitStyle = 1;
      this.canEditSettings = !1;
      // The numeric project id (distinct from the `project` key attribute) — needed to PATCH
      // /api/admin/projects/{id} for the commit-style control; resolved once via /capture-config.
      this.projectId = null;
      // Display name resolved from /capture-config (falls back to the raw `project` key attribute
      // until it loads). Shown next to the environment indicator so a visitor can immediately tell
      // which project an install is actually bound to — project keys aren't unique across a workspace.
      this.projectName = "";
      // Per-user "add comment" keyboard shortcut, synced to the account (User.AddCommentShortcut,
      // not localStorage) — set from `this.user.addCommentShortcut` in loadAuth()/saveAuth() below.
      // Default is Ctrl+Alt+Shift+C / Control+Option+Shift+C — see shortcut.ts for why.
      this.shortcut = parseShortcut(void 0);
      // True when a host (the browser extension) injected a token: auth is entirely owned by that
      // host, re-applied on every reload (see connectedCallback), so the widget's own sign-out would
      // be immediately overwritten and must not be offered — switching accounts happens in the
      // extension popup instead.
      this.authOwnedByHost = !1;
      this._pendingShotPromise = null;
      // The comment id whose pin should show the attention ripple on its NEXT renderPins() — cleared
      // shortly after so re-renders (env switch, poll, etc.) don't replay it forever.
      this._newPinId = null;
      // The toolbar's drag offset from its default bottom-right anchor (see enableToolbarDrag).
      this._toolbarDx = 0;
      this._toolbarDy = 0;
      this.unreadNotifyCount = 0;
      this._notifyPollTimer = null;
      this._updatesMenuClose = null;
      this._onVisibilityChange = null;
      this._userMenuClose = null;
      this._clusterMenuClose = null;
      this._cardMenuClose = null;
      this._recordingShortcut = !1;
      this._shortcutRecordingCleanup = null;
      this._backdropObserver = null;
      this._backdropRaf = 0;
      this._pinsRetryTimers = [];
      this._lastUrl = typeof window != "undefined" ? window.location.href : "";
      this._urlPollTimer = null;
      this._onLocChange = null;
      this._stylesPromise = null;
    }
    connectedCallback() {
      var _a2, _b;
      try {
        performance.mark("pf:boot:start");
      } catch {
      }
      if (this._mounted) return;
      this._mounted = !0, this.project = this.getAttribute("project") || "", this.environmentAttr = this.getAttribute("environment") || "", this.hasFixedEnvironment = (this.getAttribute("fixed-environment") || "").toLowerCase() === "true", this.sourceAttr = this.getAttribute("source-attr") || "data-component-source", this.screenshotEnabled = (this.getAttribute("screenshot") || "").toLowerCase() !== "false";
      let pos = (this.getAttribute("launcher-position") || "").toLowerCase();
      this.launcherPosition = POSITIONS.includes(pos) ? pos : "bottom-end", this.server = (this.getAttribute("server") || (SCRIPT_SRC ? new URL(SCRIPT_SRC).origin : window.location.origin)).replace(/\/$/, ""), this.environmentExplicit = !!this.environmentAttr, this.environmentInt = this.environmentAttr ? (_a2 = ENV_MAP[this.environmentAttr.toLowerCase()]) != null ? _a2 : 2 : 0;
      let injected = typeof window != "undefined" ? window.__POINTER_CONFIG__ : void 0;
      if (injected && (injected.server && (this.server = injected.server.replace(/\/$/, "")), injected.project && (this.project = injected.project), injected.environment && (this.environmentAttr = injected.environment, this.environmentInt = ENV_MAP[injected.environment.toLowerCase()] || this.environmentInt, this.environmentExplicit = !0), injected.fixedEnvironment === !0 && (this.hasFixedEnvironment = !0)), !this.hasFixedEnvironment)
        try {
          let savedEnv = (localStorage.getItem("pointer_env_" + this.project) || "").toLowerCase();
          savedEnv === "all" ? this.viewAllEnvironments = !0 : savedEnv && ENV_MAP[savedEnv] && (this.environmentAttr = savedEnv, this.environmentInt = ENV_MAP[savedEnv], this.environmentExplicit = !0);
        } catch {
        }
      if (this.environmentAttr || (this.environmentAttr = ENV_NAME[this.environmentInt] || "unknown"), this._collapsed = (() => {
        try {
          return sessionStorage.getItem("pointer_visible") !== "1";
        } catch {
          return !0;
        }
      })(), this._pendingInviteToken = this.stripInviteTokenFromUrl(), this.loadAuth(), injected != null && injected.token && (this.token = injected.token, injected.user !== void 0 && (this.user = injected.user), this.shortcut = parseShortcut((_b = this.user) == null ? void 0 : _b.addCommentShortcut), this.authOwnedByHost = !0), this.applyTheme(), setLang(this.resolveLang()), this.applyDir(), this.style.position = "fixed", this.style.zIndex = "2147483647", this.style.top = "0", this.style.left = "0", this.style.pointerEvents = "none", this.attachShadow({ mode: "open" }), this._styleLink = document.createElement("link"), this._styleLink.rel = "stylesheet", CSS_INTEGRITY && (this._styleLink.integrity = CSS_INTEGRITY, this._styleLink.crossOrigin = "anonymous"), this._styleLink.href = (injected == null ? void 0 : injected.cssUrl) || CSS_URL || `${this.server}/widget.css`, this.shadowRoot.appendChild(this._styleLink), this.root = document.createElement("div"), this.shadowRoot.appendChild(this.root), this._stylesPromise = this._stylesReady(), ensureHighlightStyle(), !this.project) {
        console.error("[pointer-feedback] Missing required `project` attribute. Component disabled.");
        return;
      }
      this._onHover = this.onHover.bind(this), this._onPick = this.onPick.bind(this), this._onPickKey = this.onPickKey.bind(this), this._onShortcutKeydown = this.onShortcutKeydown.bind(this), this._reposition = () => {
        this.renderPins(), this.updateMenuSide();
      }, window.addEventListener("scroll", this._reposition, !0), window.addEventListener("resize", this._reposition), this._onLocChange = () => this.handleLocationChange(), ["popstate", "hashchange", "pointer:locationchange"].forEach((ev) => window.addEventListener(ev, this._onLocChange)), typeof window != "undefined" && !window.__pf_hist__ && (window.__pf_hist__ = !0, ["pushState", "replaceState"].forEach((fn) => {
        let orig = history[fn];
        history[fn] = function(...args) {
          let res = orig.apply(this, args);
          return window.dispatchEvent(new Event("pointer:locationchange")), res;
        };
      })), this._scheduleBackdropUpdate = () => {
        this._backdropRaf || (this._backdropRaf = requestAnimationFrame(() => {
          this._backdropRaf = 0, this.punchBackdropHoles();
        }));
      }, window.addEventListener("resize", this._scheduleBackdropUpdate), window.addEventListener("scroll", this._scheduleBackdropUpdate, !0), this.root.addEventListener("transitionend", this._scheduleBackdropUpdate), this._backdropObserver = new MutationObserver((mutations) => {
        let isOwnMutation = (m) => m.target instanceof Element && m.target.matches(`${BACKDROP_SELECTOR}, ${DIALOG_CONTENT_SELECTOR}`);
        mutations.some((m) => !isOwnMutation(m)) && this._scheduleBackdropUpdate();
      }), this._backdropObserver.observe(document.body, { childList: !0, subtree: !0, attributes: !0, attributeFilter: ["style", "class"] }), this._backdropObserver.observe(this.root, { childList: !0, subtree: !0, attributes: !0, attributeFilter: ["style", "class"] }), this._scheduleBackdropUpdate(), document.addEventListener("keydown", this._onShortcutKeydown), this._boot();
    }
    // Wait for the stylesheet to load, then render the first view (avoids a flash
    // of unstyled UI). A short timeout guarantees we never hang on slow CSS.
    async _boot() {
      if (!await this._checkWidgetActive()) return;
      await Promise.all([this._stylesReady(), loadBranding(this.server)]);
      let inviteFailed = !1;
      this._pendingInviteToken && (inviteFailed = !await this.redeemInviteToken(this._pendingInviteToken), this._pendingInviteToken = null), this.token && await this.hydrateIdentity(), setLang(this.resolveLang()), this.applyDir(), this.token ? this.init() : this.renderChrome();
      try {
        performance.mark("pf:boot:end");
      } catch {
      }
      this.token && this._reportBuildSha(), this.token && this._reportWidgetLanguage(), inviteFailed && this.toast(t("toast.inviteLinkInvalid"), "error");
    }
    /**
     * Reports `<html data-build-sha>` once per page load.
     *
     * Fire-and-forget and failure-silent by design: this is a nicety on top of the feedback loop,
     * and a visitor must never see an error — or a delayed widget — because a build beacon did not
     * land. Once per load because every additional call is a no-op the server still has to scan for.
     *
     * The CLI's `pointer status --deployed` is the primary path. This one only helps where the
     * widget actually ships to production, which the install guide advises against.
     */
    async _reportBuildSha() {
      var _a2, _b;
      if (!_PointerFeedback._buildShaReported)
        try {
          let sha = (_b = (_a2 = document.documentElement) == null ? void 0 : _a2.dataset) == null ? void 0 : _b.buildSha;
          if (!sha || !/^[0-9a-f]{7,40}$/.test(sha)) return;
          _PointerFeedback._buildShaReported = !0, await pfFetch(`${this.server}/api/projects/${encodeURIComponent(this.project)}/builds`, {
            method: "POST",
            headers: { "Content-Type": "application/json", Authorization: `Bearer ${this.token}` },
            body: JSON.stringify({ sha })
          });
        } catch {
        }
    }
    /**
     * Reports the widget's own UI language, the host page's `<html lang>`, and the visiting
     * browser's locale — once per tab session (sessionStorage guard `pointer_lang_reported`), so a
     * future widget-translation priority list can be based on real usage instead of guesswork. Fire-
     * and-forget and failure-silent, same as `_reportBuildSha`: a visitor must never see (or wait on)
     * a usage beacon. Posted through the existing authenticated `POST /api/events` pipeline, so an
     * anonymous visitor (no token) simply never reports — acceptable, see the plan.
     */
    async _reportWidgetLanguage() {
      var _a2;
      try {
        if (sessionStorage.getItem("pointer_lang_reported") === "1") return;
        sessionStorage.setItem("pointer_lang_reported", "1");
      } catch {
      }
      try {
        await pfFetch(`${this.server}/api/events`, {
          method: "POST",
          headers: { "Content-Type": "application/json", Authorization: `Bearer ${this.token}` },
          body: JSON.stringify({
            type: "widget_language",
            source: "web-component",
            projectKey: this.project,
            meta: {
              ui: this.resolveLang(),
              browser: typeof navigator != "undefined" ? navigator.language : null,
              page: typeof document != "undefined" && ((_a2 = document.documentElement) == null ? void 0 : _a2.lang) || null
            }
          })
        });
      } catch {
      }
    }
    // Anonymous, pre-auth: asks the server whether this project should render on this page's
    // origin at all (gates on the project's overall activation AND, if this origin matches a
    // configured "other environment" URL, that specific mapping's own active flag). A network
    // failure or non-OK response is treated as "keep hidden," not "fail open" — an outage in
    // this check must not accidentally show the widget where it was explicitly deactivated.
    async _checkWidgetActive() {
      var _a2;
      try {
        let origin = typeof window != "undefined" ? window.location.origin : "", url = `${this.server}/api/public/projects/${encodeURIComponent(this.project)}/widget-status?origin=${encodeURIComponent(origin)}`, res = await pfFetch(url);
        if (!res.ok) return !1;
        let body = await res.json(), data = (_a2 = body == null ? void 0 : body.data) != null ? _a2 : body;
        return (data == null ? void 0 : data.active) === !0;
      } catch {
        return !1;
      }
    }
    // An admin disabled this project: tear the widget down silently — no toolbar,
    // no launcher, no toast/console error. The only trace is the 409 already visible
    // in the browser's network tab. Detected from the comments endpoint's
    // 409 "project disabled" response.
    disableSilently() {
      if (!this._disabled) {
        this._disabled = !0;
        try {
          this.stopPicking();
        } catch {
        }
        this.comments = [], this.root && (this.root.innerHTML = "");
      }
    }
    _stylesReady() {
      return this._stylesPromise ? this._stylesPromise : (this._stylesPromise = new Promise((resolve) => {
        let link = this._styleLink;
        if (!link || link.sheet) return resolve();
        let done = !1, finish = () => {
          done || (done = !0, resolve());
        };
        link.addEventListener("load", finish, { once: !0 }), link.addEventListener("error", async () => {
          try {
            let fetchOpts = { mode: "cors" };
            CSS_INTEGRITY && (fetchOpts.integrity = CSS_INTEGRITY);
            let cssUrl = link.href || CSS_URL, res = await rawFetch(cssUrl, fetchOpts);
            if (res.ok) {
              let text = await res.text();
              if (typeof CSSStyleSheet != "undefined") {
                let sheet = new CSSStyleSheet();
                typeof sheet.replace == "function" ? await sheet.replace(text) : typeof sheet.replaceSync == "function" && sheet.replaceSync(text), this.shadowRoot && (this.shadowRoot.adoptedStyleSheets = [sheet]);
              }
            }
          } catch {
          } finally {
            finish();
          }
        }, { once: !0 }), setTimeout(finish, 1500);
      }), this._stylesPromise);
    }
    disconnectedCallback() {
      var _a2, _b;
      window.removeEventListener("scroll", this._reposition, !0), window.removeEventListener("resize", this._reposition), window.removeEventListener("resize", this._scheduleBackdropUpdate), window.removeEventListener("scroll", this._scheduleBackdropUpdate, !0), (_a2 = this.root) == null || _a2.removeEventListener("transitionend", this._scheduleBackdropUpdate), (_b = this._backdropObserver) == null || _b.disconnect(), this._backdropRaf && cancelAnimationFrame(this._backdropRaf), document.removeEventListener("keydown", this._onShortcutKeydown), this._shortcutRecordingCleanup && this._shortcutRecordingCleanup(), this.stopPicking(), this.stopNotificationPolling(), this.closeUpdatesMenu(), stopPageContextCapture(), this.clearPinsRetries(), this._onLocChange && (["popstate", "hashchange", "pointer:locationchange"].forEach((ev) => window.removeEventListener(ev, this._onLocChange)), this._onLocChange = null), this._urlPollTimer && (clearInterval(this._urlPollTimer), this._urlPollTimer = null), this._mounted = !1;
    }
    // --- "Add comment" keyboard shortcut --------------------------------------
    isEditableTarget(e) {
      let target = e.composedPath()[0];
      if (!target || !target.tagName) return !1;
      let tag = target.tagName.toLowerCase();
      return tag === "input" || tag === "textarea" || tag === "select" || !!target.isContentEditable;
    }
    onShortcutKeydown(e) {
      this._recordingShortcut || this._disabled || this.isEditableTarget(e) || matchesShortcut(e, this.shortcut) && (e.preventDefault(), this.activateAddComment());
    }
    // Shared by both the toolbar's "add" button and the keyboard shortcut — expands the widget
    // first if it's collapsed (the toolbar buttons don't exist in the DOM until then), then either
    // prompts login or toggles element-picking, exactly like clicking #fbk-add.
    activateAddComment() {
      if (this._collapsed && this.showOverlay(), !this.token) {
        showLoginModal(this, () => {
          Promise.resolve(this.init()).then(() => this.togglePicking());
        });
        return;
      }
      this.togglePicking();
    }
    // Enters "recording" mode on the user-menu shortcut button: the next non-modifier keydown
    // (with at least one modifier held) becomes the new binding. Escape cancels.
    beginRecordingShortcut(btnEl) {
      this._recordingShortcut = !0;
      let original = btnEl.textContent || "";
      btnEl.textContent = t("menu.pressKeysToCancel");
      let onKey = (e) => {
        if (e.preventDefault(), e.stopPropagation(), e.key === "Escape") {
          btnEl.textContent = original, cleanup();
          return;
        }
        if (e.key === "Shift" || e.key === "Alt" || e.key === "Control" || e.key === "Meta") return;
        if (!(e.altKey || e.ctrlKey || e.metaKey || e.shiftKey)) {
          btnEl.textContent = t("menu.addModifierKey");
          return;
        }
        let binding = {
          code: e.code,
          alt: e.altKey,
          shift: e.shiftKey,
          ctrl: e.ctrlKey,
          meta: e.metaKey
        };
        btnEl.textContent = t("menu.saving"), cleanup(), this.saveShortcutPreference(binding).then((ok) => {
          btnEl.textContent = formatShortcut(this.shortcut), this.toast(ok ? t("menu.shortcutUpdated") : t("menu.failedToSaveTryAgain"), ok ? "" : "error");
        });
      }, cleanup = () => {
        this._recordingShortcut = !1, this._shortcutRecordingCleanup = null, document.removeEventListener("keydown", onKey, !0);
      };
      this._shortcutRecordingCleanup = cleanup, document.addEventListener("keydown", onKey, !0);
    }
    // Persists a new binding to the account (PATCH /api/me/preferences) so it follows the user
    // across browsers/machines — an empty string resets to the widget's built-in default. Updates
    // the cached `pointer_user` mirror on success so a page reload reflects it instantly, without
    // waiting for the next fresh login.
    async saveShortcutPreference(binding) {
      try {
        return (await this.api("/api/me/preferences", {
          method: "PATCH",
          body: JSON.stringify({ addCommentShortcut: binding ? serializeShortcut(binding) : "" })
        })).ok ? (this.shortcut = binding || parseShortcut(void 0), this.user && (this.user = { ...this.user, addCommentShortcut: binding ? serializeShortcut(binding) : void 0 }, localStorage.setItem("pointer_user", JSON.stringify(this.user))), this.updateAddButtonTooltip(), !0) : !1;
      } catch {
        return !1;
      }
    }
    // --- Auth helpers --------------------------------------------------------
    loadAuth() {
      var _a2;
      try {
        this.token = typeof localStorage != "undefined" && localStorage.getItem("pointer_token") || null;
        let raw = typeof localStorage != "undefined" ? localStorage.getItem("pointer_user") : null;
        this.user = raw ? JSON.parse(raw) : null;
      } catch {
        this.token = null, this.user = null;
      }
      this.shortcut = parseShortcut((_a2 = this.user) == null ? void 0 : _a2.addCommentShortcut);
    }
    saveAuth(token, user) {
      this.token = token, this.user = user, this.shortcut = parseShortcut(user == null ? void 0 : user.addCommentShortcut), setLang(this.resolveLang()) && this.applyDir(), localStorage.setItem("pointer_token", token), localStorage.setItem("pointer_user", JSON.stringify(user)), this.startNotificationPolling();
    }
    clearAuth() {
      this.token = null, this.user = null, this.unreadNotifyCount = 0, this.stopNotificationPolling(), this.closeUpdatesMenu(), localStorage.removeItem("pointer_token"), localStorage.removeItem("pointer_user");
    }
    handle401() {
      this.clearAuth(), showLoginModal(this);
    }
    // --- Theme ------------------------------------------------------------
    // Deliberately WIDGET-LOCAL, not account-wide: `User.theme`/`/api/me/preferences` is the same
    // field the dashboard's own theme toggle reads to paint the entire admin app, so persisting the
    // widget's choice there would silently flip the dashboard's site-wide theme too. An explicit
    // per-browser override (localStorage, set from the user menu below) wins; otherwise the host
    // page's own rendered theme; otherwise the OS preference. See theme.ts.
    resolveTheme() {
      try {
        let stored = localStorage.getItem("pointer_widget_theme");
        if (stored === "light" || stored === "dark") return stored;
      } catch {
      }
      return detectSiteTheme();
    }
    // Reflects the resolved mode onto the host element (light DOM, not shadowRoot) as
    // `data-fbk-theme` — _theme.scss's `:host([data-fbk-theme="dark"])` block reads it to swap the
    // shadow UI's token values, and a consuming app can target the same attribute from its own CSS
    // to override any single token per project, exactly like the light defaults.
    applyTheme() {
      this.setAttribute("data-fbk-theme", this.resolveTheme());
    }
    // Sets the widget's own per-browser theme override — never touches the account (see the note
    // on resolveTheme above), so it can never bleed into the host dashboard's own theme.
    setThemeOverride(mode) {
      try {
        localStorage.setItem("pointer_widget_theme", mode);
      } catch {
      }
      this.applyTheme();
    }
    // --- Language ---------------------------------------------------------
    // Same widget-local shape as theme above, for the same reason: `User.language` is the SAME
    // field the dashboard's own language switcher writes to paint the whole admin app, so an
    // explicit per-browser override (localStorage, set from the user menu below) must win without
    // ever being written back to the account — otherwise picking a language in the widget would
    // silently flip the dashboard's language too. Falling back to the account's EXISTING value
    // (read-only) when there is no local override yet is fine — the widget just never sets it.
    resolveLang() {
      var _a2;
      try {
        let stored = localStorage.getItem("pointer_widget_language");
        if (stored === "ar" || stored === "en") return stored;
      } catch {
      }
      let accountLang = (_a2 = this.user) == null ? void 0 : _a2.language;
      if (accountLang === "ar") return "ar";
      if (accountLang === "en") return "en";
      try {
        return (navigator.language || "").toLowerCase().startsWith("ar") ? "ar" : "en";
      } catch {
        return "en";
      }
    }
    // Sets the widget's own per-browser language override — never touches the account (see the
    // note on resolveLang above). The caller (wireLangBtn in toggleUserMenu) still needs to
    // re-render everything language-bearing afterward — unlike theme, a language change can't be
    // reflected by a CSS attribute flip alone, since the text itself is baked into rendered markup.
    setLanguageOverride(lang) {
      try {
        localStorage.setItem("pointer_widget_language", lang);
      } catch {
      }
      setLang(lang), this.applyDir();
    }
    // Reflects the resolved language's writing direction onto the host element (light DOM), same
    // mechanism as applyTheme() above — _base.scss's `:host([dir="rtl"])` block flips the shadow
    // UI's own layout direction (popover/sidebar/toolbar/toasts/composer) to follow the WIDGET's
    // language, independent of the host page's own dir (which only ever drives the collapsed
    // launcher's corner — see pageIsRtl() in dom.ts). Plain `dir`, unlike this class's other
    // internal attributes, is deliberate: it is the standard HTML attribute assistive tech already
    // understands, not a widget-private hook.
    applyDir() {
      this.setAttribute("dir", getLang() === "ar" ? "rtl" : "ltr");
    }
    async init() {
      await loadStatusCatalog(this.server), this.isConnected && (this.renderChrome(), await Promise.all([this.fetchComments(), this.fetchPredefinedActions(), this.fetchCaptureConfig()]), this.isConnected && (this.token && this.startNotificationPolling(), this.renderSidebar(), this.renderPins(), this.schedulePinsRetries(), this._urlPollTimer || (this._urlPollTimer = window.setInterval(() => {
        var _a2;
        if (!this.isConnected) {
          this._urlPollTimer && (clearInterval(this._urlPollTimer), this._urlPollTimer = null);
          return;
        }
        if (window.location.href !== this._lastUrl)
          this.handleLocationChange();
        else {
          let wrap = (_a2 = this.root) == null ? void 0 : _a2.querySelector("#fbk-pins-layer");
          wrap && wrap.children.length === 0 && this.comments.some((c) => isCurrentPage(c) && c.status !== "archived" && c.status !== "applied") && this.renderPins();
        }
      }, 2500)), this._collapsed && this.renderChrome()));
    }
    // Fetch the project's predefined-action options for the comment popover picker.
    // Silently no-ops on failure — the picker simply won't appear.
    async fetchPredefinedActions() {
      try {
        let r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/predefined-actions`);
        if (!r.ok) {
          this.predefinedActions = [];
          return;
        }
        let envelope = await r.json();
        this.predefinedActions = envelope && envelope.data || [];
      } catch {
        this.predefinedActions = [];
      }
    }
    // Read the project's page-context capture toggle and, if on, start buffering
    // console/network events. Silently no-ops on failure (feature stays off).
    async fetchCaptureConfig() {
      var _a2, _b, _c, _d, _e, _f, _g;
      try {
        let r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/capture-config`);
        if (!r.ok) {
          this.pageContextCaptureEnabled = !1;
          return;
        }
        let envelope = await r.json();
        this.commentFields = (_b = (_a2 = envelope == null ? void 0 : envelope.data) == null ? void 0 : _a2.commentFields) != null ? _b : [], this.pageContextCaptureEnabled = !!(envelope && envelope.data && envelope.data.pageContextCaptureEnabled), envelope != null && envelope.data && typeof envelope.data.captureTextContent == "boolean" && (this.captureTextContent = envelope.data.captureTextContent), this.projectName = envelope && envelope.data && envelope.data.name || this.project;
        let showSelector = (_c = envelope == null ? void 0 : envelope.data) == null ? void 0 : _c.showEnvironmentSelector;
        this.showEnvironmentSelector = showSelector !== !1, this.projectId = typeof ((_d = envelope == null ? void 0 : envelope.data) == null ? void 0 : _d.id) == "number" ? envelope.data.id : null;
        let resolved = (_e = envelope == null ? void 0 : envelope.data) == null ? void 0 : _e.resolvedEnvironment;
        !this.environmentExplicit && typeof resolved == "number" && ENV_NAME[resolved] && resolved !== this.environmentInt && (this.environmentInt = resolved, this.environmentAttr = ENV_NAME[resolved], this.viewAllEnvironments || await this.fetchComments()), this.commitStyle = typeof ((_f = envelope == null ? void 0 : envelope.data) == null ? void 0 : _f.commitStyle) == "number" ? envelope.data.commitStyle : 1, this.canEditSettings = !!((_g = envelope == null ? void 0 : envelope.data) != null && _g.canEditSettings), this.updateCommentsHeading(), this.renderSidebar(), this.renderCommitStyleControl(), this.pageContextCaptureEnabled && startPageContextCapture(this.server, SCRIPT_SRC);
      } catch {
        this.pageContextCaptureEnabled = !1, this.captureTextContent = !0;
      }
    }
    // Patches the already-rendered "{project}" heading in place rather than a full
    // renderChrome() — re-rendering chrome here would drop the sidebar's open/closed state
    // mid-session. Needed because the initial renderChrome() runs before fetchCaptureConfig()
    // resolves the real project name, so the heading starts out showing the raw project key as a
    // fallback. The project name is shown only in this heading — not duplicated elsewhere in the
    // header, so there's nothing else to keep in step with it.
    updateCommentsHeading() {
      let heading = this.root && this.root.querySelector("#fbk-comments-heading");
      heading && (heading.textContent = t("toolbar.projectHeading", { project: this.projectName }));
    }
    // Translated display text for an environment key ('local'/'staging'/'production') — falls back
    // to the raw key for anything else (e.g. 'unknown', before the server has resolved one).
    envDisplayLabel(key) {
      let k = (key || "").toLowerCase();
      return k === "local" ? t("toolbar.envLocal") : k === "staging" ? t("toolbar.envStaging") : k === "production" ? t("toolbar.envProduction") : key;
    }
    // Patches #fbk-commit-style in place (same reasoning as updateEnvironmentSelectorVisibility) —
    // hidden entirely unless the current caller is authorized to change it (canEditSettings), so a
    // stakeholder who couldn't save the PATCH never sees a control that would just 403.
    // TEMPORARILY forced off entirely, per explicit request — flip COMMIT_STYLE_CONTROL_ENABLED
    // back to true to restore that behavior.
    renderCommitStyleControl() {
      let host = this.root && this.root.querySelector("#fbk-commit-style");
      if (!host) return;
      if (!COMMIT_STYLE_CONTROL_ENABLED || !this.canEditSettings) {
        host.classList.add("fbk-hidden");
        return;
      }
      host.classList.remove("fbk-hidden"), host.innerHTML = TPL.commitStyleControl(this.commitStyle);
      let sel = this.root.querySelector("#fbk-commit-style-select");
      sel && sel.addEventListener("change", () => this.setCommitStyle(Number(sel.value)));
    }
    async setCommitStyle(value) {
      if (this.projectId == null || value !== 1 && value !== 2) return;
      let previous = this.commitStyle;
      this.commitStyle = value;
      try {
        let r = await this.api(`/api/admin/projects/${this.projectId}`, {
          method: "PATCH",
          body: JSON.stringify({ commitStyle: value })
        });
        if (!r.ok) throw new Error("HTTP " + r.status);
        this.toast(t("toast.commitStyleUpdated"));
      } catch (e) {
        this.commitStyle = previous, this.renderCommitStyleControl(), e.message !== "HTTP 401 Unauthorized" && this.toast(t("toast.updateFailed"), "error");
      }
    }
    // Keeps the "Comment on an element" button's tooltip showing the current shortcut after it's
    // changed from the user menu — same in-place-patch reasoning as updateCommentsHeading().
    updateAddButtonTooltip() {
      let btn = this.root && this.root.querySelector("#fbk-add");
      if (!btn) return;
      let label = formatShortcut(this.shortcut);
      btn.setAttribute("title", `${t("toolbar.commentOnElement")} (${label})`), btn.setAttribute("aria-label", t("toolbar.commentOnElementShortcut", { label }));
    }
    // --- API ----------------------------------------------------------------
    async apiLogin(email, password) {
      return pfFetch(`${this.server}/api/auth/login`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ email, password })
      });
    }
    // Anonymous: active non-admin roles for the signup / re-apply dropdowns.
    async apiRoles() {
      let r = await pfFetch(`${this.server}/api/roles?project=${encodeURIComponent(this.project)}`, {
        headers: { "Content-Type": "application/json" }
      }), envelope = await r.json();
      if (!r.ok || !envelope.isSuccess) throw new Error(envelope.message || t("auth.couldNotLoadRoles"));
      return envelope.data || [];
    }
    // Anonymous: self-signup AND re-apply (one endpoint). No token returned.
    async apiRegister(body) {
      return pfFetch(`${this.server}/api/auth/register`, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body)
      });
    }
    api(path, opts = {}) {
      let headers = {
        "Content-Type": "application/json",
        // Declares which kind of client this is, so the server knows it may include the advisory
        // payload flags (R2-06). Not an auth signal — it is trivially forgeable and nothing
        // security-critical depends on it. Its job is that the documented AI paths, which never send
        // it, never receive the flag.
        "X-Pointer-Client": "widget",
        ...this.token ? { Authorization: `Bearer ${this.token}` } : {},
        ...opts.headers || {}
      };
      return pfFetch(`${this.server}${path}`, { ...opts, headers }).then((r) => {
        if (r.status === 401)
          throw this.handle401(), new Error("HTTP 401 Unauthorized");
        return r;
      });
    }
    /**
     * Removes `?pointer_invite=` from the address bar and returns the token it held.
     *
     * SYNCHRONOUS AND FIRST, on every path including failure. Every comment captures
     * `window.location.href` and the route into its element capture, so a token still in the URL when
     * someone comments is persisted into the database and handed to anyone who can read that comment.
     * Stripping before any await — and before the redemption can fail — is what makes that
     * impossible. Other query params and the hash are preserved.
     */
    stripInviteTokenFromUrl() {
      try {
        let url = new URL(window.location.href), token = url.searchParams.get("pointer_invite");
        return token ? (url.searchParams.delete("pointer_invite"), window.history.replaceState({}, "", url.toString()), token) : null;
      } catch {
        return null;
      }
    }
    /**
     * Exchanges a stripped magic-link token for a normal session.
     *
     * Awaited in _boot() before the `if (this.token)` branch, because a first-time client has nothing
     * in storage — init() never runs for them, so the exchange has to complete before that decision.
     */
    async redeemInviteToken(token) {
      var _a2, _b;
      try {
        let controller = new AbortController(), timer = setTimeout(() => controller.abort(), 3e3), res = await pfFetch(`${this.server}/api/auth/login-with-invite`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ token }),
          signal: controller.signal
        }).finally(() => clearTimeout(timer)), envelope = await res.json(), data = (_a2 = envelope == null ? void 0 : envelope.data) != null ? _a2 : envelope;
        if (res.ok && (data == null ? void 0 : data.status) === "ok" && data.token)
          return this.saveAuth(data.token, (_b = data.user) != null ? _b : null), !0;
      } catch {
      }
      return !1;
    }
    async fetchComments() {
      try {
        let envQuery = this.viewAllEnvironments ? "" : `?environment=${this.environmentInt}`, r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments${envQuery}`);
        if (r.status === 409 || r.status === 404) {
          this.disableSilently();
          return;
        }
        if (!r.ok) throw new Error("HTTP " + r.status);
        let envelope = await r.json(), items = envelope.data && envelope.data.items || [];
        this.hiddenPrivateCount = envelope.data && Number(envelope.data.hiddenPrivateCount) || 0, this.comments = items.map((c) => ({
          ...c,
          status: STATUS_STR[c.status] || "open"
        }));
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(t("toast.couldNotReachServer", { brand: getBrandName() }), "error", t("toast.retry"), () => {
          this.fetchComments().then(() => {
            this.renderSidebar(), this.renderPins();
          });
        }), this.comments = [], this.hiddenPrivateCount = 0;
      }
    }
    // All comments returned belong to the project (no page_url filtering).
    pageComments() {
      return this.comments;
    }
    // --- Chrome (toolbar + sidebar shell) -----------------------------------
    renderChrome() {
      if (this._disabled) return;
      if (this._collapsed) {
        let n = (this.comments || []).filter((c) => c.status !== "archived" && c.status !== "applied").length;
        this.root.innerHTML = TPL.launcher(n, this.launcherPosition, pageIsRtl(), this.unreadNotifyCount);
        let launcher = this.root.querySelector("#fbk-launcher");
        launcher && launcher.addEventListener("click", () => this.showOverlay());
        return;
      }
      let displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : "", roleLabel = this.user ? escapeHtml(this.user.roleName || "") : "", avatarInitials = this.user ? escapeHtml(initials(this.user.displayName || this.user.email || "")) : "";
      this.root.innerHTML = TPL.chrome(displayName, roleLabel, this.projectName || this.project, formatShortcut(this.shortcut), this.unreadNotifyCount, avatarInitials, ariaKeyshortcuts(this.shortcut), this.filtersOpen);
      let hideBtn = this.root.querySelector("#fbk-hide");
      hideBtn && hideBtn.addEventListener("click", () => this.hideOverlay());
      let userBtn = this.root.querySelector("#fbk-user");
      userBtn && userBtn.addEventListener("click", (e) => {
        e.stopPropagation(), this.toggleUserMenu();
      });
      let updatesBtn = this.root.querySelector("#fbk-updates");
      updatesBtn && updatesBtn.addEventListener("click", (e) => {
        e.stopPropagation(), this.toggleUpdatesMenu();
      }), this.root.querySelector("#fbk-add").addEventListener("click", () => this.activateAddComment()), this.root.querySelector("#fbk-toggle").addEventListener("click", () => {
        if (!this.token) {
          showLoginModal(this, () => {
            Promise.resolve(this.init()).then(() => this.toggleSidebar(!0));
          });
          return;
        }
        this.toggleSidebar();
      }), this.root.querySelector("#fbk-refresh").addEventListener("click", async () => {
        if (!this.token) {
          showLoginModal(this, () => this.init());
          return;
        }
        await this.fetchComments(), this.renderSidebar(), this.renderPins(), this.toast(t("toast.refreshed"));
      });
      let filtersToggle = this.root.querySelector("#fbk-filters-toggle");
      filtersToggle && filtersToggle.addEventListener("click", () => {
        this.filtersOpen = !this.filtersOpen, this.root.querySelector("#fbk-filters").classList.toggle("fbk-hidden", !this.filtersOpen), filtersToggle.classList.toggle("is-active", this.filtersOpen), filtersToggle.setAttribute("aria-pressed", String(this.filtersOpen)), filtersToggle.setAttribute("aria-expanded", String(this.filtersOpen));
        let label = this.filtersOpen ? t("sidebar.hideFilters") : t("sidebar.showFilters");
        filtersToggle.setAttribute("title", label), filtersToggle.setAttribute("aria-label", label);
      }), this.root.querySelector("#fbk-close").addEventListener("click", () => this.toggleSidebar(!1));
      let resetBtn = this.root.querySelector("#fbk-reset-pos");
      resetBtn && resetBtn.addEventListener("click", () => this.resetToolbarPos()), this.restoreToolbarPos(), this.enableToolbarDrag(), this.updateMenuSide();
    }
    // Switch the active environment from the toolbar. Comments are environment-scoped, so this
    // re-queries the server and re-renders; the choice is remembered per project on this origin.
    // "all" is a widget-only filter state (see viewAllEnvironments' field doc) — it doesn't touch
    // environmentAttr/environmentInt, which keep tracking the real environment for tagging new
    // comments composed while every environment is shown.
    setEnvironment(env) {
      let key = (env || "").toLowerCase();
      if (key === "all") {
        if (this.viewAllEnvironments) return;
        this.viewAllEnvironments = !0;
        try {
          localStorage.setItem("pointer_env_" + this.project, "all");
        } catch {
        }
        if (!this.token) return;
        this.fetchComments().then(() => {
          this.renderSidebar(), this.renderPins();
        });
        return;
      }
      if (!(!ENV_MAP[key] || !this.viewAllEnvironments && key === this.environmentAttr.toLowerCase())) {
        this.viewAllEnvironments = !1, this.environmentAttr = key, this.environmentInt = ENV_MAP[key];
        try {
          localStorage.setItem("pointer_env_" + this.project, key);
        } catch {
        }
        this.token && this.fetchComments().then(() => {
          this.renderSidebar(), this.renderPins();
        });
      }
    }
    // --- Draggable toolbar ---------------------------------------------------
    // The toolbar's default corner is bottom-right (CSS inset-block-end/inset-inline-end); let the
    // user drag it by its grip so it never covers the element they want to comment on. Dragging
    // sets a `translate(dx, dy)` offset (--fbk-toolbar-dx/dy) rather than switching the anchor
    // to absolute left/top, so the anchor itself never changes — only the offset from it does.
    // Position persists per tab.
    restoreToolbarPos() {
      let tb = this.root.querySelector(".fbk-toolbar");
      if (!tb) return;
      let saved = null;
      try {
        saved = JSON.parse(localStorage.getItem("pointer_toolbar_pos") || "null");
      } catch {
      }
      if (!saved || typeof saved.dx != "number" || typeof saved.dy != "number") return;
      let rect = tb.getBoundingClientRect(), maxDx = Math.max(0, window.innerWidth - rect.width) - rect.left, maxDy = Math.max(0, window.innerHeight - rect.height) - rect.top;
      this._toolbarDx = Math.min(Math.max(saved.dx, -rect.left), maxDx), this._toolbarDy = Math.min(Math.max(saved.dy, -rect.top), maxDy), this.applyToolbarOffset(tb), tb.classList.add("is-moved");
    }
    applyToolbarOffset(tb) {
      tb.style.setProperty("--fbk-toolbar-dx", `${this._toolbarDx}px`), tb.style.setProperty("--fbk-toolbar-dy", `${this._toolbarDy}px`);
    }
    // Restore the toolbar to its default corner and forget the saved position.
    resetToolbarPos() {
      try {
        localStorage.removeItem("pointer_toolbar_pos");
      } catch {
      }
      this._toolbarDx = 0, this._toolbarDy = 0;
      let tb = this.root.querySelector(".fbk-toolbar");
      tb && (tb.style.removeProperty("--fbk-toolbar-dx"), tb.style.removeProperty("--fbk-toolbar-dy"), tb.classList.remove("is-moved")), this.updateMenuSide();
    }
    // The toolbar's own dropdowns (account/updates/pin-cluster) all open BELOW their trigger by
    // default — fine when the toolbar sits at the top of the screen, but the default corner is
    // bottom-right, and a dropdown opening downward from something already near the bottom edge
    // renders mostly or entirely off-screen (unreachable — this was the actual bug, not just a
    // dragged-toolbar edge case). Recorded once here as an attribute on the toolbar itself, rather
    // than remeasured by each menu, since all three anchor off the same toolbar and the answer is
    // the same for all of them; toggleUserMenu/toggleUpdatesMenu/toggleClusterMenu read it.
    updateMenuSide() {
      let tb = this.root.querySelector(".fbk-toolbar");
      if (!tb) return;
      let rect = tb.getBoundingClientRect(), spaceBelow = window.innerHeight - rect.bottom;
      tb.dataset.fbkMenuSide = spaceBelow < 340 ? "top" : "bottom";
    }
    // Vertically anchors a toolbar dropdown (account/updates/pin-cluster) above or below `rect`
    // per updateMenuSide()'s reading of the toolbar's own position — shared by all three so a
    // toolbar sitting near the bottom edge doesn't open a menu that renders off-screen below it.
    positionMenuVertically(menu, rect, gap = 6) {
      var _a2;
      ((_a2 = this.root.querySelector(".fbk-toolbar")) == null ? void 0 : _a2.dataset.fbkMenuSide) === "top" ? (menu.style.top = "auto", menu.style.bottom = `${Math.max(8, Math.round(window.innerHeight - rect.top + gap))}px`) : (menu.style.bottom = "auto", menu.style.top = `${Math.round(rect.bottom + gap)}px`);
    }
    enableToolbarDrag() {
      let tb = this.root.querySelector(".fbk-toolbar"), grip = this.root.querySelector("#fbk-grip");
      if (!tb || !grip) return;
      let sx = 0, sy = 0, startDx = 0, startDy = 0, baseLeft = 0, baseTop = 0, baseWidth = 0, baseHeight = 0, dragging = !1, onMove = (e) => {
        if (!dragging) return;
        let maxDx = Math.max(0, window.innerWidth - baseWidth) - baseLeft, maxDy = Math.max(0, window.innerHeight - baseHeight) - baseTop;
        this._toolbarDx = Math.min(Math.max(startDx + (e.clientX - sx), -baseLeft), maxDx), this._toolbarDy = Math.min(Math.max(startDy + (e.clientY - sy), -baseTop), maxDy), this.applyToolbarOffset(tb);
      }, onUp = (e) => {
        if (dragging) {
          dragging = !1, tb.classList.remove("is-dragging");
          try {
            grip.releasePointerCapture(e.pointerId);
          } catch {
          }
          try {
            localStorage.setItem("pointer_toolbar_pos", JSON.stringify({ dx: this._toolbarDx, dy: this._toolbarDy }));
          } catch {
          }
          tb.classList.add("is-moved"), this.updateMenuSide();
        }
      };
      grip.addEventListener("pointerdown", (e) => {
        e.preventDefault();
        let rect = tb.getBoundingClientRect();
        baseLeft = rect.left - this._toolbarDx, baseTop = rect.top - this._toolbarDy, baseWidth = rect.width, baseHeight = rect.height, startDx = this._toolbarDx, startDy = this._toolbarDy, sx = e.clientX, sy = e.clientY, dragging = !0, tb.classList.add("is-dragging");
        try {
          grip.setPointerCapture(e.pointerId);
        } catch {
        }
      }), grip.addEventListener("pointermove", onMove), grip.addEventListener("pointerup", onUp), grip.addEventListener("pointercancel", onUp);
    }
    // --- User menu (identity + sign out) ------------------------------------
    toggleUserMenu() {
      this.closeUpdatesMenu(), this.closeClusterMenu(), this.closeCardMenu();
      let host = this.root.querySelector("#fbk-menu-host");
      if (!host) return;
      if (host.querySelector("#fbk-user-menu")) {
        this.closeUserMenu();
        return;
      }
      let displayName = this.user ? escapeHtml(this.user.displayName || this.user.email) : "", roleLabel = this.user ? escapeHtml(this.user.roleName || "") : "";
      host.innerHTML = TPL.userMenu(
        displayName,
        roleLabel,
        formatShortcut(this.shortcut),
        this.authOwnedByHost,
        this.resolveTheme(),
        this.resolveLang()
      );
      let menu = host.querySelector("#fbk-user-menu"), btn = this.root.querySelector("#fbk-user");
      if (btn) {
        btn.setAttribute("aria-expanded", "true");
        let r = btn.getBoundingClientRect();
        this.positionMenuVertically(menu, r), menu.style.right = `${Math.max(8, Math.round(window.innerWidth - r.right))}px`;
      }
      let signoutBtn = host.querySelector("#fbk-signout");
      signoutBtn && signoutBtn.addEventListener("click", () => this.signOut()), host.querySelector("#fbk-shortcut-edit").addEventListener("click", (e) => {
        e.stopPropagation(), this.beginRecordingShortcut(host.querySelector("#fbk-shortcut-edit"));
      }), host.querySelector("#fbk-shortcut-reset").addEventListener("click", async (e) => {
        e.stopPropagation();
        let editBtn = host.querySelector("#fbk-shortcut-edit");
        editBtn && (editBtn.textContent = t("menu.resetting"));
        let ok = await this.saveShortcutPreference(null);
        editBtn && (editBtn.textContent = formatShortcut(this.shortcut)), this.toast(ok ? t("menu.shortcutResetToDefault") : t("menu.failedToResetTryAgain"), ok ? "" : "error");
      });
      let reopenUserMenu = () => {
        this.closeUserMenu(), this.toggleUserMenu();
      }, wireThemeBtn = (id, mode) => {
        host.querySelector(id).addEventListener("click", (e) => {
          e.stopPropagation(), this.resolveTheme() !== mode && (this.setThemeOverride(mode), reopenUserMenu());
        });
      };
      wireThemeBtn("#fbk-theme-light", "light"), wireThemeBtn("#fbk-theme-dark", "dark");
      let wireLangBtn = (id, lang) => {
        host.querySelector(id).addEventListener("click", (e) => {
          e.stopPropagation(), this.resolveLang() !== lang && (this.setLanguageOverride(lang), this.renderChrome(), this.renderSidebar(), this.renderPins(), reopenUserMenu());
        });
      };
      wireLangBtn("#fbk-lang-en", "en"), wireLangBtn("#fbk-lang-ar", "ar"), this._userMenuClose = (e) => {
        let path = e.composedPath();
        !path.includes(menu) && (!btn || !path.includes(btn)) && this.closeUserMenu();
      }, setTimeout(() => {
        this._userMenuClose && document.addEventListener("click", this._userMenuClose, !0);
      }, 0);
    }
    closeUserMenu() {
      var _a2;
      let host = this.root.querySelector("#fbk-menu-host");
      host && host.querySelector("#fbk-user-menu") && (host.innerHTML = ""), (_a2 = this.root.querySelector("#fbk-user")) == null || _a2.setAttribute("aria-expanded", "false"), this._userMenuClose && (document.removeEventListener("click", this._userMenuClose, !0), this._userMenuClose = null), this._shortcutRecordingCleanup && this._shortcutRecordingCleanup();
    }
    // --- Updates menu (in-app notifications) --------------------------------
    async toggleUpdatesMenu() {
      let host = this.root.querySelector("#fbk-menu-host");
      if (!host) return;
      if (host.querySelector("#fbk-notifications-menu")) {
        this.closeUpdatesMenu();
        return;
      }
      this.closeUserMenu(), this.closeClusterMenu(), this.closeCardMenu();
      let items = await this.apiNotifications();
      this.unreadNotifyCount > 0 && (await this.apiMarkAllNotificationsRead(), this.unreadNotifyCount = 0, this.updateNotifyBadges()), host.innerHTML = TPL.notificationsMenu(items);
      let menu = host.querySelector("#fbk-notifications-menu");
      if (!menu) return;
      let btn = this.root.querySelector("#fbk-updates");
      if (btn) {
        btn.setAttribute("aria-expanded", "true");
        let r = btn.getBoundingClientRect();
        this.positionMenuVertically(menu, r), menu.style.left = `${Math.max(8, Math.min(window.innerWidth - 330, Math.round(r.left)))}px`;
      }
      menu.querySelectorAll(".fbk-notification-item").forEach((el) => {
        el.addEventListener("click", () => {
          let commentId = el.getAttribute("data-id");
          if (this.closeUpdatesMenu(), commentId) {
            let c = this.comments.find((x) => String(x.id) === String(commentId));
            c && this.statusFilter !== "all" && this.statusFilter !== c.status && (this.statusFilter = "all"), this.toggleSidebar(!0), this.renderSidebar(), setTimeout(() => {
              let card = this.root.querySelector(`.fbk-card[data-id="${commentId}"]`);
              card && (card.scrollIntoView({ behavior: "smooth", block: "center" }), card.classList.add("highlight"), setTimeout(() => card.classList.remove("highlight"), 2e3));
            }, 100);
          }
        });
      }), this._updatesMenuClose = (e) => {
        let path = e.composedPath();
        !path.includes(menu) && (!btn || !path.includes(btn)) && this.closeUpdatesMenu();
      }, setTimeout(() => {
        this._updatesMenuClose && document.addEventListener("click", this._updatesMenuClose, !0);
      }, 0);
    }
    closeUpdatesMenu() {
      var _a2;
      let host = this.root.querySelector("#fbk-menu-host");
      host && host.querySelector("#fbk-notifications-menu") && (host.innerHTML = ""), (_a2 = this.root.querySelector("#fbk-updates")) == null || _a2.setAttribute("aria-expanded", "false"), this._updatesMenuClose && (document.removeEventListener("click", this._updatesMenuClose, !0), this._updatesMenuClose = null);
    }
    // --- Notification Polling & Verification ---------------------------------
    startNotificationPolling() {
      var _a2, _b;
      this.stopNotificationPolling(), this.fetchUnreadNotifyCount();
      let pollInterval = (_b = (_a2 = window.__POINTER_CONFIG__) == null ? void 0 : _a2.notifyPollMs) != null ? _b : 6e4;
      this._notifyPollTimer = window.setInterval(() => {
        document.visibilityState === "visible" && this.fetchUnreadNotifyCount();
      }, pollInterval), this._onVisibilityChange = () => {
        document.visibilityState === "visible" && this.fetchUnreadNotifyCount();
      }, document.addEventListener("visibilitychange", this._onVisibilityChange);
    }
    stopNotificationPolling() {
      this._notifyPollTimer !== null && (window.clearInterval(this._notifyPollTimer), this._notifyPollTimer = null), this._onVisibilityChange && (document.removeEventListener("visibilitychange", this._onVisibilityChange), this._onVisibilityChange = null);
    }
    async fetchUnreadNotifyCount() {
      var _a2;
      if (this.token)
        try {
          let r = await this.api("/api/me/notifications/unread-count");
          if (!r.ok) return;
          let envelope = await r.json(), count = typeof ((_a2 = envelope == null ? void 0 : envelope.data) == null ? void 0 : _a2.count) == "number" ? envelope.data.count : typeof (envelope == null ? void 0 : envelope.count) == "number" ? envelope.count : 0;
          this.unreadNotifyCount = count, this.updateNotifyBadges();
        } catch {
        }
    }
    updateNotifyBadges() {
      if (this._collapsed) {
        this.renderChrome();
        return;
      }
      let dot = this.root.querySelector("#fbk-notify-count");
      dot && dot.classList.toggle("fbk-hidden", this.unreadNotifyCount <= 0);
      let updatesBtn = this.root.querySelector("#fbk-updates");
      updatesBtn && updatesBtn.setAttribute("aria-label", `${t("toolbar.updates")}${unreadSuffix(this.unreadNotifyCount)}`);
    }
    async apiNotifications(unread = !1) {
      var _a2;
      try {
        let r = await this.api(`/api/me/notifications${unread ? "?unread=true" : ""}`);
        if (!r.ok) return [];
        let envelope = await r.json(), items = (_a2 = envelope == null ? void 0 : envelope.data) != null ? _a2 : envelope;
        return Array.isArray(items) ? items : [];
      } catch {
        return [];
      }
    }
    async apiMarkAllNotificationsRead() {
      try {
        await this.api("/api/me/notifications/read-all", { method: "POST" });
      } catch {
      }
    }
    async apiVerify(id, ok, note) {
      var _a2, _b;
      try {
        let r = await this.api(`/api/comments/${id}/verify`, {
          method: "POST",
          body: JSON.stringify({ ok, note: note || null })
        });
        if (!r.ok) {
          let errMessage = t("toast.failedToVerifyComment");
          try {
            let err = await r.json();
            err != null && err.message && (errMessage = err.message);
          } catch {
          }
          return this.toast(errMessage, "error"), !1;
        }
        let envelope = await r.json(), updated = (_a2 = envelope == null ? void 0 : envelope.data) != null ? _a2 : envelope, idx = this.comments.findIndex((c) => String(c.id) === String(id));
        if (idx !== -1 && updated) {
          let normalizedComment = {
            ...this.comments[idx],
            ...updated,
            status: typeof updated.status == "number" ? STATUS_STR[updated.status] || "open" : updated.status || "open",
            verifiedAt: (_b = updated.verifiedAt) != null ? _b : null
          };
          this.comments[idx] = normalizedComment;
        } else
          await this.fetchComments();
        return this.renderSidebar(), this.renderPins(), this.toast(ok ? t("toast.commentVerified") : t("toast.commentReopened")), !0;
      } catch {
        return this.toast(t("toast.failedToVerifyComment"), "error"), !1;
      }
    }
    // Clear the session and reset the widget to its logged-out (deferred-login) state.
    signOut() {
      this.closeUserMenu(), this.closeUpdatesMenu(), this.stopNotificationPolling(), this.unreadNotifyCount = 0, this.picking && this.stopPicking(), this.clearAuth(), this.comments = [], this.hiddenPrivateCount = 0, this.sidebarOpen = !1, this.mineOnly = !1, this.authorFilter = null, this.statusFilter = "all", this.renderChrome(), this.renderSidebar(), this.renderPins(), this.toast(t("toast.signedOut"));
    }
    // Collapse the overlay to the floating launcher (remembered for this tab session).
    hideOverlay() {
      this.picking && this.stopPicking(), this.sidebarOpen = !1, this._collapsed = !0;
      try {
        sessionStorage.removeItem("pointer_visible");
      } catch {
      }
      this.renderChrome(), this.toast(t("toast.hiddenClickToReopen", { brand: getBrandName() }));
    }
    // Restore the full overlay from the launcher; remembered for this tab session.
    showOverlay() {
      this._collapsed = !1;
      try {
        sessionStorage.setItem("pointer_visible", "1");
      } catch {
      }
      this.renderChrome(), this.token && this.fetchComments().then(() => {
        this.renderSidebar(), this.renderPins();
      });
    }
    toggleSidebar(force) {
      var _a2;
      this.sidebarOpen = force === void 0 ? !this.sidebarOpen : force, this.root.querySelector("#fbk-sidebar").classList.toggle("open", this.sidebarOpen), (_a2 = this.root.querySelector("#fbk-toggle")) == null || _a2.setAttribute("aria-expanded", String(this.sidebarOpen)), this.sidebarOpen && this.fetchComments().then(() => {
        this.renderSidebar(), this.renderPins();
      });
    }
    // --- Staying clickable under host-app modals ------------------------------
    // The rects our own UI currently occupies on screen — every top-level container that can be
    // visible at once. Used to punch matching holes in any modal backdrop so those areas stay
    // clickable. Elements not currently rendered/visible in this.root simply aren't found and are
    // skipped; no need to check display/visibility explicitly.
    ownUiRects() {
      var _a2;
      let selectors = [".fbk-launcher", ".fbk-toolbar", ".fbk-sidebar", ".fbk-modal-overlay", ".fbk-popover", ".fbk-menu"], rects = [];
      for (let sel of selectors) {
        let el = (_a2 = this.root) == null ? void 0 : _a2.querySelector(sel);
        if (!el) continue;
        let r = el.getBoundingClientRect();
        r.width > 0 && r.height > 0 && rects.push(r);
      }
      return rects;
    }
    // Clips a hole out of every full-viewport modal backdrop AND dialog/panel content pane currently
    // open (BACKDROP_SELECTOR, DIALOG_CONTENT_SELECTOR), exactly where our own UI sits — see the
    // connectedCallback comment for why the backdrop needs this: no z-index, however high, makes an
    // element clickable through it, because Chromium's native hit-testing can award the click to the
    // backdrop regardless of paint order. The content pane needs the SAME treatment for a separate
    // reason, confirmed empirically against a real Angular Material dialog: its content pane
    // out-ranks our max-z-index shadow content in actual paint order despite every ancestor on both
    // sides having no stacking-context-creating property that would explain it per spec — whatever
    // the underlying browser mechanism, clip-path fixes it identically. Everywhere else on the page,
    // the backdrop/dialog keeps blocking/rendering normally — only our own footprint becomes
    // click-through.
    punchBackdropHoles() {
      let targets = document.querySelectorAll(`${BACKDROP_SELECTOR}, ${DIALOG_CONTENT_SELECTOR}`);
      if (targets.length === 0) return;
      let rects = this.ownUiRects();
      targets.forEach((t2) => {
        let clipPath = rects.length === 0 ? "" : buildClipPathWithHoles(rects, t2.getBoundingClientRect());
        t2.style.clipPath !== clipPath && (t2.style.clipPath = clipPath);
      });
    }
    // --- Element picking -----------------------------------------------------
    togglePicking() {
      this.picking ? this.stopPicking() : this.startPicking();
    }
    startPicking() {
      var _a2;
      this.picking = !0, (_a2 = this.root.querySelector("#fbk-pins-layer")) == null || _a2.classList.add("picking");
      let tb = this.root.querySelector(".fbk-toolbar");
      tb == null || tb.classList.add("is-dim");
      let addBtn = this.root.querySelector("#fbk-add");
      addBtn.setAttribute("aria-pressed", "true"), addBtn.innerHTML = `<span class="fbk-toolbar-btn__icon">${ICON.close}</span>`, addBtn.title = t("toolbar.cancel"), addBtn.setAttribute("aria-label", t("toolbar.cancel")), document.addEventListener("mousemove", this._onHover, !0), document.addEventListener("click", this._onPick, !0), document.addEventListener("keydown", this._onPickKey, !0), this.toast(t("popover.clickAnyElementToComment"));
    }
    stopPicking() {
      var _a2, _b, _c, _d;
      this.picking = !1, (_b = (_a2 = this.root) == null ? void 0 : _a2.querySelector("#fbk-pins-layer")) == null || _b.classList.remove("picking"), (_d = (_c = this.root) == null ? void 0 : _c.querySelector(".fbk-toolbar")) == null || _d.classList.remove("is-dim");
      let addBtn = this.root && this.root.querySelector("#fbk-add");
      addBtn && (addBtn.setAttribute("aria-pressed", "false"), addBtn.innerHTML = `<span class="fbk-toolbar-btn__icon">${ICON.crosshair}</span>`), this.updateAddButtonTooltip(), document.removeEventListener("mousemove", this._onHover, !0), document.removeEventListener("click", this._onPick, !0), document.removeEventListener("keydown", this._onPickKey, !0), this.clearHover();
    }
    // Esc cancels element-picking (deselects the pointer) without placing a comment.
    onPickKey(e) {
      e.key !== "Escape" && e.key !== "Esc" || (e.preventDefault(), e.stopPropagation(), this.stopPicking(), this.toast(t("popover.cancelled")));
    }
    clearHover() {
      this.hovered && (this.hovered.classList.remove(HL_CLASS), this.hovered = null);
    }
    isOwnElement(el) {
      return el === this || !!el && el.tagName === "POINTER-FEEDBACK";
    }
    // Resolves the real element a pointer event is over, seeing through any full-viewport modal
    // backdrop the host app has open (BACKDROP_SELECTOR) — a dialog opened WHILE picking is active
    // would otherwise swallow the hover/click itself, since it deliberately covers the whole
    // viewport to catch clicks that dismiss it.
    //
    // e.target is the primary source of truth (NOT document.elementsFromPoint): our own UI lives in
    // a Shadow DOM behind a host element with zero intrinsic size, so point-based hit-testing can
    // never land on the host itself — only the browser's own composed-event retargeting (which sets
    // e.target to the host for any event landing on our shadow content) reliably identifies "this
    // was our own UI". elementsFromPoint is used only as a fallback, and only once e.target has
    // already proven to be a recognized backdrop — never to re-derive "is this our own UI".
    resolveHitTarget(e) {
      let target = e.target;
      if (!target || this.isOwnElement(target)) return null;
      if (!target.matches(BACKDROP_SELECTOR)) return target;
      for (let el of document.elementsFromPoint(e.clientX, e.clientY))
        if (!this.isOwnElement(el) && !el.matches(BACKDROP_SELECTOR))
          return el;
      return null;
    }
    onHover(e) {
      let el = this.resolveHitTarget(e);
      el && el !== this.hovered && (this.clearHover(), this.hovered = el, el.classList.add(HL_CLASS));
    }
    onPick(e) {
      let el = this.resolveHitTarget(e);
      if (!el) return;
      e.preventDefault(), e.stopPropagation();
      let x = e.clientX, y = e.clientY;
      this.clearHover(), this.stopPicking(), this._pendingShotPromise = null, this.openCommentPopover(x, y, el);
    }
    // Kick off a best-effort screenshot capture for `el` (resolves null on failure).
    // Idempotent per popover: reuses an in-flight capture if one already started.
    beginScreenshotCapture(el) {
      this.screenshotEnabled && (this._pendingShotPromise || (this._pendingShotPromise = captureScreenshot(el).catch((err) => (console.warn("[pointer-feedback] screenshot capture failed", err), null))));
    }
    // Upload a screenshot Blob to /api/uploads via multipart/form-data. Returns the
    // absolute URL on success, or null on failure. Deliberately NOT using api() —
    // for FormData we must let the browser set the multipart boundary itself.
    async uploadToServer(blob) {
      try {
        let ext = blob.type === "image/jpeg" ? "jpg" : "webp", fd = new FormData();
        fd.append("file", blob, `screenshot.${ext}`), fd.append("project", this.project);
        let r = await pfFetch(`${this.server}/api/uploads`, {
          method: "POST",
          headers: { ...this.token ? { Authorization: `Bearer ${this.token}` } : {} },
          body: fd
        });
        if (r.status === 401)
          return this.handle401(), null;
        if (!r.ok) throw new Error("HTTP " + r.status);
        let envelope = await r.json();
        if (!envelope || !envelope.isSuccess || !envelope.data || !envelope.data.url)
          throw new Error("upload response missing data.url");
        return envelope.data.url;
      } catch (err) {
        return console.warn("[pointer-feedback] screenshot upload failed", err), null;
      }
    }
    // --- Comment popover -----------------------------------------------------
    // (named openCommentPopover, not showPopover, to avoid clashing with the
    //  built-in HTMLElement.showPopover() from the Popover API.)
    openCommentPopover(x, y, el) {
      let currentEl = el, currentMeta = captureMetadata(currentEl, this.sourceAttr, { captureText: this.captureTextContent }), host = this.root.querySelector("#fbk-popover-host");
      host.innerHTML = TPL.popover(currentMeta, x, y, this.screenshotEnabled, this.predefinedActions, this.pageContextCaptureEnabled, this.commentFields), applyDataPosition(host, ".fbk-popover");
      let popoverEl = host.querySelector(".fbk-popover");
      if (popoverEl) {
        let rect = popoverEl.getBoundingClientRect(), margin = 8, left = Math.max(margin, Math.min(x, window.innerWidth - rect.width - margin)), top = Math.max(margin, Math.min(y, window.innerHeight - rect.height - margin));
        popoverEl.style.left = `${Math.round(left)}px`, popoverEl.style.top = `${Math.round(top)}px`;
      }
      currentEl.classList.add(HL_CLASS);
      let ta = host.querySelector("#fbk-comment-text");
      ta.focus();
      let upBtn = host.querySelector("#fbk-target-up"), downBtn = host.querySelector("#fbk-target-down"), titleEl = host.querySelector("#fbk-popover-title"), snippetEl = host.querySelector("#fbk-popover-snippet"), srcEl = host.querySelector("#fbk-popover-src"), srcPathEl = host.querySelector("#fbk-popover-src-path"), updateNavButtons = () => {
        upBtn && (upBtn.disabled = !currentEl.parentElement), downBtn && (downBtn.disabled = currentEl.children.length === 0);
      }, navigateTo = (nextEl) => {
        currentEl.classList.remove(HL_CLASS), currentEl = nextEl, currentMeta = captureMetadata(currentEl, this.sourceAttr, { captureText: this.captureTextContent }), currentEl.classList.add(HL_CLASS), currentEl.scrollIntoView({ block: "nearest", inline: "nearest" }), titleEl && (titleEl.innerHTML = `Comment on &lt;${escapeHtml(currentMeta._tag)}&gt;`), snippetEl && (snippetEl.textContent = currentMeta._snapshotPreview.slice(0, 200)), srcEl && srcEl.classList.toggle("fbk-hidden", !currentMeta._sourcePath), srcPathEl && (srcPathEl.textContent = currentMeta._sourcePath || ""), updateNavButtons();
      };
      upBtn && upBtn.addEventListener("click", () => {
        let parent = currentEl.parentElement;
        parent && navigateTo(parent);
      }), downBtn && downBtn.addEventListener("click", () => {
        let child = currentEl.children[0];
        child && navigateTo(child);
      }), updateNavButtons();
      let isPrivateComment = !1, privateToggle = host.querySelector("#fbk-comment-private");
      privateToggle && privateToggle.addEventListener("click", () => {
        isPrivateComment = !isPrivateComment, privateToggle.classList.toggle("is-active", isPrivateComment), privateToggle.setAttribute("aria-pressed", String(isPrivateComment)), privateToggle.title = isPrivateComment ? t("card.privateClickToMakePublic") : t("card.makePrivateOnlyYou"), privateToggle.setAttribute("aria-label", isPrivateComment ? t("card.makePublic") : t("card.makePrivate")), privateToggle.innerHTML = isPrivateComment ? ICON.lock : ICON.unlock;
      });
      let selectedActionIds = /* @__PURE__ */ new Set(), stopMsListening = null, actionMsControl = host.querySelector("#fbk-action-ms-control"), actionMsInput = host.querySelector("#fbk-action-ms-input"), actionMsChips = host.querySelector("#fbk-action-ms-chips"), actionMsList = host.querySelector("#fbk-action-ms-list");
      if (actionMsControl && actionMsInput && actionMsChips && actionMsList) {
        let renderChips = () => {
          actionMsChips.innerHTML = Array.from(selectedActionIds).map((id) => {
            let a = this.predefinedActions.find((x2) => x2.id === id);
            return a ? `<span class="fbk-ms-chip"><span class="fbk-ms-chip-label">${escapeHtml(a.text)}</span><button type="button" class="fbk-ms-chip-remove" data-id="${id}" aria-label="${t("popover.remove")} ${escapeHtml(a.text)}">&times;</button></span>` : "";
          }).join(""), actionMsChips.querySelectorAll(".fbk-ms-chip-remove").forEach((btn) => {
            btn.addEventListener("click", (e) => {
              e.stopPropagation(), selectedActionIds.delete(Number(btn.dataset.id)), renderChips(), renderOptions(actionMsInput.value);
            });
          });
        }, renderOptions = (query) => {
          let q = query.trim().toLowerCase(), matches = this.predefinedActions.filter(
            (a) => !selectedActionIds.has(a.id) && (!q || a.text.toLowerCase().includes(q))
          );
          actionMsList.innerHTML = matches.length ? matches.map((a) => `<div class="fbk-ms-option" role="option" data-id="${a.id}">${escapeHtml(a.text)}</div>`).join("") : `<div class="fbk-ms-empty">${t("popover.noMatches")}</div>`, actionMsList.querySelectorAll(".fbk-ms-option").forEach((opt) => {
            opt.addEventListener("mousedown", (e) => {
              e.preventDefault(), selectedActionIds.add(Number(opt.dataset.id)), actionMsInput.value = "", renderChips(), renderOptions(""), actionMsInput.focus();
            });
          });
        }, openList = () => {
          actionMsList.hidden = !1, actionMsInput.setAttribute("aria-expanded", "true");
        }, closeList = () => {
          actionMsList.hidden = !0, actionMsInput.setAttribute("aria-expanded", "false");
        };
        actionMsInput.addEventListener("focus", () => {
          renderOptions(actionMsInput.value), openList();
        }), actionMsInput.addEventListener("input", () => {
          renderOptions(actionMsInput.value), openList();
        }), actionMsInput.addEventListener("keydown", (e) => {
          if (e.key === "Escape")
            e.stopPropagation(), closeList();
          else if (e.key === "Enter") {
            e.preventDefault();
            let first = actionMsList.querySelector(".fbk-ms-option"), id = first == null ? void 0 : first.dataset.id;
            id && (selectedActionIds.add(Number(id)), actionMsInput.value = "", renderChips(), renderOptions(""));
          } else if (e.key === "Backspace" && !actionMsInput.value && selectedActionIds.size) {
            let last = Array.from(selectedActionIds).pop();
            selectedActionIds.delete(last), renderChips(), renderOptions("");
          }
        });
        let onDocClick = (e) => {
          let path = e.composedPath();
          !path.includes(actionMsControl) && !path.includes(actionMsList) && closeList();
        };
        document.addEventListener("click", onDocClick, !0), stopMsListening = () => document.removeEventListener("click", onDocClick, !0), renderOptions("");
      }
      let attachShotComment = !1, shotToggle = host.querySelector("#fbk-comment-shot");
      shotToggle && shotToggle.addEventListener("click", () => {
        attachShotComment = !attachShotComment, shotToggle.classList.toggle("active", attachShotComment), shotToggle.setAttribute("aria-checked", String(attachShotComment)), attachShotComment && this.beginScreenshotCapture(currentEl);
      });
      let isBugReportComment = !1, bugToggle = host.querySelector("#fbk-comment-bug");
      bugToggle && bugToggle.addEventListener("click", () => {
        isBugReportComment = !isBugReportComment, bugToggle.classList.toggle("active", isBugReportComment), bugToggle.setAttribute("aria-checked", String(isBugReportComment));
      });
      let cancelPopover = () => {
        currentEl.classList.remove(HL_CLASS), host.innerHTML = "", this._pendingShotPromise = null, stopMsListening == null || stopMsListening();
      };
      host.querySelector("#fbk-cancel").addEventListener("click", cancelPopover), host.addEventListener("keydown", (e) => {
        e.key === "Escape" && (e.preventDefault(), e.stopPropagation(), cancelPopover());
      });
      let moreFieldsBtn = host.querySelector("#fbk-more-fields"), extraFieldsDiv = host.querySelector("#fbk-extra-fields");
      extraFieldsDiv && this.bindFieldValidation(extraFieldsDiv, "cf"), moreFieldsBtn && extraFieldsDiv && moreFieldsBtn.addEventListener("click", () => {
        let isExpanded = moreFieldsBtn.getAttribute("aria-expanded") === "true";
        if (moreFieldsBtn.setAttribute("aria-expanded", String(!isExpanded)), moreFieldsBtn.textContent = isExpanded ? t("fields.extra") : t("fields.fewer"), extraFieldsDiv.hidden = isExpanded, !isExpanded) {
          let firstInput = extraFieldsDiv.querySelector("input, select");
          firstInput && firstInput.focus();
        }
      }), host.querySelector("#fbk-submit").addEventListener("click", async () => {
        let text = ta.value.trim();
        if (!text) return this.toast(t("popover.commentCannotBeEmpty"), "error");
        let customFields, valMap = {};
        if (extraFieldsDiv) {
          let res = this.validateAndCollectFields(extraFieldsDiv, "cf");
          if (res.hasError) return;
          customFields = res.fields, valMap = res.valMap;
        }
        let isPrivate = isPrivateComment, attachShot = attachShotComment, isBugReport = isBugReportComment, shotPromise = this._pendingShotPromise;
        this._pendingShotPromise = null;
        let predefinedActionIds = Array.from(selectedActionIds), submitBtn = host.querySelector("#fbk-submit");
        submitBtn.disabled = !0, submitBtn.textContent = t("menu.saving");
        let saved = await this.createComment({ ...currentMeta, text, isPrivate, attachShot, shotPromise, predefinedActionIds, isBugReport, customFields });
        if (saved === !0)
          currentEl.classList.remove(HL_CLASS), host.innerHTML = "", stopMsListening == null || stopMsListening();
        else if (typeof saved == "string") {
          if (submitBtn.disabled = !1, submitBtn.textContent = t("popover.add"), extraFieldsDiv) {
            extraFieldsDiv.innerHTML = renderFieldInputs(this.commentFields, valMap, "cf"), this.bindFieldValidation(extraFieldsDiv, "cf");
            let errEl = document.createElement("p");
            errEl.className = "fbk-field-error", errEl.textContent = t("fields.serverRejected") + " " + saved, extraFieldsDiv.prepend(errEl);
          }
        } else
          submitBtn.disabled = !1, submitBtn.textContent = t("popover.add");
      });
    }
    validateAndCollectFields(container, idPrefix = "cf") {
      container.querySelectorAll(".fbk-field-error").forEach((el) => el.remove()), container.querySelectorAll('[aria-invalid="true"]').forEach((el) => {
        el.removeAttribute("aria-invalid"), el.removeAttribute("aria-describedby");
      });
      let valMap = collectFieldValues(container), hasError = !1, fieldsOut = {};
      for (let def of this.commentFields) {
        let val = valMap[def.key];
        if (val !== void 0) {
          let errKey = validateFieldValue(def, val);
          errKey ? (hasError = !0, this.renderFieldError(container, def, errKey, idPrefix)) : fieldsOut[def.key] = val;
        }
      }
      return { fields: Object.keys(fieldsOut).length ? fieldsOut : void 0, hasError, valMap };
    }
    bindFieldValidation(container, idPrefix) {
      container.querySelectorAll('[name^="fbk-cf-"]').forEach((el) => {
        let validate = () => {
          var _a2;
          let key = el.name.replace(/^fbk-cf-/, ""), def = this.commentFields.find((d) => d.key === key);
          if ((_a2 = container.querySelector(`#${idPrefix}-${key}-error`)) == null || _a2.remove(), el.removeAttribute("aria-invalid"), el.removeAttribute("aria-describedby"), def && el.value.trim()) {
            let errKey = validateFieldValue(def, el.value.trim());
            errKey && this.renderFieldError(container, def, errKey, idPrefix);
          }
        };
        ["input", "change", "blur"].forEach((ev) => el.addEventListener(ev, validate));
      });
    }
    // Shared by the composer's extra-fields panel and the card's inline "Edit fields" form: renders
    // an inline error under the offending input and marks it aria-invalid/aria-describedby, so both
    // surfaces show identical validation feedback for the same i18n error key.
    renderFieldError(container, def, errKey, idPrefix = "cf") {
      var _a2;
      let input = container.querySelector(`[name="fbk-cf-${escapeHtml(def.key)}"]`);
      if (!input) return;
      input.setAttribute("aria-invalid", "true");
      let errId = `${idPrefix}-${def.key}-error`;
      input.setAttribute("aria-describedby", errId);
      let errEl = document.createElement("p");
      errEl.className = "fbk-field-error", errEl.id = errId, errEl.textContent = t(errKey, { hosts: (def.allowedHosts || []).join(", ") }), (_a2 = input.parentElement) == null || _a2.appendChild(errEl);
    }
    // Returns true on success (popover should close), false on failure (popover stays open).
    async createComment(data) {
      var _a2, _b, _c;
      let vw = window.innerWidth, vh = window.innerHeight, deviceType = vw < 768 ? "mobile" : vw < 1024 ? "tablet" : "desktop", element = {
        selector: data.selector,
        snapshot: data.snapshot,
        classes: data.classes,
        computedStyles: data.computedStyles,
        appliedCssRules: data.appliedCssRules,
        sourcePath: data.sourcePath,
        parentInfo: data.parentInfo,
        // Page the comment was left on — gives the apply step the route/page,
        // essential for multi-page apps (window.location reflects the current page).
        pageUrl: window.location.href,
        // Active route relative to the origin: path + query params (+ hash, so
        // hash-routed SPAs are covered too).
        route: window.location.pathname + window.location.search + window.location.hash,
        pageTitle: document.documentElement.hasAttribute("data-snapshot-mask") || !this.captureTextContent ? "•••" : document.title,
        viewportWidth: vw,
        viewportHeight: vh,
        deviceType,
        devicePixelRatio: window.devicePixelRatio || 1,
        userAgent: navigator.userAgent
      };
      if (data.attachShot && data.shotPromise) {
        let blob = await Promise.resolve(data.shotPromise).catch(() => null);
        if (blob) {
          let url = await this.uploadToServer(blob);
          url ? element.screenshotUrl = url : this.toast(t("toast.screenshotUploadFailed"), "error");
        }
      }
      let language = (_a2 = data.language) != null ? _a2 : await detectTextLanguageAsync(data.text), bodyObj = {
        body: data.text,
        environment: this.environmentInt,
        isPrivate: !!data.isPrivate,
        element,
        isBugReport: !!data.isBugReport,
        language
      };
      if (data.predefinedActionIds && data.predefinedActionIds.length && (bodyObj.predefinedActionIds = data.predefinedActionIds), data.customFields && Object.keys(data.customFields).length > 0 && (bodyObj.customFields = data.customFields), data.isBugReport) {
        let pageContext = getPageContextPayload();
        pageContext && (bodyObj.pageContext = pageContext);
      }
      try {
        let r = await this.api(`/api/projects/${encodeURIComponent(this.project)}/comments`, {
          method: "POST",
          body: JSON.stringify({ ...bodyObj, projectKey: this.project })
        });
        if (r.status === 409 || r.status === 404)
          return this.disableSilently(), !0;
        if (!r.ok) {
          let errEnv = await r.json().catch(() => null), msg = errEnv && errEnv.message || "";
          if (msg.toLowerCase().includes("field"))
            return await this.fetchCaptureConfig(), msg;
          if (data.predefinedActionIds && data.predefinedActionIds.length && msg.toLowerCase().includes("action"))
            return await this.fetchPredefinedActions(), this.toast(t("toast.actionNoLongerAvailable"), "error"), !1;
          if (r.status === 403)
            return this.toast(t("toast.commentsNotAllowedFromAddress"), "error"), !1;
          if (r.status === 429) {
            let retryAfter = Number((_c = (_b = r.headers) == null ? void 0 : _b.get) == null ? void 0 : _c.call(_b, "retry-after"));
            return this.toast(
              retryAfter > 0 ? t("toast.tooManyCommentsRetryIn", { n: retryAfter, s: retryAfter === 1 ? "" : "s" }) : t("toast.tooManyCommentsWait"),
              "error"
            ), !1;
          }
          throw new Error("HTTP " + r.status);
        }
        let comment = (await r.json()).data;
        comment && this.comments.push({ ...comment, status: STATUS_STR[comment.status] || "open" });
        let newId = comment ? String(comment.id) : "";
        return this._newPinId = newId || null, this.renderSidebar(), this.renderPins(), newId && setTimeout(() => {
          String(this._newPinId) === newId && (this._newPinId = null);
        }, 3e3), this.toast(t("toast.commentAdded"), "success", newId ? t("toast.undo") : void 0, newId ? () => this.deleteComment(newId) : void 0), !0;
      } catch (e) {
        return e.message !== "HTTP 401 Unauthorized" && this.toast(t("toast.failedToSaveComment"), "error"), !1;
      }
    }
    // --- Mutations -----------------------------------------------------------
    async addReply(id, text) {
      try {
        if (!(await this.api(`/api/comments/${id}/replies`, {
          method: "POST",
          body: JSON.stringify({ body: text })
        })).ok) throw new Error();
        await this.fetchComments(), this.renderSidebar(), this.renderPins();
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(t("toast.failedToReply"), "error");
      }
    }
    async toggleApply(comment) {
      let nextStr = comment.status === "pending-apply" ? "open" : "pending-apply", nextInt = STATUS_INT[nextStr];
      try {
        if (!(await this.api(`/api/comments/${comment.id}`, {
          method: "PATCH",
          body: JSON.stringify({ status: nextInt })
        })).ok) throw new Error();
        comment.status = nextStr, this.renderSidebar(), this.renderPins(), this.toast(nextStr === "pending-apply" ? t("toast.markedForApply") : t("toast.unmarked"));
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(t("toast.updateFailed"), "error");
      }
    }
    // Generic status change (Re-open → open, Archive → archived).
    async setStatus(comment, nextStr, toastMsg) {
      let nextInt = STATUS_INT[nextStr];
      try {
        if (!(await this.api(`/api/comments/${comment.id}`, {
          method: "PATCH",
          body: JSON.stringify({ status: nextInt })
        })).ok) throw new Error();
        comment.status = nextStr, this.renderSidebar(), this.renderPins(), this.toast(toastMsg || t("toast.updated"));
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(t("toast.updateFailed"), "error");
      }
    }
    // Toggle a comment's privacy — author-only (enforced server-side too).
    async setVisibility(comment, isPrivate) {
      try {
        let r = await this.api(`/api/comments/${comment.id}/visibility`, {
          method: "PATCH",
          body: JSON.stringify({ isPrivate })
        });
        if (!r.ok) throw new Error("HTTP " + r.status);
        comment.isPrivate = isPrivate, this.renderSidebar(), this.renderPins(), this.toast(isPrivate ? t("toast.markedPrivate") : t("toast.madePublic"));
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(t("toast.updateFailed"), "error");
      }
    }
    async markCompleted(comment) {
      let label = this.user ? this.user.displayName || this.user.email : null;
      try {
        let r = await this.api(`/api/comments/${comment.id}`, {
          method: "PATCH",
          body: JSON.stringify({ status: STATUS_INT.applied, appliedByLabel: label })
        });
        if (!r.ok) throw new Error("HTTP " + r.status);
        comment.status = "applied", label && (comment.appliedByLabel = label), this.renderSidebar(), this.renderPins(), this.toast(t("toast.markedCompleted"));
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(t("toast.updateFailed"), "error");
      }
    }
    /**
     * Delete confirmation for a comment's own card: an opaque overlay covering the WHOLE card
     * (not just the kebab menu — the menu is already closed by the time this runs), so nothing
     * else on the card can be mis-clicked while confirming. No auto-dismiss: unlike the old
     * in-menu confirm (which had to give the dropdown back for other uses), this is a deliberate
     * modal-style prompt that stays until the viewer explicitly confirms or cancels.
     */
    confirmDeleteCard(id) {
      let card = this.root && this.root.querySelector(`.fbk-card[data-id="${id}"]`);
      if (!card || card.querySelector(".fbk-card-delete-confirm")) return;
      let overlay = document.createElement("div");
      overlay.className = "fbk-card-delete-confirm", overlay.innerHTML = `<p class="fbk-card-delete-confirm-q">${t("card.deleteThisComment")}</p><div class="fbk-card-delete-confirm-actions"><button type="button" class="fbk-mini danger" data-c="yes">${t("card.confirmDelete")}</button><button type="button" class="fbk-mini" data-c="no">${t("toolbar.cancel")}</button></div>`, card.appendChild(overlay), overlay.querySelector('[data-c="yes"]').addEventListener("click", (e) => {
        e.stopPropagation(), this.deleteComment(id);
      }), overlay.querySelector('[data-c="no"]').addEventListener("click", (e) => {
        e.stopPropagation(), overlay.remove();
      });
    }
    async deleteComment(id) {
      try {
        let r = await this.api(`/api/comments/${id}`, { method: "DELETE" });
        if (!r.ok) {
          let body = await r.json().catch(() => null);
          throw new Error(body && body.message || "HTTP " + r.status);
        }
        this.comments = this.comments.filter((c) => String(c.id) !== String(id)), this.closeCardMenu(), this.renderSidebar(), this.renderPins(), this.toast(t("toast.deleted"));
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(e.message || t("toast.deleteFailed"), "error");
      }
    }
    // Inline edit (own comments only): swap the body text for a textarea + controls.
    startEdit(id) {
      let card = this.root && this.root.querySelector(`.fbk-card[data-id="${id}"]`);
      if (!card || card.querySelector(".fbk-edit")) return;
      let comment = (this.comments || []).find((x) => String(x.id) === String(id));
      if (!comment) return;
      let textEl = card.querySelector(".fbk-text");
      if (!textEl) return;
      let hasShot = !!(comment.element && comment.element.screenshotUrl), editor = document.createElement("div");
      editor.className = "fbk-edit", editor.style.margin = "6px 0", editor.innerHTML = `
        <textarea class="fbk-textarea fbk-edit-body">${escapeHtml(comment.body || "")}</textarea>
        ${hasShot ? `<label class="fbk-edit-option"><input type="checkbox" class="fbk-edit-rmshot" /> ${t("card.removeImage")}</label>` : ""}
        <div class="fbk-reply-row">
          <button class="fbk-btn primary fbk-btn-fill fbk-edit-save">${t("card.save")}</button>
          <button class="fbk-mini fbk-edit-cancel">${t("toolbar.cancel")}</button>
        </div>`, textEl.style.display = "none", textEl.insertAdjacentElement("afterend", editor);
      let ta = editor.querySelector(".fbk-edit-body");
      ta.focus(), editor.querySelector(".fbk-edit-cancel").addEventListener("click", () => {
        editor.remove(), textEl.style.display = "";
      }), editor.querySelector(".fbk-edit-save").addEventListener("click", () => {
        let body = ta.value.trim();
        if (!body) {
          this.toast(t("popover.commentCannotBeEmpty"), "error");
          return;
        }
        let rm = editor.querySelector(".fbk-edit-rmshot"), removeScreenshot = !!(rm && rm.checked);
        this.saveEdit(id, body, removeScreenshot);
      });
    }
    async saveEdit(id, body, removeScreenshot) {
      try {
        let r = await this.api(`/api/comments/${id}`, {
          method: "PUT",
          body: JSON.stringify({ body, removeScreenshot })
        });
        if (!r.ok) {
          let b = await r.json().catch(() => null);
          throw new Error(b && b.message || "HTTP " + r.status);
        }
        await this.fetchComments(), this.renderSidebar(), this.renderPins(), this.toast(t("toast.commentUpdated"), "success");
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(e.message || t("toast.failedToUpdateComment"), "error");
      }
    }
    // Inline edit for a comment's admin-defined field values — same swap-in-place pattern as
    // startEdit, but replaces .fbk-card-fields (or inserts one after .fbk-text when the card has
    // no values yet) with input controls instead of a textarea. Wired from the kebab menu's
    // "Edit fields" item (see toggleCardMenu).
    startEditFields(c) {
      let card = this.root && this.root.querySelector(`.fbk-card[data-id="${c.id}"]`);
      if (!card || card.querySelector(".fbk-card-fields-edit")) return;
      let textEl = card.querySelector(".fbk-text"), existingDl = card.querySelector(".fbk-card-fields"), existingWrapper = card.querySelector(".fbk-card-fields-wrapper");
      if (!existingDl && !existingWrapper && !textEl) return;
      let currentValues = {};
      (c.customFields || []).forEach((f) => {
        currentValues[f.key] = f.value;
      });
      let idPrefix = "ef-" + c.id, defs = this.commentFields;
      if (!defs || defs.length === 0) return;
      let editor = document.createElement("div");
      editor.className = "fbk-card-fields-edit", editor.innerHTML = `
        ${renderFieldInputs(defs, currentValues, idPrefix)}
        <div class="fbk-reply-row">
          <button type="button" class="fbk-mini fbk-fields-save">${t("fields.save")}</button>
          <button type="button" class="fbk-mini fbk-fields-cancel">${t("fields.cancel")}</button>
        </div>`, this.bindFieldValidation(editor, idPrefix), existingWrapper ? existingWrapper.replaceWith(editor) : existingDl ? existingDl.replaceWith(editor) : textEl.insertAdjacentElement("afterend", editor), editor.querySelector(".fbk-fields-cancel").addEventListener("click", () => {
        this.renderSidebar();
      }), editor.querySelector(".fbk-fields-save").addEventListener("click", () => {
        this.saveEditFields(c, editor, idPrefix);
      });
    }
    async saveEditFields(c, editor, idPrefix) {
      var _a2;
      let res = this.validateAndCollectFields(editor, idPrefix);
      if (res.hasError) return;
      let fieldsOut = res.fields || {}, saveBtn = editor.querySelector(".fbk-fields-save");
      saveBtn && (saveBtn.disabled = !0, saveBtn.textContent = t("menu.saving"));
      try {
        let r = await this.api(`/api/comments/${c.id}/fields`, {
          method: "PATCH",
          body: JSON.stringify({ customFields: fieldsOut })
        });
        if (!r.ok) {
          let b = await r.json().catch(() => null);
          throw new Error(b && b.message || "HTTP " + r.status);
        }
        let envelope = await r.json(), updated = (_a2 = envelope == null ? void 0 : envelope.data) != null ? _a2 : envelope, idx = this.comments.findIndex((x) => String(x.id) === String(c.id));
        idx !== -1 && updated && (this.comments[idx] = { ...this.comments[idx], ...updated }), this.renderSidebar(), this.toast(t("fields.saved"));
      } catch (e) {
        saveBtn && (saveBtn.disabled = !1, saveBtn.textContent = t("fields.save")), e.message !== "HTTP 401 Unauthorized" && this.toast(e.message || t("toast.updateFailed"), "error");
      }
    }
    // Inline edit for a single reply (own replies only) — same swap-body-for-a-textarea pattern
    // as the comment's own startEdit, scoped to one .fbk-reply row instead of the whole card.
    startEditReply(commentId, replyId) {
      let row = this.root && this.root.querySelector(`.fbk-reply[data-reply-id="${replyId}"]`);
      if (!row || row.querySelector(".fbk-edit")) return;
      let comment = (this.comments || []).find((x) => String(x.id) === String(commentId)), reply = comment && (comment.replies || []).find((r) => String(r.id) === String(replyId));
      if (!reply) return;
      let mainEl = row.querySelector(".fbk-reply-main"), actionsEl = row.querySelector(".fbk-reply-kebab");
      if (!mainEl) return;
      let editor = document.createElement("div");
      editor.className = "fbk-edit", editor.style.flex = "1", editor.innerHTML = `
        <textarea class="fbk-textarea fbk-reply-edit-body">${escapeHtml(reply.body || reply.text || "")}</textarea>
        <div class="fbk-reply-row">
          <button class="fbk-btn primary fbk-btn-fill fbk-edit-save">${t("card.save")}</button>
          <button class="fbk-mini fbk-edit-cancel">${t("toolbar.cancel")}</button>
        </div>`, mainEl.style.display = "none", actionsEl && (actionsEl.style.display = "none"), mainEl.insertAdjacentElement("afterend", editor);
      let ta = editor.querySelector(".fbk-reply-edit-body");
      ta.focus();
      let close = () => {
        editor.remove(), mainEl.style.display = "", actionsEl && (actionsEl.style.display = "");
      };
      editor.querySelector(".fbk-edit-cancel").addEventListener("click", close), editor.querySelector(".fbk-edit-save").addEventListener("click", () => {
        let body = ta.value.trim();
        if (!body) {
          this.toast(t("popover.commentCannotBeEmpty"), "error");
          return;
        }
        this.saveReplyEdit(replyId, body);
      });
    }
    async saveReplyEdit(replyId, body) {
      try {
        let r = await this.api(`/api/replies/${replyId}`, {
          method: "PUT",
          body: JSON.stringify({ body })
        });
        if (!r.ok) {
          let b = await r.json().catch(() => null);
          throw new Error(b && b.message || "HTTP " + r.status);
        }
        await this.fetchComments(), this.renderSidebar(), this.toast(t("toast.commentUpdated"), "success");
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(e.message || t("toast.failedToUpdateComment"), "error");
      }
    }
    // Delete confirmation for a single reply: an overlay covering that reply's own bubble — same
    // "on the thing itself, not inside a menu" treatment as confirmDeleteCard. The kebab menu is
    // already closed by the time this runs (see toggleReplyMenu's delete wiring).
    confirmDeleteReplyBubble(replyId) {
      let row = this.root && this.root.querySelector(`.fbk-reply[data-reply-id="${replyId}"]`);
      if (!row || row.querySelector(".fbk-card-delete-confirm")) return;
      let overlay = document.createElement("div");
      overlay.className = "fbk-card-delete-confirm", overlay.innerHTML = `<p class="fbk-card-delete-confirm-q">${t("card.deleteThisReply")}</p><div class="fbk-card-delete-confirm-actions"><button type="button" class="fbk-mini danger" data-c="yes">${t("card.confirmDelete")}</button><button type="button" class="fbk-mini" data-c="no">${t("toolbar.cancel")}</button></div>`, row.appendChild(overlay), overlay.querySelector('[data-c="yes"]').addEventListener("click", (e) => {
        e.stopPropagation(), this.deleteReply(replyId);
      }), overlay.querySelector('[data-c="no"]').addEventListener("click", (e) => {
        e.stopPropagation(), overlay.remove();
      });
    }
    async deleteReply(replyId) {
      try {
        let r = await this.api(`/api/replies/${replyId}`, { method: "DELETE" });
        if (!r.ok) {
          let b = await r.json().catch(() => null);
          throw new Error(b && b.message || "HTTP " + r.status);
        }
        await this.fetchComments(), this.renderSidebar(), this.renderPins(), this.toast(t("toast.deleted"));
      } catch (e) {
        e.message !== "HTTP 401 Unauthorized" && this.toast(e.message || t("toast.deleteFailed"), "error");
      }
    }
    // True when comment `c` was authored by the current logged-in user.
    /**
     * Completes `this.user` with the fields the card template needs (`id`, `isAdmin`, `isQuickAccess`)
     * when the session came from a host that only supplied a display name. One `GET /api/auth/me`,
     * in memory only for host-owned sessions — the host re-injects its own user object on every
     * activation, and persisting a wider profile than it chose to share would defeat that choice.
     */
    async hydrateIdentity() {
      if (!(!this.token || this.user && this.user.id))
        try {
          let r = await this.api("/api/auth/me");
          if (!r.ok) return;
          let env = await r.json().catch(() => null), me = env && env.data ? env.data : env;
          if (!me || !me.id) return;
          if (this.user = {
            ...this.user || {},
            id: String(me.id),
            displayName: this.user && this.user.displayName || me.displayName,
            isAdmin: !!me.isAdmin,
            isQuickAccess: !!me.isQuickAccess,
            language: this.user && this.user.language || me.language
          }, !this.authOwnedByHost)
            try {
              localStorage.setItem("pointer_user", JSON.stringify(this.user));
            } catch {
            }
        } catch {
        }
    }
    isMine(c) {
      let uid = this.user && this.user.id;
      return uid ? String(c.authorId || "").toLowerCase() === String(uid).toLowerCase() : !1;
    }
    // Distinct comment authors in the current project list.
    distinctAuthors(comments) {
      let seen = /* @__PURE__ */ new Set(), out = [];
      for (let c of comments) {
        let id = String(c.authorId || "");
        id && !seen.has(id) && (seen.add(id), out.push({ id, name: c.authorName || id }));
      }
      return out;
    }
    // Apply the "who" filters in priority order: Mine wins; else a chosen author.
    scopeByWho(comments) {
      return this.mineOnly ? comments.filter((c) => this.isMine(c)) : this.authorFilter ? comments.filter((c) => String(c.authorId || "") === this.authorFilter) : comments;
    }
    // --- Sidebar render ------------------------------------------------------
    renderSidebar() {
      var _a2, _b, _c;
      let all = this.pageComments(), canMine = !!(this.user && this.user.id);
      canMine || (this.mineOnly = !1);
      let authors = this.distinctAuthors(all);
      this.authorFilter && !authors.some((a) => a.id === this.authorFilter) && (this.authorFilter = null);
      let scoped = this.scopeByWho(all), counts = {
        // "All" means active (non-archived, non-completed); those move out to their own chips.
        all: scoped.filter((c) => c.status !== "archived" && c.status !== "applied").length,
        open: scoped.filter((c) => c.status === "open").length,
        "pending-apply": scoped.filter((c) => c.status === "pending-apply").length,
        applied: scoped.filter((c) => c.status === "applied").length,
        archived: scoped.filter((c) => c.status === "archived").length
      }, countEl = this.root.querySelector("#fbk-count");
      countEl && (countEl.textContent = String(all.filter((c) => c.status !== "archived" && c.status !== "applied").length));
      let filtersEl = this.root.querySelector("#fbk-filters");
      if (filtersEl) {
        let activeFilters = catalogToFilters(), fixedEnvLabel = this.hasFixedEnvironment || !this.showEnvironmentSelector ? this.envDisplayLabel(this.environmentAttr || ENV_NAME[this.environmentInt] || "staging") : null, envValue = this.viewAllEnvironments ? "all" : (this.environmentAttr || ENV_NAME[this.environmentInt] || "staging").toLowerCase(), whoRow = (canMine ? TPL.mineToggle(this.mineOnly) : "") + (authors.length > 1 && !this.mineOnly ? TPL.authorFilter(authors, this.authorFilter || "") : ""), whatRow = TPL.statusFilterSelect(activeFilters, this.statusFilter, counts) + TPL.envFilterSelect(fixedEnvLabel, envValue);
        filtersEl.innerHTML = `<div class="fbk-filters-row">${whoRow}</div><div class="fbk-filters-row">${whatRow}</div>`;
        let mineBtn = filtersEl.querySelector("#fbk-mine-toggle");
        mineBtn && mineBtn.addEventListener("click", () => {
          this.mineOnly = !this.mineOnly, this.renderSidebar(), this.renderPins();
        });
        let statusSel = filtersEl.querySelector("#fbk-status-filter");
        statusSel && statusSel.addEventListener("change", () => {
          this.statusFilter = statusSel.value, this.renderSidebar();
        });
        let envSel = filtersEl.querySelector("#fbk-env");
        envSel && envSel.addEventListener("change", () => this.setEnvironment(envSel.value));
        let authorSel = filtersEl.querySelector("#fbk-author-filter");
        authorSel && authorSel.addEventListener("change", () => {
          this.authorFilter = authorSel.value || null, this.renderSidebar(), this.renderPins();
        });
      }
      let list = this.root.querySelector("#fbk-list");
      if (!list) return;
      let awaitingMyVerification = (c) => c.status === "applied" && !c.verifiedAt && this.isMine(c), shown = this.statusFilter === "all" ? scoped.filter((c) => c.status !== "archived" && (c.status !== "applied" || awaitingMyVerification(c))) : scoped.filter((c) => c.status === this.statusFilter);
      if (!scoped.length) {
        list.innerHTML = TPL.empty(this.mineOnly ? t("sidebar.noOwnComments") : t("sidebar.noCommentsYet"));
        return;
      }
      if (!shown.length) {
        let filterLabel = ((_a2 = catalogToFilters().find((f) => f.key === this.statusFilter)) != null ? _a2 : { label: this.statusFilter }).label;
        list.innerHTML = TPL.empty(t("sidebar.noFilteredComments", {
          label: filterLabel.toLowerCase(),
          suffix: this.mineOnly ? t("sidebar.ofYours") : ""
        }));
        return;
      }
      let isQuickAccess = !!((_b = this.user) != null && _b.isQuickAccess), myId = (_c = this.user) != null && _c.id ? String(this.user.id).toLowerCase() : null;
      list.innerHTML = shown.map((c, i) => (c._mine = this.isMine(c), c._canVerify = c._mine || !!(this.user && this.user.isAdmin), (c.replies || []).forEach((r) => {
        r._mine = !r.isAi && !!(myId && r.authorId && String(r.authorId).toLowerCase() === myId);
      }), TPL.card(c, i, isQuickAccess))).join(""), list.querySelectorAll(".fbk-card").forEach((card) => {
        let id = card.dataset.id, textEl = card.querySelector(".fbk-text"), readMoreBtn = card.querySelector('[data-act="toggle-read-more"]');
        textEl && readMoreBtn && id && (this.expandedCommentIds.has(id) ? (textEl.classList.remove("fbk-text-clamped"), textEl.appendChild(readMoreBtn), readMoreBtn.classList.remove("fbk-hidden"), readMoreBtn.textContent = t("card.readLess")) : (textEl.classList.add("fbk-text-clamped"), textEl.prepend(readMoreBtn), readMoreBtn.textContent = "… " + t("card.readMore"), textEl.scrollHeight > textEl.clientHeight + 1 && readMoreBtn.classList.remove("fbk-hidden")), readMoreBtn.addEventListener("click", (e) => {
          e.stopPropagation(), textEl.classList.toggle("fbk-text-clamped") ? (this.expandedCommentIds.delete(id), textEl.prepend(readMoreBtn), readMoreBtn.textContent = "… " + t("card.readMore")) : (this.expandedCommentIds.add(id), textEl.appendChild(readMoreBtn), readMoreBtn.textContent = t("card.readLess"));
        }));
      }), list.querySelectorAll(".fbk-card-field-val").forEach((valEl) => {
        let textEl = valEl.querySelector(".fbk-card-field-val-text"), moreBtn = valEl.querySelector('[data-act="toggle-field-more"]');
        textEl && moreBtn && (valEl.classList.contains("is-expanded") ? (moreBtn.classList.remove("fbk-hidden"), moreBtn.textContent = t("fields.seeLess")) : textEl.scrollWidth > textEl.clientWidth + 1 && moreBtn.classList.remove("fbk-hidden"), moreBtn.addEventListener("click", (e) => {
          e.stopPropagation();
          let expanded = valEl.classList.toggle("is-expanded");
          moreBtn.textContent = expanded ? t("fields.seeLess") : t("fields.seeMore");
        }));
      }), list.querySelectorAll('[data-act="edit-fields"]').forEach((b) => b.addEventListener("click", (e) => {
        e.stopPropagation();
        let c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        c && this.startEditFields(c);
      })), list.querySelectorAll('[data-act="apply"]').forEach((b) => b.addEventListener("click", () => {
        let c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        c && c.status !== "applied" && this.toggleApply(c);
      })), list.querySelectorAll('[data-act="card-menu"]').forEach((b) => b.addEventListener("click", (e) => {
        e.stopPropagation();
        let c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        c && this.toggleCardMenu(b, c);
      })), list.querySelectorAll('[data-act="flash-pin"]').forEach((b) => b.addEventListener("click", (e) => {
        e.stopPropagation();
        let id = b.dataset.id;
        id && this.flashPin(id);
      })), list.querySelectorAll('[data-act="archive"]').forEach((b) => b.addEventListener("click", () => {
        let c = this.comments.find((x) => String(x.id) === String(b.dataset.id));
        c && this.setStatus(c, "archived", t("toast.archivedMsg"));
      })), list.querySelectorAll('[data-act="reply-toggle"]').forEach((btn) => btn.addEventListener("click", () => {
        let row = btn.closest(".fbk-reply-row"), inp = row == null ? void 0 : row.querySelector(".fbk-reply-input");
        inp && (btn.classList.add("fbk-hidden"), inp.classList.remove("fbk-hidden"), inp.focus());
      })), list.querySelectorAll(".fbk-reply-input").forEach((inp) => {
        let collapse = () => {
          let row = inp.closest(".fbk-reply-row"), btn = row == null ? void 0 : row.querySelector('[data-act="reply-toggle"]');
          inp.classList.add("fbk-hidden"), btn == null || btn.classList.remove("fbk-hidden");
        };
        inp.addEventListener("keydown", (e) => {
          e.key === "Enter" && !e.shiftKey ? (e.preventDefault(), inp.value.trim() && (this.addReply(inp.dataset.id, inp.value.trim()), inp.value = "")) : e.key === "Escape" && (inp.value = "", collapse());
        }), inp.addEventListener("blur", () => {
          inp.value.trim() || collapse();
        });
      }), list.querySelectorAll('[data-act="reply-menu"]').forEach((b) => b.addEventListener("click", (e) => {
        e.stopPropagation();
        let comment = this.comments.find((x) => String(x.id) === String(b.dataset.commentId)), reply = comment && (comment.replies || []).find((r) => String(r.id) === String(b.dataset.replyId));
        comment && reply && this.toggleReplyMenu(b, comment, reply);
      })), list.querySelectorAll(".fbk-reply:not(.fbk-reply-ai) .fbk-reply-main").forEach((el) => {
        el.addEventListener("click", () => {
          var _a3;
          return (_a3 = el.closest(".fbk-reply")) == null ? void 0 : _a3.classList.toggle("expanded");
        });
      }), list.querySelectorAll('[data-act="verify-ok"]').forEach((b) => b.addEventListener("click", () => {
        let id = b.dataset.id;
        id && this.apiVerify(id, !0);
      })), list.querySelectorAll('[data-act="verify-reject"]').forEach((b) => b.addEventListener("click", () => {
        let id = b.dataset.id;
        if (!id) return;
        let box = list.querySelector(`#fbk-verify-box-${id}`);
        if (box) {
          let show = box.classList.contains("fbk-hidden");
          box.classList.toggle("fbk-hidden", !show);
          let input = box.querySelector(`#fbk-verify-note-${id}`);
          input && show && input.focus();
        }
      })), list.querySelectorAll('[data-act="verify-cancel"]').forEach((b) => b.addEventListener("click", () => {
        let id = b.dataset.id;
        if (!id) return;
        let box = list.querySelector(`#fbk-verify-box-${id}`);
        if (box) {
          box.classList.add("fbk-hidden");
          let input = box.querySelector(`#fbk-verify-note-${id}`);
          input && (input.value = "");
        }
      })), list.querySelectorAll('[data-act="verify-submit"]').forEach((b) => b.addEventListener("click", () => {
        var _a3;
        let id = b.dataset.id;
        if (!id) return;
        let input = list.querySelector(`#fbk-verify-note-${id}`), note = (_a3 = input == null ? void 0 : input.value) == null ? void 0 : _a3.trim();
        if (!note) {
          input == null || input.focus(), this.toast(t("toast.pleaseProvideNoteNotFixed"), "error");
          return;
        }
        this.apiVerify(id, !1, note);
      })), list.querySelectorAll(".fbk-verify-note-input").forEach((inp) => inp.addEventListener("keydown", (e) => {
        if (e.key === "Enter") {
          let id = inp.id.replace("fbk-verify-note-", ""), note = inp.value.trim();
          if (!note) {
            inp.focus(), this.toast(t("toast.pleaseProvideNoteNotFixed"), "error");
            return;
          }
          this.apiVerify(id, !1, note);
        }
      }));
    }
    schedulePinsRetries() {
      this.clearPinsRetries(), [300, 1e3, 2500].forEach((ms) => {
        let id = window.setTimeout(() => {
          this.isConnected && this.renderPins();
        }, ms);
        this._pinsRetryTimers.push(id);
      });
    }
    clearPinsRetries() {
      this._pinsRetryTimers.forEach((id) => clearTimeout(id)), this._pinsRetryTimers = [];
    }
    handleLocationChange() {
      if (typeof window == "undefined") return;
      let cur = window.location.href;
      cur !== this._lastUrl && (this._lastUrl = cur, this.renderPins(), this.schedulePinsRetries());
    }
    // --- Pins ----------------------------------------------------------------
    renderPins() {
      let wrap = this.root && this.root.querySelector("#fbk-pins-layer");
      if (!wrap) return;
      let all = this.pageComments().filter((c) => c.status !== "archived" && c.status !== "applied"), here = this.scopeByWho(all), items = [];
      here.forEach((c) => {
        if (!isCurrentPage(c)) return;
        let el = matchElement(c);
        if (!el) return;
        let rect = el.getBoundingClientRect();
        rect.width === 0 && rect.height === 0 || items.push({ c, x: rect.left, y: rect.top });
      });
      let groups = [];
      for (let item of items) {
        let group = groups.find((g) => Math.hypot(item.x - g[0].x, item.y - g[0].y) <= PIN_CLUSTER_RADIUS);
        group ? group.push(item) : groups.push([item]);
      }
      wrap.innerHTML = groups.map((g) => {
        let cx = g.reduce((sum, it) => sum + it.x, 0) / g.length, cy = g.reduce((sum, it) => sum + it.y, 0) / g.length, clampedCx = cx >= 0 && cx < PIN_HALF_WIDTH ? PIN_HALF_WIDTH : cx <= window.innerWidth && cx > window.innerWidth - PIN_HALF_WIDTH ? window.innerWidth - PIN_HALF_WIDTH : cx, clampedCy = cy >= 0 && cy < PIN_HEIGHT ? PIN_HEIGHT : cy, rect = { left: clampedCx, top: clampedCy };
        if (g.length > 1) return TPL.pinCluster(g.map((it) => it.c), rect);
        let tipSide = clampedCy - PIN_HEIGHT - PIN_TOOLTIP_HEIGHT_ESTIMATE < 0 ? "bottom" : "top", tooltipHalf = PIN_TOOLTIP_WIDTH / 2, tipAlign = clampedCx - tooltipHalf < PIN_TOOLTIP_EDGE_MARGIN ? "start" : clampedCx + tooltipHalf > window.innerWidth - PIN_TOOLTIP_EDGE_MARGIN ? "end" : "center";
        return TPL.pin(g[0].c, rect, String(g[0].c.id) === String(this._newPinId), tipSide, tipAlign);
      }).join(""), applyDataPosition(wrap, ".fbk-pin-wrapper"), wrap.querySelectorAll(".fbk-pin-cluster").forEach((btn) => btn.addEventListener("click", (e) => {
        e.stopPropagation();
        let wrapper = btn.closest(".fbk-pin-wrapper"), clustered = ((wrapper == null ? void 0 : wrapper.dataset.ids) || "").split(",").filter(Boolean).map((id) => this.comments.find((c) => String(c.id) === id)).filter((c) => !!c);
        this.toggleClusterMenu(btn, clustered);
      })), wrap.querySelectorAll(".fbk-pin").forEach((btn) => {
        btn.classList.contains("fbk-pin-cluster") || btn.addEventListener("click", () => {
          let wrapper = btn.closest(".fbk-pin-wrapper"), id = wrapper == null ? void 0 : wrapper.dataset.id;
          id && this.highlightCommentCard(id);
        });
      });
    }
    // Opens the sidebar (if needed) and scrolls/highlights one comment's card — shared by a
    // standalone pin's click and the expanded cluster menu's item clicks.
    highlightCommentCard(id) {
      this.toggleSidebar(!0), this.renderSidebar(), setTimeout(() => {
        let card = this.root.querySelector(`.fbk-card[data-id="${id}"]`);
        card && (card.scrollIntoView({ behavior: "smooth", block: "center" }), card.classList.add("highlight"), setTimeout(() => card.classList.remove("highlight"), 2e3));
      }, 100);
    }
    // Reverse of highlightCommentCard: clicking a card's id badge flashes that comment's pin on
    // the page instead. A standalone pin is `[data-id]`; one folded into a collision cluster (see
    // renderPins' clustering) only carries the comma-joined `data-ids` on its wrapper, so both are
    // checked. When the comment belongs to a DIFFERENT page (see isCurrentPage) there is no pin to
    // flash here at all — renderPins() never rendered one — so follow the comment to its own page
    // instead of reporting a dead end; flashing the pin there too is deliberately skipped (would
    // need to survive a full navigation/reload, for little payoff over just landing on the page).
    // Once we know we're on the RIGHT page, a still-missing pin (doFlashPin's own check) means the
    // target element itself can't be found right now — applied/archived, removed, or (a common
    // case) a portal/overlay node (a menu, tooltip, modal) that only exists in the DOM while open —
    // never "wrong page" at that point, so say so with a different message than the redirect above.
    flashPin(id) {
      let comment = this.comments.find((x) => String(x.id) === String(id)), pageUrl = comment && comment.element && comment.element.pageUrl;
      if (comment && pageUrl && !isCurrentPage(comment)) {
        window.location.href = pageUrl;
        return;
      }
      let sidebar = this.root.querySelector("#fbk-sidebar");
      sidebar == null || sidebar.classList.add("fbk-peek-hide");
      let target = comment && matchElement(comment);
      target && target.scrollIntoView({ block: "center" }), setTimeout(() => this.doFlashPin(id), target ? 150 : 0);
    }
    doFlashPin(id) {
      let restoreSidebar = () => {
        var _a2;
        return (_a2 = this.root.querySelector("#fbk-sidebar")) == null ? void 0 : _a2.classList.remove("fbk-peek-hide");
      }, layer = this.root.querySelector("#fbk-pins-layer");
      if (!layer) {
        restoreSidebar();
        return;
      }
      let wrapper = layer.querySelector(`.fbk-pin-wrapper[data-id="${id}"]`);
      wrapper || (wrapper = Array.from(layer.querySelectorAll(".fbk-pin-wrapper[data-ids]")).find((w) => (w.dataset.ids || "").split(",").includes(String(id))) || null);
      let pin = wrapper == null ? void 0 : wrapper.querySelector(".fbk-pin");
      if (!pin) {
        this.toast(t("toast.pinElementNotFound")), restoreSidebar();
        return;
      }
      pin.classList.remove("fbk-pin-flash"), pin.offsetWidth, pin.classList.add("fbk-pin-flash"), setTimeout(() => {
        pin.classList.remove("fbk-pin-flash"), restoreSidebar();
      }, 1600);
    }
    // --- Pin cluster menu ------------------------------------------------------
    toggleClusterMenu(btn, comments) {
      let host = this.root.querySelector("#fbk-menu-host");
      if (!host) return;
      if (host.querySelector("#fbk-pin-cluster-menu")) {
        this.closeClusterMenu();
        return;
      }
      if (this.closeUserMenu(), this.closeUpdatesMenu(), this.closeCardMenu(), comments.length === 0) return;
      host.innerHTML = TPL.pinClusterMenu(comments);
      let menu = host.querySelector("#fbk-pin-cluster-menu");
      if (!menu) return;
      btn.setAttribute("aria-expanded", "true");
      let r = btn.getBoundingClientRect(), menuHeight = menu.offsetHeight, spaceBelow = window.innerHeight - r.bottom;
      spaceBelow < menuHeight + 6 && r.top > spaceBelow ? (menu.style.top = "auto", menu.style.bottom = `${Math.max(8, Math.round(window.innerHeight - r.top + 6))}px`) : (menu.style.bottom = "auto", menu.style.top = `${Math.round(r.bottom + 6)}px`), menu.style.left = `${Math.max(8, Math.min(window.innerWidth - 248, Math.round(r.left - 100)))}px`, menu.querySelectorAll(".fbk-pin-cluster-item").forEach((item) => {
        item.addEventListener("click", () => {
          let id = item.dataset.id;
          this.closeClusterMenu(), id && this.highlightCommentCard(id);
        });
      }), this._clusterMenuClose = (e) => {
        let path = e.composedPath();
        !path.includes(menu) && !path.includes(btn) && this.closeClusterMenu();
      }, setTimeout(() => {
        this._clusterMenuClose && document.addEventListener("click", this._clusterMenuClose, !0);
      }, 0);
    }
    closeClusterMenu() {
      let host = this.root.querySelector("#fbk-menu-host");
      host && host.querySelector("#fbk-pin-cluster-menu") && (host.innerHTML = "", this.root.querySelectorAll('.fbk-pin-cluster[aria-expanded="true"]').forEach((b) => b.setAttribute("aria-expanded", "false"))), this._clusterMenuClose && (document.removeEventListener("click", this._clusterMenuClose, !0), this._clusterMenuClose = null);
    }
    // --- Card actions menu (kebab menu: private/public, edit, delete) --------
    toggleCardMenu(btn, c) {
      var _a2, _b;
      let host = this.root.querySelector("#fbk-menu-host");
      if (!host) return;
      if (host.querySelector("#fbk-card-menu")) {
        this.closeCardMenu();
        return;
      }
      this.closeUserMenu(), this.closeUpdatesMenu(), this.closeClusterMenu();
      let canEditFields = this.commentFields.length > 0 && !!(c._mine || (_a2 = this.user) != null && _a2.isAdmin);
      host.innerHTML = TPL.cardMenu(c, !!((_b = this.user) != null && _b.isQuickAccess), canEditFields);
      let menu = host.querySelector("#fbk-card-menu");
      if (!menu) return;
      btn.setAttribute("aria-expanded", "true");
      let r = btn.getBoundingClientRect(), menuHeight = menu.offsetHeight, spaceBelow = window.innerHeight - r.bottom;
      spaceBelow < menuHeight + 6 && r.top > spaceBelow ? (menu.style.top = "auto", menu.style.bottom = `${Math.max(8, Math.round(window.innerHeight - r.top + 6))}px`) : (menu.style.bottom = "auto", menu.style.top = `${Math.round(r.bottom + 6)}px`), menu.style.right = `${Math.max(8, Math.round(window.innerWidth - r.right))}px`;
      let copyPromptBtn = menu.querySelector('[data-menu-act="copy-apply-prompt"]');
      copyPromptBtn && copyPromptBtn.addEventListener("click", () => {
        this.closeCardMenu(), this.copyApplyPrompt(c);
      });
      let completeBtn = menu.querySelector('[data-menu-act="complete"]');
      completeBtn && completeBtn.addEventListener("click", () => {
        this.closeCardMenu(), c.status !== "applied" && this.markCompleted(c);
      });
      let reopenBtn = menu.querySelector('[data-menu-act="reopen"]');
      reopenBtn && reopenBtn.addEventListener("click", () => {
        this.closeCardMenu(), this.setStatus(c, "open", t("toast.reopenedMsg"));
      });
      let visBtn = menu.querySelector('[data-menu-act="visibility"]');
      visBtn && visBtn.addEventListener("click", () => {
        this.closeCardMenu(), this.setVisibility(c, visBtn.dataset.private === "true");
      });
      let editBtn = menu.querySelector('[data-menu-act="edit"]');
      editBtn && editBtn.addEventListener("click", () => {
        this.closeCardMenu(), this.startEdit(String(c.id));
      });
      let editFieldsBtn = menu.querySelector('[data-menu-act="edit-fields"]');
      editFieldsBtn && editFieldsBtn.addEventListener("click", () => {
        this.closeCardMenu(), this.startEditFields(c);
      });
      let delBtn = menu.querySelector('[data-menu-act="delete"]');
      delBtn && delBtn.addEventListener("click", () => {
        this.closeCardMenu(), this.confirmDeleteCard(String(c.id));
      }), this._cardMenuClose = (e) => {
        let path = e.composedPath();
        !path.includes(menu) && !path.includes(btn) && this.closeCardMenu();
      }, setTimeout(() => {
        this._cardMenuClose && document.addEventListener("click", this._cardMenuClose, !0);
      }, 0);
    }
    closeCardMenu() {
      let host = this.root.querySelector("#fbk-menu-host");
      host && host.querySelector("#fbk-card-menu") && (host.innerHTML = "", this.root.querySelectorAll('.fbk-card-kebab[aria-expanded="true"], .fbk-reply-kebab[aria-expanded="true"]').forEach((b) => b.setAttribute("aria-expanded", "false"))), this._cardMenuClose && (document.removeEventListener("click", this._cardMenuClose, !0), this._cardMenuClose = null);
    }
    // Kebab menu for a single reply (see replyMenu template) — shares #fbk-card-menu/closeCardMenu
    // with the comment's own kebab (only one such menu is ever open at once), just anchored under
    // the reply's own trigger instead and built from TPL.replyMenu instead of TPL.cardMenu.
    toggleReplyMenu(btn, c, r) {
      let host = this.root.querySelector("#fbk-menu-host");
      if (!host) return;
      if (host.querySelector("#fbk-card-menu")) {
        this.closeCardMenu();
        return;
      }
      this.closeUserMenu(), this.closeUpdatesMenu(), this.closeClusterMenu(), host.innerHTML = TPL.replyMenu(c, r);
      let menu = host.querySelector("#fbk-card-menu");
      if (!menu) return;
      btn.setAttribute("aria-expanded", "true");
      let rect = btn.getBoundingClientRect(), menuHeight = menu.offsetHeight, spaceBelow = window.innerHeight - rect.bottom;
      spaceBelow < menuHeight + 6 && rect.top > spaceBelow ? (menu.style.top = "auto", menu.style.bottom = `${Math.max(8, Math.round(window.innerHeight - rect.top + 6))}px`) : (menu.style.bottom = "auto", menu.style.top = `${Math.round(rect.bottom + 6)}px`), menu.style.right = `${Math.max(8, Math.round(window.innerWidth - rect.right))}px`;
      let copyPromptBtn = menu.querySelector('[data-menu-act="copy-apply-prompt"]');
      copyPromptBtn && copyPromptBtn.addEventListener("click", () => {
        this.closeCardMenu(), this.copyApplyPrompt(c, !0);
      });
      let editBtn = menu.querySelector('[data-menu-act="edit"]');
      editBtn && editBtn.addEventListener("click", () => {
        this.closeCardMenu(), this.startEditReply(String(c.id), String(r.id));
      });
      let delBtn = menu.querySelector('[data-menu-act="delete"]');
      delBtn && delBtn.addEventListener("click", () => {
        this.closeCardMenu(), this.confirmDeleteReplyBubble(String(r.id));
      }), this._cardMenuClose = (e) => {
        let path = e.composedPath();
        !path.includes(menu) && !path.includes(btn) && this.closeCardMenu();
      }, setTimeout(() => {
        this._cardMenuClose && document.addEventListener("click", this._cardMenuClose, !0);
      }, 0);
    }
    // --- Single-item apply prompt ("Copy apply prompt" in the kebab menu) ----
    // A short, human-style instruction — not a technical spec — naming just the one comment to
    // apply. No comment/reply text is embedded (so there's nothing here that needs fencing as
    // untrusted input): the agent already has the apply skill installed and pulls the real content
    // itself via `get --json`, exactly as it would for any item from the normal apply queue.
    // `isFollowUp`: true when copied from a REPLY's own kebab (see toggleReplyMenu) rather than the
    // comment's — flags that there's more context than just the original comment body to read.
    buildSingleItemApplyPrompt(c, isFollowUp = !1) {
      let brand = getBrandName(), followUp = isFollowUp ? " (there is a follow-up reply on it — read all replies, not just the original comment)" : "";
      return `Apply ${brand} comment #${c.id} in this repo — run \`npx pointer-feedback get ${c.id} --json\` to see it${followUp}, then follow the apply skill to fix it and mark it done.`;
    }
    async copyApplyPrompt(c, isFollowUp = !1) {
      try {
        await navigator.clipboard.writeText(this.buildSingleItemApplyPrompt(c, isFollowUp)), this.toast(t("toast.applyPromptCopied"));
      } catch {
        this.toast(t("toast.copyFailed"), "error");
      }
    }
    // --- Toast ---------------------------------------------------------------
    // `type` keeps every existing call site's convention ('', 'success', 'error') working
    // unchanged — '' and 'success' both render the success (green check) variant; 'error' maps to
    // the danger (red) variant. 'warn' (amber) is available for a future call site; nothing emits
    // it yet. `actionLabel`/`onAction` add an optional inline button (e.g. "Undo", "Retry") —
    // `onAction` runs, then the toast dismisses either way.
    toast(msg, type = "", actionLabel, onAction) {
      var _a2, _b;
      let variant = type === "error" ? "danger" : type === "warn" ? "warn" : "success", host = this.ensureToastContainer(), wrap = document.createElement("div");
      wrap.innerHTML = TPL.toast(variant, msg, actionLabel);
      let el = wrap.firstElementChild;
      host.appendChild(el);
      let dismissed = !1, dismiss = () => {
        dismissed || (dismissed = !0, el.classList.add("dismissing"), setTimeout(() => el.remove(), 160));
      };
      (_a2 = el.querySelector(".fbk-toast-close")) == null || _a2.addEventListener("click", dismiss), actionLabel && ((_b = el.querySelector(".fbk-toast-action")) == null || _b.addEventListener("click", () => {
        onAction == null || onAction(), dismiss();
      }));
      let duration = Math.min(6e3, Math.max(2200, msg.length * 50));
      setTimeout(dismiss, duration);
    }
    // Toasts stack in their own fixed container (see _toast.scss) rather than as loose siblings —
    // otherwise two toasts shown close together would render on top of each other. Created lazily
    // and reused; renderChrome()'s full innerHTML swap can wipe it (same as any other overlay it
    // doesn't own), which only matters if a re-render happens to land inside a toast's ~2s life.
    ensureToastContainer() {
      let host = this.root.querySelector("#fbk-toast-container");
      return host || (host = document.createElement("div"), host.id = "fbk-toast-container", host.className = "fbk-toast-container", host.setAttribute("role", "region"), host.setAttribute("aria-label", t("toast.notifications")), host.setAttribute("aria-live", "polite"), this.root.appendChild(host)), host;
    }
  };
  /**
   * Module-level, not per-instance: "once per page load" has to hold even if the host page mounts
   * two widgets, which is exactly when a duplicate beacon would be least expected.
   */
  _PointerFeedback._buildShaReported = !1;
  var PointerFeedback = _PointerFeedback;

  // src/index.ts
  window.customElements && window.customElements.get("pointer-feedback") || customElements.define("pointer-feedback", PointerFeedback);
})();
