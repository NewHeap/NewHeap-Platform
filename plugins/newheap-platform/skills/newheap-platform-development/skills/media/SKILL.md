---
name: newheap-media-development
description: Implement or review NewHeap consumer media storage, folders, files, metadata, thumbnails, HTTP contracts, domain authorization and media events.
---

# NewHeap media development

Read [media](references/media.md) before changing media composition or behavior.

Select the storage adapter in the composition root. Keep folder, tag, property and authorization rules in typed consumer services, enforce rights in the backend and expose one typed HTTP contract with documented content types and limits.

Keep relational file-structure providers independent from blob storage adapters. Handle blob and metadata failures as one explicit consistency boundary and avoid leaking provider details into neutral contracts.

Verify folder and file lifecycles, upload and download, metadata, search, thumbnails, authorization, events, missing blobs, limits and cleanup. Exercise relational metadata on SQL Server and PostgreSQL and run the selected blob adapter through the same storage contract.
