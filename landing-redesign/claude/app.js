/* Shared infra: theme toggle, API_BASE + fetch helper, branding fetch. Sections added later should
   call PF.fetchJSON(path) and render defensively — see BRIEF.md's per-endpoint fallback rules. */
(function () {
  "use strict";

  var API_BASE = (typeof window !== "undefined" && window.__POINTER_API__)
    || (document.querySelector('meta[name="pointer-api"]') || {}).content
    || "https://api.pointer.moamen.work";

  function fetchJSON(path) {
    return fetch(API_BASE + path, { headers: { accept: "application/json" } })
      .then(function (r) { if (!r.ok) throw new Error("http " + r.status); return r.json(); })
      .then(function (res) { return res && (res.data !== undefined ? res.data : res); });
  }

  window.PF = { API_BASE: API_BASE, fetchJSON: fetchJSON };

  // ---- theme -------------------------------------------------------------
  function applyTheme(theme) {
    if (theme === "dark" || theme === "light") {
      document.documentElement.setAttribute("data-theme", theme);
    } else {
      document.documentElement.removeAttribute("data-theme");
    }
    try { localStorage.setItem("pf_theme", theme || ""); } catch (e) {}
  }
  function currentTheme() {
    try { return localStorage.getItem("pf_theme") || ""; } catch (e) { return ""; }
  }
  function systemPrefersDark() {
    return window.matchMedia && window.matchMedia("(prefers-color-scheme: dark)").matches;
  }

  document.addEventListener("DOMContentLoaded", function () {
    applyTheme(currentTheme());
    var btn = document.getElementById("theme-toggle");
    if (btn) {
      btn.addEventListener("click", function () {
        var effectiveDark = currentTheme() ? currentTheme() === "dark" : systemPrefersDark();
        applyTheme(effectiveDark ? "light" : "dark");
      });
    }
    var burger = document.getElementById("burger");
    if (burger) {
      burger.addEventListener("click", function () {
        var expanded = burger.getAttribute("aria-expanded") === "true";
        burger.setAttribute("aria-expanded", String(!expanded));
        document.querySelector(".nav-links").classList.toggle("nav-links--open", !expanded);
      });
    }

    // ---- branding (white-label) ------------------------------------------
    fetchJSON("/api/branding").then(function (b) {
      if (!b) return;
      if (b.productName) {
        document.querySelectorAll("[data-brand-name]").forEach(function (el) { el.textContent = b.productName; });
        document.title = document.title.replace(/^Pointer/, b.productName);
      }
      if (b.assets && b.assets.logo) {
        document.querySelectorAll("[data-brand-logo]").forEach(function (el) { el.src = b.assets.logo; });
      }
      if (b.assets && b.assets.favicon) {
        var link = document.querySelector('link[rel="icon"]');
        if (link) link.href = b.assets.favicon;
      }
      if (b.primaryColor) {
        document.documentElement.style.setProperty("--brand", b.primaryColor);
      }
      if (b.urls) {
        if (b.urls.app) document.querySelectorAll("[data-app-url]").forEach(function (el) { el.href = b.urls.app; });
        if (b.urls.demo) document.querySelectorAll("[data-demo-url]").forEach(function (el) { el.href = b.urls.demo; });
        if (b.urls.docs) document.querySelectorAll("[data-doclink]").forEach(function (el) { el.href = b.urls.docs; });
      }
    }).catch(function () { /* keep bundled defaults */ });
  });
})();

/* Cost-aware graph: scroll-triggered fill, correct static end-state by default (see styles.css
   .cost-graph rules — .js-armed is the only state that starts collapsed). */
(function () {
  "use strict";
  document.addEventListener("DOMContentLoaded", function () {
    var costGraph = document.getElementById("cost-graph");
    if (!costGraph) return;
    var reduceMotion = window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
    if (reduceMotion || !("IntersectionObserver" in window)) return;
    costGraph.classList.add("js-armed");
    var io = new IntersectionObserver(function (entries) {
      entries.forEach(function (entry) {
        if (entry.isIntersecting) {
          costGraph.classList.add("in-view");
          io.disconnect();
        }
      });
    }, { threshold: 0.35 });
    io.observe(costGraph);
  });
})();

