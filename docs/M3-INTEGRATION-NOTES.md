# M3 integration notes

Working notes for finishing milestone M3 ("Semantic find-act-wait") on the integration branch
`m3-semantic-find-act-wait`. Delete this file in the M3 docs pass, once its open items are fixed or recorded in
BACKLOG.md and docs/AUTOMATION-ROADMAP.md.

## Status

All 14 M3 items are implemented in four waves (A-D) of parallel branches. Each branch had its own implementer and an
independent reviewer, and blocker and major findings were reworked. All four waves are merged here, together with
M2.F1-M2.F6. 64 tools (54 in 1.14.0). The safe suite passes: about 6,000 unit and contract tests and about 200
E2E-harness tests, with the desktop-gated tests skipped.

None of the M3 behaviour has run against a real desktop: every new UI Automation path is tested through fakes only.

## Left to do

1. A review of the whole branch, with every finding checked by a second reviewer, then a fix round. The open items
   below feed it. One is a must-fix: Windows SmartScreen ("Windows protected your PC") is not on the default
   target-policy deny list, so respond_to_dialog can press "Run anyway" in one call.
2. A docs pass: TOOLS.md "Changes for existing clients", SKILL.md, the server instructions (2,997 of 3,000
   characters), the roadmap As-shipped notes and defect register, BACKLOG.md. Delete this file.
3. Mark the PR ready and merge it with a merge commit (no squash) after green CI.
4. Release 1.15.0 (steps in .claude/commands/commit.md). **Release blocker:** the default UI Automation engine is now
   uia3. Before the release the user runs the gated no-input UI Automation scenarios on a real desktop with both
   engines (FixtureUiaEngineTests, FixtureUiElementsTests, with WAM_ALLOW_DESKTOP_NOINPUT_TESTS=1; run by the user,
   never by an agent). If uia3 and uia2 differ, set UiaEngineOptions.Default to uia2 for the release.

## Open items from the waves

The follow-ups, deferred items and review minors each wave reported, by branch. Items that an integration step
already handled (merges, snapshot regeneration, code numbering) are left out. Some may be fixed already by a later
wave; check the code before acting.
### Wave A

#### m3-uia-engine

- RELEASE BLOCKER: run FixtureUiaEngineTests and FixtureUiElementsTests with both engines on a real desktop (the gated no-input suite, run by the user) before releasing with the uia3 default. If they differ, set UiaEngineOptions.Default to uia2 for that release.
- M3.4: if refs or find use FindAll or ReadSubtree on large roots, add a real depth or count limit; IUiaTree now documents that they are not bounded.
- M3.6: add a FixtureOnlyCallPolicy rule for WrapperCallKind.UiaAction, decide AutoSetFocus, and run blocking pattern actions off the read worker. M3.7/M3.8: UIA events (CoalesceEvents).
- Not done: HasMoreChildren at the depth limit, collapseStructural, maxElements and format for capture_with_elements, D-UIA-07 warnings, and moving the browser reads onto the seam (M5.7).
- deferred/declined: Minor: uia3 is the default engine but has never run on a real desktop: I kept uia3 as the default and made the real-desktop run a written release blocker in BACKLOG.md (M3.2 row) and in the roadmap's M3.2 As-shipped note. If the engine scenarios differ, UiaEngineOptions.Default goes back to uia2 for that release.
- open review minor: uia3 is the default engine but has never run against a real desktop (src/WindowsAppManagerMcp/Services/UiaEngineOptions.cs)
- open review minor: MaxElements truncation is reported when the limit falls on the last element at a depth below maxDepth (src/WindowsAppManagerMcp/Services/UIElementService.cs)
- open review minor: RespondsWithin treats every SendMessageTimeout failure as 'not responding' and never reads GetLastError (src/WindowsAppManagerMcp/Native/NativeWindowWrapper.cs)
- open review minor: Uia3Tree.GetPattern wraps by the order of its type checks, not by the pattern type asked for (src/WindowsAppManagerMcp/Native/Uia3Tree.cs)
- open review minor: ReadIsModal under UIA3 reads all six Window-pattern properties (src/WindowsAppManagerMcp/Native/Uia3Tree.cs)
- open review minor: ReadSubtree and FindAll: maxElements limits only the output, not the cost (src/WindowsAppManagerMcp/Native/Uia3Tree.cs)

#### m3-wait-for-window

