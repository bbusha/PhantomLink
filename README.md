# PhantomLink

## What this Release Actually IS
This release is a unity mod tool with an external UI and Universal compatibility between MelonLoader 5.5 up to 7.1
it is a more powerful tool heavily inspired by unityExplorer, with many new feature being added with each release
works with Il2Cpp and Mono Unity games

has multiple tabs Including 

--
-Types
-Classes
-Method Browser
-Properties
-Scene Browser
-Hierarchy
-Game Controls
-Object Finder
-Scene Hierarchy
-Factory Browser (attempts to find Object Spawners)
-Field Editor
-Patch Manager
-Logs
--

should be compatible with ANY unity game, if its not, create a issue in issues

this release is completley free, after days debating on implementing it as a paid service, i decided everyone deserves it for free.
however with that stated, this tool will not be Open Source. and i would prefer if no one releases this tool as a copy or as a paid service if they reverse engineer it or just clone the repo.








## What This release Contains
- PhantomLink.exe (published output)
- Mods\
  - net6\PhantomLink.MelonIntegration.dll
  - net35\PhantomLink.MelonIntegration.dll

This deployment intentionally does NOT bundle the game's Steam/Unity/System assemblies.

## Quick Start (Recommended)
1. Install MelonLoader into the game first 
2. Run the game once (without this mod) so MelonLoader generates folders.
3. Copy this entire Deployment folder into your game's directory (same folder as the game .exe).
4. Copy the correct mod DLL into the game's Mods folder (see “Installing The Mod” below).
5. Run PhantomLink.exe.
6. Start the game. The mod and the GUI will connect automatically.



## Supported MelonLoader Versions: 5.5 - 7.1

The tool automatically detects and works with these MelonLoader versions:
- Version 7.x: Looks for MelonLoader.dll
- Version 6.x: Looks for MelonLoader_ml.dll  
- Version 5.x: Looks for MelonLoader.ModHandler.dll

## Installing The Mod
1. Make sure the game has a Mods folder:
   - If it does not exist yet, run the game once with MelonLoader installed.
2. Pick the correct build:
   - If your MelonLoader uses .NET 6: use Deployment\Mods\net6\PhantomLink.MelonIntegration.dll
   - If your MelonLoader uses .NET Framework/.net 35 (older): use Deployment\Mods\net35\PhantomLink.MelonIntegration.dll
3. Copy the chosen DLL to:
   GameFolder\Mods\PhantomLink.MelonIntegration.dll

If you’re unsure which one to use:
- Try net6 first on modern MelonLoader installs.
- If the game logs show missing System.* Version=6.0.0.0 errors, switch to net35.

## Running The Tool
1. Start the GUI:
   - PhantomLink\PhantomLink.exe
2. Start the game.
3. In the GUI:
   - Use the Types/Classes/Methods/Properties views to inspect runtime types.
   - Runtime reflection is restricted to Assembly-CSharp and Unity assemblies to avoid noise.

## Requirements:
- .NET 6.0 Desktop Runtime (version 6.0.0 or later)
- MelonLoader installed in the game directory

## Troubleshooting:
### GUI won’t start
- Install .NET 6 Desktop Runtime:
https://dotnet.microsoft.com/download/dotnet/6.0

### Mod doesn’t load
- Confirm the mod DLL is in GameFolder\Mods\
- Run the game once with MelonLoader installed so it generates folders
- Check the game’s MelonLoader logs/console output for errors

### GUI and mod don’t connect
- Start the GUI first, then the game
- Make sure you didn’t copy the mod DLL into Deployment\Mods\ only (it must go into GameFolder\Mods\)

### Nothing shows up / too many types
- The tool intentionally limits metadata browsing to Assembly-CSharp and Unity assemblies.
- If a game uses additional gameplay assemblies (not Assembly-CSharp), add them to the allow-list in MetadataLoader.
