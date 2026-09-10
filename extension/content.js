(() => {
  "use strict";

  if (window.__CHATGPT_ASSISTANT_BRIDGE_LOADED__) return;
  window.__CHATGPT_ASSISTANT_BRIDGE_LOADED__ = true;

  const DEFAULTS = {
    bridgeEnabled: true,
    localConnectionEnabled: true,
    autoContext: true,
    autoTools: true,
    autoApply: true,
    allowExecution: false,
    allowServerExecution: false
  };
  const MAX_AUTOMATION_TURNS = 30;
  const MAX_CONTINUATION_NUDGES = 2;
  const MAX_IDENTICAL_ACTION_REPEATS = 3;
  const TOOL_RESULT_LIMIT = 90000;
  const INITIAL_CONTEXT_LIMIT = 28000;
  const BRIDGE_CALL_TIMEOUT_MS = 45000;
  const ASSISTANT_SCAN_DELAY_MS = 1600;
  const STALL_WATCHDOG_MS = 9000;
  const MANUAL_RECOVERY_COOLDOWN_MS = 1800;
  const state = {
    settings: { ...DEFAULTS },
    connected: false,
    busy: false,
    bypassNextSend: false,
    automationTurns: 0,
    recentActionSignatures: [],
    lastPath: location.pathname,
    settleTimer: null,
    scanRunning: false,
    lastAssistantSignature: "",
    processedSignatures: new Set(),
    lastCheckpointId: null,
    awaitingBridgeContinuation: false,
    continuationNudges: 0,
    stallTimer: null,
    stalled: false,
    stalledReason: "",
    lastManualRecoveryAt: 0,
    workspaceRoots: [],
    primaryRoot: "",
    executionEnabled: false,
    servers: [],
    defaultServer: "",
    allowedLocalCommands: [],
    allowedLocalScripts: [],
    statusDetail: "در حال اتصال به پروژه…"
  };

  function buildProtocolGuide() {
    const rootIds = (state.workspaceRoots || []).map((root) => root.id).filter(Boolean);
    const rootsText = rootIds.length ? rootIds.join(", ") : "web88";
    const primaryRoot = state.primaryRoot || rootIds[0] || "web88";

    return `
[PROJECT BRIDGE AVAILABLE]
Project Bridge is ACTIVE and available through the browser extension.
It is a client-side execution bridge, not a native ChatGPT tool.
Commands written inside <project_bridge_action> are intercepted by the extension,
executed locally, and returned as [PROJECT BRIDGE RESULT].

IMPORTANT:
- Do not claim Project Bridge is unavailable because it is not listed as a native tool.
- Do not ask the user to manually paste files when Bridge can retrieve them.
- Never guess file contents; read or search files through Bridge first.
- Act as an autonomous implementation agent. When the user asks to create or change project files, do not stop after explaining or printing sample code: execute write_files or apply_patch.
- A coding task is complete only after the requested artifacts were created/changed and the result was verified with the returned verification data, read_file, project_tree, git_diff, or an available test.
- If the requested target and content are already clear, act immediately. If either is unclear, inspect the minimum necessary project context and then act without asking the user to perform routine file operations.
Available workspace roots: ${rootsText}
Primary root: ${primaryRoot}
First understand the user's request. Do not guess file contents and do not ask the user to manually paste project files when a Bridge action can retrieve them.
Before every Bridge action or patch, write one very short Persian line starting with "Project Bridge Note:" that says what you just learned and exactly what you are checking or changing next. Keep it concrete and simple, not generic.
Search the whole workspace by using root "all" when you do not yet know which project contains the code.
When project information is needed, output exactly ONE Bridge command and no explanation, using one of these forms:
<project_bridge_action>{"action":"search_code","args":{"query":"search terms","root":"all"}}</project_bridge_action>
<project_bridge_action>{"action":"read_file","args":{"root":"${primaryRoot}","path":"relative/path","startLine":1,"endLine":300}}</project_bridge_action>
<project_bridge_action>{"action":"project_tree","args":{"root":"all","limit":300}}</project_bridge_action>
<project_bridge_action>{"action":"git_status","args":{"root":"${primaryRoot}"}}</project_bridge_action>
<project_bridge_action>{"action":"git_diff","args":{"root":"${primaryRoot}","path":"relative/path"}}</project_bridge_action>
<project_bridge_action>{"action":"run_test","args":{"id":"test-id","root":"${primaryRoot}"}}</project_bridge_action>
<project_bridge_action>{"action":"undo","args":{"checkpointId":"checkpoint-id"}}</project_bridge_action>
A new page, component, or project scaffold can be created in one call. Parent folders are created automatically:
<project_bridge_action>{"action":"write_files","args":{"root":"${primaryRoot}","directories":["src/pages"],"files":[{"path":"src/pages/index.html","content":"<!doctype html>..."}],"overwrite":false}}</project_bridge_action>
A write_files call may contain up to 100 files and 100 directories. Use overwrite:false for new files. Set overwrite:true only when replacing a known existing file with complete content. Prefer apply_patch for editing existing files.
A read_file path is relative to its selected root. Search results are prefixed with [root-id].
After each [PROJECT BRIDGE RESULT], continue the same task and request one more Bridge action if needed.
Treat a successful write_files result with verified:true as proof that the files exist. If verified is false or an action fails, diagnose and retry with a corrected action instead of claiming completion.
When a code change is ready, choose exactly one workspace root and output exactly one valid unified Git patch inside the tag below.
IMPORTANT: Put the diff inside a fenced \`\`\`diff code block so leading spaces on unchanged context lines are preserved.
Every unified-diff line must keep its required prefix: one space for context, "-" for removed lines, and "+" for added lines.
Use real hunk ranges such as @@ -10,3 +10,4 @@; never write placeholders such as @@ ... or bare "...".
Do not put explanations or prose inside the diff fence.
<project_bridge_patch root="${primaryRoot}">
\`\`\`diff
diff --git a/path b/path
--- a/path
+++ b/path
@@ -10,3 +10,4 @@
 context line
-old line
+new line
 context line
\`\`\`
</project_bridge_patch>
The root attribute MUST identify the workspace root that owns the patched files. One patch may modify files from only one root.
The Bridge can create files/folders or apply a valid patch automatically, create a checkpoint, and return the result. After changing files, request an allowed test when useful, then provide a concise final summary.
Never request absolute paths, secrets, .env, credentials, dependency folders, or arbitrary shell commands.
[END PROJECT BRIDGE AVAILABLE]`;
  }

  function buildAgentEfficiencyGuide() {
    const defaultServerText = state.defaultServer
      ? `Default SSH server for the active project: ${state.defaultServer}
When using remote_tree, remote_search, remote_read_file, remote_apply_patch, or exec_server for this project, omit "server" or use "${state.defaultServer}" unless the user explicitly asks for another configured server.`
      : "No default SSH server is linked to the active project; include a configured server id when using SSH actions.";

    const execution = state.executionEnabled
      ? `Execution is enabled.
Allowed local executables: ${(state.allowedLocalCommands || []).join(", ") || "(none)"}
Allowed local PowerShell scripts: ${(state.allowedLocalScripts || []).join(", ") || "(none)"}
Configured server profiles: ${(state.servers || []).join(", ") || "(none)"}
${defaultServerText}
Local project connection: ${state.settings.localConnectionEnabled ? "ON" : "OFF — do not read, search, edit or execute anything locally"}.
SSH server connection: ${state.settings.allowServerExecution ? "ON" : "OFF — do not use any remote tools"}.
For configured SSH project profiles, use remote_tree, remote_search and remote_read_file to inspect the remote project directly.
Use remote_apply_patch only when a code change is ready; it requires SSH execution permission and the extension's edit permission.
Use exec_local only for an allowlisted executable/script and only when local Console permission is enabled.
Use exec_server only for a configured SSH profile and its allowed command prefixes, and only when Server Execution permission is enabled.
For Laravel database counts on a configured server, prefer exec_server with php artisan tinker and no semicolon, for example:
<project_bridge_action>{"action":"exec_server","args":{"command":"php artisan tinker --execute=\"echo App\\\\Models\\\\User::count()\"","timeoutSeconds":90}}</project_bridge_action>
The remote command safety filter rejects semicolons, redirects, command substitution, and secret paths; if a command is rejected, simplify it and retry once instead of stopping.
Never request passwords, private keys, .env contents, or interactive login. Server profiles and SSH keys are managed only from the extension popup.`
      : "Console and SSH permission is disabled. Do not request exec_local or exec_server and do not ask the user for credentials.";

    return `
[PROJECT BRIDGE EFFICIENCY]
For one operation, use one action. For 2-8 independent known operations, prefer one batch action to reduce ChatGPT/Bridge round trips.
Batch children may use prepare_context, search_code, read_file, project_tree, git_status, git_diff, run_test, exec_local, exec_server, remote_tree, remote_search, or remote_read_file.
Do not nest batch. Do not put apply_patch or undo inside batch.
If the relevant file/location is unknown, do one focused search first, then batch the relevant reads/checks.
${execution}
[END PROJECT BRIDGE EFFICIENCY]`;
  }

  function bridgeCall(action, params = {}) {
    return new Promise((resolve) => {
      let settled = false;

      const finish = (value) => {
        if (settled) return;
        settled = true;
        clearTimeout(timeoutId);
        resolve(value);
      };

      const timeoutId = setTimeout(() => {
        finish({
          ok: false,
          error: `Bridge بیش از ${Math.round(BRIDGE_CALL_TIMEOUT_MS / 1000)} ثانیه پاسخ نداد و درخواست متوقف شد.`
        });
      }, BRIDGE_CALL_TIMEOUT_MS);

      try {
        chrome.runtime.sendMessage(
          { type: "project-bridge-call", action, params },
          (response) => {
            const error = chrome.runtime.lastError;
            if (error) {
              finish({ ok: false, error: error.message });
              return;
            }
            finish(response || { ok: false, error: "پاسخی از افزونه دریافت نشد." });
          }
        );
      } catch (error) {
        finish({ ok: false, error: error.message || String(error) });
      }
    });
  }

  function loadSettings() {
    return new Promise((resolve) => {
      chrome.storage.local.get(DEFAULTS, (values) => {
        state.settings = { ...DEFAULTS, ...values };
        resolve();
      });
    });
  }

  function createStatusWidget() {
    if (!state.settings.bridgeEnabled) return;
    if (document.getElementById("apb-status")) return;
    const root = document.createElement("div");
    root.id = "apb-status";
    root.className = "apb-status apb-status--connecting";
    root.setAttribute("dir", "rtl");
    root.innerHTML = `
      <div class="apb-status__scene">
        <div class="apb-status__speech-stack">
          <div class="apb-status__activity" hidden>
            <div class="apb-status__activity-title">
              <span class="apb-status__activity-icon" aria-hidden="true">✦</span>
              <span class="apb-status__activity-title-text">دارم انجامش می‌دم</span>
            </div>
            <div class="apb-status__activity-text"></div>
          </div>
          <div class="apb-status__pill">
            <span class="apb-status__dot" aria-hidden="true"></span>
            <span class="apb-status__copy">
              <span class="apb-status__label">دستیار هوشمندت</span>
              <span class="apb-status__detail">در حال اتصال…</span>
              <span class="apb-status__server" hidden></span>
            </span>
          </div>
        </div>
        <button class="apb-status__mascot" type="button" aria-label="دستیار هوشمند؛ برای وضعیت یا رفع گیر کلیک کن">
          <span class="apb-status__spark apb-status__spark--one">✦</span>
          <span class="apb-status__spark apb-status__spark--two">✧</span>
          <svg class="apb-status__mascot-svg" viewBox="0 0 118 220" role="presentation">
            <ellipse class="apb-status__shadow" cx="59" cy="211" rx="27" ry="5"></ellipse>
            <g class="apb-status__sponge">
              <path class="apb-status__sponge-body" d="M27 47c2-8 9-10 16-7 5-3 11-3 16 0 6-3 12-3 17 0 7-3 14-1 16 7-2 7-1 13 1 18-2 7-2 12 0 18-2 6-2 12 0 18-2 6-2 11 0 17 1 7-3 12-9 14-5 2-10 1-15 0-7 3-15 3-22 0-5 2-11 3-16-1-6-3-8-9-7-15 2-6 2-11 0-17 2-6 2-12 0-18 2-6 2-12 0-18-2-5-3-11-1-16z"></path>
              <circle class="apb-status__sponge-hole" cx="39" cy="56" r="4"></circle>
              <circle class="apb-status__sponge-hole" cx="79" cy="58" r="3"></circle>
              <circle class="apb-status__sponge-hole" cx="35" cy="108" r="3"></circle>
              <circle class="apb-status__sponge-hole" cx="82" cy="113" r="4.5"></circle>
              <ellipse class="apb-status__sponge-hole" cx="49" cy="120" rx="3" ry="5"></ellipse>

              <g class="apb-status__sponge-eyes">
                <ellipse class="apb-status__sponge-eye-white" cx="49" cy="77" rx="12" ry="15"></ellipse>
                <ellipse class="apb-status__sponge-eye-white" cx="70" cy="77" rx="12" ry="15"></ellipse>
                <circle class="apb-status__sponge-iris" cx="51" cy="79" r="5.5"></circle>
                <circle class="apb-status__sponge-iris" cx="68" cy="79" r="5.5"></circle>
                <circle class="apb-status__sponge-pupil" cx="51" cy="79" r="2.5"></circle>
                <circle class="apb-status__sponge-pupil" cx="68" cy="79" r="2.5"></circle>
                <circle class="apb-status__sponge-eye-glint" cx="52" cy="77.5" r="1"></circle>
                <circle class="apb-status__sponge-eye-glint" cx="69" cy="77.5" r="1"></circle>
              </g>

              <path class="apb-status__sponge-brow apb-status__sponge-brow--left" d="M38 60c5-4 10-4 14-1"></path>
              <path class="apb-status__sponge-brow apb-status__sponge-brow--right" d="M66 59c5-3 10-2 14 2"></path>
              <path class="apb-status__sponge-nose" d="M58 82c6 1 8 5 4 9-3 2-6 1-7-1"></path>
              <ellipse class="apb-status__sponge-cheek" cx="39" cy="99" rx="5" ry="2.8"></ellipse>
              <ellipse class="apb-status__sponge-cheek" cx="80" cy="99" rx="5" ry="2.8"></ellipse>

              <path class="apb-status__sponge-mouth apb-status__sponge-mouth--normal" d="M46 102c8 8 18 8 26 0"></path>
              <path class="apb-status__sponge-mouth apb-status__sponge-mouth--happy" d="M43 99c4 18 27 20 33 0-10 7-23 7-33 0z"></path>
              <path class="apb-status__sponge-tongue" d="M52 111c5-4 13-4 18 0-4 7-14 8-18 0z"></path>
              <path class="apb-status__sponge-mouth apb-status__sponge-mouth--worried" d="M47 106c4-3 7 3 11 0 4-3 8 3 13 0"></path>
              <path class="apb-status__sponge-mouth apb-status__sponge-mouth--sad" d="M47 113c8-10 17-10 25 0"></path>
              <rect class="apb-status__sponge-tooth" x="53" y="102" width="6" height="8" rx="1"></rect>
              <rect class="apb-status__sponge-tooth" x="60" y="102" width="6" height="8" rx="1"></rect>
              <path class="apb-status__sponge-tear apb-status__sponge-tear--left" d="M40 90c-3 6-4 10-1 12 4 2 6-2 4-6z"></path>
              <path class="apb-status__sponge-tear apb-status__sponge-tear--right" d="M78 90c3 6 4 10 1 12-4 2-6-2-4-6z"></path>

              <path class="apb-status__sponge-shirt" d="M30 126c19 5 39 5 58 0l-1 12c-19 5-37 5-56 0z"></path>
              <path class="apb-status__sponge-collar" d="M45 129l13 10-9 4-8-12zM74 129l-14 10 9 4 9-12z"></path>
              <path class="apb-status__sponge-tie" d="M59 137l6 7-5 6-6-6z"></path>
              <path class="apb-status__sponge-tie-tail" d="M60 148l6 13-7 6-7-6 6-13z"></path>
              <path class="apb-status__sponge-shorts" d="M31 139c19 5 37 5 56 0l-1 20c-18 5-36 5-54 0z"></path>
              <path class="apb-status__sponge-belt" d="M32 143c18 4 36 4 54 0v5c-18 4-36 4-54 0z"></path>

              <path class="apb-status__sponge-arm apb-status__sponge-arm--left" d="M31 114c-10 7-14 15-16 24"></path>
              <circle class="apb-status__sponge-hand" cx="15" cy="140" r="5"></circle>
              <path class="apb-status__sponge-arm apb-status__sponge-arm--right" d="M87 113c11 5 15 12 17 20"></path>
              <circle class="apb-status__sponge-hand" cx="104" cy="135" r="5"></circle>
              <path class="apb-status__sponge-leg" d="M44 158v24"></path>
              <path class="apb-status__sponge-leg" d="M74 158v24"></path>
              <path class="apb-status__sponge-sock" d="M39 174h11v12H39z"></path>
              <path class="apb-status__sponge-sock" d="M69 174h11v12H69z"></path>
              <path class="apb-status__sponge-shoe" d="M34 184h17c6 0 9 7 3 10H32c-4-3-2-8 2-10z"></path>
              <path class="apb-status__sponge-shoe" d="M68 184h17c5 1 7 6 3 10H67c-6-2-5-8 1-10z"></path>
            </g>
          </svg>
        </button>
      </div>
    `;
    root.title = "دستیار پروژه؛ اگر کار گیر کرد روی کاراکتر کلیک کن";
    document.documentElement.appendChild(root);
    root.querySelector(".apb-status__mascot")?.addEventListener("click", async () => {
      await recoverAssistantFlow(true);
    });
  }

  function removeStatusWidget() {
    document.getElementById("apb-status")?.remove();
  }

  function setStatus(kind, detail) {
    if (!state.settings.bridgeEnabled) {
      removeStatusWidget();
      return;
    }
    createStatusWidget();
    const widget = document.getElementById("apb-status");
    widget.className = `apb-status apb-status--${kind}`;
    const detailNode = widget.querySelector(".apb-status__detail");
    if (detailNode) detailNode.textContent = detail;
    const serverNode = widget.querySelector(".apb-status__server");
    if (serverNode) {
      const serverDetail = connectedServerDetail();
      serverNode.hidden = !serverDetail;
      serverNode.textContent = serverDetail;
    }
    state.statusDetail = detail;
  }

  function connectedServerDetail() {
    if (!state.defaultServer) return "";
    const root = (state.workspaceRoots.find((item) => item.primary) || state.workspaceRoots[0] || {});
    const projectName = root.name || state.primaryRoot || "پروژه";
    return `SSH: ${state.defaultServer} برای ${projectName}`;
  }

  function setActivityTitle(text) {
    const widget = document.getElementById("apb-status");
    const titleNode = widget?.querySelector(".apb-status__activity-title-text");
    if (titleNode) titleNode.textContent = text;
  }

  function clearStallWatchdog() {
    clearTimeout(state.stallTimer);
    state.stallTimer = null;
  }

  function markStalled(reason = "پاسخ ادامه پیدا نکرد.") {
    state.stalled = true;
    state.stalledReason = reason;
    clearStallWatchdog();
    setStatus("stalled", "گیر کردم؛ برای رفع مشکل روی من کلیک کن");
    setActivityTitle("یه گیر کوچیک پیش اومده");
    setActivitySummary(`${reason} روی من کلیک کن تا دوباره ادامه بدم.`, "error");
  }

  function clearStalledState() {
    state.stalled = false;
    state.stalledReason = "";
    const widget = document.getElementById("apb-status");
    widget?.classList.remove("apb-status--stalled");
  }

  function armStallWatchdog() {
    clearStallWatchdog();
    if (!state.awaitingBridgeContinuation) return;
    state.stallTimer = setTimeout(async () => {
      if (!state.awaitingBridgeContinuation || state.busy || responseIsStreaming()) {
        armStallWatchdog();
        return;
      }

      const messages = assistantMessages();
      const last = messages.at(-1);
      const text = (last?.innerText || last?.textContent || "").trim();
      const actionPayload = extractBridgeActionPayload(text);
      const patchMatch = text.match(/<project_bridge_patch\b/i);

      if (actionPayload || patchMatch) {
        scheduleAssistantScan();
        armStallWatchdog();
        return;
      }

      markStalled("بعد از دریافت نتیجه، ادامه‌ی کار از سمت ChatGPT متوقف شد.");
    }, STALL_WATCHDOG_MS);
  }

  async function recoverAssistantFlow(manual = false) {
    const now = Date.now();
    if (manual && now - state.lastManualRecoveryAt < MANUAL_RECOVERY_COOLDOWN_MS) return;
    if (manual) state.lastManualRecoveryAt = now;

    if (state.busy) {
      setActivityTitle("هنوز دارم کار می‌کنم");
      setActivitySummary("الان یک عملیات در حال اجراست؛ به محض تمام شدن نتیجه را ادامه می‌دهم.", "working");
      return;
    }

    if (responseIsStreaming()) {
      setActivityTitle("پاسخ هنوز در راهه");
      setActivitySummary("ChatGPT هنوز در حال پاسخ‌دادن است؛ فعلاً چیزی گیر نکرده.", "working");
      scheduleAssistantScan();
      return;
    }

    clearStallWatchdog();
    clearStalledState();
    state.scanRunning = false;
    state.lastAssistantSignature = "";
    state.processedSignatures.clear();

    if (state.awaitingBridgeContinuation || manual) {
      setStatus("busy", "دارم ارتباط ادامه‌ی کار را بازیابی می‌کنم…");
      setActivityTitle("دارم درستش می‌کنم");
      setActivitySummary("چرخه‌ی ادامه را تازه‌سازی کردم و از ChatGPT می‌خواهم دقیقاً از همان‌جا ادامه دهد.", "working");
      try {
        await sendContinuationNudge(true);
      } catch (error) {
        markStalled(error?.message || "بازیابی خودکار انجام نشد.");
      }
      return;
    }

    setActivityTitle("همه‌چی مرتبه");
    setActivitySummary("من فعالم و حواسم به روند کار هست. اگر جایی گیر کند، همین‌جا بهت می‌گویم.", "result");
    scheduleAssistantScan();
  }

  function setActivitySummary(text, tone = "working") {
    if (!state.settings.bridgeEnabled) return;
    createStatusWidget();
    const widget = document.getElementById("apb-status");
    const activity = widget?.querySelector(".apb-status__activity");
    const textNode = widget?.querySelector(".apb-status__activity-text");
    if (!activity || !textNode) return;

    const clean = String(text || "").replace(/\s+/g, " ").trim();
    if (!clean) {
      activity.hidden = true;
      return;
    }

    textNode.textContent = clean.length > 240
      ? `${clean.slice(0, 237).trim()}…`
      : clean;
    activity.dataset.tone = tone;
    if (tone === "error") setActivityTitle("یه مشکلی پیش اومده");
    else if (tone === "result") setActivityTitle("این مرحله انجام شد");
    else if (!state.stalled) setActivityTitle("دارم انجامش می‌دم");
    activity.hidden = false;
  }

  function extractBridgeNote(text) {
    const match = String(text || "").match(
      /Project Bridge Note:\s*([\s\S]*?)(?=\n\s*<project_bridge_(?:action|patch)\b|$)/i
    );
    return match ? match[1].trim() : "";
  }

  function updateActivityFromLatestAssistant() {
    const messages = assistantMessages();
    const last = messages.at(-1);
    if (!last) return;
    const text = (last.innerText || last.textContent || "").trim();
    const note = extractBridgeNote(text);
    if (note) setActivitySummary(note, "working");
  }

  function bridgeResultSummary(action, result) {
    if (!result?.ok) return result?.error ? `این مرحله انجام نشد: ${result.error} دارم مسیر بعدی را بررسی می‌کنم.` : "این مرحله انجام نشد؛ دارم علتش را پیدا می‌کنم.";
    if (action === "apply_patch") return "تغییرات روی پروژه اعمال شد. الان نتیجه را به ChatGPT می‌دهم تا بررسی کند مرحله‌ی بعدی لازم هست یا نه.";
    if (action === "write_files") return "فایل‌ها ساخته شدند. الان دارم نتیجه را برای ادامه‌ی کار بررسی می‌کنم.";
    if (action === "read_file") return "فایل موردنظر را خواندم. حالا ChatGPT بر اساس محتوای واقعی آن تصمیم می‌گیرد چه تغییری لازم است.";
    if (action === "search_code") return "بخش‌های مرتبط کد را پیدا کردم. الان دارم بهترین محل تغییر را مشخص می‌کنم.";
    if (action === "project_tree") return "ساختار پروژه را گرفتم. الان مسیر فایل‌های مرتبط را بررسی می‌کنم.";
    if (action === "git_diff") return "تغییرات فعلی پروژه را گرفتم. دارم بررسی می‌کنم چیزی جا نمانده باشد.";
    if (action === "run_test") return result.passed ? "تست‌ها با موفقیت رد شدند. الان فقط نتیجه نهایی را جمع‌بندی می‌کنم." : "تست رد نشد. دارم خطا را به ChatGPT می‌دهم تا اصلاحش کند.";
    if (action === "exec_local") return "فرمان محلی اجرا شد. الان خروجی‌اش را بررسی می‌کنم.";
    if (action === "exec_server") return "فرمان روی سرور اجرا شد. الان نتیجه‌اش را برای ادامه‌ی کار بررسی می‌کنم.";
    return "نتیجه این مرحله رسید. الان دارم تصمیم می‌گیرم مرحله‌ی بعدی چیست.";
  }

  async function ping() {
    if (!state.settings.bridgeEnabled) {
      state.connected = false;
      removeStatusWidget();
      return { ok: false, disabled: true };
    }
    const response = await bridgeCall("ping");
    state.connected = Boolean(response?.ok);
    if (state.connected) {
      state.workspaceRoots = Array.isArray(response.workspaceRoots) ? response.workspaceRoots : [];
      state.primaryRoot = (state.workspaceRoots.find((root) => root.primary) || state.workspaceRoots[0] || {}).id || "";
      state.executionEnabled = Boolean(response.executionEnabled && response.executionGranted && state.settings.allowExecution);
      state.servers = Array.isArray(response.servers) ? response.servers : [];
      state.defaultServer = String(response.defaultServer || "");
      state.allowedLocalCommands = Array.isArray(response.allowedLocalCommands) ? response.allowedLocalCommands : [];
      state.allowedLocalScripts = Array.isArray(response.allowedLocalScripts) ? response.allowedLocalScripts : [];
      const rootName = String(response.projectRoot || "").split(/[\\/]/).filter(Boolean).pop() || "پروژه";
      const count = state.workspaceRoots.length;
      setStatus("connected", count > 1 ? `${rootName} + ${count - 1} مسیر متصل` : `${rootName} متصل است`);
    } else {
      setStatus("error", response?.error || "Bridge در دسترس نیست");
    }
    return response;
  }

  function isVisibleElement(element) {
    if (!(element instanceof Element)) return false;
    const style = window.getComputedStyle(element);
    const rect = element.getBoundingClientRect();
    return (
      style.display !== "none" &&
      style.visibility !== "hidden" &&
      style.opacity !== "0" &&
      rect.width > 0 &&
      rect.height > 0
    );
  }

  function getComposer() {
    const preferredSelectors = [
      "#prompt-textarea",
      "[data-testid='prompt-textarea']",
      "div.ProseMirror[contenteditable='true']",
      "form [contenteditable='true'][role='textbox']",
      "form [contenteditable='true']"
    ];

    for (const selector of preferredSelectors) {
      const candidates = [...document.querySelectorAll(selector)];
      const visible = candidates.find((node) => isVisibleElement(node));
      if (visible) return visible;
    }

    const visibleTextareas = [...document.querySelectorAll("form textarea")]
      .filter((node) => isVisibleElement(node));
    if (visibleTextareas.length) return visibleTextareas[0];

    return null;
  }

  function getComposerText(composer = getComposer()) {
    if (!composer) return "";
    if (composer instanceof HTMLTextAreaElement || composer instanceof HTMLInputElement) {
      return composer.value.trim();
    }
    return (composer.innerText || composer.textContent || "").trim();
  }

  function isComposerTarget(target) {
    const composer = getComposer();
    return Boolean(composer && (target === composer || composer.contains(target)));
  }

  function setComposerText(text) {
    const composer = getComposer();
    if (!composer) return false;
    composer.focus();

    if (composer instanceof HTMLTextAreaElement || composer instanceof HTMLInputElement) {
      const prototype = composer instanceof HTMLTextAreaElement
        ? HTMLTextAreaElement.prototype
        : HTMLInputElement.prototype;
      const setter = Object.getOwnPropertyDescriptor(prototype, "value")?.set;
      if (setter) setter.call(composer, text);
      else composer.value = text;

      composer.dispatchEvent(new InputEvent("input", {
        bubbles: true,
        composed: true,
        inputType: "insertText",
        data: text
      }));
      composer.dispatchEvent(new Event("change", { bubbles: true }));
      return true;
    }

    if (composer.getAttribute("contenteditable") === "true") {
      const selection = window.getSelection();
      const range = document.createRange();
      range.selectNodeContents(composer);
      selection.removeAllRanges();
      selection.addRange(range);

      let inserted = false;
      try {
        inserted = document.execCommand("insertText", false, text);
      } catch {
        inserted = false;
      }

      if (!inserted || getComposerText(composer) !== text.trim()) {
        composer.replaceChildren();

        const lines = String(text).split("\n");
        for (let i = 0; i < lines.length; i += 1) {
          const paragraph = document.createElement("p");
          if (lines[i]) {
            paragraph.textContent = lines[i];
          } else {
            paragraph.appendChild(document.createElement("br"));
          }
          composer.appendChild(paragraph);
        }
      }

      composer.dispatchEvent(new InputEvent("input", {
        bubbles: true,
        composed: true,
        inputType: "insertText",
        data: text
      }));
      composer.dispatchEvent(new Event("change", { bubbles: true }));

      return getComposerText(composer).length > 0 || text.length === 0;
    }

    return false;
  }

  function getSendButton() {
    const selectors = [
      "button[data-testid='send-button']",
      "button[aria-label*='Send']",
      "button[aria-label*='send']",
      "button[aria-label*='ارسال']",
      "form button[type='submit']"
    ];
    for (const selector of selectors) {
      const candidates = [...document.querySelectorAll(selector)];
      const candidate = candidates.find((button) => !button.disabled && isVisibleElement(button));
      if (candidate) return candidate;
    }
    return null;
  }

  function isSendButton(target) {
    if (!(target instanceof Element)) return false;
    return Boolean(target.closest(
      "button[data-testid='send-button'], button[aria-label*='Send'], button[aria-label*='send'], button[aria-label*='ارسال'], form button[type='submit']"
    ));
  }

  async function waitForSendButton(timeoutMs = 8000) {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
      const button = getSendButton();
      if (button) return button;
      await delay(120);
    }
    return null;
  }

  async function triggerSend() {
    const button = await waitForSendButton();
    if (button) {
      state.bypassNextSend = true;
      button.click();
      setTimeout(() => { state.bypassNextSend = false; }, 500);
      return true;
    }
    const composer = getComposer();
    const form = composer?.closest("form");
    if (form) {
      state.bypassNextSend = true;
      form.requestSubmit();
      setTimeout(() => { state.bypassNextSend = false; }, 500);
      return true;
    }
    return false;
  }

  function shouldInterceptPrompt(text) {
    if (!text || !state.settings.bridgeEnabled || !state.settings.autoContext) return false;
    if (text.startsWith("[PROJECT BRIDGE RESULT]")) return false;
    if (text.includes("[PROJECT BRIDGE AVAILABLE]")) return false;
    return true;
  }

  function initialContextBlock(result) {
    if (!result?.ok || !result.context) return "";
    const context = String(result.context).slice(0, INITIAL_CONTEXT_LIMIT);
    return `\n[PROJECT BRIDGE INITIAL CONTEXT]\nThis context was selected heuristically from the active workspace for the user's request. Use it for orientation, but inspect exact files before editing when needed.\n${context}\n[END PROJECT BRIDGE INITIAL CONTEXT]\n`;
  }

  function extractBridgeActionPayload(text) {
    const source = String(text || "");
    const patterns = [
      /<project_bridge_action>\s*([\s\S]*?)\s*<\/project_bridge_action>/i,
      /\[PROJECT BRIDGE ACTION\]\s*([\s\S]*?)\s*\[END PROJECT BRIDGE ACTION\]/i,
      /```project_bridge\s*([\s\S]*?)\s*```/i
    ];
    for (const pattern of patterns) {
      const match = source.match(pattern);
      if (match) return match[1];
    }
    return "";
  }

  function looksLikeStalledContinuation(text) {
    const value = String(text || "").trim();
    if (!value) return false;

    const patterns = [
      /مستقیم\s+ادامه\s+می[‌ ]?دهم/i,
      /ادامه\s+می[‌ ]?دهم/i,
      /متوقف\s+نمی[‌ ]?شوم/i,
      /نتیجه.+برگردد.+ادامه/i,
      /continue\s+(?:directly|working|the\s+(?:task|work))/i,
      /won['’]?t\s+stop\s+until/i,
      /i(?:'ll|\s+will)\s+continue/i
    ];

    return patterns.some((pattern) => pattern.test(value));
  }

  async function sendContinuationNudge(force = false) {
    if (state.continuationNudges >= MAX_CONTINUATION_NUDGES) {
      if (!force) {
        markStalled("ChatGPT چند بار گفت ادامه می‌دهد اما فرمان بعدی را اجرا نکرد.");
        return;
      }
      state.continuationNudges = 0;
    }

    if (state.busy) {
      return;
    }

    state.continuationNudges += 1;
    const message = [
      "[PROJECT BRIDGE CONTINUE]",
      "Continue the existing coding task now.",
      "Do not only say that you will continue.",
      force ? "The user manually requested recovery because the automation appeared stuck." : "",
      force ? "Re-evaluate the latest Project Bridge result and continue from the current task state without asking the user to repeat anything." : "",
      "If another Project Bridge operation is needed, output the next valid Project Bridge action or patch immediately.",
      "If no further Bridge operation is needed, provide the actual final result now.",
      "[END PROJECT BRIDGE CONTINUE]"
    ].join("\n");

    await waitForComposerReady();
    if (!setComposerText(message)) throw new Error("کادر پیام برای ادامه خودکار پیدا نشد.");
    await delay(120);
    if (!await triggerSend()) throw new Error("ارسال پیام ادامه خودکار انجام نشد.");
    clearStalledState();
    setStatus("connected", force ? "بازیابی انجام شد؛ ادامه‌ی کار درخواست شد" : "ChatGPT متوقف شده بود؛ ادامه خودکار درخواست شد");
    setActivityTitle("دوباره راه افتاد");
    setActivitySummary(force ? "مشکل را ریست کردم و ادامه‌ی همان کار را دوباره فرستادم." : "دیدم روند متوقف شده بود؛ خودم ادامه‌ی کار را دوباره درخواست کردم.", "result");
  }

  async function enrichAndSend(originalText) {
    if (state.busy) return;
    state.busy = true;
    clearStallWatchdog();
    clearStalledState();
    state.automationTurns = 0;
    state.recentActionSignatures = [];
    state.lastAssistantSignature = "";
    state.awaitingBridgeContinuation = false;
    state.continuationNudges = 0;
    setStatus("busy", "در حال ارسال درخواست به ChatGPT…");
    setActivityTitle("دارم شروع می‌کنم");
    setActivitySummary("درخواستت رو گرفتم؛ اول زمینه‌ی مرتبط پروژه رو پیدا می‌کنم تا روی فایل درست کار کنم.", "working");
    try {
      if (!state.connected) {
        const connection = await ping();
        if (!connection.ok) throw new Error(connection.error || "Bridge متصل نیست.");
      }

      let contextBlock = "";
      if (state.settings.autoContext && state.settings.localConnectionEnabled) {
        setStatus("busy", "در حال پیدا کردن زمینه مرتبط پروژه…");
        setActivitySummary("دارم داخل پروژه می‌گردم تا فایل‌ها و بخش‌هایی که به درخواستت مربوطند پیدا شوند.", "working");
        const contextResult = await bridgeCall("prepare_context", { prompt: originalText });
        contextBlock = initialContextBlock(contextResult);
        if (contextResult?.ok) setActivitySummary("بخش‌های مرتبط رو پیدا کردم؛ حالا درخواست کامل رو با همین اطلاعات برای ChatGPT می‌فرستم.", "working");
      }

      const augmented = `[USER REQUEST]\n${originalText}\n${contextBlock}\n${buildProtocolGuide()}\n${buildAgentEfficiencyGuide()}`;
      if (!setComposerText(augmented)) throw new Error("کادر پیام ChatGPT پیدا نشد.");
      await delay(100);
      if (!await triggerSend()) throw new Error("دکمه ارسال ChatGPT پیدا نشد.");
      setStatus("connected", "در انتظار تصمیم ChatGPT برای بررسی پروژه…");
      setActivitySummary("درخواست ارسال شد. الان منتظرم ChatGPT مرحله‌ی بعدی کار روی پروژه رو مشخص کنه.", "working");
    } catch (error) {
      setComposerText(originalText);
      setStatus("error", error.message || "ارسال پیام ناموفق بود");
    } finally {
      state.busy = false;
    }
  }

  function interceptKeyboard(event) {
    if (state.bypassNextSend || state.busy) return;
    if (event.key !== "Enter" || event.shiftKey || event.ctrlKey || event.altKey || event.metaKey || event.isComposing) return;
    if (!isComposerTarget(event.target)) return;
    const text = getComposerText();
    if (!shouldInterceptPrompt(text)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    enrichAndSend(text);
  }

  function interceptClick(event) {
    if (state.bypassNextSend || state.busy || !isSendButton(event.target)) return;
    const text = getComposerText();
    if (!shouldInterceptPrompt(text)) return;
    event.preventDefault();
    event.stopImmediatePropagation();
    enrichAndSend(text);
  }

  function assistantMessages() {
    const primary = [...document.querySelectorAll("[data-message-author-role='assistant']")];
    if (primary.length) return primary;
    return [...document.querySelectorAll("main article")].filter((node) => {
      const label = node.getAttribute("aria-label") || "";
      return /assistant|chatgpt/i.test(label);
    });
  }

  function responseIsStreaming() {
    const buttons = [...document.querySelectorAll(
      "button[data-testid='stop-button'], button[aria-label*='Stop'], button[aria-label*='stop'], button[aria-label*='توقف']"
    )];
    return buttons.some((button) => isVisibleElement(button));
  }

  function simpleHash(text) {
    let hash = 2166136261;
    for (let i = 0; i < text.length; i += 1) {
      hash ^= text.charCodeAt(i);
      hash = Math.imul(hash, 16777619);
    }
    return (hash >>> 0).toString(16);
  }

  function scheduleAssistantScan() {
    clearTimeout(state.settleTimer);
    state.settleTimer = setTimeout(() => {
      scanAssistantResponse().catch((error) => {
        setStatus("error", error?.message || "خطا هنگام بررسی پاسخ ChatGPT");
      });
    }, ASSISTANT_SCAN_DELAY_MS);
  }

  async function scanAssistantResponse() {
    if (state.scanRunning) return;
    state.scanRunning = true;

    try {
      if (!state.settings.bridgeEnabled || !state.settings.autoTools) return;

      if (state.stalled) return;

      // ممکن است پاسخ ChatGPT دقیقاً زمانی وارد DOM شود که Bridge هنوز
      // نتیجه مرحله قبلی را ارسال می‌کند. در این حالت نباید پاسخ را از دست بدهیم؛
      // بعد از آزاد شدن busy دوباره آن را بررسی می‌کنیم.
      if (state.busy) {
        scheduleAssistantScan();
        return;
      }

      // اگر پاسخ هنوز در حال استریم است، بعداً دوباره بررسی می‌کنیم.
      // finally تضمین می‌کند scanRunning قبل از تلاش بعدی آزاد شود.
      if (responseIsStreaming()) {
        scheduleAssistantScan();
        return;
      }

      const messages = assistantMessages();
      const last = messages.at(-1);
      if (!last) return;

      const text = (last.innerText || last.textContent || "").trim();
      if (!text) return;

      clearStallWatchdog();
      clearStalledState();
      const note = extractBridgeNote(text);
      if (note) setActivitySummary(note, "working");

      const signature = `${location.pathname}:${simpleHash(text)}`;
      if (state.processedSignatures.has(signature)) return;

      // پاسخ باید یک بازه بدون تغییر بماند تا فرمان نصفه اجرا نشود.
      if (state.lastAssistantSignature !== signature) {
        state.lastAssistantSignature = signature;
        scheduleAssistantScan();
        return;
      }

      state.processedSignatures.add(signature);

      const patchMatch = text.match(/<project_bridge_patch(?:\s+root=["']([^"']+)["'])?\s*>\s*([\s\S]*?)\s*<\/project_bridge_patch>/i);
      if (patchMatch && state.settings.autoApply) {
        const root = patchMatch[1] || state.primaryRoot || undefined;
        const patch = stripFence(patchMatch[2]);
        if (looksLikeRealUnifiedPatch(patch)) {
          state.awaitingBridgeContinuation = false;
          await executeBridgeRequest("apply_patch", { root, patch });
          return;
        }
      }

      const actionPayload = extractBridgeActionPayload(text);
      if (actionPayload) {
        let command;
        try {
          command = JSON.parse(stripFence(actionPayload));
        } catch (error) {
          await sendAutomatedResult("parse_action", {
            ok: false,
            error: `فرمان Bridge JSON معتبر نیست: ${error.message}`
          });
          return;
        }

        const allowed = new Set([
          "search_code",
          "read_file",
          "project_tree",
          "git_status",
          "git_diff",
          "write_files",
          "run_test",
          "undo",
          "batch",
          "exec_local",
          "exec_server",
          "remote_tree",
          "remote_search",
          "remote_read_file",
          "remote_apply_patch"
        ]);

        if (!allowed.has(command.action)) {
          await sendAutomatedResult(command.action || "unknown", {
            ok: false,
            error: "این عملیات در چرخه خودکار مجاز نیست."
          });
          return;
        }

        if ((command.action === "write_files" || command.action === "remote_apply_patch") && !state.settings.autoApply) {
          await sendAutomatedResult("write_files", {
            ok: false,
            error: "اجازه ساخت و ویرایش خودکار در تنظیمات افزونه خاموش است."
          });
          return;
        }

        const localExecutionRequested = command.action === "exec_local" || (
          command.action === "batch" && Array.isArray(command.args?.actions) &&
          command.args.actions.some((item) => item?.action === "exec_local")
        );
        const serverExecutionRequested = ["exec_server", "remote_apply_patch", "remote_tree", "remote_search", "remote_read_file"].includes(command.action) || (
          command.action === "batch" && Array.isArray(command.args?.actions) &&
          command.args.actions.some((item) => ["exec_server", "remote_apply_patch", "remote_tree", "remote_search", "remote_read_file"].includes(item?.action))
        );
        if (localExecutionRequested && !state.settings.allowExecution) {
          await sendAutomatedResult(command.action, {
            ok: false,
            permissionRequired: "allowExecution",
            error: "دسترسی Console محلی در پنجره افزونه خاموش است."
          });
          return;
        }
        if (serverExecutionRequested && !state.settings.allowServerExecution) {
          await sendAutomatedResult(command.action, {
            ok: false,
            permissionRequired: "allowServerExecution",
            error: "اتصال به سرور در پنجره افزونه خاموش است."
          });
          return;
        }

        state.awaitingBridgeContinuation = false;
        await executeBridgeRequest(command.action, command.args || {});
        return;
      }

      if (state.awaitingBridgeContinuation && looksLikeStalledContinuation(text)) {
        await sendContinuationNudge();
        armStallWatchdog();
        return;
      }

      state.awaitingBridgeContinuation = false;
      state.continuationNudges = 0;
      clearStallWatchdog();
    } catch (error) {
      setStatus("error", error?.message || "خطا هنگام پردازش پاسخ ChatGPT");
    } finally {
      state.scanRunning = false;
    }
  }

  function looksLikeRealUnifiedPatch(value) {
    const patch = String(value || "").trim();
    if (!patch) return false;

    return /^diff --git a\/.+ b\/.+$/m.test(patch) &&
      /^--- (?:a\/.+|\/dev\/null)$/m.test(patch) &&
      /^\+\+\+ (?:b\/.+|\/dev\/null)$/m.test(patch) &&
      /^@@ .+ @@/m.test(patch);
  }

  function stripFence(value) {
    return String(value || "")
      .replace(/^```(?:diff|json|project_bridge)?\s*/i, "")
      .replace(/\s*```$/i, "")
      .trim();
  }

  async function executeBridgeRequest(action, args) {
    if (state.automationTurns >= MAX_AUTOMATION_TURNS) {
      markStalled(`چرخه خودکار بعد از ${MAX_AUTOMATION_TURNS} مرحله به سقف ایمنی رسید.`);
      return;
    }

    const actionSignature = simpleHash(`${action}:${JSON.stringify(args || {})}`);
    state.recentActionSignatures.push(actionSignature);
    if (state.recentActionSignatures.length > MAX_IDENTICAL_ACTION_REPEATS) {
      state.recentActionSignatures.shift();
    }

    if (
      state.recentActionSignatures.length === MAX_IDENTICAL_ACTION_REPEATS &&
      state.recentActionSignatures.every((signature) => signature === actionSignature)
    ) {
      markStalled("یک فرمان یکسان ۳ بار پشت سر هم تکرار شد و برای جلوگیری از حلقه متوقفش کردم.");
      return;
    }

    state.automationTurns += 1;
    state.busy = true;
    clearStallWatchdog();
    clearStalledState();
    const isWriteAction = action === "apply_patch" || action === "write_files";
    setStatus("busy", isWriteAction ? "در حال ساخت و اعمال تغییر…" : "در حال اجرای درخواست کد…");
    setActivitySummary(isWriteAction ? "ChatGPT تغییرات رو آماده کرده؛ الان دارم واقعاً روی فایل‌های پروژه اعمالشون می‌کنم." : "دارم فرمان درخواست‌شده رو روی پروژه اجرا می‌کنم و خروجی واقعی رو برمی‌گردونم.", "working");
    try {
      const result = await bridgeCall(action, args);
      if (result?.checkpointId) {
        state.lastCheckpointId = result.checkpointId;
        chrome.storage.local.set({ lastCheckpointId: result.checkpointId });
      }
      setActivitySummary(bridgeResultSummary(action, result), result?.ok ? "result" : "error");
      state.awaitingBridgeContinuation = true;
      await sendAutomatedResult(action, result);
      setStatus(result?.ok ? "connected" : "error", result?.ok ? "نتیجه به ChatGPT ارسال شد" : (result?.error || "عملیات ناموفق بود"));
      if (result?.ok) setActivitySummary("خروجی این مرحله به ChatGPT رسید؛ الان منتظرم ببینم کار تمام شده یا مرحله‌ی بعدی لازم است.", "working");
      armStallWatchdog();
    } finally {
      state.busy = false;
    }
  }

  async function sendAutomatedResult(action, result) {
    const serialized = JSON.stringify(result, null, 2);
    const trimmed = serialized.length > TOOL_RESULT_LIMIT
      ? `${serialized.slice(0, TOOL_RESULT_LIMIT)}\n[truncated by extension]`
      : serialized;
    const message = `[PROJECT BRIDGE RESULT]\nAction: ${action}\n${trimmed}\n[END PROJECT BRIDGE RESULT]\nContinue the coding task. Request one more bridge action if needed; otherwise provide the final result.`;
    await waitForComposerReady();
    if (!setComposerText(message)) throw new Error("کادر پیام برای ارسال نتیجه پیدا نشد.");
    await delay(120);
    if (!await triggerSend()) throw new Error("ارسال خودکار نتیجه انجام نشد.");
  }

  async function waitForComposerReady(timeoutMs = 15000) {
    const started = Date.now();
    while (Date.now() - started < timeoutMs) {
      if (getComposer() && !responseIsStreaming()) return true;
      await delay(200);
    }
    throw new Error("ChatGPT برای دریافت نتیجه Bridge آماده نشد.");
  }

  function watchNavigation() {
    if (location.pathname !== state.lastPath) {
      state.lastPath = location.pathname;
      clearStallWatchdog();
      clearStalledState();
      state.automationTurns = 0;
      state.lastAssistantSignature = "";
      state.processedSignatures.clear();
      state.awaitingBridgeContinuation = false;
      state.continuationNudges = 0;
      setTimeout(ping, 500);
    }
  }

  function isAssistantElement(element) {
    if (!(element instanceof Element)) return false;

    if (
      element.matches?.("[data-message-author-role='assistant']") ||
      element.closest?.("[data-message-author-role='assistant']") ||
      element.querySelector?.("[data-message-author-role='assistant']")
    ) {
      return true;
    }

    const articles = [];
    if (element.matches?.("main article")) articles.push(element);

    const closestArticle = element.closest?.("main article");
    if (closestArticle) articles.push(closestArticle);

    for (const article of element.querySelectorAll?.("main article") || []) {
      articles.push(article);
    }

    return articles.some((article) => {
      const label = article.getAttribute("aria-label") || "";
      return /assistant|chatgpt/i.test(label);
    });
  }

  function mutationTouchesAssistant(mutation) {
    const targetElement = mutation.target instanceof Element
      ? mutation.target
      : mutation.target?.parentElement;

    if (targetElement?.closest?.("#apb-status")) return false;
    if (isAssistantElement(targetElement)) return true;

    for (const node of mutation.addedNodes || []) {
      const element = node instanceof Element ? node : node?.parentElement;
      if (element?.closest?.("#apb-status")) continue;
      if (isAssistantElement(element)) return true;
    }

    return false;
  }

  function delay(milliseconds) {
    return new Promise((resolve) => setTimeout(resolve, milliseconds));
  }

  async function start() {
    await loadSettings();
    if (state.settings.bridgeEnabled) createStatusWidget();
    document.addEventListener("keydown", interceptKeyboard, true);
    document.addEventListener("click", interceptClick, true);
    const observer = new MutationObserver((mutations) => {
      watchNavigation();

      if (!state.settings.bridgeEnabled || !state.settings.autoTools) return;
      if (!mutations.some((mutation) => mutationTouchesAssistant(mutation))) return;

      scheduleAssistantScan();
    });
    observer.observe(document.documentElement, {
      childList: true,
      subtree: true,
      characterData: true
    });
    chrome.storage.onChanged.addListener((changes, area) => {
      if (area !== "local") return;
      for (const [key, change] of Object.entries(changes)) {
        if (key in DEFAULTS) state.settings[key] = change.newValue;
      }
      if (!state.settings.bridgeEnabled) {
        state.connected = false;
        state.busy = false;
        clearStallWatchdog();
        removeStatusWidget();
      } else if (changes.bridgeEnabled || changes.activeProjectPath || changes.allowExecution || changes.allowServerExecution || changes.serverProfilesUpdatedAt) {
        ping();
      }
    });
    if (state.settings.bridgeEnabled) await ping();
  }

  start();
})();
