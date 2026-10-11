# TermCity UI conventions

- All menus, submenus, dialog boxes, confirmation prompts, hover tooltips and
  other popup surfaces must use the same terminal treatment: a black background,
  two closed white border lines spaced four logical pixels apart, and the shared
  12-logical-pixel content inset. Apply this automatically to new or changed UI;
  do not wait for an explicit styling request.
- Reuse `TerminalFrame` in `godot/TerminalFrame.cs` for control-based frames and
  its tooltip style for Godot's `TooltipPanel` theme. Do not introduce default
  Godot popup styling or unrelated border implementations.
- Preserve colored text headers and accessible focus states within these frames.
- Construction icons are an explicit exception: each icon gets one white
  outline. Only the surrounding rail uses the shared double-line border flowing
  from the header. At three-way joins, keep the outer line continuous and the
  inner square corners separate; never bridge the space between branch lines.
- Verify affected popup styling and layout with the Godot integration smoke
  checks, including narrow windows.

See [the developer guide](../docs/DEVELOPMENT.md) for architecture and validation
commands.
