# Harp Behavior + Actuonix L12-R servo — setup notes

Driving an **Actuonix L12-R** micro linear servo (6V, RC-servo control) from a
**Harp Behavior board** (Open Ephys OEPS-1216, WhoAmI 1216) via **Bonsai**.

## ⚡ The one thing that will bite you: POWER

The board needs **TWO power sources connected at the same time**:

1. **USB** — data + the board's 3.3V logic (this is what gives you the COM port).
2. **Barrel jack (~12V)** — the board's **own** supply. This 12V feeds the DC-DC
   that generates the **+5V rail**, and *that rail powers every digital output
   (DO0–DO3) and the servo outputs.*

If the barrel is unplugged, the board still enumerates and comms/registers/ADC all
work — but **every output is dead (0V), so the servo won't move.** Symptom looks
like software but isn't. (Traced in the board file:
`JACK +12V → L1 → IC2 (DC-DC) → +5.45V → IC5 → +5V → output drivers`. USB VBUS only
feeds the FT232 + the 3.3V logic regulator, never the +5V rail.)

The **6V supply is for the ACTUATOR motor only** — it is separate from the board's
12V. Keep them distinct.

> Note: an early "the barrel jack breaks USB enumeration" scare was a red herring —
> that was a charge-only USB-C cable. Barrel 12V + USB coexist fine.

## Wiring

**Output terminal block** (6 screw terminals), left → right:

```
[1 GND] [2 DO0] [3 DO1] [4 GND] [5 DO2] [6 DO3]
```

Servo signal is only available on **DO2 (pin 5)** and **DO3 (pin 6)**.

Actuator (L12-R: white = signal, red = +6V, black = ground):

| Wire | To |
|------|----|
| white (signal) | Output **pin 5 (DO2)** |
| red | 6V supply **+** |
| black | 6V supply **–** |
| (jumper) board **GND (pin 4)** | 6V supply **–**  (common ground — required) |

Plus: board **barrel 12V** connected, **USB** connected.

## Software

- **Bonsai 2.9.1** (portable) at `%LOCALAPPDATA%\Bonsai`.
- Packages: **Bonsai.Harp 3.5.0**, **Harp.Behavior 0.2.0** (from nuget.org).
- Device firmware observed: FW 3.3, HW 2.1, Core 1.15.

### Run it
Open **`servo_manual_control.bonsai`**, press **Start (F5)**. Click empty canvas →
Properties panel → set **`Position`** (an externalized property) to **1000–2000**:

- **1000** = fully retracted
- **1500** = mid
- **2000** = fully extended

The value is live-tunable while running (a 20 Hz timer re-sends it). Under the hood
the workflow sends `EnableServos = ServoOutput2`, `ServoMotor2Period = 20000` µs
(50 Hz), and a continuously-refreshed `ServoMotor2Pulse` (1000–2000 µs).

Other workflows in this folder: `servo_sweep_test.bonsai` (auto strokes in/out) and
`loopback_test.bonsai` (diagnostic: toggles outputs, reads them back on an input).

---

# Sequence task (human / keyboard pilot)

`task_3port_keyboard.bonsai` — 3-port sequence-reproduction task, keyboard version.
Screen shows three ports; a 2-item sequence flashes during **sample**; after a
**delay** the subject reproduces it during **response**. The servo runs the epoch
structure (extended during sample + response, retracted during delay + ITI) but is
**not part of the task** — it's there to validate timing against real hardware.

`task_epochs_servo.bonsai` — just the epoch/servo loop, no task. Useful for timing checks.

### Ports and keys

| Port | Key | Screen position |
|---|---|---|
| 0 | Left arrow | left circle |
| 1 | Up arrow | centre circle |
| 2 | Right arrow | right circle |

### Trial structure (9 phases, flat)

| Phase | Default | Emits code |
|---|---|---|
| ITI | `ItiDuration` 1 s | 1 = sample onset (port extends) |
| cue lead-in | `CueLeadIn` 0.2 s | 10 = cue 1 on |
| cue 1 | `CueDuration` 0.4 s | 20 = cue 1 off |
| gap | `CueGap` 0.4 s | 11 = cue 2 on |
| cue 2 | `CueDuration` 0.4 s | 21 = cue 2 off |
| sample tail | `SampleTail` 0.3 s | 2 = delay onset (port retracts) |
| delay | `DelayDuration` 1.5 s | 3 = extend commanded |
| port travel | `PortTravel` 1 s | **5 = GO** (port arrived, respond now) |
| response | `ResponseDuration` 3 s **max** | 4 = ITI onset (trial end) |

Sample epoch total = 1.7 s. All nine are externalized properties, live-editable.

**The response phase ends on the second distinct press, not on the timer.** It is an
`Amb` race between the `ResponseDuration` timer and the key stream through
`DistinctUntilChanged -> Take(2) -> TakeLast(1)`, whichever fires first. So:

- Two different ports pressed -> trial ends immediately, code 4 fires on that press.
- Same port twice (e.g. `0,0`) -> does **not** end the trial; `DistinctUntilChanged`
  collapses it, exactly matching the response-scoring rule. This is deliberate: it is
  the keyboard analogue of a lick bout, which must not count as two visits.
- Fewer than two distinct ports -> the timer ends the trial at `ResponseDuration`.

Because the key stream is only subscribed when the response phase begins, presses
during the delay do **not** count toward termination — though they are still logged.

Trial length is therefore variable: 8.2 s worst case, ~6 s when answering promptly
(down from a fixed 12.5 s).

**The go cue is code 5, not code 3.** Code 3 is when the servo is *commanded* to
extend; the port is still travelling for `PortTravel` after that. Only at code 5 is
the port actually in reach — that's when the green go light appears on screen, and
it's the correct zero point for reaction times. Set `PortTravel` to the measured
travel time for whatever stroke you end up using (currently ~1 s for 7 mm).

### Log file

Written next to the workflow as `task_log_<timestamp>.csv`, one row per event:

| Column | Meaning |
|---|---|
| `Timestamp` | wall-clock time of the event |
| `Trial` | 0-based trial number |
| `Code` | event code (below) |
| `Port` | port index 0/1/2; `-1` when not applicable |

Event codes: **1** sample onset · **2** delay onset · **3** extend commanded ·
**5** GO (port arrived) · **4** ITI onset · **10** cue 1 on · **20** cue 1 off ·
**11** cue 2 on · **21** cue 2 off · **30** key press.

The cued port is on the `10` and `11` rows; the pressed port is on the `30` rows.

### Two things to know when analysing

- **Keypresses are logged at all times**, not gated to the response window — each
  `30` row carries the trial number it fell in, so filter to the response epoch
  offline. This is deliberate: early/late presses are data, not noise.
- **Sequences are currently deterministic**, cycling the 6 orderings of 2 distinct
  ports (`0→1, 0→2, 1→0, 1→2, 2→0, 2→1`) as `trial % 6`. Fine for timing validation;
  randomise before collecting real behaviour.

Reaction time = key-press timestamp (`30`) minus the **code 5** timestamp of the
same trial. Do not use code 3 — that's the command, not the arrival.