- M3.8 wait_for_element could reuse the WaitTimedOut code and the same 'not satisfied is isError, with the body' convention for consistency.
- M7.2: switch WaitForConditionAsync from polling to event wake-ups (SetWinEventHook); the polling loop is isolated in WindowWaitService.Conditions.cs.
- The gated FixtureWindowWaitTests have not been run against the real desktop; run them with the opt-in on a machine where that is safe.
- open review minor: Satisfied result can include windows that have not held for stableMs (src/WindowsAppManagerMcp/Services/WindowWaitService.Conditions.cs)
- open review minor: visible/foreground timeout messages can misstate the state of a window that is still settling (src/WindowsAppManagerMcp/Services/WindowWaitService.Conditions.cs)
- open review minor: gone by handle warns 'check the selector' when the window simply closed before the wait started (src/WindowsAppManagerMcp/Services/WindowWaitService.Conditions.cs)
- open review minor: newOnly / excludeHandles combined with a handle selector can only time out, and is not refused (src/WindowsAppManagerMcp/Tools/WindowFinderTools.Wait.cs)
- open review minor: title_changed with a title filter in the selector fails with HandleMismatch when the change already happened (src/WindowsAppManagerMcp/Services/WindowWaitService.Conditions.cs)

#### m3-launch-arrange

- Run the gated FixturePlacementTests on a real desktop with the input opt-in; it has never been run.
- Shared files the integrator must merge: ServiceRegistration.cs (one ILaunchPlacementService registration), docs/TOOLS.md (launch sections, audit table, error reference, Changes for existing clients), SKILL.md, the roadmap M3.13 note, ToolEnumTests, ToolDocCatalog, ContractServices (Placement mock), tools.snapshot.json.
- Older M2.6 code uses the same loose substring match: WindowWaitService.FindReuseSignal takes a title that changed to contain a DocumentHints name as reusedExistingWindow or openedInTab. It could use ProcessService.TitleShowsName too; I left it alone because it is the existing M2.6 path, outside this item.
- An app that moves its window later than about 500 ms, or more than once, is not followed (possible with the M3.7/M3.8 waits).
- apply_layout's launchMissing does not use LaunchPlacementService; placing by monitor identity is left to M6.7/M6.8.
- open review minor: Store-app and ms-settings: reuse never happens, despite what the docs say (src/WindowsAppManagerMcp/Services/ProcessService.cs)
- open review minor: An unexpected exception in the placement still hides a successful launch (src/WindowsAppManagerMcp/Tools/AppLauncherTools.cs)

#### m3-m2-followups

- M2.F1 (force on a protected window without elicitation) still needs the user's decision.
- F4 has not been checked on a real desktop: confirm that a prelaunched or suspended Store frame comes back as NoDesktop or CurrentDesktop and is left out. No gated scenario was added because the fixture app cannot make Windows prelaunch a Store app.
- Possible refinement for F2: VS Code-based editors are matched by process anywhere in their windows, so a right click in a native dialog they own (for example a file dialog) also follows the paste rule. This was left as is on purpose, since it only refuses more.
- deferred/declined: M2.F1 force on a protected window without elicitation: Pending user decision, as instructed. No change to force/confirm behaviour; the BACKLOG row and the roadmap bullet still show it open.
- open review minor: F3: a non-zero exit is not always a failed launcher; Chromium returns 24 after a successful handoff (src/WindowsAppManagerMcp/Services/ProcessService.cs)

### Wave B

#### m3-chromium-warmup

