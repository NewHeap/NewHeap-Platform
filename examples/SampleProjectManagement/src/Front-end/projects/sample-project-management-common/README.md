# sample-project-management-common

This small source library contains only the shared domain contract and the
NewHeap API client for the two sample applications. It deliberately does not
add a generic repository, facade, or base-component layer.

The generated case catalog (`src/lib/sample-cases.ts`) is not part of the
`sample-project-management-common` barrel. Import it from
`sample-project-management-common/sample-cases` in lazily loaded code only, so it
stays out of the initial bundles of both applications.
