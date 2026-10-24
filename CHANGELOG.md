# Changelog

## 0.9.0-download-only — 2026-10-24

- Replace the shipping home/accounts/play workflow with a native download workspace.
- Compile a separate, explicitly allowlisted download assembly without authentication or game execution.
- Add searchable version channels, offline catalog cache, named Vanilla instances and a persistent queue with interrupted-session recovery.
- Add pause/resume, task retry, verified repair, instance rename, folder access and a minimal JSON download report.
- Fix legacy classifier-only libraries being incorrectly treated as unclassified Maven artifacts, which caused official downloads to fail with HTTP 404.
- Update product scope, privacy and AppID-review documentation.
- Add regression coverage for distribution boundaries, queue recovery, safe reports, catalog persistence and selective corruption repair.

Earlier features and release artifacts are historical; this release does not claim to provide their gameplay, server or runtime functionality.
