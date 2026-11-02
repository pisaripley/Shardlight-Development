# CST-320 VR Game Development

Unity VR puzzle project with controller and hand-tracking support.

## Open the project

1. Clone this repository.
2. In Unity Hub, add the repository folder as a project.
3. Open it with **Unity 6.3 LTS (6000.3.25f1)** and allow Unity to import assets and restore packages.
4. Open `Assets/Scenes/SampleScene.unity`.

The repository includes the complete `Assets`, `Packages`, and `ProjectSettings` folders. Unity regenerates its `Library`, `Temp`, and other local cache folders.

## Play

The project uses OpenXR and is configured for Meta Quest, including hand tracking tested through Meta Quest Link. Connect the headset through Link before starting Play in the editor. Read the welcome instructions and select **BEGIN**.

- **Controllers:** left thumbstick moves; right thumbstick snap-turns. Aim the left controller at the floor and click its thumbstick to teleport. Press the left X button for settings. Use the trigger to select and drag.
- **Hands:** aim the left hand at the floor and pinch to teleport. Hold the left palm open, flat, and facing up for settings. Point and pinch with the right hand to select and drag.
- **Settings:** master-volume and puzzle-tone sliders, plus return-to-start and close buttons.

## Puzzles

- **Echo Orbs:** repeat the flashing colour pattern.
- **Lights Out:** follow the blue path to the second station. Selecting a tile changes it and its adjacent neighbours; turn all nine tiles blue.

Both puzzles support controllers and hand tracking.

## Wrist inventory

Gameplay scenes with `ShardlightMenus` create a compact five-slot selector automatically.

- **VR hands:** a watch stays on the back of the left wrist. Raise it toward you and look directly at its face for 0.65 seconds to expand a sideways inventory panel above the watch, about 30 cm wide. Pinch with your right hand and move it sideways by 6.5 cm per item; release to select. Each pinch keeps a fixed scroll direction and measures movement relative to the wrist so tracking rotation cannot generate runaway scrolling. Steps are limited to one every 0.2 seconds. Looking away closes the bar after a short delay; losing hand tracking cancels an unfinished selection. The watch face remains visible whenever the left hand is tracked. The game keeps running.
- **Desktop:** press **Tab** (or **I**) to toggle the selector. Use the **middle mouse scroll wheel** to cycle items with a sliding transition; selection wraps around and rapid wheel input is queued. Tab or Escape closes it. Pause, settings and dialogue hide it.
- Collectables (`InventoryItem`), shards and NPC item rewards populate the same inventory. Assign an optional sprite in a collectable's **Icon** field to override the default artwork. Sword, bow, bomb, torch, key and potion items use their generated icons automatically; runes, shards and other items use the rune icon as a fallback. PNG sources and Unity sprite settings are in `Assets/Resources/InventoryIcons`; `generation-prompts.json` records the built-in image generation prompts. An empty inventory displays a hint. In Editor Play Mode, **Example Inventory In Editor** on `ShardlightMenus` is enabled by default and adds seven example items for scrolling tests; disable it to test normal collection. Example items are never added in builds.
- `Inventory.SelectedItem` exposes the current selection. Subscribe to `Inventory.SelectionChanged` for equipment or power behavior. Selecting an item alone does not consume it or activate a power. Inventory is retained between scene loads for the current session and cleared by Begin; disk saves are not implemented.
- The saved `WristInventory` component exposes wrist height/scale, gaze dwell, pinch thresholds, watch rotation and scroll distance for headset tuning. Physical dimensions and interaction distances account for scaled XR origins. Wrist interactions require XR Hands tracking through the player's `XROrigin`; desktop needs no hand setup.

## VR gameplay setup

Forest, Mountain Pass and Ancient Ruins save `WristInventory` and `LeftTeleportAim` components on their gameplay UI object, including references to the scene XR rig and teleportation provider. Use **Tools > Shardlight > Apply and save VR gameplay setup** in Edit Mode to reapply these defaults. It refuses to run in Play Mode.

Aim the left hand down at a clear floor or terrain surface and make a fresh thumb/index pinch to teleport. A curved arc and landing marker indicate a valid destination. Steep surfaces, occupied landing spaces and player colliders are excluded. Menus and the expanded inventory block teleportation. Existing controller teleportation remains handled by XRI.

In VR, a compact persistent status display keeps objectives, shard count, alert status and watch guidance visible independently of hand tracking. The same information is available on the wrist. Brief notifications appear at a fixed world position and fade. Pause/settings menus respect the XR origin scale and use XRI hand/controller UI interaction.

**Tools > Shardlight > Validate XR fallback** runs isolated Edit Mode checks for restoring head/hand/controller components, sideways watch layout, wrist placement at normal and quarter scale, and all seven icon imports. Headset gesture feel still requires a hardware playtest.

The hand visualizer skips absent optional joints, and the Edit Mode setup removes null bone references from both the Explorer Player prefab and scene rigs. Original hand mesh materials are retained. Only the detected platform hand pair activates, preventing overlapping Quest and Android XR visuals. Invalid legacy blend-shape receivers are disabled. Run `git lfs pull` after cloning to download the actual hand FBX models.
