(function () {
  "use strict";

  var docEl = document.documentElement;

  var STRINGS = {
    en: {
      title: "Pointer — A comment becomes a change",
      skip: "Skip to content",
      "nav.brief": "The brief",
      "nav.how": "How it works",
      "nav.pricing": "Pricing",
      "nav.extension": "Extension",
      "nav.docs": "Docs",
      "nav.signin": "Sign in",
      "nav.demo": "Try the demo",
      "nav.menu": "Menu",
      "hero.kicker": "Element-level feedback · AI-ready",
      "hero.title": "A comment becomes a change.",
      "hero.sub":
        "Pointer pins a stakeholder’s one sentence to the exact element they clicked, detects their language, and hands your AI coding tool a structured brief — selector, winning CSS, route, viewport. The agent edits the real source, commits, never pushes. You review the diff.",
      "hero.cta1": "Try the demo — no install",
      "hero.cta2": "Create an account",
      "hero.proof": "plain HTTP · any AI tool · every language, translated in",
      "hero.sr":
        "How Pointer works, shown as a demo: a stakeholder writes a comment in Arabic on an upgrade button; Pointer translates it to English, builds a structured brief — selector, winning CSS, route, viewport, source — and an AI tool applies the edit as a commit that awaits human review. Nothing is ever pushed automatically.",
      "lang.toggle": "ع",
      "lang.aria": "Switch to Arabic",
      "theme.dark": "Switch to dark mode",
      "theme.light": "Switch to light mode",
      "brief.kicker": "The brief",
      "brief.title": "One comment, nine coordinates.",
      "brief.sub":
        "Every comment ships as a structured brief the agent can act on directly — not a screenshot with a sentence attached.",
      "brief.f1": "Names the exact DOM element. No screenshot, no description needed.",
      "brief.f2":
        "The element’s own opening tag, attributes and text — capped short, deliberately shallow so the model isn’t drowned in child markup.",
      "brief.f3":
        "The rules that actually win — not the full computed dump. Answers “why is it blue?” before the agent has to ask.",
      "brief.f4": "Disambiguate the structure when the selector alone is generic.",
      "brief.f5": "Identifies which screen or view to open.",
      "brief.f6": "Turns “broken on mobile” into a reproducible condition.",
      "brief.f7": "Console errors and failed or slow network requests, captured at the moment of the issue.",
      "brief.f8": "The source file behind the element, when mapping is enabled — tiered by what the build exposes.",
      "brief.f9": "The BCP-47 tag the comment was detected in — so translation is automatic, in both directions.",
      "brief.optin": "opt-in · bug reports",
      "brief.mapping": "when source mapping is on",
      "how.kicker": "The loop",
      "how.title": "Point. Triage. Apply. Commit.",
      "how.sub":
        "Four steps from a stakeholder’s sentence to a reviewed commit — and the reply comes back in their own language.",
      "how.tab1": "Point & comment",
      "how.tab2": "Triage",
      "how.tab3": "Applied by AI",
      "how.tab4": "Committed, never pushed",
      "how.p1":
        "Anyone you invite can click any element on the running app and leave one sentence — in the language they think in. Pointer detects the language and translates it in automatically, on every install, by default.",
      "how.n1": "signed in · click · one sentence",
      "how.p2":
        "Comments land in one queue in the dashboard, tagged by project, environment, stakeholder and status — not scattered as screenshots across five channels.",
      "how.n2": "one queue · tagged · triaged",
      "how.p3":
        "A developer hands the queue to any AI coding tool over plain HTTP. The agent starts at the selector and the winning CSS rule — not at src/ — and when a run holds mechanical edits, they’re typed out by a cheaper model while the premium one plans and reviews.",
      "how.n3": "any tool · plain HTTP · no lock-in",
      "how.p4":
        "The agent commits in your project’s style and never pushes. Every applied comment carries its commit URL and links back to its author. You review the diff; when the fix ships, the author is notified and confirms — or reopens it.",
      "how.n4": "your review · then 👍 or 👎",
      "contrast.kicker": "The contrast",
      "contrast.title": "The same request, with and without coordinates.",
      "contrast.without": "Without Pointer",
      "contrast.with": "With Pointer",
      "contrast.w1": "“The checkout feels broken on my phone” — no element, no repro, no viewport.",
      "contrast.w2": "A developer guesses the file, the rule, the screen.",
      "contrast.w3": "An AI agent loops through blind exploration of the codebase before it touches the right file.",
      "contrast.w4": "The fix lands as a screenshot reply in a thread — nothing links back to a commit.",
      "contrast.c1":
        "Click the element — the brief already carries the selector, the winning CSS, the route, the viewport.",
      "contrast.c2": "The agent starts at the selector and the winning rule, not at src/.",
      "contrast.c3":
        "Mechanical edits and translation delegate to a cheaper model; judgment stays on the premium one.",
      "contrast.c4": "Every applied comment links back to its commit URL — committed, never pushed.",
      "why.kicker": "The margin",
      "why.title": "Why teams point instead of screenshot.",
      "why.t1tag": "time",
      "why.t1": "Context attached to the click",
      "why.b1":
        "No meeting to reconstruct what “broken on mobile” meant — the brief already says where, what, and at which viewport.",
      "why.t2tag": "cost",
      "why.t2": "Fewer clarification loops",
      "why.b2": "The ask arrives with its coordinates attached, so threads stay short and meetings turn into reviews.",
      "why.t3tag": "tokens",
      "why.t3": "Fewer premium-model tokens",
      "why.b3":
        "The agent starts at the selector and the winning CSS rule. Mechanical edits and translation go to a cheaper model, so the expensive one spends its tokens on the parts that need judgment.",
      "why.t4tag": "queue",
      "why.t4": "One organized queue",
      "why.b4": "Tagged, triaged, per project and environment — not screenshots in five channels.",
      "cost.kicker": "Cost-aware apply",
      "cost.title": "Delegation, drawn to shape.",
      "cost.sub":
        "When a run has 3+ comments in disjoint files and an edit is mechanical — a copy tweak, a color swap, a prop change — the orchestrating model plans and reviews while a cheaper worker types the edit out.",
      "cost.aSeg": "investigation · review · edits · translation",
      "cost.aName": "One model does everything",
      "cost.aNote": "every task on the premium model",
      "cost.bSeg1": "investigation + review",
      "cost.bSeg2": "mechanical edits + translation",
      "cost.bName": "Pointer: cost-aware delegation",
      "cost.l1": "mechanical edits — typed by the cheaper model",
      "cost.l2": "translation, both directions — cheaper model",
      "cost.cap":
        "Qualitative by rule: no savings percentage, token count, or speed figure is printed anywhere — none has been measured yet. The bar shapes show only that the delegated share is the minority; investigation and review remain the harder part.",
      "feat.kicker": "The kit",
      "feat.title": "Small to install. Wide to work with.",
      "feat.t1tag": "install",
      "feat.t1": "Two-line install",
      "feat.b1": "A script tag and a custom element. No SDK, no build step, no framework lock-in.",
      "feat.t2tag": "tenancy",
      "feat.t2": "Multi-project, multi-tenant",
      "feat.b2":
        "Workspaces with isolation enforced server-side. Every comment is tagged by project, environment, and stakeholder.",
      "feat.t3tag": "isolation",
      "feat.t3": "Shadow-DOM widget",
      "feat.b3":
        "The widget lives in its own shadow root — your styles can’t leak in, its styles can’t leak out. Any framework, any stack.",
      "feat.t4tag": "extension",
      "feat.t4": "Browser extension",
      "feat.b4":
        "The same element-level comments on pages you don’t host — client sites, staging behind login, third-party tools.",
      "feat.b4link": "See the install stepper",
      "feat.x1": "environments · local / staging / production",
      "feat.x2": "AI rules · workspace › project › personal",
      "feat.x3": "picked actions",
      "feat.x4": "CLI pointer-feedback",
      "feat.x5": "source mapping · Vite",
      "feat.x6": "deploy awareness · applied vs live",
      "feat.x7": "👍 / 👎 verification",
      "feat.x8": "secret detection",
      "feat.x9": "input-value masking",
      "feat.x10": "hashed, rotatable API keys",
      "feat.x11": "self-hostable · API + Postgres",
      "stacks.kicker": "Field evidence",
      "stacks.title": "Works with your stack.",
      "stacks.sub":
        "Anonymized counts from projects syncing feedback through Pointer — totals live from the server, names withheld.",
      "stacks.stacksLabel": "stacks",
      "stacks.aiLabel": "AI tools",
      "stats.kicker": "The count",
      "stats.title": "Pointer in numbers.",
      "stats.sub":
        "Public totals, thresholded for anonymity — each one appears only once it clears the server’s bar.",
      "stats.langLabel": "feedback written in",
      "stats.aiLabel": "applied by",
      "stats.mApplied": "applied comments",
      "stats.mProjects": "projects",
      "stats.mWorkspaces": "workspaces",
      "stats.mMedian": "median time to apply",
      "stats.min": "under {n} min",
      "stats.hours": "{n} hours",
      "stats.days": "{n} days",
      "team.kicker": "The crew",
      "team.title": "Built for the whole team.",
      "team.h1": "The people who point",
      "team.b1":
        "Clients, PMs, testers — invited in, nothing to install, nothing to learn. Click any element, type one sentence in your own language, done. Replies come back in that same language.",
      "team.n1": "invited · no install · any language",
      "team.h2": "The people who apply",
      "team.b2":
        "Pull the queue with any AI coding tool over plain HTTP, let cost-aware delegation type the mechanical parts, and review the result. Committed, never pushed — the final word is yours.",
      "team.n2": "any tool · plain HTTP · your review",
      "trust.kicker": "On the record",
      "trust.title": "Fair questions, straight answers.",
      "trust.q1": "“An AI edits my code?”",
      "trust.a1":
        "It commits, never pushes. You review every diff before it lands, and each applied comment carries its commit URL — the change traces back to the ask.",
      "trust.q2": "“What exactly do you capture?”",
      "trust.a2":
        "Element-level DOM metadata only: selector, winning CSS, route, viewport. Never form values, cookies, localStorage, or request bodies.",
      "trust.l1": "Privacy",
      "trust.l2": "Data & self-hosting",
      "trust.l2b": "Self-hosting details",
      "trust.q3": "“Which AI tool do I need?”",
      "trust.a3":
        "Any. The queue is plain HTTP, so whatever agent you already use can pull it — and if your tool speaks MCP, there’s a server for that too. No lock-in.",
      "trust.q4": "“Is my data stuck with you?”",
      "trust.a4": "No. Pointer self-hosts: the API plus a Postgres database you own. Your data stays yours.",
      "plan.kicker": "Pricing",
      "plan.title": "Pricing.",
      "plan.sub": "Rendered live from the server — what you see is what the API says right now.",
      "plan.fbTitle": "Plans are being drawn up.",
      "plan.fbBody":
        "Pricing isn’t reachable from the server at this moment. You can still create an account and start pointing.",
      "plan.free": "Free",
      "plan.mo": "/ mo",
      "plan.yr": "/ yr",
      "plan.soon": "Coming soon",
      "ext.kicker": "The extension",
      "ext.title": "Feedback on sites you don’t host.",
      "ext.sub":
        "The browser extension pins the same element-level comments on pages you can’t instrument — client sites, staging behind login, third-party tools.",
      "ext.store": "Install from the Chrome Web Store",
      "ext.zip": "Download the zip",
      "ext.stepsDone": "steps done",
      "ext.reset": "Reset",
      "ext.s1": "Download the extension zip and keep it somewhere stable",
      "ext.s2": "Unzip it",
      "ext.s3": "Open chrome://extensions",
      "ext.s4": "Switch on Developer mode",
      "ext.s5": "Click “Load unpacked” and pick the unzipped folder",
      "ext.s6": "Sign in from the extension’s toolbar button",
      "cta.kicker": "Plot your first comment",
      "cta.title": "Point at it. It gets done.",
      "cta.sub":
        "Open the demo and leave a comment on anything — no install, no account. Or set up a project and point your own app.",
      "cta.proof": "committed, never pushed · every language, translated in",
      "foot.tagline": "A comment becomes a change.",
      "foot.docsH": "Docs",
      "foot.docsInstall": "Install",
      "foot.docsApply": "Apply feedback",
      "foot.docsMcp": "MCP server",
      "foot.docsKeys": "API keys",
      "foot.docsAll": "All docs",
      "foot.productH": "Product",
      "foot.dashboard": "Dashboard",
      "foot.legalH": "Legal & more",
      "foot.privacy": "Privacy",
      "foot.data": "Data & self-hosting",
      "foot.github": "GitHub",
      "foot.note": "self-hostable · API + Postgres · your data stays yours"
    },
    ar: {
      title: "Pointer — تعليقٌ يتحوَّل إلى تغيير",
      skip: "تخطَّ إلى المحتوى",
      "nav.brief": "البيان",
      "nav.how": "كيف يعمل",
      "nav.pricing": "الأسعار",
      "nav.extension": "الإضافة",
      "nav.docs": "الوثائق",
      "nav.signin": "تسجيل الدخول",
      "nav.demo": "جرِّب العرض",
      "nav.menu": "القائمة",
      "hero.kicker": "ملاحظات على مستوى العنصر · جاهزة للبرمجة الذكية",
      "hero.title": "تعليقٌ يتحوَّل إلى تغيير.",
      "hero.sub":
        "تُثبِّت Pointer جملة صاحب المصلحة على العنصر الذي نقر عليه بالضبط، وتتعرَّف على لغته تلقائيًا، ثم تُسلِّم أداة البرمجة الذكية بيانًا مهيكلًا: المُحدِّد، وقواعد CSS الفاعلة، والمسار، ومنفذ العرض. تُعدِّل الأداة الكود المصدري الحقيقي، وتُنشئ commit من دون دفعه أبدًا — والمراجعة النهائية لك.",
      "hero.cta1": "جرِّب العرض التجريبي — دون تثبيت",
      "hero.cta2": "أنشئ حسابًا",
      "hero.proof": "‏HTTP صِرف · أيُّ أداة ذكاء اصطناعي · كل اللغات، مترجَمة تلقائيًا",
      "hero.sr":
        "آلية عمل Pointer في العرض التجريبي: يكتب صاحب المصلحة تعليقًا بالعربية على زرّ الترقية؛ فتترجمه Pointer إلى الإنجليزية وتبني بيانًا مهيكلًا — المُحدِّد، وقواعد CSS الفاعلة، والمسار، ومنفذ العرض، والملف المصدر — ثم تُطبِّقه أداة ذكاء اصطناعي كـ commit ينتظر مراجعتك، ولا يُدفع أبدًا تلقائيًا.",
      "lang.toggle": "EN",
      "lang.aria": "التبديل إلى الإنجليزية",
      "theme.dark": "التبديل إلى الوضع الداكن",
      "theme.light": "التبديل إلى الوضع الفاتح",
      "brief.kicker": "البيان",
      "brief.title": "تعليقٌ واحد، تسعُ إحداثيات.",
      "brief.sub":
        "كل تعليق يُشحن كبيانٍ مهيكل تستطيع أداة البرمجة الذكية التعامل معه مباشرة — لا لقطة شاشة بجملة ملحقة.",
      "brief.f1": "يسمّي عنصر DOM بدقة. لا حاجة إلى لقطة شاشة ولا وصف.",
      "brief.f2":
        "وسمُ العنصر الافتتاحي وخصائصه ونصّه — مقتطعٌ عمدًا ليظلّ سطحيًا فلا يُغرق النموذج في ترميز العناصر الأبناء.",
      "brief.f3":
        "القواعد التي تنتصر فعلًا — لا النسخة المحسوبة كاملة. يجيب عن «لماذا هو أزرق؟» قبل أن يضطر الوكيل إلى السؤال.",
      "brief.f4": "يزيل اللبس عن البنية حين يكون المُحدِّد وحده عامًا.",
      "brief.f5": "يحدّد أي شاشة أو عرض ينبغي فتحه.",
      "brief.f6": "يحوّل «معطوب على الجوال» إلى حالة قابلة لإعادة الإنتاج.",
      "brief.f7": "أخطاء الطرفية وطلبات الشبكة الفاشلة أو البطيئة، ملتقطة لحظة حدوث المشكلة.",
      "brief.f8": "ملف المصدر خلف العنصر، حين يكون الربط مفعّلًا — متدرّجًا بحسب ما يكشفه البناء.",
      "brief.f9": "وسم BCP-47 الذي اكتُشف فيه التعليق — فتتم الترجمة تلقائيًا في الاتجاهين.",
      "brief.optin": "اختياري · تقارير أعطال",
      "brief.mapping": "حين يكون ربط المصدر مفعّلًا",
      "how.kicker": "الحلقة",
      "how.title": "أشِر. صنِّف. طبِّق. ثبِّت.",
      "how.sub": "أربع خطوات من جملة صاحب المصلحة إلى commit مراجَع — والردّ يعود بلغته نفسها.",
      "how.tab1": "أشِر وعلِّق",
      "how.tab2": "التصنيف",
      "how.tab3": "تطبّقه الأداة الذكية",
      "how.tab4": "commit من دون دفع",
      "how.p1":
        "أي شخص تدعوه يستطيع النقر على أي عنصر في التطبيق العامل وترك جملة واحدة — باللغة التي يفكر بها. تكتشف Pointer اللغة وتترجم التعليق تلقائيًا، في كل تثبيت، افتراضيًا.",
      "how.n1": "مسجَّل الدخول · نقرة · جملة واحدة",
      "how.p2":
        "تصل التعليقات إلى قائمة واحدة في لوحة التحكم، موسومة بالمشروع والبيئة وصاحب المصلحة والحالة — لا لقطات شاشة مبعثرة عبر خمس قنوات.",
      "how.n2": "قائمة واحدة · موسومة · مصنَّفة",
      "how.p3":
        "يسلّم المطوّر القائمة إلى أي أداة برمجة ذكية عبر HTTP صِرف. يبدأ الوكيل عند المُحدِّد وقاعدة CSS المنتصرة — لا عند src/ — وحين تحمل الجولة تعديلات ميكانيكية، يكتبها نموذج أرخص بينما يخطّط النموذج المميّز ويراجع.",
      "how.n3": "أي أداة · HTTP صِرف · بلا ارتهان",
      "how.p4":
        "ينشئ الوكيل commit بأسلوب مشروعك ولا يدفع أبدًا. كل تعليق مطبَّق يحمل رابط commit الخاص به ويرتبط بصاحبه. أنت تراجع الفرق؛ وحين يُشحن الإصلاح يُخطَر الكاتب فيؤكد — أو يعيد فتحه.",
      "how.n4": "مراجعتك · ثم 👍 أو 👎",
      "contrast.kicker": "المقارنة",
      "contrast.title": "الطلب نفسه، بإحداثيات ومن دونها.",
      "contrast.without": "من دون Pointer",
      "contrast.with": "مع Pointer",
      "contrast.w1": "«الدفع يبدو معطوبًا على جوالي» — لا عنصر، لا إعادة إنتاج، لا منفذ عرض.",
      "contrast.w2": "يخمّن المطوّر الملف والقاعدة والشاشة.",
      "contrast.w3": "يجوب وكيل الذكاء الاصطناعي الشيفرة استكشافًا أعمى قبل أن يلمس الملف الصحيح.",
      "contrast.w4": "يصل الإصلاح كردٍّ بلقطة شاشة في محادثة — لا شيء يرتبط بـ commit.",
      "contrast.c1":
        "انقر العنصر — البيان يحمل أصلًا المُحدِّد وقاعدة CSS المنتصرة والمسار ومنفذ العرض.",
      "contrast.c2": "يبدأ الوكيل عند المُحدِّد والقاعدة المنتصرة، لا عند src/.",
      "contrast.c3": "تُسنَد التعديلات الميكانيكية والترجمة إلى نموذج أرخص؛ والحكم يبقى للنموذج المميّز.",
      "contrast.c4": "كل تعليق مطبَّق يرتبط برابط commit الخاص به — commit من دون دفع.",
      "why.kicker": "الهامش",
      "why.title": "لماذا يُشير الفريق بدل لقطات الشاشة.",
      "why.t1tag": "الوقت",
      "why.t1": "السياق مرفقٌ بالنقرة",
      "why.b1":
        "لا اجتماع لإعادة بناء ما عناه «معطوب على الجوال» — البيان يقول أصلًا أين وماذا وعند أي منفذ عرض.",
      "why.t2tag": "التكلفة",
      "why.t2": "حلقات توضيح أقل",
      "why.b2": "يصل الطلب بإحداثياته المرفقة، فتبقى المحادثات قصيرة وتتحول الاجتماعات إلى مراجعات.",
      "why.t3tag": "الرموز",
      "why.t3": "رموز أقل على النموذج المميّز",
      "why.b3":
        "يبدأ الوكيل عند المُحدِّد وقاعدة CSS المنتصرة. التعديلات الميكانيكية والترجمة تذهب إلى نموذج أرخص، فينفق النموذج الغالي رموزه على الأجزاء التي تحتاج حكمًا.",
      "why.t4tag": "القائمة",
      "why.t4": "قائمة واحدة منظمة",
      "why.b4": "موسومة ومصنَّفة، لكل مشروع وبيئة — لا لقطات شاشة في خمس قنوات.",
      "cost.kicker": "التطبيق الواعي بالتكلفة",
      "cost.title": "الإسناد، مرسومًا إلى الشكل.",
      "cost.sub":
        "حين تحمل الجولة 3 تعليقات أو أكثر في ملفات متفرقة وكان التعديل ميكانيكيًا — تعديل نص، تبديل لون، تغيير خاصية مباشر — يخطّط النموذج المنسّق ويراجع بينما يكتب النموذج العامل الأرخص التعديل.",
      "cost.aSeg": "استقصاء · مراجعة · تعديلات · ترجمة",
      "cost.aName": "نموذج واحد يفعل كل شيء",
      "cost.aNote": "كل مهمة على النموذج المميّز",
      "cost.bSeg1": "استقصاء + مراجعة",
      "cost.bSeg2": "تعديلات ميكانيكية + ترجمة",
      "cost.bName": "Pointer: إسناد واعٍ بالتكلفة",
      "cost.l1": "التعديلات الميكانيكية — يكتبها النموذج الأرخص",
      "cost.l2": "الترجمة في الاتجاهين — النموذج الأرخص",
      "cost.cap":
        "نوعيٌّ بقاعدة: لا نسبة توفير ولا عدد رموز ولا رقم سرعة يُطبع في أي مكان — لم يُقَس أيٌّ منها بعد. شكلَا العمودين يظهران فقط أن الحصة المُسنَدة أقلية؛ فالاستقصاء والمراجعة يبقيان الجزء الأصعب.",
      "feat.kicker": "العدة",
      "feat.title": "صغيرة التثبيت. واسعة التوافق.",
      "feat.t1tag": "التثبيت",
      "feat.t1": "تثبيت من سطرين",
      "feat.b1": "وسم سكربت وعنصر مخصص. لا حزمة SDK، ولا خطوة بناء، ولا ارتهان لإطار.",
      "feat.t2tag": "التعدد",
      "feat.t2": "متعدد المشاريع والمستأجرين",
      "feat.b2":
        "مساحات عمل بعزلٍ مفروض من جهة الخادم. كل تعليق موسوم بالمشروع والبيئة وصاحب المصلحة.",
      "feat.t3tag": "العزل",
      "feat.t3": "عنصر في Shadow DOM",
      "feat.b3":
        "يعيش الودجت في جذر ظلّ خاص به — أنماطك لا تتسرب إليه وأنماطه لا تتسرب إليك. أي إطار، أي حزمة.",
      "feat.t4tag": "الإضافة",
      "feat.t4": "إضافة المتصفح",
      "feat.b4":
        "التعليقات نفسها على مستوى العنصر في صفحات لا تستضيفها — مواقع العملاء، وبيئة staging خلف تسجيل الدخول، وأدوات الغير.",
      "feat.b4link": "شاهد خطوات التثبيت",
      "feat.x1": "بيئات · محلي / staging / إنتاج",
      "feat.x2": "قواعد الذكاء · مساحة عمل › مشروع › شخصي",
      "feat.x3": "إجراءات منتقاة",
      "feat.x4": "الأداة pointer-feedback",
      "feat.x5": "ربط المصدر · Vite",
      "feat.x6": "وعي بالنشر · مطبَّق مقابل مباشر",
      "feat.x7": "تحقق 👍 / 👎",
      "feat.x8": "كشف الأسرار",
      "feat.x9": "تقنيع قيم الإدخال",
      "feat.x10": "مفاتيح API مشفَّرة وقابلة للتدوير",
      "feat.x11": "قابل للاستضافة الذاتية · API + Postgres",
      "stacks.kicker": "شواهد الميدان",
      "stacks.title": "يعمل مع حزمتك.",
      "stacks.sub":
        "أعداد مجهولة الهوية من مشاريع تُزامن الملاحظات عبر Pointer — الإجماليات حيّة من الخادم، والأسماء محجوبة.",
      "stacks.stacksLabel": "الحزم",
      "stacks.aiLabel": "أدوات الذكاء الاصطناعي",
      "stats.kicker": "العدّ",
      "stats.title": "Pointer بالأرقام.",
      "stats.sub":
        "إجماليات عامة، محجوبة حفاظًا على عدم الكشف — يظهر كل رقم متى تجاوز عتبة الخادم فقط.",
      "stats.langLabel": "كُتبت الملاحظات بـ",
      "stats.aiLabel": "طُبِّقت بواسطة",
      "stats.mApplied": "تعليقات مطبَّقة",
      "stats.mProjects": "مشاريع",
      "stats.mWorkspaces": "مساحات عمل",
      "stats.mMedian": "وسيط زمن التطبيق",
      "stats.min": "أقل من {n} دقيقة",
      "stats.hours": "{n} ساعات",
      "stats.days": "{n} أيام",
      "team.kicker": "الطاقم",
      "team.title": "مبنيٌّ للفريق كله.",
      "team.h1": "من يُشير",
      "team.b1":
        "العملاء ومديرو المنتج والمختبرون — مدعوّون، لا شيء لتثبيته ولا ليتعلَّموه. انقر أي عنصر، اكتب جملة بلغتك، انتهى. وتصلك الردود باللغة نفسها.",
      "team.n1": "بدعوة · بلا تثبيت · أي لغة",
      "team.h2": "من يطبِّق",
      "team.b2":
        "اسحب القائمة بأي أداة برمجة ذكية عبر HTTP صِرف، ودَع الإسناد الواعي بالتكلفة يكتب الأجزاء الميكانيكية، ثم راجع النتيجة. commit من دون دفع — الكلمة الأخيرة لك.",
      "team.n2": "أي أداة · HTTP صِرف · مراجعتك",
      "trust.kicker": "على السجل",
      "trust.title": "أسئلة عادلة، إجابات صريحة.",
      "trust.q1": "«ذكاء اصطناعي يعدّل شيفرتي؟»",
      "trust.a1":
        "ينشئ commit ولا يدفع أبدًا. تراجع كل فرقٍ قبل أن يستقر، وكل تعليق مطبَّق يحمل رابط commit الخاص به — فيرجع التغيير إلى طلبه.",
      "trust.q2": "«ماذا تلتقطون بالضبط؟»",
      "trust.a2":
        "بيانات DOM على مستوى العنصر فقط: المُحدِّد، وقواعد CSS الفاعلة، والمسار، ومنفذ العرض. أبدًا لا قيم النماذج ولا الكوكيز ولا localStorage ولا متن الطلبات.",
      "trust.l1": "الخصوصية",
      "trust.l2": "البيانات والاستضافة الذاتية",
      "trust.l2b": "تفاصيل الاستضافة الذاتية",
      "trust.q3": "«أي أداة ذكاء اصطناعي أحتاج؟»",
      "trust.a3":
        "أيَّ أداة. القائمة HTTP صِرف، فيستطيع أي وكيل تستخدمه سحبها — وإن كانت أداتك تتحدث MCP فهناك خادم لذلك أيضًا. بلا ارتهان.",
      "trust.q4": "«هل تنحصر بياناتي لديكم؟»",
      "trust.a4": "لا. تُستضاف Pointer ذاتيًا: الواجهة البرمجية مع قاعدة Postgres تملكها أنت. بياناتك تبقى لك.",
      "plan.kicker": "الأسعار",
      "plan.title": "الأسعار.",
      "plan.sub": "تُعرض حيًّا من الخادم — ما تراه هو ما تقوله الواجهة البرمجية الآن.",
      "plan.fbTitle": "الخطط تُرسَم الآن.",
      "plan.fbBody":
        "الأسعار غير متاحة من الخادم في هذه اللحظة. يبقى بوسعك إنشاء حساب والبدء بالتأشير.",
      "plan.free": "مجانًا",
      "plan.mo": "/ شهر",
      "plan.yr": "/ سنة",
      "plan.soon": "قريبًا",
      "ext.kicker": "الإضافة",
      "ext.title": "ملاحظات على مواقع لا تستضيفها.",
      "ext.sub":
        "تثبّت إضافة المتصفح التعليقات نفسها على مستوى العنصر في صفحات لا تستطيع تجهيزها — مواقع العملاء، وstaging خلف تسجيل الدخول، وأدوات الغير.",
      "ext.store": "ثبِّتها من متجر كروم",
      "ext.zip": "نزِّل الملف المضغوط",
      "ext.stepsDone": "من الخطوات منجزة",
      "ext.reset": "إعادة",
      "ext.s1": "نزِّل الملف المضغوط للإضافة واحفظه في مكان ثابت",
      "ext.s2": "فكّ الضغط",
      "ext.s3": "افتح chrome://extensions",
      "ext.s4": "فعِّل وضع المطوّر",
      "ext.s5": "انقر «Load unpacked» واختر المجلد المفكوك",
      "ext.s6": "سجِّل الدخول من زر الإضافة في شريط الأدوات",
      "cta.kicker": "ثبِّت تعليقك الأول",
      "cta.title": "أشِر إليه. فيُنجَز.",
      "cta.sub":
        "افتح العرض التجريبي واترك تعليقًا على أي شيء — بلا تثبيت ولا حساب. أو أنشئ مشروعًا وأشِر إلى تطبيقك أنت.",
      "cta.proof": "commit من دون دفع · كل اللغات، مترجَمة تلقائيًا",
      "foot.tagline": "تعليقٌ يتحوَّل إلى تغيير.",
      "foot.docsH": "الوثائق",
      "foot.docsInstall": "التثبيت",
      "foot.docsApply": "تطبيق الملاحظات",
      "foot.docsMcp": "خادم MCP",
      "foot.docsKeys": "مفاتيح API",
      "foot.docsAll": "كل الوثائق",
      "foot.productH": "المنتج",
      "foot.dashboard": "لوحة التحكم",
      "foot.legalH": "قانوني والمزيد",
      "foot.privacy": "الخصوصية",
      "foot.data": "البيانات والاستضافة الذاتية",
      "foot.github": "GitHub",
      "foot.note": "قابل للاستضافة الذاتية · API + Postgres · بياناتك تبقى لك"
    }
  };

  function curLang() {
    return docEl.lang === "ar" ? "ar" : "en";
  }

  /* ---- theme ---- */
  var themeBtn = document.getElementById("theme-toggle");

  function currentTheme() {
    if (docEl.dataset.theme === "dark" || docEl.dataset.theme === "light") {
      return docEl.dataset.theme;
    }
    return window.matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }

  function refreshThemeLabel() {
    if (!themeBtn) return;
    var key = currentTheme() === "dark" ? "theme.light" : "theme.dark";
    themeBtn.setAttribute("aria-label", STRINGS[curLang()][key]);
  }

  function setTheme(t) {
    docEl.dataset.theme = t;
    try { localStorage.setItem("p-theme", t); } catch (e) {}
    refreshThemeLabel();
  }

  if (themeBtn) {
    themeBtn.addEventListener("click", function () {
      setTheme(currentTheme() === "dark" ? "light" : "dark");
    });
  }

  /* ---- language / RTL ---- */
  var langBtn = document.getElementById("lang-toggle");

  function applyLang(lang) {
    var dict = STRINGS[lang];
    docEl.lang = lang;
    docEl.dir = lang === "ar" ? "rtl" : "ltr";
    document.title = dict.title;
    var nodes = document.querySelectorAll("[data-i18n]");
    for (var i = 0; i < nodes.length; i++) {
      var key = nodes[i].getAttribute("data-i18n");
      if (dict[key] != null) nodes[i].textContent = dict[key];
    }
    var ariaNodes = document.querySelectorAll("[data-i18n-aria]");
    for (var j = 0; j < ariaNodes.length; j++) {
      var akey = ariaNodes[j].getAttribute("data-i18n-aria");
      if (dict[akey] != null) ariaNodes[j].setAttribute("aria-label", dict[akey]);
    }
    try { localStorage.setItem("p-lang", lang); } catch (e) {}
    renderDynamic();
    refreshThemeLabel();
  }

  if (langBtn) {
    langBtn.addEventListener("click", function () {
      applyLang(curLang() === "ar" ? "en" : "ar");
    });
  }

  /* ---- mobile nav ---- */
  var navToggle = document.getElementById("nav-toggle");

  function closeNav() {
    docEl.removeAttribute("data-nav-open");
    if (navToggle) navToggle.setAttribute("aria-expanded", "false");
  }

  if (navToggle) {
    navToggle.addEventListener("click", function () {
      var open = docEl.hasAttribute("data-nav-open");
      if (open) {
        closeNav();
      } else {
        docEl.setAttribute("data-nav-open", "");
        navToggle.setAttribute("aria-expanded", "true");
      }
    });
    var navLinks = document.querySelectorAll(".nav-links a");
    for (var k = 0; k < navLinks.length; k++) {
      navLinks[k].addEventListener("click", closeNav);
    }
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape") closeNav();
    });
  }

  /* ---- hero animation: JS adds motion; static end-state is the default ---- */
  var hero = document.querySelector(".hero");
  var reduceMotion = window.matchMedia("(prefers-reduced-motion: reduce)").matches;

  if (hero && !reduceMotion && "IntersectionObserver" in window) {
    hero.setAttribute("data-anim", "");
    var io = new IntersectionObserver(
      function (entries) {
        for (var i = 0; i < entries.length; i++) {
          if (entries[i].isIntersecting) {
            hero.removeAttribute("data-paused");
          } else {
            hero.setAttribute("data-paused", "");
          }
        }
      },
      { threshold: 0.05 }
    );
    io.observe(hero);
  }

  /* ---- live branding (GET /api/branding) — defensive; bundled defaults survive failure ---- */
  var metaApi = document.querySelector('meta[name="pointer-api"]');
  var API_BASE =
    window.__POINTER_API__ ||
    (metaApi && metaApi.content) ||
    "https://api.pointer.moamen.work";

  function applyBranding(d) {
    if (d.productName) {
      var names = document.querySelectorAll("[data-brand-name]");
      for (var i = 0; i < names.length; i++) names[i].textContent = d.productName;
      document.title = STRINGS[curLang()].title.replace("Pointer", d.productName);
    }
    if (d.primaryColor && /^#[0-9a-fA-F]{6}$/.test(d.primaryColor)) {
      var c = d.primaryColor;
      var style = document.createElement("style");
      style.textContent =
        ":root{--accent:" + c + ";--accent-deep:" + c + "}" +
        ':root[data-theme="dark"]{--accent:color-mix(in srgb,' + c + " 70%,white)}" +
        "@media(prefers-color-scheme:dark){:root:not([data-theme]){--accent:color-mix(in srgb," + c + " 70%,white)}}";
      document.head.appendChild(style);
    }
    if (d.urls) {
      if (d.urls.app) {
        var appLinks = document.querySelectorAll("[data-app-link]");
        for (var a = 0; a < appLinks.length; a++) appLinks[a].href = d.urls.app;
      }
      if (d.urls.demo) {
        var demoLinks = document.querySelectorAll("[data-demo-link]");
        for (var b = 0; b < demoLinks.length; b++) demoLinks[b].href = d.urls.demo;
      }
      if (d.urls.docs) {
        var docsLinks = document.querySelectorAll("[data-docs-link]");
        for (var c2 = 0; c2 < docsLinks.length; c2++) docsLinks[c2].href = d.urls.docs;
      }
      if (d.urls.landing) {
        var canon = document.querySelector('link[rel="canonical"]');
        if (canon) canon.href = d.urls.landing;
        var ogUrl = document.querySelector('meta[property="og:url"]');
        if (ogUrl) ogUrl.setAttribute("content", d.urls.landing);
      }
    }
    if (d.tagline) {
      var tagls = document.querySelectorAll("[data-brand-tagline]");
      for (var g = 0; g < tagls.length; g++) tagls[g].textContent = d.tagline;
    }
    if (d.extension) {
      var extStore = document.getElementById("ext-store");
      var extZip = document.getElementById("ext-zip");
      if (extStore && d.extension.storeUrl) {
        extStore.href = d.extension.storeUrl;
        extStore.setAttribute("target", "_blank");
        extStore.hidden = false;
      }
      if (extZip && d.extension.zipUrl) {
        extZip.href = d.extension.zipUrl;
        extZip.setAttribute("download", "");
        extZip.hidden = false;
      }
    }
    if (d.assets && d.assets.favicon) {
      var icon = document.querySelector('link[rel="icon"]');
      if (icon) icon.href = d.assets.favicon;
    }
  }

  try {
    fetch(API_BASE.replace(/\/+$/, "") + "/api/branding")
      .then(function (r) { return r.ok ? r.json() : null; })
      .then(function (res) {
        var d = res && (res.data || res);
        if (d && typeof d === "object") applyBranding(d);
      })
      .catch(function () {});
  } catch (e) {}

  /* ---- i18n helper for dynamic (API-rendered) labels ---- */
  function t(key) {
    var d = STRINGS[curLang()];
    if (d[key] != null) return d[key];
    return STRINGS.en[key] != null ? STRINGS.en[key] : key;
  }
  function fmt(tpl, n) { return String(tpl).replace("{n}", String(n)); }

  /* ---- §4 the loop: tablist (click + arrow keys, RTL-aware) ---- */
  (function () {
    var tablist = document.getElementById("flow-tabs");
    if (!tablist) return;
    var tabs = [];
    var tabNodes = tablist.querySelectorAll('[role="tab"]');
    for (var i = 0; i < tabNodes.length; i++) tabs.push(tabNodes[i]);

    function current() {
      for (var i = 0; i < tabs.length; i++) {
        if (tabs[i].getAttribute("aria-selected") === "true") return i;
      }
      return 0;
    }
    function select(idx, focus) {
      for (var i = 0; i < tabs.length; i++) {
        var on = i === idx;
        tabs[i].setAttribute("aria-selected", on ? "true" : "false");
        tabs[i].tabIndex = on ? 0 : -1;
        var panel = document.getElementById(tabs[i].getAttribute("aria-controls"));
        if (panel) panel.hidden = !on;
      }
      if (focus) tabs[idx].focus();
    }
    for (var j = 0; j < tabs.length; j++) {
      (function (idx) {
        tabs[idx].addEventListener("click", function () { select(idx); });
      })(j);
    }
    tablist.addEventListener("keydown", function (e) {
      var n = tabs.length;
      var rtl = docEl.dir === "rtl";
      if (e.key === "ArrowRight" || e.key === "ArrowLeft") {
        e.preventDefault();
        var forward = e.key === "ArrowRight" ? !rtl : rtl;
        var next = current() + (forward ? 1 : n - 1);
        select(next % n, true);
      } else if (e.key === "Home") {
        e.preventDefault();
        select(0, true);
      } else if (e.key === "End") {
        e.preventDefault();
        select(n - 1, true);
      }
    });
  })();

  /* ---- §7 cost graph: fill on scroll (JS adds motion; full bars are the default) ---- */
  var costPlot = document.getElementById("cost-plot");
  if (costPlot && !reduceMotion && "IntersectionObserver" in window) {
    var costIO = new IntersectionObserver(
      function (entries) {
        for (var i = 0; i < entries.length; i++) {
          if (entries[i].isIntersecting) {
            costPlot.setAttribute("data-anim", "");
            costIO.disconnect();
          }
        }
      },
      { threshold: 0.35 }
    );
    costIO.observe(costPlot);
  }

  /* ---- §14 extension: walkable stepper ---- */
  (function () {
    var steps = [];
    var stepNodes = document.querySelectorAll(".step-btn");
    for (var i = 0; i < stepNodes.length; i++) steps.push(stepNodes[i]);
    if (!steps.length) return;
    var count = document.getElementById("step-count");
    var reset = document.getElementById("step-reset");

    function refresh() {
      var done = 0;
      for (var i = 0; i < steps.length; i++) {
        if (steps[i].getAttribute("aria-pressed") === "true") done++;
      }
      if (count) count.textContent = done + " / " + steps.length;
    }
    for (var j = 0; j < steps.length; j++) {
      steps[j].addEventListener("click", function () {
        this.setAttribute("aria-pressed", this.getAttribute("aria-pressed") === "true" ? "false" : "true");
        refresh();
      });
    }
    if (reset) {
      reset.addEventListener("click", function () {
        for (var k = 0; k < steps.length; k++) steps[k].setAttribute("aria-pressed", "false");
        refresh();
      });
    }
    refresh();
  })();

  /* ---- footer year ---- */
  var yearEl = document.getElementById("f-year");
  if (yearEl) yearEl.textContent = String(new Date().getFullYear());

  /* ---- live data: shared fetch helper ---- */
  var appLinkEl = document.querySelector("[data-app-link]");
  var APP_URL = (appLinkEl && appLinkEl.href) || "https://app.pointer.moamen.work";

  function getJSON(path) {
    var base = API_BASE.replace(/\/+$/, "");
    return fetch(base + path)
      .then(function (r) { return r.ok ? r.json() : null; })
      .catch(function () { return null; });
  }

  /* ---- §13 pricing: GET /api/plans ---- */
  var plansData = null;

  function renderPlans(list) {
    var grid = document.getElementById("plan-grid");
    if (!grid || !Array.isArray(list) || !list.length) return;
    var sorted = list.slice().sort(function (a, b) {
      return (a.sortOrder || 0) - (b.sortOrder || 0);
    });
    grid.innerHTML = "";
    for (var i = 0; i < sorted.length; i++) {
      var p = sorted[i];
      var card = document.createElement("article");
      card.className = "panel plan" + (p.displayState === 1 ? " plan-soon" : "");

      var name = document.createElement("h3");
      name.className = "plan-name";
      name.textContent = p.name || p.slug || "—";

      var price = document.createElement("p");
      price.className = "plan-price";
      if (p.displayState !== 1) {
        var amount = document.createElement("span");
        amount.className = "plan-amount";
        if (!p.priceMonthly) {
          amount.textContent = t("plan.free");
        } else if ((p.currency || "USD").toUpperCase() === "USD") {
          amount.textContent = "$" + p.priceMonthly;
        } else {
          amount.textContent = p.priceMonthly + " " + (p.currency || "").toUpperCase();
        }
        price.appendChild(amount);
        if (p.priceMonthly) {
          var per = document.createElement("span");
          per.className = "plan-per";
          per.textContent = p.interval === 1 ? t("plan.yr") : t("plan.mo");
          price.appendChild(per);
        }
      } else {
        var soon = document.createElement("span");
        soon.className = "chip";
        soon.textContent = t("plan.soon");
        price.appendChild(soon);
      }

      card.appendChild(name);
      card.appendChild(price);

      if (Array.isArray(p.featureBullets) && p.featureBullets.length) {
        var ul = document.createElement("ul");
        ul.className = "plan-feats";
        for (var f = 0; f < p.featureBullets.length; f++) {
          var li = document.createElement("li");
          li.textContent = p.featureBullets[f];
          ul.appendChild(li);
        }
        card.appendChild(ul);
      }

      if (p.displayState !== 1) {
        var cta = document.createElement("a");
        cta.className = "btn btn-primary";
        cta.href = APP_URL;
        cta.textContent = t("hero.cta2");
        card.appendChild(cta);
      }
      grid.appendChild(card);
    }
  }

  getJSON("/api/plans").then(function (res) {
    var list = res && (res.data || res);
    if (Array.isArray(list) && list.length) {
      plansData = list;
      renderPlans(list);
    }
  });

  /* ---- §9 stacks: GET /api/public/stacks-summary (hide unless data) ---- */
  var stacksData = null;
  var STACK_NAMES = {
    react: "React", vue: "Vue", angular: "Angular", svelte: "Svelte",
    "next": "Next.js", "next.js": "Next.js", nuxt: "Nuxt", remix: "Remix",
    astro: "Astro", solid: "SolidJS", qwik: "Qwik", jquery: "jQuery",
    htmx: "htmx", alpine: "Alpine.js", "lit": "Lit", preact: "Preact",
    dotnet: ".NET", node: "Node.js", nodejs: "Node.js", express: "Express",
    nestjs: "NestJS", nest: "NestJS", fastapi: "FastAPI", django: "Django",
    flask: "Flask", laravel: "Laravel", rails: "Rails", spring: "Spring",
    python: "Python", php: "PHP", go: "Go", golang: "Go", rust: "Rust",
    java: "Java", ruby: "Ruby", vite: "Vite", tailwind: "Tailwind CSS",
    bootstrap: "Bootstrap",
    "claude-code": "Claude Code", claude: "Claude", cursor: "Cursor",
    windsurf: "Windsurf", opencode: "OpenCode", "opencode-glm": "opencode + GLM",
    antigravity: "Antigravity", gemini: "Gemini", "gemini-cli": "Gemini CLI",
    copilot: "GitHub Copilot", aider: "Aider", codex: "Codex",
    cline: "Cline", "roo-code": "Roo Code", vscode: "VS Code", jetbrains: "JetBrains"
  };

  function humanize(tok) {
    if (STACK_NAMES[tok]) return STACK_NAMES[tok];
    return String(tok)
      .replace(/[-_.]+/g, " ")
      .replace(/\b\w/g, function (ch) { return ch.toUpperCase(); });
  }
  function topEntries(obj, limit) {
    var arr = [];
    for (var k in obj) {
      if (Object.prototype.hasOwnProperty.call(obj, k)) {
        var n = Number(obj[k]) || 0;
        if (n > 0) arr.push([k, n]);
      }
    }
    arr.sort(function (a, b) { return b[1] - a[1]; });
    return arr.slice(0, limit || 8);
  }
  function fillTags(ul, entries) {
    ul.innerHTML = "";
    for (var i = 0; i < entries.length; i++) {
      var li = document.createElement("li");
      li.className = "tag";
      li.appendChild(document.createTextNode(humanize(entries[i][0])));
      var n = document.createElement("span");
      n.className = "tag-n";
      n.textContent = String(entries[i][1]);
      li.appendChild(n);
      ul.appendChild(li);
    }
  }
  function renderStacks(d) {
    var sec = document.getElementById("stacks");
    if (!sec) return;
    var merged = {};
    var groups = [d.frontend, d.backend];
    for (var g = 0; g < groups.length; g++) {
      var obj = groups[g] || {};
      for (var k in obj) {
        if (Object.prototype.hasOwnProperty.call(obj, k)) {
          merged[k] = (merged[k] || 0) + (Number(obj[k]) || 0);
        }
      }
    }
    fillTags(document.getElementById("stack-tags"), topEntries(merged, 8));
    fillTags(document.getElementById("aitool-tags"), topEntries(d.aiTools || {}, 8));
    sec.hidden = false;
  }

  getJSON("/api/public/stacks-summary").then(function (res) {
    var d = res && (res.data || res);
    if (d && typeof d.totalProjects === "number" && d.totalProjects > 0) {
      stacksData = d;
      renderStacks(d);
    }
  });

  /* ---- §10 stats: GET /api/public/stats (each metric independently nullable) ---- */
  var statsData = null;

  function fmtMedian(h) {
    if (h == null || isNaN(h)) return null;
    if (h < 1) return fmt(t("stats.min"), Math.max(1, Math.round(h * 60)));
    if (h < 48) return fmt(t("stats.hours"), Math.round(h * 10) / 10);
    return fmt(t("stats.days"), Math.round(h / 24));
  }
  function langName(code) {
    try {
      return new Intl.DisplayNames([curLang()], { type: "language" }).of(code);
    } catch (e) {
      return code;
    }
  }
  function statCard(grid, value, label) {
    var wrap = document.createElement("div");
    wrap.className = "stat panel";
    var dt = document.createElement("dt");
    dt.className = "stat-l";
    dt.textContent = label;
    var dd = document.createElement("dd");
    dd.className = "stat-v";
    dd.textContent = value;
    wrap.appendChild(dt);
    wrap.appendChild(dd);
    grid.appendChild(wrap);
  }
  function fillNameTags(ul, codes) {
    ul.innerHTML = "";
    for (var i = 0; i < codes.length; i++) {
      var li = document.createElement("li");
      li.className = "tag";
      li.textContent = langName(codes[i]);
      ul.appendChild(li);
    }
  }
  function renderStats(d) {
    var sec = document.getElementById("numbers");
    if (!sec) return;
    var grid = document.getElementById("stat-grid");
    var langBlock = document.getElementById("lang-block");
    var aiBlock = document.getElementById("stat-aitools-block");
    grid.innerHTML = "";
    langBlock.hidden = true;
    aiBlock.hidden = true;

    var any = false;
    if (typeof d.appliedComments === "number" && d.appliedComments > 0) {
      statCard(grid, d.appliedComments + "+", t("stats.mApplied"));
      any = true;
    }
    if (typeof d.projects === "number" && d.projects > 0) {
      statCard(grid, d.projects + "+", t("stats.mProjects"));
      any = true;
    }
    if (typeof d.workspaces === "number" && d.workspaces > 0) {
      statCard(grid, d.workspaces + "+", t("stats.mWorkspaces"));
      any = true;
    }
    var med = fmtMedian(d.medianHoursToApply);
    if (med) {
      statCard(grid, med, t("stats.mMedian"));
      any = true;
    }
    if (Array.isArray(d.languages) && d.languages.length) {
      fillNameTags(document.getElementById("lang-tags"), d.languages);
      langBlock.hidden = false;
      any = true;
    }
    if (Array.isArray(d.aiTools) && d.aiTools.length) {
      fillNameTags(document.getElementById("stat-aitool-tags"), d.aiTools);
      aiBlock.hidden = false;
      any = true;
    }
    sec.hidden = !any;
  }

  getJSON("/api/public/stats").then(function (res) {
    var d = res && (res.data || res);
    if (d && typeof d === "object") {
      statsData = d;
      renderStats(d);
    }
  });

  /* ---- re-render API-backed sections when the language flips ---- */
  function renderDynamic() {
    if (plansData) renderPlans(plansData);
    if (statsData) renderStats(statsData);
    if (stacksData) renderStacks(stacksData);
  }

  refreshThemeLabel();
})();
