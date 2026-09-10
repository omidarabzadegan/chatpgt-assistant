const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');

function bridge(settings = {}, nativeError = null) {
  let listener;
  let calls = 0;
  const runtime = {
    id: 'test',
    onMessage: { addListener(fn) { listener = fn; } },
    onStartup: { addListener() {} },
    onInstalled: { addListener() {} },
    sendNativeMessage(host, request, callback) {
      calls++;
      runtime.lastError = nativeError && { message: nativeError };
      callback({ ok: true });
      runtime.lastError = null;
    }
  };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '..', 'extension', 'background.js'), 'utf8'), {
    URL, crypto: { randomUUID: () => 'request' },
    chrome: {
      runtime,
      action: { setBadgeText() {}, setBadgeBackgroundColor() {}, setTitle() {} },
      storage: { local: { get(defaults, callback) { callback({ ...defaults, ...settings }); } }, onChanged: { addListener() {} } }
    }
  });
  return {
    calls: () => calls,
    request: (action, params = {}, url = 'chrome-extension://test/popup.html') => new Promise(resolve =>
      listener({ type: 'project-bridge-call', action, params }, { url }, resolve))
  };
}

test('history management requires extension UI and local permission', async () => {
  for (const action of ['history_list', 'history_restore', 'history_clear']) {
    const b = bridge();
    assert.equal((await b.request(action, {}, 'https://chatgpt.com/')).ok, false);
    assert.equal(b.calls(), 0);
    assert.equal((await b.request(action)).ok, true);
    const disabled = bridge({ localConnectionEnabled: false });
    assert.equal((await disabled.request(action)).permissionRequired, 'localConnectionEnabled');
    assert.equal(disabled.calls(), 0);
  }
});

test('SSH off blocks all remote actions including batches before native messaging', async () => {
  const b = bridge();
  for (const action of ['exec_server', 'remote_tree', 'remote_search', 'remote_read_file', 'remote_apply_patch', 'test_server']) {
    assert.equal((await b.request(action)).permissionRequired, 'allowServerExecution');
    assert.equal((await b.request('batch', { actions: [{ action }] })).permissionRequired, 'allowServerExecution');
  }
  assert.equal(b.calls(), 0);
  assert.equal((await b.request('read_file')).ok, true);
});

test('bridge off never calls native host for profiles or ping', async () => {
  const b = bridge({ bridgeEnabled: false });
  for (const action of ['ping', 'server_profiles']) assert.equal((await b.request(action)).disabled, true);
  assert.equal(b.calls(), 0);
});

test('SSH on permits remote requests and bindings remain extension-only', async () => {
  const b = bridge({ allowServerExecution: true });
  assert.equal((await b.request('remote_tree')).ok, true);
  assert.equal((await b.request('bind_project_server', {}, 'https://chatgpt.com/')).ok, false);
});

test('missing native host gives installation instructions', async () => {
  const b = bridge({}, 'Specified native messaging host not found.');
  assert.match((await b.request('ping')).error, /scripts\/install\.ps1/);
});

test('local off blocks local operations and mixed batches while SSH remains available', async () => {
  const b = bridge({ localConnectionEnabled: false, allowExecution: true, allowServerExecution: true });
  for (const action of ['prepare_context', 'search_code', 'read_file', 'project_tree', 'git_status', 'git_diff', 'write_files', 'apply_patch', 'undo', 'run_test', 'exec_local']) {
    assert.equal((await b.request(action)).permissionRequired, 'localConnectionEnabled');
    assert.equal((await b.request('batch', { actions: [{ action: 'remote_tree' }, { action }] })).permissionRequired, 'localConnectionEnabled');
  }
  assert.equal(b.calls(), 0);
  assert.equal((await b.request('remote_tree')).ok, true);
  assert.equal((await b.request('ping')).ok, true);
});
