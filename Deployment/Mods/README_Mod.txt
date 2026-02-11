# PhantomLink MelonLoader Mod

## Installation:

1. Run the game once with MelonLoader installed (without this mod) so it generates its folders.
2. Choose the correct mod build:
   - net6: Deployment\Mods\net6\PhantomLink.MelonIntegration.dll
   - net35: Deployment\Mods\net35\PhantomLink.MelonIntegration.dll
3. Copy it to the game:
   GameFolder\Mods\PhantomLink.MelonIntegration.dll
4. Start Deployment\PhantomLink.exe
5. Start the game
6. The mod will automatically:
   - Connect to the Runtime Tool GUI
   - Provide universal runtime editing capabilities
   - Support both IL2CPP and Mono game types

## Common Mistakes
- Copying the mod DLL into Deployment\Mods\ and forgetting to copy it into GameFolder\Mods\
- Using the net6 mod on a net35 MelonLoader install (or vice versa)
- Not running the game once after installing MelonLoader (folders not generated yet)

## Features:
- Universal compatibility across all MelonLoader versions
- Automatic IL2CPP/Mono environment detection
- Named pipe communication with Runtime Tool GUI
- Background thread processing (non-blocking)
- Error resilience with comprehensive fallbacks

## Requirements:
- MelonLoader installed in the game
- PhantomLink GUI running (for full functionality)
