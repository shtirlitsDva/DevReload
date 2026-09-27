<ui-automation>
`ui_*` is compiled into `DevReload.dll` (phase 1). It does what `acad_send_command` cannot: read and drive WPF palettes, click native modal dialogs, synthesize mouse input for jigs and grips, take screenshots.
</ui-automation>

<wpf>
- Assert on the ViewModel dump from `ui_snapshot`, not on pixels.
- A node `id` (`"0/3/1"`), an x:Name, or an AutomationId is an `elementRef`.
- The tree changes after each action. Call `ui_snapshot` again before the next ref.
- `ui_invoke` is for the Invoke pattern (buttons, menu items). Toggle and selection controls: `ui_toggle`, `ui_select`.
</wpf>

<modal-dialogs>
- A modal dialog blocks the WPF tools: they run on the main thread and the modal holds the idle pump. They fail fast with a message that names `ui_list_windows`/`ui_dialog_*`.
- The dialog tools run off the main thread, so they work while a modal is open. Main frame `enabled:false` in `ui_list_windows` = a modal is open.
- Close a dialog with `ui_dialog_click` (posts `BM_CLICK`: no cursor, no focus, safe on a background instance). `ui_dialog_buttons` lists the labels, nested file-dialog buttons included. Then call the WPF tool again.
- `ui_press_key` sends a real keystroke. Pass `hwnd` so the dialog gets focus first.
</modal-dialogs>

<mouse-and-jigs>
- Call `ui_canvas_capture_view` while quiescent, before the jig. The canvas tools need it for WCS→pixel mapping.
- Point jigs (`ed.Drag` + `AcquirePoint`) commit on the first button event. Drive them with `ui_mouse_move` ×N, then `ui_canvas_click`.
- `ui_canvas_drag`/`ui_canvas_drag_capture` hold the button down for the whole gesture: use them for grips, window select, drag-move.
- OSNAP snaps canvas clicks to real geometry.
- A jig that writes coordinates into a bound WPF control updates the palette live — a good end-to-end regression target.
</mouse-and-jigs>

<concurrency>
- WPF tools, screenshots, `ui_list_windows`, `ui_dialog_*` act inside the target process. Parallel-safe across pids; `ui_screenshot_window` (PrintWindow) captures occluded and background windows.
- Mouse tools and `ui_press_key` use the one session-wide cursor and foreground. `pid` selects the instance to foreground and map coordinates for. Drive one instance at a time.
</concurrency>

<screenshots>
Screenshots are inline base64 PNG. Prefer `ui_screenshot_wcs_box`/`ui_screenshot_region` to a full window. For `ui_canvas_drag_capture`, use a high `captureStride` and few `steps`.
</screenshots>
