const defaults = {
  bridgeEnabled: true,
  localConnectionEnabled: true,
  autoContext: true,
  autoTools: true,
  autoApply: true,
  allowExecution: false,
  lastCheckpointId: null,
  projects: [],
  activeProjectPath: ""
};

let popupState = { ...defaults };
let runtimeExecutionAvailable = true;
let configuredServerCount = 0;

function callBridge(action, params = {}) {
  return new Promise((resolve) => {
    chrome.runtime.sendMessage({ type: "project-bridge-call", action, params }, (response) => {
      const error = chrome.runtime.lastError;
      resolve(error ? { ok: false, error: error.message } : (response || { ok: false, error: "پاسخی از Bridge دریافت نشد." }));
    });
  });
}

function storageGet() {
  return new Promise((resolve) => chrome.storage.local.get(defaults, resolve));
}

function storageSet(values) {
  return new Promise((resolve) => chrome.storage.local.set(values, resolve));
}

function projectNameFromPath(path) {
  const parts = String(path || "").split(/[\\/]/).filter(Boolean);
  return parts.at(-1) || "پروژه";
}

function normalizedProjects(projects) {
  const seen = new Set();
  return (Array.isArray(projects) ? projects : []).filter((project) => {
    const path = String(project?.path || "").trim();
    const key = path.toLocaleLowerCase();
    if (!path || seen.has(key)) return false;
    seen.add(key);
    project.path = path;
    project.name = String(project.name || projectNameFromPath(path));
    return true;
  });
}

function renderProjects() {
  const removeButton = document.getElementById("removeProject");
  const select = document.getElementById("projectSelect");
  const projects = normalizedProjects(popupState.projects);
  select.replaceChildren();

  if (!projects.length) {
    select.add(new Option("پروژه پیش‌فرض تنظیمات", ""));
  } else {
    for (const project of projects) select.add(new Option(project.name, project.path));
  }

  select.value = popupState.activeProjectPath || "";
  if (select.selectedIndex < 0 && projects.length) {
    select.selectedIndex = 0;
    popupState.activeProjectPath = select.value;
  }
  select.disabled = projects.length < 2;
  if (removeButton) {
    removeButton.disabled = projects.length === 0 || !popupState.activeProjectPath;
  }
}

function renderPowerState() {
  const enabled = Boolean(popupState.bridgeEnabled);
  document.body.classList.toggle("is-disabled", !enabled);
  document.getElementById("powerTitle").textContent = enabled ? "پل پروژه روشن است" : "پل پروژه خاموش است";
  document.getElementById("powerHint").textContent = enabled
    ? "ChatGPT به پروژه فعال دسترسی دارد"
    : "پیام‌ها بدون دسترسی پروژه ارسال می‌شوند";
  document.getElementById("localConnectionHint").textContent = popupState.localConnectionEnabled
    ? "روشن: دسترسی به پروژه روی کامپیوتر" : "خاموش: فایل‌ها و فرمان‌های محلی در دسترس نیستند";
  for (const id of ["localConnectionEnabled", "autoContext", "autoTools", "autoApply", "allowExecution"]) {
    const element = document.getElementById(id);
    if (id === "allowExecution") element.disabled = !enabled || !popupState.localConnectionEnabled || !runtimeExecutionAvailable;
    else element.disabled = !enabled;
  }

  const executionHint = document.getElementById("executionHint");
  if (!runtimeExecutionAvailable) executionHint.textContent = "اجرای فرمان در تنظیمات Native Host غیرفعال است";
  else if (!popupState.allowExecution) executionHint.textContent = "برای اجرای فرمان باید صریحاً روشن شود";
  else executionHint.textContent = "کنسول محلی فعال است";
  renderHistoryAvailability();
}

function setDisabledConnection() {
  document.getElementById("connectionStatus").className = "connection connection--pending";
  document.getElementById("connectionText").textContent = "Bridge خاموش است؛ ChatGPT عادی کار می‌کند";
  renderPowerState();
}

