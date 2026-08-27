# KSP-BulldozerMod

A Kerbal Space Program (KSP 1.12) mod that **clears the rocks (Parallax scatters) from a strip in front of a rover**, permanently, and **without touching the terrain height**. It does not move soil: it stops Parallax from growing its rocks where you have driven the blade.

Requires **[Parallax Continued](https://github.com/Gameslinx/Tessellation)** — without it there is no scatter to remove, and the menu entries hide themselves.

## Using it

In flight, on a landed rover, from the part action window (PAW) of any command module:

| Entry | What it does |
|---|---|
| **Pass length** | 5 to 200 m, in steps of 5 |
| **Pass width** | 5 to 20 m, in steps of 5 |
| **Highlight rocks that would go** | in-place preview, and an exact one: it samples the real mask rather than an approximation of it |
| **Blade pass (reloads the game)** | applies the pass — **saves and reloads the game** |

The strip starts at the middle of the vessel and runs forward, along the **navball heading**. The control point must therefore look along the ground ("Control from here" on a part that faces forward); otherwise the pass is refused with a message.

Clearing goes through a reload because Parallax decides which scatters exist **when it builds a terrain tile**, so a fresh, cold build is needed. That is harmless here — no height is ever modified, so the rover comes back exactly at its saved pose.

## Building

`build.bat` (Windows) or `build.sh`, with the `KSPDIR` environment variable pointing at the KSP install. Produces `Release/BulldozerMod.zip`; `install.bat` deploys it. On a fresh clone, run `git submodule update --init` first (the shared `KSP-Shared` code is a submodule).

## Documentation

**Everything is in [CLAUDE.md](CLAUDE.md)** (written in French): the need it answers, how the Parallax exclusion mechanism works, the mask geometry, the traps that cost real time, what has been verified in game, and what is left to do. That is the entry point for picking the subject back up cold.
