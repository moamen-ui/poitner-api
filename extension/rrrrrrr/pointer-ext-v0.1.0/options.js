"use strict";
(() => {
  // src/shared.ts
  var DEFAULT_SERVER = "https://api.pointer.moamen.work";

  // src/options.ts
  var serverEl = document.getElementById("server");
  var okEl = document.getElementById("ok");
  function send(msg) {
    return new Promise((resolve) => chrome.runtime.sendMessage(msg, resolve));
  }
  (async () => {
    const state = await send({ type: "getState" });
    serverEl.value = state.server || DEFAULT_SERVER;
  })();
  document.getElementById("save").onclick = async () => {
    const server = serverEl.value.trim().replace(/\/$/, "");
    if (!/^https?:\/\//.test(server)) {
      okEl.textContent = "Enter a valid http(s) URL";
      okEl.style.color = "#dc2626";
      return;
    }
    await send({ type: "setServer", server });
    okEl.textContent = "Saved \u2713";
    okEl.style.color = "#16a34a";
    setTimeout(() => {
      okEl.textContent = "";
    }, 1500);
  };
})();