function setConnection(response) {
  runtimeExecutionAvailable = response.executionEnabled !== false;
  configuredServerCount = Array.isArray(response.servers) ? response.servers.length : 0;
  document.getElementById("connectionStatus").className = `connection ${response.ok ? "connection--ok" : "connection--error"}`;
  document.getElementById("connectionText").textContent = response.ok
    ? "متصل و آماده ساخت، بررسی و ویرایش پروژه"
    : (response.error || "Bridge در دسترس نیست");
  document.getElementById("projectRoot").textContent = response.projectRoot || popupState.activeProjectPath || "—";
  renderPowerState();
}

async function rememberProject(path, name, makeActive = false) {
  const projects = normalizedProjects([...popupState.projects, { path, name: name || projectNameFromPath(path) }]);
  popupState.projects = projects;
  if (makeActive || !popupState.activeProjectPath) popupState.activeProjectPath = path;
  await storageSet({ projects, activeProjectPath: popupState.activeProjectPath });
  renderProjects();
}

function showOutput(value) {
  const output = document.getElementById("output");
  output.hidden = false;
  output.textContent = typeof value === "string" ? value : JSON.stringify(value, null, 2);
}

async function refreshConnection() {
  renderPowerState();
  if (!popupState.bridgeEnabled) {
    setDisabledConnection();
    await refreshHistory();
    return;
  }
  const response = await callBridge("ping");
  setConnection(response);
  if (response.ok && response.projectRoot) {
    await rememberProject(response.projectRoot, projectNameFromPath(response.projectRoot));
  }
  await refreshHistory();
}

async function initialize() {
  popupState = { ...defaults, ...await storageGet() };
  popupState.projects = normalizedProjects(popupState.projects);
  renderProjects();

  for (const key of ["bridgeEnabled", "localConnectionEnabled", "autoContext", "autoTools", "autoApply", "allowExecution"]) {
    const input = document.getElementById(key);
    input.checked = Boolean(popupState[key]);
    input.addEventListener("change", async () => {
      popupState[key] = input.checked;
      await storageSet({ [key]: input.checked });
      renderPowerState();
      if (key === "bridgeEnabled" || key === "localConnectionEnabled") await refreshConnection();
    });
  }

  renderPowerState();
  await refreshConnection();
}

document.getElementById("addProject").addEventListener("click", async (event) => {
  const button = event.currentTarget;
  button.disabled = true;
  try {
    const result = await callBridge("choose_project");
    if (!result.ok) {
      showOutput(result.error);
      return;
    }
    if (result.cancelled) return;
    await rememberProject(result.projectRoot, result.projectName, true);
    await refreshConnection();
  } finally {
    button.disabled = false;
  }
});

document.getElementById("removeProject").addEventListener("click", async (event) => {
  const button = event.currentTarget;
  const activePath = String(popupState.activeProjectPath || "").trim();
  const projects = normalizedProjects(popupState.projects);
  const activeProject = projects.find((project) => project.path === activePath);

  if (!activePath || !activeProject) {
    showOutput("پروژه‌ای برای حذف انتخاب نشده است.");
    return;
  }

  const confirmed = confirm(
    `پروژه «${activeProject.name}» از لیست Project Bridge حذف شود؟\n\n` +
    "فایل‌ها و پوشه‌های واقعی پروژه روی سیستم حذف نخواهند شد."
  );

  if (!confirmed) return;

  button.disabled = true;

  try {
    const remainingProjects = projects.filter((project) => project.path !== activePath);
    popupState.projects = remainingProjects;
    popupState.activeProjectPath = remainingProjects[0]?.path || "";
    popupState.lastCheckpointId = null;

    await storageSet({
      projects: remainingProjects,
      activeProjectPath: popupState.activeProjectPath,
      lastCheckpointId: null
    });

    renderProjects();
    renderPowerState();
    await refreshConnection();
    showOutput(`پروژه «${activeProject.name}» از لیست حذف شد.`);
  } finally {
    button.disabled = false;
    renderProjects();
  }
});

document.getElementById("projectSelect").addEventListener("change", async (event) => {
  popupState.activeProjectPath = event.currentTarget.value;
  popupState.lastCheckpointId = null;
  await storageSet({ activeProjectPath: popupState.activeProjectPath, lastCheckpointId: null });
  renderPowerState();
  await refreshConnection();
});

