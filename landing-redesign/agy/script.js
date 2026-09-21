/**
 * Pointer - Landing Page Client Logic
 * Handles i18n (EN/AR with full RTL), Theme Switching, Live API Integrations,
 * Accessible Walkable Stepper, and Animated Cost-Aware Apply Graph.
 */

(function() {
  'use strict';

  // API Base Resolution per BRIEF.md
  var API_BASE = (typeof window !== 'undefined' && window.__POINTER_API__)
    || (document.querySelector('meta[name="pointer-api"]') || {}).content
    || "https://api.pointer.moamen.work";

  // Global state for live data & URLs
  var state = {
    lang: 'en',
    theme: 'light',
    branding: {
      productName: 'Pointer',
      urls: {
        app: 'https://app.pointer.moamen.work',
        demo: 'https://demo.pointer.moamen.work',
        docs: 'https://pointer.moamen.work/docs/',
        landing: 'https://pointer.moamen.work'
      },
      extension: {
        storeUrl: 'https://chromewebstore.google.com/detail/pointer-feedback/dilmhkkffghagljpbijnacjebfcjlcji?authuser=0&hl=en',
        zipUrl: 'https://pointer.moamen.work/pointer-extension.zip'
      }
    },
    plans: null,
    stacks: null,
    stats: null
  };

  // Comprehensive Translation Dictionary
  var TRANSLATIONS = {
    en: {
      // Nav
      nav_signin: "Sign in",
      nav_try_demo: "Try the demo",
      
      // Hero
      hero_title: "An AI coding tool is only as good as the feedback it receives.",
      hero_subtitle: "Point, click, and comment on your live app. Generate a structured DOM and state brief that any AI agent can action immediately.",
      hero_cta_demo: "Try the demo — no install",
      hero_cta_account: "Create an account",
      hero_human_label: "Stakeholder Comment",
      hero_human_time: "Just now",
      hero_human_text: '"Make this submit button span the full width."',
      hero_machine_label: "Structured Brief",
      
      // Section 3: The Brief
      brief_badge: "The Living Spec",
      brief_title: "The Structured Brief",
      brief_subtitle: "Every comment carries the exact DOM, style, and runtime state an AI tool needs to touch the right file on the first turn.",
      brief_col_field: "Field",
      brief_col_reason: "Why an AI tool cares",
      brief_f_selector_desc: "Identifies the exact DOM element without a screenshot or a description",
      brief_f_snapshot_desc: "The element's own opening tag, attributes (≤120 chars), text (≤160 chars) — deliberately shallow so it doesn't drown the model in child markup",
      brief_f_applied_desc: "The rules that win, not the full computed dump — answers \"why is it blue?\" before the agent asks",
      brief_f_computed_desc: "Disambiguates structure when the selector is generic",
      brief_f_route_desc: "Identifies which screen/view to open",
      brief_f_viewport_desc: "Turns \"broken on mobile\" into a reproducible condition",
      brief_f_context_desc: "Console errors and failed/slow network requests at the moment of the issue (opt-in for bug reports)",
      brief_f_source_desc: "Configured attribute or framework dev-mode path when present (when source mapping is enabled)",
      brief_f_lang_desc: "The BCP-47 tag the comment was detected in",
      brief_sample_header: "Sample Captured Payload",
      
      // Section 4: How It Works
      loop_badge: "Feedback Loop",
      loop_title: "How it works",
      loop_subtitle: "Four steps from raw feedback to a verified local commit.",
      loop_step1_title: "1. Point & comment",
      loop_step1_desc: "Anyone signed in clicks an element on the running app and leaves a short comment in whatever language they think in.",
      loop_step2_title: "2. Triage in the dashboard",
      loop_step2_desc: "Tagged by project, environment (Local, Staging, Production), stakeholder, and status in one unified queue.",
      loop_step3_title: "3. Applied by AI",
      loop_step3_desc: "A developer hands the queue to any AI coding tool over plain HTTP. Non-English comments are translated automatically so the model executes in English.",
      loop_step4_title: "4. Committed, never pushed",
      loop_step4_desc: "The AI commits per project style and links each applied comment back to the author. It never pushes — human reviews the diff. When deployed, author confirms with 👍/👎.",
      loop_translation_badge: "Worker Model Translation",
      loop_translation_text: "Non-English comments are translated in, and replies translated back out, by a lightweight model — never the expensive orchestrator.",

      // Section 5: Before / After
      contrast_badge: "The Contrast",
      contrast_title: "Why precision changes everything",
      contrast_subtitle: "Stop treating AI coding tools like human juniors who have to guess your intent.",
      contrast_without_title: "Without Pointer",
      contrast_without_item1: "Vague Slack threads: \"The button on checkout looks wrong.\"",
      contrast_without_item2: "Dev guesses intent; AI agent searches codebase files blindly.",
      contrast_without_item3: "Missing viewport, device pixel ratio, and winning CSS cascades.",
      contrast_without_item4: "Multiple back-and-forth review rounds and wasted iteration loops.",
      contrast_with_title: "With Pointer",
      contrast_with_item1: "Exact DOM selector, winning CSS rules, and shallow element snapshot.",
      contrast_with_item2: "Pinpoints source file directly when source mapping is enabled.",
      contrast_with_item3: "Automatic translation for multilingual stakeholder feedback.",
      contrast_with_item4: "Clean local git commit created; human reviews diff before pushing.",

      // Section 6 & 7: Economics & Cost Graph
      econ_badge: "AI Economics",
      econ_title: "Why it saves time, cost, and tokens",
      econ_subtitle: "Structured inputs eliminate exploratory loops and delegate repetitive tasks to cheaper models.",
      econ_card1_title: "Save developer time",
      econ_card1_desc: "Context is attached at capture time. No more \"what browser were you on?\" clarification meetings.",
      econ_card2_title: "Cut iteration costs",
      econ_card2_desc: "Fewer dead-end prompts and discarded edits before touching the correct component.",
      econ_card3_title: "Spend fewer AI tokens",
      econ_card3_desc: "The agent starts at the winning CSS rule and selector, replacing whole-repo exploration with a targeted brief.",
      econ_card4_title: "One organized queue",
      econ_card4_desc: "Multi-tenant, multi-environment queue eliminates scattered screenshots across five Slack channels.",
      
      graph_title: "Cost-aware apply delegation",
      graph_desc: "When an edit touches multiple files or is mechanical (copy tweaks, style adjustments, prop changes), Pointer splits the work: the premium model plans and reviews, while a cheaper model executes mechanical typing and background translations.",
      graph_bar_a_title: "One model does everything",
      graph_bar_a_desc: "Investigation, review, edits, translation — all on one expensive model.",
      graph_bar_b_title: "Pointer: cost-aware delegation",
      graph_seg_review: "Investigation + review (premium model)",
      graph_seg_worker: "Mechanical edits + translation → cheaper model",
      graph_legend_1: "Mechanical edits (copy tweaks, style swaps, component props)",
      graph_legend_2: "Built-in translation (multilingual input & reply translation)",
      graph_honesty_note: "Fewer premium-model tokens spent on typing and translating.",

      // Section 8: Features Grid
      feat_badge: "Engineering Standards",
      feat_title: "Engineered for production environments",
      feat_subtitle: "Lightweight integration without runtime bloat or framework lock-in.",
      feat_card1_title: "Two-line install",
      feat_card1_desc: "Drop a script tag and custom element onto your page. No heavy SDK or bundle bloat.",
      feat_card2_title: "Multi-project & multi-tenant",
      feat_card2_desc: "Strict isolation enforced server-side. Manage Local, Staging, and Production environments.",
      feat_card3_title: "Shadow DOM isolation",
      feat_card3_desc: "Complete CSS encapsulation. Host styles never leak into the widget, and widget styles never affect host markup.",
      feat_card4_title: "Browser extension",
      feat_card4_desc: "Inspect and leave agent-ready comments on staging sites, client builds, or web apps you don't host.",
      feat_chip1: "Input-value masking",
      feat_chip2: "Secret detection (sk-, AKIA... flagged)",
      feat_chip3: "Deploy awareness (Applied vs Live)",
      feat_chip4: "Self-hostable: API + Postgres",

      // Section 9: Works with your stack
      stack_title: "Works with your stack",
      stack_subtitle: "Live telemetry from active projects and AI coding tools.",
      stack_group_frameworks: "Frameworks & Backend:",
      stack_group_tools: "AI Coding Tools:",

      // Section 10: Stats
      stats_title: "Pointer in numbers",
      stats_subtitle: "Public telemetry cleared through anonymity thresholds.",
      stats_applied: "Applied comments",
      stats_projects: "Active projects",
      stats_workspaces: "Workspaces",
      stats_median: "Median time to apply",
      stats_languages_prefix: "Feedback written in: ",

      // Section 11: Whole Team
      team_badge: "Collaboration",
      team_title: "Built for the whole team",
      team_subtitle: "Stakeholders give clear visual feedback. Developers receive clean, actionable tasks.",
      team_stakeholder_title: "For Stakeholders & Clients",
      team_stakeholder_p1: "Click directly on the running app and leave a sentence in your own language.",
      team_stakeholder_p2: "Nothing to install and zero technical knowledge required.",
      team_stakeholder_p3: "Feedback is translated automatically, and replies return in your language.",
      team_stakeholder_p4: "Confirm fixes with a single 👍 or 👎 once deployed to production.",
      team_dev_title: "For Developers & AI Engineers",
      team_dev_p1: "Pull the structured queue over plain HTTP with your preferred AI coding tool.",
      team_dev_p2: "Agent starts at the selector and winning CSS rules (and source path when source mapping is enabled).",
      team_dev_p3: "Compatible with Claude Code, Cursor, Windsurf, OpenCode, and Antigravity.",
      team_dev_p4: "AI commits locally with commit URL; you review the diff before pushing.",

      // Section 12: Trust & Privacy
      trust_badge: "Security & Ethics",
      trust_title: "Trust & privacy",
      trust_subtitle: "Strict boundaries around your code, your data, and your user privacy.",
      trust_q1: "An AI edits my code?",
      trust_a1: "The AI commits locally; it never pushes. Every applied comment carries its git commit URL so you can inspect the diff before anything touches your repository.",
      trust_q2: "What do you capture?",
      trust_a2: "Element-level DOM metadata only: selector, shallow HTML snippet, winning CSS rules, and route. Never passwords, form inputs, session cookies, localStorage, or API request bodies. See our Data & Self-Hosting and Privacy Policy pages.",
      trust_q3: "Which AI tool do I use?",
      trust_a3: "Any AI coding tool you already use (Claude Code, Cursor, Windsurf, OpenCode, Antigravity). Plain HTTP integration with zero vendor lock-in.",
      trust_q4: "Is my data stuck with you?",
      trust_a4: "Fully self-hostable. Run the open API and Postgres database on your own infrastructure so data never leaves your network.",

      // Section 13: Pricing
      pricing_badge: "Transparent Tiers",
      pricing_title: "Simple, transparent pricing",
      pricing_subtitle: "Start free on your local stack. Upgrade when you need team collaboration.",
      pricing_coming_soon: "Coming soon",
      pricing_free_title: "Free",
      pricing_free_price: "$0",
      pricing_free_period: "forever",
      pricing_free_b1: "3 projects",
      pricing_free_b2: "5 seats",
      pricing_free_b3: "100 comments / month",
      pricing_free_b4: "Community support",
      pricing_pro_title: "Pro",
      pricing_pro_price: "$5",
      pricing_pro_period: "per month",
      pricing_pro_b1: "Unlimited projects",
      pricing_pro_b2: "Unlimited seats",
      pricing_pro_b3: "Priority support",
      pricing_pro_b4: "Cost-aware delegation",
      pricing_cta_account: "Create an account",

      // Section 14: Extension Stepper
      ext_badge: "Browser Extension",
      ext_title: "Browser extension & install stepper",
      ext_subtitle: "Leave structured comments on staging builds, client apps, or any site you don't host.",
      ext_tab1: "1. Download",
      ext_tab2: "2. Unzip",
      ext_tab3: "3. Extensions",
      ext_tab4: "4. Load",
      ext_tab5: "5. Point",
      ext_s1_title: "Step 1: Download Extension",
      ext_s1_desc: "Download the extension package or install directly from the Chrome Web Store.",
      ext_s1_btn: "Install Extension",
      ext_s1_btn_zip: "Download ZIP Package",
      ext_s2_title: "Step 2: Extract Archive",
      ext_s2_desc: "Unzip the downloaded archive into a permanent folder on your local machine.",
      ext_s3_title: "Step 3: Open Chrome Extensions",
      ext_s3_desc: "In Chrome, Brave, or Edge, navigate to chrome://extensions in the address bar.",
      ext_s4_title: "Step 4: Enable Developer Mode & Load",
      ext_s4_desc: "Toggle Developer mode in the top-right corner, click \"Load unpacked\", and choose the extracted folder.",
      ext_s5_title: "Step 5: Sign in & Point",
      ext_s5_desc: "Click the Pointer icon in your browser toolbar, sign in, and click any element on the running web app.",
      ext_next_btn: "Next step",
      ext_prev_btn: "Previous step",

      // Section 15: Final CTA & Footer
      final_title: "Ready to give your AI agent exact feedback?",
      final_subtitle: "Cut out vague screenshots and endless exploratory loops. Start pointing at the UI today.",
      final_cta_demo: "Try the demo — no install",
      final_cta_account: "Create an account",
      footer_tagline: "Agentic feedback for web applications.",
      footer_review_note: "Commits, never pushes. Built for human verification.",
      footer_col_docs: "Documentation",
      footer_link_install: "Install Guide",
      footer_link_apply: "Apply Feedback",
      footer_link_keys: "API Keys",
      footer_link_alldocs: "All Documentation",
      footer_col_app: "Product",
      footer_link_dashboard: "Web Dashboard",
      footer_link_demo: "Interactive Demo",
      footer_link_extension: "Browser Extension",
      footer_col_trust: "Trust & Legal",
      footer_link_privacy: "Privacy Policy",
      footer_link_data: "Data & Self-Hosting",
      footer_link_github: "GitHub Repository",
      footer_copy: "Pointer. Precision input layer for AI coding tools."
    },
    ar: {
      // Nav
      nav_signin: "تسجيل الدخول",
      nav_try_demo: "جرب العرض التوضيحي",
      
      // Hero
      hero_title: "أداة البرمجة بالذكاء الاصطناعي لا تكون أفضل من الملاحظات التي تتلقاها.",
      hero_subtitle: "أشر، انقر، وعلّق على تطبيقك الفعلي. أنشئ ملخصاً برمجياً دقيقاً يمكن لأي وكيل ذكاء اصطناعي تنفيذه فوراً.",
      hero_cta_demo: "جرب العرض التوضيحي — بدون تثبيت",
      hero_cta_account: "إنشاء حساب",
      hero_human_label: "تعليق من فريق العمل",
      hero_human_time: "الآن",
      hero_human_text: '"اجعل زر الإرسال هذا يمتد بعرض الحاوية بالكامل."',
      hero_machine_label: "الملخص المهيكل",

      // Section 3: The Brief
      brief_badge: "المعيار التقني المباشر",
      brief_title: "الملخص المهيكل",
      brief_subtitle: "يحمل كل تعليق بيانات شجرة DOM والأنماط وحالة التشغيل التي يحتاجها الوكيل البرمجي للوصول للملف الصحيح من الخطوة الأولى.",
      brief_col_field: "الحقل البرمجي",
      brief_col_reason: "لماذا يهتم وكيل الذكاء الاصطناعي",
      brief_f_selector_desc: "يحدد عنصر DOM بدقة متناهية دون الحاجة إلى لقطة شاشة أو وصف إنشائي.",
      brief_f_snapshot_desc: "وسم الفتح وسمات العنصر والنص القريب — مقتضب عمداً حتى لا يغرق النموذج في تفاصيل العناصر التابعة.",
      brief_f_applied_desc: "القواعد البرمجية الفائزة في تطبيق النمط — يجيب عن سؤال 'لماذا هو أزرق؟' قبل أن يسأل الوكيل.",
      brief_f_computed_desc: "يزيل الغموض الهيكلي عندما يكون محدد العنصر عاماً.",
      brief_f_route_desc: "يحدد الشاشة أو المسار البرمجي المطلوب فتحه.",
      brief_f_viewport_desc: "يحول عبارة 'معطل على الهاتف' إلى بيئة وشروط قابلة لإعادة الإنتاج برمجياً.",
      brief_f_context_desc: "أخطاء وحدة التحكم (Console) وطلبات الشبكة الفاشلة أو البطيئة في لحظة حدوث المشكلة.",
      brief_f_source_desc: "مسار ملف المصدر من سمات المكون أو وضع التطوير (عند تفعيل ربط المصدر source mapping).",
      brief_f_lang_desc: "رمز BCP-47 للغة التي كُتب بها التعليق.",
      brief_sample_header: "نموذج البيانات الملتقطة",

      // Section 4: How It Works
      loop_badge: "دورة العمل الكاملة",
      loop_title: "كيف يعمل النظام",
      loop_subtitle: "أربع خطوات من الملاحظة المباشرة إلى إيداع Git معتمد ومراجع.",
      loop_step1_title: "1. أشر وعلّق",
      loop_step1_desc: "ينقر أي عضو في الفريق على العنصر الفعلي ويكتب ملاحظته بأي لغة يفكر بها.",
      loop_step2_title: "2. فرز وتنظيم في لوحة التحكم",
      loop_step2_desc: "تصنيف تلقائي حسب المشروع، بيئة التشغيل (محلي، تجريبي، إنتاج)، وصاحب الملاحظة في قائمة موحدة.",
      loop_step3_title: "3. تطبيق ذكي بالذكاء الاصطناعي",
      loop_step3_desc: "يستلم المطور قائمة التعديلات عبر HTTP قياسي؛ تترجم التعليقات غير الإنجليزية تلقائياً ليعمل الوكيل بالإنجليزية.",
      loop_step4_title: "4. إيداع Git دون رفع تلقائي",
      loop_step4_desc: "ينشئ الوكيل إيداعاً محلياً ويُرفق رابط التعليق؛ لا يقوم بالرفع أبداً، ويراجع المطور الفروقات (diff). عند الإطلاق يعتمد العضو التعديل بـ 👍 أو 👎.",
      loop_translation_badge: "ترجمة تلقائية مدمجة",
      loop_translation_text: "يُترجم تعليق العميل غير الإنجليزي بنموذج اقتصادي فوري، وتعود الردود بنفس لغته الأصلية.",

      // Section 5: Before / After
      contrast_badge: "مقارنة المسارات",
      contrast_title: "الفارق بين التخمين والبيانات الدقيقة",
      contrast_subtitle: "توقف عن إضاعة وقت الوكلاء البرمجية في التخمين واستكشاف الملفات الخاطئة.",
      contrast_without_title: "بدون بوينتر",
      contrast_without_item1: "رسائل غامضة في سلاك: 'الزر يبدو غير متناسق في شاشة الدفع'.",
      contrast_without_item2: "المطور يحاول فك شفرة لقطات الشاشة؛ والوكيل البرمجي يستكشف الملفات عشوائياً.",
      contrast_without_item3: "غياب أبعاد الشاشة الحقيقية والقواعد البرمجية المتداخلة الفائزة.",
      contrast_without_item4: "حلقات نقاش مطولة ومحاولات متكررة لتصحيح التنسيقات.",
      contrast_with_title: "مع بوينتر",
      contrast_with_item1: "محدد DOM دقيق، القواعد الفائزة، ولقطة سريعة لسمات العنصر.",
      contrast_with_item2: "تحديد مسار الملف المصدر فوراً عند تفعيل ربط المصدر (source mapping).",
      contrast_with_item3: "ترجمة فورية وتلقائية للملاحظات المكتوبة باللغات غير الإنجليزية.",
      contrast_with_item4: "إنشاء إيداع محلي مع رابط توثيقي ليقوم المطور بمراجعة الفروقات واعتمادها.",

      // Section 6 & 7: Economics & Cost Graph
      econ_badge: "اقتصاديات الذكاء الاصطناعي",
      econ_title: "لماذا يوفر الوقت والتكلفة واستهلاك الرموز (Tokens)",
      econ_subtitle: "توجيه الوكيل ببيانات مهيكلة يقلل من الاستكشاف العشوائي ويفوض المهام الميكانيكية للنماذج الاقتصادية.",
      econ_card1_title: "توفير وقت المطورين",
      econ_card1_desc: "سياق فوري متصل بالعنصر؛ لا مزيد من المحادثات التوضيحية أو التساؤل عن أبعاد الشاشة.",
      econ_card2_title: "خفض تكاليف التكرار",
      econ_card2_desc: "تقليل التعديلات الضائعة والمسارات المسدودة قبل العثور على العنصر المستهدف.",
      econ_card3_title: "استهلاك رمزي أقل (Tokens)",
      econ_card3_desc: "يبدأ الوكيل من محدد DOM وقاعدة CSS الفائزة بدلاً من مسح شجرة الملفات بالكامل.",
      econ_card4_title: "قائمة موحدة ومنظمة",
      econ_card4_desc: "قائمة واحدة متعددة المشاريع والبيئات بدلاً من تشتت الملاحظات عبر القنوات.",

      graph_title: "تفويض المهام الواعي للتكلفة",
      graph_desc: "عندما تمس التعديلات ملفات متعددة أو تكون ميكانيكية (تعديل نصوص، تبديل ألوان، خصائص مباشرة)، يقوم بوينتر بتقسيم العمل: النموذج المتميز يخطط ويراجع، بينما يتولى النموذج الاقتصادي كتابة التعديلات الميكانيكية والترجمة.",
      graph_bar_a_title: "نموذج واحد يقوم بكل شيء",
      graph_bar_a_desc: "الفحص، المراجعة، التعديلات، والترجمة — كلها تستهلك النموذج باهظ التكلفة.",
      graph_bar_b_title: "بوينتر: تفويض واعٍ للتكلفة",
      graph_seg_review: "الفحص والمراجعة (النموذج المتميز)",
      graph_seg_worker: "التعديلات الميكانيكية والترجمة ← نموذج اقتصادي",
      graph_legend_1: "تعديلات ميكانيكية (تعديل النصوص، تغيير الألوان، الخصائص المباشرة)",
      graph_legend_2: "ترجمة فورية للتعليقات والردود المتبادلة",
      graph_honesty_note: "استهلاك أقل لرموز النموذج المتميز في عمليات الطباعة والترجمة الروتينية.",

      // Section 8: Features Grid
      feat_badge: "الميزات الأساسية",
      feat_title: "مصمم لبيئات الإنتاج الحقيقية",
      feat_subtitle: "تكامل سلس خفيف الوزن دون حزم ثقيلة أو فرض أطر عمل معينة.",
      feat_card1_title: "تثبيت في سطرين",
      feat_card1_desc: "وسم سكريبت وعنصر مخصص، بدون حزم SDK ضخمة تؤثر على سرعة الموقع.",
      feat_card2_title: "تعدد المشاريع وبيئات العمل",
      feat_card2_desc: "عزل أمني صارم على الخادم مع دعم البيئات المتعددة (محلي، تجريبي، إنتاج).",
      feat_card3_title: "عزل تام في Shadow DOM",
      feat_card3_desc: "أنماط معزولة بالكامل؛ لا تتأثر الأداة بتنسيقات موقعك ولا تؤثر عليه.",
      feat_card4_title: "إضافة المتصفح",
      feat_card4_desc: "تعليق دقيق على مواقع العملاء وبيئات الاختبار التي لا تملك حق تعديل شفرتها.",
      feat_chip1: "إخفاء قيم المدخلات الحساسة",
      feat_chip2: "رصد واستبعاد المفاتيح والرموز السرية",
      feat_chip3: "متابعة النشر (تم التطبيق مقابل مباشر)",
      feat_chip4: "استضافة ذاتية: واجهة API وقاعدة Postgres",

      // Section 9: Works with your stack
      stack_title: "يعمل مع بنيتك البرمجية",
      stack_subtitle: "إحصائيات حية من المشاريع وأدوات البرمجة بالذكاء الاصطناعي النشطة.",
      stack_group_frameworks: "أطر العمل والبنية التحتية:",
      stack_group_tools: "أدوات البرمجة بالذكاء الاصطناعي:",

      // Section 10: Stats
      stats_title: "بوينتر في أرقام",
      stats_subtitle: "بيانات تشغيلية عامة اجتازت حدود الخصوصية والأمان.",
      stats_applied: "تعليقاً تم تطبيقه",
      stats_projects: "مشروعاً نشطاً",
      stats_workspaces: "مساحات عمل",
      stats_median: "متوسط وقت التطبيق",
      stats_languages_prefix: "لغات الملاحظات المسجلة: ",

      // Section 11: Whole Team
      team_badge: "التعاون المشترك",
      team_title: "مصمم للفريق بأكمله",
      team_subtitle: "أصحاب المصلحة يقدمون ملاحظات واضحة، والمطورون يتلقون مهاماً برمجية دقيقة.",
      team_stakeholder_title: "لأصحاب المصلحة والعملاء",
      team_stakeholder_p1: "انقر مباشرة على التطبيق الفعلي واكتب بلغتك الأم دون تثبيت أي برامج.",
      team_stakeholder_p2: "لا يتطلب أي معرفة تقنية أو خبرة برمجية.",
      team_stakeholder_p3: "تتم ترجمة الملاحظات تلقائياً وتعود الردود بلغتك الأصلية.",
      team_stakeholder_p4: "اعتمد التعديلات المنفذة بنقرة واحدة 👍 أو 👎 عند نشرها.",
      team_dev_title: "للمطورين ومهندسي الذكاء الاصطناعي",
      team_dev_p1: "اسحب قائمة الملاحظات عبر HTTP باستخدام أداة البرمجة المفضلة لديك.",
      team_dev_p2: "يبدأ الوكيل من المحدد والقواعد الفائزة (ومسار الملف عند تفعيل ربط المصدر).",
      team_dev_p3: "متوافق مع Claude Code, Cursor, Windsurf, OpenCode, و Antigravity.",
      team_dev_p4: "ينشئ الوكيل إيداعاً محلياً مع رابط توثيقي؛ أنت من يراجع الفروقات قبل الرفع.",

      // Section 12: Trust & Privacy
      trust_badge: "الأمان والخصوصية",
      trust_title: "الثقة والخصوصية",
      trust_subtitle: "حدود صارمة لحماية شفرتك البرمجية وبيانات مستخدميك.",
      trust_q1: "هل يقوم الذكاء الاصطناعي بتعديل الكود مباشرة في المستودع؟",
      trust_a1: "ينشئ الوكيل إيداعاً محلياً (Git Commit) ولا يقوم بالرفع أبداً (Never Pushes). أنت من يراجع الفروقات بدقة ويقرر الرفع، ويحمل كل تعليق رابط الإيداع الخاص به.",
      trust_q2: "ما هي البيانات التي تجمعونها؟",
      trust_a2: "بيانات وصفية على مستوى عنصر DOM فقط (المحدد، القواعد الفائزة، المسار). لا نلتقط أبداً قيم الحقول، كلمات المرور، ملفات تعريف الارتباط، أو محتوى طلبات الشبكة. راجع صفحة البيانات والخصوصية.",
      trust_q3: "ما هي أداة الذكاء الاصطناعي التي يمكنني استخدامها؟",
      trust_a3: "أي أداة برمجة تفضلها (Claude Code, Cursor, Windsurf, OpenCode, Antigravity) عبر طلبات HTTP عادية دون أي احتكار.",
      trust_q4: "هل بياناتي محبوسة في خوادمكم؟",
      trust_a4: "يمكنك استضافة النظام ذاتياً بالكامل (Self-Hosted): واجهة برمجية مفتوحة وقاعدة بيانات Postgres لتبقى بياناتك في خوادمك الخاصة.",

      // Section 13: Pricing
      pricing_badge: "باقات واضحة",
      pricing_title: "خطط تسعير بسيطة وشفافة",
      pricing_subtitle: "ابدأ مجاناً على جهازك، وانتقل للخطط المدفوعة عندما يحتاج فريقك للتعاون.",
      pricing_coming_soon: "قريباً",
      pricing_free_title: "المجانية",
      pricing_free_price: "$0",
      pricing_free_period: "دائماً",
      pricing_free_b1: "3 مشاريع",
      pricing_free_b2: "5 مقاعد",
      pricing_free_b3: "100 تعليق / شهرياً",
      pricing_free_b4: "دعم المجتمع",
      pricing_pro_title: "الاحترافية",
      pricing_pro_price: "$5",
      pricing_pro_period: "شهرياً",
      pricing_pro_b1: "مشاريع غير محدودة",
      pricing_pro_b2: "مقاعد غير محدودة",
      pricing_pro_b3: "دعم ذو أولوية",
      pricing_pro_b4: "تفويض واعٍ للتكلفة",
      pricing_cta_account: "إنشاء حساب",

      // Section 14: Extension Stepper
      ext_badge: "إضافة المتصفح",
      ext_title: "إضافة المتصفح ودليل التثبيت التفاعلي",
      ext_subtitle: "علّق على أي موقع عميل أو بيئة تجريبية خارجية دون الحاجة لتعديل شفرة الموقع.",
      ext_tab1: "1. التحميل",
      ext_tab2: "2. فك الضغط",
      ext_tab3: "3. الإضافات",
      ext_tab4: "4. التحميل",
      ext_tab5: "5. البدء",
      ext_s1_title: "الخطوة 1: تحميل الإضافة",
      ext_s1_desc: "قم بتحميل حزمة الإضافة أو تثبيتها مباشرة من متجر Chrome الإلكتروني.",
      ext_s1_btn: "تثبيت الإضافة",
      ext_s1_btn_zip: "تحميل حزمة ZIP",
      ext_s2_title: "الخطوة 2: فك ضغط الحزمة",
      ext_s2_desc: "فك ضغط الملف المضغوط إلى مجلد دائم على جهازك الشخصي.",
      ext_s3_title: "الخطوة 3: فتح صفحة الإضافات",
      ext_s3_desc: "في متصفح Chrome أو Brave أو Edge، اكتب chrome://extensions في شريط العناوين.",
      ext_s4_title: "الخطوة 4: تفعيل وضع المطور وتحميل المجلد",
      ext_s4_desc: "فعّل خيار Developer mode في الزاوية، وانقر على Load unpacked واختر المجلد المفكوك.",
      ext_s5_title: "الخطوة 5: تسجيل الدخول والبدء",
      ext_s5_desc: "انقر على أيقونة Pointer في شريط المتصفح، وسجل الدخول، ثم انقر على أي عنصر في التطبيق.",
      ext_next_btn: "الخطوة التالية",
      ext_prev_btn: "الخطوة السابقة",

      // Section 15: Final CTA & Footer
      final_title: "ابدأ بتوجيه وكيل الذكاء الاصطناعي ببيانات دقيقة اليوم.",
      final_subtitle: "تخلص من لقطات الشاشة المبهمة والحلقات الاستكشافية الضائعة.",
      final_cta_demo: "جرب العرض التوضيحي — بدون تثبيت",
      final_cta_account: "إنشاء حساب",
      footer_tagline: "الملاحظات الدقيقة لأدوات البرمجة بالذكاء الاصطناعي.",
      footer_review_note: "إيداع محلي، بدون رفع تلقائي. مصمم للمراجعة البشرية.",
      footer_col_docs: "التوثيق",
      footer_link_install: "دليل التثبيت",
      footer_link_apply: "تطبيق الملاحظات",
      footer_link_keys: "مفاتيح API",
      footer_link_alldocs: "كافة التوثيقات",
      footer_col_app: "المنتج",
      footer_link_dashboard: "لوحة التحكم",
      footer_link_demo: "العرض التوضيحي",
      footer_link_extension: "إضافة المتصفح",
      footer_col_trust: "الثقة والقانونية",
      footer_link_privacy: "سياسة الخصوصية",
      footer_link_data: "البيانات والاستضافة الذاتية",
      footer_link_github: "مستودع GitHub",
      footer_copy: "بوينتر. طبقة الإدخال الدقيقة لأدوات البرمجة بالذكاء الاصطناعي."
    }
  };

  // Helper: Fetch with timeout
  function fetchWithTimeout(url, timeoutMs) {
    timeoutMs = timeoutMs || 2000;
    var controller = new AbortController();
    var id = setTimeout(function() { controller.abort(); }, timeoutMs);
    return fetch(url, { signal: controller.signal })
      .then(function(res) {
        clearTimeout(id);
        if (!res.ok) throw new Error('HTTP ' + res.status);
        return res.json();
      });
  }

  // Token humanizer mapping for Stacks
  var STACK_NAMES = {
    'react': 'React',
    'dotnet': '.NET',
    'aspnetmvc': 'ASP.NET MVC',
    'tailwind': 'Tailwind CSS',
    'vite': 'Vite',
    'razor': 'Razor',
    'alpine': 'Alpine.js',
    'mysql': 'MySQL',
    'redis': 'Redis',
    'node': 'Node.js',
    'claude-code': 'Claude Code',
    'antigravity': 'Antigravity',
    'cursor': 'Cursor',
    'windsurf': 'Windsurf',
    'opencode-glm': 'OpenCode + GLM',
    'other': 'Other Tools'
  };

  function humanizeToken(token) {
    if (STACK_NAMES[token]) return STACK_NAMES[token];
    return token.charAt(0).toUpperCase() + token.slice(1);
  }

  // Language management
  function applyLanguage(lang) {
    state.lang = lang;
    var dict = TRANSLATIONS[lang] || TRANSLATIONS.en;
    var isRtl = (lang === 'ar');

    document.documentElement.setAttribute('dir', isRtl ? 'rtl' : 'ltr');
    document.documentElement.setAttribute('lang', lang);

    var toggleBtn = document.getElementById('lang-toggle');
    if (toggleBtn) {
      toggleBtn.textContent = isRtl ? 'EN' : 'AR';
      toggleBtn.setAttribute('aria-label', isRtl ? 'Switch to English' : 'التبديل إلى العربية');
    }

    // Replace all static data-i18n elements
    document.querySelectorAll('[data-i18n]').forEach(function(el) {
      var key = el.getAttribute('data-i18n');
      if (dict[key]) {
        el.textContent = dict[key];
      }
    });

    // Re-render dynamic components with translated labels
    if (state.plans) renderPlans(state.plans);
    if (state.stats) renderStats(state.stats);
    if (state.stacks) renderStacks(state.stacks);

    try {
      localStorage.setItem('pointer-lang', lang);
    } catch (e) {}
  }

  // Theme management
  function applyTheme(theme) {
    state.theme = theme;
    document.documentElement.setAttribute('data-theme', theme);
    try {
      localStorage.setItem('pointer-theme', theme);
    } catch (e) {}
  }

  // Branding Integration
  function loadBranding() {
    fetchWithTimeout(API_BASE + '/api/branding', 2500)
      .then(function(json) {
        var data = json.data || json;
        if (!data) return;

        if (data.productName) {
          state.branding.productName = data.productName;
          document.querySelectorAll('[data-brand-name]').forEach(function(el) {
            el.textContent = data.productName;
          });
        }

        if (data.assets && data.assets.logo) {
          var navLogo = document.getElementById('nav-logo');
          if (navLogo) navLogo.src = data.assets.logo;
          var footerLogo = document.getElementById('footer-logo');
          if (footerLogo) footerLogo.src = data.assets.logo;
        }

        if (data.assets && data.assets.favicon) {
          var fav = document.getElementById('favicon');
          if (fav) fav.href = data.assets.favicon;
        }

        if (data.urls) {
          state.branding.urls = Object.assign({}, state.branding.urls, data.urls);
          if (data.urls.app) {
            document.querySelectorAll('a[href^="https://app.pointer.moamen.work"]').forEach(function(el) {
              el.href = data.urls.app;
            });
          }
          if (data.urls.demo) {
            document.querySelectorAll('a[href^="https://demo.pointer.moamen.work"]').forEach(function(el) {
              el.href = data.urls.demo;
            });
          }
          if (data.urls.docs) {
            document.querySelectorAll('[data-link="docs"]').forEach(function(el) {
              el.href = data.urls.docs;
            });
          }
        }

        if (data.extension) {
          state.branding.extension = Object.assign({}, state.branding.extension, data.extension);
          var extBtn = document.getElementById('ext-primary-btn');
          if (extBtn) {
            if (data.extension.storeUrl) {
              extBtn.href = data.extension.storeUrl;
              extBtn.target = '_blank';
            } else if (data.extension.zipUrl) {
              extBtn.href = data.extension.zipUrl;
              extBtn.download = 'pointer-extension.zip';
            }
          }
        }
      })
      .catch(function(err) {
        console.warn('Branding API load skipped, using bundled defaults.', err);
      });
  }

  // Plans Integration (Section 13)
  function renderPlans(list) {
    var container = document.getElementById('plans-grid');
    if (!container) return;
    container.innerHTML = '';

    if (!list || !list.length) {
      renderPlansFallback();
      return;
    }

    var dict = TRANSLATIONS[state.lang] || TRANSLATIONS.en;
    list.sort(function(a, b) { return (a.sortOrder || 0) - (b.sortOrder || 0); });

    list.forEach(function(plan) {
      var isComingSoon = plan.displayState === 1;
      var card = document.createElement('div');
      card.className = 'pricing-card' + (isComingSoon ? ' is-dimmed' : '');

      var isFree = !plan.priceMonthly || plan.priceMonthly === 0;
      var priceDisplay = isFree ? (dict.pricing_free_price || '$0') : ('$' + plan.priceMonthly);
      var cadenceDisplay = isFree ? (dict.pricing_free_period || 'forever') : (dict.pricing_pro_period || '/mo');

      var bullets = (plan.featureBullets && plan.featureBullets.length)
        ? plan.featureBullets
        : (isFree ? [dict.pricing_free_b1, dict.pricing_free_b2, dict.pricing_free_b3, dict.pricing_free_b4]
                  : [dict.pricing_pro_b1, dict.pricing_pro_b2, dict.pricing_pro_b3, dict.pricing_pro_b4]);

      var html = '';
      html += '<div class="pricing-card-header">';
      html += '  <div class="pricing-card-meta">';
      html += '    <h3 class="pricing-plan-name">' + plan.name + '</h3>';
      if (isComingSoon) {
        html += '    <span class="pricing-badge">' + (dict.pricing_coming_soon || 'Coming soon') + '</span>';
      }
      html += '  </div>';
      html += '  <div class="pricing-price-wrap">';
      html += '    <span class="pricing-price">' + priceDisplay + '</span>';
      html += '    <span class="pricing-cadence">' + cadenceDisplay + '</span>';
      html += '  </div>';
      html += '</div>';

      html += '<ul class="pricing-bullets">';
      bullets.forEach(function(b) {
        if (b) html += '<li><span class="bullet-check">✓</span> <span>' + b + '</span></li>';
      });
      html += '</ul>';

      if (!isComingSoon) {
        html += '<div class="pricing-cta-wrap">';
        html += '  <a href="' + state.branding.urls.app + '" class="btn btn-outline" style="width:100%;">' + (dict.pricing_cta_account || 'Create an account') + '</a>';
        html += '</div>';
      } else {
        html += '<div class="pricing-cta-wrap">';
        html += '  <span class="btn btn-outline is-disabled" style="width:100%;opacity:0.5;pointer-events:none;">' + (dict.pricing_coming_soon || 'Coming soon') + '</span>';
        html += '</div>';
      }

      card.innerHTML = html;
      container.appendChild(card);
    });
  }

  function renderPlansFallback() {
    var container = document.getElementById('plans-grid');
    if (!container) return;
    var dict = TRANSLATIONS[state.lang] || TRANSLATIONS.en;

    container.innerHTML = [
      '<div class="pricing-card">',
      '  <div class="pricing-card-header">',
      '    <h3 class="pricing-plan-name">' + (dict.pricing_free_title || 'Free') + '</h3>',
      '    <div class="pricing-price-wrap"><span class="pricing-price">$0</span><span class="pricing-cadence">' + (dict.pricing_free_period || 'forever') + '</span></div>',
      '  </div>',
      '  <ul class="pricing-bullets">',
      '    <li><span class="bullet-check">✓</span> <span>' + dict.pricing_free_b1 + '</span></li>',
      '    <li><span class="bullet-check">✓</span> <span>' + dict.pricing_free_b2 + '</span></li>',
      '    <li><span class="bullet-check">✓</span> <span>' + dict.pricing_free_b3 + '</span></li>',
      '    <li><span class="bullet-check">✓</span> <span>' + dict.pricing_free_b4 + '</span></li>',
      '  </ul>',
      '  <div class="pricing-cta-wrap"><a href="' + state.branding.urls.app + '" class="btn btn-outline" style="width:100%;">' + dict.pricing_cta_account + '</a></div>',
      '</div>',
      '<div class="pricing-card">',
      '  <div class="pricing-card-header">',
      '    <h3 class="pricing-plan-name">' + (dict.pricing_pro_title || 'Pro') + '</h3>',
      '    <div class="pricing-price-wrap"><span class="pricing-price">$5</span><span class="pricing-cadence">' + (dict.pricing_pro_period || 'per month') + '</span></div>',
      '  </div>',
      '  <ul class="pricing-bullets">',
      '    <li><span class="bullet-check">✓</span> <span>' + dict.pricing_pro_b1 + '</span></li>',
      '    <li><span class="bullet-check">✓</span> <span>' + dict.pricing_pro_b2 + '</span></li>',
      '    <li><span class="bullet-check">✓</span> <span>' + dict.pricing_pro_b3 + '</span></li>',
      '    <li><span class="bullet-check">✓</span> <span>' + dict.pricing_pro_b4 + '</span></li>',
      '  </ul>',
      '  <div class="pricing-cta-wrap"><a href="' + state.branding.urls.app + '" class="btn btn-primary" style="width:100%;">' + dict.pricing_cta_account + '</a></div>',
      '</div>'
    ].join('');
  }

  function loadPlans() {
    fetchWithTimeout(API_BASE + '/api/plans', 2500)
      .then(function(json) {
        var list = json && (json.data || json);
        if (Array.isArray(list) && list.length > 0) {
          state.plans = list;
          renderPlans(list);
        } else {
          renderPlansFallback();
        }
      })
      .catch(function(err) {
        console.warn('Plans fetch failed, rendering fallback.', err);
        renderPlansFallback();
      });
  }

  // Stacks Summary Integration (Section 9)
  function renderStacks(data) {
    var section = document.getElementById('stacks-section');
    if (!section) return;

    if (!data || !data.totalProjects || data.totalProjects <= 0) {
      section.style.display = 'none';
      return;
    }

    var dict = TRANSLATIONS[state.lang] || TRANSLATIONS.en;
    section.style.display = 'block';

    var frameworkListEl = document.getElementById('stacks-frameworks-list');
    var toolsListEl = document.getElementById('stacks-tools-list');

    // Combine frontend and backend
    var stackCombined = {};
    if (data.frontend) {
      for (var f in data.frontend) { stackCombined[f] = (stackCombined[f] || 0) + data.frontend[f]; }
    }
    if (data.backend) {
      for (var b in data.backend) { stackCombined[b] = (stackCombined[b] || 0) + data.backend[b]; }
    }

    var sortedStacks = Object.keys(stackCombined).sort(function(a, b) {
      return stackCombined[b] - stackCombined[a];
    });

    var sortedTools = data.aiTools ? Object.keys(data.aiTools).sort(function(a, b) {
      return data.aiTools[b] - data.aiTools[a];
    }) : [];

    if (frameworkListEl) {
      frameworkListEl.innerHTML = sortedStacks.map(function(key) {
        return '<span class="stack-tag"><span class="tag-name">' + humanizeToken(key) + '</span><span class="tag-count mono">' + stackCombined[key] + '</span></span>';
      }).join('');
    }

    if (toolsListEl) {
      toolsListEl.innerHTML = sortedTools.map(function(key) {
        return '<span class="stack-tag stack-tool-tag"><span class="tag-name">' + humanizeToken(key) + '</span><span class="tag-count mono">' + data.aiTools[key] + '</span></span>';
      }).join('');
    }
  }

  function loadStacks() {
    var section = document.getElementById('stacks-section');
    fetchWithTimeout(API_BASE + '/api/public/stacks-summary', 2500)
      .then(function(json) {
        var data = json.data || json;
        if (data && data.totalProjects > 0) {
          state.stacks = data;
          renderStacks(data);
        } else {
          if (section) section.style.display = 'none';
        }
      })
      .catch(function(err) {
        if (section) section.style.display = 'none';
      });
  }

  // Pointer in Numbers Integration (Section 10)
  function renderStats(data) {
    var section = document.getElementById('stats-section');
    var grid = document.getElementById('stats-grid');
    if (!section || !grid) return;

    if (!data) {
      section.style.display = 'none';
      return;
    }

    var dict = TRANSLATIONS[state.lang] || TRANSLATIONS.en;
    var cards = [];

    if (typeof data.appliedComments === 'number' && data.appliedComments > 0) {
      cards.push({
        num: data.appliedComments + '+',
        label: dict.stats_applied || 'Applied comments'
      });
    }

    if (typeof data.projects === 'number' && data.projects > 0) {
      cards.push({
        num: data.projects + '+',
        label: dict.stats_projects || 'Active projects'
      });
    }

    if (typeof data.workspaces === 'number' && data.workspaces > 0) {
      cards.push({
        num: data.workspaces + '+',
        label: dict.stats_workspaces || 'Workspaces'
      });
    }

    if (typeof data.medianHoursToApply === 'number' && data.medianHoursToApply > 0) {
      var h = data.medianHoursToApply;
      var formatted = '';
      if (h < 1) {
        var mins = Math.max(1, Math.round(h * 60));
        formatted = state.lang === 'ar' ? ('أقل من ' + mins + ' دقيقة') : ('under ' + mins + ' min');
      } else if (h < 48) {
        formatted = h.toFixed(1) + (state.lang === 'ar' ? ' ساعة' : ' hours');
      } else {
        var days = Math.round(h / 24);
        formatted = days + (state.lang === 'ar' ? ' أيام' : ' days');
      }
      cards.push({
        num: formatted,
        label: dict.stats_median || 'Median time to apply'
      });
    }

    if (!cards.length) {
      section.style.display = 'none';
      return;
    }

    section.style.display = 'block';
    grid.innerHTML = cards.map(function(c) {
      return [
        '<div class="stat-card">',
        '  <div class="stat-number mono">' + c.num + '</div>',
        '  <div class="stat-label">' + c.label + '</div>',
        '</div>'
      ].join('');
    }).join('');

    // Handle languages row if non-empty
    var langContainer = document.getElementById('stats-languages');
    if (langContainer) {
      if (data.languages && data.languages.length > 0) {
        langContainer.style.display = 'flex';
        var displayNames = null;
        try {
          if (typeof Intl !== 'undefined' && Intl.DisplayNames) {
            displayNames = new Intl.DisplayNames([state.lang], { type: 'language' });
          }
        } catch (e) {}

        var langTokens = data.languages.map(function(code) {
          var name = code;
          if (displayNames) {
            try { name = displayNames.of(code) || code; } catch (e) {}
          }
          return '<span class="lang-pill mono">' + name + '</span>';
        }).join('');

        langContainer.innerHTML = '<span class="lang-prefix">' + (dict.stats_languages_prefix || 'Feedback written in: ') + '</span>' + langTokens;
      } else {
        langContainer.style.display = 'none';
      }
    }
  }

  function loadStats() {
    var section = document.getElementById('stats-section');
    fetchWithTimeout(API_BASE + '/api/public/stats', 2500)
      .then(function(json) {
        var data = json.data || json;
        if (data) {
          state.stats = data;
          renderStats(data);
        } else {
          if (section) section.style.display = 'none';
        }
      })
      .catch(function(err) {
        if (section) section.style.display = 'none';
      });
  }

  // Cost-Aware Apply Graph Scroll Trigger & Accessibility
  function initCostGraph() {
    var graphCard = document.getElementById('cost-graph-card');
    if (!graphCard) return;

    var prefersReduced = false;
    try {
      prefersReduced = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
    } catch (e) {}

    if (prefersReduced) {
      // Keep static end-state immediately
      graphCard.classList.add('is-animated');
      return;
    }

    document.documentElement.classList.add('js-ready');

    if (typeof IntersectionObserver !== 'undefined') {
      var observer = new IntersectionObserver(function(entries) {
        entries.forEach(function(entry) {
          if (entry.isIntersecting) {
            graphCard.classList.add('is-animated');
            observer.unobserve(entry.target);
          }
        });
      }, { threshold: 0.2 });
      observer.observe(graphCard);
    } else {
      graphCard.classList.add('is-animated');
    }
  }

  // Walkable Stepper (Section 14)
  function initStepper() {
    var tabs = document.querySelectorAll('.stepper-tab');
    var steps = document.querySelectorAll('.stepper-pane');
    if (!tabs.length || !steps.length) return;

    function goToStep(stepIndex) {
      tabs.forEach(function(t, idx) {
        var isActive = idx === stepIndex;
        var isPassed = idx < stepIndex;
        t.setAttribute('aria-selected', isActive ? 'true' : 'false');
        t.classList.toggle('is-active', isActive);
        t.classList.toggle('is-passed', isPassed);
      });

      steps.forEach(function(s, idx) {
        s.classList.toggle('is-active', idx === stepIndex);
      });
    }

    tabs.forEach(function(tab, idx) {
      tab.addEventListener('click', function() {
        goToStep(idx);
      });
      tab.addEventListener('keydown', function(e) {
        if (e.key === 'ArrowRight' || e.key === 'ArrowDown') {
          e.preventDefault();
          var next = (idx + 1) % tabs.length;
          tabs[next].focus();
          goToStep(next);
        } else if (e.key === 'ArrowLeft' || e.key === 'ArrowUp') {
          e.preventDefault();
          var prev = (idx - 1 + tabs.length) % tabs.length;
          tabs[prev].focus();
          goToStep(prev);
        }
      });
    });

    // Wire Next & Prev buttons
    document.querySelectorAll('[data-stepper-next]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var current = 0;
        tabs.forEach(function(t, i) { if (t.classList.contains('is-active')) current = i; });
        if (current < tabs.length - 1) goToStep(current + 1);
      });
    });

    document.querySelectorAll('[data-stepper-prev]').forEach(function(btn) {
      btn.addEventListener('click', function() {
        var current = 0;
        tabs.forEach(function(t, i) { if (t.classList.contains('is-active')) current = i; });
        if (current > 0) goToStep(current - 1);
      });
    });
  }

  // Setup DOM Event Listeners
  document.addEventListener('DOMContentLoaded', function() {
    // 1. Language Init
    var savedLang = 'en';
    try {
      savedLang = localStorage.getItem('pointer-lang') || 'en';
    } catch (e) {}
    applyLanguage(savedLang);

    var langToggle = document.getElementById('lang-toggle');
    if (langToggle) {
      langToggle.addEventListener('click', function() {
        var nextLang = state.lang === 'ar' ? 'en' : 'ar';
        applyLanguage(nextLang);
      });
    }

    // 2. Theme Init
    var themeToggle = document.getElementById('theme-toggle');
    if (themeToggle) {
      themeToggle.addEventListener('click', function() {
        var current = document.documentElement.getAttribute('data-theme') || 'light';
        var next = current === 'dark' ? 'light' : 'dark';
        applyTheme(next);
      });
    }

    // 3. API Integrations
    loadBranding();
    loadPlans();
    loadStacks();
    loadStats();

    // 4. Interactive Components
    initCostGraph();
    initStepper();
  });

})();
