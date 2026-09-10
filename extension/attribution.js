/* Copyright (c) 2026 Omid Arabzadegan. LicenseRef-Attribution-Preserving-1.0.
 * Required visible credit: see LICENSE and AGENTS.md.
 * Encoded strings discourage accidental edits; they are not encryption.
 */
(() => {
  "use strict";
  const attribution = Object.freeze({
    name: "\u0627\u0645\u06cc\u062f \u0639\u0631\u0628 \u0632\u0627\u062f\u06af\u0627\u0646",
    label: "\u062a\u0648\u0633\u0639\u0647 \u06cc\u0627\u0641\u062a\u0647 \u062a\u0648\u0633\u0637",
    phone: "\x30\x39\x31\x32\x38\x38\x34\x38\x37\x30\x37"
  });
  const target = document.getElementById("developerCredit");
  if (!target) return;
  const label = document.createElement("span");
  label.textContent = attribution.label;
  const contact = document.createElement("a");
  contact.textContent = attribution.name;
  contact.href = `tel:${attribution.phone}`;
  contact.title = `${attribution.name} — ${attribution.phone}`;
  const number = document.createElement("span");
  number.textContent = attribution.phone;
  number.dir = "ltr";
  target.replaceChildren(label, contact, number);
})();
