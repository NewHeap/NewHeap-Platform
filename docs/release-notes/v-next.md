# v-next

## @newheap/platform-common: modal accessibility

| Breaking change | Required action |
| --- | --- |
| The dialog role, ARIA name and fallback focus target moved from `.nh-modal-holder` to `.nh-modal`. | Update selectors and tests to target `[role="dialog"]` or `.nh-modal`; pass a meaningful translated `title`. |