/* Sections 9 ("works with your stack") and 10 ("Pointer in numbers") — live API data with
   skeleton-then-hide/populate behavior. Both fetches use PF.fetchJSON and degrade gracefully
   (hide the section) on empty data, non-2xx, or a slow/no network within ~2s. */
(function () {
  "use strict";

  var KNOWN_TOKENS = {
    react: "React", vue: "Vue", angular: "Angular", svelte: "Svelte",
    next: "Next.js", nuxt: "Nuxt", remix: "Remix", astro: "Astro",
    dotnet: ".NET", node: "Node.js", express: "Express",
    django: "Django", flask: "Flask", rails: "Rails", laravel: "Laravel",
    php: "PHP", go: "Go", java: "Java", spring: "Spring", python: "Python",
    "claude-code": "Claude Code", cursor: "Cursor", windsurf: "Windsurf",
    opencode: "opencode", "opencode-glm": "opencode + GLM", copilot: "Copilot",
    antigravity: "Antigravity", codeium: "Codeium"
  };

  function humanizeToken(tok) {
    if (KNOWN_TOKENS[tok]) return KNOWN_TOKENS[tok];
    return String(tok)
      .split(/[-_\s]+/)
      .filter(Boolean)
      .map(function (w) { return w.charAt(0).toUpperCase() + w.slice(1); })
      .join(" ");
  }

  function renderTagRow(container, counts) {
    var entries = Object.keys(counts || {}).map(function (k) { return [k, counts[k]]; });
    entries.sort(function (a, b) { return b[1] - a[1]; });
    container.innerHTML = "";
    entries.forEach(function (entry) {
      var chip = document.createElement("span");
      chip.className = "tag-chip";
      var name = document.createElement("span");
      name.textContent = humanizeToken(entry[0]);
      var count = document.createElement("span");
      count.className = "tag-chip-count font-mono";
      count.textContent = String(entry[1]);
      chip.appendChild(name);
      chip.appendChild(count);
      container.appendChild(chip);
    });
    return entries.length;
  }

  function initStacks() {
    var section = document.getElementById("stacks");
    if (!section || !window.PF) return;
    var stackRow = document.getElementById("stacks-stack-row");
    var aiRow = document.getElementById("stacks-ai-row");
    var settled = false;

    function settle(fn) {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      fn();
    }

    var timeout = setTimeout(function () { settle(function () { section.hidden = true; }); }, 2000);

    PF.fetchJSON("/api/public/stacks-summary").then(function (data) {
      settle(function () {
        if (!data || !data.totalProjects) { section.hidden = true; return; }
        var merged = {};
        Object.keys(data.frontend || {}).forEach(function (k) { merged[k] = (merged[k] || 0) + data.frontend[k]; });
        Object.keys(data.backend || {}).forEach(function (k) { merged[k] = (merged[k] || 0) + data.backend[k]; });
        var stackCount = renderTagRow(stackRow, merged);
        var aiCount = renderTagRow(aiRow, data.aiTools);
        if (stackCount === 0 && aiCount === 0) section.hidden = true;
      });
    }).catch(function () {
      settle(function () { section.hidden = true; });
    });
  }

  function formatMedianValue(hours) {
    // Returns a small array of DOM nodes: localized unit words carry data-i18n so language
    // toggles retranslate them; the number itself is live data and stays untranslated.
    var frag = document.createDocumentFragment();
    function addI18nSpan(key, fallback) {
      var span = document.createElement("span");
      span.setAttribute("data-i18n", key);
      var dict = (window.PF_I18N && window.PF_I18N[(window.PF_currentLang && window.PF_currentLang()) || "en"]) || {};
      span.textContent = dict[key] || fallback;
      frag.appendChild(span);
      return span;
    }
    function addText(t) {
      frag.appendChild(document.createTextNode(t));
    }
    if (hours < 1) {
      addI18nSpan("stats.unit.under", "under");
      addText(" ");
      addText(String(Math.max(1, Math.round(hours * 60))));
      addText(" ");
      addI18nSpan("stats.unit.min", "min");
    } else if (hours < 48) {
      addText(String(Math.round(hours * 10) / 10));
      addText(" ");
      addI18nSpan("stats.unit.hours", "hours");
    } else {
      addText(String(Math.round(hours / 24)));
      addText(" ");
      addI18nSpan("stats.unit.days", "days");
    }
    return frag;
  }

  function initStats() {
    var section = document.getElementById("stats");
    if (!section || !window.PF) return;
    var grid = document.getElementById("stats-grid");
    var langGroup = document.getElementById("stats-languages-group");
    var langRow = document.getElementById("stats-languages-row");
    var settled = false;

    function settle(fn) {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      fn();
    }

    var timeout = setTimeout(function () { settle(function () { section.hidden = true; }); }, 2000);

    function addStatCard(valueNode, labelKey, labelFallback) {
      var card = document.createElement("div");
      card.className = "stat-card card";
      var value = document.createElement("span");
      value.className = "stat-value font-mono";
      value.appendChild(valueNode);
      var label = document.createElement("div");
      label.className = "stat-label";
      label.setAttribute("data-i18n", labelKey);
      var dict = (window.PF_I18N && window.PF_I18N[(window.PF_currentLang && window.PF_currentLang()) || "en"]) || {};
      label.textContent = dict[labelKey] || labelFallback;
      card.appendChild(value);
      card.appendChild(label);
      grid.appendChild(card);
    }

    PF.fetchJSON("/api/public/stats").then(function (data) {
      settle(function () {
        if (!data) { section.hidden = true; return; }
        grid.innerHTML = "";
        var cardCount = 0;
        if (data.appliedComments != null) {
          var v1 = document.createElement("span"); v1.textContent = String(data.appliedComments);
          addStatCard(v1, "stats.metric.appliedComments", "Applied comments"); cardCount++;
        }
        if (data.projects != null) {
          var v2 = document.createElement("span"); v2.textContent = String(data.projects);
          addStatCard(v2, "stats.metric.projects", "Projects"); cardCount++;
        }
        if (data.workspaces != null) {
          var v3 = document.createElement("span"); v3.textContent = String(data.workspaces);
          addStatCard(v3, "stats.metric.workspaces", "Workspaces"); cardCount++;
        }
        if (data.medianHoursToApply != null) {
          addStatCard(formatMedianValue(data.medianHoursToApply), "stats.metric.medianHoursToApply", "Median time to apply");
          cardCount++;
        }

        var languages = data.languages || [];
        if (languages.length) {
          var lang = (window.PF_currentLang && window.PF_currentLang()) || "en";
          var displayNames = null;
          try { displayNames = new Intl.DisplayNames([lang], { type: "language" }); } catch (e) { displayNames = null; }
          langRow.innerHTML = "";
          languages.forEach(function (code) {
            var chip = document.createElement("span");
            chip.className = "tag-chip";
            var name = code;
            if (displayNames) {
              try { name = displayNames.of(code) || code; } catch (e2) {}
            }
            chip.textContent = name;
            langRow.appendChild(chip);
          });
          langGroup.hidden = false;
        } else {
          langGroup.hidden = true;
        }

        if (cardCount === 0 && languages.length === 0) section.hidden = true;
      });
    }).catch(function () {
      settle(function () { section.hidden = true; });
    });
  }

  document.addEventListener("DOMContentLoaded", function () {
    initStacks();
    initStats();
  });
})();

