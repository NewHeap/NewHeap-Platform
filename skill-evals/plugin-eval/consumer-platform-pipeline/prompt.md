---
# Generated from skill-evals/evals.json by npm run guidance:generate. Do not edit.
description: "Skill newheap-backend-development; guidance rules nh-backend-platform-pipeline; workspace management-portal."
tags: ["backend"]
expected_outcome: "The API registers the platform with AddNewHeapPlatformAspNetCommon, calls UseEndpointRoutingOnly() on NewHeapAspNetCommonOptionsBuilder and builds the pipeline once with UseNewHeapPlatformAspNetCommon; it maps the area route with MapAreaControllerRoute in the UseEndpoints callback instead of UseMvc(Action<IRouteBuilder>), and neither turns EnableEndpointRouting off nor adds UseRouting, UseAuthentication or UseAuthorization around the platform pipeline."
max_turns: 30
timeout_seconds: 900
allowed_tools: ["Read", "Glob", "Grep", "Skill", "Write", "Edit"]
append_system_prompt: "This is an automated evaluation without a person to answer questions. The current directory is the repository that the request refers to. When you cannot run a command, such as a package restore, build, test or script, state the exact command and arguments you would run instead. If you would need to ask the user something before continuing, ask it in your final message and stop. End with a short summary of the guidance you used, the decisions you made, the files you changed and the commands you ran or would run."
---

Set up the ASP.NET composition of a new NewHeap API so that its controllers get endpoint metadata such as authorization and CORS attributes, and add a conventional route for an MVC area.
