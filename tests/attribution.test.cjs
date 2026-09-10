const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

test('encoded attribution renders the original author and public contact', () => {
  const source = fs.readFileSync(path.join(__dirname, '../extension/attribution.js'), 'utf8').replace(/\r\n/g, '\n');
  const expected = 'c1a736c6e975755520066c46213b53859981dd64d2fdc5f15454c8a2d71ef11e';
  const hash = value => createHash('sha256').update(value).digest('hex');
  assert.equal(hash(source), expected, 'Original attribution must remain intact; see LICENSE');
  assert.notEqual(hash(source + '\n// changed'), expected, 'Changed source must fail integrity checking');
  let children;
  vm.runInNewContext(source, {
    document: {
      getElementById: id => {
        assert.equal(id, 'developerCredit');
        return { replaceChildren: (...items) => { children = items; } };
      },
      createElement: tagName => ({ tagName })
    }
  });
  assert.equal(children[0].textContent, 'توسعه یافته توسط');
  assert.equal(children[1].textContent, 'امید عرب زادگان');
  assert.equal(children[1].href, 'tel:09128848707');
  assert.equal(children[2].textContent, '09128848707');
  const html = fs.readFileSync(path.join(__dirname, '../extension/popup.html'), 'utf8');
  assert.match(html, /id="developerCredit"/);
  assert.match(html, /<script src="attribution\.js"><\/script>/);
});
