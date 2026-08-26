# fluid-like-crowd

A dense crowd that does not walk through itself, and six things you can drop on it while it does.

Unity 6, URP, Burst and the job system. One scene, press play.

![the crowd at a chokepoint](screenshot.png)

## Why it does not overlap

Steering-force crowds are in a race they cannot win. A body walking into a packed crowd puts a
full step of overlap back every frame, and the solver spends that frame taking the same step
out again, so more iterations only lose more slowly. On a contacts-only version of this I
watched a chokepoint settle above two hundred per cent floor coverage, which is bodies standing
inside each other.

So it is done twice. `CrowdContinuum` measures the crowd as a medium and solves for the
pressure that slows walkers down before they reach a jam, so the overlap never gets made.
`CrowdSolver` then moves bodies until nobody is inside anybody else or inside a wall, and reads
velocity back off where they ended up rather than off what was intended. That last detail is
the one worth stealing: a shove that displaces the front rank becomes the front rank's
velocity, which displaces the second, and that the third. Nobody modelled a wave. Press `4`
into a packed corridor and watch it cross ranks it never touched.

Three contact passes do what sixteen could not.

## What is in here

`Assets/Crowd` is the solver and knows nothing about a scene — the pressure solve, the Jacobi
contact pass, signed distance to the rock by Danielsson's two sweeps, a flow field that runs
down the middle of a corridor instead of scraping its inside wall, and a counting-sort spatial
hash. `Assets/Demo` is the map, the abilities and the drawing.

## The map

300 by 176 units, about thirty-six thousand of which you can walk on, and it is a tour rather
than a level: nine stretches in a row, each asking something different. A comb with seven
identical gaps. A funnel closing from a hundred and forty units of frontage down to sixteen,
with a twelve-unit gate immediately behind it. A lattice that draws lanes. The funnel and the
gate stop ten units short of the top and bottom edges, so there is a long way round as well as
a short one — a gate that is the only route has nothing to decide.

None of that is in code. It is in the scene under an `Arena` object, one `Map Box` per piece,
moved and sized with the ordinary transform tools; drag in `Assets/Demo/Rock.prefab` for
another. Sealing a corner off this way is easy, so the load reports any walkable ground the
wavefront could not reach.

## The six abilities

| key | | |
|---|---|---|
| `1` | Arrow rain | wide and soft, thins a crowd rather than clearing one |
| `2` | Mortar | fewer and heavier, on a wider patch |
| `3` | Strafing run | drag a line, the impacts march down it |
| `4` | Shockwave | catches nothing, throws everything inside thirty-four units |
| `5` | Stasis field | throws nothing, holds everything still for two and a half seconds |
| `6` | Atom | fourteen units of circle, everything inside it gone |

Nothing has a wind-up, a cooldown or a limit. A cast goes off on the frame the button comes up
and stays armed afterwards, so you can drop the same thing on the same jam twice running with
one thing changed in between. Nothing is caught twice either: a cast is dozens of impacts and
they count as one event.

## Controls

| | |
|---|---|
| `1`–`6` | arm an ability. Click to drop, drag for the strafing run |
| right click / `Esc` | put it away |
| wheel / middle drag / `Z` | zoom about the cursor, pan, fit the map |

The solver's knobs are not on keys. They are `CrowdTuning.Default()`.

## Running it

Unity 6000.5.7f1 or newer. Clone, open `Assets/Scenes/CrowdDemo.unity`, press play. Nothing to
wire up.

Body radius is the number that decides how many fit. At 0.2 a hundred thousand bodies cover
about a third of the floor; at 0.3 the same hundred thousand would want seventy-eight per cent
of it, and the population quietly plateaus at half of what you asked for.

## Where it came from

This is the defence-phase crowd out of a tower defence I am building, pulled out into something
small enough to read. The solver files are the shipping ones.
