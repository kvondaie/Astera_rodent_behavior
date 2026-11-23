# Lick port setup — dummy's guide (2 ports)

## You need
- Harp Behavior board (with its 12 V adapter + USB)
- 1 Janelia lick board (JF-SV-LP0001)
- 2 Lee valves (LHDB1233518H, 12 V)
- 2 ordinary Ethernet patch cables
- 2 jumper shunts (0.1″, e.g. DigiKey A26242-ND)
- Multimeter
- A short piece of wire (for the bench test)

## 1. Jumpers on the Janelia board (power OFF)
Put a shunt on **JP5 pins 1–2** and another on **JP5 pins 3–4**.
(Pin 1 is usually the square pad.) Without these the lick board gets no power.

## 2. Valves into the Janelia board
- Valve 1 → **P1**
- Valve 2 → **P2**
Either way round. No diode needed — the Harp board has them.

## 3. Cables
- Harp **Port 0** ⟶ cable ⟶ Janelia **J4**
- Harp **Port 1** ⟶ cable ⟶ Janelia **J6**
Harp Port 2 stays empty (for the 3rd port later).

## 4. Power up the Harp
Plug in the Harp's **12 V adapter** and **USB**. Nothing else needs power —
the Janelia board and valves get theirs through the cables.

## 5. Two meter checks
Black probe on any Janelia ground (J3 pin 1). Red probe on:
- **JP5 pin 4** → should read **~5 V**. (If 0 V: check jumpers and cable.)
- **JP1 pin 3** → just note the reading (0 V or ~5 V). This pin goes to the
  headbar later; tell Claude what you saw.

## 6. Test the licks (no mouse)
1. Open Bonsai → `harp\lickport_test.bonsai` → press **F5**.
2. With the spouts wired (spout 1 → JP1 pin 1, spout 2 → JP1 pin 2), touch a
   plain wire from **JP1 pin 3 (V+)** to spout 1, then to spout 2. Each touch = one "lick".
   Safe: the board limits the current with 10 MΩ resistors on each spout.
   (No spouts yet? Touch JP1 pin 3 straight to JP1 pin 1 or pin 2.)
3. Stop (**Shift+F5**) and open the new `lick_log….csv` in the harp folder.
   You should see `Lick0` (spout 1) or `Lick1` (spout 2) flip 0 → 1 → 0.
4. Nothing? Turn trim pot **R10** (spout 1) or **R21** (spout 2) a bit and retry.

## 7. Test the valves
With the workflow running, press **1** (valve 1) or **2** (valve 2).
You should hear a click. With water connected, one drop per press.
Pulse length = 15 ms; change it via `ValveMs0` / `ValveMs1` (click empty canvas → Properties, then restart).

## 8. Real use
- Spout 1 wire → **JP1 pin 1**, spout 2 wire → **JP1 pin 2**
- Headbar wire → **JP1 pin 3**

## If something's wrong
| Problem | Check |
|---|---|
| No 5 V at JP5 pin 4 | Jumpers on JP5? Harp 12 V adapter plugged in? Cable fully clicked in? |
| Licks never show | Pots R10/R21; test wire touching JP1 pin 3 and the right spout |
| Lick stuck at 1 | Turn the pot the other way; spout touching something metal? |
| No valve click | Harp 12 V adapter; valve seated in P1/P2; right key (1 or 2) |
| Bonsai error | Only one workflow can use COM4 at a time |
