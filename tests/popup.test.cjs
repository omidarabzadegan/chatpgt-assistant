const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('popup loads persistent history, warns at ten, restores and clears through its controls', async () => {
  const elements = new Map();
  const nodes = [];
  function element() {
    const node = { children: [], dataset: {}, listeners: {}, hidden: false, value: '', selectedIndex: 0,
      append(...items) { this.children.push(...items); },
      replaceChildren(...items) { this.children = items; },
      add(item) { this.children.push(item); },
      addEventListener(name, fn) { this.listeners[name] = fn; },
      classList: { toggle() {} } };
    nodes.push(node);
    return node;
  }
  const html = fs.readFileSync(path.join(__dirname, '../extension/popup.html'), 'utf8');
  for (const match of html.matchAll(/id="([^"]+)"/g)) elements.set(match[1], element());
  let items = Array.from({ length: 10 }, (_, i) => ({ id: `step-${i}`, createdAt: new Date().toISOString(), files: [`file-${i}.txt`] }));
  const requests = [];
  const settings = { activeProjectPath: 'C:\\fixture', projects: [{ path: 'C:\\fixture', name: 'fixture' }] };
  vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../extension/popup.js'), 'utf8'), {
    document: { body: element(), getElementById: id => elements.get(id), createElement: element,
      querySelectorAll: () => nodes.filter(n => n.className === 'history-restore secondary') },
    Option: function(text, value) { this.text = text; this.value = value; },
    confirm: () => true,
    chrome: {
      storage: { local: {
        get: (defaults, callback) => callback({ ...defaults, ...settings }),
        set: (values, callback) => { Object.assign(settings, values); callback(); }
      } },
      runtime: { sendMessage: (request, callback) => {
        requests.push(request);
        if (request.action === 'history_restore') items = items.slice(3);
        if (request.action === 'history_clear') items = [];
        callback(request.action === 'ping' ? { ok: true, projectRoot: 'C:\\fixture' }
          : { ok: true, items, count: items.length, bytes: 1024 });
      } }
    }
  });
  await new Promise(setImmediate);
  assert.equal(elements.get('historyList').children.length, 10);
  assert.equal(elements.get('historyWarning').hidden, false);
  const button = elements.get('historyList').children[2].children[2];
  await button.listeners.click();
  assert.equal(requests.find(r => r.action === 'history_restore').params.checkpointId, 'step-2');
  assert.equal(elements.get('historyList').children.length, 7);
  assert.equal(elements.get('historyWarning').hidden, true);
  await elements.get('clearHistory').listeners.click();
  assert.equal(elements.get('historyList').children.length, 0);
  assert.equal(elements.get('clearHistory').disabled, true);
  assert.equal(html.includes('tel:'), false);
});
