# Common Workflows

Read this for step-by-step recipes of common tasks (press a button, fill in a field or a form), and for the kinds of requests this skill covers.

Each workflow is the loop: observe and find, act, wait, verify.

## Press a Button

1. **Find:** `get_desktop_state` with the app `window` (its element lines have refs), or `find_ui_elements` with the button's `name` and `controlType: "Button"`
2. **Act:** `element_action` `invoke` with the button's `ref` (or with the `window`, `name` and `controlType` in one call)
3. **Wait and verify:** `success` and `outcomeVerified`; `openedPopup` is a window the press opened (with `status: "InvokedPendingModal"`, a dialog the press waits for): read a dialog with `respond_to_dialog`. Wait for the effect with `wait_for_element` or `wait_for_window`
4. Only when the button has no pattern (`PatternNotSupported`): `fallback: "mouse"`, or `focus_window`, then `mouse_click` at its `clickPoint` with `windowHandle`

## Fill In a Field

1. **Find:** `find_ui_elements` with the field's `name` or `automationId` and `controlType: "Edit"`
2. **Act:** `element_action` `set_value` with the `ref` and `value`; when `outcomeVerified` is false (a web or React field), repeat with `mode: "type"`
3. **Verify:** `after` holds the text, or read it back with `read_element_text` (a password field is never read: check it another way). With keys instead, see **Input** in [input.md](input.md#in-the-loop), and check `charactersTyped`

Common keys and the `send_keys` syntax are in [input.md](input.md#common-keys).

## Fill In a Form in One Call

1. **Find:** `get_desktop_state` with the app `window`, or `find_ui_elements`, for the fields' refs or automation ids
2. **Act:** `run_steps` with the `window` and one step per field: `{"tool": "element_action", "args": {"ref": "...", "action": "set_value", "value": "..."}}`, `toggle` with `value: "on"`, `select`, then `invoke` on the submit button, and a `wait_for_element` (or `wait_for_window`) step for what should appear
3. **Verify:** `success`; on a failure, `stoppedBy`, `errorCode` and the failed step's `result` say which field and why: the fields before it are filled. Read the form back before you submit again

## More Workflows

- Save a File As, Answer a Prompt: [dialogs.md](dialogs.md)
- Launch and Position an App: [windows-and-apps.md](windows-and-apps.md#launch-and-position-an-app)
- Arrange Windows for Development, Save and Restore a Workspace, Multi-Monitor Setup, Keep a Window in View: [layouts-and-monitors.md](layouts-and-monitors.md)
- See What Is in a Window: [screens-and-ocr.md](screens-and-ocr.md#see-what-is-in-a-window)
- Reach an Item Far Down a List: [lists.md](lists.md#reach-an-item-far-down-a-list)
- Copy Text Out of an App: [clipboard.md](clipboard.md#copy-text-out-of-an-app)

## When to Use

Use this skill when the user asks to:

### Desktop State
- "What is on my screen?" / "What is open right now?"
- "Which controls does [app] show?" / "Which menus or dialogs are open?"

### Controls, Menus and Dialogs
- "Click Save in [app]" / "Press OK" / "Press the [button]"
- "Tick the [check box]" / "Select [item] in the [list]" / "Pick [option] in the [drop-down]"
- "Fill in the [field] with [text]"
- "Fill in this form: [fields]" (`run_steps`: every field in one call)
- "Choose File > Save As in [app]" / "Open the [menu] menu"
- "Answer the 'Save changes?' prompt" / "Close the dialog"
- "Save the document as [path]" / "Open [path] in [app]"
- "Wait until the [button] is enabled" / "Wait for the [dialog] to close"
- "Find [file] in the Explorer list" / "Scroll to [item] in the list"

### Reading Text and Tables
- "Read the e-mail in the reading pane" / "What does the status bar say?"
- "Read the processes in Task Manager" / "List the rows of the [table]"

### Window Discovery
- "List all windows" / "Show open windows"
- "Find Chrome windows" / "Find windows with [title]"
- "What window is focused?"

### Window Control
- "Move [app] to [position]"
- "Resize [app] to [dimensions]"
- "Snap [app] to [left/right/top/bottom] half"
- "Snap [app] to [position] quarter/third"
- "Maximize/minimize/restore [app]"
- "Focus [app]" / "Bring [app] to front"
- "Close [app] window"
- "Move [app] to monitor 2"

### Application Management
- "Open Notepad" / "Launch Chrome"
- "Open VS Code in [directory]"
- "Open Notepad on the left half of monitor 2" / "Open Calculator, or use the one that is open"
- "List running processes"
- "Kill process [name/PID]"

### Screenshots & UI Automation
- "Take a screenshot" / "Capture the screen"
- "Take a screenshot of monitor 0"
- "Capture the [app] window"
- "Capture a region at [x, y] with size [width x height]"
- "Capture [app] with numbered marks on its controls"
- "Get all UI elements from the [app] window"
- "Get all buttons and text inputs from [app]"
- "Which control has keyboard focus?" / "What is at position [x, y]?"
- "What does the canvas / remote desktop say?" / "Click the word [text] in [app]" (OCR, when UI Automation shows no text)

### Input Simulation / Click & Keyboard Automation
- "Click at position [x, y]"
- "Right-click at [x, y]"
- "Double-click at [x, y]"
- "Ctrl+click the second file" / "Triple-click the line to select it"
- "Drag the file into the message" / "Move the slider to the right"
- "Move the mouse to [x, y]"
- "Scroll down in [window]"
- "Type 'Hello World' in the text field"
- "Press Ctrl+C to copy" / "Press Ctrl+V to paste"
- "Copy the text in the terminal and read it" / "Put this text on the clipboard"
- "Press Enter" / "Press Tab"

### Browser Reading
- "What page is open in Chrome?" / "Get the URL of the current tab"
- "Read the text of the page open in Edge"
- "Get the full text of this JavaScript-heavy page"

### Layout Management
- "Save my current window arrangement as [name]"
- "Apply my [name] layout"
- "Show my saved layouts"
- "Delete the [name] layout"
- "Lay out VS Code on the left two thirds and Edge on the right third" (save_layout with `placements`: snap positions and monitor names, no capture; then apply_layout)
- "What would my [name] layout move?" (apply_layout with `dryRun: true`: changes nothing)

### Server
- "Which version of the server is running?" / "Is input paused?"
- "Why can't you see the [tool] tool?"
