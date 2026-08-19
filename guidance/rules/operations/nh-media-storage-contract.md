---
id: nh-media-storage-contract
title: "Media storage and authorization as a consumer contract"
area: media
reference: operations
summary: "Choose an independent storage-provider package in the composition root, keep media authorization domain-specific, and provide one typed HTTP contract for folders, files, metadata, and events."
sample-cases: ["SPM-177", "SPM-178", "SPM-179", "SPM-180", "SPM-181", "SPM-182", "SPM-183", "SPM-184", "SPM-185", "SPM-186", "SPM-187", "SPM-188"]
public-symbols: ["UseFileSystemMediaStorage", "NhMediaServiceConfigurationContext", "IAuthorizationModule"]
skills: ["newheap-consumer-development"]
providers: ["sql-server", "postgresql"]
risk: high
---
## Preferred approach

Select a file-system, S3, or other storage adapter only in the composition root. Keep media folders, tags, properties, and domain authorization in typed consumer services. Enforce upload, download, and mutation rights in the backend, and document content types, limits, and responses in OpenAPI and Scalar. Publish typed media events within the agreed transactional boundary.

Reference only the relational provider that the application uses. The PostgreSQL file-structure package depends on the neutral Media.Core contract and does not require the SQL Server provider package; the SQL Server provider follows the same boundary.

Test PostgreSQL and SQL Server for all relational metadata, migrations, lookup hashes, folder operations and file operations. Test the selected blob adapter separately with the same storage contract tests.

## Avoid

- Storage-provider choices in controllers or frontend code.
- Trusting only file extensions for content type or safety.
- Creating a media record without handling the corresponding blob failure and rollback.
- Leaking provider-specific metadata into a neutral media contract.

## Verification

Test the folder lifecycle, upload and download, search and sorting, metadata, thumbnails, authorization, and events. Check missing blobs, oversized uploads, forbidden access, and consistent cleanup after failures.
