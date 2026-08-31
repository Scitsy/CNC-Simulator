O0015 (PANEL SWITCH DEMO - SINGLE BLOCK / BLOCK SKIP / OPT STOP)
(STOCK: 3.0 IN DIA X 4.0 IN LONG - THE POWER-ON DEFAULT)
(LEADWELL LTC-208 / FANUC 0i-TF PLUS)
(
(  Exercises the three operator-panel switches that actually change how a
(  program runs. Try it four times, changing one switch each time:
(
(    all off      - runs straight through, both roughing passes, no stops
(    BLOCK SKIP   - the two / blocks are skipped, so the second pass and
(                   the second facing cut never happen
(    OPT STOP     - pauses at M01, partway through; press Cycle Start again
(    SINGLE BLOCK - one block per Cycle Start press, all the way down
(
(  Watch the console: skipped blocks are reported, and M01 says which way
(  the switch was set.
(

G20 (INCH)
G99 (FEED PER REV)
G18
G40 G80

N10 (FACE)
T0101
G97 S1200 M03
G00 X3.2 Z0.02
G94 X0 Z0.0 F0.006
/G94 X0 Z-0.02 F0.006  (second facing cut - skipped by BLOCK SKIP)

N20 (ROUGH THE OD IN TWO PASSES)
G00 X3.1 Z0.1
G90 X2.85 Z-1.5 F0.010
X2.70

M01 (OPTIONAL STOP - pauses only when OPT STOP is armed)

/G90 X2.55 Z-1.5     (third roughing pass - skipped by BLOCK SKIP)

N30 (FINISH)
G00 X3.2 Z0.1
G96 S450 M03
G00 X2.50 Z0.05
G01 Z-1.5 F0.006
G01 X3.05
G97 S600

N40 (PARK)
G00 X3.2 Z0.5
G28 U0 W0
M05
M09
M30
