# Menu simulator

Open an APT movie or FEL layout and select **Menu simulator**. The simulator embeds
MVP-Workshop's existing APT rasterizer/VM or FEL compositor/font/timeline preview.
It is a preview workspace; routes, bindings and scores are session settings, not
exportable game modifications.

Use **Capture input**, then arrows and Enter/Space; Escape returns/closes a popup.
The direction buttons and focus selector also work. Enable **Controller (XInput)**
for controller 0: D-pad moves, A activates, B returns. Controller input is polled
only while this simulator contains keyboard focus and is visible. Physical hardware
has not been validated. Directional navigation selects an enabled control in the
requested half-plane using authored positions; it does not reproduce a native
navigation table. Mouse selection inside the embedded preview remains inspection.

Choose a destination and press **Connect focused → target**, then **Activate**.
The connection is an explicit simulated screen-manager service. Check **Popup
(modal)** to create a modal route. **Open** mounts the selected destination directly.
FEL destinations play their decoded ENTER timeline from its start while Run timelines is enabled.
The popup retains a snapshot of the previous workspace behind its active preview;
underlying scripts do not continue. Back reconstructs the previous screen and
restores focus if that control still exists. It does not resume that screen's VM.
Nested popup history is supported. Reset returns to the initially opened asset,
clears history and rebuilds runtime state. Routes/context remain available; **Clear
routes** and **Clear trace** reset those separately. Routes and bindings are not
persisted across closing the document/application.

The context selectors use the existing roster/DAT joins and stadium registry.
Home/away names, abbreviations, nicknames, stadium and player name/jersey/position
are decoded data. Scores are explicitly simulated editable integers. Apply binds
values only to fields you specify, one `field=context.key` line per binding:

```
menu/homeTeam=home.name
menu/awayTeam=away.name
menu/score=home.score
```

For APT use an instance path from Selection (omit `root/`); for FEL use a TB element
name from the tree/source. These example field paths are illustrative. Available
context keys and current values appear beside the preview. Bindings override the
rendered text after native script evaluation, not the native script variables.
Missing keys retain authored/script text. This avoids assuming undocumented game
service APIs or data-variable names. Identically named FEL text fields share a
binding within the session.

Project edits, undo/redo and loaded project overrides trigger a runtime rebuild
from the current overlay bytes, including referenced art/layout/localization and
roster data. Route definitions, text bindings, selected teams/player and score
inputs survive these refreshes. A removed/renamed control falls back to the first
available control; stale routes are never applied to a different control. FEL
control IDs include source path, line and name, so inserting source lines can
require reconnecting routes. Assets are read through the same project overlay as
other viewers; the disc is never written.

| Interaction | Fidelity |
|---|---|
| APT InitActions, class construction, frame actions, supported bytecode, timers, script-set display properties | Existing bounded VM, retained between simulator renders |
| APT authored `onRollOver`, `onRollOut`, `onPress`, `onRelease` | Executed when resolved to native functions; directional input and focus selection dispatch rollover/out; activation dispatches press/release |
| APT focus candidates | Visible buttons and sprite instances with a resolved onRelease handler; script `enabled` respected |
| APT stop/play/goto/next/previous timeline actions | Supported for entered timelines; labels use existing movie label resolution |
| FEL normal/highlight/pressed visuals, includes, native fonts, timelines | Existing decoded compositor; only focused button receives highlight/pressed state |
| FEL button callbacks/navigation, game screen transitions, popup routing | Explicit simulated services; FEL callbacks are not decoded |
| Teams/players/stadium | Existing decoded sources; field binding is an explicit preview override |
| Scores, modal composition, navigation geometry | Simulated host behavior |
| Network/login/storage/audio/game launch, unresolved methods/opcodes | Not executed; diagnosed, never silently converted to simulated success |

APT button conditional-action records and clip-event flags are not decoded by the
existing parser; only resolved named handlers execute. Playback
samples each entered frame, not arbitrary skipped frames during manual scrubbing.
Use Reset before replaying a flow after scrubbing or changing preview targets.
The VM retains its 60-second timer horizon, recursion/instruction/timer bounds and
existing format/rendering limitations (see [APT fidelity](APT_FIDELITY.md)).
FEL animation easing, native script execution and special blend behavior retain
the existing preview limitations. Missing resources remain visible in each
preview's fidelity panel.

Trace entries distinguish `input`, `native script`, `simulated service` and
`diagnostic`. Native timeline entries include action offsets and frame numbers.
Unsupported calls remain in APT Limitations and are copied to the trace once per
runtime. Trace is capped at 1,000 entries and can be exported as text.

Validation:

```
dotnet run --project tests/MenuSimulation -c Release
MVP-Workshop.exe --menu-selftest path-to-report.txt
```

The core regression executable checks navigation/disabled controls, connected
routes, popup/back focus, reset, trace bounds, native handler dispatch from a
synthetic relocated APT bytecode fixture, retained state, timeline/stop behavior,
missing handlers and scripts-off behavior. The WPF self-test loads actual decoded
FEL screens, connects them with explicit simulator routes, checks modal/back,
verifies a decoded team-name binding changes rendered pixels with playback paused,
applies and undoes a project FEL edit, mounts native stdPopup APT, checks missing
handler diagnostics, then captures the workspace. See
[UI validation report](../research/menu-simulator-test.txt). This validates host
connections, not a recovered native game screen graph.

FEL BU records often hide their hit artwork while a sibling FC/TB draws the button.
The simulator discovers logical BU controls through visible parent groups, and
uses the closest group containing a BU to select sibling frame/text states. The
BU artwork visibility flag does not disable a logical control. This association
is preview policy; native enable/disable callbacks remain unresolved.

Screens whose groups are normally enabled by game callbacks may initially hide their text/controls (including team-selection variants). Use the embedded FEL tree visibility controls to enable those authored groups explicitly; the simulator does not infer those game callbacks.
