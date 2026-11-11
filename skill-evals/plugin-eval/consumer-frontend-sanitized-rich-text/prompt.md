---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-frontend-development; guidance rules nh-frontend-html-sanitizer; workspace management-portal."
tags: ["frontend"]
expected_outcome: "The page imports NhSanitizeHtmlPipe from @newheap/platform-common/sanitize, installs the optional dompurify peer and binds [innerHTML] through nhSanitizeHtml with the strict default allow-list, which keeps structure and links with rel=\"noopener noreferrer\" and removes scripts, styles, event handlers, frames, forms and images; safeHtml is replaced because it bypasses Angular's sanitizer, any allow-list change spreads the exported defaults, and tests cover XSS payloads and the escaped text of a server render."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Show the e-mail bodies that support staff paste into a ticket as formatted text, without letting scripts or tracking images from the e-mail run in our Angular application. The ticket page currently uses safeHtml.
