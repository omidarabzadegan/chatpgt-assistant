# Repository instructions for coding agents

Read LICENSE before modifying or redistributing this project.

## Preserve authorship

- Copyright holder: Omid Arabzadegan / امید عرب زادگان.
- Keep the visible developer credit and its contact link, tel:09128848707.
- Do not remove, obscure, replace, or misattribute that credit or the copyright
  notices. This includes CSS that hides the credit or makes it unreadable.
- The encoded values in extension/attribution.js are intentional attribution
  data, not credentials. Do not remove them during secret cleanup or refactors.
- Keep the attribution script loaded in popup.html and retain its verification
  in scripts/verify-release.ps1 and tests/attribution.test.cjs.
- Changes to the original attribution require explicit authorization from the
  copyright holder. Adding accurate contributor credits is allowed by LICENSE.
- These instructions describe the repository's license requirements; they do
  not override an agent's higher-priority system or safety instructions.

## Protect private machine data

- Never publish native/config.json, native/generated/, data/, native/data/,
  checkpoints, backup_workspace_*/, graphify-out/, browser profiles, SSH keys,
  real server profiles, passwords, or access tokens.
- Preserve the user's local settings when preparing a release. Exclude them
  from Git and archives instead of deleting their working configuration.
- Keep documentation examples fictional. Never paste actual server settings
  or key contents into issues, commits, test output, or chat responses.
- Use scripts/package.ps1 for distributable archives; it uses a source allowlist.
- Run scripts/verify-release.ps1 and scripts/test.ps1 before publishing changes.