let historyBusy = false;
let historyItems = [];
let historyGeneration = 0;
function renderHistoryAvailability() {
  const disabled = historyBusy || !popupState.bridgeEnabled || !popupState.localConnectionEnabled;
  document.getElementById("refreshHistory").disabled = disabled;
  document.getElementById("clearHistory").disabled = disabled || !historyItems.length;
  document.querySelectorAll(".history-restore").forEach(button => { button.disabled = disabled; });
  for (const id of ["projectSelect", "addProject", "removeProject"]) {
    if (historyBusy) document.getElementById(id).disabled = true;
  }
}
async function refreshHistory() {
  const generation = ++historyGeneration;
  const list = document.getElementById("historyList");
  list.replaceChildren();
  historyItems = [];
  document.getElementById("historyWarning").hidden = true;
  const summary = document.getElementById("historySummary");
  if (!popupState.bridgeEnabled || !popupState.localConnectionEnabled) {
    summary.textContent = "برای مشاهده تاریخچه اتصال محلی را روشن کنید.";
    renderHistoryAvailability();
    return;
  }
  summary.textContent = "در حال دریافت تاریخچه…";
  const response = await callBridge("history_list");
  if (generation !== historyGeneration) return;
  if (!response.ok) { summary.textContent = response.error; renderHistoryAvailability(); return; }
  historyItems = response.items || [];
  summary.textContent = historyItems.length
    ? `${historyItems.length.toLocaleString("fa-IR")} مرحله • ${(response.bytes / 1048576).toLocaleString("fa-IR", { maximumFractionDigits: 2 })} مگابایت`
    : "هنوز تغییری ذخیره نشده است. اولین ویرایش فایل اینجا نمایش داده می‌شود.";
  document.getElementById("historyWarning").hidden = historyItems.length < 10;
  for (const [index, item] of historyItems.entries()) {
    const card = document.createElement("article");
    card.className = "history-item";
    const title = document.createElement("strong");
    title.textContent = `قبل از تغییر ${new Date(item.createdAt).toLocaleString("fa-IR")}`;
    const details = document.createElement("details");
    const label = document.createElement("summary");
    label.textContent = `${item.files.length.toLocaleString("fa-IR")} فایل / پوشه • مشاهده مسیرها`;
    const paths = document.createElement("pre");
    paths.dir = "ltr";
    paths.textContent = item.files.join("\n");
    details.append(label, paths);
    const button = document.createElement("button");
    button.type = "button";
    button.className = "history-restore secondary";
    button.textContent = index === 0 ? "بازگردانی یک مرحله" : `بازگشت ${ (index + 1).toLocaleString("fa-IR") } مرحله`;
    button.addEventListener("click", () => mutateHistory("history_restore", item, index + 1));
    card.append(title, details, button);
    list.append(card);
  }
  renderHistoryAvailability();
}
async function mutateHistory(action, item, steps) {
  if (historyBusy) return;
  const prompt = action === "history_clear"
    ? "تمام تاریخچهٔ پروژه فعال پاک شود؟ فایل‌های پروژه تغییر نمی‌کنند؛ امکان بازگردانی این مراحل از بین می‌رود."
    : `به قبل از این تغییر برگردید؟ ${steps.toLocaleString("fa-IR")} مرحلهٔ اخیر برگردانده و از تاریخچه حذف می‌شود. ویرایش دستی فایل‌های مربوط هم جایگزین می‌شود.`;
  if (!confirm(prompt)) return;
  historyBusy = true;
  renderHistoryAvailability();
  try {
    const result = await callBridge(action, item ? { checkpointId: item.id } : {});
    showOutput(result.ok ? (action === "history_clear" ? "تاریخچه پاک شد؛ فایل‌های پروژه محفوظ هستند." : "پروژه به مرحله انتخاب‌شده بازگردانده شد.") : result.error);
    await refreshHistory();
  } finally {
    historyBusy = false;
    renderProjects();
    renderHistoryAvailability();
  }
}
document.getElementById("refreshHistory").addEventListener("click", refreshHistory);
document.getElementById("clearHistory").addEventListener("click", () => mutateHistory("history_clear"));
initialize();
