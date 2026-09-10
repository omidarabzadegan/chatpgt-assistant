(function () {
  const $ = (id) => document.getElementById(id);
  let profiles = [];
  let selectedKeyPath = "";
  let loadVersion = 0;

  function callBridge(action, params = {}) {
    return new Promise((resolve) => {
      chrome.runtime.sendMessage({ type: "project-bridge-call", action, params }, (response) => {
        const error = chrome.runtime.lastError;
        resolve(error ? { ok: false, error: error.message } : (response || { ok: false, error: "پاسخی از Bridge دریافت نشد." }));
      });
    });
  }

  function show(value) {
    const output = $("output");
    output.hidden = false;
    output.textContent = typeof value === "string" ? value : JSON.stringify(value, null, 2);
  }

  function fill(profile = null) {
    $("serverForm").hidden = false;
    $("serverId").value = profile?.id || "";
    $("serverHost").value = profile?.host || "";
    $("serverUser").value = profile?.user || "";
    $("serverPort").value = profile?.port || 22;
    $("serverWorkingDirectory").value = profile?.workingDirectory || "";
    $("serverCommands").value = (profile?.allowedCommands || []).join("\n");
    selectedKeyPath = "";
    $("sshKeyName").textContent = profile?.identityFileName || "کلیدی انتخاب نشده";
    $("removeServer").disabled = !profile;
  }

  function render() {
    const select = $("serverSelect");
    select.replaceChildren();
    select.add(new Option(profiles.length ? "انتخاب سرور…" : "هنوز سروری تعریف نشده", ""));
    profiles.forEach((profile) => {
      select.add(new Option(`${profile.id} — ${profile.user}@${profile.host}`, profile.id));
    });
    $("serverForm").hidden = true;
  }

  async function load() {
    const version = ++loadVersion;
    const state = await new Promise((resolve) => chrome.storage.local.get({ bridgeEnabled: true, allowServerExecution: false }, resolve));
    if (version !== loadVersion) return;
    $("allowServerExecution").checked = Boolean(state.allowServerExecution);
    $("serverExecutionHint").textContent = state.allowServerExecution
      ? "روشن: بررسی و اجرای فرمان روی سرور مجاز است"
      : "خاموش: هیچ بررسی یا فرمانی روی سرور اجرا نمی‌شود";
    $("allowServerExecution").disabled = !state.bridgeEnabled;
    $("testServer").disabled = !state.bridgeEnabled || !state.allowServerExecution;
    for (const id of ["newServer", "serverSelect", "bindProjectServer", "saveServer", "removeServer", "chooseSshKey"]) $(id).disabled = !state.bridgeEnabled;
    if (!state.bridgeEnabled) {
      profiles = [];
      render();
      $("output").hidden = true;
      return;
    }
    const result = await callBridge("server_profiles");
    if (version !== loadVersion) return;
    if (!result.ok) return show(result.error);
    profiles = Array.isArray(result.servers) ? result.servers : [];
    render();
    $("serverSelect").value = result.defaultServer || "";
    const selected = profiles.find((profile) => profile.id === result.defaultServer);
    if (selected) fill(selected);
  }

  function payload() {
    return {
      id: $("serverId").value.trim(),
      host: $("serverHost").value.trim(),
      user: $("serverUser").value.trim(),
      port: Number($("serverPort").value || 22),
      workingDirectory: $("serverWorkingDirectory").value.trim(),
      identityFile: selectedKeyPath,
      allowedCommands: $("serverCommands").value.split(/\r?\n/).map((item) => item.trim()).filter(Boolean)
    };
  }

  $("newServer")?.addEventListener("click", () => fill());

  $("serverSelect")?.addEventListener("change", (event) => {
    const profile = profiles.find((item) => item.id === event.currentTarget.value);
    if (profile) fill(profile);
    else $("serverForm").hidden = true;
  });

  $("chooseSshKey")?.addEventListener("click", async () => {
    const result = await callBridge("choose_ssh_key");
    if (!result.ok) return show(result.error);
    if (result.cancelled) return;
    selectedKeyPath = result.path;
    $("sshKeyName").textContent = result.name || "SSH Key انتخاب شد";
  });

  $("saveServer")?.addEventListener("click", async () => {
    const result = await callBridge("save_server_profile", payload());
    show(result.ok ? `سرور «${result.server}» ذخیره شد.` : result.error);
    if (result.ok) await load();
  });

  $("testServer")?.addEventListener("click", async () => {
    const server = $("serverId").value.trim();
    if (!server) return show("ابتدا سرور را ذخیره یا انتخاب کنید.");
    const result = await callBridge("test_server", { server, timeoutSeconds: 15 });
    show(result.ok && result.connected
      ? `اتصال SSH به «${server}» برقرار است.\n${result.output || ""}`
      : result);
  });

  $("removeServer")?.addEventListener("click", async () => {
    const server = $("serverId").value.trim();
    if (!server || !confirm(`پروفایل SSH «${server}» حذف شود؟`)) return;
    const result = await callBridge("remove_server_profile", { server });
    show(result.ok ? `پروفایل «${server}» حذف شد.` : result.error);
    if (result.ok) await load();
  });

  $("allowServerExecution")?.addEventListener("change", (event) => {
    chrome.storage.local.set({ allowServerExecution: event.currentTarget.checked });
  });

  chrome.storage.local.get({ allowServerExecution: false }, (state) => {
    if ($("allowServerExecution")) $("allowServerExecution").checked = Boolean(state.allowServerExecution);
  });

  $("bindProjectServer").addEventListener("click", async () => {
    const server = $("serverSelect").value;
    if (!server) return show("ابتدا سرور را انتخاب کنید.");
    const result = await callBridge("bind_project_server", { server });
    show(result.ok ? `سرور «${server}» برای پروژه فعال انتخاب شد.` : result.error);
    if (result.ok) {
      await chrome.storage.local.set({ serverProfilesUpdatedAt: Date.now() });
      await load();
    }
  });

  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === "local" && (changes.activeProjectPath || changes.bridgeEnabled || changes.allowServerExecution)) load();
  });
  load();
})();