/* Section 13 (pricing) — GET /api/plans. Unlike stacks/stats this section must NEVER fully hide:
   on empty data, a non-2xx response, or a ~2s timeout it renders one graceful fallback card
   instead of a blank grid (see BRIEF.md "Live API contracts" and the definition-of-done rule that
   every live section must render completely with zero network). */
(function () {
  "use strict";

  function dict() {
    return (window.PF_I18N && window.PF_I18N[(window.PF_currentLang && window.PF_currentLang()) || "en"]) || {};
  }

  function i18nText(key, fallback) {
    var d = dict();
    return d[key] || fallback;
  }

  function currencySymbol(code) {
    var map = { USD: "$", EUR: "€", GBP: "£" };
    return map[code] || (code ? code + " " : "$");
  }

  function priceNode(plan) {
    var frag = document.createDocumentFragment();
    if (!plan.priceMonthly) {
      var free = document.createElement("span");
      free.setAttribute("data-i18n", "pricing.free");
      free.textContent = i18nText("pricing.free", "Free");
      frag.appendChild(free);
      return frag;
    }
    frag.appendChild(document.createTextNode(currencySymbol(plan.currency) + plan.priceMonthly));
    var unit = document.createElement("span");
    unit.className = "pricing-unit";
    var unitKey = plan.interval === 1 ? "pricing.perYear" : "pricing.perMonth";
    unit.setAttribute("data-i18n", unitKey);
    unit.textContent = i18nText(unitKey, plan.interval === 1 ? "/yr" : "/mo");
    frag.appendChild(unit);
    return frag;
  }

  function buildPlanCard(plan) {
    var isSoon = plan.displayState === 1;
    var card = document.createElement("div");
    card.className = "pricing-card card" + (isSoon ? " pricing-card--soon" : "");

    var head = document.createElement("div");
    head.className = "pricing-card-head";
    var h3 = document.createElement("h3");
    h3.textContent = plan.name || "";
    var price = document.createElement("div");
    price.className = "pricing-price font-mono";
    price.appendChild(priceNode(plan));
    head.appendChild(h3);
    head.appendChild(price);
    card.appendChild(head);

    var ul = document.createElement("ul");
    ul.className = "pricing-features";
    (plan.featureBullets || []).forEach(function (bullet) {
      var li = document.createElement("li");
      li.innerHTML = '<svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2"><path d="M20 6 9 17l-5-5"/></svg>';
      var span = document.createElement("span");
      span.textContent = bullet;
      li.appendChild(span);
      ul.appendChild(li);
    });
    card.appendChild(ul);

    if (isSoon) {
      var pill = document.createElement("span");
      pill.className = "pill pill-archived";
      pill.setAttribute("data-i18n", "pricing.comingSoon");
      pill.textContent = i18nText("pricing.comingSoon", "Coming soon");
      card.appendChild(pill);
    } else {
      var cta = document.createElement("a");
      cta.className = "btn btn-primary";
      cta.setAttribute("data-app-url", "");
      cta.href = "https://app.pointer.moamen.work";
      cta.setAttribute("data-i18n", "pricing.cta");
      cta.textContent = i18nText("pricing.cta", "Create an account");
      card.appendChild(cta);
    }
    return card;
  }

  function buildFallbackCard() {
    var card = document.createElement("div");
    card.className = "pricing-card card pricing-card--fallback";
    var h3 = document.createElement("h3");
    h3.setAttribute("data-i18n", "pricing.fallback.title");
    h3.textContent = i18nText("pricing.fallback.title", "Get started free");
    var p = document.createElement("p");
    p.setAttribute("data-i18n", "pricing.fallback.body");
    p.textContent = i18nText("pricing.fallback.body", "Create an account to see current plans and pricing.");
    var cta = document.createElement("a");
    cta.className = "btn btn-primary";
    cta.setAttribute("data-app-url", "");
    cta.href = "https://app.pointer.moamen.work";
    cta.setAttribute("data-i18n", "pricing.fallback.cta");
    cta.textContent = i18nText("pricing.fallback.cta", "Create an account");
    card.appendChild(h3);
    card.appendChild(p);
    card.appendChild(cta);
    return card;
  }

  function initPricing() {
    var grid = document.getElementById("pricing-grid");
    if (!grid || !window.PF) return;
    var settled = false;

    function settle(fn) {
      if (settled) return;
      settled = true;
      clearTimeout(timeout);
      fn();
    }

    function renderFallback() {
      grid.innerHTML = "";
      grid.appendChild(buildFallbackCard());
    }

    var timeout = setTimeout(function () { settle(renderFallback); }, 2000);

    // PF.fetchJSON already unwraps a {data: [...]} envelope; an unwrapped array passes through
    // unchanged (see app.js's shared fetchJSON above), so `list` covers both response shapes.
    PF.fetchJSON("/api/plans").then(function (list) {
      settle(function () {
        if (!Array.isArray(list) || list.length === 0) { renderFallback(); return; }
        var sorted = list.slice().sort(function (a, b) { return (a.sortOrder || 0) - (b.sortOrder || 0); });
        grid.innerHTML = "";
        sorted.forEach(function (plan) { grid.appendChild(buildPlanCard(plan)); });
      });
    }).catch(function () {
      settle(renderFallback);
    });
  }

  document.addEventListener("DOMContentLoaded", initPricing);
})();

