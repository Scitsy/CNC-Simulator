# Custom Macro B

Custom Macro B is FANUC's embedded macro programming language - variables, arithmetic, conditional
branches, and loops, written directly into G-code blocks. It's what turns a fixed sequence of moves
into something closer to a real program: a bolt-circle drilling routine that takes a hole count as
a parameter, a groove pattern that repeats until a variable says stop, a subroutine that reports
back through a shared variable. This is a write-up of what's implemented here, why the scope stops
where it does, and two real bugs that came out of building it.

## What's supported

- **Variables**: `#1`-`#33` local (reset for each `G65`/`G66` call, restored when the call returns -
  nested calls each get their own fresh set), `#100`-`#999` common (shared everywhere, persist for
  the whole run), and `#0` (always null). Assign with `#1=5` or `#1=[#2+3]`.
- **Expressions**: `+ - * /` and `MOD`, unary minus, and `SIN`/`COS`/`TAN`/`ATAN`/`SQRT`/`ABS`/
  `ROUND`/`FIX`/`FUP` (trig in degrees). Grouping uses square brackets - round parens are already
  spoken for as comments in G-code, so `[[#1+#2]*2]` does the job `(...)` would elsewhere.
- **Branches**: `IF[condition] GOTO n` jumps to the block carrying `Nn` if true, otherwise falls
  through; a bare `GOTO n` always jumps. Conditions compare two expressions with
  `EQ`/`NE`/`GT`/`LT`/`GE`/`LE`. Compound conditions chain with `AND`/`OR`, each in its own
  brackets - `IF[#1 GT 0] AND[#2 LT 10] GOTO 50` - evaluated strictly left to right (no precedence
  between `AND` and `OR`, so mixing them needs parentheses of intent, not just of syntax).
- **Loops**: `WHILE[condition] DO1 ... END1` - matching `DO`/`END` numbers (1-3) allow nesting up to
  three loops. The condition is re-checked every time execution reaches `DO`.
- **Macro calls**: `G65 P(program) L(repeat) <args>` calls a macro once; argument letters (`A B C I
  J K D E F H Q R S T U V W X Y Z`) map to fixed local variables inside the called macro (FANUC's
  own "Argument Specification I" mapping). Every `G65` call gets a fresh, isolated set of locals,
  and calls nest - a macro can call another macro, up to a depth cap.
- **`G66`/`G67` modal calls**: same argument syntax as `G65`, but instead of running once, `G66`
  arms the macro to auto-fire (with the same captured arguments) before every later block that
  commands axis motion, until `G67` cancels it.
- **System variables**: `#4001` (active motion mode - `0/1/2/3` for `G00/G01/G02/G03`), `#5001`/
  `#5002` (current work-coordinate X/Z). Read-only; writing to them raises an alarm.

## What's deliberately out of scope

Indirect addressing (`#[expr]` - using an expression's *result* as a variable number) and multiple
statements per block aren't implemented. Both are real Custom Macro B features on an actual
control, but neither came up as something a realistic lathe demo program actually needed, and both
add real parsing complexity for a benefit this project hasn't needed to cash in on. Same reasoning
for system variables: `#4001` and `#5001`/`#5002` exist because a real macro needed them (see
below) - there's no attempt at the fuller `#1000`-`#9999` range a real control exposes (tool
offsets, alarm status, and so on) unless a specific macro idea needs it.

## Worked example: G66/G67 in O0011

`O0011_modal_macro_demo.nc` cuts three witness grooves at different Z positions using one macro
body, armed once:

```gcode
T0505
M03 S600
M08
G00 Z2
G00 X26
G66 P9020 D18        ; arm the modal call - D=18 becomes the macro's target diameter
#150=-10
G00 Z-10              ; <- G66 fires O9020 here, using #150 as the groove's Z
#150=-20
G00 Z-20               ; <- fires again
#150=-30
G00 Z-30                ; <- fires again
G67                      ; cancel
G00 X26 Z2
M09
M00

O9020 (D=TARGET DIAMETER, Z FROM COMMON #150)
G00 Z#150
G75 R0.3
G75 X#7 Z#150 P500 F0.08
M99
```

`G66`'s own arguments (`D18` here) are captured once, at arm time - so the per-stop Z can't travel
through them. The per-stop value instead rides in a common variable (`#150`), set by the main
program right before each triggering move. That's a real workaround, not an aesthetic choice: at
the time this program was written, a macro had no way to ask "where am I" - `#150` was the only
channel available to hand it a changing target. That gap is exactly why `#5001`/`#5002` exist now;
a macro written today could read its own position directly instead.

## Bug: the self-recursion trap

The mechanism above - fire the armed macro before any subsequent motion block - has one sharp edge:
the macro's *own* body also contains motion (`G00 Z#150` positioning to the target before cutting).
Naively, that move is itself a "subsequent motion block," which would re-trigger the same modal
macro from inside itself - which positions and cuts again, re-triggering again, recursing until
something stops it. Left unguarded, one `G66` line would blow through the call-depth limit instead
of just cutting the one feature it was meant to.

The fix is a depth check in the trigger point (`LatheSimulator.cs`'s `TriggerModalMacroIfArmed`,
called from `RunBlockRange` just before a block's own motion executes):

```csharp
// _callDepth == 0 restricts firing to the top-level call chain only - never inside the
// modal macro's own body (or any other nested M98/G65 call). Without this, the modal
// macro's own motion (e.g. "G00 Z#150" positioning to its target before cutting) would
// immediately re-trigger the same modal call from inside itself, recursing until the
// call-depth cap aborts it instead of just cutting the one feature it was meant to.
if (_modalMacroActive && _callDepth == 0 && HasMotionWord(block))
    InvokeMacroProgram(_modalMacroProgram, _modalMacroRepeat, _modalMacroArgs, blocks);
```

`_callDepth` is already tracked for the ordinary nested-call-depth cap; reusing it here as a "am I
inside a call right now" flag was enough to close the hole without new state. The lesson generalizes
past this one feature: any "fire on the next matching event" mechanism needs to ask whether its own
side effects also match the event it's listening for.

## Try it yourself

`O0011_modal_macro_demo.nc` and `O0009_macro_groove_pattern.nc` (a `WHILE`-loop-driven groove
pattern) are the two clearest demonstrations. `O0013_g74_and_system_vars_demo.nc` shows the newer
system variables in isolation - run it and watch `#101`/`#102`/`#110`-`#113` populate on the MACRO
screen as the program reads back its own position and motion mode.
