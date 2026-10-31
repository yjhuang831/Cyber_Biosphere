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
  - If a save with the same name already exists, it is overwritten.

- All save, species-library, and preference files are written atomically (temp file, then swapped into place),
  so a crash mid-write keeps the previous file instead of leaving a truncated one.

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

- Full snapshot load path is implemented, in two phases:
  - Phase 1 (no scene changes): parse, validate, migrate legacy schemas, rebuild all genomes. A bad file fails here and the running simulation is untouched.
  - Phase 2: rebuild map size/topology, restore environment settings (tile-level values are recalculated from them),
    plants, seeds, counters, time state, and the selected season.

- Schema `15` (current): seeds save `carbon`, moved from the parent and passed to the seedling; older saves load with seeds holding none.

- Schema `14`: soil organic carbon removed; older saves load without it, and any carbon they held there is dropped.

- Schema `13`: dissolved carbon removed; plants save `structuralCarbon` (all carbon in the plant) and `availableCarbon` (its internal budget, a subset) instead of `carbon`.

- Schema `12`: plants save `starchCapacity` and `starchStored`; older saves start with no storage.

- Schema `11`: genera and binomial names. Genomes save `genusId`; `genera` lists each genus (id, name, library
  flag, species list, parent, founding day, founder colour, representative genome indices). `nextGenusId` and
  `nextGenusNameIndex` continue the sequences. Older saves are grouped and named at the next dawn.

- Schema `9`: carbon cycle.
  - Per-tile carbon/nutrient pools and `atmosphericCarbon` are doubles; tiles add `litterCarbon` (decay pool).
  - Plants save `carbon` (biomass).
  - Genome strands are sorted by trait id (restored on load); the old `dominantDNA` field is gone.
  - Loading schema < 9: plants start with 0 biomass carbon (refilled by photosynthesis), and the atmosphere is topped
    up to `referenceAtmosphericCarbonPerTile` per tile if lower, because CO2 now drives photosynthesis.

- Schema `8` (schemas `6` and `7` remain loadable and are migrated in memory on load):
  - Tiles hold environment only. Plants (`plants`) and seeds (`seeds`) are separate lists keyed by tile coordinates.
  - Every genome is stored once in `genomes` and referenced by index (`-1` = none); seeds from one parent share one entry.
  - Genomes carry `speciesGuid`, the library species identity (see Species Library below).
  - Saves the exact day phase (day/night switch state, accumulators, germination schedule) and `currentSeason`.
    Legacy saves reconstruct the day phase from the light level, skip germination for the rest of the loaded day,
    and infer the season from the closest seasonal temperature.
  - Saves seed `germinationCue` / `coldPreservation`, plant `nightEffect` and per-day clone attempts, and the
    `maxCloneAttemptsPerDay` setting (older saves load it as 0 = unlimited).
  - Genomes no longer need names to be saved; any genome with DNA is kept (previously unnamed genomes were silently dropped).
  - Known gap: an in-progress season transition is not resumed; the loaded temperature/noon stay at their mid-blend values.

- Species Library (`persistentDataPath/species`, managed by `GenusManager`):
  - Each species is identified by a GUID; genus/species names are display labels. New files are named `<guid>.json`.
  - Name clashes (case-insensitive) are resolved by suffixing the epithet: `bar` -> `bar (2)`.
  - Legacy files (no GUID) get a deterministic content-derived GUID and are upgraded in place on first load,
    so bundled default species get the same GUID on every install.
  - `GenusManager.RenameSpecies(guid, genus, species, out finalName)` renames an entry (suffixing on clash) and
    updates matching genomes in a running simulation. `LibraryChanged` fires after any add/rename/delete.

- Schema `7` notes (still apply):
  - Includes saved `noonTimeHours` for day/night continuity.
  - No longer stores per-tile caches for `temperature`, `hydration`, `light`, `radiation`.
  - Keeps per-tile `oxygen` so local oxygen state from grass/environment history is preserved.
  - No longer stores genome `phenotypes`; phenotypes are rebuilt from DNA on load.
  - Adds map-level available N/P/K pools plus the atmospheric carbon reservoir.
  - Loader supports schemas `6` to `15`; older schemas are rejected.
