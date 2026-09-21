// Static-UI-text translation (English/Arabic). Deliberately text-only — the shadow UI stays
// LTR regardless of language (see _base.scss's `:host { direction: ltr }`); this only swaps the
// strings baked into TPL.* templates and the handful of dynamically-set title/aria-label/toast
// strings in element.ts. Never translates: comment/reply body text, author/display names, project
// names, the brand name, predefined-prompt text, server-provided status-catalog labels (an admin
// can rename these per project, so the widget cannot know their language), payload-flag names,
// commit SHAs/URLs, keyboard-shortcut key labels, file paths.

export type Lang = 'en' | 'ar';

let currentLang: Lang = 'en';

/** Normalizes anything other than exactly 'ar' to 'en'. Returns whether it actually changed. */
export function setLang(lang: string | null | undefined): boolean {
  const next: Lang = lang === 'ar' ? 'ar' : 'en';
  if (next === currentLang) return false;
  currentLang = next;
  return true;
}

export function getLang(): Lang {
  return currentLang;
}

// --- Comment-text language detection ------------------------------------
// Detects the language of a COMMENT's own text (not the widget's UI language — a user can write
// Arabic in an English-language widget). Deterministic, offline, no dependencies: Unicode script
// ranges plus a handful of letters that distinguish scripts shared by several languages. Returns
// a confident BCP-47 primary tag, or 'unknown' when the rules aren't sure — callers should only
// spend a real detection pass (e.g. an LLM call) on 'unknown', never on a returned tag.
const ARABIC_URDU_ONLY = /[ٹڈڑںےھ]/;
const ARABIC_PASHTO_ONLY = /[ټډړږښڼ]/;
const ARABIC_PERSIAN_ONLY = /[پچژگ]/;
// ك / ي (Arabic keyboard) vs ک / ی (Persian keyboard) — the Persian forms alone aren't
// conclusive, since many Arabic writers' input methods/fonts normalize to them too.
const ARABIC_PERSIAN_KEYBOARD = /[کی]/;
const ARABIC_SCRIPT = /[؀-ۿݐ-ݿࢠ-ࣿﭐ-﷿ﹰ-﻿]/;
const HEBREW_SCRIPT = /[֐-׿]/;
const HANGUL_SCRIPT = /[가-힯ᄀ-ᇿ]/;
const KANA_SCRIPT = /[぀-ヿ]/;
const HAN_SCRIPT = /[一-鿿]/;
const CYRILLIC_SCRIPT = /[Ѐ-ӿ]/;
const CYRILLIC_UKRAINIAN_ONLY = /[їєґ]/;
const LETTER_RE = /\p{L}/gu;
// Any LETTER outside ASCII (é ñ ü ç …) — punctuation like “ ” — is deliberately ignored so
// smart quotes / dashes in an English comment don't demote it to 'unknown'.
const NON_ASCII_LETTER_RE = /(?![\x00-\x7F])\p{L}/u;
const ENGLISH_STOPWORDS = new Set([
  'the', 'and', 'this', 'that', 'should', 'with', 'when', 'please', 'button', 'click', 'text',
  'page', 'not', 'but', 'from', 'are', 'was', 'have', 'has', 'will', 'can',
]);