/* Section 14 (browser extension) — a keyboard-accessible tab/accordion stepper (ARIA tablist
   pattern, roving tabindex, arrow-key navigation) plus a small scoped /api/branding fetch that
   prefers extension.storeUrl / extension.zipUrl when present. Falls back to the bundled zip
   (assets/pointer-extension.zip) with zero network. */
(function () {
  "use strict";

  function initExtensionStepper() {
    var tablist = document.getElementById("ext-tablist");
    if (!tablist) return;
    var tabs = Array.prototype.slice.call(tablist.querySelectorAll(".ext-step"));
    var panels = tabs.map(function (t) { return document.getElementById(t.getAttribute("aria-controls")); });
    var prevBtn = document.getElementById("ext-prev");
    var nextBtn = document.getElementById("ext-next");
    var current = 0;

    function show(index) {
      current = Math.max(0, Math.min(tabs.length - 1, index));
      tabs.forEach(function (tab, i) {
        var selected = i === current;
        tab.setAttribute("aria-selected", String(selected));
        tab.tabIndex = selected ? 0 : -1;
        panels[i].hidden = !selected;
      });
      if (prevBtn) prevBtn.disabled = current === 0;
      if (nextBtn) nextBtn.disabled = current === tabs.length - 1;
    }

    tabs.forEach(function (tab, i) {
      tab.addEventListener("click", function () { show(i); });
      tab.addEventListener("keydown", function (e) {
        if (e.key === "ArrowRight" || e.key === "ArrowDown") {
          e.preventDefault(); show((i + 1) % tabs.length); tabs[current].focus();
        } else if (e.key === "ArrowLeft" || e.key === "ArrowUp") {
          e.preventDefault(); show((i - 1 + tabs.length) % tabs.length); tabs[current].focus();
        }
      });
    });
    if (prevBtn) prevBtn.addEventListener("click", function () { show(current - 1); tabs[current].focus(); });
    if (nextBtn) nextBtn.addEventListener("click", function () { show(current + 1); tabs[current].focus(); });

    show(0);
  }

  function initExtensionBranding() {
    if (!window.PF) return;
    PF.fetchJSON("/api/branding").then(function (b) {
      if (!b || !b.extension) return;
      var storeBtn = document.getElementById("ext-store-btn");
      var downloadBtn = document.getElementById("ext-download-btn");
      if (b.extension.storeUrl && storeBtn) {
        storeBtn.href = b.extension.storeUrl;
        storeBtn.hidden = false;
      }
      if (b.extension.zipUrl && downloadBtn) {
        downloadBtn.href = b.extension.zipUrl;
      }
    }).catch(function () { /* keep the bundled zip fallback (assets/pointer-extension.zip) */ });
  }

  document.addEventListener("DOMContentLoaded", function () {
    initExtensionStepper();
    initExtensionBranding();
  });
})();
