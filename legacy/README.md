# legacy

These are the old dual-monitor `komorebi-bar` configurations, no longer used.

They were replaced by `WorkspaceTray/`, which draws the workspace number, the
focused window title and the layout directly on the Windows taskbar instead of
reserving 50px at the top of each screen.

Kept only for reference. Do **not** add `bar_configurations` back to
`komorebi.json` unless you also want the bar back.

Notes:

- `work_area_offset` in komorebi-bar means: `top` shifts the work area origin
  down, while `bottom` shrinks its height. To reserve `H` pixels at the top you
  must set `top: H, bottom: H`; setting only `top` pushes the extra `H` pixels
  into the bottom of the screen (usually under the taskbar).
- With two bars you need one config per monitor index, each with its own
  `work_area_offset`.