export function detectTextLanguage(text: string): string {
  const stripped = (text || '')
    .replace(/`[^`]*`/g, ' ')
    .replace(/```[\s\S]*?```/g, ' ')
    .replace(/https?:\/\/\S+/gi, ' ')
    .replace(/\d+/g, ' ');
  const letters = stripped.match(LETTER_RE) || [];
  if (letters.length < 20) return 'unknown';

  if (ARABIC_SCRIPT.test(stripped)) {
    if (ARABIC_URDU_ONLY.test(stripped)) return 'ur';
    if (ARABIC_PASHTO_ONLY.test(stripped)) return 'ps';
    if (ARABIC_PERSIAN_ONLY.test(stripped)) return 'fa';
    if (ARABIC_PERSIAN_KEYBOARD.test(stripped)) return 'unknown';
    return 'ar';
  }
  if (HEBREW_SCRIPT.test(stripped)) return 'he';
  if (HANGUL_SCRIPT.test(stripped)) return 'ko';
  if (KANA_SCRIPT.test(stripped)) return 'ja';
  if (HAN_SCRIPT.test(stripped)) return 'zh';
  if (CYRILLIC_SCRIPT.test(stripped)) {
    return CYRILLIC_UKRAINIAN_ONLY.test(stripped) ? 'uk' : 'unknown';
  }

  if (NON_ASCII_LETTER_RE.test(stripped)) return 'unknown';
  const words = stripped.toLowerCase().match(/[a-z]+/g) || [];
  const matched = new Set(words.filter((w) => ENGLISH_STOPWORDS.has(w)));
  return matched.size >= 3 ? 'en' : 'unknown';
}

// Chrome's on-device LanguageDetector API, when present: preferred over the rule-based detector
// above for its accuracy, but strictly optional and never allowed to slow down or block posting a
// comment — guarded by a feature check, an availability check, a short race timeout, and a
// confidence floor, with any failure (including the timeout) falling back to the caller's own
// rule-based result.
interface ChromeLanguageDetection { detectedLanguage: string; confidence: number }
interface ChromeLanguageDetector { detect(text: string): Promise<ChromeLanguageDetection[]> }
interface ChromeLanguageDetectorCtor {
  availability(): Promise<'unavailable' | 'downloadable' | 'downloading' | 'available'>;
  create(): Promise<ChromeLanguageDetector>;
}

export async function detectTextLanguageAsync(text: string): Promise<string> {
  const fallback = detectTextLanguage(text);
  try {
    const ctor = (self as unknown as { LanguageDetector?: ChromeLanguageDetectorCtor }).LanguageDetector;
    if (!ctor) return fallback;
    const availability = await ctor.availability();
    if (availability !== 'available') return fallback;
    const detector = await ctor.create();
    const timeout = new Promise<null>((resolve) => setTimeout(() => resolve(null), 300));
    const results = await Promise.race([detector.detect(text), timeout]);
    if (!results || !results.length) return fallback;
    const best = results[0];
    if (best.confidence >= 0.8 && best.detectedLanguage) return best.detectedLanguage;
    return fallback;
  } catch {
    return fallback;
  }
}

/** `{name}`-style placeholder substitution — used for the handful of strings with dynamic parts. */
export function t(key: string, vars?: Record<string, string | number>): string {
  const raw = STRINGS[currentLang][key] ?? STRINGS.en[key] ?? key;
  if (!vars) return raw;
  return raw.replace(/\{(\w+)\}/g, (_, k) => (k in vars ? String(vars[k]) : `{${k}}`));
}

const STRINGS: Record<Lang, Record<string, string>> = {
  en: {
    // --- auth (login/signup modal) ---
    'auth.leaveFeedbackOn': 'Leave feedback on',
    'auth.skipForNow': 'Skip for now',
    'auth.email': 'Email',
    'auth.password': 'Password',
    'auth.signIn': 'Sign in',
    'auth.chooseRoleToRequestAgain': 'Choose a role to request again',
    'auth.requestAgain': 'Request again',
    'auth.noAccount': 'No account?',
    'auth.createAccount': 'Create account',
    'auth.name': 'Name',
    'auth.role': 'Role',
    'auth.alreadyHaveAccount': 'Already have an account?',
    'auth.backToSignIn': 'Back to sign in',
    'auth.loadingRoles': 'Loading roles…',
    'auth.noRolesAvailable': 'No roles available',
    'auth.couldNotLoadRoles': 'Could not load roles.',
    'auth.pleaseEnterEmail': 'Please enter your email.',
    'auth.pleaseEnterPassword': 'Please enter your password.',
    'auth.pendingApproval': 'Your request is awaiting admin approval.',
    'auth.accountDisabled': 'Your account is disabled.',
    'auth.requestRejected': 'Your request was rejected.',
    'auth.invalidCredentials': 'Invalid email or password.',
    'auth.networkError': 'Network error. Please try again.',
    'auth.signingIn': 'Signing in…',
    'auth.pleaseChooseRole': 'Please choose a role.',
    'auth.enterEmailPasswordToRequestAgain': 'Enter your email and password to request again.',
    'auth.submitting': 'Submitting…',
    'auth.couldNotSubmitRequest': 'Could not submit your request.',
    'auth.requestSubmittedMsg': 'Request submitted — an admin will review it.',
    'auth.pleaseEnterName': 'Please enter your name.',
    'auth.pleaseChoosePassword': 'Please choose a password.',
    'auth.couldNotCreateAccount': 'Could not create your account.',
    'auth.requestSubmittedBtn': 'Request submitted',

    // --- toolbar ---
    'toolbar.dragToReposition': 'Drag to reposition',
    'toolbar.commentOnElement': 'Comment on an element',
    'toolbar.commentOnElementShortcut': 'Comment on an element, shortcut {label}',
    'toolbar.cancel': 'Cancel',
    'toolbar.viewCommentsList': 'View comments list',
    'toolbar.comments': 'Comments',
    'toolbar.projectHeading': '{project}',
    'toolbar.recentActivityUpdates': 'Recent activity &amp; updates',
    'toolbar.updates': 'Updates',
    'toolbar.signedInAs': 'Signed in as',
    'toolbar.account': 'Account',
    'toolbar.hideBrand': 'Hide {brand}',
    'toolbar.resetToolbarPosition': 'Reset toolbar position',
    'toolbar.refreshComments': 'Refresh comments',
    'toolbar.close': 'Close',
    'toolbar.envFixedTitle': 'Environment — fixed for this install',
    'toolbar.envSwitchTitle': 'Environment — comments are scoped per environment',
    'toolbar.envAll': 'All',
    'toolbar.envLocal': 'local',
    'toolbar.envStaging': 'staging',
    'toolbar.envProduction': 'production',
    'toolbar.environment': 'Environment',
    'toolbar.commitStyle': 'Commit style',
    'toolbar.commitStyleTitle': 'How the AI apply flow commits applied comments',
    'toolbar.oneCommit': 'One commit',
    'toolbar.separateCommits': 'Separate commits',

    // --- user menu ---
    'menu.addComment': 'Add comment',
    'menu.clickThenPressKeyCombo': 'Click, then press a new key combo',
    'menu.resetToDefault': 'Reset to default',
    'menu.theme': 'Theme',
    'menu.light': 'Light',
    'menu.lightTheme': 'Light theme',
    'menu.dark': 'Dark',
    'menu.darkTheme': 'Dark theme',
    'menu.language': 'Language',
    'menu.extensionSignedInNote': 'Signed in via the browser extension — sign out from its popup.',
    'menu.signOut': 'Sign out',
    'menu.pressKeysToCancel': 'Press keys… (Esc to cancel)',
    'menu.addModifierKey': 'Add a modifier key (Alt/Shift/Ctrl/⌘)…',
    'menu.saving': 'Saving…',
    'menu.resetting': 'Resetting…',
    'menu.shortcutUpdated': 'Shortcut updated',
    'menu.failedToSaveTryAgain': 'Failed to save — try again',
    'menu.shortcutResetToDefault': 'Shortcut reset to default',
    'menu.failedToResetTryAgain': 'Failed to reset — try again',

    // --- launcher ---
    'launcher.openFeedbackFor': 'Open {brand} feedback',

    // --- sidebar / filters ---
    'sidebar.mineOnly': 'Mine only',
    'sidebar.showOnlyMyComments': 'Show only my comments',
    'sidebar.status': 'Status',
    'sidebar.filterByStatus': 'Filter by status',
    'sidebar.filterByUser': 'Filter by user',
    'sidebar.user': 'User',
    'sidebar.showFilters': 'Show filters',
    'sidebar.hideFilters': 'Hide filters',
    'sidebar.allUsers': 'All users',
    'sidebar.noCommentsYet': 'No comments on this project yet.<br/>Click the inspect icon, then click an element.',
    'sidebar.noOwnComments': "You haven't left any comments yet.",
    'sidebar.noFilteredComments': 'No comments in "{label}"{suffix}.',
    'sidebar.ofYours': ' of yours',

    // --- comment card ---
    'card.deployedIn': 'Deployed in {sha}',
    'card.live': 'live',
    'card.completed': 'completed',
    'card.pending': 'pending',
    'card.archived': 'archived',
    'card.verified': 'Verified',
    'card.looksRight': 'Looks right',
    'card.notFixed': 'Not fixed',
    'card.explainNotFixed': 'Explain what is still not fixed…',
    'card.submit': 'Submit',
    'card.viewCommit': 'View commit',
    'card.commit': 'commit',
    'card.containsSecretPayload': 'contains a secret/payload?',
    'card.defaultReplyAuthor': 'User',
    'card.automatedReply': 'Automated reply',
    'card.aiVia': 'via {name}',
    'card.edited': 'edited',
    'card.jumpToPin': 'Flash this comment\'s pin on the page',
    'card.reply': 'Reply',
    'card.replyPlaceholder': 'Reply…',
    'card.markedReadyClickToUnmark': 'Marked ready — click to unmark',
    'card.markReadyToApply': 'Mark ready to apply',
    'card.ready': 'Ready',
    'card.reopen': 'Re-open',
    'card.archive': 'Archive',
    'card.edit': 'Edit',
    'card.delete': 'Delete',
    'card.moreActions': 'More actions',
    'card.copyApplyPrompt': 'Copy apply prompt',
    'card.complete': 'Complete',
    'card.envLocal': 'Local',
    'card.envStaging': 'Staging',
    'card.envProduction': 'Production',
    'card.privateClickToMakePublic': 'Private — click to make public',
    'card.makePrivateOnlyYou': 'Make private (only you)',
    'card.makePublic': 'Make public',
    'card.makePrivate': 'Make private',
    'card.removeImage': 'Remove image',
    'card.save': 'Save',
    'card.deleteThisComment': 'Delete this comment?',
    'card.deleteThisReply': 'Delete this reply?',
    'card.confirmDelete': 'Confirm delete',

    // --- comment popover ---
    'popover.selectParentElement': 'Select parent element',
    'popover.selectFirstChildElement': 'Select first child element',
    'popover.commentOn': 'Comment on',
    'popover.whatShouldChange': 'What should change here?',
    'popover.predefinedPrompts': 'Predefined prompts',
    'popover.searchPrompts': 'Search prompts…',
    'popover.searchPredefinedPrompts': 'Search predefined prompts',
    'popover.noMatches': 'No matches',
    'popover.remove': 'Remove',
    'popover.attachScreenshot': 'Attach screenshot',
    'popover.reportBugTitle': 'Attaches any console errors/warnings and failed or slow network requests seen on this page',
    'popover.reportAsABug': 'Report as a bug',
    'popover.add': 'Add',
    'popover.commentCannotBeEmpty': 'Comment cannot be empty',
    'popover.clickAnyElementToComment': 'Click any element to comment on it — or press Esc to cancel',
    'popover.cancelled': 'Cancelled',

    // --- pins ---
    'pin.ready': 'Ready',
    'pin.applied': 'Applied',
    'pin.archived': 'Archived',
    'pin.open': 'Open',
    'pin.commentHash': 'Comment #{n}',
    'pin.byAuthor': ' by {author}',
    'pin.reply': 'reply',
    'pin.replies': 'replies',
    'pin.overlappingComments': '{n} overlapping comments at this location',

    // --- notifications ---
    'notifications.updates': 'Updates',
    'notifications.noUpdatesYet': 'No updates yet',
    'notifications.update': 'Update',
    'notifications.applied': 'Applied',
    'notifications.reopened': 'Reopened',
    'notifications.newReply': 'New reply',
    'notifications.commit': 'Commit',

    // --- toasts ---
    'toast.failedToVerifyComment': 'Failed to verify comment',
    'toast.commentVerified': 'Comment verified',
    'toast.commentReopened': 'Comment re-opened',
    'toast.signedOut': 'Signed out',
    'toast.hiddenClickToReopen': '{brand} hidden — click the button to reopen',
    'toast.couldNotReachServer': 'Could not reach {brand} server',
    'toast.retry': 'Retry',
    'toast.refreshed': 'Refreshed',
    'toast.pinElementNotFound': 'This comment\'s element isn\'t visible right now (hidden, removed, or temporary)',
    'toast.applyPromptCopied': 'Apply prompt copied — paste it into your AI tool',
    'toast.copyFailed': 'Could not copy to clipboard',
    'toast.commitStyleUpdated': 'Commit style updated',
    'toast.updateFailed': 'Update failed',
    'toast.updated': 'Updated',
    'toast.actionNoLongerAvailable': 'That action is no longer available — please choose another and try again.',
    'toast.commentsNotAllowedFromAddress': 'Comments are not allowed from this address',
    'toast.tooManyCommentsRetryIn': 'Too many comments — try again in {n} second{s}.',
    'toast.tooManyCommentsWait': 'Too many comments — please wait a moment and try again.',
    'toast.screenshotUploadFailed': 'Screenshot upload failed — saving without it',
    'toast.commentAdded': 'Comment added',
    'toast.undo': 'Undo',
    'toast.failedToSaveComment': 'Failed to save comment',
    'toast.failedToReply': 'Failed to reply',
    'toast.markedForApply': 'Marked for apply',
    'toast.unmarked': 'Unmarked',
    'toast.markedPrivate': 'Marked private',
    'toast.madePublic': 'Made public',
    'toast.markedCompleted': 'Marked completed',
    'toast.deleted': 'Deleted',
    'toast.deleteFailed': 'Delete failed',
    'toast.commentUpdated': 'Comment updated',
    'toast.failedToUpdateComment': 'Failed to update comment',
    'toast.reopenedMsg': 'Re-opened',
    'toast.archivedMsg': 'Archived',
    'toast.pleaseProvideNoteNotFixed': 'Please provide a note explaining what is not fixed',
    'toast.notifications': 'Notifications',
    'fields.more': 'Add more fields',
    'fields.fewer': 'Fewer fields',
    'fields.edit': 'Edit fields',
    'fields.save': 'Save fields',
    'fields.cancel': 'Cancel',
    'fields.none': 'None',
    'fields.invalidUrl': 'Must be a valid URL',
    'fields.hostNotAllowed': 'Must be a link on {hosts}',
    'fields.tooLong': 'Value is too long',
    'fields.saved': 'Fields saved',
    'fields.serverRejected': 'Server rejected:',

  },
  ar: {
    // --- auth (login/signup modal) ---
    'auth.leaveFeedbackOn': 'قدّم ملاحظاتك على',
    'auth.skipForNow': 'تخطَّ هذا الآن',
    'auth.email': 'البريد الإلكتروني',
    'auth.password': 'كلمة المرور',
    'auth.signIn': 'تسجيل الدخول',
    'auth.chooseRoleToRequestAgain': 'اختر دورًا لإعادة الطلب',
    'auth.requestAgain': 'إعادة الطلب',
    'auth.noAccount': 'ليس لديك حساب؟',
    'auth.createAccount': 'إنشاء حساب',
    'auth.name': 'الاسم',
    'auth.role': 'الدور',
    'auth.alreadyHaveAccount': 'لديك حساب بالفعل؟',
    'auth.backToSignIn': 'العودة لتسجيل الدخول',
    'auth.loadingRoles': 'جارٍ تحميل الأدوار…',
    'auth.noRolesAvailable': 'لا توجد أدوار متاحة',
    'auth.couldNotLoadRoles': 'تعذّر تحميل الأدوار.',
    'auth.pleaseEnterEmail': 'يرجى إدخال بريدك الإلكتروني.',
    'auth.pleaseEnterPassword': 'يرجى إدخال كلمة المرور.',
    'auth.pendingApproval': 'طلبك بانتظار موافقة المسؤول.',
    'auth.accountDisabled': 'حسابك معطّل.',
    'auth.requestRejected': 'تم رفض طلبك.',
    'auth.invalidCredentials': 'البريد الإلكتروني أو كلمة المرور غير صحيحة.',
    'auth.networkError': 'خطأ في الشبكة. يرجى المحاولة مرة أخرى.',
    'auth.signingIn': 'جارٍ تسجيل الدخول…',
    'auth.pleaseChooseRole': 'يرجى اختيار دور.',
    'auth.enterEmailPasswordToRequestAgain': 'أدخل بريدك الإلكتروني وكلمة المرور لإعادة الطلب.',
    'auth.submitting': 'جارٍ الإرسال…',
    'auth.couldNotSubmitRequest': 'تعذّر إرسال طلبك.',
    'auth.requestSubmittedMsg': 'تم إرسال الطلب — سيراجعه أحد المسؤولين.',
    'auth.pleaseEnterName': 'يرجى إدخال اسمك.',
    'auth.pleaseChoosePassword': 'يرجى اختيار كلمة مرور.',
    'auth.couldNotCreateAccount': 'تعذّر إنشاء حسابك.',
    'auth.requestSubmittedBtn': 'تم إرسال الطلب',

    // --- toolbar ---
    'toolbar.dragToReposition': 'اسحب لتغيير الموضع',
    'toolbar.commentOnElement': 'أضف تعليقًا على عنصر',
    'toolbar.commentOnElementShortcut': 'أضف تعليقًا على عنصر، الاختصار {label}',
    'toolbar.cancel': 'إلغاء',
    'toolbar.viewCommentsList': 'عرض قائمة التعليقات',
    'toolbar.comments': 'التعليقات',
    'toolbar.projectHeading': '{project}',
    'toolbar.recentActivityUpdates': 'النشاط الأخير والتحديثات',
    'toolbar.updates': 'التحديثات',
    'toolbar.signedInAs': 'مسجّل الدخول باسم',
    'toolbar.account': 'الحساب',
    'toolbar.hideBrand': 'إخفاء {brand}',
    'toolbar.resetToolbarPosition': 'إعادة ضبط موضع شريط الأدوات',
    'toolbar.refreshComments': 'تحديث التعليقات',
    'toolbar.close': 'إغلاق',
    'toolbar.envFixedTitle': 'البيئة — ثابتة لهذا التثبيت',
    'toolbar.envSwitchTitle': 'البيئة — التعليقات مرتبطة بكل بيئة على حدة',
    'toolbar.envAll': 'الكل',
    'toolbar.envLocal': 'محلي',
    'toolbar.envStaging': 'الاختبار',
    'toolbar.envProduction': 'الإنتاج',
    'toolbar.environment': 'البيئة',
    'toolbar.commitStyle': 'أسلوب الالتزام',
    'toolbar.commitStyleTitle': 'كيفية التزام التعليقات المطبَّقة عبر مسار تطبيق الذكاء الاصطناعي',
    'toolbar.oneCommit': 'التزام واحد',
    'toolbar.separateCommits': 'التزامات منفصلة',

    // --- user menu ---
    'menu.addComment': 'إضافة تعليق',
    'menu.clickThenPressKeyCombo': 'انقر، ثم اضغط تركيبة مفاتيح جديدة',
    'menu.resetToDefault': 'إعادة إلى الافتراضي',
    'menu.theme': 'المظهر',
    'menu.light': 'فاتح',
    'menu.lightTheme': 'المظهر الفاتح',
    'menu.dark': 'داكن',
    'menu.darkTheme': 'المظهر الداكن',
    'menu.language': 'اللغة',
    'menu.extensionSignedInNote': 'تم تسجيل الدخول عبر إضافة المتصفح — سجّل الخروج من نافذتها المنبثقة.',
    'menu.signOut': 'تسجيل الخروج',
    'menu.pressKeysToCancel': 'اضغط المفاتيح… (Esc للإلغاء)',
    'menu.addModifierKey': 'أضف مفتاح تعديل (Alt/Shift/Ctrl/⌘)…',
    'menu.saving': 'جارٍ الحفظ…',
    'menu.resetting': 'جارٍ إعادة الضبط…',
    'menu.shortcutUpdated': 'تم تحديث الاختصار',
    'menu.failedToSaveTryAgain': 'فشل الحفظ — حاول مرة أخرى',
    'menu.shortcutResetToDefault': 'تمت إعادة الاختصار إلى الافتراضي',
    'menu.failedToResetTryAgain': 'فشلت إعادة الضبط — حاول مرة أخرى',

    // --- launcher ---
    'launcher.openFeedbackFor': 'فتح ملاحظات {brand}',

    // --- sidebar / filters ---
    'sidebar.mineOnly': 'تعليقاتي فقط',
    'sidebar.showOnlyMyComments': 'عرض تعليقاتي فقط',
    'sidebar.status': 'الحالة',
    'sidebar.filterByStatus': 'تصفية حسب الحالة',
    'sidebar.filterByUser': 'تصفية حسب المستخدم',
    'sidebar.user': 'المستخدم',
    'sidebar.showFilters': 'إظهار الفلاتر',
    'sidebar.hideFilters': 'إخفاء الفلاتر',
    'sidebar.allUsers': 'جميع المستخدمين',
    'sidebar.noCommentsYet': 'لا توجد تعليقات على هذا المشروع بعد.<br/>انقر على أيقونة الفحص، ثم انقر على عنصر.',
    'sidebar.noOwnComments': 'لم تترك أي تعليقات بعد.',
    'sidebar.noFilteredComments': 'لا توجد تعليقات ضمن "{label}"{suffix}.',
    'sidebar.ofYours': ' الخاصة بك',

    // --- comment card ---
    'card.deployedIn': 'تم النشر في {sha}',
    'card.live': 'مباشر',
    'card.completed': 'مكتمل',
    'card.pending': 'قيد الانتظار',
    'card.archived': 'مؤرشف',
    'card.verified': 'تم التحقق',
    'card.looksRight': 'يبدو صحيحًا',
    'card.notFixed': 'لم يُصلح',
    'card.explainNotFixed': 'اشرح ما لم يتم إصلاحه بعد…',
    'card.submit': 'إرسال',
    'card.viewCommit': 'عرض الالتزام',
    'card.commit': 'التزام',
    'card.containsSecretPayload': 'قد يحتوي على بيانات سرية؟',
    'card.defaultReplyAuthor': 'مستخدم',
    'card.automatedReply': 'رد آلي',
    'card.aiVia': 'بواسطة {name}',
    'card.edited': 'مُعدَّل',
    'card.jumpToPin': 'إظهار دبوس هذا التعليق على الصفحة',
    'card.reply': 'رد',
    'card.replyPlaceholder': 'رد…',
    'card.markedReadyClickToUnmark': 'وُضع علامة جاهز — انقر لإلغائها',
    'card.markReadyToApply': 'وضع علامة جاهز للتطبيق',
    'card.ready': 'جاهز',
    'card.reopen': 'إعادة الفتح',
    'card.archive': 'أرشفة',
    'card.edit': 'تعديل',
    'card.delete': 'حذف',
    'card.moreActions': 'المزيد من الإجراءات',
    'card.copyApplyPrompt': 'نسخ تعليمة التطبيق',
    'card.complete': 'إكمال',
    'card.envLocal': 'محلي',
    'card.envStaging': 'الاختبار',
    'card.envProduction': 'الإنتاج',
    'card.privateClickToMakePublic': 'خاص — انقر لجعله عامًا',
    'card.makePrivateOnlyYou': 'اجعله خاصًا (أنت فقط)',
    'card.makePublic': 'اجعله عامًا',
    'card.makePrivate': 'اجعله خاصًا',
    'card.removeImage': 'إزالة الصورة',
    'card.save': 'حفظ',
    'card.deleteThisComment': 'هل تريد حذف هذا التعليق؟',
    'card.deleteThisReply': 'هل تريد حذف هذا الرد؟',
    'card.confirmDelete': 'تأكيد الحذف',

    // --- comment popover ---
    'popover.selectParentElement': 'اختر العنصر الأصل',
    'popover.selectFirstChildElement': 'اختر العنصر الفرعي الأول',
    'popover.commentOn': 'تعليق على',
    'popover.whatShouldChange': 'ما الذي يجب تغييره هنا؟',
    'popover.predefinedPrompts': 'اقتراحات جاهزة',
    'popover.searchPrompts': 'ابحث في الاقتراحات…',
    'popover.searchPredefinedPrompts': 'البحث في الاقتراحات الجاهزة',
    'popover.noMatches': 'لا توجد نتائج',
    'popover.remove': 'إزالة',
    'popover.attachScreenshot': 'إرفاق لقطة شاشة',
    'popover.reportBugTitle': 'يُرفق أي أخطاء/تحذيرات في وحدة التحكم وطلبات الشبكة الفاشلة أو البطيئة في هذه الصفحة',
    'popover.reportAsABug': 'الإبلاغ كخلل',
    'popover.add': 'إضافة',
    'popover.commentCannotBeEmpty': 'لا يمكن أن يكون التعليق فارغًا',
    'popover.clickAnyElementToComment': 'انقر على أي عنصر للتعليق عليه — أو اضغط Esc للإلغاء',
    'popover.cancelled': 'تم الإلغاء',

    // --- pins ---
    'pin.ready': 'جاهز',
    'pin.applied': 'مطبَّق',
    'pin.archived': 'مؤرشف',
    'pin.open': 'مفتوح',
    'pin.commentHash': 'تعليق رقم {n}',
    'pin.byAuthor': ' بواسطة {author}',
    'pin.reply': 'رد واحد',
    'pin.replies': '{n} ردود',
    'pin.overlappingComments': '{n} تعليقات متداخلة في هذا الموضع',

    // --- notifications ---
    'notifications.updates': 'التحديثات',
    'notifications.noUpdatesYet': 'لا توجد تحديثات بعد',
    'notifications.update': 'تحديث',
    'notifications.applied': 'تم التطبيق',
    'notifications.reopened': 'أُعيد فتحه',
    'notifications.newReply': 'رد جديد',
    'notifications.commit': 'الالتزام',

    // --- toasts ---
    'toast.failedToVerifyComment': 'فشل التحقق من التعليق',
    'toast.commentVerified': 'تم التحقق من التعليق',
    'toast.commentReopened': 'أُعيد فتح التعليق',
    'toast.signedOut': 'تم تسجيل الخروج',
    'toast.hiddenClickToReopen': 'تم إخفاء {brand} — انقر على الزر لإعادة فتحه',
    'toast.couldNotReachServer': 'تعذّر الوصول إلى خادم {brand}',
    'toast.retry': 'إعادة المحاولة',
    'toast.refreshed': 'تم التحديث',
    'toast.pinElementNotFound': 'عنصر هذا التعليق غير ظاهر حاليًا (مخفي أو محذوف أو مؤقت)',
    'toast.applyPromptCopied': 'تم نسخ تعليمة التطبيق — الصقها في أداة الذكاء الاصطناعي',
    'toast.copyFailed': 'تعذر النسخ إلى الحافظة',
    'toast.commitStyleUpdated': 'تم تحديث أسلوب الالتزام',
    'toast.updateFailed': 'فشل التحديث',
    'toast.updated': 'تم التحديث',
    'toast.actionNoLongerAvailable': 'لم يعد هذا الإجراء متاحًا — يرجى اختيار إجراء آخر والمحاولة مرة أخرى.',
    'toast.commentsNotAllowedFromAddress': 'التعليقات غير مسموح بها من هذا العنوان',
    'toast.tooManyCommentsRetryIn': 'عدد كبير جدًا من التعليقات — حاول مرة أخرى بعد {n} ثانية.',
    'toast.tooManyCommentsWait': 'عدد كبير جدًا من التعليقات — يرجى الانتظار قليلًا والمحاولة مرة أخرى.',
    'toast.screenshotUploadFailed': 'فشل رفع لقطة الشاشة — سيُحفظ التعليق دونها',
    'toast.commentAdded': 'تمت إضافة التعليق',
    'toast.undo': 'تراجع',
    'toast.failedToSaveComment': 'فشل حفظ التعليق',
    'toast.failedToReply': 'فشل إرسال الرد',
    'toast.markedForApply': 'وُضعت علامة للتطبيق',
    'toast.unmarked': 'تم إلغاء العلامة',
    'toast.markedPrivate': 'وُضعت علامة خاص',
    'toast.madePublic': 'أصبح عامًا',
    'toast.markedCompleted': 'وُضعت علامة مكتمل',
    'toast.deleted': 'تم الحذف',
    'toast.deleteFailed': 'فشل الحذف',
    'toast.commentUpdated': 'تم تحديث التعليق',
    'toast.failedToUpdateComment': 'فشل تحديث التعليق',
    'toast.reopenedMsg': 'أُعيد فتحه',
    'toast.archivedMsg': 'تمت الأرشفة',
    'toast.pleaseProvideNoteNotFixed': 'يرجى كتابة ملاحظة تشرح ما لم يتم إصلاحه',
    'toast.notifications': 'الإشعارات',
    'fields.more': 'إضافة المزيد من الحقول',
    'fields.fewer': 'حقول أقل',
    'fields.edit': 'تعديل الحقول',
    'fields.save': 'حفظ الحقول',
    'fields.cancel': 'إلغاء',
    'fields.none': 'لا شيء',
    'fields.invalidUrl': 'يجب أن يكون رابطاً صالحاً',
    'fields.hostNotAllowed': 'يجب أن يكون الرابط من {hosts}',
    'fields.tooLong': 'القيمة طويلة جداً',
    'fields.saved': 'تم حفظ الحقول',
    'fields.serverRejected': 'رفض الخادم:',

  },
};
