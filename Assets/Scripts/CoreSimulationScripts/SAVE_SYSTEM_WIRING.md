# Save System Wiring Guide

This document explains how to wire the current save system in Unity after the recent changes.

## 1. Current Save Behavior

- Autosave:
  - Writes to: `Application.persistentDataPath/Autosaves`
  - File name format: `sim_autosave_yyyyMMdd_HHmmss.json`
  - Keeps max 10 autosave files, deletes oldest when exceeding 10.
  - During autosave: simulation pauses, an autosave status text is shown, then simulation resumes to previous time scale.

- Manual save:
  - Writes to: `Application.persistentDataPath/ManualSaves`
  - Uses player-provided name as filename.
  - If same name already exists, a timestamp/suffix is appended automatically.

- Loading:
  - Fully separated into `SimulationLoadSystem`.
  - Main menu queues the chosen snapshot path/name, then loads the simulation scene.
  - `TimeController.Start()` auto-applies any queued snapshot after scene initialization.
  - Input supports:
    - Bare file name, example: `my_save`
    - File name with extension, example: `my_save.json`
    - Relative path from `persistentDataPath`, example: `Autosaves/sim_autosave_20260409_193000.json`
    - Absolute path.

## 2. TimeController Fields To Assign In Inspector

On the GameObject with `TimeController`, assign these references:

- Existing:
  - `environmentManager`
  - `grassManager`
  - `seedManager`
  - Existing UI texts/sliders already used by your simulation.

- Autosave guard UI:
  - `autoSaveStatusText`: a `Text` UI element that will show "Autosaving...".
  - `autoSaveStatusMessage`: optional custom message.

- Manual save input:
  - `manualSaveNameInputField`: `InputField` where player types manual save name.

## 2b. MenuManager Fields To Assign In Inspector

On the GameObject with `MenuManager`, assign:

- `loadFileNameInputField`: `InputField` where player types file name/path to load.
- `simulationSceneName`: the exact simulation scene name to open after queueing load.

## 3. Button Wiring

### Manual Save Button

- Wire button `OnClick` to:
  - `TimeController.SaveSimulationNow()`

Behavior:
- Reads text from `manualSaveNameInputField`.
- If empty, uses fallback name like `manual_save_yyyyMMdd_HHmmss`.
- Saves into `ManualSaves`.

### Load Button

- Wire button `OnClick` to:
  - `MenuManager.StartSimulationFromLoadInputField()`

Behavior:
- Reads text from `loadFileNameInputField`.
- Resolves file in autosave/manual directories (or path input).
- Queues that load request in `SimulationLoadSystem`.
- Loads `simulationSceneName`.
- `TimeController` applies the queued snapshot automatically on scene start.

## 4. Optional Direct Calls (Code)

- Manual save with explicit name:
  - `timeController.SaveSimulationWithName("my_name");`

- Queue load from main menu by explicit filename/path:
  - `menuManager.StartSimulationWithLoad("my_name.json");`
  - `menuManager.StartSimulationWithLoad("Autosaves/sim_autosave_20260409_193000.json");`

- Advanced direct API (without menu helper):
  - `SimulationLoadSystem.QueueSnapshotLoadByIdentifier("my_name.json");`
  - `SimulationLoadSystem.QueueSnapshotLoadByIdentifier("Autosaves/sim_autosave_20260409_193000.json");`

## 5. Notes From Previous Changes

- Full snapshot load path is implemented:
  - Rebuilds map size/topology.
  - Restores environment settings, then recalculates tile-level environment values from those base settings.
  - Restores grass instances, seeds, genomes, genus/species runtime registry, counters, and time state.
  - Applies saved genus/species history from snapshot so historical entries persist (including non-living records).
  - `GenusManager.preserveHistoricalSpecies` defaults to `true`, so runtime pruning does not delete extinct history entries.

- Schema version is now `6`:
  - Includes saved `noonTimeHours` for day/night continuity.
  - No longer stores per-tile caches for `temperature`, `hydration`, `light`, `radiation`.
  - Keeps per-tile `oxygen` so local oxygen state from grass/environment history is preserved.
  - No longer stores genome `phenotypes`; phenotypes are rebuilt from DNA on load.
  - Replaces genome `cachedDNAComposition` with compact `dominantDNA` strand indicator (`0 = M`, `1 = P`).
  - Loader expects current schema only (prototype mode, no backward compatibility path).