- Real-desktop validation is missing. It is unverified whether UIA FromHandle on Chrome_RenderWidgetHostHWND returns the Document itself or a pane above it (the code handles both, searching three levels of panes). Add a WebView2 pane to the fixture app (the M1.11/M3.3 open item) and a gated scenario against live Chromium content.
- Expose forceAccessibility on launch_applications_batch items. The service already honours LaunchRequest.ForceAccessibility and reports it per item.
- M3.8 wait_for_element should call the warm-up once per wait, not per poll. M5.7 browser reading could reuse it.
- SPI_SETSCREENREADER (enable_screen_reader_flag) is deferred: it needs a crash-safe restore story (or a user decision to accept a session-wide side effect) before it can be offered, even behind an env var.
- deferred/declined/partial: Opt-in enable_screen_reader_flag (SPI_SETSCREENREADER): Not implemented, even behind an env-var opt-in. The flag is session-wide: every running app (Office, browsers, Electron) changes behaviour and cost while it is set. A stdio server that is killed or crashes cannot restore it, so it would stay on for the user's session. Two servers would race to restore it. The SPIF_SENDCHANGE broadcast reaches every top-level window. The warm-up plus forceAccessibility cover the Chromium cases. Documented in TOOLS.md and the roadmap note.
- deferred/declined/partial: Gated E2E scenario: Added FixtureUiElementsTests.GetUIElements_NamesTheWpfFramework_AndWarmsNothingUpInANativeWindow (DesktopNoInputFact, not run). No WebView2 fixture pane and no live Chromium scenario were added.
- open review minor: New gated E2E assertion likely fails: a WPF window's TitleBar is Win32 (tests/WindowsAppManagerMcp.E2ETests/Scenarios/FixtureUiElementsTests.cs)
- open review minor: Web content without a Chrome_RenderWidgetHostHWND (WebView2 visual hosting) gets no warm-up and no signal (src/WindowsAppManagerMcp/Services/AccessibilityWarmup.cs)

#### m3-element-refs

