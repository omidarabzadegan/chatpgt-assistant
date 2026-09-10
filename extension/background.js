const NATIVE_HOST = "com.chatgpt_assistant.project_bridge";
const ALLOWED_ACTIONS = new Set([
  "ping",
  "choose_project",
  "choose_ssh_key",
  "server_profiles",
  "bind_project_server",
  "save_server_profile",
  "remove_server_profile",
  "test_server",
  "prepare_context",
  "search_code",
  "read_file",
  "project_tree",
  "git_status",
  "git_diff",
  "write_files",
  "apply_patch",
  "undo",
  "history_list",
  "history_restore",
  "history_clear",
  "run_test",
  "batch",
  "exec_local",
  "exec_server",
  "remote_tree",
  "remote_search",
  "remote_read_file",
  "remote_apply_patch"
]);

function isTrustedSender(sender) {
  if (!sender || !sender.url) return true;
  if (sender.url.startsWith("https://chatgpt.com")) return true;
  try {
    const url = new URL(sender.url);
    return url.origin === "https://chatgpt.com" || url.protocol === "chrome-extension:";
  } catch {
    return false;
  }
}

function isExtensionSender(sender) {
  return Boolean(sender?.url?.startsWith(`chrome-extension://${chrome.runtime.id}/`));
}

function callNative(action, params = {}) {
  return new Promise((resolve, reject) => {
    chrome.storage.local.get({ activeProjectPath: "" }, ({ activeProjectPath }) => {
      const nativeParams = { ...params };
      if (action !== "choose_project" && activeProjectPath) nativeParams.__projectRoot = activeProjectPath;
      else delete nativeParams.__projectRoot;

      chrome.runtime.sendNativeMessage(
        NATIVE_HOST,
        { action, params: nativeParams, requestId: crypto.randomUUID() },
        (response) => {
          const runtimeError = chrome.runtime.lastError;
          if (runtimeError) {
            const message = runtimeError.message || "";
            return reject(new Error(message.includes("Specified native messaging host not found")
              ? "میزبان محلی افزونه ثبت نشده یا مسیر آن جابه‌جا شده است. scripts/install.ps1 را اجرا و افزونه را Reload کنید."
              : message));
          }
          if (!response) return reject(new Error("Native Bridge پاسخی برنگرداند."));
          resolve(response);
        }
      );
    });
  });
}

function updateActionAppearance(enabled) {
  chrome.action.setBadgeText({ text: enabled ? "" : "OFF" });
  chrome.action.setBadgeBackgroundColor({ color: enabled ? "#10b981" : "#64748b" });
  chrome.action.setTitle({ title: enabled ? "دستیار چت جی پی تی - روشن" : "دستیار چت جی پی تی - خاموش" });
}

function requestsExecution(action, params = {}) {
  if (action === "exec_local") return true;
  if (action !== "batch" || !Array.isArray(params.actions)) return false;
  return params.actions.some((item) => item?.action === "exec_local");
}

function requestsServerExecution(action, params = {}) {
  if (["exec_server", "remote_tree", "remote_search", "remote_read_file", "remote_apply_patch", "test_server"].includes(action)) return true;
  return action === "batch" && Array.isArray(params.actions) &&
    params.actions.some((item) => requestsServerExecution(item?.action, item?.params || item?.args || {}));
}

function requestsLocalConnection(action, params = {}) {
  if (["prepare_context", "search_code", "read_file", "project_tree", "git_status", "git_diff", "write_files", "apply_patch", "undo", "history_list", "history_restore", "history_clear", "run_test", "exec_local"].includes(action)) return true;
  return action === "batch" && Array.isArray(params.actions) &&
    params.actions.some((item) => requestsLocalConnection(item?.action, item?.params || item?.args || {}));
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message || message.type !== "project-bridge-call") return false;
  if (!isTrustedSender(sender)) {
    sendResponse({ ok: false, error: "فرستنده پیام مجاز نیست." });
    return false;
  }
  if (!ALLOWED_ACTIONS.has(message.action)) {
    sendResponse({ ok: false, error: "عملیات Bridge مجاز نیست." });
    return false;
  }
  const extensionOnlyActions = new Set(["history_list", "history_restore", "history_clear", "choose_project", "choose_ssh_key", "server_profiles", "bind_project_server", "save_server_profile", "remove_server_profile", "test_server"]);
  if (extensionOnlyActions.has(message.action) && !isExtensionSender(sender)) {
    sendResponse({ ok: false, error: "این عملیات مدیریتی فقط از پنجره خود افزونه مجاز است." });
    return false;
  }

  chrome.storage.local.get({ bridgeEnabled: true, localConnectionEnabled: true, allowExecution: false, allowServerExecution: false }, ({ bridgeEnabled, localConnectionEnabled, allowExecution, allowServerExecution }) => {
    if (!bridgeEnabled && message.action !== "choose_project") {
      sendResponse({ ok: false, disabled: true, error: "Project Bridge خاموش است." });
      return;
    }
    if (!localConnectionEnabled && requestsLocalConnection(message.action, message.params)) {
      sendResponse({ ok: false, permissionRequired: "localConnectionEnabled", error: "اتصال محلی در پنجره افزونه خاموش است." });
      return;
    }
    if (requestsExecution(message.action, message.params) && !allowExecution) {
      sendResponse({ ok: false, permissionRequired: "allowExecution", error: "دسترسی Console محلی در پنجره افزونه خاموش است." });
      return;
    }
    if (requestsServerExecution(message.action, message.params) && !allowServerExecution) {
      sendResponse({ ok: false, permissionRequired: "allowServerExecution", error: "اتصال به سرور در پنجره افزونه خاموش است." });
      return;
    }
    const nativeParams = { ...(message.params || {}), __executionGranted: Boolean(allowExecution), __serverExecutionGranted: Boolean(allowServerExecution) };
    callNative(message.action, nativeParams)
      .then(sendResponse)
      .catch((error) => sendResponse({ ok: false, error: error.message || String(error) }));
  });
  return true;
});

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && changes.bridgeEnabled) updateActionAppearance(Boolean(changes.bridgeEnabled.newValue));
});

chrome.runtime.onStartup.addListener(() => {
  chrome.storage.local.get({ bridgeEnabled: true }, ({ bridgeEnabled }) => updateActionAppearance(Boolean(bridgeEnabled)));
});

chrome.runtime.onInstalled.addListener(({ reason }) => {
  if (reason === "install") {
    chrome.storage.local.set({
      autoContext: true,
      autoTools: true,
      autoApply: true,
      allowExecution: false,
      localConnectionEnabled: true,
      allowServerExecution: false,
      bridgeEnabled: true,
      projects: [],
      activeProjectPath: ""
    });
  }
  chrome.storage.local.get({ bridgeEnabled: true }, ({ bridgeEnabled }) => updateActionAppearance(Boolean(bridgeEnabled)));
});

chrome.storage.local.get({ bridgeEnabled: true }, ({ bridgeEnabled }) => updateActionAppearance(Boolean(bridgeEnabled)));
