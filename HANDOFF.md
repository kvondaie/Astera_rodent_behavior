# Project Handoff — Harp Behavior + Actuonix L12-R linear servo

**Status: ✅ Working.** The actuator moves under manual control from Bonsai.
Handing this project off (personal → Astera account). This file is self-contained
so nothing depends on the prior account's Claude Code memory or chat history.

---

## 1. What this project is

Drive an **Actuonix L12-R** micro linear servo (linear actuator) from a
**Harp Behavior board** (Open Ephys OEPS-1216) using **Bonsai**, with live manual
position control. Foundation for later work (scripted motion, syncing to Harp
events, position readback).

## 2. Hardware

| Item | Detail |
|------|--------|
| Controller | Harp Behavior board (Open Ephys OEPS-1216). WhoAmI 1216, HW 2.1, **FW 3.3**, Core 1.15 |
| Actuator | Actuonix **L12-R**, 30 mm stroke, **100:1** gear, 6 VDC, RC-servo control. No-load speed 13 mm/s, max force 42 N |
| Board power | Board's **~12 V barrel supply** (its own adapter) — REQUIRED, see §3 |
| Actuator power | Separate **6 V supply** (motor power only) |
| PC link | USB → shows up as a COM port (was **COM3** here); needs a **data** USB cable, not charge-only |

## 3. ⚡ POWER — the thing that cost us a day (read this)

The board needs **THREE power connections**, and all must be present:

1. **USB** — data + the board's 3.3 V logic (this is what creates the COM port).
2. **Board 12 V barrel** — the board's own adapter. This 12 V feeds the on-board
   DC-DC that makes the **+5 V rail**, and *that rail powers every digital output
   (DO0–DO3) and the servo outputs.*
3. **Actuator 6 V** — powers the L12-R motor only (separate circuit).

**If the 12 V barrel is unplugged, the board still enumerates and comms/registers/ADC
all work — but every output pin is dead (0 V), so the servo silently won't move.**
Verified in the board netlist: `JACK +12V → L1 → IC2 (DC-DC) → +5.45V → IC5 → +5V →
output drivers`; USB VBUS only feeds the FT232 + the 3.3 V logic regulator, never +5 V.

> A long red herring: early on it looked like "the barrel jack breaks USB
> enumeration." It didn't — that was a **charge-only USB-C cable**. Barrel 12 V and
> USB coexist fine (COM3 stayed stable with both connected). Use a known data cable.

## 4. Wiring

**Output terminal block** (6 screw terminals), left → right, from the board file:

```
[1 GND] [2 DO0] [3 DO1] [4 GND] [5 DO2] [6 DO3]
```
Servo PWM is available only on **DO2 (pin 5)** and **DO3 (pin 6)**.

**Input block** (3 terminals), left → right: `[1 DI3] [2 GND] [3 ADC1_IN]`.

Actuator (L12-R wire colors: **white = signal, red = +6 V, black = ground**):

| Wire | Connect to |
|------|-----------|
| white (signal) | Output **pin 5 (DO2)** |
| red | 6 V supply **+** |
| black | 6 V supply **–** |
| jumper: board **GND (pin 4)** | 6 V supply **–**  ← common ground, REQUIRED (or DO2's pulse has no reference) |

Plus board **12 V barrel** in, and **USB** in.

## 5. Software

- **Bonsai 2.9.1** (portable) installed at `%LOCALAPPDATA%\Bonsai` (Start-menu shortcut "Bonsai").
- Packages via Bonsai → Tools → Manage Packages (nuget.org source):
  **Bonsai.Harp 3.5.0**, **Harp.Behavior 0.2.0** (pulls in dependencies).
- Requires .NET Framework 4.x (present on the original Win11 machine).

> **Same machine, re-homing under the Astera account:** Bonsai, the packages, the
> COM port (**COM3**) and all wiring are already set up on this PC — nothing to
> reinstall or rewire. Just open the `.bonsai` files.

## 6. Files in this folder

| File | Purpose |
|------|---------|
| `servo_manual_control.bonsai` | **Main workflow.** Live manual control via a top-level `Position` property. |
| `servo_sweep_test.bonsai` | Auto-strokes the actuator fully in/out (quick "is it alive" test). |
| `loopback_test.bonsai` | Diagnostic: toggles outputs, reads them back on an input. |
| `README.md` | Operating instructions (wiring, power, how to run). |
| `HANDOFF.md` | This file. |

## 7. How to run

1. Confirm all power connected (USB + board 12 V + actuator 6 V), actuator wired per §4.
2. Open `servo_manual_control.bonsai` in Bonsai; check the **Device** node `PortName`
   matches the actual COM port.
3. Press **Start (F5)**. Click empty canvas → Properties → set **`Position`**.

**Position = servo pulse width in microseconds. Usable range 1000–2000:**
- 1000 = fully retracted (0 mm), 1500 = mid (15 mm), 2000 = fully extended (30 mm)
- Linear: `mm = (Position − 1000) / 1000 × 30`  (≈ 0.03 mm/unit)
- Stay within 1000–2000; the actuator clamps at its ends outside that.

Under the hood the workflow sends `EnableServos = ServoOutput2`,
`ServoMotor2Period = 20000` µs (50 Hz), and a 20 Hz-refreshed `ServoMotor2Pulse`
(the refresh is what makes `Position` live-editable — writing the value *inside* a
node instead does NOT update live, because Bonsai bakes node values at Start).

## 8. Gotchas already solved (don't re-derive these)

- **No movement but no errors** → check the **12 V barrel** is connected (§3).
- **COM port missing** → charge-only USB cable; use a data cable. A USB-C-to-A cable
  into a normal USB-A port is the most reliable.
- **Editing a value live does nothing** → only **externalized** properties update at
  runtime; that's why `Position` is externalized at the top level.
- **Servo registers accept writes but nothing happens** → almost always power (§3),
  not firmware — FW 3.3 supports servos (EnableServos reads back correctly).

## 9. Open items / possible next steps

- Actuator seems slower than 13 mm/s spec → likely **voltage droop** under load
  through the dupont jumpers/6 V supply; check voltage at the actuator *while moving*,
  use thicker/shorter leads or a stiffer supply if it's well under 6 V.
- Nice-to-have: a `Position_mm` (0–30) input that converts to pulse internally,
  with per-unit calibration constants.
- Not yet explored: scripted motion sequences, syncing the actuator to other Harp
  events, reading actuator position back.

## 10. Account-switch checklist (same machine, re-homing under Astera)

- [ ] Nothing to reinstall or rewire — Bonsai, packages, wiring, and **COM3** all
      stay as-is on this PC.
- [ ] The only real gap: the prior Claude Code **memory** lives under the personal
      account and does **not** transfer to the Astera account. **This file + `README.md`
      are the carryover** — start the new account's context by pointing it here.
- [ ] If you relocate the project folder, keep the files together (`.bonsai` ×3,
      `README.md`, `HANDOFF.md`) and update any absolute paths you may add later.
- [ ] Re-point any personal-account-specific settings/integrations to Astera as needed.
