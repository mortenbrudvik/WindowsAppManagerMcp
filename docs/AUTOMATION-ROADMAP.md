# Automation Roadmap

This roadmap covers improving and extending the automation capabilities of Windows App Manager MCP. It comes from an automated feature review on 2026-10-10: two independent multi-agent review runs. Each run had seven subsystem reviewers, an adversarial verifier per subsystem, an external landscape scan, a roadmap synthesizer and a critic. The two runs were then merged into this document. The review was static: no tests or binaries were run.

**Findings:** 120 distinct verified defects (2 critical, 19 high, 62 medium, 37 low). Both runs independently found 90 of them; 34 are fixed or partly fixed by M0. The full register is in the [appendix](#appendix-defect-register). File and line references are as of commit `309c821`.

Task status is tracked in [BACKLOG.md](../BACKLOG.md) (Phase 13). This document holds the design notes.

## Assessment

Both reviews reach the same diagnosis of WindowsAppManagerMcp. The architecture is sound: services behind interfaces, DI, mockable native wrappers, per-monitor DPI, structured Success/ErrorCode results, and UIA and CDP read paths. It is not yet safe or reliable for an agent to drive a Windows task from start to finish. Five problems stand out. (1) The model cannot see screenshots, because they arrive as base64 inside a JSON text block. (2) Keyboard and mouse input has no target and no verified activation, so it can land in the agent's own terminal; the test suite already did this with Alt+F4. (3) Many tools report success after doing the wrong thing or nothing at all: maximize on the wrong monitor, unknown states treated as restore, save_layout always true, CDP reads from the wrong tab, and a controlTypes filter that drops nested matches. (4) Headline features are broken: a stale plugin binary, an unserializable debug-launch result, and no published release. (5) Everything is addressed by raw HWND plus hand-computed physical pixels, with no waits and no element actions, so a simple form fill costs 4-10 round trips that cannot be verified. The plan works in layers. M0, shipped in 1.12.0, stops the bleeding. M1 makes seeing and typing safe by default. It starts with a default host-window input guard, ships image content together with the true capture origin, and adds verified activation, guarded input with receiving-window reporting, and a desktop-safe fixture e2e suite to validate the Windows-specific behaviour. M2 makes results honest and settles the MCP surface (contract tests, annotations, stable SDK, curated toolsets) before M3-M5 add about 30 tools. It also pulls scroll and clipboard forward. M3 is the biggest lever. It moves from blind coordinates to selector-addressed, pattern-first UIA actions with stable refs, popup-aware search, Chromium accessibility activation, text and table reading, virtualized items and server-side waits, each returning verified post-state. That cuts typical tasks to 1-2 calls that can each be checked. M4-M7 complete the input vocabulary, build a thin browser bridge with a Playwright handoff, add breadth, and hold the deferred items. Design assumptions were checked against the repo. ModelContextProtocol 0.5.0-preview.1 already exposes McpServerOptions.Filters.CallToolFilters and McpServer.ElicitAsync, so the M0 exception filter and M2 elicitation can be built on the current SDK. ProcessStartInfo.ArgumentList is honored with UseShellExecute=true on .NET 10; re-check this on the net8 target. The main residual risks are Windows-behaviour details the critics flagged: the foreground-lock unlock, popup hit-testing, InvokePattern blocking while its modal is open, Edge title formats, and stale element refs. That is why the fixture e2e suite sits in M1, ahead of the large feature work.

## Milestones

| Milestone | Goal | Items |
|-----------|------|-------|
| [M0: Stop the bleeding](#m0-stop-the-bleeding) | Every later fix reaches users and can be tested without touching the developer's desktop. The most dangerous confirmed defects are gone: unsafe tests, the stale binary, the IntPtr crash and opaque errors, the '(x86)' launch block, silent kill escalation, races and UIA crashes, lost layouts, CDP hangs and wrong-tab reads, and docs that teach Alt+F4. | 8 |
| [M1: See and type safely](#m1-see-and-type-safely) | The model sees real pixels it can click from accurately. Keyboard and mouse input cannot reach the agent's own host, a shell window, an elevated or deny-listed window, or an unintended target. Every input result reports where it actually went. The user can pause everything with a hotkey. A desktop-safe suite against a self-owned fixture app validates the real foreground, UIPI and capture behaviour. | 12 |
| [M2: Honest results and a stable MCP surface](#m2-honest-results-and-a-stable-mcp-surface) | Every tool either does what it reports or says precisely why not: typed parameters, real post-state, verified close, handoff-aware launch and faithful layouts. The MCP surface (contract tests, generated docs, annotations, stable SDK, curated toolsets, server instructions) is settled before M3-M5 add about 30 tools. Scroll and clipboard, the two primitives agents miss first, are pulled forward. | 17 |
| [M3: Semantic find-act-wait](#m3-semantic-find-act-wait) | The biggest lever. Move from blind coordinates to selector-addressed, pattern-first UI Automation, so 'click Save in app X' becomes 1-2 calls that can each be verified. Elements are found by name, automationId and type, including in menus, popups, dialogs, Chromium/Electron apps and virtualized lists. Agents act through UIA patterns with verified post-state, read text and tables directly, and wait server-side instead of sleeping. | 14 |
| [M4: Input vocabulary](#m4-input-vocabulary) | Keyboard and mouse produce exactly what was asked, on any keyboard layout and button configuration. The tools cover the chords, drags, multi-clicks, modifier-clicks and long text that real tasks need, and nothing can stay held down after a call returns. | 4 |
| [M5: Browser bridge](#m5-browser-bridge) | Reliable CDP for debug-enabled Chromium, bound to the right window and tab, with a few high-value tools: readiness, tabs, navigation, waits and paged content. The UIA fallback is locale-independent, and anything richer is handed to Playwright MCP explicitly. After M2 this milestone can run in parallel with M3/M4. | 8 |
| [M6: Platform, efficiency and breadth](#m6-platform-efficiency-and-breadth) | Fewer, leaner tools and fewer round trips through consolidation and run_steps. Coverage for surfaces UIA misses (OCR, visual change detection), packaged apps, richer process, monitor and z-order control, layout schema v2 and MCP resources and prompts. Most of these sit behind opt-in toolsets. | 9 |
| [M7: Later](#m7-later) | Valuable but deferred: a modern capture backend and visual aids, event-driven window tracking, virtual-desktop detection, opt-in takeover detection, best-effort workspace restore, and a scenario harness that measures round trips. | 6 |

## M0 Stop the bleeding

Every later fix reaches users and can be tested without touching the developer's desktop. The most dangerous confirmed defects are gone: unsafe tests, the stale binary, the IntPtr crash and opaque errors, the '(x86)' launch block, silent kill escalation, races and UIA crashes, lost layouts, CDP hangs and wrong-tab reads, and docs that teach Alt+F4.

M0 shipped in release 1.12.0 on 2026-10-10 (PR #1, release commit ef7d56f). It covers the 8 planned items plus integration fixes, a six-lens final review, and a hardening round driven by an external review. CI on main is green: 1509 tests, a check that desktop tests stay skipped on the runner, and a strict check of the committed plugin exe on the release tag. The repository's first GitHub release was created from that tag.

- M0.3 shipped differently from its first design: failures come back as structured isError results with an errorCode instead of a rethrown McpException, and every tool call is logged with its duration. The M0.2 and M0.3 designs below describe what shipped.
- The DesktopNoInput test tier planned in M0.1 is deferred to M1.11, because no capture-only desktop tests exist yet.
- marketplace.json metadata.version stays at 1.0.0 on purpose: it versions the marketplace listing, not the plugin. Set-Version.ps1 stamps plugin.json and the plugin entry.
- Beyond the plan, M0 also routes every process start and kill through NativeProcessWrapper, checked by IL guard tests that now work as an allow-list. It stops tracking .claude/settings.local.json, replaces the third-party release action with the GitHub CLI, pins the remaining third-party actions to commit SHAs, and adds a CI step that proves desktop tests stay skipped.
- The plugin exe in bin/ was rebuilt for 1.12.0 with scripts/Publish-Plugin.ps1 and reports 1.12.0+fc8d350.

### M0.1 Deny-by-default desktop test gating

**Priority:** P0 · **Effort:** M · **Kind:** fix · **Branch:** `m0-test-isolation` (Shipped in 1.12.0)

Builds on the merged input-isolation fix (6c4648d). Any test that touches the real desktop is skipped unless the developer explicitly opts in, so no runner, filter or worktree can send input, launch apps or kill processes by accident. CI runs only non-desktop tests.

**Design.** Add [DesktopFact]/[DesktopTheory] (plus a DesktopNoInput variant for self-owned-window capture tests). They set Skip unless an opt-in env var such as WAM_DESKTOP_TESTS=1 is present. The deny is in code because runsettings and --filter are bypassed by IDE runners, explicit filters and agents running plain dotnet test in .worktrees. Keep [Trait('Category','Desktop')] only for CI selection. Inject IProcessLauncher (Start(ProcessStartInfo) -> IProcessHandle) into ProcessService so ProcessServiceTests:458/479/500 stop launching Chrome and Notepad. Replace the KillProcess-on-csrss/lsass test (:431) with a pure test of the ProtectedProcesses set. Optional: widen InputEvent now to (Kind, Vk, Scan, Flags, Dx, Dy, MouseData), so later scan-code, absolute-move and wheel work does not force a test rewrite. test.yml runs non-desktop tests with --blame-hang-timeout and a re-baselined coverage gate. Remove the unfiltered 'dotnet test' pre-approval from .claude/settings.local.json and the README. Deferred: the DesktopNoInput variant moves to M1.11, where the first self-owned-window capture tests need it.

*Sources: r1:R1, r2:R1, r1:crit:R1, r2:crit:R1, r1:INP-12, r1:MCP-5, r2:INP-1, r2:MSP-6, r2:PAL-G10, r2:MSP-G5*

### M0.2 Single-source version and working release pipeline

**Priority:** P0 · **Effort:** M · **Kind:** fix · **Branch:** `m0-release-pipeline` (Shipped in 1.12.0)

Make the exe the plugin runs the one built from main. Make the version visible over MCP, make tag pushes produce GitHub releases, and remove stale duplicate binaries and wrong publish paths.

**Design.** As shipped: Directory.Build.props holds the single <Version>, and Program.cs reports it, with the commit SHA, as ServerInfo.Version. scripts/Set-Version.ps1 stamps Directory.Build.props, .claude-plugin/plugin.json and the plugin entry in marketplace.json. scripts/Publish-Plugin.ps1 publishes into bin/, the path plugin.json runs, and refuses uncommitted build inputs (and -AllowDirty into bin/). scripts/Test-PluginBinary.ps1 checks the exe's version and that no build input (src/, config/, root build files) changed since the commit it was built from. /commit and /update-plugin call these scripts. The stale dist/, publish-new/ and .claude-plugin/bin binaries are gone. build.yml checks the exe strictly on v* tags and whenever a push or pull request changes it. Its release job (contents: write) creates the GitHub release with the gh CLI, and every third-party action is pinned to a commit SHA. README states the real runtime requirement (.NET 8 Windows Desktop Runtime). Still open from the original design: a CI guard that starts the committed exe and diffs tools/list against a schema snapshot (M2.12).

*Sources: r1:R2, r2:R2, r1:crit:R2, r1:MCP-3, r1:MCP-G8, r1:MCP-12, r2:MSP-4, r2:MSP-5, r2:MSP-G6, r2:MSP-14, r2:MSP-15*

### M0.3 Tool contract: serializable DTOs, serialization test, exception-surfacing filter

**Priority:** P0 · **Effort:** S · **Kind:** fix · **Branch:** `m0-tool-contract` (Shipped in 1.12.0)

Fix the launch_browser_with_debug crash, which comes from System.Text.Json refusing to serialize IntPtr. Add a test over every tool that would have caught it, and replace the SDK's opaque 'An error occurred invoking' with actionable messages.

**Design.** As shipped: DebugBrowserLaunchResultDto uses long? for the window handle, and a contract test serializes every tool's result type with all members populated using the SDK's McpJsonUtilities.DefaultOptions. A call-tool filter (Infrastructure/ToolErrorReporting.cs, registered through the shared ServiceRegistration used by Program.cs and the DI tests) returns a structured isError result instead of rethrowing: errorCode ToolError for deliberate McpException (or the specific code of a ToolErrorException, e.g. PresetNotFound, StorageUnavailable), InvalidArguments for argument-binding failures, UnhandledException for anything else; cancellation and protocol errors pass through. It logs every call's start and completion (tool, duration, outcome; never arguments or results) in the WindowsAppManagerMcp.ToolCalls category at Information, with a Warning for slow calls.

*Sources: r1:R6, r2:R3, r1:R8, r1:crit-prio:R8, r1:R3, r1:MCP-2, r2:BRW-1, r2:MSP-2, r2:BRW-G7*

### M0.4 Launch validation, argument quoting and truthful kill_process

**Priority:** P0 · **Effort:** M · **Kind:** fix · **Branch:** `m0-launch-and-kill` (Shipped in 1.12.0)

Allow normal paths such as 'Program Files (x86)' while guarding against argument injection through .bat/.cmd files. Quote arguments correctly. kill_process must never silently escalate to a hard kill and must report what really happened.

**Design.** InputValidationService: drop the shell-metacharacter check on FileName and WorkingDirectory, because UseShellExecute=true never goes through cmd.exe. Keep the control-character and embedded-quote checks. Use Path.GetFullPath plus Exists for rooted paths, and set an ErrorCode on every rejection branch. Build arguments with ArgumentList, verified to apply under ShellExecute on .NET 10 (confirm on net8), or with a CommandLineBuilder.Join that follows CommandLineToArgvW rules for backslashes before quotes, embedded quotes and empty arguments. Pass --user-data-dir without manual quotes. If the resolved target is .bat/.cmd, including PATH shims such as code.cmd, reject arguments containing & | < > ^ % ! or a double quote unless allowShellMetacharacters=true (the BatBadBut class). Debug-launch url: Uri.TryCreate with an http/https/file allowlist; trusted browser paths bypass the user-input validator. kill_process(processId, confirm, mode=graceful|force (default graceful), gracefulTimeoutMs=5000, includeChildren=false, expectedProcessName/StartTime to guard PID reuse). Graceful mode posts WM_CLOSE only to the Alt-Tab set (visible, unowned, non-WS_EX_TOOLWINDOW), never to IME, tray or hidden helper windows. It never escalates: it returns exited=false with status stillRunning or blockedByPrompt and the GW_ENABLEDPOPUP dialog. Force mode calls Kill(entireProcessTree: includeChildren). Check the WaitForExit result. Results add method, exited and killedPids. Map Win32Exception 5 to AccessDenied and server ancestors to RefusedHostAncestor. Extend ProtectedProcesses with sihost, ctfmon, StartMenuExperienceHost, ShellExperienceHost, SearchHost, TextInputHost and fontdrvhost.

*Sources: r1:R49, r2:R36, r2:R9, r1:R55, r2:crit:R9, r1:PAL-2, r1:PAL-5, r1:PAL-7, r1:PAL-8, r1:PAL-9, r1:BRW-5, r1:BRW-14, r1:PAL-missed-3, r1:PAL-missed-5, r1:MCP-missed-3, r2:PAL-2, r2:PAL-3, r2:PAL-7, r2:PAL-13, r2:PAL-14, r2:BRW-2, r2:BRW-9, r2:BRW-13*

### M0.5 Concurrency safety and a UIA traversal that survives bad nodes

**Priority:** P0 · **Effort:** M · **Kind:** fix · **Branch:** `m0-concurrency-and-uia-crash` (Shipped in 1.12.0)

The SDK runs tool calls concurrently. Make the monitor state an atomic snapshot and serialize all injected input. Make UIA traversal survive null ControlType (WinUI AppBar and SemanticZoom) and per-element failures instead of aborting the whole call.

**Design.** MonitorService.RefreshMonitors builds a local ImmutableArray and publishes it atomically with Volatile.Write. Each public method reads exactly one snapshot, and GetAllWindows/FindWindows take one snapshot per enumeration. Optionally use a ~250 ms TTL plus SystemEvents.DisplaySettingsChanged invalidation behind an INativeMonitorWrapper. InputSimulationService gets a process-wide SemaphoreSlim(1,1) InputGate held for each whole Click, TypeText or SendKeys (InputBusy on timeout), so streams never interleave and Ctrl is never held across another call. UIA: when ControlType is null (ids 50039/50040 under the managed client), fall back to LocalizedControlType and then 'Unknown' instead of TryGetValue(null) aborting the call. Raw ids cannot be read through System.Windows.Automation. Wrap each node in try/catch that skips the node, adds a Warning and counts SkippedElements. Enumerate children so that one vanished sibling cannot end the walk. The LayoutService cache is handled in M0.6.

*Sources: r1:R7, r2:R26, r1:R42, r2:R20, r2:R11, r1:WC-11, r1:INP-7, r1:SCR-11, r1:MCP-13, r1:UIA-2, r1:UIA-missed-2, r2:WC-10, r2:INP-missed-monitor-race, r2:SCR-missed-monitor-race, r2:UIA-3*

### M0.6 Layout storage outside the versioned plugin folder, with honest results

**Priority:** P0 · **Effort:** S · **Kind:** fix · **Branch:** `m0-layout-storage` (Shipped in 1.12.0)

Saved layouts live inside the versioned plugin folder today, so every update wipes them, and the sample presets never ship. Move storage, seed the samples, migrate old files, report save and delete outcomes honestly, reload from disk and make the cache thread-safe.

**Design.** Default to %LOCALAPPDATA%/WindowsAppManagerMcp/layouts, overridable with one env var (pick WAM_LAYOUTS_DIR or WAM_LAYOUTS_PATH) that plugin.json can set. Embed config/layouts/*.json as resources and seed them on first run. Migrate any *.json found in AppContext.BaseDirectory/layouts once. Create the directory lazily and return StorageUnavailable instead of failing the singleton factory. save_layout passes the write result through (SaveFailed), returns InvalidName for bad names, and takes overwrite with a replaced flag. delete_layout returns PresetNotFound. get_layout and apply_layout reload from disk using an mtime check. _presetCache becomes a ConcurrentDictionary with a reload lock. The path is reported later in get_server_info.

*Sources: r1:R57, r2:R39, r2:crit-prio:R39, r1:PAL-3, r1:PAL-16, r1:MCP-8, r1:MCP-missed-1, r2:PAL-5, r2:MSP-11, r2:PAL-missed-layout-cache-stale*

### M0.7 CDP timeouts, safe tab selection and explicit data source

**Priority:** P0 · **Effort:** M · **Kind:** fix · **Branch:** `m0-cdp-timeouts` (Shipped in 1.12.0)

No CDP call may hang; today one hangs forever when a JS alert is open. Never return another tab's or another browser's data with Success=true. Every result says whether it came from CDP or UIA.

**Design.** Browser tools become async with timeoutMs (default 5000-10000, clamped 1000-30000) linked to the request CancellationToken. That token goes into ConnectAsync, SendAsync and ReceiveAsync, replacing CancellationToken.None at CdpService.cs:94/129/187/195 and the .GetAwaiter().GetResult() calls. Pass Runtime.evaluate its timeout. Timeouts return CdpTimeout. An open JS dialog is detected through Page.javascriptDialogOpening or a short probe and returns DialogOpen. Target selection: never match an empty title or host, and no reverse-substring matching. Require the listening-port owner PID (GetExtendedTcpTable) to equal the window's PID. When several targets remain, bind through Browser.getWindowForTarget windowId plus bounds. Use the title only as a tie-breaker, after a browser-aware normalizer strips Unicode Cf characters (U+200B), ' and N more pages' and profile/browser suffixes. Exact title equality would regress Edge. Otherwise return AmbiguousTarget with the candidates. Results add Source ('cdp'|'uia'), TargetId and CdpError. A new mode parameter takes auto|cdp|uia, and cdp mode never silently falls back. A textOnly=false request that UIA cannot honor is reported, not dropped.

*Sources: r1:R61, r2:R44, r2:crit-prio:R44, r1:crit:R61, r1:crit-prio:R65, r1:BRW-1, r1:BRW-2, r1:BRW-6, r1:BRW-13, r2:BRW-3, r2:BRW-4*

### M0.8 Docs safety hotfix

**Priority:** P0 · **Effort:** S · **Kind:** fix · **Branch:** `m0-docs-safety` (Shipped in 1.12.0)

Stop the skill and docs from teaching the failure that already closed the user's terminal, and from pointing agents at paths that are known to be broken. Document the four browser tools and fix the tool counts and parameter names.

**Design.** Delete the '%{F4}' and 'Press Alt+F4' guidance and blind destructive-chord examples: SKILL.md:65/231, README.md:130, TOOLS.md:1529-1536. Add a rule: confirm get_foreground_window before any destructive chord, and prefer close_window. Remove 'analyze the base64' (SKILL.md:200/241) and describe the real screenshot result shape and maxWidth/saveToFile until M1 image content lands. Remove the controlTypes filtering advice (SKILL.md:206/245) until M1.8 ships. Document get_browser_url, get_active_browser_url, get_browser_content and launch_browser_with_debug, with the CDP security note: unauthenticated local endpoint, empty profile. Set the tool count to 42 and fix parameter names (executable, not path; processId, not pid; positioning, not maximize). Replace 'wait briefly' with explicit poll-and-verify guidance until wait_for_window exists.

*Sources: r1:R11, r1:crit-prio:R11, r1:R4, r2:R4, r1:MCP-10, r1:BRW-15, r1:SCR-3, r2:MSP-7, r2:PAL-17, r2:BRW-15*

## M1 See and type safely

The model sees real pixels it can click from accurately. Keyboard and mouse input cannot reach the agent's own host, a shell window, an elevated or deny-listed window, or an unintended target. Every input result reports where it actually went. The user can pause everything with a hotkey. A desktop-safe suite against a self-owned fixture app validates the real foreground, UIPI and capture behaviour.

### M1.1 Default host-window input guard

**Priority:** P0 · **Effort:** S · **Kind:** fix · **Branch:** `m1-security-probes` (detection done; wiring open)

Input that names no target still goes to the focused window, which is usually the agent's own terminal. A cheap HostGuard on the default input path makes that impossible unless the agent explicitly targets the host.

**Design.** At startup a HostGuard singleton records three things. (a) The server's ancestor PIDs, from a Toolhelp parent walk with creation-time checks. (b) The root owner of GetConsoleWindow(). (c) GetForegroundWindow() at server start, which covers Windows Terminal default-terminal delegation, where WindowsTerminal.exe is not an ancestor. Before every SendInput batch, inside the input gate, resolve the receiving root window: GetAncestor(GetForegroundWindow(), GA_ROOTOWNER) for keys, and WindowFromPoint (a new P/Invoke) plus GA_ROOT for mouse input. If that window is in the host set, return TargetIsAgentHost with the observed foreground window, unless the call explicitly targets it with allowHostTarget=true. Add WindowInfo.IsAgentHost and report the host set in get_server_info. Unit-test with mocked wrappers.

**Shipped so far (detection only).** IHostGuard (HostGuard) is created at server start by a hosted service, so the foreground window it records is the one the agent was started from. It records the ancestors with their start times, the root owner of GetConsoleWindow() and the root owner of the startup foreground window, and answers IsHostProcess(pid) and IsHostWindow(hwnd), root-owner aware. Refinements: an ancestor PID that now belongs to a newer process stops counting, while one that cannot be verified still counts. The process that owns the console host window is a host process; the startup foreground window protects only itself and the popups it owns. The desktop shell (the owner of GetShellWindow()) is left out of the window rule, because explorer.exe is often an ancestor and also owns the taskbar, the desktop and File Explorer; a startup foreground window that belongs to the shell is not recorded. Still open: the input-path check with TargetIsAgentHost and allowHostTarget, the close guard (M1.7), WindowInfo.IsAgentHost and get_server_info.

*Sources: r1:crit-missing:host-input-guard, r2:crit-missing:host-input-guard, r1:crit:R55, r1:R55, r2:crit:R8, r1:INP-1, r1:MCP-4, r2:MSP-3*

### M1.2 Captures as MCP image content with the true origin and an image-to-screen transform

**Priority:** P0 · **Effort:** M · **Kind:** fix

Return every capture as an image content block the model can see, with vision-sized defaults. Ship the corrected window-capture origin and transform in the same change: downscaled images on top of a (0,0) origin would increase mis-clicks.

**Design.** A shared ToolImages helper returns CallToolResult{Content=[ImageContentBlock{Data, MimeType}, TextContentBlock(metadata JSON without ImageData)], IsError=!Success} and isolates the SDK's byte-API change. maxDimension sets the long edge (default 1568), with maxWidth kept as an alias. Default to jpeg q80 for monitor and window captures, and png for small regions or on request. An unknown format returns InvalidFormat. Encode from Format32bppRgb so the undefined alpha byte cannot make regions transparent. output=image|file|both|metadata, with saveToFile kept as an alias. outputPath is resolved with GetFullPath, needs an extension that matches the format, gets no implicit directory creation, and needs overwrite=true to replace an existing file. Origin: IScreenCaptureWrapper returns CaptureResult(Bgra, Width, Height, ScreenOriginX/Y, Method). Frame captures use the GetWindowRect origin; client captures use ClientToScreen(hwnd,{0,0}) (new P/Invoke). This replaces CapturedRegion(0,0) at ScreenshotService.cs:185, and capture_with_elements overlays use the same origin. Metadata: imageToScreen{offsetX, offsetY, scaleX, scaleY}, originalWidth/Height, mimeType, byteSize and coordinateSpace='physical-virtual-screen'. ScaleFactor is documented as informational only. Delete the dead SaveImageToFile and EncodeImage.

*Sources: r1:R15, r2:R12, r1:R17, r1:crit-prio:R17, r2:R13, r2:crit-prio:R13, r1:R5, r1:R55, r1:SCR-1, r1:MCP-1, r1:SCR-5, r1:SCR-12, r1:SCR-G3, r1:MCP-missed-2, r2:SCR-1, r2:MSP-1, r2:SCR-G1, r2:SCR-2, r2:SCR-9, r2:MSP-missed-overlay-offset*

### M1.3 Verified window activation (EnsureForeground)

**Priority:** P0 · **Effort:** M · **Kind:** fix · **Branch:** `m1-foreground` (Done)

One routine that gets a window in front despite the foreground lock and reports what is actually in front afterwards. focus_window, the input guard, element-action fallbacks and layout focus all use it.

**Design.** IWindowService.EnsureForeground(handle, timeoutMs=1000) returns FocusOutcome{IsForeground, ForegroundHandle/Title/Process, Method: already|setForeground|attachThreadInput|inputUnlock, BlockingPopup?, ErrorCode FocusFailed|NotResponding|Cloaked}. It runs on one dedicated long-lived thread, which gets a message queue on its first User32 call, so attach and detach happen on the same thread. Steps: (1) Succeed if the GA_ROOTOWNER of the foreground window is the target's root. Owned dialogs count as success; report them through GW_ENABLEDPOPUP. (2) Refuse a hung or cloaked target. (3) SW_RESTORE if the target is iconic. (4) SetForegroundWindow. (5) AttachThreadInput(GetCurrentThreadId(), fgTid, TRUE), then BringWindowToTop and SetForegroundWindow, detaching in a finally block. Skip this step when the foreground thread is hung or the foreground process has higher integrity. (6) Last resort: make the server the last-input process with a zero-delta MOUSEEVENTF_MOVE or an unassigned VK, never a lone Alt tap into the agent's terminal, then SetForegroundWindow. After activation, if GetGUIThreadInfo(targetTid).flags has GUI_INMENUMODE, send Esc to the target. Poll for verification after each step. focus_window returns FocusOutcome.

**As shipped.** EnsureForeground lives on its own IForegroundService (ForegroundService), next to IsInFront(handle), the same in-front rule as a cheap read for the input guard's re-checks; the unverified IWindowService.FocusWindow is gone. The thread calls are behind a new INativeThreadInputWrapper (GetCurrentThreadId, GetWindowThreadId, AttachThreadInput, GetGuiThreadInfo), and NativeCallGuardTests pins AttachThreadInput to it. ForegroundThread runs every activation on one long-lived background thread, one call at a time, started on first use. A call to another thread's window (ShowWindow, BringWindowToTop) can block until that thread answers, so a call that misses timeoutMs plus a 2 s grace returns NotResponding: its token is cancelled, so it takes no further step (no late activation), its thread finishes, detaches on the same thread and exits, and the next call starts a new thread; while 2 such threads are still blocked, calls fail at once. Refinements: the in-front rule is the GA_ROOTOWNER check plus, when the target is itself an owned window, its presence in the foreground window's GW_OWNER chain, so a dialog target's owner or sibling dialog does not count as the dialog. The dialog in front of the target is BlockingPopup; so is the GW_ENABLEDPOPUP of a target that a modal dialog disables (WS_DISABLED), and that dialog is what gets activated. The unlock is a zero-distance MOUSEEVENTF_MOVE only, not an unassigned VK: a key event goes to the keyboard focus, which is the agent's terminal exactly when activation is refused, and terminals can forward unknown keys to the console app; the unlock is skipped when the input desktop is not available. Esc is sent only after an activation step (never on "already"), at most twice (a dropdown, then the menu bar), each time right after re-verifying that the target is in front, and reported as MenuClosed. focus_window returns FocusResult (success kept, equal to isForeground; handle, method, foregroundHandle/Title/Process, blockingPopup, menuClosed, error, errorCode), and apply_layout reports one focus entry per focused placement. The E2E guard classifies AttachThreadInput as a new ThreadInputAttach kind: refused in the no-input tier, and in the input tier allowed only for the calling thread's own queue; a zero-distance mouse move may be sent wherever the cursor is. A gated scenario brings the fixture to the front. Not done here: bringToFront in move/snap/set_bounds/move_to_monitor still calls BringWindowToTop (r2:WC-missed-BringWindowToTop-activates); it was left out to keep WindowService edits off the sibling branches' code.

*Sources: r1:R25, r2:R6, r1:crit:R25, r2:crit:R6, r1:WC-1, r1:WC-G3, r2:WC-1, r2:WC-G4, r2:MSP-3, r2:WC-missed-BringWindowToTop-activates, r2:PAL-missed-apply_layout-ignores-focus-result*

### M1.4 Integrity, elevation and desktop-state probes

**Priority:** P0 · **Effort:** S · **Kind:** extend · **Branch:** `m1-security-probes` (Done)

A small mockable service that answers three questions: is the target more privileged than the server, is the input desktop available, and is the session locked. Without it, UIPI and secure-desktop failures are invisible to the agent.

**Design.** IProcessSecurityProbe uses a new NativeMethods.Advapi32 (OpenProcessToken; GetTokenInformation with TokenElevation and TokenIntegrityLevel; GetSidSubAuthority), User32 OpenInputDesktop/CloseDesktop and WTSQuerySessionInformation. Results are cached per PID plus creation time. GetIntegrity(pid) returns Low/Medium/High/System/Unknown; ACCESS_DENIED on OpenProcessToken from a medium-integrity server maps to LikelyElevated. It also exposes ServerIntegrity, IsInputDesktopAvailable() and IsSessionLocked(). Consumers: the input guard (TargetElevated, DesktopUnavailable, SessionLocked before SendInput); UIA warnings for elevated targets; WindowService Win32 error mapping (5 to AccessDenied); get_server_info; list_processes (M6.6).

**As shipped.** ProcessSecurityProbe sits on a read-only INativeSecurityWrapper, whose Advapi32, desktop and Wtsapi32 calls are on the NativeCallGuardTests allow-list. The levels add Untrusted and MediumPlus, and ProcessIntegrity.IsMorePrivilegedThan compares raw RIDs, as UIPI does. ACCESS_DENIED from OpenProcess maps to LikelyElevated too (system and protected processes deny it), but only while the server runs below high integrity; an elevated server reports Unknown. The cache key is the creation time that the token query itself read, so a PID reused in between is not mixed up; Unknown results are not cached, and the cache is bounded. IsInputDesktopAvailable also requires the input desktop to be the server's own desktop and the session not to be disconnected, because OpenInputDesktop still succeeds in a disconnected session. No consumer is wired yet: TargetElevated, DesktopUnavailable and SessionLocked arrive with M1.5.

*Sources: r2:R7, r1:R26, r1:R52, r1:INP-5, r1:PAL-G4, r1:MCP-G5, r2:INP-3, r2:UIA-8, r2:WC-9, r2:PAL-G4, r2:PAL-14, r2:MSP-G7*

### M1.5 Target-guarded input with receiving-window reporting

**Priority:** P0 · **Effort:** M · **Kind:** fix

Every mouse and keyboard tool can name its intended window. The server refuses to send input that would land elsewhere, including clicks into a modal-disabled window. Every result reports where the input actually went.

**Design.** Add an optional window target (a handle now; a selector after M2.4) to click, right_click, double_click, mouse_move, type_text, send_keys and every new input tool. IInputTargetGuard runs inside the M0 input gate. Probe checks (M1.4) come first. Keys: EnsureForeground, then require the foreground root to equal the target root. Mouse: accept the WindowFromPoint+GA_ROOT window if it equals the target root, is in the target's GW_OWNER chain, or belongs to the target process or a descendant (Toolhelp walk; covers WebView2/msedgewebview2 and IME popups). Otherwise return TargetOccluded. If IsWindowEnabled(target) is false, return BlockedByModal with the GW_ENABLEDPOPUP handle and title. type_text re-checks the foreground root every 32 characters and stops with TargetLostFocus and CharactersTyped. A configurable destructive-chord list (%{F4}, ^w, ^q, ^+{ESC}, #l) requires an explicit target, otherwise DestructiveChordNeedsTarget. Every input result, guarded or not, gains ReceivingWindow{Handle, Title, ProcessName, ProcessId}, ForegroundAfter and ActualX/Y. The host guard (M1.1) and the policy (M1.12) run on the same path.

*Sources: r1:R26, r2:R8, r2:crit:R8, r2:R11, r1:INP-1, r1:INP-5, r1:MCP-4, r1:UIA-G6, r1:INP-G6, r2:INP-2, r2:INP-3, r2:MSP-3, r2:INP-G6, r2:MSP-G3, r2:WC-G4*

### M1.6 Unfiltered window reads and user-facing listings

**Priority:** P0 · **Effort:** M · **Kind:** fix

Post-action reads, get_foreground_window and the guard must be able to see untitled popups, menus and splash screens. Listings must stop showing phantom cloaked or shell windows and must attribute UWP windows to the real app.

**Design.** INativeWindowWrapper gains GetClassName, GetWindowLongPtr(GWL_EXSTYLE), GetWindow(GW_OWNER), DwmGetWindowAttribute (CLOAKED=14, EXTENDED_FRAME_BOUNDS=9), IsHungAppWindow and GetDpiForWindow. ReadWindowInfo(hwnd) does no filtering and backs GetWindowInfo, get_foreground_window, post-action reads, captures and the guard's ReceivingWindow. The IsUserFacingAppWindow predicate applies to listings only: the window is visible, not cloaked, not WS_EX_TOOLWINDOW unless it is also WS_EX_APPWINDOW, unowned, and its class is not Progman, WorkerW, Shell_TrayWnd or Shell_SecondaryTrayWnd. Cheap filters run before Process lookups, and PID-to-name is cached per call. An explicit handle, className or includeShell=true bypasses the predicate, so the taskbar and tray stay reachable. For ApplicationFrameWindow, report the CoreWindow child's PID and process with HostProcessName=ApplicationFrameHost. find_windows gains includeCloaked, includeToolWindows and className. WindowInfo gains ClassName, IsCloaked and IsAgentHost.

*Sources: r1:R32, r2:R29, r1:crit:R32, r1:WC-4, r1:WC-5, r1:WC-13, r1:PAL-14, r1:UIA-6, r1:SCR-6, r1:WC-G2, r2:WC-3, r2:WC-8, r2:WC-12, r2:MSP-missed-untitled-windows, r2:PAL-missed-layout-captures-shell-windows*

### M1.7 Close guards for shell and agent-host windows

**Priority:** P0 · **Effort:** S · **Kind:** fix

close_window today posts WM_CLOSE to Program Manager (which opens the Shut Down dialog) or to the agent's own terminal after nothing more than an IsWindow check.

**Design.** close_window and close_windows_batch refuse Progman, WorkerW, Shell_TrayWnd, Shell_SecondaryTrayWnd and HostGuard windows with ProtectedWindow, unless force=true. A batch of more than 3 items requires confirm=true. A PostMessage failure reads GetLastPInvokeError and maps 5 to AccessDenied. Verified close with prompt detection follows in M2.5.

*Sources: r1:R55, r2:R10, r1:WC-12, r1:PAL-5, r2:WC-11*

### M1.8 Fix the UIA traversal filters behind a testable tree seam

**Priority:** P0 · **Effort:** M · **Kind:** fix

controlTypes:'Button,Edit' returns nothing with Success=true, size and enabled filters prune whole subtrees, and maxDepth is off by one. None of this can be unit-tested today.

**Design.** Add IUiaTree (FromHandle, GetChildren(node, ct), Read(node) returning a properties record) with a System.Windows.Automation adapter in DI and a FakeUiaTree for tests. UIElementService replaces its static calls at :65/:112/:153. A node that fails the type, size or enabled filter is not emitted, but its matching descendants are spliced into its parent; only IsOffscreen prunes a subtree. Each emitted node is counted once, which removes the double count at :296. maxDepth counts returned levels exactly. Unknown controlTypes names return InvalidControlType listing the valid names. Rect.Empty or non-finite bounds become Bounds=null. A sibling-advance failure sets Truncated instead of stopping silently. A timeout returns the partial tree. Re-enable the controlTypes guidance in SKILL.md once this ships. Builds on the M0.5 null-ControlType and per-node handling.

*Sources: r1:R42, r2:R19, r1:crit:R42, r1:UIA-1, r1:UIA-8, r1:UIA-10, r1:UIA-11, r1:UIA-G9, r1:SCR-missed-1, r2:UIA-1, r2:SCR-4, r2:UIA-6, r2:UIA-12, r2:UIA-13, r2:UIA-missed-unknown-controlTypes, r2:UIA-missed-sibling-advance-drop*

### M1.9 get_focused_element and get_element_at_point

**Priority:** P1 · **Effort:** S · **Kind:** extend

The cheapest checks before typing ('is the caret in the Email field?') and the cheapest way to map a screenshot point to a semantic element. Pulled forward because the observe-act-verify guidance relies on them.

**Design.** get_focused_element() reads AutomationElement.FocusedElement on the UIA worker under a timeout. It returns name, controlType, automationId, className, isPassword, value (at most 200 characters, masked for passwords), the owning top-level hwnd (GA_ROOT) and the process. get_element_at_point(x, y, ancestors=3) uses AutomationElement.FromPoint, whose physical coordinates match the click space, and resolves the top-level window with WindowFromPoint plus GA_ROOT. Both run under a timeout, because FromPoint can hang on unresponsive providers. They return an element ref once the M3.4 registry exists.

*Sources: r2:R24, r2:crit-prio:R24, r1:R46, r1:UIA-G6, r2:UIA-G6*

### M1.10 Input session safety: pause hotkey, cancellation and stuck-input recovery

**Priority:** P1 · **Effort:** M · **Kind:** improve

Give the user a guaranteed way to stop a runaway action. Make long input cancellable, and make sure an interrupted batch can never leave modifiers or buttons held down system-wide.

**Design.** A dedicated message-loop thread calls RegisterHotKey with a configurable chord (default Ctrl+Alt+Shift+Pause); a registration failure is reported in get_server_info. Pressing the hotkey sets Paused, cancels in-flight input through a linked CancellationTokenSource and sends compensating key-ups. While paused, every input tool returns PausedByUser. get_control_status reports the state. Resuming requires the hotkey again or a resume_input call confirmed by the user through elicitation, so the agent cannot resume itself, and send_keys refuses to inject the pause chord. type_text and send_keys become async and take the request CancellationToken. The server tracks the keys and buttons it has injected down. On a short SendInput count, an exception or a cancellation, it sends KEYUPs for every modifier that GetAsyncKeyState reports down. A release_all_input tool does the same on demand, and it also runs on IHostApplicationLifetime.ApplicationStopping and on stdin EOF. If the user is physically holding a modifier, input returns ModifiersHeld unless releaseHeldModifiers=true.

*Sources: r2:crit-missing:pause-hotkey, r1:R56, r2:R11, r1:R28, r1:crit:R28, r1:crit:R29, r1:INP-8, r1:INP-G8, r2:INP-G7, r2:INP-12, r2:INP-missed-partial-batch-stuck-modifiers*

### M1.11 Desktop-safe end-to-end tests against a self-owned fixture app

**Priority:** P1 · **Effort:** M · **Kind:** improve · **Branch:** `m1-fixture-e2e` (scaffold done)

After M0.1, nothing tests the real foreground, UIPI, SendInput, capture and UIA paths, where Windows behaviour differs from mocks. A fixture app that the suite owns makes these tests safe to run.

**Design.** tests/FixtureApp is a small WinForms or WPF exe with known AutomationIds: an edit box, a checkbox, a combo box, a button that opens a modal, a context menu, a 10k-item virtualized list, a WebView2 pane and a document that shows a save prompt on close. The E2E project is gated by the M0.1 opt-in. It drives the fixture through the in-process MCP client (M2.12; direct services until then), uses only targeted, guarded input against the fixture, and kills the fixture in Dispose. It covers EnsureForeground, the host guard, BlockedByModal, the capture origin and transform, DPI moves, and later find/act/wait. It runs in a separate CI job on windows-latest only after confirming that the runner has an interactive desktop; otherwise it runs on a self-hosted runner or in Windows Sandbox (.wsb) through workflow_dispatch. It never runs locally without the opt-in. Add the DesktopNoInput gate tier (deferred from M0.1) for capture-only tests against the fixture window.

**Scaffold (as built).** tests/FixtureApp is WamFixtureApp, a code-only WPF app (WPF rather than WinForms for real UIA virtualization in the 10k-item list and popup-window context menus). It has the controls above except the WebView2 pane, which would add a package and a runtime dependency to every build; add it with the Chromium work in M3. It writes a JSON-lines event log (a ready event with its pid, hwnd, DPI, window and client rects and every element's screen rect, then every change it receives) as ground truth independent of the server, takes an instance token for its titles, can start without activation or with the modal or the document open, sets its own DPI awareness, and ends itself when the test host exits or after a maximum lifetime. The gate moved into a shared project, WindowsAppManagerMcp.Tests.Infrastructure, and gained [DesktopNoInputFact]/[DesktopNoInputTheory]: their own opt-in WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1 (the full opt-in includes it), traits Category=Desktop plus Category=DesktopNoInput. tests/WindowsAppManagerMcp.E2ETests builds the real services through ServiceRegistration and puts every native wrapper behind a fail-closed DispatchProxy guard. That includes the process and monitor wrappers that ProcessService and MonitorService would otherwise create themselves: the harness registers them, and an ordinary test walks every resolved service for unguarded wrappers. WrapperCatalog classifies every wrapper member explicitly, and a member it does not list is refused. No-input tests can only read. Input tests can act only on the fixture process, with the foreground (keys) or the window under the cursor (mouse) checked before every SendInput batch, as a stand-in for M1.1/M1.5. No tier may start a process, and CDP calls are refused. First scenarios against current behaviour: find_windows, get_window_info, get_ui_elements (AutomationIds, control types, bounds against the fixture's own rects, realized items only), capture_window of the client area and the frame with swatch pixels at the expected offsets (no-input), and focus_window plus type_text into the edit box (input). .github/workflows/e2e.yml runs them only through workflow_dispatch and skips with a notice when the runner has no interactive desktop; test.yml's coverage check now merges every project's coverage file. Next: classify in WrapperCatalog the wrapper members that the other M1 branches add, then add EnsureForeground, host guard, BlockedByModal and capture-origin assertions as M1.1-M1.5 land, DPI moves, the in-process MCP client (M2.12), and an alpha==255 check once M1.2 encodes captures without alpha.

*Sources: r1:crit-missing:fixture-e2e, r2:crit-prio:R5, r2:R5, r1:R5, r1:SCR-G10, r2:MSP-G5*

### M1.12 Server-enforced target allow/deny policy

**Priority:** P2 · **Effort:** S · **Kind:** extend

The agent acts on untrusted screen and web content, and the model sets its own confirm flags. A server-side policy stops a prompt-injected agent from typing into password managers, credential prompts or other sensitive apps.

**Design.** WAM_DENY_PROCESSES defaults to common password managers, CredentialUIBroker.exe and consent.exe; an optional WAM_ALLOW_PROCESSES restricts targets further. The policy is enforced through a PID-to-image-name lookup in IInputTargetGuard, element_action, close/kill and capture (deny-listed windows are refused or blacked out). Violations return TargetDeniedByPolicy, and get_server_info reports the active policy. The model cannot override it with tool arguments.

*Sources: r1:crit-missing:target-policy*

## M2 Honest results and a stable MCP surface

Every tool either does what it reports or says precisely why not: typed parameters, real post-state, verified close, handoff-aware launch and faithful layouts. The MCP surface (contract tests, generated docs, annotations, stable SDK, curated toolsets, server instructions) is settled before M3-M5 add about 30 tools. Scroll and clipboard, the two primitives agents miss first, are pulled forward.

### M2.1 Honest error contract: isError, no null results, per-item batch errors

**Priority:** P1 · **Effort:** M · **Kind:** improve

Results with Success=false go out with isError=false, null results arrive as empty content, and one bad batch item aborts the whole batch with an NRE.

**Design.** Add IToolOutcome{Success, Error, ErrorCode} and a helper ToolResults.Json(dto) that returns CallToolResult{TextContentBlock(JSON), IsError=!Success}; use it in every tool. get_layout and get_foreground_window return {found:false, errorCode:'NotFound'} instead of null. Batch items are validated individually, with InvalidArgument per item and no NRE at WindowManagerTools.cs:386. Argument errors throw McpException listing the allowed values. The M0.3 call filter gains timing and errorCode logging.

*Sources: r1:R8, r2:R28, r1:MCP-14, r1:WC-7, r1:MCP-G10, r2:MSP-8*

### M2.2 Typed parameters and strict validation with real outcomes

**Priority:** P1 · **Effort:** S · **Kind:** fix

Unknown state, positioning, matchBy and monitorIndex values silently fall back to a different action that is then reported as success. Native failures carry no reason.

**Design.** Use C# enums with JsonStringEnumConverter so inputSchema lists the valid values: WindowStateRequest, MonitorPositioning, SnapPosition, LayoutMatchBy, KillMode, ImageFormat. This removes the fallbacks at WindowManagerTools.cs:121/358, WindowService.cs:274 and LayoutService.cs:293. Validate monitorIndex (InvalidMonitorIndex with the valid range); CalculateSnapBounds throws instead of falling back to the primary monitor at MonitorService.cs:113. Validate maxLength>0 and similar ranges. SetWindowState ignores ShowWindow's return value and reports success from the post-state (IsIconic/IsZoomed/IsWindowVisible polled for up to 300 ms). Mutating wrapper methods return (Ok, Win32Error) through Marshal.GetLastPInvokeError, mapping 5 to AccessDenied. Emit the unused MoveFailed, ResizeFailed, StateChangeFailed, FocusFailed and CloseFailed codes, and add NotResponding, Cloaked and AmbiguousWindow.

*Sources: r1:R34, r2:R28, r1:WC-7, r1:WC-8, r1:WC-9, r1:MCP-6, r1:MCP-9, r1:PAL-15, r2:WC-7, r2:MSP-8, r2:MSP-9, r2:INP-13, r2:PAL-10*

### M2.3 Correct, verified and hang-proof geometry operations

**Priority:** P1 · **Effort:** L · **Kind:** fix

Fixes several placement defects: maximize on the wrong monitor, moving or snapping a maximized window, minimized windows shrinking to 160x28, gaps from the invisible DWM frame, double DPI scaling, and calls that hang on 'Not Responding' windows. Every geometry result carries the observed state.

**Design.** PrepareForGeometryChange: if the window IsIconic or IsZoomed, call SetWindowPlacement(SW_SHOWNORMAL, rcNormalPosition = target rect in workspace coordinates), or SW_SHOWNOACTIVATE/SW_RESTORE at most twice for restore-to-maximized; measure only afterwards. MoveWindow uses SWP_NOSIZE and ResizeWindow SWP_NOMOVE, and neither ever activates; bringToFront is explicit. MoveWindowToMonitor takes the size from rcNormalPosition and clamps it to the target work area. It then does a two-phase move: SWP_NOSIZE onto the target monitor, settle, and re-read GetWindowRect after the app has applied WM_DPICHANGED, then set the final size. Never pre-scale by the DPI ratio. 'maximize' happens only after the move, and MonitorFromWindow==target is verified. Place by the visible frame: inflate the rect by the margins between GetWindowRect and DWMWA_EXTENDED_FRAME_BOUNDS. SnapMath.Calculate(workArea, position, frameInsets) is a pure function with edges at X+W*k/n so parts tile exactly; it sits behind INativeMonitorWrapper, and the test copy CalculateSnapBoundsInternal is deleted. Results report Bounds (visible frame), OuterBounds and State. Hang safety: IsHungAppWindow returns NotResponding; otherwise use SWP_ASYNCWINDOWPOS/ShowWindowAsync and poll up to timeoutMs. The tools become async and batches use Task.Delay. A new settleMs uses SettleAndRead, which waits for two equal extended-frame reads.

*Sources: r1:R33, r1:R35, r2:R27, r2:R26, r2:crit:R27, r1:R5, r1:WC-2, r1:WC-3, r1:WC-6, r1:WC-10, r1:PAL-11, r1:SCR-9, r1:WC-missed-1, r1:WC-missed-3, r2:WC-2, r2:WC-4, r2:WC-5, r2:WC-6, r2:WC-9, r2:WC-G6, r2:MSP-8, r2:WC-missed-minimized-shrink, r2:WC-missed-restore-to-maximized, r2:MSP-missed-maximized-not-restored, r2:MSP-missed-ShowWindow-return*

### M2.4 Window selectors, ambiguity errors and get_window_info

**Priority:** P1 · **Effort:** M · **Kind:** improve

Every tool needs a raw HWND, matching is substring-only, and layouts silently pick windows[0]. A shared selector resolves to exactly one window or returns an explicit error.

**Design.** WindowSelector/WindowQuery: Handle, TitleContains, TitleEquals, TitleRegex (100 ms timeout), ProcessName (exact and case-insensitive; contains is opt-in), ProcessId, ClassName, Foreground, Index, IncludeCloaked, IncludeUntitled and Limit. It is shared by FindWindows, LayoutService and every tool. WindowService.ResolveWindow returns one handle, NoMatch, or AmbiguousMatch with up to 5 candidates, and never windows[0]. Title matching uses the browser-aware normalizer from M0.7 (strips Unicode Cf characters such as U+200B and ' and N more pages'). An optional window object is added to move, resize, bounds, snap, state, focus, close, move_to_monitor, batch items, captures, get_ui_elements, input and browser tools; handle is kept for compatibility. A stale-handle guard returns HandleMismatch when the expected PID or title no longer matches. New get_window_info(window, detail). find_windows gains sort='zorder' and limit.

*Sources: r1:R36, r2:R29, r1:crit:R61, r1:WC-G2, r1:WC-G4, r1:BRW-G8, r1:SCR-G8, r2:WC-G2, r2:WC-G3, r2:WC-G5*

### M2.5 Verified close, owned-dialog discovery and close_application

**Priority:** P1 · **Effort:** M · **Kind:** improve

close_window cannot tell 'closed' from 'still open' from 'blocked by a Save changes? prompt'. There is no graceful app-level close.

**Design.** close_window and close_windows_batch gain waitMs (default 2000, max 30000). They post WM_CLOSE and poll IsWindow. If the window is still open, find the blocker: GW_ENABLEDPOPUP != h, owned #32770 windows (EnumWindows plus GW_OWNER), or a modal UIA dialog (WindowPattern.IsModal). Status is closed, stillOpen or promptShown, with prompt{handle, title, className, text, buttons}. New get_owned_windows(window). New close_application(window|processId|processName, timeoutMs) sends WM_CLOSE only to the Alt-Tab set, never to Default IME, MSCTFIME, GDI+ hook or hidden tray windows. Packaged apps are closed through the ApplicationFrameWindow that hosts their CoreWindow, with UIA WindowPattern.Close as the fallback. It never forces, and it reports remainingWindows and promptDetected.

*Sources: r1:R37, r2:R10, r2:crit:R9, r1:WC-G6, r1:PAL-G7, r2:WC-11, r2:WC-G10, r2:PAL-G6*

### M2.6 Handoff-aware launch-to-window resolution

**Priority:** P1 · **Effort:** M · **Kind:** fix

Launches that hand off to another process return no window after the full timeout. This covers browsers, calc to CalculatorApp, code, explorer and Firefox. Single-instance and tabbed apps (Win11 Notepad, Terminal, VS Code, Explorer tabs) open no new window at all. Return the window that actually appeared, whichever process owns it.

**Design.** IWindowWaitService.WaitForNewWindowAsync(baseline, ownerPredicate, timeoutMs (max 60000), stableMs=300, ct). The baseline records top-level HWNDs, their titles and the foreground window before Start. It polls every 100 ms, running cheap filters first. A candidate must survive stableMs, which skips splash screens, and then a bounded WaitForInputIdle runs. The owner predicate matches the launched PID or a descendant (Toolhelp walk with creation-time checks), the same image path (QueryFullProcessImageNameW), a handoff map (wt to WindowsTerminal, code to Code, calc to CalculatorApp via the CoreWindow mapping), or a caller hint expectWindow{processName?, titleContains?}. If no new window appears, an owner-matching existing window that became foreground or changed its title after Start returns reusedExistingWindow or openedInTab. A null Process.Start under ShellExecute counts as a handoff, not ProcessStartFailed. Process.Start runs in Task.Run with a timeout so a UAC prompt returns awaitingUserConsent instead of blocking the call. LaunchResult gains launchStatus (windowObserved|reusedExistingWindow|openedInTab|possibleHandoff|exitedWithoutWindow|timedOut|awaitingUserConsent), window (a full WindowInfoDto with the real owner PID) and launcherExited. Batch launches start every process first, then wait concurrently against one deadline, with a per-item ErrorCode. Thread.Sleep loops become Task.Delay(ct).

*Sources: r1:R48, r2:R37, r1:crit:R48, r1:crit-missing:single-instance-launch, r1:R52, r1:PAL-1, r1:BRW-3, r1:PAL-G1, r1:MCP-G3, r1:PAL-missed-6, r2:PAL-1, r2:PAL-12, r2:PAL-16, r2:BRW-10, r2:WC-12, r2:BRW-missed-detection-heuristics*

### M2.7 Launch path hygiene and protocol allowlist

**Priority:** P1 · **Effort:** S · **Kind:** fix

Bare names are resolved against the server's working directory first (binary planting), and file:// skips every check. Settings pages and Store apps cannot be launched at all. The ms-settings: and shell:AppsFolder allowlist is pulled forward from the larger AUMID item.

**Design.** Resolve bare names ourselves: App Paths (HKCU, then HKLM), then a manual PATH enumeration with PATHEXT that skips relative, empty and '.' entries. SearchPathW cannot exclude the current directory. Pass the absolute path to ShellExecute, and default WorkingDirectory to the executable's folder, never the server's working directory. Add a regression test that plants a same-named exe in the working directory. Route file:// through ValidateFilePath. Allow ms-settings: and shell:AppsFolder\<AUMID>, plus schemes from WAM_ALLOWED_PROTOCOLS. Keep javascript:, vbscript:, data: and mshta: blocked.

*Sources: r1:R49, r1:crit:R49, r2:R40, r2:crit-prio:R40, r1:PAL-missed-2, r1:PAL-missed-7, r2:PAL-G3*

### M2.8 Faithful layout apply with one-to-one matching and per-placement results

**Priority:** P1 · **Effort:** M · **Kind:** fix

Today N Chrome windows collapse onto one placement, an empty matcher matches every window, apply reports success when nothing matched, and a freshly launched untitled window causes an NRE. Recording a few identity fields at capture time is cheap and fixes the confirmed high-severity PAL-4.

**Design.** Apply takes one snapshot using the user-facing predicate and assigns windows one to one: a HashSet tracks assigned windows, and each placement takes the best unassigned candidate scored on title fragment, className, exe path and distance from the saved rect. Captured matchers compare processName exactly, case-insensitively. An empty matcher under title_only is notApplicable, not match-all, and unknown matchBy values are rejected. Capture records exact processName, className, a title pattern, an ordinal and WindowState as additive optional fields, the cheap identity subset of schema v2. Each placement runs in try/catch, and a null launched window becomes a per-placement error instead of the NRE at LayoutService.cs:227/254. Per-placement results are {status: applied|notFound|notApplicable|failed, window, errorCode}, and the overall result is Success=false or status 'partial'. Apply uses M2.3 geometry and M1.3 activation, and reports the focus result.

*Sources: r1:R57, r1:R58, r1:crit-prio:R58, r2:R38, r1:PAL-4, r1:PAL-6, r1:PAL-12, r1:PAL-13, r1:WC-missed-2, r2:PAL-4, r2:PAL-6, r2:PAL-8, r2:PAL-9, r2:MSP-10, r2:MSP-missed-layout-same-window*

### M2.9 Capture health: never drop pixels, typed capture errors and warnings

**Priority:** P1 · **Effort:** M · **Kind:** fix

capture_with_elements drops the image on any UIA failure. Minimized, cloaked, hung, blank or off-screen captures all report Success=true. GDI misuse costs extra copies and can corrupt pixels.

**Design.** capture_with_elements validates the handle with IsWindow only, so untitled popups work. It captures pixels independently of the UIA walk and, if the walk fails or times out, still returns the image with ElementsError and Truncated. Add a ScreenshotErrorCode enum (WindowMinimized, WindowHidden, WindowCloaked, RegionOffScreen, CaptureFailed, EncodeFailed), plus Warnings[] and CaptureMethod. Pre-check IsIconic, IsWindowVisible, DWMWA_CLOAKED and IsHungAppWindow; a hung window gets a screen-DC BitBlt with a warning. Opt-in restoreIfMinimized: SW_SHOWNOACTIVATE, wait for two equal frames rather than a fixed 200 ms, capture, then re-minimize with SW_SHOWMINNOACTIVE, because SW_MINIMIZE activates another window. Add a blank-image warning from pixel sampling. Crop frame captures to DWMWA_EXTENDED_FRAME_BOUNDS. Intersect regions with the virtual screen using MONITOR_DEFAULTTONULL. Detect DPI-unaware targets and warn or scale. Call SelectObject(old) before GetDIBits, or use CreateDIBSection to drop two copies. Apply a 64 MP budget. Add WindowState to UIElementResult.

*Sources: r1:R16, r1:R20, r2:R15, r2:R14, r1:crit:R16, r1:UIA-7, r1:UIA-6, r1:SCR-6, r1:UIA-5, r1:SCR-2, r1:SCR-4, r1:UIA-9, r1:SCR-7, r1:SCR-9, r1:SCR-10, r1:SCR-G4, r1:SCR-missed-2, r1:SCR-missed-3, r2:SCR-6, r2:SCR-7, r2:SCR-11, r2:SCR-12, r2:SCR-G6*

### M2.10 Tool annotations, titles and human confirmation through elicitation

**Priority:** P1 · **Effort:** S · **Kind:** improve

No tool sets ReadOnly, Destructive, Idempotent, OpenWorld or Title, so clients must treat find_windows like kill_process. Irreversible actions rely on a confirm flag that the model sets for itself.

**Design.** Every [McpServerTool] gets a Title and hints. ReadOnly: find/get/list/wait tools and browser reads. Captures: ReadOnly=false because they can write files, but non-destructive. Geometry tools: Destructive=false, Idempotent=true. Destructive=true: close*, kill_process, delete_layout, save_layout (it overwrites), send_keys, type_text, the click family and element_action. OpenWorld: launch tools and browser navigation. kill_process, close_windows_batch and close_application take McpServer and call ElicitAsync (present in 0.5.0-preview.1) with names, PIDs and titles when the client advertises elicitation, falling back to confirm otherwise. This lands after the M0.5 concurrency fixes, because ReadOnly hints make clients parallelize more. The contract test enforces annotations on every tool.

*Sources: r1:R9, r2:R50, r1:R14, r1:MCP-G1, r1:WC-G7, r2:MSP-G4*

### M2.11 Server instructions, get_server_info and an observe-act-verify skill

**Priority:** P1 · **Effort:** S · **Kind:** improve

Clients without the plugin get no guidance at all. Nothing reports the build, integrity level, DPI mode, host windows or pause state. SKILL.md needs a rewrite around verified actions.

**Design.** Set options.ServerInstructions (15-20 lines). Coordinates are physical virtual-screen pixels and may be negative. Map image to screen with imageToScreen or captureId, never with ScaleFactor. Always pass a window target to input tools. Prefer element tools over coordinates. Use waits, not sleeps. Explain the error codes (TargetIsAgentHost, TargetElevated, BlockedByModal, PausedByUser, TargetDeniedByPolicy) and state that destructive chords need a verified target. Add a read-only get_server_info returning version and gitSha, sdkVersion, integrity and elevation, effective dpiAwareness (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext())), inputDesktopAvailable, sessionLocked, foregroundLockTimeout, monitors with DPI, host windows, pause and hotkey state, deny policy, layouts and audit paths, and enabled toolsets. DPI: declare PerMonitorV2 in app.manifest only and drop the API call, which always fails with ACCESS_DENIED once the manifest sets awareness; log the effective awareness. Rewrite SKILL.md around locate, activate/verify, act, wait, verify, with browser and failure-mode sections. This item does not depend on generated docs.

*Sources: r1:R11, r1:R13, r2:R51, r2:crit:R51, r2:crit:R26, r1:MCP-G9, r1:MCP-G5, r1:WC-14, r2:MSP-G7, r2:MSP-G8, r2:WC-14*

### M2.12 In-process MCP contract tests, shared DI and generated docs

**Priority:** P1 · **Effort:** M · **Kind:** improve

Test the server the way an agent sees it, over MCP. Snapshot the tool surface, enforce payload budgets and catch stale binaries. Generate the tool tables in the docs so they stop drifting. This is the safety net for M2's enum and schema changes and for the SDK upgrade.

**Design.** Add services.AddWindowsAppManager(options) in ServiceCollectionExtensions, used by Program.cs and by the tests; this fixes DependencyInjectionTests missing seven types. Contract tests host AddMcpServer().WithStreamServerTransport over in-memory pipes with mocked services and connect an McpClient. They (a) snapshot tools/list (name, title, annotations, inputSchema) into tools.snapshot.json, (b) call every tool and assert non-empty content, isError==!Success and an image block first for captures, (c) run reflection guards for nint members and annotations, (d) make about 50 parallel read calls, and (e) measure payload sizes and assert per-tool token budgets. A CI guard starts the committed bin exe, calls tools/list and diffs the result against the snapshot, which catches the stale binaries a version check cannot. tools/DocGen, or an UPDATE_SNAPSHOTS=1 test run, rewrites marker regions in README.md, docs/TOOLS.md and SKILL.md, and CI fails on git diff --exit-code.

*Sources: r1:R3, r1:R4, r2:R4, r2:crit:R51, r1:crit:R2, r1:crit:R12, r1:MCP-G7, r1:MCP-2, r1:MCP-14, r1:MCP-10, r2:MSP-G5, r2:MSP-7, r2:PAL-17, r2:BRW-15*

### M2.13 Upgrade the MCP SDK to stable; cancellation and progress everywhere

**Priority:** P1 · **Effort:** M · **Kind:** improve

Move off ModelContextProtocol 0.5.0-preview.1 right after the contract tests exist, before M3-M5 add about 30 tools. Porting 42 tools now is cheaper than porting about 75 later.

**Design.** Upgrade to the current stable ModelContextProtocol line, with the M2.12 contract tests as the safety net. Expected breaks are isolated in helpers: ImageContentBlock bytes in ToolImages, IList collections, and StructuredContent as JsonElement?. Every potentially long-running tool becomes async with a CancellationToken linked to its own timeout. Replace the unlinked CancellationTokenSources at UIElementTools.cs:73 and ScreenshotTools.cs:190, and turn Thread.Sleep into Task.Delay(ct). Add IProgress<ProgressNotificationValue> to apply_layout, the batch launch, deep UIA walks and the wait tools.

*Sources: r1:R14, r1:crit-prio:R14, r2:R52, r1:MCP-G10, r1:INP-G8, r1:UIA-3, r2:MSP-G8*

### M2.14 Toolsets with a curated default and a read-only mode

**Priority:** P1 · **Effort:** S · **Kind:** extend

This roadmap grows the surface from 42 to about 70-75 tools, and every schema sits in context on every turn. A curated core of about 20-25 tools by default, with everything else opt-in, keeps tool selection reliable.

**Design.** Program.cs reads WAM_TOOLSETS / WAM_EXCLUDE_TOOLS and WAM_READ_ONLY=1 before the WithTools registrations. The default core covers desktop state, find/act/wait, windows, capture, guarded input, launch and server info. Opt-in groups: ocr, visual, browser-actions, layouts-v2, workspace and process. Read-only mode registers only ReadOnly=true methods through McpServerTool.Create. Disabled groups are also blocked inside run_steps. Document plugin.json env profiles, for example an 'observer' profile. This must land before M3-M5 add tools.

*Sources: r1:R10, r1:crit-prio:R10, r2:R53, r1:MCP-G6, r2:MSP-G9*

### M2.15 scroll primitive (pulled forward)

**Priority:** P1 · **Effort:** S · **Kind:** extend

Most list items in Settings, Explorer or Outlook cannot be reached without scrolling. The wheel constants already exist, and scroll needs only the M1 guard, not the M4 keyboard rewrite.

**Design.** scroll(window?, x?, y? (default: client centre), deltaY in notches, deltaX, space?) sends one SendInput batch: MOVE|ABSOLUTE|VIRTUALDESK to the normalized point ((x - SM_XVIRTUALSCREEN) * 65535 / (SM_CXVIRTUALSCREEN - 1)), then MOUSEEVENTF_WHEEL/HWHEEL with mouseData = unchecked((uint)(notches*120)). It goes through the M1 guard, host guard and policy, and returns the window under the cursor, since the wheel follows the 'scroll inactive windows' setting. Element-level scrolling comes with element_action (M3.6).

*Sources: r2:R34, r2:crit-prio:R34, r1:R29, r1:INP-G3, r2:INP-G2, r2:UIA-G8*

### M2.16 Clipboard get/set with a privacy guard (pulled forward)

**Priority:** P1 · **Effort:** S · **Kind:** extend

Copy-then-read is the cheapest way to extract text from apps with poor UIA (terminals, Electron, canvases, RDP). Reading the user's clipboard could leak passwords, so reads are guarded.

**Design.** Run on a dedicated STA thread, because MCP calls arrive on MTA threads. Use System.Windows.Clipboard, or OpenClipboard with CF_UNICODETEXT, retrying on CLIPBRD_E_CANT_OPEN. set_clipboard(text) also sets ExcludeClipboardContentFromMonitorProcessing so the text stays out of Win+V history. get_clipboard(format=text|html|files, maxLength=20000) refuses or masks content that carries ExcludeClipboardContentFromMonitorProcessing, CanIncludeInClipboardHistory=0 or 'Clipboard Viewer Ignore'. By default it returns content only if GetClipboardSequenceNumber changed after a server-initiated copy in this session, and otherwise reports clipboardNotFromAgent. Reading the user's own clipboard is an opt-in through server config or elicitation, not a flag the model sets. Paste-mode typing comes in M4.4.

*Sources: r1:R31, r1:crit-prio:R31, r1:crit:R31, r2:R35, r2:crit-prio:R35, r2:crit:R35, r1:INP-G5, r2:INP-G5*

### M2.17 Audit log of injected input and destructive actions

**Priority:** P2 · **Effort:** S · **Kind:** extend

When an action does damage, neither the user nor the agent can reconstruct what was sent where. A cheap append-only log builds trust in a server that types into the desktop.

**Design.** The call filter writes JSONL to %LOCALAPPDATA%/WindowsAppManagerMcp/audit/<date>.jsonl, one line per input, close, kill, launch or layout action: {ts, tool, target handle/title/process, receivingWindow, outcome, errorCode, durationMs}. It never logs type_text or set_clipboard content, only its length, and never logs secrets. Retention rolls over 7 days, and get_server_info reports the path.

*Sources: r2:crit-missing:audit-log, r1:R8*

## M3 Semantic find-act-wait

The biggest lever. Move from blind coordinates to selector-addressed, pattern-first UI Automation, so 'click Save in app X' becomes 1-2 calls that can each be verified. Elements are found by name, automationId and type, including in menus, popups, dialogs, Chromium/Electron apps and virtualized lists. Agents act through UIA patterns with verified post-state, read text and tables directly, and wait server-side instead of sleeping.

### M3.1 Bounded, cached UIA walks with partial results and compact output

**Priority:** P1 · **Effort:** M · **Kind:** improve

Each element currently costs 8-10 uncached cross-process calls, a timeout throws away everything, and payloads are unbounded. Make reads fast, bounded and useful even when incomplete.

**Design.** Build one CacheRequest covering Name, ControlType, AutomationId, ClassName, BoundingRectangle, IsEnabled, IsOffscreen, IsKeyboardFocusable, RuntimeId and the Is*PatternAvailable flags, activated around the walk. Navigate with ControlViewWalker and read cached values. Walk breadth-first with maxElements (default 300) and a deadline, returning Success=true with Truncated, TruncationReason and SkippedElements. Pre-check IsHungAppWindow and SendMessageTimeout(WM_NULL, SMTO_ABORTIFHUNG, 500) to return WindowNotResponding. Reads run on one dedicated UIA worker with a bounded queue; pattern calls that can block never run there (see M3.6). format=flat|tree|text, where text is one line per element, e.g. `w1e12 Button 'Save' aid=SaveBtn @812,44 80x24 [disabled]`. interactableOnly = IsEnabled && (IsKeyboardFocusable || any action pattern).

*Sources: r2:R20, r1:R47, r1:R12, r1:crit:R44, r1:UIA-4, r1:UIA-G7, r1:UIA-G8, r2:UIA-2, r2:UIA-4, r2:UIA-5, r2:UIA-7, r2:UIA-G7*

### M3.2 UIA3 (FlaUI) backend behind a widened seam, before refs, actions and waits

**Priority:** P1 · **Effort:** L · **Kind:** improve

The managed client has no per-call timeouts, cannot read newer control-type ids and has no LegacyIAccessible. Building refs, patterns, events and waits on it and porting them later doubles the work, so swap the engine behind the seam first. This is a time-boxed spike.

**Design.** Widen IUiaTree to cover patterns, events and cache requests, not just traversal. Implement it on FlaUI.UIA3 or a prebuilt Interop.UIAutomationClient, not a COMReference, which dotnet build cannot resolve. Set IUIAutomation2 ConnectionTimeout to about 1000 ms and TransactionTimeout to about 5000 ms, which gives hard deadlines for hung providers and for an Invoke blocked by its own modal. Use BuildUpdatedCache(TreeScope_Subtree), read raw control-type ids (50039/50040) and add LegacyIAccessible DoDefaultAction. A config switch keeps the managed UIA2 adapter as the fallback; tool contracts do not change, and the M1.11 fixture suite runs against both adapters. If the spike fails, M3.4-M3.8 proceed on the managed adapter behind the same seam.

*Sources: r1:R47, r1:crit-prio:R47, r2:R25, r1:crit:R42, r1:UIA-G9, r2:UIA-G9, r1:UIA-2, r1:UIA-3*

### M3.3 Chromium, Electron and WebView2 accessibility activation

**Priority:** P1 · **Effort:** M · **Kind:** extend

VS Code, Slack, Teams, Discord, the new Outlook and WebView2 panes build their web accessibility tree lazily. The first query sees an empty Chrome_RenderWidgetHostHWND, and the agent wrongly concludes the control is absent.

**Design.** In the UIA adapter, detect Chrome_WidgetWin_*, Chrome_RenderWidgetHostHWND and WebView2 host classes. On the first query of such a window, touch the Document element, then poll for up to about 1.5 s in 200-500 ms steps until the Document has children or its child count stops changing. Report AccessibilityWarmup or accessibilityNotReady instead of returning a silently thin tree. launch_application gains forceAccessibility, which appends --force-renderer-accessibility for Chromium and Electron executables. An optional, opt-in enable_screen_reader_flag sets SPI_SETSCREENREADER; it is documented as a global side effect and restored on shutdown. Element results carry frameworkId (Chrome, XAML, WPF, Win32, WinForm).

*Sources: r1:crit-missing:chromium-warmup, r2:crit-missing:web-accessibility, r1:R62, r1:R63, r2:R48, r2:BRW-missed-renderer-accessibility, r1:BRW-missed-2*

### M3.4 Element registry with stable refs and find_ui_elements

**Priority:** P1 · **Effort:** M · **Kind:** extend

Search on the server by name, automationId, controlType and className, and get back refs that later calls can use. Stale or ambiguous refs return explicit errors, and a ref never silently resolves to a different element.

**Design.** IElementRegistry singleton. Refs are globally unique and never reused within a session (e.g. w<hwndSeq>e<n>), and the same element (same RuntimeId) keeps its ref across snapshots. Entries hold {element, RuntimeId, hwnd, selector, fingerprint (AutomationId, Name, ControlType, ClassName, index path), generation, lastUsed}, LRU of about 5000, dropped once !IsWindow(hwnd). Resolve order: a live read such as Current.ProcessId (GetRuntimeId is memoized and cannot detect staleness), then FindFirst by RuntimeIdProperty under the window, then re-run the stored selector and verify the fingerprint, then ElementStale ('call find_ui_elements again'). get_ui_elements and capture_with_elements emit refs. find_ui_elements(window, name?, nameMatch=exact|contains|regex, automationId?, controlType? (list), className?, withinRef?, scope (M3.5), visibleOnly=true, enabledOnly=false, maxResults=20, detail=minimal|standard|full) runs provider-side PropertyConditions, so it is not limited by maxDepth. It returns MatchCount, Truncated, refs, ClickPoint (TryGetClickablePoint, falling back to the bounds centre) and Patterns[]. detail=standard adds Value (at most 200 characters, masked when IsPassword), ToggleState, ExpandCollapseState, IsSelected and HasKeyboardFocus. detail=full adds FrameworkId, ProcessId, NativeWindowHandle, HelpText, AccessKey and LocalizedControlType. Under the managed client, unknown control types are matched client-side by class or LocalizedControlType.

*Sources: r1:R43, r2:R21, r1:crit:R43, r1:R46, r2:R24, r1:UIA-G2, r1:UIA-G3, r1:UIA-G5, r1:MCP-G2, r2:UIA-G1, r2:UIA-G2, r2:UIA-G5, r2:MSP-G1*

### M3.5 Popup-, menu- and dialog-aware element search

**Priority:** P1 · **Effort:** M · **Kind:** extend

Context menus (#32768), ComboLBox dropdowns, WPF Popups, WinUI flyouts and WebView2 popups are separate top-level windows outside the target's UIA subtree. An agent can open a menu today but cannot find 'Delete' in it.

**Design.** Add scope=window|windowAndPopups|desktop to find_ui_elements, wait_for_element and get_desktop_state; the default is windowAndPopups when the target is in the foreground. windowAndPopups searches under one deadline: the target window; every visible top-level window of the target's PID and thread, including owned, tool and untitled ones; GW_ENABLEDPOPUP; desktop children matched provider-side by PropertyCondition(ProcessIdProperty, pid); and popups from descendant processes such as msedgewebview2.exe. New get_active_popups(window?) returns open menus, dropdowns and dialogs with refs. element_action expand, and any invoke that opens a popup, returns openedPopup{ref, handle} after a short wait. respond_to_dialog(window, button) reads the dialog text and buttons (UIA, or #32770 children) and invokes the chosen button, addressed by AutomationId or ClassName where possible so it works on localized Windows. A menu_path action ('File>Save As') uses ExpandCollapse plus Invoke.

*Sources: r1:crit-missing:popup-scope, r2:crit-missing:popup-search, r2:WC-G10, r1:R19, r1:R37*

### M3.6 element_action: pattern-first actions with verified post-state

**Priority:** P1 · **Effort:** L · **Kind:** extend

Act on elements through UIA patterns, which work on background and occluded windows without fighting the foreground lock, and fall back to a guarded mouse click only when needed. Each call returns the before and after state so the agent can verify in the same call.

**Design.** One tool: element_action(ref | window+selector, action, value?, fallback=mouse|none, timeoutMs=5000). Actions: invoke, click, double_click, right_click, focus, set_value, append_text, toggle, select, add_to_selection, remove_from_selection, expand, collapse, scroll_into_view, scroll, set_range_value, realize, window_close, window_minimize, window_maximize. Pre-validate IsEnabled and IsOffscreen and return actionable errors ('offscreen: use scroll_into_view'). Pattern paths: Invoke; Toggle looped up to 3 times until the target state; ValuePattern.SetValue after IsReadOnly and IsPassword checks, then read back; ExpandCollapse; SelectionItem; ScrollItem, or ScrollPattern on the nearest scrollable ancestor (returns percent and atEnd); RangeValue; WindowPattern; LegacyIAccessible DoDefaultAction on UIA3. Invoke, Toggle and ExpandCollapse run on a throwaway thread outside the shared UIA worker, because a Win32 Invoke blocks until the modal it opened closes; in-flight threads are capped. On timeout the result is invokedPendingModal with a window-snapshot diff, so the agent can read and dismiss the modal. The set_value keyboard fallback (focus, select-all, type) runs only when FocusedElement equals the target and the M1 foreground guard passes, otherwise FocusNotOnElement; set_value takes mode=value|type|paste. The mouse fallback is a guarded click at ClickPoint after EnsureForeground. The result reports the method used, before and after state (value, toggle, expand, selected, name, enabled), outcomeVerified and openedPopup. Every call passes through the policy and the audit log.

*Sources: r1:R44, r2:R22, r1:crit:R44, r1:UIA-G1, r1:INP-G1, r1:MCP-G2, r2:UIA-G3, r2:INP-G1, r2:MSP-G1, r2:UIA-G8*

### M3.7 wait_for_window

**Priority:** P1 · **Effort:** S · **Kind:** extend

Launch-then-act and close-then-verify are the most common races. One server-side wait with a clear timeout replaces guessed sleeps and repeated find_windows calls.

**Design.** Async wait_for_window(selector, condition=exists|gone|foreground|visible|title_changed|input_idle, newOnly/excludeHandles, timeoutMs=10000 (max 60000), stableMs=300, ct, progress). It runs on the M2.6 IWindowWaitService and the M2.4 resolver, so it finds untitled dialogs. It returns {satisfied, timedOut, elapsedMs, window, candidates when ambiguous}. input_idle is best effort and documented as meaningless for console and UWP launchers. It polls for now and switches to event-driven wake-ups once M7.2 exists.

*Sources: r1:R50, r2:R30, r1:WC-G1, r1:PAL-G1, r1:MCP-G3, r2:WC-G1, r2:PAL-G1, r2:MSP-G2*

### M3.8 wait_for_element

**Priority:** P1 · **Effort:** M · **Kind:** extend

Most flakiness in desktop automation is timing: controls not rendered yet, a dialog still opening, a button that enables only after validation. One blocking call that returns the final state replaces polling full tree walks.

**Design.** Async wait_for_element(window selector, element selector | ref, scope, condition=exists|gone|enabled|visible|focused|name_equals|value_equals|toggle_state, expected?, timeoutMs=10000 (max 60000), ct, progress). It waits for the window first, which covers 'after launch'. It runs the M3.4 selector under the CacheRequest with backoff from 100 to 500 ms, optionally wakes early on StructureChanged and PropertyChanged handlers (removed in a finally block), and applies the M3.3 warm-up. It returns {satisfied, timedOut (distinct from not found), element with ref, elapsedMs, polls, lastObservedState}.

*Sources: r1:R45, r2:R23, r1:UIA-G4, r1:MCP-G3, r2:UIA-G4, r2:MSP-G2*

### M3.9 Read element text and tables (TextPattern, ValuePattern, Grid/Table)

**Priority:** P1 · **Effort:** M · **Kind:** extend

Reading the Outlook message list or reading pane, Explorer's details view, Task Manager or a Notepad or Word document currently means dumping trees (Name only) or taking screenshots.

**Design.** read_element_text(ref|selector, offset=0, maxLength=20000) reads ValuePattern.Value in full, then TextPattern.DocumentRange (Clone, MoveEndpointByUnit to the offset, GetText(maxLength)) for paging, then Name. It returns {text, totalLength, truncated, nextOffset, source} and redacts IsPassword fields. read_table(ref, startRow=0, maxRows=50, maxColumns=20) reads headers through TablePattern.GetColumnHeaders and cells through GridPattern.GetItem, or a DataItem/Row walk under one CacheRequest. It returns {headers, rows, totalRows, truncated}.

*Sources: r2:crit-missing:text-table-reading*

### M3.10 Realize virtualized list and tree items

**Priority:** P2 · **Effort:** M · **Kind:** extend

Outlook, Explorer, Settings and long WPF/WinUI lists virtualize, so off-screen items do not exist in the UIA tree and find reports a miss for an item that is really there.

**Design.** find_ui_elements gains searchVirtualized=true. When the scope container supports ItemContainerPattern, it loops FindItemByProperty(startAfter, Name|AutomationId, value), then calls VirtualizedItemPattern.Realize() and ScrollItemPattern.ScrollIntoView, and returns a ref. element_action gains a 'realize' action. The fallback scrolls page by page with ScrollPattern and re-queries, bounded by maxScrolls and the deadline. The fixture app's 10k-item list is the test.

*Sources: r1:crit-missing:virtualized-lists, r2:crit-missing:virtualized-items, r1:UIA-G8*

### M3.11 Capture registry, image-space clicks and Set-of-Marks tied to element refs

**Priority:** P1 · **Effort:** M · **Kind:** extend

The server, not the model, should convert image coordinates to screen coordinates, and it should reject stale captures. Numbered marks are the same refs element_action accepts, so the agent can pick a target visually and then act on it semantically.

**Design.** ICaptureRegistry (LRU of 32 entries, 10-minute TTL) stores CaptureRecord{id, ts, hwnd?, origin, scale, windowRectAtCapture, elements?}. Mouse tools accept space=screen|window|client|image: image needs captureId, and window and client resolve through GetWindowRect or ClientToScreen. If the window has moved more than 2 px since the capture, the call is refused with StaleCapture. Results echo both the requested point and the resolved screen point. capture_with_elements draws overlays after resizing, so marks survive downscaling. Each mark is a filled, contrast-colored tag showing the element's registry ref. Defaults: labels=true, maxElements=80, and actionableOnly when controlTypes is empty. drawOverlays=false returns a clean image with the marks only in the JSON, and the legend has one compact line per mark (`[w1e17] Button 'Save' #btnSave center=(x,y)`). There is no click_mark tool; element_action(ref) acts on a mark directly. capture_window accepts ref plus padding to crop to one element, and take_screenshot with monitorIndex=-1 captures the whole virtual screen.

*Sources: r1:R17, r1:R18, r2:R13, r2:R14, r2:R15, r1:SCR-G1, r1:SCR-G2, r1:SCR-G8, r1:INP-G2, r2:SCR-G2, r2:SCR-G3, r2:SCR-G7, r2:UIA-G10, r2:INP-G8, r2:SCR-3, r2:SCR-5, r2:SCR-missed-capture-timing-skew, r2:SCR-missed-faint-overlays*

### M3.12 get_desktop_state: one-call observation

**Priority:** P1 · **Effort:** M · **Kind:** extend

Getting oriented takes three or four calls today, two of them heavy. One compact observation with actionable refs becomes the recommended first call.

**Design.** Read-only get_desktop_state(window? (default: foreground), include=[windows, focus, elements, popups] plus optional screenshot, maxElements=80, interactableOnly=true, screenshotMaxLongEdge=1280). It returns: the foreground window {handle, title, process}; the focused element; the cursor position; held modifiers and Caps Lock; the pause state; the top N user-facing windows in z-order as compact rows [handle, title, process, state, monitor]; the target's open popups; the target's interactive elements in text format with refs; and, optionally, a Set-of-Marks image block. Action tools gain observe=none|state|screenshot, which returns this state in the same response. SKILL.md makes it the first call.

*Sources: r1:R19, r1:R37, r2:R53, r1:INP-G6, r1:MCP-G4, r2:MSP-G9*

### M3.13 Launch-and-arrange in one call

**Priority:** P1 · **Effort:** M · **Kind:** extend

Starting an app and placing it takes four calls today, and the agent races the app's own restore of its saved bounds. One call should launch, place, focus and confirm.

**Design.** launch_application and batch items gain placement {snap?, monitor?, bounds?, state?, focus?} and reuseExisting. Once M2.6 has resolved the window, apply M2.3 geometry and M1.3 activation. Re-check after about 500 ms and re-apply once if the app restored its saved bounds (Chrome, VS Code). Return {finalBounds, finalState, focused, launchStatus}.

*Sources: r1:R51, r2:R37, r1:PAL-G2, r2:PAL-G2*

### M3.14 Common file dialog helper (localization-safe)

**Priority:** P2 · **Effort:** M · **Kind:** extend

Open and Save As dialogs appear in almost every end-to-end task, and composing them from element actions costs 4-6 error-prone calls.

**Design.** file_dialog(window? (default: the foreground dialog, found through the popup scope), path, action=open|save, overwrite=false, timeoutMs). Locate the #32770/IFileDialog window and set the filename edit (AutomationId 1148, or ComboBoxEx32 > Edit) through ValuePattern with the full path. Invoke the default button by AutomationId ('1'/IDOK), not by caption. Handle the 'already exists, replace?' prompt according to overwrite, using respond_to_dialog, and wait for the dialog to be gone. Return {completed, finalPath, promptHandled}. Validate the path the same way as screenshot outputPath. No English captions anywhere.

*Sources: r1:crit-missing:file-dialog, r2:non-goal:english-only-dialogs*

## M4 Input vocabulary

Keyboard and mouse produce exactly what was asked, on any keyboard layout and button configuration. The tools cover the chords, drags, multi-clicks, modifier-clicks and long text that real tasks need, and nothing can stay held down after a call returns.

### M4.1 Keyboard engine: layout-aware mapping and a full key grammar

**Priority:** P1 · **Effort:** M · **Kind:** fix

Ctrl or Alt plus punctuation never fires, 'Hello' types 'hello', AZERTY digits come out wrong, '100%{ENTER}' sends Alt+Enter, and scan codes are always 0. There are also no escapes, groups, Win modifier or numpad keys.

**Design.** A pure KeySpecParser produces InputEvents and is unit-tested through the input seam with a mocked layout. Grammar: escapes {+} {^} {%} {~} {(} {)} {{} {}}; groups such as +(abc); repeats such as {LEFT 5} (capped at 100); ~ as Enter; # as the Win modifier; {KEY down}/{KEY up} within one call, with key-ups appended at the end of the batch; NUMPAD0-9, ADD, SUBTRACT, MULTIPLY, DIVIDE, F13-F24, VK_OEM_* and media/browser keys. Syntax errors return InvalidKeySpecification. send_keys maps every character, modified or not, with VkKeyScanExW on the foreground thread's layout to get the VK plus any Shift/AltGr it needs. wScan comes from MapVirtualKeyExW(MAPVK_VK_TO_VSC_EX), with EXTENDEDKEY set for 0xE0 codes. KEYEVENTF_UNICODE is used only for characters not on the layout, so 'j', '/', Office KeyTips and RDP/VM clients work. Caps Lock is taken into account. type_text stays Unicode by default and normalizes \r\n. If the text contains \n or \t, an explicit controlChars=unicode|keys|shiftEnter is required; there is no default newline-to-Enter, which would submit chat messages or move Excel cells. send_keys gains delayMs pacing between top-level tokens. A partial SendInput insert triggers the M1.10 compensating key-ups and returns an error with progress.

*Sources: r1:R27, r2:R32, r1:crit:R27, r2:crit:R32, r1:INP-2, r1:INP-3, r1:INP-4, r1:INP-9, r1:INP-10, r1:INP-8, r1:INP-missed-1, r2:INP-4, r2:INP-5, r2:INP-6, r2:INP-7, r2:INP-8, r2:INP-9, r2:INP-G4*

### M4.2 Mouse correctness: atomic absolute clicks, swapped buttons, real double-click

**Priority:** P1 · **Effort:** S · **Kind:** fix

Today a click is SetCursorPos, a 10 ms sleep, then relative button events, so it can land wherever the cursor drifted. Swapped buttons invert click and right_click, and a double-click can exceed the double-click time.

**Design.** A shared InputBatchBuilder sends one SendInput array per operation: MOVE|ABSOLUTE|VIRTUALDESK to the normalized point, then the button events. Primary and secondary map to physical buttons through GetSystemMetrics(SM_SWAPBUTTON). Double and triple clicks go in one batch with gaps under GetDoubleClickTime()-50. After sending, read GetCursorPos into ActualX/Y and return CursorClamped on a mismatch.

*Sources: r1:R29, r2:R33, r1:INP-6, r1:INP-11, r2:INP-10, r2:INP-11*

### M4.3 mouse_click, drag and press_keys, with no cross-call holds

**Priority:** P1 · **Effort:** M · **Kind:** extend

Adds drag-and-drop, middle and X buttons, triple clicks, modifier-clicks and structured chords such as 'ctrl+shift+t', which are more reliable for LLMs than SendKeys punctuation. Press-and-hold exists only within a single call.

**Design.** mouse_click(x,y | captureId | ref, button=primary|secondary|middle|x1|x2, clickCount 1-3, modifiers [ctrl, shift, alt, win], window?) sends modifiers down, the click and modifiers up atomically; click, right_click and double_click become thin aliases (consolidated in M6.1). drag(from, to, button, steps=10, stepDelayMs, holdBeforeMoveMs=100, modifiers?, window?) interpolates absolute moves beyond SM_CXDRAG and releases the button in a finally block. press_keys(keys: 'ctrl+shift+t' | ['ctrl','shift','t'] | sequences such as 'tab*3', repeat, holdMs, delayBetweenMs, window?) shares the M4.1 key table. There are no standalone key_down/mouse_down tools. If they are ever added, they need a 10 s TTL watchdog plus release on ApplicationStopping and stdin EOF. All of these go through the M1 guards. Document that Win+L and Ctrl+Alt+Del cannot be injected.

*Sources: r1:R28, r1:R29, r2:R34, r1:crit:R28, r1:crit:R29, r1:INP-G3, r1:INP-G4, r2:INP-G3, r2:INP-G4, r2:MSP-G10*

### M4.4 type_text paste mode and bounded long text

**Priority:** P2 · **Effort:** M · **Kind:** extend

Long or multi-line text typed key by key is slow, and autocomplete and auto-indent corrupt it. A runaway type_text cannot be bounded until a paste path exists.

**Design.** type_text gains method=unicode|keys|paste|auto, where auto pastes above about 200 characters. paste saves the text clipboard, sets the new text with ExcludeClipboardContentFromMonitorProcessing, sends Ctrl+V with scan codes through the guard, waits about 300 ms and restores the old text; the restore is best effort and documented as lossy. keys uses VkKeyScanEx, for RDP and VM targets. With delay=0, text goes out in 64-character batches. Results return Length and CharactersTyped (counting runes) instead of echoing Text. The 5000-character keystroke cap ships together with paste mode, never before it.

*Sources: r1:R31, r2:R35, r2:R11, r2:crit:R11, r1:INP-G5, r1:INP-G8, r2:INP-G5*

## M5 Browser bridge

Reliable CDP for debug-enabled Chromium, bound to the right window and tab, with a few high-value tools: readiness, tabs, navigation, waits and paged content. The UIA fallback is locale-independent, and anything richer is handed to Playwright MCP explicitly. After M2 this milestone can run in parallel with M3/M4.

### M5.1 Debug-browser launch readiness and first-run suppression

**Priority:** P1 · **Effort:** S · **Kind:** fix

launch_browser_with_debug reports success without checking that the endpoint answers. Fresh profiles hit welcome, default-browser and search-engine-choice screens. Per-user installs are missed.

**Design.** Always pass --no-first-run, --no-default-browser-check and --disable-search-engine-choice-screen. Default to port=0 and read <userDataDir>/DevToolsActivePort, which removes the IsPortInUse race and the well-known 9222; an explicit port is validated to 1024-65535. Poll /json/version for up to about 10 s and return cdpReady, browserVersion, browserWsEndpoint and cdpEndpoint (http://127.0.0.1:<port>); otherwise fail with DebugLaunchFailed and a reason. Resolve the executable through App Paths (HKCU, then HKLM), then ProgramFiles, ProgramFiles(x86) and LocalApplicationData, with an executablePath override, and list the paths tried in errors. Refuse a browser's default 'User Data' directory. The tool description warns that the profile starts empty and the local endpoint is unauthenticated.

*Sources: r1:R62, r2:R45, r1:BRW-4, r1:BRW-10, r1:BRW-11, r1:PAL-missed-1, r2:BRW-8, r2:BRW-13, r2:PAL-missed-debug-launch-unchecked*

### M5.2 CDP transport seam, persistent sessions and explicit errors

**Priority:** P1 · **Effort:** M · **Kind:** improve

CdpService builds HttpClient and ClientWebSocket inline and uses one-shot evaluates with a hard-coded id of 1, so the hang and matching bugs cannot be tested. Make the transport testable and every failure explicit.

**Design.** Inject an HttpMessageHandler and an ICdpConnectionFactory/ICdpTransport that produces ICdpConnection{SendAsync(method, params, ct), Events}. Keep a persistent CdpSession per target with incrementing ids, event dispatch and a CancellationToken on every await. Accumulate frames in an ArrayBufferWriter and connect to 127.0.0.1. Extract BuildDebugLaunchArguments and SelectTarget as pure functions. Tests use an in-process HttpListener fake CDP server serving /json and /json/version, with scripted Runtime.evaluate replies and a never-reply mode. Every browser tool accepts targetId. Errors are explicit: CdpUnavailable, CdpTargetNotFound, CdpTimeout, CdpEvalException and DialogOpen.

*Sources: r2:R44, r1:R61, r1:R5, r1:R67, r1:BRW-G9, r2:BRW-7, r2:BRW-14, r2:BRW-G7*

### M5.3 HWND-to-target binding and automatic port discovery

**Priority:** P1 · **Effort:** M · **Kind:** extend

The agent should not need to know cdpPort, and tab, navigation and wait tools all need a deterministic mapping from a window to its active tab.

**Design.** ICdpService.ResolveTargetForWindowAsync(hwnd, port?, ct). It reads the browser's command line (NtQueryInformationProcess with ProcessCommandLineInformation) for --remote-debugging-port and --user-data-dir; port 0 means read DevToolsActivePort. It verifies that the listening-port owner PID equals the window's PID. It calls Target.getTargets (pages only) and Browser.getWindowForTarget, groups targets by windowId, and matches them to the HWND by bounds within a tolerance; Chrome reports DIPs, so scale by GetDpiForWindow/96. The normalized title is only a tie-breaker, and more than one remaining match returns AmbiguousTarget. cdpPort becomes optional on every browser tool. If M0.7 did not ship the windowId binding, it lands here first; the critic rated it P0.

*Sources: r1:R65, r1:crit-prio:R65, r1:BRW-G1, r1:BRW-2, r2:BRW-G1*

### M5.4 Debug-browser lifecycle: reuse, named profiles, close and cleanup

**Priority:** P2 · **Effort:** M · **Kind:** extend

Re-running setup fails with PortInUse, profiles are either throwaway or raw paths, and nothing closes the browser or deletes the cookies left in %TEMP%.

**Design.** reuseIfRunning: if the port answers from a Chromium PID, return AlreadyRunning with the resolved window and endpoint instead of PortInUse. profile=<name> lives under %LOCALAPPDATA%/WindowsAppManagerMcp/profiles. Placement goes through M3.13. Add get_cdp_status(port). close_debug_browser(port|processId, deleteProfile=true, confirm) sends Browser.close, waits for the process tree to exit, kills any leftovers, and deletes only the temp profile directories this server created and tracked, retrying while file locks are released. No headless option.

*Sources: r1:R68, r2:R45, r1:BRW-G7, r1:BRW-10, r2:BRW-G5*

### M5.5 Tabs, navigation with a load wait, and browser waits

**Priority:** P1 · **Effort:** L · **Kind:** extend

Opening a URL in a specific window and knowing when it has loaded takes five or six fragile calls today, and tabs cannot be listed or switched without blind keystrokes.

**Design.** list_browser_tabs(window) returns [{index, targetId?, title, url?, active, windowHandle}]: CDP targets bound through M5.3, or UIA TabItem elements with SelectionItemPattern.IsSelected, which is locale-independent. activate_tab(window, index|titleContains|targetId) uses /json/activate or SelectionItemPattern.Select, with LegacyIAccessible as the fallback. browser_navigate(window|targetId, url (scheme-validated), newTab=false, waitUntil=load|domcontentloaded|none, timeoutMs=15000) runs Page.enable plus Page.navigate (surfacing errorText) or Target.createTarget, and waits for loadEventFired or readyState. The UIA fallback is EnsureForeground, ValuePattern.SetValue on the omnibox, a guarded Enter, and polling the document URL. It returns {finalUrl, title, httpStatus, loadState, elapsedMs, source}. wait_for_browser(window|targetId, urlContains|urlRegex|titleContains|textContains|selector (CDP only), timeoutMs).

*Sources: r1:R66, r2:R46, r1:BRW-G2, r1:BRW-G3, r1:BRW-G4, r2:BRW-G1, r2:BRW-G2*

### M5.6 Paged, scoped, structured browser content

**Priority:** P1 · **Effort:** S · **Kind:** improve

get_browser_content returns multi-MB outerHTML by default, measures ContentLength after truncation and has no paging. Content should be bounded, pageable, structured and marked untrusted.

**Design.** get_browser_content(format=text|markdown|links|outline|html|accessibility, selector (CDP only), offset, maxLength (default 20000, hard cap; values <=0 are rejected)) returns {content, totalLength (measured before truncation), truncated, nextOffset, source, format, targetId, untrusted:true}. CDP: one Runtime.evaluate of an in-page function that slices inside the page and produces innerText, deduplicated links, headings, simple markdown, or outerHTML with script, style and svg stripped; accessibility uses Accessibility.getFullAXTree reduced to role/name lines. UIA: clone DocumentRange, MoveEndpointByUnit to the offset and GetText(maxLength); read hrefs from Hyperlink ValuePatterns; bound the walk by node count and deadline.

*Sources: r1:R64, r2:R47, r1:BRW-9, r1:MCP-11, r1:BRW-G6, r1:MCP-missed-4, r2:BRW-11, r2:BRW-G4, r2:UIA-missed-browser-content-unbounded*

### M5.7 Locale-independent UIA browser fallback

**Priority:** P1 · **Effort:** M · **Kind:** fix

The omnibox lookup relies on English names. A page titled 'Address bar' returns the window title as the URL. Display text is returned unvalidated, and the browser window cannot be found when the agent's terminal is in front.

**Design.** Read the URL from the top-level web Document's ValuePattern first; it holds the committed URL and is locale-independent. Otherwise use the omnibox found by ClassName (OmniboxViewViews; verify per browser) inside the first ToolBar under the frame, searching from the root's children and never inside a Document subtree. Never use Name as a value, and require the result to pass Uri.TryCreate or a host-like pattern. Return Url, DisplayUrl, urlSource and isEditing, and report NewTabPage separately from UrlBarNotFound. Scale size thresholds by DPI and drop the 2000 px cap. Content reads use the active, non-offscreen Document whose value matches the committed URL, return ContentAreaNotFound otherwise, apply the M3.3 warm-up, and skip the duplicate URL lookup. Normalize handles with GA_ROOT. get_active_browser_url gains fallback='topmost' for when the agent's terminal is in front. Rename the reader interface to IBrowserUiaReader, with a deadline and a CancellationToken.

*Sources: r1:R63, r2:R48, r1:BRW-7, r1:BRW-8, r1:UIA-12, r1:UIA-13, r1:UIA-missed-1, r2:BRW-5, r2:BRW-6, r2:BRW-12, r2:UIA-11, r2:BRW-G6, r2:UIA-missed-url-matches-window-title, r2:BRW-missed-duplicate-url-lookup*

### M5.8 Thin DOM bridge and explicit Playwright MCP handoff

**Priority:** P2 · **Effort:** M · **Kind:** extend

Add a few CDP primitives that connect the DOM to the visible desktop window and act without needing the foreground, then point agents to Playwright MCP for full DOM automation.

**Design.** browser_evaluate(window|targetId, expression, awaitPromise, timeoutMs) returns the value or exceptionDetails. browser_handle_dialog(targetId, accept, promptText?) uses Page.handleJavaScriptDialog, which also recovers from DialogOpen. browser_element_rect(targetId, selector|text|role+name) returns a rect in physical screen pixels from DOM.getContentQuads, anchored on the Document's UIA BoundingRectangle (or the Chrome_RenderWidgetHostHWND rect) and scaled from CSS pixels; never use screenX plus outer-minus-inner. browser_click and browser_type use Input.dispatchMouseEvent and Input.insertText, which work on background windows and are not subject to UIPI. browser_screenshot uses Page.captureScreenshot and returns image content; activate the target first, because occluded windows stop producing frames. SKILL.md documents attaching Playwright MCP with --cdp-endpoint to the returned endpoint. This is an opt-in toolset.

*Sources: r1:R67, r2:R49, r1:BRW-G5, r2:BRW-G3*

## M6 Platform, efficiency and breadth

Fewer, leaner tools and fewer round trips through consolidation and run_steps. Coverage for surfaces UIA misses (OCR, visual change detection), packaged apps, richer process, monitor and z-order control, layout schema v2 and MCP resources and prompts. Most of these sit behind opt-in toolsets.

### M6.1 Tool consolidation and measured token budgets

**Priority:** P2 · **Effort:** M · **Kind:** improve

Overlapping tools make tool selection less reliable, and default payloads are unbounded. Merge the duplicates using short deprecation windows, and enforce the budgets measured in the contract tests.

**Design.** get_monitors(includeVirtualScreen) replaces get_primary_monitor and list_screens. find_windows with no filters replaces get_all_windows. set_window_bounds with optional x, y, width and height (SWP_NOMOVE/NOSIZE) replaces move_window and resize_window. get_browser_url(window?, default foreground) replaces get_active_browser_url. click, right_click and double_click become mouse_click aliases. Single-window tools accept handles[], and their batch twins are deprecated. Old names carry '[Deprecated: use X]' for one release and are then removed, tracked by the contract snapshot. Budgets come from the M2.12 measurements, for example at most 4k tokens for default get_ui_elements and find_ui_elements. The savings come from format, maxElements and dropping echoes, not from WhenWritingNull, which is already the SDK default.

*Sources: r1:R12, r2:R53, r1:crit:R12, r1:MCP-G6, r1:UIA-G8, r2:MSP-G9*

### M6.2 run_steps: bounded, guarded step sequences

**Priority:** P2 · **Effort:** L · **Kind:** extend

Form filling and menu navigation cost one round trip per step, and focus can drift between any two of them. A bounded batch with one guard and stop-on-error cuts round trips about five-fold.

**Design.** run_steps(window (required for input steps), steps[] (at most 50 steps and 20k characters of text) of focus|find|element_action|type|press_keys|click|scroll|drag|wait_for_window|wait_for_element|assert_foreground|assert_element|capture, stopOnError=true, delayBetweenStepsMs=50, observe=state|screenshot|none). Later steps can reference earlier results, e.g. $steps.<id>.ref. The whole sequence runs under the input gate, with the guard re-checked before every step. Results list per-step {status, elapsedMs, method, outcomeVerified, errorCode}, and steps after a failure are marked skipped. Cancel or pause triggers release_all_input. The tool reports progress, and disabled toolsets are blocked inside the batch. It is not a workflow or recorder engine.

*Sources: r1:R30, r2:R54, r1:INP-G7, r1:INP-missed-1, r2:INP-G7, r2:MSP-G10*

### M6.3 OCR: read_screen_text and find_text_on_screen

**Priority:** P2 · **Effort:** M · **Kind:** extend

When UIA is empty (canvas apps, games, RDP/Citrix, rendered PDFs), OCR provides text-based targeting and cheap text verification with no vision tokens.

**Design.** Retarget to net8.0-windows10.0.19041.0, keeping the WPF reference; note the larger single-file publish. IOcrService: BGRA from IScreenCaptureWrapper goes into SoftwareBitmap.CreateCopyFromBuffer(Bgra8, Ignore), then OcrEngine.TryCreateFromUserProfileLanguages or a language parameter, returning OcrUnavailable when the engine is null. Images larger than OcrEngine.MaxImageDimension are tiled. read_screen_text(window|region|monitor, language?) returns lines and words with screen bounds, mapped through the capture transform. find_text_on_screen(text, match=contains|exact|regex, window|region) returns matches with click-ready centres, a captureId and source='ocr'. This is an opt-in toolset.

*Sources: r1:R22, r2:R16, r1:SCR-G6, r2:SCR-G5*

### M6.4 Visual change detection

**Priority:** P2 · **Effort:** M · **Kind:** extend

Cheap checks that cost no image tokens: did the screen change after an action, and has rendering settled.

**Design.** VisualDiffService computes 16x16-tile mean-luma signatures from the BGRA buffers and keeps them in the capture registry under a memory cap. New tools: wait_for_visual_change(window|region, baselineCaptureId?, timeoutMs=5000, pollMs=200, minChangedPercent=0.5); wait_for_visual_idle(window|region, stableMs=500, timeoutMs=10000); compare_captures(idA, idB). Each returns {changed|idle, changedPercent, changedRegions (screen pixels, merged tiles), elapsedMs, captureId}. All are async, cancellable and report progress. This is an opt-in toolset.

*Sources: r1:R21, r2:R17, r1:SCR-G5, r2:SCR-G4*

### M6.5 Packaged app launch by AUMID and installed-app discovery

**Priority:** P2 · **Effort:** L · **Kind:** extend

Calculator, Notepad, Terminal, Teams, the new Outlook and Spotify cannot be launched with a usable PID or window, and friendly names do not resolve to anything launchable.

**Design.** list_installed_apps(nameFilter?, limit=50) enumerates FOLDERID_AppsFolder on an STA thread (SHGetKnownFolderItem plus BHID_EnumItems, reading PKEY_AppUserModel_ID through IShellItem2) and returns {displayName, appId, isPackaged}. launch_application(appId) calls IApplicationActivationManager.ActivateApplication on an STA thread and passes the real PID to M2.6; this fails from an elevated server, and that case is reported. Display names resolve through Start Menu .lnk files (IShellLinkW) and App Paths. runAsAdmin (Verb=runas) is documented as requiring human UAC approval, maps Win32Exception 1223 to ElevationCancelled, and warns that a non-elevated server cannot drive the result.

*Sources: r1:R53, r2:R40, r1:PAL-G3, r2:PAL-G3, r2:PAL-G4*

### M6.6 Richer list_processes and elevation fields

**Priority:** P2 · **Effort:** S · **Kind:** improve

WindowCount is 0 whenever includeWindowless is true, and results lack windows, parent, start time and elevation, so 'which window belongs to this app' takes extra calls.

**Design.** Always build the per-PID window map and return windows:[{handle, title}]. Add parentProcessId from one Toolhelp snapshot, startTime, and isElevated/integrity from M1.4. includeCommandLine (NtQueryInformationProcess, class 60) is opt-in and redacts secret-looking values (--token=, password=, key=, Bearer). Add limit=50, sortBy=name|memory|startTime, pathContains and titleContains. isElevated is also available as an opt-in field on WindowInfo.

*Sources: r1:R54, r1:R52, r2:R41, r2:crit:R43, r1:PAL-G8, r1:PAL-missed-4, r2:PAL-15, r2:PAL-G7*

### M6.7 Monitor addressing, percentage placement, z-order and topmost

**Priority:** P2 · **Effort:** M · **Kind:** extend

Users say 'the left monitor', and enumeration order shifts when docking. Agents also need to pin a reference window or raise one above a topmost overlay without stealing focus.

**Design.** Every monitor parameter accepts an index, 'primary', 'current', 'left'/'right'/'leftmost'/'rightmost' or a device name, resolved by MonitorService.ResolveMonitor. Monitors get a stable id (DeviceName), a friendlyName (QueryDisplayConfig plus DisplayConfigGetDeviceInfo), DPI and their relation to the primary; index semantics stay unchanged for existing presets. set_window_bounds gains unit=pixels|percent and relativeTo=screen|workArea, reusing the percentage math moved out of LayoutService.cs:249-252. SetWindowPos gains insertAfter. New set_window_zorder(window, mode=topmost|notopmost|top|bottom) uses SWP_NOMOVE|NOSIZE|NOACTIVATE and returns isTopmost read back from WS_EX_TOPMOST. New set_window_visibility(window, visible) uses ShowWindowAsync with SW_HIDE/SW_SHOWNA and requires confirm. WindowInfo gains IsTopmost and Dpi.

*Sources: r1:R38, r1:R39, r2:R31, r1:WC-G5, r1:WC-G8, r1:PAL-10, r2:WC-G7, r2:WC-G8*

### M6.8 Layout schema v2 and agent-authored layouts with dry run

**Priority:** P2 · **Effort:** L · **Kind:** extend

Presets cannot record window state or monitor identity, so they break after docking. The agent can only create a layout by arranging real windows and cannot preview what apply would move.

**Design.** Set LayoutPreset.Version=2 and upgrade v1 files on load. Each placement stores processName (exact), exePath, className, titlePattern and ordinal; state and normalRect (GetWindowPlacement, converted from workspace to screen coordinates); monitor identity {deviceName, isPrimary, bounds, scale} plus a topology fingerprint; and launch info {executable, arguments[], workingDirectory, appId}, with the AUMID for ApplicationFrameHost windows read through SHGetPropertyStoreForWindow. Apply scores candidates and assigns them one to one. It resolves each monitor by device name, then the primary flag, then nearest geometry, reporting monitorFallback. It restores through SetWindowPlacement plus a second SetWindowPos when the DPI differs, re-applies max/min state and reads the bounds back. define_layout (or save_layout with placements[]) resolves snap names through SnapMath at apply time. Add update_layout(name, index, patch). apply_layout gains dryRun and placementIndexes. include/excludeProcesses become string[].

*Sources: r1:R58, r1:R59, r2:R42, r2:R38, r1:PAL-G5, r1:PAL-G6, r1:PAL-10, r1:PAL-11, r2:PAL-G5*

### M6.9 MCP resources and prompts

**Priority:** P3 · **Effort:** S · **Kind:** extend

After the SDK upgrade, expose read-mostly state as resources and common recipes as prompts, for clients that use them.

**Design.** WithResources: desktop://monitors, windows://list, layout://{name} and screenshot://{captureId}. WithPrompts: automate_app(goal, app), arrange_workspace(description), fill-form-with-elements and diagnose-click-did-nothing. Set UseStructuredContent=true on read tools. The explicit handle and ref tools remain the primary API.

*Sources: r1:R14, r2:R52, r1:MCP-G10, r2:MSP-G8*

## M7 Later

Valuable but deferred: a modern capture backend and visual aids, event-driven window tracking, virtual-desktop detection, opt-in takeover detection, best-effort workspace restore, and a scenario harness that measures round trips.

### M7.1 Windows.Graphics.Capture backend, cursor, grid and zoom

**Priority:** P3 · **Effort:** L · **Kind:** extend

PrintWindow returns black frames for some DirectComposition, WinUI and UWP surfaces, and its fallback captures whatever window is covering the target. The model also cannot see the cursor or a coordinate grid.

**Design.** WgcCaptureWrapper implements IScreenCaptureWrapper: IGraphicsCaptureItemInterop.CreateForWindow/CreateForMonitor, Direct3D11CaptureFramePool.CreateFreeThreaded, one frame copied to a staging texture and mapped to BGRA, with a frame-wait timeout because minimized windows produce no frames. Enable IsCursorCaptureEnabled. Set IsBorderRequired=false only on build 22000+, behind an ApiInformation check. captureMethod=auto|printwindow|wgc|screen; auto falls back to WGC when PrintWindow fails or the blank heuristic fires. includeCursor draws the cursor through GetCursorInfo, GetIconInfo and DrawIconEx (DeleteObject both bitmaps afterwards), and the cursor position is always reported. gridSpacing draws screen-coordinate labels after resize. A capture_region scale of 1-4 zooms small regions, with the transform recorded.

*Sources: r1:R23, r1:R24, r2:R18, r1:SCR-G7, r1:SCR-G9, r2:SCR-G8, r2:SCR-G9, r2:SCR-G10*

### M7.2 Window event journal

**Priority:** P3 · **Effort:** L · **Kind:** extend

Answer 'did a dialog pop up, did focus move, did the title change' in one cheap call, catch transient popups that polling misses, and let waits wake on events.

**Design.** WindowEventService (an IHostedService) runs a dedicated thread with SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_OBJECT_CREATE/DESTROY/SHOW/HIDE/NAMECHANGE, WINEVENT_OUTOFCONTEXT|WINEVENT_SKIPOWNPROCESS) and a GetMessage loop. It keeps only top-level windows (OBJID_WINDOW, CHILDID_SELF) in a 1024-entry ring buffer with sequence numbers. get_window_events(sinceSeq?, types?, max=50) returns the entries plus nextSeq. wait_for_window and wait_for_element wake on buffer signals, and action results can include the events since the call started.

*Sources: r1:R40, r1:WC-G9, r2:R30*

### M7.3 Virtual desktop awareness (detection only)

**Priority:** P3 · **Effort:** S · **Kind:** extend

Windows on other virtual desktops show up unmarked, and snapping or capturing them silently does nothing useful.

**Design.** Call IVirtualDesktopManager.IsWindowOnCurrentVirtualDesktop (a documented COM interface, created on an MTA helper) and cross-check with DWM_CLOAKED_SHELL. Add WindowInfo.IsOnCurrentDesktop. Listings default to the current desktop, and includeOtherDesktops=true widens them. focus_window reports when the shell switched desktops. There is no cross-desktop move.

*Sources: r1:R41, r1:WC-G10, r2:WC-G9*

### M7.4 User-takeover detection (opt-in, warning-first)

**Priority:** P3 · **Effort:** S · **Kind:** extend

Long injections keep going after the user grabs the mouse. This complements the M1.10 pause hotkey, which remains the primary control.

**Design.** Opt-in. A low-level WH_KEYBOARD_LL/WH_MOUSE_LL hook thread records only the time of the last input without LLKHF_INJECTED/LLMHF_INJECTED, never its content. GetLastInputInfo cannot be used, because SendInput and the user's own Enter in the agent terminal both update it. Only foreign input that arrives during an in-flight long operation (type_text, drag, run_steps) counts as takeover. The operation stops with UserInterrupted, reports progress and releases held input.

*Sources: r1:R56, r2:R11, r2:crit:R11, r1:INP-G8*

### M7.5 Workspace snapshot and restore (best effort)

**Priority:** P3 · **Effort:** L · **Kind:** extend

'Restore my morning workspace' should reopen the right files, folders and URLs, not only the window rectangles.

**Design.** save_workspace extends v2 layouts with, per app: exe path, working directory, AUMID, Explorer folders via IShellWindows, and tab URLs via CDP when the browser has a debug port (otherwise the active tab via UIA, marked partial). Command lines are captured only when opted in per preset, with secret-looking values redacted. restore_workspace(name, dryRun) relaunches missing apps through M2.6/M6.5, opens URLs and folders, applies the placements, and returns per-placement results. It is documented as best effort, because single-instance apps and browsers without a debug port cannot be fully restored.

*Sources: r1:R60, r2:R43, r2:crit:R43, r1:PAL-G9, r2:PAL-G8*

### M7.6 Scenario evaluation harness with a round-trip metric

**Priority:** P3 · **Effort:** M · **Kind:** extend

A repeatable suite of real desktop tasks that measures success rate, tool calls per task and response size, so automation improvements can be quantified.

**Design.** Scenarios run through the in-process MCP client against the fixture app and real targets: Notepad type-and-save through the Save dialog, Calculator via UIA, a Settings deep link, debug Chrome navigate-and-read, and a two-window layout round trip. They run in Windows Sandbox or on a self-hosted interactive runner through workflow_dispatch, gated by the desktop opt-in. Each scenario records success, calls, wall time and response bytes as a JSON artifact, compared against the previous run.

*Sources: r2:R5, r2:MSP-G5*

## Non-goals

- Do not reimplement Playwright-style DOM automation (CSS/ARIA selectors, form filling, uploads, network interception, multi-engine support) in CdpService. Keep a thin CDP bridge that ties the DOM to the visible desktop window, and hand off to Playwright MCP with --cdp-endpoint.
- Do not bundle a vision grounding model (OmniParser, UI-TARS, GroundingDINO) or a custom OCR engine, and do not run an LLM loop inside the server. The client model plans; the server supplies UIA refs, Windows.Media.Ocr, Set-of-Marks and verified actions.
- Do not add generic shell, PowerShell, registry, filesystem or HTTP tools (Windows-MCP style). They duplicate the host's own tools and greatly widen the prompt-injection surface of a server that already injects input.
- Do not bypass UIPI, the foreground lock or the secure desktop: no uiAccess manifest, no elevated-by-default server, no SYSTEM helper service, no DLL injection, no driver-level input. Detect and report TargetElevated/DesktopUnavailable. The only elevation path is an explicit runAsAdmin flag that a human approves through UAC.
- Do not try to inject Ctrl+Alt+Del or Win+L, or to drive UAC or the secure desktop.
- Do not use undocumented virtual-desktop COM interfaces (IVirtualDesktopManagerInternal) to switch desktops or move other processes' windows between them; MoveWindowToDesktop returns E_ACCESSDENIED for foreign windows anyway. Detection only.
- Do not build a YAML workflow engine or macro recorder, and never record the user's live keyboard and mouse with global hooks to build macros. run_steps is a bounded, guarded batch, and durable replayable workflows belong in the client. The opt-in takeover hook records timestamps only.
- Do not run persistent or triggered background layouts inside the stdio server. Its lifetime is tied to one client session, duplicate servers would fire twice, and a message-only window cannot receive WM_DISPLAYCHANGE. Revisit only if a long-lived host appears.
- Do not add a headless mode to launch_browser_with_debug. A headless browser has no HWND, so this server's window, UIA and capture tools cannot reach it.
- Do not add one tool per UIA pattern (invoke_element, toggle_element, select_item and so on), and do not keep deprecated aliases indefinitely. Use one element_action, one-release deprecation windows and a curated default toolset to keep the per-turn schema budget down.
- Do not promise lossless clipboard save and restore, or a full multi-tab session restore. Delay-rendered clipboard formats and browsers without a debug port make both best effort, so document the limits.
- Do not do a wholesale UIA3/FlaUI rewrite. Swap engines only behind the IUiaTree seam, after the managed traversal fixes and fake-tree tests exist, and keep UIA2 as a fallback.
- Do not build RDP-loopback 'picture-in-picture' agent desktops (UFO2 style). Target guards, the pause hotkey and takeover detection address input contention at proportionate cost.
- Do not make PostMessage/SendMessage control input (WM_CHAR, WM_LBUTTONDOWN, ControlSend-style) the default 'background' input path; Chromium, WPF, UWP and WinUI ignore or mishandle posted input. Use UIA patterns and CDP Input.dispatch* instead.
- Do not add an HTTP or remote transport or remote-control features. A local stdio process is the right trust boundary for a server that types into the user's desktop.
- Do not keep base64-in-JSON as the default image path, and do not reimplement Anthropic's computer-use tool one-for-one. Return MCP image content plus semantic targets, with pixel-coordinate tools as the fallback.
- Do not ship English-only dialog helpers driven by button captions. Address dialogs by AutomationId, ClassName (#32770) and patterns, so automation works on localized Windows.
- Do not pursue MSIX/ODR registration until its containment model has been validated against the broad UI access this server needs.
- Do not expose input holds that span tool calls, such as standalone key_down/mouse_down. Holds live inside one call (press_keys holdMs, drag, run_steps) and are released in a finally block.

## Design corrections to keep

These are mistakes the critics found in earlier proposals. Check every implementation against them.

- Default host-window guard: input with no target must never reach the agent's host. HostGuard covers ancestor PIDs, the GetConsoleWindow() owner, and the foreground window at server start (Windows Terminal delegation means WindowsTerminal.exe is not an ancestor). The input guard and close guard both consult it, and the check runs before every SendInput batch, not only when a target is passed.
- Capture origin fix (GetWindowRect for frame captures, ClientToScreen for client captures) plus imageToScreen {offset, scale} must ship in the same change as image content and default downscaling. Otherwise visible downscaled images increase mis-clicks.
- EnsureForeground runs on one dedicated long-lived thread and calls AttachThreadInput(GetCurrentThreadId(), fgTid). Attaching two foreign threads to each other (fgTid, targetTid) satisfies none of the SetForegroundWindow rules. Skip the attach when the foreground thread is hung or the foreground process has higher integrity.
- Never send a lone Alt tap to unlock the foreground; it reaches the agent's terminal and can leave the target in menu mode. Use a zero-delta MOUSEEVENTF_MOVE or an unassigned VK, only after the attach path has failed. Then check GUI_INMENUMODE through GetGUIThreadInfo and send Esc if it is set. Record the Method used.
- Mouse hit-test guard: accept the target root, windows in its GW_OWNER chain, and windows of the target process or its descendants (WebView2 msedgewebview2.exe and IME popups). Check IsWindowEnabled(target) and return BlockedByModal with the GW_ENABLEDPOPUP window, instead of letting the click be silently ignored.
- Element refs must be globally unique and never reused within a session (for example w<hwndSeq>e<n>), and the same RuntimeId must keep its ref. Check liveness with a live property read, because GetRuntimeId is memoized. Return ElementStale; a ref must never silently resolve to a different element after a re-walk.
- Invoke, Toggle and ExpandCollapse can block until the modal they open is closed. Run them on throwaway threads outside the shared UIA worker, cap the number in flight, and return invokedPendingModal with a window-snapshot diff. Never recycle a worker that is stuck in a cross-process call.
- The set_value keyboard fallback (select-all, then type) is allowed only when FocusedElement equals the target and the foreground guard passes; otherwise return FocusNotOnElement. This is the INP-1 destructive-chord hazard.
- The managed System.Windows.Automation client cannot read raw control-type ids 50039/50040. Fall back to LocalizedControlType (locale-dependent) and then 'Unknown', and match those nodes client-side by class. Raw ids are available only after the UIA3 swap.
- Schedule the UIA3/FlaUI swap after the IUiaTree seam and fake-tree tests, and before refs, actions and waits are built; time-box it as a spike. Widen the seam to cover patterns, events and cache requests, not just traversal.
- Upgrade the MCP SDK off 0.5.0-preview.1, and land curated toolsets, right after the contract tests and before M3-M5 add about 30 tools. CallToolFilters and ElicitAsync already exist in 0.5.0-preview.1, so the M0 exception filter and M2 elicitation do not wait for the upgrade.
- send_keys maps every character through VkKeyScanExW on the foreground thread's layout (VK, Shift/AltGr and scan code) and uses KEYEVENTF_UNICODE only when the character is not on the layout. Otherwise single-key shortcuts ('j', '/', KeyTips, RDP/VM clients) silently do nothing. type_text keeps Unicode as its default.
- type_text must not send newline as Enter by default, because that submits chat messages and moves Excel cells. When the text contains newline or tab, require an explicit controlChars=unicode|keys|shiftEnter.
- No key or mouse-button hold may span tool calls. If key_down/mouse_down ever exist, they need a 10 s TTL watchdog plus release on IHostApplicationLifetime.ApplicationStopping and on stdin EOF.
- get_clipboard must honor the privacy formats (ExcludeClipboardContentFromMonitorProcessing, CanIncludeInClipboardHistory=0, Clipboard Viewer Ignore). By default it returns only content from a copy the server itself initiated (GetClipboardSequenceNumber). Reading the user's own clipboard is opt-in through config or elicitation, not a flag the model sets.
- Launch resolution must accept reused existing windows (status reusedExistingWindow or openedInTab) for single-instance and tabbed apps such as Win11 Notepad, Terminal, VS Code and Explorer. A null Process.Start under ShellExecute is a handoff, not a failure.
- Resolve bare executable names through App Paths (HKCU, then HKLM) and a manual PATH/PATHEXT enumeration that skips relative, empty and '.' entries. SearchPathW cannot exclude the current directory. Add a regression test that plants an exe in the working directory.
- CDP target binding: the primary binding is Browser.getWindowForTarget windowId plus bounds and a listening-port-owner PID check. The title is only a tie-breaker, after a normalizer strips Unicode Cf characters (U+200B in 'Microsoft Edge'), ' and N more pages' and profile/browser suffixes. Exact title equality regresses Edge. Use the same normalizer in window selectors.
- Release integrity: a ProductVersion prefix check cannot detect the stale-bin state. Make CI the only producer of bin/, or fail PRs that hand-edit it. Add a CI guard that runs the committed exe, calls tools/list and diffs against the checked-in schema snapshot.
- Desktop tests: deny by default in code with [DesktopFact]/[DesktopTheory], skipped unless an env var opts in. runsettings and --filter alone are bypassed by IDE runners, explicit filters and agents in worktrees. Re-baseline the coverage gate, and add a fixture-app E2E job so the real input, UIA and foreground paths are still tested somewhere.
- Graceful kill and close_application must post WM_CLOSE only to the Alt-Tab set (visible, unowned, non-toolwindow), never to Default IME, MSCTFIME, GDI+ hook or hidden tray windows. Packaged apps close through the ApplicationFrameWindow that hosts their CoreWindow, with UIA WindowPattern.Close as the fallback.
- Cross-DPI moves use two phases: SWP_NOSIZE onto the target monitor, settle and re-read, then size. Never pre-scale by targetDpi/sourceDpi, because PMv2 apps rescale themselves on WM_DPICHANGED and get double-scaled.
- restoreIfMinimized must re-minimize with SW_SHOWMINNOACTIVE, because SW_MINIMIZE activates another window and breaks the foreground assumptions. Wait for two equal frames or rects rather than a fixed 200 ms.
- The listing predicate (no cloaked, tool or shell windows) must be bypassed when an explicit handle, className or includeShell is given, so the taskbar, tray and notification flyouts stay reachable.
- DPI: declare PerMonitorV2 in app.manifest only and drop the SetProcessDpiAwarenessContext call, which always fails with ACCESS_DENIED once the manifest sets awareness. Log and report the effective awareness instead.
- Takeover detection cannot use GetLastInputInfo, which SendInput and the user's own Enter in the terminal both update. Use low-level hooks with the injected-flag check, count only input during an in-flight operation, and make it opt-in. The explicit pause hotkey is the primary control, and the agent must not be able to resume itself after a pause.
- Do not cap type_text at 5000 characters until paste mode ships, or the cap removes an existing capability.
- WhenWritingNull is already the SDK default. Token savings come from format, maxElements and dropping echoes, enforced by budgets measured in the contract tests.
- Server instructions and get_server_info must not depend on generated docs; r2 had a dependency that violated its own sequence.
- Command lines captured for list_processes or workspaces are opt-in, and secret-looking values (--token=, password=, key=, Bearer) are redacted before anything is written to disk or returned to the model.
- Pulled forward: the docs-safety hotfix and exception filter (to M0); layout storage and CDP timeouts (to M0); the capture-origin subset (to ship with image content in M1.2); get_focused_element (to M1); scroll, clipboard get/set, the protocol allowlist, the layout capture-identity subset, toolsets and the SDK upgrade (to M2); and a desktop smoke/fixture suite (to M1).

## Appendix: defect register

Run ids such as `r1:WC-1` and `r2:WC-1` trace each entry back to the raw review runs.

| Key | Severity | Area | Defect | Location | Found by | M0 |
|-----|----------|------|--------|----------|----------|----|
| D-BRW-01 | critical | Browser | launch_browser_with_debug cannot serialize its success result (nint? WindowHandle) | `src/WindowsAppManagerMcp/Tools/BrowserReadingTools.cs:138` | both | fixed |
| D-SCR-01 | critical | Screenshots | Screenshots are returned as base64 inside JSON text, not as MCP image content | `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:34` | both | open |
| D-BRW-02 | high | Browser | CDP WebSocket calls have no timeout or cancellation | `src/WindowsAppManagerMcp/Services/CdpService.cs:195` | both | fixed |
| D-BRW-03 | high | Browser | CDP target selection uses title substring matching and can return another tab's or browser's data | `src/WindowsAppManagerMcp/Services/CdpService.cs:145` | both | fixed |
| D-BRW-04 | high | Browser | Debug browser starts with a fresh profile but does not suppress first-run UI | `src/WindowsAppManagerMcp/Services/ProcessService.cs:470` | both | open |
| D-INP-01 | high | Input | Keyboard and mouse input is not tied to a target window and never verifies which window received it | `src/WindowsAppManagerMcp/Tools/InputSimulationTools.cs:79` | both | open |
| D-INP-02 | high | Input | InputSimulationService unit tests send real mouse/keyboard input, including Alt+F4, to the developer's desktop | `tests/WindowsAppManagerMcp.Tests/Unit/Services/InputSimulationServiceTests.cs:567` | both | fixed |
| D-INP-03 | high | Input | Input blocked by UIPI/elevation or the secure desktop is not detected or diagnosed | `src/WindowsAppManagerMcp/Native/NativeMethods.Input.cs:9` | both | open |
| D-MCP-01 | high | MCP surface & project | The plugin runs a stale committed exe, and /commit and /update-plugin publish to paths the plugin never runs | `.claude-plugin/plugin.json:24` | both | partly fixed |
| D-MCP-02 | high | MCP surface & project | Test suite drives the real desktop beyond input (launches Chrome/Notepad, attempts kills), runs unfiltered in CI/README, and the CI test job keeps failing | `tests/WindowsAppManagerMcp.Tests/Unit/Services/ProcessServiceTests.cs:458` | both | fixed |
| D-PAL-01 | high | Launch & layouts | Launching via a launcher stub or single-instance app (browsers with a URL) returns a dead PID and no window handle | `src/WindowsAppManagerMcp/Services/ProcessService.cs:360` | both | open |
| D-PAL-02 | high | Launch & layouts | Shell-metacharacter check rejects any path containing '(' or ')', so 'Program Files (x86)' apps and Edge debug launch always fail | `src/WindowsAppManagerMcp/Services/InputValidationService.cs:14` | both | fixed |
| D-PAL-03 | high | Launch & layouts | Layouts are stored in the versioned plugin install folder: lost on update and bundled samples never deployed | `src/WindowsAppManagerMcp/Program.cs:49` | both | fixed |
| D-PAL-04 | high | Launch & layouts | A captured layout cannot restore more than one window per process: every placement hits the same window | `src/WindowsAppManagerMcp/Services/LayoutService.cs:245` | both | open |
| D-PAL-05 | high | Launch & layouts | kill_process graceful mode escalates to a forced process-tree kill after 3 seconds | `src/WindowsAppManagerMcp/Services/ProcessService.cs:303` | both | fixed |
| D-UIA-01 | high | UI Automation | controlTypes filter discards matching descendants of non-matching containers and double-counts TotalElementCount | `src/WindowsAppManagerMcp/Services/UIElementService.cs:284` | both | open |
| D-UIA-02 | high | UI Automation | A null ControlType (UWP/WinUI AppBar, SemanticZoom) or any non-ElementNotAvailable exception on one element aborts the whole traversal | `src/WindowsAppManagerMcp/Services/UIElementService.cs:369` | both | fixed |
| D-WIN-01 | high | Windows & monitors | focus_window has no foreground-lock workaround and never verifies focus changed | `src/WindowsAppManagerMcp/Services/WindowService.cs:196` | both | open |
| D-WIN-02 | high | Windows & monitors | Geometry operations never un-maximize (and move/resize ignore minimized windows) | `src/WindowsAppManagerMcp/Services/WindowService.cs:151` | both | open |
| D-WIN-03 | high | Windows & monitors | move_window_to_monitor 'maximize' maximizes on the current monitor and other modes do not fit the window to the target | `src/WindowsAppManagerMcp/Services/WindowService.cs:268` | both | open |
| D-WIN-04 | high | Windows & monitors | Window listings (and save_layout) include cloaked, other-virtual-desktop, tool and shell windows | `src/WindowsAppManagerMcp/Services/WindowService.cs:25` | both | open |
| D-BRW-05 | medium | Browser | Silent fallback from CDP to UIA with no indication of the method used; textOnly=false is ignored | `src/WindowsAppManagerMcp/Services/BrowserReadingService.cs:201` | both | fixed |
| D-BRW-06 | medium | Browser | UIA address-bar lookup is English-only, its fallback fails on wide/high-DPI windows, and it can return non-URL text | `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:111` | both | open |
| D-BRW-07 | medium | Browser | Browser URL name search matches the top-level window's own title, so a page title can be returned as the URL | `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:180` | both | open |
| D-BRW-08 | medium | Browser | UIA URL is the omnibox display text, usually missing the scheme or showing in-progress typing | `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:279` | both | open |
| D-BRW-09 | medium | Browser | get_browser_content is unbounded by default, has no truncation signal or paging, and ContentLength is post-truncation | `src/WindowsAppManagerMcp/Tools/BrowserReadingTools.cs:37` | both | open |
| D-BRW-10 | medium | Browser | launch_browser_with_debug never checks the CDP endpoint opened, does not validate the port, and never cleans up temp profiles | `src/WindowsAppManagerMcp/Services/ProcessService.cs:503` | both | open |
| D-BRW-11 | medium | Browser | Debug browser exposes an unauthenticated CDP port on the well-known 9222 | `src/WindowsAppManagerMcp/Services/ProcessService.cs:472` | both | open |
| D-BRW-12 | medium | Browser | --user-data-dir is quoted twice, so paths with spaces split into separate arguments | `src/WindowsAppManagerMcp/Services/ProcessService.cs:473` | both | fixed |
| D-BRW-13 | medium | Browser | Debug-launch url argument is not validated and can inject browser switches | `src/WindowsAppManagerMcp/Services/ProcessService.cs:473` | both | partly fixed |
| D-BRW-14 | medium | Browser | Browser UIA searches and content reads are unbounded (whole-tree Descendants scans, full-document GetText, no deadline) | `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:232` | both | open |
| D-BRW-15 | medium | Browser | UIA content path does not wake Chromium's on-demand accessibility tree | `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:335` | both | open |
| D-BRW-16 | medium | Browser | UIA content extraction can return browser UI text or a side panel via the frame-pane fallback | `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:335` | r2 | open |
| D-INP-04 | medium | Input | send_keys shortcuts on punctuation/OEM keys (Ctrl+-, Ctrl+=, Ctrl+/, Ctrl+comma, Ctrl+backtick) are sent as Unicode packets and never fire | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:371` | both | open |
| D-INP-05 | medium | Input | send_keys sends letters and digits as raw virtual keys, ignoring case, Caps Lock and keyboard layout | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:474` | both | open |
| D-INP-06 | medium | Input | No Win modifier, and {CTRL}/{ALT}/{SHIFT}/{WIN} in braces are tapped rather than held | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:359` | both | open |
| D-INP-07 | medium | Input | send_keys implements only part of the SendKeys grammar: no escapes, grouping, repeat counts or '~' | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:327` | both | open |
| D-INP-08 | medium | Input | type_text sends Tab, CR and LF as Unicode packets instead of real Tab/Enter key presses | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:238` | both | open |
| D-INP-09 | medium | Input | Input operations are not serialized across concurrent MCP requests | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:234` | r1 | fixed |
| D-INP-10 | medium | Input | Clicks ignore swapped mouse buttons | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:204` | both | open |
| D-MCP-03 | medium | MCP surface & project | Agent-facing docs drift from the registered tools: wrong tool count, browser tools undocumented, wrong parameter names | `skills/using-windows-app-manager/SKILL.md:73` | both | fixed |
| D-MCP-04 | medium | MCP surface & project | The exe requires the .NET 8 Windows Desktop Runtime but docs only ask for the .NET 8 Runtime | `src/WindowsAppManagerMcp/WindowsAppManagerMcp.csproj:11` | both | fixed |
| D-MCP-05 | medium | MCP surface & project | Errors surface as generic SDK errors, empty results or stripped messages; one malformed batch item aborts the whole batch | `src/WindowsAppManagerMcp/Tools/LayoutPresetTools.cs:39` | both | partly fixed |
| D-MCP-06 | medium | MCP surface & project | The GitHub release job has never succeeded (read-only token) | `.github/workflows/build.yml:42` | r2 | fixed |
| D-MCP-07 | medium | MCP surface & project | Committed .claude/settings.local.json grants broad no-prompt permissions and local artifacts are tracked | `.claude/settings.local.json:63` | both | partly fixed |
| D-PAL-06 | medium | Launch & layouts | Layout matching can target arbitrary windows: empty matchers/title_only match everything, unknown matchBy is accepted, ClassName ignored | `src/WindowsAppManagerMcp/Services/LayoutService.cs:313` | both | open |
| D-PAL-07 | medium | Launch & layouts | Protocol policy blocks legitimate targets (ms-settings:, shell:AppsFolder) yet allows cmd/powershell with arbitrary arguments | `src/WindowsAppManagerMcp/Services/InputValidationService.cs:17` | r1 | open |
| D-PAL-08 | medium | Launch & layouts | launch_application joins arguments without proper Windows quoting | `src/WindowsAppManagerMcp/Services/ProcessService.cs:70` | both | fixed |
| D-PAL-09 | medium | Launch & layouts | Process.Start returning null (ShellExecute handed off to an existing instance) is reported as failure | `src/WindowsAppManagerMcp/Services/ProcessService.cs:75` | both | open |
| D-PAL-10 | medium | Launch & layouts | Layouts identify monitors only by enumeration index and silently fall back to monitor 0 | `src/WindowsAppManagerMcp/Services/LayoutService.cs:246` | both | open |
| D-PAL-11 | medium | Launch & layouts | apply_layout ignores window state and mixed DPI | `src/WindowsAppManagerMcp/Services/LayoutService.cs:254` | both | open |
| D-PAL-12 | medium | Launch & layouts | save_layout and delete_layout report success when nothing was saved or deleted | `src/WindowsAppManagerMcp/Tools/LayoutPresetTools.cs:83` | both | fixed |
| D-PAL-13 | medium | Launch & layouts | apply_layout returns only counts, never verifies placement, and reports success with zero matches | `src/WindowsAppManagerMcp/Services/LayoutService.cs:272` | both | fixed |
| D-PAL-14 | medium | Launch & layouts | launchMissing cannot work for agent-saved layouts: no launch command is captured and commands cannot carry arguments | `src/WindowsAppManagerMcp/Services/LayoutService.cs:220` | both | open |
| D-PAL-15 | medium | Launch & layouts | apply_layout throws NullReferenceException when a freshly launched window is untitled or already closed | `src/WindowsAppManagerMcp/Services/LayoutService.cs:227` | both | open |
| D-PAL-16 | medium | Launch & layouts | LayoutService preset cache is not thread-safe and get_layout/apply_layout never reload from disk | `src/WindowsAppManagerMcp/Services/LayoutService.cs:68` | both | fixed |
| D-PAL-17 | medium | Launch & layouts | Bare executable names are resolved against the server's current directory first (binary planting) | `src/WindowsAppManagerMcp/Services/ProcessService.cs:65` | r1 | partly fixed |
| D-PAL-18 | medium | Launch & layouts | Arguments passed to batch-file shims (e.g. code.cmd) let cmd.exe interpret '&', '\|' and %VAR% | `src/WindowsAppManagerMcp/Services/ProcessService.cs:70` | r1 | fixed |
| D-SCR-02 | medium | Screenshots | capture_with_elements with includeFrame=false offsets every overlay and ImageBounds by the frame/title-bar size | `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:216` | both | open |
| D-SCR-03 | medium | Screenshots | capture_with_elements is all-or-nothing: any UIA failure or timeout drops the screenshot | `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:197` | both | open |
| D-SCR-04 | medium | Screenshots | Window captures give no screen origin or image-to-screen transform | `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:185` | both | open |
| D-SCR-05 | medium | Screenshots | Capturing a minimized, hidden or cloaked window succeeds with a tiny or black image | `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:76` | both | open |
| D-SCR-06 | medium | Screenshots | outputPath allows unvalidated writes anywhere, overwriting existing files | `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:405` | both | open |
| D-SCR-07 | medium | Screenshots | Defaults produce full-resolution lossless PNGs and maxWidth limits only width | `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:46` | r2 | open |
| D-SCR-08 | medium | Screenshots | Element overlays are drawn at full resolution before the maxWidth downscale, making them faint or invisible | `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:515` | r2 | open |
| D-SCR-09 | medium | Screenshots | DPI-unaware target windows may be rendered by PrintWindow at logical size inside a physical-size bitmap (plausible, not reproduced) | `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:78` | r1 | open |
| D-UIA-03 | medium | UI Automation | Traversal timeout discards the partially built tree | `src/WindowsAppManagerMcp/Services/UIElementService.cs:164` | both | open |
| D-UIA-04 | medium | UI Automation | timeoutMs is not a hard bound: a blocked UIA call or hung window holds the request and MCP cancellation is ignored | `src/WindowsAppManagerMcp/Services/UIElementService.cs:223` | both | open |
| D-UIA-05 | medium | UI Automation | No CacheRequest: about 10 uncached cross-process calls per element with duplicate reads | `src/WindowsAppManagerMcp/Services/UIElementService.cs:278` | both | open |
| D-UIA-06 | medium | UI Automation | visibleOnly/interactableOnly/minWidth/minHeight prune whole subtrees, and interactableOnly only checks IsEnabled | `src/WindowsAppManagerMcp/Services/UIElementService.cs:314` | both | open |
| D-UIA-07 | medium | UI Automation | Minimized or elevated target windows return an empty tree as Success with no diagnostic | `src/WindowsAppManagerMcp/Services/UIElementService.cs:151` | both | open |
| D-UIA-08 | medium | UI Automation | UIElementService has no test seam, so filter tests never reach the traversal code | `src/WindowsAppManagerMcp/Services/UIElementService.cs:112` | r2 | open |
| D-WIN-05 | medium | Windows & monitors | Untitled windows are invisible to GetWindowInfo, so get_foreground_window, post-action re-queries and capture_with_elements fail on them | `src/WindowsAppManagerMcp/Services/WindowService.cs:466` | both | open |
| D-WIN-06 | medium | Windows & monitors | MonitorService singleton clears and rebuilds a shared List on every call with no locking | `src/WindowsAppManagerMcp/Services/MonitorService.cs:18` | both | fixed |
| D-WIN-07 | medium | Windows & monitors | Invalid monitorIndex, positioning and state values are silently coerced to defaults and reported as success | `src/WindowsAppManagerMcp/Services/MonitorService.cs:113` | both | open |
| D-WIN-08 | medium | Windows & monitors | set_window_state reports ShowWindow's previous-visibility flag as Success | `src/WindowsAppManagerMcp/Services/WindowService.cs:181` | both | open |
| D-WIN-09 | medium | Windows & monitors | close_window / close_windows_batch have no guard for shell or agent-host windows | `src/WindowsAppManagerMcp/Services/WindowService.cs:309` | both | open |
| D-WIN-10 | medium | Windows & monitors | close_window reports success as soon as WM_CLOSE is queued | `src/WindowsAppManagerMcp/Services/WindowService.cs:309` | r2 | open |
| D-WIN-11 | medium | Windows & monitors | Native window failures return a bare success:false; GetLastError is never read and elevation/UIPI is never detected | `src/WindowsAppManagerMcp/Tools/WindowManagerTools.cs:40` | both | partly fixed |
| D-WIN-12 | medium | Windows & monitors | Calls on hung ('Not Responding') windows can block a tool call indefinitely | `src/WindowsAppManagerMcp/Native/NativeWindowWrapper.cs:63` | both | open |
| D-WIN-13 | medium | Windows & monitors | UWP/packaged app windows are attributed to ApplicationFrameHost instead of the app process | `src/WindowsAppManagerMcp/Services/WindowService.cs:470` | both | open |
| D-WIN-14 | medium | Windows & monitors | Snap and reported bounds use GetWindowRect including DWM's invisible resize borders | `src/WindowsAppManagerMcp/Services/MonitorService.cs:120` | both | open |
| D-WIN-15 | medium | Windows & monitors | move_window_to_monitor measures the window before restoring it, shrinking minimized windows to their iconic size | `src/WindowsAppManagerMcp/Services/WindowService.cs:228` | both | open |
| D-BRW-17 | low | Browser | Browser executable lookup misses per-user installs, App Paths and non-stable channels | `src/WindowsAppManagerMcp/Services/ProcessService.cs:394` | r1 | open |
| D-BRW-18 | low | Browser | CDP reads use 4 HTTP requests and 2 WebSocket handshakes per call via 'localhost' | `src/WindowsAppManagerMcp/Services/CdpService.cs:36` | both | open |
| D-BRW-19 | low | Browser | Browser detection suffix/prefix heuristics classify non-browsers as browsers and inject --new-window | `src/WindowsAppManagerMcp/Services/BrowserDetectionService.cs:76` | both | open |
| D-BRW-20 | low | Browser | UIA content path always repeats the expensive URL lookup and attaches its possibly bogus value | `src/WindowsAppManagerMcp/Services/BrowserReadingService.cs:226` | r2 | open |
| D-BRW-90 | low | Browser | BrowserReadingService tests depend on whatever real process owns PID 1000 | `tests/WindowsAppManagerMcp.Tests/Unit/Services/BrowserReadingServiceTests.cs:456` | later review | fixed |
| D-INP-11 | low | Input | Clicks are not atomic (SetCursorPos, sleep, then SendInput) and the landing point and double-click timing are not verified | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:190` | both | open |
| D-INP-12 | low | Input | Partial SendInput insertion can leave modifier keys logically held | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:293` | both | open |
| D-INP-13 | low | Input | Virtual-key events always carry scan code 0 | `src/WindowsAppManagerMcp/Native/NativeStructs.cs:178` | both | open |
| D-INP-14 | low | Input | send_keys injects the whole sequence in one burst with no pacing | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:291` | r1 | open |
| D-INP-15 | low | Input | type_text is unbounded and uncancellable, and echoes the full text back | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:234` | r2 | open |
| D-INP-16 | low | Input | Input error codes are misleading or unused | `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:306` | r2 | open |
| D-MCP-90 | low | MCP surface & project | DependencyInjectionTests claimed to mirror Program.cs but had drifted (8 types missing) | `tests/WindowsAppManagerMcp.Tests/Integration/DependencyInjectionTests.cs:27` | later review | fixed |
| D-PAL-19 | low | Launch & layouts | Launch/kill error codes are inaccurate: the AccessDenied branch is unreachable and kill success is not confirmed | `src/WindowsAppManagerMcp/Services/ProcessService.cs:342` | both | fixed |
| D-PAL-20 | low | Launch & layouts | list_processes(includeWindowless:true) reports WindowCount=0 for every process | `src/WindowsAppManagerMcp/Services/ProcessService.cs:114` | both | open |
| D-PAL-21 | low | Launch & layouts | kill_process acts on a bare PID with no identity check (PID reuse) | `src/WindowsAppManagerMcp/Services/ProcessService.cs:288` | both | fixed |
| D-PAL-22 | low | Launch & layouts | kill_process protected list omits shell-infrastructure processes and the agent's own host | `src/WindowsAppManagerMcp/Services/ProcessService.cs:226` | r2 | partly fixed |
| D-PAL-23 | low | Launch & layouts | Launch waits are synchronous Thread.Sleep loops with no timeout cap or cancellation; batch waits run sequentially and drop ErrorCode | `src/WindowsAppManagerMcp/Tools/AppLauncherTools.cs:30` | both | open |
| D-PAL-24 | low | Launch & layouts | file:// is allowed as a 'safe' protocol, bypassing every executable check | `src/WindowsAppManagerMcp/Services/InputValidationService.cs:37` | r1 | fixed |
| D-PAL-25 | low | Launch & layouts | apply_layout ignores the result of FocusWindow for focus placements | `src/WindowsAppManagerMcp/Services/LayoutService.cs:260` | r2 | open |
| D-SCR-10 | low | Screenshots | The undefined high byte of GDI pixels is copied into an ARGB bitmap, producing transparent PNG pixels | `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:560` | both | open |
| D-SCR-11 | low | Screenshots | Window capture has no hang protection: PrintWindow runs synchronously against the target's UI thread | `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:130` | r1 | open |
| D-SCR-12 | low | Screenshots | Frame captures include the invisible DWM resize borders | `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:78` | r1 | open |
| D-SCR-13 | low | Screenshots | Large captures triple-copy pixels and the 16384 cap still allows multi-GB allocations | `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:188` | both | open |
| D-SCR-14 | low | Screenshots | GetDIBits is called while the bitmap is still selected into the memory DC | `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:49` | both | open |
| D-SCR-15 | low | Screenshots | Annotation rectangles are too wide/tall for elements extending above or left of the image | `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:639` | r2 | open |
| D-SCR-16 | low | Screenshots | capture_region silently returns black for off-screen areas and reports the nearest monitor | `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:122` | r2 | open |
| D-SCR-17 | low | Screenshots | capture_with_elements combines window bounds, UIA bounds and pixels taken at different times | `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:149` | r2 | open |
| D-SCR-18 | low | Screenshots | Dead encode/save paths, undisposed EncoderParameters and silent format fallback | `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:360` | r1 | open |
| D-UIA-09 | low | UI Automation | Empty bounding rectangles serialize as int.MinValue coordinates | `src/WindowsAppManagerMcp/Services/UIElementService.cs:304` | both | open |
| D-UIA-10 | low | UI Automation | maxDepth is off by one (N returns N+1 levels) and truncation is not reported | `src/WindowsAppManagerMcp/Services/UIElementService.cs:340` | both | open |
| D-UIA-11 | low | UI Automation | A single vanished sibling silently drops all remaining siblings in the traversal | `src/WindowsAppManagerMcp/Services/UIElementService.cs:249` | both | fixed |
| D-UIA-12 | low | UI Automation | Unknown controlTypes names are silently accepted and return empty success | `src/WindowsAppManagerMcp/Tools/UIElementTools.cs:53` | r2 | open |
| D-WIN-16 | low | Windows & monitors | bringToFront (default true in snap) activates the window despite SWP_NOACTIVATE, making keyboard focus nondeterministic | `src/WindowsAppManagerMcp/Services/WindowService.cs:124` | both | open |
| D-WIN-17 | low | Windows & monitors | set_window_state('restore'/'normal') on a window minimized from maximized returns it maximized | `src/WindowsAppManagerMcp/Services/WindowService.cs:177` | r2 | open |
| D-WIN-18 | low | Windows & monitors | DPI awareness is per-monitor v1 via API call with the HRESULT ignored and no manifest | `src/WindowsAppManagerMcp/Program.cs:13` | both | open |
| D-WIN-19 | low | Windows & monitors | Snap tests exercise a copied algorithm; MonitorService has no test seam | `tests/WindowsAppManagerMcp.Tests/Unit/Services/MonitorServiceTests.cs:145` | both | open |
| D-WIN-20 | low | Windows & monitors | Batch delayBetweenMs is unbounded and blocks via Thread.Sleep with no cancellation | `src/WindowsAppManagerMcp/Services/WindowService.cs:341` | r2 | open |

### Defect details

**D-BRW-01: launch_browser_with_debug cannot serialize its success result (nint? WindowHandle)** (critical, `src/WindowsAppManagerMcp/Tools/BrowserReadingTools.cs:138`, r2:BRW-1, r1:MCP-2, r2:MSP-2)

- Impact: On the normal path (browser started, window found) the tool returns an opaque error with no PID, port or profile dir; a retry fails with PortInUse or opens more browsers.
- Fix: Declare long? WindowHandle mapped via WindowHandle?.ToInt64() and add a contract test that serializes every tool return type with the MCP serializer options.
- M0: fixed on `m0-tool-contract`

**D-SCR-01: Screenshots are returned as base64 inside JSON text, not as MCP image content** (critical, `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:34`, r1:SCR-1, r2:SCR-1, r1:MCP-1, r2:MSP-1)

- Impact: The model cannot see the image at all, and a 1080p PNG becomes 1.4M+ characters that exceed Claude Code's MCP output cap (truncated or written to a file) or burn hundreds of thousands of tokens, breaking the entire visual loop.
- Fix: Return CallToolResult with an ImageContentBlock (base64, image/png or image/jpeg) plus a TextContentBlock of metadata JSON without ImageData, and IsError = !Success.

**D-BRW-02: CDP WebSocket calls have no timeout or cancellation** (high, `src/WindowsAppManagerMcp/Services/CdpService.cs:195`, r1:BRW-1, r2:BRW-3)

- Impact: On a tab with an alert/confirm/beforeunload dialog, a paused debugger or a hung renderer, Runtime.evaluate never replies and get_browser_content/get_browser_url hang forever, leaking the worker and socket.
- Fix: Make the browser tools async with the SDK CancellationToken plus a clamped timeoutMs linked into Connect/Send/Receive, and pass a timeout to Runtime.evaluate.
- M0: fixed on `m0-cdp-timeouts`

**D-BRW-03: CDP target selection uses title substring matching and can return another tab's or browser's data** (high, `src/WindowsAppManagerMcp/Services/CdpService.cs:145`, r1:BRW-2, r2:BRW-4)

- Impact: 'GitHub - Google Chrome'.Contains('Google') picks a google.com tab, duplicate tabs depend on /json order, and a handle from a different browser instance returns the debug browser's data, all with Success=true.
- Fix: Verify the port's owning PID matches the HWND's process, then bind HWND to target deterministically (Browser.getWindowForTarget / bounds match), failing with CdpTargetNotMatched instead of guessing.
- M0: fixed on `m0-cdp-timeouts`

**D-BRW-04: Debug browser starts with a fresh profile but does not suppress first-run UI** (high, `src/WindowsAppManagerMcp/Services/ProcessService.cs:470`, r1:BRW-4, r2:BRW-8)

- Impact: Every launch can show welcome/sign-in, default-browser and (EEA) search-engine-choice dialogs, the returned handle may be the first-run window, and the agent must click through them before the URL loads.
- Fix: Always add --no-first-run --no-default-browser-check --disable-search-engine-choice-screen (and Edge equivalents) and confirm the returned HWND shows the requested URL.

**D-INP-01: Keyboard and mouse input is not tied to a target window and never verifies which window received it** (high, `src/WindowsAppManagerMcp/Tools/InputSimulationTools.cs:79`, r1:INP-1, r2:INP-2, r1:MCP-4, r2:MSP-3)

- Impact: If focus_window was refused or focus moved (toast, dialog, user, the agent's terminal) between calls, '^a{DELETE}', '%{F4}' or a typed password lands in the wrong app while Success=true is returned.
- Fix: Add an optional windowHandle to all input tools; before SendInput ensure GetAncestor(GetForegroundWindow(), GA_ROOT) equals the target (focusing it via the lock workaround), fail with ForegroundMismatch otherwise, and report the receiving window.

**D-INP-02: InputSimulationService unit tests send real mouse/keyboard input, including Alt+F4, to the developer's desktop** (high, `tests/WindowsAppManagerMcp.Tests/Unit/Services/InputSimulationServiceTests.cs:567`, r1:INP-12, r2:INP-1, r1:MCP-5, r2:MSP-6)

- Impact: dotnet test (CLI, IDE or an agent verifying a change) clicks, pastes, deletes and sends Alt+F4 into the focused window, typically closing the terminal; the send_keys grammar has no isolated coverage.
- Fix: Land the fix-input-test-isolation work (INativeInputWrapper, 6c4648d), extend InputEvent with Dx/Dy/MouseData/Scan, and add table-driven parser tests asserting exact event sequences.
- M0: fixed on `m0-test-isolation`

**D-INP-03: Input blocked by UIPI/elevation or the secure desktop is not detected or diagnosed** (high, `src/WindowsAppManagerMcp/Native/NativeMethods.Input.cs:9`, r1:INP-5, r2:INP-3)

- Impact: Clicking or typing into Task Manager, an admin terminal, an installer or a UAC prompt silently does nothing while Success=true (or a generic 'SendInput failed') is returned, so the agent retries blindly.
- Fix: Pre-flight the target (foreground window for keys, WindowFromPoint for clicks), compare its token integrity level with the server's, read GetLastError on short SendInput counts, and return ElevatedTarget/DesktopUnavailable codes.

**D-MCP-01: The plugin runs a stale committed exe, and /commit and /update-plugin publish to paths the plugin never runs** (high, `.claude-plugin/plugin.json:24`, r1:MCP-3, r2:MSP-4)

- Impact: Marketplace users and the agent run a binary whose tool schemas differ from source and docs (e.g. no maxWidth/saveToFile), and /update-plugin reports success without refreshing the running binary.
- Fix: Use a single publish target (bin/) for /commit, /update-plugin and CI, delete dist/, publish-new/ and .claude-plugin/bin/, and keep one version source.
- M0: partly fixed on `m0-release-pipeline`

**D-MCP-02: Test suite drives the real desktop beyond input (launches Chrome/Notepad, attempts kills), runs unfiltered in CI/README, and the CI test job keeps failing** (high, `tests/WindowsAppManagerMcp.Tests/Unit/Services/ProcessServiceTests.cs:458`, r1:MCP-5, r2:MSP-6, r1:bro-m1)

- Impact: 'Unit' tests open real browser and Notepad windows that are never cleaned up, CI has failed 7 runs in a row so regressions like the nint serialization crash ship unnoticed, and the documented test command is unsafe to run.
- Fix: Put an IProcessLauncher seam behind ProcessService, tag remaining real-OS tests as Integration, and exclude them from default and CI runs.
- M0: fixed on `m0-test-isolation`

**D-PAL-01: Launching via a launcher stub or single-instance app (browsers with a URL) returns a dead PID and no window handle** (high, `src/WindowsAppManagerMcp/Services/ProcessService.cs:360`, r1:PAL-1, r2:PAL-1, r1:BRW-3, r2:BRW-10)

- Impact: launch_application('chrome', [url], waitForWindow:true) while Chrome runs waits the full timeout and returns WindowHandle=null with an exited PID, so find_windows(processId)/kill_process fail and the agent must guess which window is new.
- Fix: Snapshot top-level HWNDs before Process.Start, then wait (async) for a new visible unowned window from the launched PID or any process with the same image, returning its HWND/PID and a handedOff flag.

**D-PAL-02: Shell-metacharacter check rejects any path containing '(' or ')', so 'Program Files (x86)' apps and Edge debug launch always fail** (high, `src/WindowsAppManagerMcp/Services/InputValidationService.cs:14`, r1:PAL-2, r2:PAL-2, r2:BRW-2)

- Impact: No 32-bit app can be launched by full path and launch_browser_with_debug(browser:'edge') never succeeds on a standard install, with a misleading 'command injection' error.
- Fix: Drop the metacharacter check for FileName/WorkingDirectory (keep control chars and embedded quotes), validate with Path.GetFullPath, and start trusted BrowserPaths entries directly.
- M0: fixed on `m0-launch-and-kill`

**D-PAL-03: Layouts are stored in the versioned plugin install folder: lost on update and bundled samples never deployed** (high, `src/WindowsAppManagerMcp/Program.cs:49`, r1:PAL-3, r2:PAL-5, r1:MCP-8, r2:MSP-11)

- Impact: Saved layouts vanish after the next plugin version bump, documented examples like apply_layout('work-setup') are never found, and the constructor throws if the folder is read-only.
- Fix: Store presets under %LOCALAPPDATA%/WindowsAppManagerMcp/layouts (env override), migrate existing files on startup, and seed the bundled samples on first run.
- M0: fixed on `m0-layout-storage`

**D-PAL-04: A captured layout cannot restore more than one window per process: every placement hits the same window** (high, `src/WindowsAppManagerMcp/Services/LayoutService.cs:245`, r1:PAL-4, r2:PAL-4, r2:mcp-m1)

- Impact: Save-then-restore fails for browsers, Explorer, VS Code and Office (the most common multi-window apps) while reporting a full WindowsArranged count, so the agent tells the user it worked.
- Fix: Track already-assigned handles and pick the best unassigned candidate scored by captured title, className and ordinal.

**D-PAL-05: kill_process graceful mode escalates to a forced process-tree kill after 3 seconds** (high, `src/WindowsAppManagerMcp/Services/ProcessService.cs:303`, r1:PAL-5, r2:PAL-3, r1:MCP-7)

- Impact: Closing an editor with unsaved changes shows the save prompt and then kills it and all children 3 s later, losing the work (and every shell/build under a terminal or IDE) while the agent believes it chose the safe mode.
- Fix: When forceKill=false never escalate; return GracefulCloseTimedOut/blockedByPrompt with the prompt handle if the process is still running.
- M0: fixed on `m0-launch-and-kill`

**D-UIA-01: controlTypes filter discards matching descendants of non-matching containers and double-counts TotalElementCount** (high, `src/WindowsAppManagerMcp/Services/UIElementService.cs:284`, r1:UIA-1, r2:UIA-1, r1:scr-m1, r2:SCR-4)

- Impact: get_ui_elements or capture_with_elements with controlTypes='Button,Edit' (the documented workflow) returns an almost empty list/overlay because controls sit inside Pane/ToolBar/Group, so the agent concludes the control does not exist.
- Fix: When a node fails only the type filter, splice its matching descendants into the parent's list, count only included elements, and add tests over a fake tree.

**D-UIA-02: A null ControlType (UWP/WinUI AppBar, SemanticZoom) or any non-ElementNotAvailable exception on one element aborts the whole traversal** (high, `src/WindowsAppManagerMcp/Services/UIElementService.cs:369`, r1:UIA-2, r2:UIA-3)

- Impact: get_ui_elements and capture_with_elements fail completely ('Value cannot be null') on any window with a CommandBar or SemanticZoom, and one bad Chromium/Electron node discards hundreds of good elements.
- Fix: Null-guard GetControlTypeName (fall back to LocalizedControlType or the raw id) and catch Exception per element, skipping the node and recording it in a Warnings/SkippedElements field.
- M0: fixed on `m0-concurrency-and-uia-crash`

**D-WIN-01: focus_window has no foreground-lock workaround and never verifies focus changed** (high, `src/WindowsAppManagerMcp/Services/WindowService.cs:196`, r1:WC-1, r2:WC-1)

- Impact: The server is a background stdio child of the agent host, so SetForegroundWindow is usually refused (taskbar flashes) yet focus_window reports success. The skill tells the agent to focus then type, so keystrokes land in the agent's own terminal or another app.
- Fix: Add an EnsureForeground routine behind INativeWindowWrapper (restore if iconic, SetForegroundWindow, then AttachThreadInput/BringWindowToTop or an Alt-tap fallback) and return success only if GetForegroundWindow's root equals the target.

**D-WIN-02: Geometry operations never un-maximize (and move/resize ignore minimized windows)** (high, `src/WindowsAppManagerMcp/Services/WindowService.cs:151`, r1:WC-3, r2:WC-4, r2:mcp-m2)

- Impact: snap_window/set_window_bounds on a maximized browser or IDE leaves WS_MAXIMIZE set, so the window is reported 'maximized' with half-screen bounds and jumps back on the next restore; move/resize on a minimized window return success with no visible effect.
- Fix: Add a shared PrepareForGeometryChange step that, when IsIconic or IsZoomed, uses SetWindowPlacement(showCmd=SW_SHOWNORMAL, rcNormalPosition=target) (or SW_RESTORE up to twice) before SetWindowPos.

**D-WIN-03: move_window_to_monitor 'maximize' maximizes on the current monitor and other modes do not fit the window to the target** (high, `src/WindowsAppManagerMcp/Services/WindowService.cs:268`, r1:WC-2, r2:WC-2, r2:MSP-8)

- Impact: 'Move app to monitor 2 and maximize' returns success while the window stays maximized on the original monitor; a large window moved from 4K to 1080p overflows the target with its title bar above the work area, so it cannot be dragged back.
- Fix: Restore first, place rcNormalPosition inside the target work area (clamped and DPI-scaled) via SetWindowPlacement/SetWindowPos, then SW_MAXIMIZE, and verify MonitorFromWindow equals the target.

**D-WIN-04: Window listings (and save_layout) include cloaked, other-virtual-desktop, tool and shell windows** (high, `src/WindowsAppManagerMcp/Services/WindowService.cs:25`, r1:WC-4, r2:WC-3, r1:PAL-14, r2:pro-m1)

- Impact: get_all_windows/find_windows return invisible suspended UWP frames, 'Windows Input Experience', other-desktop windows and 'Program Manager'; actions on them 'succeed' invisibly, and save_layout/apply_layout capture and move these phantom windows (including resizing the desktop host).
- Fix: Add IsCloaked (DWMWA_CLOAKED) to INativeWindowWrapper and by default exclude cloaked windows, WS_EX_TOOLWINDOW without WS_EX_APPWINDOW, and Progman/WorkerW/Shell_TrayWnd, with includeCloaked/includeShell opt-ins.

**D-BRW-05: Silent fallback from CDP to UIA with no indication of the method used; textOnly=false is ignored** (medium, `src/WindowsAppManagerMcp/Services/BrowserReadingService.cs:201`, r1:BRW-6, r2:BRW-7)

- Impact: An agent passing cdpPort for rendered HTML silently gets UIA text or a UIA error (e.g. UrlBarNotFound) with no sign CDP failed or why.
- Fix: Add Source ('cdp'|'uia'), format and CdpError fields plus a mode parameter (auto|cdp|uia) that returns explicit CDP error codes in cdp mode.
- M0: fixed on `m0-cdp-timeouts`

**D-BRW-06: UIA address-bar lookup is English-only, its fallback fails on wide/high-DPI windows, and it can return non-URL text** (medium, `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:111`, r1:BRW-7, r2:BRW-6, r1:UIA-12, r2:UIA-11)

- Impact: On a localized browser maximized on 4K the omnibox (>2000 px) is skipped (UrlBarNotFound), 'localhost:3000' fails, and the heuristic can return a page form value or a label like 'Search or enter address' as the URL with Success=true.
- Fix: Use locale-independent locators (document element value, OmniboxViewViews class, toolbar-scoped Edit outside any Document), validate candidates as URLs, and never fall back to Name.

**D-BRW-07: Browser URL name search matches the top-level window's own title, so a page title can be returned as the URL** (medium, `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:180`, r1:ui--m1, r2:ui--m1)

- Impact: A page whose <title> contains 'address and search bar' makes get_browser_url return attacker-chosen text as the URL with Success=true, which can mislead an agent verifying which site it is on before entering credentials.
- Fix: Exclude the root window and Document subtrees from the name search and validate the result as a URL.

**D-BRW-08: UIA URL is the omnibox display text, usually missing the scheme or showing in-progress typing** (medium, `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:279`, r1:BRW-8, r2:BRW-5)

- Impact: get_browser_url returns 'github.com/org/repo', so URL comparisons fail and passing it to launch_application treats it as a file path.
- Fix: Read the committed URL from the web Document element's value first, otherwise normalize and return Url plus DisplayUrl.

**D-BRW-09: get_browser_content is unbounded by default, has no truncation signal or paging, and ContentLength is post-truncation** (medium, `src/WindowsAppManagerMcp/Tools/BrowserReadingTools.cs:37`, r1:BRW-9, r2:BRW-11, r1:MCP-11, r1:mcp-m4)

- Impact: One call on an SPA dumps hundreds of KB to MB into context and exceeds client output limits; with maxLength set, the agent cannot tell content was cut or fetch the rest.
- Fix: Default maxLength to ~20,000 chars with a hard cap, add offset, and return TotalLength, Truncated and NextOffset.

**D-BRW-10: launch_browser_with_debug never checks the CDP endpoint opened, does not validate the port, and never cleans up temp profiles** (medium, `src/WindowsAppManagerMcp/Services/ProcessService.cs:503`, r1:BRW-10, r2:BRW-8, r1:pro-m1, r2:pro-m2)

- Impact: Success and a dead CdpPort are returned when policy disables debugging, a userDataDir hands off to a running instance, or Chrome 136+ ignores the flag; later reads fail confusingly and each launch leaves a full profile in %TEMP%.
- Fix: Validate the port, poll http://127.0.0.1:{port}/json/version until ready or fail with DebugLaunchFailed, return the browser WS endpoint, and delete temp profiles this tool created.

**D-BRW-11: Debug browser exposes an unauthenticated CDP port on the well-known 9222** (medium, `src/WindowsAppManagerMcp/Services/ProcessService.cs:472`, r2:BRW-13, r1:BRW-10)

- Impact: Any local process (e.g. infostealer malware scanning 9222) can attach, read cookies and run script in any origin the agent signs into, and the cookies persist in the leftover temp profile.
- Fix: Default to --remote-debugging-port=0, read the port from DevToolsActivePort in the user-data dir, and delete the profile on exit.

**D-BRW-12: --user-data-dir is quoted twice, so paths with spaces split into separate arguments** (medium, `src/WindowsAppManagerMcp/Services/ProcessService.cs:473`, r1:BRW-5, r2:BRW-9, r1:mcp-m3, r1:PAL-8, r2:PAL-7)

- Impact: For profiles under a user folder with a space (or Chrome's 'User Data'), Chrome uses a truncated or unwritable directory and opens the stray token as a URL, and CDP may attach to the wrong profile.
- Fix: Build the command line with ProcessStartInfo.ArgumentList and pass --user-data-dir=<path> without manual quotes.
- M0: fixed on `m0-launch-and-kill`

**D-BRW-13: Debug-launch url argument is not validated and can inject browser switches** (medium, `src/WindowsAppManagerMcp/Services/ProcessService.cs:473`, r1:BRW-5, r2:BRW-13)

- Impact: A 'URL' taken from untrusted page text (prompt injection) that starts with '-' or contains quoted switches is passed to the browser as command-line flags.
- Fix: Require Uri.TryCreate(Absolute) with an http/https/file/about scheme, reject values starting with '-', and insert '--' before the positional URL.
- M0: partly fixed on `m0-launch-and-kill`

**D-BRW-14: Browser UIA searches and content reads are unbounded (whole-tree Descendants scans, full-document GetText, no deadline)** (medium, `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:232`, r1:UIA-13, r2:ui--m3, r2:BRW-12)

- Impact: On heavy pages (long diffs, infinite feeds) get_browser_url/get_browser_content in UIA mode can block for tens of seconds or more and read the whole document even for a small maxLength, with no way to cancel.
- Fix: Pass a deadline/CancellationToken through IUIAutomationWrapper, scope URL searches to the toolbar subtree, use GetText(maxLength), and cap walks by node count and time.

**D-BRW-15: UIA content path does not wake Chromium's on-demand accessibility tree** (medium, `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:335`, r1:bro-m2, r2:bro-m1)

- Impact: The first get_browser_content on an untouched Chrome/Edge window returns empty content, ContentExtractionFailed or frame text as success while an identical second call works, so results are flaky.
- Fix: Trigger the renderer accessibility tree and retry briefly until the Document has children before extracting.

**D-BRW-16: UIA content extraction can return browser UI text or a side panel via the frame-pane fallback** (medium, `src/WindowsAppManagerMcp/Native/UIAutomationWrapper.cs:335`, r2:BRW-12)

- Impact: Success=true with tab titles, bookmarks or side-panel text instead of the page, which the agent cannot distinguish from real content.
- Fix: Select the active page's Document (on-screen, value equals the committed URL) and return ContentAreaNotFound instead of falling back to the frame pane.

**D-INP-04: send_keys shortcuts on punctuation/OEM keys (Ctrl+-, Ctrl+=, Ctrl+/, Ctrl+comma, Ctrl+backtick) are sent as Unicode packets and never fire** (medium, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:371`, r1:INP-2, r2:INP-4)

- Impact: Zoom, toggle comment, settings and VS Code terminal shortcuts do nothing or insert a stray character, while Success=true is returned.
- Fix: Map characters via VkKeyScanExW with the foreground thread's layout and send the real VK (plus required Shift) whenever a modifier is active; use KEYEVENTF_UNICODE only for plain text.

**D-INP-05: send_keys sends letters and digits as raw virtual keys, ignoring case, Caps Lock and keyboard layout** (medium, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:474`, r1:INP-3, r2:INP-5)

- Impact: send_keys('Hello{ENTER}') types 'hello', Caps Lock inverts case, and on AZERTY '1' types '&', silently corrupting form input, search terms and file names.
- Fix: Send unmodified characters as KEYEVENTF_UNICODE (as type_text does) and use case-insensitive VK mapping plus VkKeyScanExW only for modifier chords.

**D-INP-06: No Win modifier, and {CTRL}/{ALT}/{SHIFT}/{WIN} in braces are tapped rather than held** (medium, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:359`, r1:INP-4, r2:INP-6)

- Impact: Win+R, Win+D, Win+E, Win+Shift+S and Win+Arrow cannot be sent: '{WIN}r' opens Start and searches 'r', and '{CTRL}c' taps Ctrl then types 'c', both with Success=true.
- Fix: Add a '#' Win modifier prefix (AutoHotkey convention) and '{KEY down}'/'{KEY up}' tokens, tracking held keys and releasing them at the end.

**D-INP-07: send_keys implements only part of the SendKeys grammar: no escapes, grouping, repeat counts or '~'** (medium, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:327`, r1:INP-4, r2:INP-7)

- Impact: send_keys('100%{ENTER}') sends Alt+Enter, 'C++' types 'c' plus Shift taps, '~' and '(...)' are typed literally, and literal + ^ % { } cannot be sent; LLMs trained on .NET SendKeys get silent wrong behavior.
- Fix: Replace the parser with a tokenizer supporting {+} {^} {%} {~} {(} {)} {{} {}} escapes, +(abc) groups, {LEFT 5} repeats, '~' as Enter and NUMPAD/F13-F24/media keys, returning InvalidKeySpecification on errors.

**D-INP-08: type_text sends Tab, CR and LF as Unicode packets instead of real Tab/Enter key presses** (medium, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:238`, r1:INP-9, r2:INP-8)

- Impact: type_text('user<TAB>pass<LF>') does not move between dialog fields or submit (IsDialogMessage and default buttons look for VK_TAB/VK_RETURN), and CRLF can double line breaks in editors, all with Success=true.
- Fix: Normalize CRLF/CR to LF and send LF as VK_RETURN, Tab as VK_TAB and Backspace as VK_BACK by default, with a controlChars option to keep literal packets.

**D-INP-09: Input operations are not serialized across concurrent MCP requests** (medium, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:234`, r1:INP-7)

- Impact: Parallel type_text calls interleave into garbled text, and a send_keys('^a') holding Ctrl can turn another call's characters into shortcuts.
- Fix: Guard every input operation with one process-wide SemaphoreSlim.
- M0: fixed on `m0-concurrency-and-uia-crash`

**D-INP-10: Clicks ignore swapped mouse buttons** (medium, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:204`, r1:INP-11, r2:INP-10)

- Impact: On a left-handed setup 'click' opens context menus and 'right_click' activates items, so an inspecting right-click can open a file or press a button.
- Fix: Read GetSystemMetrics(SM_SWAPBUTTON) per click and swap LEFT/RIGHT flags so 'click' always means the primary button.

**D-MCP-03: Agent-facing docs drift from the registered tools: wrong tool count, browser tools undocumented, wrong parameter names** (medium, `skills/using-windows-app-manager/SKILL.md:73`, r1:MCP-10, r2:MSP-7, r2:PAL-17, r1:BRW-15, r2:BRW-15)

- Impact: Agents following SKILL.md pass nonexistent parameters (path, pid), never discover get_browser_content/get_browser_url and fall back to screenshots, and learn manual wait-and-poll workarounds.
- Fix: Generate tool tables in README/TOOLS.md/SKILL.md from tools/list output, fix parameter names, and add a browser and coordinate-space section.
- M0: fixed on `m0-docs-safety`

**D-MCP-04: The exe requires the .NET 8 Windows Desktop Runtime but docs only ask for the .NET 8 Runtime** (medium, `src/WindowsAppManagerMcp/WindowsAppManagerMcp.csproj:11`, r1:MCP-12, r2:MSP-15)

- Impact: On a machine with only the base runtime the MCP server exits at startup and the agent simply has no tools, with no actionable error.
- Fix: Publish the plugin exe self-contained or document the .NET 8 Desktop Runtime (x64) prerequisite with a link.
- M0: fixed on `m0-release-pipeline`

**D-MCP-05: Errors surface as generic SDK errors, empty results or stripped messages; one malformed batch item aborts the whole batch** (medium, `src/WindowsAppManagerMcp/Tools/LayoutPresetTools.cs:39`, r1:MCP-14, r2:MSP-9)

- Impact: The agent cannot tell 'not found' from a broken call or which batch item/field was wrong, so it retries blindly or gives up.
- Fix: Return explicit {found:false, errorCode} objects instead of null, validate batch items individually with per-item errors, and include exception details in error results.
- M0: partly fixed on `m0-tool-contract`

**D-MCP-06: The GitHub release job has never succeeded (read-only token)** (medium, `.github/workflows/build.yml:42`, r2:MSP-5)

- Impact: No downloadable releases exist, so README 'Download Release' and the /commit release URL 404 and non-plugin clients have no supported binary.
- Fix: Add permissions: contents: write to the release job, move to softprops/action-gh-release@v2, and version the assets.
- M0: fixed on `m0-release-pipeline`

**D-MCP-07: Committed .claude/settings.local.json grants broad no-prompt permissions and local artifacts are tracked** (medium, `.claude/settings.local.json:63`, r2:MSP-14, r1:MCP-5)

- Impact: Anyone who clones the repo inherits allow rules letting an agent run arbitrary PowerShell, curl, taskkill and the desktop-driving tests without a prompt, and release commits pick up local artifacts.
- Fix: git rm --cached settings.local.json and the tmpclaude-* files, put a minimal shared allow-list in .claude/settings.json, and replace git add -A in /commit with explicit paths.
- M0: partly fixed on `m0-test-isolation`

**D-PAL-06: Layout matching can target arbitrary windows: empty matchers/title_only match everything, unknown matchBy is accepted, ClassName ignored** (medium, `src/WindowsAppManagerMcp/Services/LayoutService.cs:313`, r1:PAL-6, r2:PAL-10)

- Impact: apply_layout(matchBy:'title_only') on a captured layout repeatedly resizes unrelated windows such as the agent's own terminal or IDE while reporting N windows arranged.
- Fix: Treat a matcher with no usable criterion as no match (reported per placement), validate matchBy as an enum, and honor ClassName.

**D-PAL-07: Protocol policy blocks legitimate targets (ms-settings:, shell:AppsFolder) yet allows cmd/powershell with arbitrary arguments** (medium, `src/WindowsAppManagerMcp/Services/InputValidationService.cs:17`, r1:PAL-7)

- Impact: 'Open display settings' or launching a Store app is impossible, so the agent falls back to powershell Start-Process, which is allowed; the block adds friction without security.
- Fix: Allow ms-settings: and shell:AppsFolder/<AUMID> by default plus a configurable protocol allow-list, while still blocking script schemes.

**D-PAL-08: launch_application joins arguments without proper Windows quoting** (medium, `src/WindowsAppManagerMcp/Services/ProcessService.cs:70`, r1:PAL-8, r2:PAL-7)

- Impact: Arguments with trailing backslashes, embedded quotes, spaces or empty strings are mangled, so the app opens the wrong path or nothing while Success is reported.
- Fix: Use ProcessStartInfo.ArgumentList (or PasteArguments-style escaping).
- M0: fixed on `m0-launch-and-kill`

**D-PAL-09: Process.Start returning null (ShellExecute handed off to an existing instance) is reported as failure** (medium, `src/WindowsAppManagerMcp/Services/ProcessService.cs:75`, r1:PAL-9, r2:PAL-12)

- Impact: Opening a document or URL works but returns ProcessStartFailed, so the agent retries and opens duplicates or tells the user it failed.
- Fix: Return Success=true with ProcessId=null and a 'handled by existing instance' status, then locate the new window via the HWND-diff logic.

**D-PAL-10: Layouts identify monitors only by enumeration index and silently fall back to monitor 0** (medium, `src/WindowsAppManagerMcp/Services/LayoutService.cs:246`, r1:PAL-10, r2:PAL-8)

- Impact: After undocking, placements for monitors 1-2 stack on monitor 0; after re-docking in a different order windows swap monitors, all reported as success.
- Fix: Save monitor identity (DeviceName, IsPrimary, Bounds, DPI) and resolve by DeviceName, then primary, then nearest, reporting unresolved placements.

**D-PAL-11: apply_layout ignores window state and mixed DPI** (medium, `src/WindowsAppManagerMcp/Services/LayoutService.cs:254`, r1:PAL-11, r2:PAL-8)

- Impact: Maximized windows are moved while still maximized (half-size but flagged maximized) and windows crossing 100%/150% monitors end up the wrong size, never verified.
- Fix: Capture showCmd and the normal rect with GetWindowPlacement, restore before placing, apply via SetWindowPlacement, and verify the final rect.

**D-PAL-12: save_layout and delete_layout report success when nothing was saved or deleted** (medium, `src/WindowsAppManagerMcp/Tools/LayoutPresetTools.cs:83`, r1:PAL-12, r2:PAL-6, r1:MCP-8, r2:MSP-10, r1:MCP-14, r1:PAL-16)

- Impact: After a failed write the agent tells the user 'layout saved' and the next apply returns not found; delete_layout('typo') reports success.
- Fix: Propagate SavePresetAsync failures as SaveFailed, warn on WindowCount==0, and return PresetNotFound from delete.
- M0: fixed on `m0-layout-storage`

**D-PAL-13: apply_layout returns only counts, never verifies placement, and reports success with zero matches** (medium, `src/WindowsAppManagerMcp/Services/LayoutService.cs:272`, r1:PAL-12, r2:PAL-11, r2:MSP-10)

- Impact: apply_layout with none of the apps open returns success with windowsArranged=0, and the agent cannot tell which apps are missing or misplaced without extra get_all_windows round trips.
- Fix: Return per-placement entries (status arranged/notFound/launched/launchFailed, handle, requested vs actual rect) and Success=false or Partial when nothing or not everything matched.
- M0: fixed on `m0-layout-storage`

**D-PAL-14: launchMissing cannot work for agent-saved layouts: no launch command is captured and commands cannot carry arguments** (medium, `src/WindowsAppManagerMcp/Services/LayoutService.cs:220`, r1:PAL-13, r2:PAL-9)

- Impact: 'Restore my layout and open anything missing' launches nothing for saved layouts, and slow or splash-screen apps in hand-written presets report 'Failed to launch'.
- Fix: Capture GetProcessPath (and AUMID/command line) into LaunchCommand with LaunchArguments/WorkingDirectory, and wait for the new window via the HWND-diff logic.

**D-PAL-15: apply_layout throws NullReferenceException when a freshly launched window is untitled or already closed** (medium, `src/WindowsAppManagerMcp/Services/LayoutService.cs:227`, r1:win-m2, r1:PAL-13, r2:PAL-9)

- Impact: apply_layout(launchMissing:true) fails as an SDK tool error and loses the counts for placements already processed, leaving the desktop half-arranged with no record of what moved.
- Fix: Null-check the launched window info and record the placement as launchFailed/notFound instead of throwing.

**D-PAL-16: LayoutService preset cache is not thread-safe and get_layout/apply_layout never reload from disk** (medium, `src/WindowsAppManagerMcp/Services/LayoutService.cs:68`, r1:PAL-16, r1:mcp-m1, r2:pro-m4, r1:MCP-13)

- Impact: Preset files added or edited on disk are 'not found' or stale until list_layouts runs, so results depend on call order; parallel calls can throw 'Collection was modified'.
- Fix: Use a ConcurrentDictionary or lock and reload (or check file timestamps) in GetPreset.
- M0: fixed on `m0-layout-storage`

**D-PAL-17: Bare executable names are resolved against the server's current directory first (binary planting)** (medium, `src/WindowsAppManagerMcp/Services/ProcessService.cs:65`, r1:pro-m2)

- Impact: In an untrusted repository containing notepad.exe or code.cmd, launch_application('notepad') or a layout launchCommand runs the planted file, and launched apps inherit the repo as working directory.
- Fix: Resolve bare names via App Paths and PATH only (excluding CWD) and default WorkingDirectory to the user profile.
- M0: partly fixed on `m0-launch-and-kill`

**D-PAL-18: Arguments passed to batch-file shims (e.g. code.cmd) let cmd.exe interpret '&', '|' and %VAR%** (medium, `src/WindowsAppManagerMcp/Services/ProcessService.cs:70`, r1:pro-m3)

- Impact: launch_application('code', [a path containing 'R&D']) opens the wrong path and tries to run the remainder as a command; a path from untrusted content can execute commands.
- Fix: Detect .cmd/.bat targets and apply cmd.exe escaping (caret-escape metacharacters, reject %), or resolve the shim to its underlying executable.
- M0: fixed on `m0-launch-and-kill`

**D-SCR-02: capture_with_elements with includeFrame=false offsets every overlay and ImageBounds by the frame/title-bar size** (medium, `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:216`, r1:UIA-5, r1:SCR-2, r2:UIA-9, r2:SCR-3, r2:mcp-m4)

- Impact: Boxes are drawn ~8 px right and ~31 px down (more at high DPI) of their controls and edge elements are clipped, so an agent reasoning from the image or ImageBounds targets the wrong control.
- Fix: Have IScreenCaptureWrapper.CaptureWindow return the actual screen origin (GetWindowRect or ClientToScreen(hwnd,{0,0})) and compute overlays/ImageBounds from it.

**D-SCR-03: capture_with_elements is all-or-nothing: any UIA failure or timeout drops the screenshot** (medium, `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:197`, r1:UIA-7, r2:MSP-12, r2:SCR-8, r2:UIA-2)

- Impact: On heavy windows (browsers, Electron, Office) where visual grounding matters most, a UIA timeout or crash returns neither overlays nor the plain image, costing an extra capture_window call.
- Fix: Always capture the image and return it with Elements=[] or partial overlays plus ElementsError/Truncated fields when retrieval fails.

**D-SCR-04: Window captures give no screen origin or image-to-screen transform** (medium, `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:185`, r1:SCR-3, r2:SCR-5, r2:MSP-13)

- Impact: capture_window reports origin (0,0), so an agent clicking what it sees (especially in client-area or maxWidth-scaled captures) is off by the frame offset, DPI and downscale factor and misclicks.
- Fix: Return the true screen origin in CapturedRegion plus an explicit ImageToScreen {OffsetX, OffsetY, ScaleX, ScaleY}.

**D-SCR-05: Capturing a minimized, hidden or cloaked window succeeds with a tiny or black image** (medium, `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:76`, r1:SCR-4, r2:SCR-7)

- Impact: An app that starts minimized or sits on another virtual desktop yields Success=true with a 160x28 or black image, so the agent concludes the UI is broken or acts blindly.
- Fix: Check IsIconic, IsWindowVisible and DWMWA_CLOAKED before capturing and return WindowMinimized/WindowHidden/WindowCloaked, or offer opt-in restoreIfMinimized.

**D-SCR-06: outputPath allows unvalidated writes anywhere, overwriting existing files** (medium, `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:405`, r1:SCR-8, r2:SCR-9, r1:mcp-m2)

- Impact: A mis-filled or prompt-injected outputPath (e.g. a .docx or source file) is silently replaced with image bytes through a tool that looks read-only, bypassing the client's file-write permissions; relative paths land in an unknown CWD.
- Fix: Require an absolute path with an extension matching the format, refuse to overwrite unless overwrite=true, return the full path, and optionally restrict writes to a screenshot root.

**D-SCR-07: Defaults produce full-resolution lossless PNGs and maxWidth limits only width** (medium, `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:46`, r2:SCR-2)

- Impact: Even once returned as image content, a 4K capture is 5-15 MB, exceeding per-image API limits (5 MB) and being downscaled client-side anyway, wasting bandwidth and tokens.
- Fix: Add maxDimension applied to the long edge, default it to about 1568, and consider JPEG ~80 as the default for monitor/window captures.

**D-SCR-08: Element overlays are drawn at full resolution before the maxWidth downscale, making them faint or invisible** (medium, `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:515`, r2:scr-m1)

- Impact: maxWidth plus JPEG (the documented way to keep captures affordable) yields overlays a vision model can barely see, defeating capture_with_elements.
- Fix: Downscale first and draw overlays on the scaled image with scaled coordinates and a minimum stroke/label size.

**D-SCR-09: DPI-unaware target windows may be rendered by PrintWindow at logical size inside a physical-size bitmap (plausible, not reproduced)** (medium, `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:78`, r1:scr-m2)

- Impact: On 125-150% monitors legacy apps could appear in the top-left of a partly black image, with UIA overlays (physical coordinates) misaligned, so the agent mis-targets by vision.
- Fix: Verify on a scaled monitor with a DPI-unaware app and, if confirmed, fall back to a screen-DC BitBlt of the window rect or scale by the window's DPI.

**D-UIA-03: Traversal timeout discards the partially built tree** (medium, `src/WindowsAppManagerMcp/Services/UIElementService.cs:164`, r1:UIA-3, r2:UIA-2)

- Impact: On large Chromium/Electron/Office trees the walk exceeds timeoutMs and the agent gets zero elements despite thousands already read, then retries at lower depth and pays the full cost again.
- Fix: Return the partial tree with Success=true, Truncated=true and a TruncationReason, traversing breadth-first so shallow levels survive a cut-off.

**D-UIA-04: timeoutMs is not a hard bound: a blocked UIA call or hung window holds the request and MCP cancellation is ignored** (medium, `src/WindowsAppManagerMcp/Services/UIElementService.cs:223`, r1:UIA-3, r2:UIA-5)

- Impact: Pointing get_ui_elements at a 'Not Responding' app blocks the tool far past timeoutMs and the client cannot cancel it.
- Fix: Check IsHungAppWindow/SendMessageTimeout(WM_NULL) first (WindowNotResponding), run the walk on a worker awaited with WaitAsync(deadline), and link the SDK CancellationToken with the timeout.

**D-UIA-05: No CacheRequest: about 10 uncached cross-process calls per element with duplicate reads** (medium, `src/WindowsAppManagerMcp/Services/UIElementService.cs:278`, r1:UIA-4, r2:UIA-4)

- Impact: Tree retrieval is roughly an order of magnitude slower than a cached fetch, driving the timeouts and producing inconsistent snapshots as elements change between property reads.
- Fix: Build one CacheRequest for the needed properties and pattern-availability flags and fetch children per level with FindAll(TreeScope.Children) under it.

**D-UIA-06: visibleOnly/interactableOnly/minWidth/minHeight prune whole subtrees, and interactableOnly only checks IsEnabled** (medium, `src/WindowsAppManagerMcp/Services/UIElementService.cs:314`, r1:UIA-8, r2:UIA-6)

- Impact: minWidth drops every button inside a zero-size or narrow container, and interactableOnly=true still returns all labels and panes, so output size and contents are unpredictable.
- Fix: Apply node filters as include/exclude while still recursing (hoisting qualifying descendants), and define interactable as enabled, on-screen and focusable or supporting an action pattern.

**D-UIA-07: Minimized or elevated target windows return an empty tree as Success with no diagnostic** (medium, `src/WindowsAppManagerMcp/Services/UIElementService.cs:151`, r1:UIA-9, r2:UIA-8)

- Impact: The agent gets success with zero elements and concludes the app has no UI instead of restoring the window or asking for elevation.
- Fix: Check IsIconic/cloaking and the target's integrity level up front and return WindowMinimized/ElevatedTarget (or a Warnings entry), optionally with an autoRestore parameter.

**D-UIA-08: UIElementService has no test seam, so filter tests never reach the traversal code** (medium, `src/WindowsAppManagerMcp/Services/UIElementService.cs:112`, r2:UIA-12)

- Impact: Traversal, filter and depth bugs (the controlTypes and maxDepth defects) shipped despite 15+ filter tests, and fixes to them cannot be regression-tested.
- Fix: Introduce an IAutomationTree abstraction with a UIA implementation and an in-memory fake, inject it, and test traversal against fake trees.

**D-WIN-05: Untitled windows are invisible to GetWindowInfo, so get_foreground_window, post-action re-queries and capture_with_elements fail on them** (medium, `src/WindowsAppManagerMcp/Services/WindowService.cs:466`, r1:WC-13, r2:WC-8, r2:mcp-m5, r1:UIA-6, r2:UIA-10, r1:SCR-6, r2:SCR-8)

- Impact: get_foreground_window returns null when a menu, taskbar or untitled popup has focus (indistinguishable from no window), move/resize return NewBounds=null, and capture_with_elements rejects untitled popups/menus/splash windows as 'invalid handle' even though capture_window works.
- Fix: Split an unfiltered ReadWindowInfo(hWnd) (empty title plus className allowed) from the listing predicate, use it for GetWindowInfo/GetForegroundWindow/capture_with_elements, and return WindowNotFound only when IsWindow is false.

**D-WIN-06: MonitorService singleton clears and rebuilds a shared List on every call with no locking** (medium, `src/WindowsAppManagerMcp/Services/MonitorService.cs:18`, r1:WC-11, r2:WC-10, r1:SCR-11, r2:scr-m3, r2:inp-m1, r1:INP-7, r1:MCP-13)

- Impact: The SDK dispatches requests concurrently, so parallel calls (get_all_windows with get_monitors, a click's coordinate validation, a screenshot) can throw 'Collection was modified', see an empty list (spurious CoordinatesOutOfBounds / monitor index out of range) or capture the wrong monitor; the list is also rebuilt once per enumerated window.
- Fix: Build a new immutable monitor list and publish it with an atomic reference swap (or lock), and take one snapshot per public call instead of refreshing per window.
- M0: fixed on `m0-concurrency-and-uia-crash`

**D-WIN-07: Invalid monitorIndex, positioning and state values are silently coerced to defaults and reported as success** (medium, `src/WindowsAppManagerMcp/Services/MonitorService.cs:113`, r1:WC-8, r1:WC-9, r2:WC-7, r1:MCP-6, r1:MCP-9, r2:MSP-8)

- Impact: snap_window(monitorIndex:2) on a 2-monitor setup snaps on the primary, positioning:'left' centers, and set_window_state('minimise'/'hide'/'close') restores the window, all with Success=true, so the agent never learns its input was wrong.
- Fix: Validate every index and enum-like string (or make them C# enums so the schema lists values) and return InvalidMonitorIndex/InvalidPositioning/InvalidState with the allowed values.

**D-WIN-08: set_window_state reports ShowWindow's previous-visibility flag as Success** (medium, `src/WindowsAppManagerMcp/Services/WindowService.cs:181`, r1:WC-9, r2:WC-5, r2:mcp-m3)

- Impact: Every call on a visible window reports success even when nothing changed or the call was blocked, while a successful change on a hidden window reports failure and the batch path omits its NewState, so batch Succeeded counts are wrong.
- Fix: Ignore ShowWindow's return value, poll IsIconic/IsZoomed/IsWindowVisible briefly after the call, set Success from observed==requested state, and always return NewState.

**D-WIN-09: close_window / close_windows_batch have no guard for shell or agent-host windows** (medium, `src/WindowsAppManagerMcp/Services/WindowService.cs:309`, r1:WC-12, r2:WC-11)

- Impact: A 'close everything except X' batch built from get_all_windows can WM_CLOSE 'Program Manager' (opens the Shut Down dialog) or the agent's own Windows Terminal, ending the session.
- Fix: Refuse shell classes (Progman, WorkerW, Shell_TrayWnd) and windows owned by the server's ancestor processes unless force=true, and flag such windows (isAgentHost) in WindowInfo.

**D-WIN-10: close_window reports success as soon as WM_CLOSE is queued** (medium, `src/WindowsAppManagerMcp/Services/WindowService.cs:309`, r2:WC-11)

- Impact: The window can stay open behind a 'Save changes?' prompt or because the app is hung, but the agent is told it closed and moves on.
- Fix: After posting WM_CLOSE, poll IsWindow up to a timeoutMs and return closed/stillOpen/promptShown (detect prompts via GetWindow(GW_ENABLEDPOPUP)).

**D-WIN-11: Native window failures return a bare success:false; GetLastError is never read and elevation/UIPI is never detected** (medium, `src/WindowsAppManagerMcp/Tools/WindowManagerTools.cs:40`, r1:WC-7, r2:WC-9, r2:MSP-9)

- Impact: Moving or closing an elevated window (Task Manager, admin terminal, installer) is blocked by UIPI and the agent gets no reason, so it retries or switches strategy instead of telling the user elevation is needed; exceptions are masked as a generic SDK error.
- Fix: Return (ok, win32Error) from mutating wrapper calls, map ERROR_ACCESS_DENIED to AccessDeniedElevated, add IsElevated to WindowInfo, and fill Error/ErrorCode on every failure.
- M0: partly fixed on `m0-tool-contract`

**D-WIN-12: Calls on hung ('Not Responding') windows can block a tool call indefinitely** (medium, `src/WindowsAppManagerMcp/Native/NativeWindowWrapper.cs:63`, r1:WC-10, r2:WC-9)

- Impact: Moving, snapping, minimizing or restoring a hung window, exactly when an agent tries to recover it, blocks inside SetWindowPos/ShowWindow until the app recovers or the client times out; batches stall at the hung item.
- Fix: Check IsHungAppWindow up front and return WindowNotResponding, otherwise use SWP_ASYNCWINDOWPOS/ShowWindowAsync and poll the result with a bounded timeout.

**D-WIN-13: UWP/packaged app windows are attributed to ApplicationFrameHost instead of the app process** (medium, `src/WindowsAppManagerMcp/Services/WindowService.cs:470`, r1:WC-5, r2:WC-12)

- Impact: find_windows(processName:'Calculator') finds nothing, launch_application cannot find UWP windows by PID, and save_layout stores 'ApplicationFrameHost' for every UWP window so apply_layout sends all those placements to the same window.
- Fix: For ApplicationFrameWindow, resolve the Windows.UI.Core.CoreWindow child's PID/process as appProcessId/appProcessName, keep hostProcessName, and add a className filter.

**D-WIN-14: Snap and reported bounds use GetWindowRect including DWM's invisible resize borders** (medium, `src/WindowsAppManagerMcp/Services/MonitorService.cs:120`, r1:WC-6, r2:WC-6)

- Impact: Snapped windows show ~7 px gaps at edges and ~14 px between halves (more at high DPI), unlike Windows Snap, and edge-relative click coordinates computed from Bounds are off by the border.
- Fix: Add GetExtendedFrameBounds (DWMWA_EXTENDED_FRAME_BOUNDS) to the wrapper, grow target rects by the frame insets before SetWindowPos, and report visible-frame Bounds (with OuterBounds separately).

**D-WIN-15: move_window_to_monitor measures the window before restoring it, shrinking minimized windows to their iconic size** (medium, `src/WindowsAppManagerMcp/Services/WindowService.cs:228`, r1:win-m1, r2:win-m1)

- Impact: Moving a minimized window to another monitor leaves a ~160x28 sliver (or a tiny maximized window) on the target and overwrites the user's normal size, while returning success.
- Fix: Read the restored size from GetWindowPlacement.rcNormalPosition (or restore first) before computing the target rectangle.

**D-BRW-17: Browser executable lookup misses per-user installs, App Paths and non-stable channels** (low, `src/WindowsAppManagerMcp/Services/ProcessService.cs:394`, r1:BRW-11)

- Impact: With a per-user Chrome/Brave install or only Beta/Canary, launch_browser_with_debug returns BrowserNotFound although the browser is installed.
- Fix: Resolve via App Paths (HKCU then HKLM), then ProgramFiles/ProgramFilesX86/LocalApplicationData paths including Beta/Dev/Canary.

**D-BRW-18: CDP reads use 4 HTTP requests and 2 WebSocket handshakes per call via 'localhost'** (low, `src/WindowsAppManagerMcp/Services/CdpService.cs:36`, r1:BRW-13, r2:BRW-14)

- Impact: Each CDP read pays seconds of avoidable latency (compounded by IPv6 fallback and 5 s timeouts when the port is down) and can mix URL and content from different navigations.
- Fix: Use 127.0.0.1, fetch /json once, and evaluate title/url/text in one WebSocket session.

**D-BRW-19: Browser detection suffix/prefix heuristics classify non-browsers as browsers and inject --new-window** (low, `src/WindowsAppManagerMcp/Services/BrowserDetectionService.cs:76`, r1:BRW-14, r2:bro-m2)

- Impact: launch_application of a portable non-browser tool with a URL gets an unknown '--new-window' switch, and get_browser_url on it returns UrlBarNotFound instead of NotABrowser.
- Fix: Apply suffix tiers only with a known browser family token and inject --new-window only for known Chromium/Firefox families.

**D-BRW-20: UIA content path always repeats the expensive URL lookup and attaches its possibly bogus value** (low, `src/WindowsAppManagerMcp/Services/BrowserReadingService.cs:226`, r2:bro-m3)

- Impact: get_browser_content is slower and can attach a wrong URL to correct content, so the agent records the page under the wrong address.
- Fix: Make the URL lookup optional for content reads or reuse a validated URL, and mark it unverified when heuristic.

**D-BRW-90: BrowserReadingService tests depend on whatever real process owns PID 1000** (low, `tests/WindowsAppManagerMcp.Tests/Unit/Services/BrowserReadingServiceTests.cs:456`, external-review:D4)

- Impact: Tests mock GetWindowThreadProcessId to return 1000, then the service calls the real Process.GetProcessById(1000), so results depend on the developer's machine.
- Fix: Move the process-name lookup behind a native wrapper seam and mock it.
- M0: fixed on `m0-hardening-tests`

**D-INP-11: Clicks are not atomic (SetCursorPos, sleep, then SendInput) and the landing point and double-click timing are not verified** (low, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:190`, r1:INP-6, r2:INP-11, r1:INP-11)

- Impact: A physical mouse move, concurrent mouse_move or ClipCursor during the gap redirects the click while the requested coordinates are reported; a double_click delayMs at or above the user's double-click time registers as two single clicks.
- Fix: Send one SendInput array (absolute VIRTUALDESK move, down, up; all four events for double-click), cap delayMs below GetDoubleClickTime(), and report GetCursorPos as ActualX/ActualY.

**D-INP-12: Partial SendInput insertion can leave modifier keys logically held** (low, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:293`, r1:INP-8, r2:inp-m2)

- Impact: If injection stops mid-batch (desktop switch, UAC, input block), Ctrl/Alt/Shift stay down so later clicks become Ctrl+clicks and typed text becomes shortcuts for both agent and user; pre-existing stuck modifiers are not detected.
- Fix: Track inserted modifiers and send compensating key-ups on partial insertion, and check GetAsyncKeyState for modifiers/buttons before each operation.

**D-INP-13: Virtual-key events always carry scan code 0** (low, `src/WindowsAppManagerMcp/Native/NativeStructs.cs:178`, r1:INP-10, r2:INP-9)

- Impact: RDP/Hyper-V/VMware/Citrix clients, raw-input games and frameworks deriving KeyboardEvent.code from scan codes misread or drop send_keys keystrokes while success is reported.
- Fix: Fill wScan with MapVirtualKeyExW(vk, MAPVK_VK_TO_VSC_EX) and set KEYEVENTF_EXTENDEDKEY for 0xE0-prefixed codes, optionally offering a scan-code-only mode.

**D-INP-14: send_keys injects the whole sequence in one burst with no pacing** (low, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:291`, r1:inp-m1)

- Impact: Sequences whose later keys depend on UI opened by earlier keys ('^l' then a URL, '%f' then a menu key) can arrive before that UI exists, forcing the agent to split calls and widen the focus-drift window.
- Fix: Add an optional per-chord delay or a '{WAIT n}' token.

**D-INP-15: type_text is unbounded and uncancellable, and echoes the full text back** (low, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:234`, r2:INP-12)

- Impact: A 10,000-character string with delayMs=100 types for ~17 minutes and keeps typing into whatever has focus after the client cancels; echoing the text doubles token cost.
- Fix: Make the tool async with the SDK CancellationToken, check it and the foreground window between chunks, cap length, and return a character count instead of the text.

**D-INP-16: Input error codes are misleading or unused** (low, `src/WindowsAppManagerMcp/Services/InputSimulationService.cs:306`, r2:INP-13)

- Impact: An agent branching on ErrorCode cannot tell a key-spec syntax error from an unknown key name, or a locked/secure desktop from an injection failure, leading to wrong retries.
- Fix: Map syntax errors to InvalidKeySpecification and unknown names to UnknownKey, add CursorPositionFailed/DesktopUnavailable, and remove unused codes.

**D-MCP-90: DependencyInjectionTests claimed to mirror Program.cs but had drifted (8 types missing)** (low, `tests/WindowsAppManagerMcp.Tests/Integration/DependencyInjectionTests.cs:27`, final-review:TEST-3, external-review:A5, external-review:D3)

- Impact: Production wiring for screenshot, UI Automation and input tools was untested; removing the error-reporting filter left every test green.
- Fix: Share one registration method between Program.cs and the tests.
- M0: fixed on `m0-final-fixes`

**D-PAL-19: Launch/kill error codes are inaccurate: the AccessDenied branch is unreachable and kill success is not confirmed** (low, `src/WindowsAppManagerMcp/Services/ProcessService.cs:342`, r1:PAL-15, r2:PAL-14)

- Impact: The agent cannot tell 'blocked by policy' from 'needs elevation' from 'not found', and may report a process killed while it still runs.
- Fix: Catch Win32Exception/AggregateException with NativeErrorCode 5 as AccessDenied with requiresElevation, and confirm exit with WaitForExit before reporting success.
- M0: fixed on `m0-launch-and-kill`

**D-PAL-20: list_processes(includeWindowless:true) reports WindowCount=0 for every process** (low, `src/WindowsAppManagerMcp/Services/ProcessService.cs:114`, r1:pro-m4, r2:PAL-15)

- Impact: An agent listing all processes to find which owns windows sees zeros and may relaunch an app or kill the wrong PID.
- Fix: Always build windowCounts and apply the windowless filter only when includeWindowless is false.

**D-PAL-21: kill_process acts on a bare PID with no identity check (PID reuse)** (low, `src/WindowsAppManagerMcp/Services/ProcessService.cs:288`, r1:pro-m5, r2:PAL-13)

- Impact: If the target exits between list_processes and kill_process and the PID is reused, a different process and its whole tree are killed.
- Fix: Add expectedProcessName (and optional expectedStartTime) parameters and refuse on mismatch.
- M0: fixed on `m0-launch-and-kill`

**D-PAL-22: kill_process protected list omits shell-infrastructure processes and the agent's own host** (low, `src/WindowsAppManagerMcp/Services/ProcessService.cs:226`, r2:PAL-13)

- Impact: An agent 'cleaning up processes' can kill ctfmon/TextInputHost (breaking keyboard input), Start-menu hosts, or its own session host.
- Fix: Extend the protected list and refuse to kill ancestors of Environment.ProcessId unless allowHostTermination=true.
- M0: partly fixed on `m0-launch-and-kill`

**D-PAL-23: Launch waits are synchronous Thread.Sleep loops with no timeout cap or cancellation; batch waits run sequentially and drop ErrorCode** (low, `src/WindowsAppManagerMcp/Tools/AppLauncherTools.cs:30`, r1:pro-m6, r2:PAL-16)

- Impact: A large timeout or a batch of handoff launchers takes N x timeout and outlives the client's request timeout, notifications/cancelled cannot stop it, and batch failures cannot be branched on by code.
- Fix: Make launch async with the SDK CancellationToken, clamp waitTimeoutMs, wait for batch items concurrently against one deadline, and add ErrorCode to LaunchResultItem.

**D-PAL-24: file:// is allowed as a 'safe' protocol, bypassing every executable check** (low, `src/WindowsAppManagerMcp/Services/InputValidationService.cs:37`, r1:pro-m7)

- Impact: A file:/// URL pointing at cmd.exe or a UNC tool launches with no warning while ms-settings: is blocked, an inconsistent policy agents can stumble into.
- Fix: Treat file:// targets as paths and run them through the same executable validation.
- M0: fixed on `m0-launch-and-kill`

**D-PAL-25: apply_layout ignores the result of FocusWindow for focus placements** (low, `src/WindowsAppManagerMcp/Services/LayoutService.cs:260`, r2:pro-m3)

- Impact: The agent assumes the focused app is ready for type_text/send_keys and sends keystrokes to whichever window actually has focus.
- Fix: Report the focus outcome per placement and in the overall result.

**D-SCR-10: The undefined high byte of GDI pixels is copied into an ARGB bitmap, producing transparent PNG pixels** (low, `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:560`, r1:SCR-5, r2:SCR-6)

- Impact: Captures (fully so on the BitBlt fallback) can contain transparent regions that render black or blank in vision pipelines, and resizing loses colors.
- Fix: Create bitmaps as Format32bppRgb (or force alpha to 0xFF) before encoding/resizing, and report the capture method used.

**D-SCR-11: Window capture has no hang protection: PrintWindow runs synchronously against the target's UI thread** (low, `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:130`, r1:SCR-7)

- Impact: Capturing a 'Not Responding' window to diagnose it can block the tool call for as long as the target is hung.
- Fix: Check IsHungAppWindow and fall back to screen-DC BitBlt (or error), and run PrintWindow with a bounded wait plus a CancellationToken.

**D-SCR-12: Frame captures include the invisible DWM resize borders** (low, `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:78`, r1:SCR-9)

- Impact: includeFrame=true images carry junk strips on three sides and are offset from the visible window, mismatching monitor-capture geometry.
- Fix: Crop to DWMWA_EXTENDED_FRAME_BOUNDS and report that origin in CapturedRegion.

**D-SCR-13: Large captures triple-copy pixels and the 16384 cap still allows multi-GB allocations** (low, `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:188`, r1:SCR-10, r2:SCR-12)

- Impact: Polling full-virtual-desktop captures on multi-4K setups allocates hundreds of MB of LOH memory per call (about 3 GB at the cap), causing GC pressure or OutOfMemory.
- Fix: Capture into a CreateDIBSection wrapped directly by Bitmap (removing two copies) and lower the total-pixel cap or downscale during capture.

**D-SCR-14: GetDIBits is called while the bitmap is still selected into the memory DC** (low, `src/WindowsAppManagerMcp/Native/ScreenCaptureWrapper.cs:49`, r1:scr-m3, r2:SCR-12)

- Impact: Outside the documented contract; can yield garbage or 'GetDIBits failed' on some drivers or remote sessions as intermittent, undiagnosable capture errors.
- Fix: Call SelectObject(memoryDc, oldBitmap) before GetDIBits.

**D-SCR-15: Annotation rectangles are too wide/tall for elements extending above or left of the image** (low, `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:639`, r2:SCR-10, r2:UIA-9)

- Impact: For partly off-image elements the drawn box and ImageBounds overrun the element's real edge by |X|/|Y| px and overlap neighbors, skewing computed centers.
- Fix: Clip with Rectangle.Intersect(elementRect, imageRect).

**D-SCR-16: capture_region silently returns black for off-screen areas and reports the nearest monitor** (low, `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:122`, r2:SCR-11)

- Impact: A miscomputed region (e.g. logical instead of physical pixels) returns a black image with a valid MonitorIndex and Success=true, hiding the coordinate bug.
- Fix: Intersect the region with the virtual screen, error when nothing overlaps or warn with the clipped region, and use MONITOR_DEFAULTTONULL.

**D-SCR-17: capture_with_elements combines window bounds, UIA bounds and pixels taken at different times** (low, `src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs:149`, r2:scr-m2)

- Impact: If the window moves, scrolls or finishes loading during the multi-second traversal, overlays misalign with the pixels and nothing flags it, so the agent clicks stale targets.
- Fix: Re-read the window rect after traversal and capture, and flag (or retry) when it changed.

**D-SCR-18: Dead encode/save paths, undisposed EncoderParameters and silent format fallback** (low, `src/WindowsAppManagerMcp/Services/ScreenshotService.cs:360`, r1:SCR-12)

- Impact: An unsupported format silently yields another format, and duplicated code paths risk fixes (e.g. alpha handling) landing in only one.
- Fix: Delete the dead methods, wrap EncoderParameters in using, reject unsupported formats, and add a MimeType field.

**D-UIA-09: Empty bounding rectangles serialize as int.MinValue coordinates** (low, `src/WindowsAppManagerMcp/Services/UIElementService.cs:304`, r1:UIA-10, r2:UIA-7)

- Impact: With visibleOnly=false, geometry-less elements report bounds of -2147483648, so a computed center is garbage and the agent cannot tell 'no geometry' from a real position.
- Fix: Emit Bounds=null (or HasBounds=false) when rect.IsEmpty or non-finite, skip size filters for such nodes, and use Floor/Ceiling rounding.

**D-UIA-10: maxDepth is off by one (N returns N+1 levels) and truncation is not reported** (low, `src/WindowsAppManagerMcp/Services/UIElementService.cs:340`, r1:UIA-11, r2:UIA-13)

- Impact: A cheap overview (maxDepth=1) returns an extra, often largest, level, pushing into timeouts, and the agent cannot tell whether nodes exist below the cut-off.
- Fix: Count window children as depth 1 and report MaxDepthReached/HasMoreChildren on truncated nodes.

**D-UIA-11: A single vanished sibling silently drops all remaining siblings in the traversal** (low, `src/WindowsAppManagerMcp/Services/UIElementService.cs:249`, r1:ui--m2, r2:ui--m2)

- Impact: In dynamic UIs (refreshing lists, toasts, Chromium re-layout) half a toolbar or list can disappear from a result that still reports success, so the agent concludes controls are absent.
- Fix: Snapshot children with FindAll(TreeScope.Children) under a CacheRequest (or resume from the last live sibling) and set a Truncated/Warning flag on failure.
- M0: fixed on `m0-concurrency-and-uia-crash`

**D-UIA-12: Unknown controlTypes names are silently accepted and return empty success** (low, `src/WindowsAppManagerMcp/Tools/UIElementTools.cs:53`, r2:ui--m4)

- Impact: The agent cannot tell 'bad filter value' from 'control not present' and may conclude the UI lacks the control.
- Fix: Reject unknown names with the list of valid control types, or echo the accepted names in the result.

**D-WIN-16: bringToFront (default true in snap) activates the window despite SWP_NOACTIVATE, making keyboard focus nondeterministic** (low, `src/WindowsAppManagerMcp/Services/WindowService.cs:124`, r1:win-m3, r2:win-m2)

- Impact: Whether focus moves to the rearranged window depends on whether the server currently has foreground rights, so type_text/send_keys after a layout step go to an unpredictable window.
- Fix: Use SetWindowPos(HWND_TOP, SWP_NOACTIVATE) for z-order only, document that bringToFront does not focus, and require focus_window for activation.

**D-WIN-17: set_window_state('restore'/'normal') on a window minimized from maximized returns it maximized** (low, `src/WindowsAppManagerMcp/Services/WindowService.cs:177`, r2:win-m4)

- Impact: An agent asking for 'normal' gets 'maximized' and needs a second call; in the batch path NewState may be omitted so it cannot even see this.
- Fix: For 'normal', use SW_SHOWNORMAL (or SetWindowPlacement showCmd=SW_SHOWNORMAL) and verify IsZoomed is false afterwards.

**D-WIN-18: DPI awareness is per-monitor v1 via API call with the HRESULT ignored and no manifest** (low, `src/WindowsAppManagerMcp/Program.cs:13`, r1:WC-14, r2:WC-13)

- Impact: If the call ever fails (awareness already set by a host or shim), every bound, snap rect and click coordinate becomes silently DPI-virtualized on scaled displays, with nothing telling the agent.
- Fix: Declare PerMonitorV2 in app.manifest (or SetProcessDpiAwarenessContext(-4) with fallback), log non-S_OK results to stderr, and report the effective awareness in get_monitors.

**D-WIN-19: Snap tests exercise a copied algorithm; MonitorService has no test seam** (low, `tests/WindowsAppManagerMcp.Tests/Unit/Services/MonitorServiceTests.cs:145`, r1:WC-15, r2:WC-14)

- Impact: Regressions in the production snap math, monitor-index fallback or maximized/minimized handling pass the suite unnoticed.
- Fix: Extract a pure SnapMath used by MonitorService (or inject a monitor-enumeration seam), delete the mirrored helper, and test production code with mocks.

**D-WIN-20: Batch delayBetweenMs is unbounded and blocks via Thread.Sleep with no cancellation** (low, `src/WindowsAppManagerMcp/Services/WindowService.cs:341`, r2:win-m3)

- Impact: A model passing a large delay (e.g. 5000 ms over 10 windows) blocks the request for nearly a minute and the client cannot cancel it.
- Fix: Clamp delayBetweenMs and make batch tools async with the SDK CancellationToken and Task.Delay.

### Refuted during verification

- r1:BRW-12 (CDP response decoding could corrupt multi-byte UTF-8 split across WebSocket fragments): refuted because Chromium escapes every non-ASCII character in DevTools JSON, so a chunk boundary can never split a multi-byte sequence. Accumulating frames before decoding is still good hygiene.