- Run the gated FixtureElementRefsTests on a real desktop (no-input opt-in, run by the user). None of the split search, clickable points, uniqueness proof or live resolution has run against a live desktop.
- M3.6 element_action: an element inside a recycled list item whose own identity does not change (a row's check box) still resolves Live, so read the containing item's name, or what you act on, before a destructive action. Treat ElementStale and AmbiguousElement as stop signals, never as hints to retry on a look-alike.
- Optional: a window thread-id check for handle reuse within the same process and class. It needs a new INativeWindowWrapper member plus NativeCallGuard and WrapperCatalog entries; not done because handle values come back only after a slot's 16-bit reuse counter wraps.
- Optional: provider-side nameMatch contains via PropertyConditionFlags_MatchSubstring (UIA3, Windows 10 1809+), unverified.
- M3.5 can add an optional scope parameter to find_ui_elements; omitting it should keep today's window-only search. A scoped search should pass Unique=null, as withinRef does.
- open review minor: Candidate/deadline limits only act between parts; a wide level or a single-chain tree is still one unbounded FindAll (src/WindowsAppManagerMcp/Services/ElementSearch.cs)
- open review minor: Live resolution path accepts the stored element without the identity check the other paths apply (src/WindowsAppManagerMcp/Services/ElementRegistry.cs)
- open review minor: Registry capacity equals get_ui_elements' maxElements cap, so one large walk evicts every other ref (src/WindowsAppManagerMcp/Services/ElementRegistry.cs)
- open review minor: The 2000-candidate limit and the deadline only apply between parts; one wide or unsplittable part is unbounded (src/WindowsAppManagerMcp/Services/ElementSearch.cs)
- open review minor: A single maxElements=5000 walk (or capture) evicts every other ref; the capacity equals the walk cap (src/WindowsAppManagerMcp/Services/ElementRegistry.cs)
- open review minor: Window-handle reuse within the same process is not detected (src/WindowsAppManagerMcp/Services/ElementRegistry.cs)
- review response partially_fixed: Candidate/deadline limits only act between parts; a wide level or a single-chain tree is still one unbounded FindAll -> Fixed: when a name filter (contains or regex) runs, the deadline and the 2000-candidate limit are checked between the elements of one part's results, so the filter never runs past them. Without a filter, counting what the provider already returned is free and keeps counts exact; applying the check there would also drop matches already fetched. Single-child chains (up to 32) no longer use the 16-node split budget, so a browser's wrapper panes do not collapse the split. Declined: splitting a wide level child by child would turn one provider call into thousands of cross-process calls for a large list, slower than one bounded call. The UIA3 transaction timeout ends such a call; the uia2 exposure is now stated in the ElementSearch remarks, TOOLS.md and the As-shipped note, and the docs say the limits act between provider calls.
- review response partially_fixed: Window-handle reuse within the same process is not detected -> Window records now hold the process id and the window class (existing INativeWindowWrapper.GetClassName), and a change in either drops the window's refs (test: same process, class changes to #32770). Reuse by a window of the same process and class is not detected; a thread-id check would need a new wrapper member and guard entries. Such reuse is rare, since an HWND value comes back only after its slot's 16-bit reuse counter wraps. Documented on IsSameWindow and listed as an optional follow-up.

#### m3-capture-registry

- Wave D: Set-of-Marks marks and legend tied to M3.4 refs; overlays drawn after resizing; labels/maxElements/actionableOnly/drawOverlays; elements stored in CaptureRecord; capture_window ref+padding element crop.
- Run the gated no-input FixtureCaptureIdTests on a real desktop (never run here).
- M4: when drag or mouse_down tools are added, give them the same space/captureId parameters through InputSimulationTools.Point.
- Pre-existing doc drift seen but not changed: the TOOLS.md 'Image coordinates' table still says a capture_window frame capture starts at the window rectangle's corner, but since M2.9 it starts at the visible frame (DWMWA_EXTENDED_FRAME_BOUNDS).
- BACKLOG.md M3.11 row was left Pending (not updated, to keep shared-file edits minimal); the integrator may mark it partial.
- deferred/declined/partial: capture_window ref-less element cropping: Not trivial without M3.4 refs or selectors: it needs an element lookup under the capture plus a crop. capture_region over an element's bounds already does it in two calls. Deferred to wave D.
- deferred/declined/partial: Set-of-Marks marks/legend tied to M3.4 refs: Not built, as instructed: wave D (marks, legend, overlays after resizing, labels/maxElements/actionableOnly/drawOverlays, elements in CaptureRecord).
- open review minor: A restoreIfMinimized capture returns a captureId that is stale at birth (src/WindowsAppManagerMcp/Services/ScreenshotService.cs)
- open review minor: PointRecheck is not re-run in RecheckMouse, so a window that moves between CheckMouse and the press (or between the two presses of a double_click) is not caught (src/WindowsAppManagerMcp/Services/InputTargetGuard.cs)
- open review minor: A screen-capture captureId only checks the virtual-screen bounding box, so rearranged monitors with the same bounding box go undetected (src/WindowsAppManagerMcp/Services/CoordinateSpaceResolver.cs)

#### m3-force-confirm

- The service's ProtectedWindow message still says 'Pass force=true only if the user explicitly asked to close this window'. I left it unchanged on purpose so the second flag stays a separate step; the ConfirmationRequired message explains confirm. Change it if one round trip is preferred.
- find_windows' isAgentHost description still says close tools refuse such a window 'unless force=true'. It is still true that force is needed, but the description does not mention the new confirm or elicitation step. I left it alone to avoid an unrelated snapshot change.
- The integrator should regenerate snapshots and docs after merging sibling branches. This branch touched tools.snapshot.json (close_window, close_windows_batch), the close_window parameter table in docs/TOOLS.md, and one line each in ErrorCodes.cs (doc comment only), SKILL.md and README.md.

### Wave C

#### m3-popup-search

- Run the gated input-tier FixturePopupTests on a real desktop. They have never run.
- m3-element-action: for openedPopup, take GetPopups(window) handles before acting, then call WaitForNewPopupAsync. It skips tooltips. A captioned dialog that is not owned by the target, or that is owned by a hidden window, may be refused by the input guard (TargetOccluded): act on it by naming it as the target.
- Optional: make InputTargetGuard.BelongsToTarget share code with the popup rule. They now differ on purpose: the guard asks whether a click may land, the service asks whose popup a window is. The difference is documented on IPopupService.
- Optional: give desktop-scope reads their own stuck-read bucket per window process, if stuck providers in unrelated windows prove to block the named app in practice.
- Wave D: respond_to_dialog, menu_path, get_desktop_state scope.
- deferred/declined/partial: Stuck-read accounting is attributed to the named target for multi-window scopes: Documented in the ElementSearchService remarks and the roadmap, as the reviewer's first option. Behaviour unchanged: a per-window worker key would split one search into several worker reads, and hung windows are already skipped before they are read.
- open review minor: Popups-first search can keep the named window from being searched at all under the new default scope (src/WindowsAppManagerMcp/Services/ElementSearchService.cs)
- open review minor: Uniqueness proof regressed when a popup also has matches (src/WindowsAppManagerMcp/Services/ElementSearchService.cs)
- open review minor: A UI Automation timeout opening a popup fails the whole search, and the NotSupported retry is outside the skip guard (src/WindowsAppManagerMcp/Services/ElementSearchService.cs)
- open review minor: WaitForNewPopupAsync treats tooltips as the opened popup and may snapshot processes on every poll (src/WindowsAppManagerMcp/Services/PopupService.cs)

#### m3-element-action

- Run the gated FixtureElementActionTests on a real desktop with the input opt-in (never run here). Also check that AutoSetFocus=0 keeps a background Invoke from taking the foreground, that WPF's asynchronous Invoke reports openedPopup for the fixture modal, and that the toggle wait handles a real Chromium check box.
- Server instructions are at 2992 of 3000 characters; a sibling that adds to them (wait_for_element) must trim elsewhere.
- The keyboard path is not atomic: the gate is released between the focus check and the keys sent through the input service. Focus is checked again after Ctrl+A, and the input guard re-checks the foreground window, but not the focused element, before every batch.
- element_action has no releaseHeldModifiers parameter: ModifiersHeld from its mouse or keyboard paths cannot be overridden from the tool.
- An element inside a recycled list row can still resolve after the row changed (M3.4 caveat); this is documented.
- An element of another process whose nearest window handle belongs to a third process (a windowless fragment hosted across processes) is refused (fails closed); check on a real desktop that WebView2 and XAML-island content still pass.
- Wave D: the menu_path action, respond_to_dialog and the file dialog helper (not built).
- open review minor: window_* top-level check lets the action through when the element's window handle is unknown (src/WindowsAppManagerMcp/Services/ElementActionService.Plans.cs)
- open review minor: Pause is not rechecked after the element read, before the pattern action (src/WindowsAppManagerMcp/Services/ElementActionService.cs)
- open review minor: Policy and elevation checks use the ref's window, not the element's own process (src/WindowsAppManagerMcp/Services/ElementActionService.cs)
- open review minor: A minimized window's elements are refused as offscreen, with a scroll_into_view hint that cannot help (src/WindowsAppManagerMcp/Services/ElementActionService.Plans.cs)
- open review minor: Paste mode writes the clipboard even with the clipboard toolset off, and has no length cap (src/WindowsAppManagerMcp/Services/ElementActionService.cs)
- open review minor: Target checks cover the ref's registered window, never the process or window the element actually lives in (src/WindowsAppManagerMcp/Services/ElementActionService.cs)
- open review minor: mode=paste replaces the user's clipboard even when the user excluded set_clipboard or the clipboard toolset (src/WindowsAppManagerMcp/Services/ElementActionService.cs)

#### m3-wait-for-element

- User decision: value_equals never reads a password field, against the task's 'compare, never echo' design. Comparing would need the never-read rule lifted in IUiaTree.ReadValue and IUiaValuePattern.Value.
- Run the gated no-input FixtureElementWaitTests on a real desktop. None of the wait has run against live UI Automation.
- M7.2: UIA event wake-ups (StructureChanged, PropertyChanged) for wait_for_element, together with wait_for_window's SetWinEventHook. This needs new IUiaTree event members on both engines, with removal off the worker and off event threads.
- Possible: a stableMs for wait_for_element. A list that is re-created can satisfy gone for a single check.
- M3.6 element_action could call wait_for_element's service for its post-state checks, or reuse ElementWaitService.ParseToggleState and ConditionName.
- deferred/declined/partial: Optional early wake-ups on UIA StructureChanged/PropertyChanged events: The IUiaTree seam has no event members, so both engines would need new ones. Registering and removing handlers are cross-process calls that can block on a hung provider; removal can neither run on the UiaWorker thread a stuck read holds nor on a UIA event thread. None of it can be verified on a real desktop here. Polling gives the same results, so events stay a latency optimisation for M7.2.
- deferred/declined/partial: value_equals must not leak password values (IsPassword: compare, never echo): Nothing leaks: a test checks that the serialized result never contains the password. Changed from the design, though: the value is never read, so nothing is compared. Reading it would break the IUiaTree.ReadValue / IUiaValuePattern.Value contract and the M1.9/M3.4 never-read rule, and providers mask or refuse the value anyway. Password matches are skipped; if only password fields match, the wait ends with InvalidArguments.
- open review minor: gone with a stale withinRef ends with ElementStale, but TOOLS.md and the handoff say gone is satisfied (src/WindowsAppManagerMcp/Services/ElementWaitService.cs)
- open review minor: gone with a window {handle} that already closed returns InvalidHandle instead of being satisfied (src/WindowsAppManagerMcp/Services/ElementWaitService.cs)
- open review minor: 'Window closed' is inferred from UI Automation FromHandle failing, without confirming through Win32 (src/WindowsAppManagerMcp/Services/ElementWaitService.cs)
- open review minor: The once-per-wait warm-up is tied to poll 1 and is lost if the first check never reaches it (src/WindowsAppManagerMcp/Services/ElementWaitService.cs)

#### m3-read-text-tables

- Run the gated no-input FixtureElementReadTests on a real desktop. Neither GetTextPage (on either engine) nor the Grid and row walks have run against a live provider.
- The SDK serializes results with System.Text.Json's default encoder, so quotes, <, >, &, ' and every non-ASCII character become six-byte \uXXXX escapes. Non-Latin text then costs up to 6x its length in tokens for every reading tool (get_browser_content, read_element_text, ...). Consider UnsafeRelaxedJsonEscaping-style options for tool results.
- Not done in read_table: row headers (TablePattern.GetRowHeaders), rows inside Group children (for example Explorer's grouped views without the Grid pattern), and a richer fixture grid (a WPF DataGrid with headers) for E2E. Not done in read_element_text: text attributes and the selection.
- M3.8 wait_for_element and M3.6 element_action can reuse ElementReadService's Prepare pattern (precheck, then warm-up, then Resolve on one worker call). element_action's value-verification could reuse the password-safe text read.
- open review minor: Text page size is bounded by the provider, not by the server (src/WindowsAppManagerMcp/Native/Uia3Tree.cs)
- open review minor: Claimed 16,000-token bound only holds for ASCII text (tests/WindowsAppManagerMcp.Tests/Contract/ElementReadContractTests.cs)
- open review minor: A cell that vanishes during its value read fails the whole table (src/WindowsAppManagerMcp/Services/ElementReadService.Table.cs)
- open review minor: An incomplete search with zero matches is reported as ElementNotFound (src/WindowsAppManagerMcp/Services/ElementReadService.cs)

### Wave D

#### m3-virtualized

- Run the gated FixtureVirtualizedListTests on a real desktop: the no-input test with the no-input opt-in, and the three realize tests with the input opt-in. None of this has run against live UI Automation.
- Unverified on real providers: Explorer's and Outlook's ItemContainer placeholders. If they cannot be read, such a list now reports PartsSkipped. find_ui_elements still returns what it found, but element_action realize by criteria refuses with OperationTimedOut. The way through is find_ui_elements, then realize by ref.
- The scroll fallback needs exactly one visible list. Windows with a navigation tree and a content list (Explorer, Outlook, Settings) return AmbiguousElement. The fix is a way to choose the list on element_action, for example a withinRef or listRef parameter fed from find_ui_elements' virtualized.containers refs. That is best done after m3-dialogs is merged, since it also edits element_action.
- ElementActionServiceTests.Toggle_OfAThreeStateBox_WaitsForEachChange_AndReachesTheState (wave C) seems timing-dependent: it failed in about 1 of 4 runs of its class here, on code this branch does not change. It deserves a deterministic clock or a looser wait in the fake.
- Not done, as before: putting the list's scroll position back after a maxScrolls search that fails, scrolling a list inside a popup, and a case-insensitive FindItemByProperty retry.
- open review minor: maxScrolls: timeoutMs is documented as bounding the whole search but each page and the final realize get the full timeout (src/WindowsAppManagerMcp/Tools/ElementActionTools.Realize.cs)
- open review minor: Scroll fallback is AmbiguousElement in the windows it targets (navigation tree + content list) (src/WindowsAppManagerMcp/Tools/ElementActionTools.Realize.cs)
- open review minor: virtualized:true is also set on realized offscreen items, and visibleOnly is silently ignored for them (src/WindowsAppManagerMcp/Services/ElementSearchService.Virtualized.cs)

#### m3-desktop-state

- Run the gated no-input FixtureDesktopStateTests on a real desktop. Check that GetKeyState's toggle bit reports Caps Lock correctly from a thread-pool thread.
- The M3.5 As-shipped note still lists 'the scope of get_desktop_state' under Not done. It can be marked done.
- Pre-existing flaky tests: the ElementActionServiceTests toggle tests (ToggleStateWait 30 ms with 1 ms polls) and ProcessKillTests.KillProcess_Graceful_CancelledWhileWaitingForTheExit_Throws.
- Possible: add observe to other action tools through IDesktopStateService. They should reuse element_action's rule of not naming a window the action was refused for.
- open review minor: Skill and tool description say include ['screenshot'] 'adds' a capture, but include replaces the default sections (skills/using-windows-app-manager/SKILL.md)
- open review minor: element_action observe explicitly reads the element's window even after the action was refused as TargetDeniedByPolicy or TargetIsAgentHost (src/WindowsAppManagerMcp/Tools/ElementActionTools.cs)
- open review minor: Server instructions no longer tell the agent to call get_server_info (src/WindowsAppManagerMcp/Infrastructure/ServerIdentity.cs)
- open review minor: Failed result reports input.paused=false regardless of the real pause state (src/WindowsAppManagerMcp/Services/DesktopStateService.cs)

#### m3-dialogs

- Policy owner: consider adding 'smartscreen' to TargetPolicySettings.DefaultDeniedProcesses. respond_to_dialog, like element_action before it, can press 'Run anyway' in one call.
- Flaky existing test: ElementActionServiceTests.Toggle_OfAThreeStateBox_WaitsForEachChange_AndReachesTheState (wave C) fails intermittently, apparently timing-dependent; owner of M3.6 to stabilize.
- Run the gated input-tier FixtureDialogTests on a real desktop; they have never run.
- On a real desktop, check that the title bar's caption buttons lie inside the TitleBar's bounds for #32770 boxes and task dialogs. Also check the ids: a localized MB_OK button, CommandButton_6 and CommandButton_7, FileNameControlHost with Edit 1001, and ComboBox 1148 with Edit 1148.
- Add a fixture button that shows a Win32 SaveFileDialog so a gated scenario can cover file_dialog end to end.
- Not covered: UWP/WinUI FileOpenPicker (PickerHost) and other pickers that are not #32770; a Win32 menu bar (#32768) menu_path scenario; get_desktop_state's scope (M3.12).
- deferred/declined/partial: Unowned dialog named by handle: Still not taken as a dialog, to keep respond_to_dialog to dialogs. The DialogNotFound message and TOOLS.md now say an unowned window is no dialog and to use element_action.
- deferred/declined/partial: SmartScreen on the default deny list: Predates this branch and lives in the shared TargetPolicySettings; recorded as a follow-up for the policy owner.
- open review minor: respond_to_dialog says a timed-out press 'was not pressed' (src/WindowsAppManagerMcp/Services/DialogService.cs)
- open review minor: file_dialog treats any Yes/No prompt as the replace prompt (src/WindowsAppManagerMcp/Services/FileDialogService.cs)
- open review minor: menu_path has no overall deadline (src/WindowsAppManagerMcp/Services/MenuPathService.cs)
- open review minor: menu_path silently ignores the `fallback` argument (src/WindowsAppManagerMcp/Services/MenuPathService.cs)
- open review minor: Audit line has no dialog target when the guard refuses (src/WindowsAppManagerMcp/Services/DialogService.cs)
- open review minor: Any Yes/No prompt after Save is answered as the replace prompt, even when the file did not exist (src/WindowsAppManagerMcp/Services/FileDialogService.cs)
- open review minor: The overwrite=false pre-check and finalPath use the literal path, but the dialog expands %VAR% and may append the default extension (src/WindowsAppManagerMcp/Services/FileDialogPaths.cs)
- open review minor: respond_to_dialog presses a button in whatever dialog is in front before the agent can see its text (src/WindowsAppManagerMcp/Services/DialogService.cs)
- open review minor: Dialog button names and titles are unbounded, including in error messages (src/WindowsAppManagerMcp/Services/DialogService.cs)
- open review minor: file_dialog reads the prompt the press opened without the target check (src/WindowsAppManagerMcp/Services/FileDialogService.cs)

#### m3-set-of-marks

- Run the gated no-input FixtureSetOfMarksTests and FixtureCaptureIdTests on a real desktop. Check tag readability and how crowded 80 marks look.
- m3-desktop-state (M3.12) could reuse UIElementFilter.ActionableOnly and ElementMarkLegend.
- A relaxed JSON encoder for tool results would avoid the escaped single quote in legend lines (about 3 tokens per line).
- README.md line 15 still says 'optional UI element overlays'.
- deferred/declined/partial: Elements stored in CaptureRecord: Nothing would read them; refs are resolved through the element registry. The registry generation comment no longer promises this
- open review minor: capture_window ref can return WindowNotFound/OperationException, which the docs don't list (src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs)
- open review minor: maxElements counts walked elements, not marks (src/WindowsAppManagerMcp/Tools/ScreenshotTools.cs)
