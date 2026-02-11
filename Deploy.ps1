# PowerShell deployment script for PhantomLink
# Creates a standalone deployment folder with all dependencies

param(
    [string]$OutputPath = "PhantomLink",
    [string]$GamePath = ""
)

Write-Host "Creating standalone deployment for PhantomLink..."

# Create deployment directory
if (Test-Path $OutputPath) {
    Remove-Item -Recurse -Force $OutputPath
}
New-Item -ItemType Directory -Path $OutputPath | Out-Null

# Publish GUI (minimal output) and copy it into Deployment root
$publishTemp = Join-Path $OutputPath ".tmp_gui_publish"
if (Test-Path $publishTemp) {
    Remove-Item -Recurse -Force $publishTemp
}
New-Item -ItemType Directory -Path $publishTemp -Force | Out-Null

$guiProject = "PhantomLink.GUI\PhantomLink.GUI.csproj"
if (Test-Path $guiProject) {
    & dotnet publish $guiProject -c Release -o $publishTemp `
        /p:SelfContained=false `
        /p:PublishSingleFile=true `
        /p:IncludeNativeLibrariesForSelfExtract=true `
        /p:DebugType=None `
        /p:DebugSymbols=false | Out-Host

    Copy-Item -Recurse -Force (Join-Path $publishTemp "*") $OutputPath
    Write-Host "Copied published GUI output folder: $publishTemp"
} else {
    Write-Warning "GUI project not found: $guiProject"
}

if (Test-Path $publishTemp) {
    Remove-Item -Recurse -Force $publishTemp
}

# Create README with usage instructions
$readmeContent = @"
# PhantomLink Deployment

## What This Folder Contains
- PhantomLink.exe + minimal required runtime files (published output)
- Mods\
  - net6\PhantomLink.MelonIntegration.dll
  - net35\PhantomLink.MelonIntegration.dll

This deployment intentionally does NOT bundle the game's Steam/Unity/System assemblies.

## Quick Start (Recommended)
1. Install MelonLoader into the game first (run the installer for your game).
2. Run the game once (without this mod) so MelonLoader generates folders.
3. Copy this entire Deployment folder into your game's directory (same folder as the game .exe).
4. Copy the correct mod DLL into the game's Mods folder (see “Installing The Mod” below).
5. Run PhantomLink.exe.
6. Start the game. The mod and the GUI will connect automatically.

## Folder Layout Example
GameFolder\
  Game.exe
  Mods\
    (your mods go here)
  MelonLoader\
  Deployment\
    PhantomLink.exe
    README.txt
    Mods\
      net6\
      net35\

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
   - If your MelonLoader uses .NET Framework (older): use Deployment\Mods\net35\PhantomLink.MelonIntegration.dll
3. Copy the chosen DLL to:
   GameFolder\Mods\PhantomLink.MelonIntegration.dll

If you’re unsure which one to use:
- Try net6 first on modern MelonLoader installs.
- If the game logs show missing System.* Version=6.0.0.0 errors, switch to net35.

## Running The Tool
1. Start the GUI:
   - Deployment\PhantomLink.exe
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
"@

Set-Content -Path (Join-Path $OutputPath "README.txt") -Value $readmeContent

# Create Mods folder and copy the MelonLoader mod DLLs (net6 + net35)
$modsPath = Join-Path $OutputPath "Mods"
New-Item -ItemType Directory -Path $modsPath -Force | Out-Null

$modNet6Path = "PhantomLink.MelonIntegration\bin\Release\net6.0\PhantomLink.MelonIntegration.dll"
$modNet35Path = "PhantomLink.MelonIntegration\bin\Release\net35\PhantomLink.MelonIntegration.dll"

$modsNet6Path = Join-Path $modsPath "net6"
$modsNet35Path = Join-Path $modsPath "net35"
New-Item -ItemType Directory -Path $modsNet6Path -Force | Out-Null
New-Item -ItemType Directory -Path $modsNet35Path -Force | Out-Null

if (Test-Path $modNet6Path) {
    Copy-Item -Force $modNet6Path $modsNet6Path
    Write-Host "Copied MelonLoader mod (net6) to: $modsNet6Path"
} else {
    Write-Warning "MelonLoader mod DLL (net6) not found: $modNet6Path"
}

if (Test-Path $modNet35Path) {
    Copy-Item -Force $modNet35Path $modsNet35Path
    Write-Host "Copied MelonLoader mod (net35) to: $modsNet35Path"
} else {
    Write-Warning "MelonLoader mod DLL (net35) not found: $modNet35Path"
}

# Create a README for the mod deployment
$modReadmeContent = @"
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
"@

Set-Content -Path (Join-Path $modsPath "README_Mod.txt") -Value $modReadmeContent
Write-Host "Created mod deployment instructions"

if (-not [string]::IsNullOrWhiteSpace($GamePath)) {
    try {
        $resolvedGamePath = Resolve-Path $GamePath
        $gameDeployPath = Join-Path $resolvedGamePath "Deployment"
        New-Item -ItemType Directory -Path $gameDeployPath -Force | Out-Null

        Copy-Item -Recurse -Force (Join-Path $OutputPath "*") $gameDeployPath
        Write-Host "Copied Deployment to game: $gameDeployPath"
    } catch {
        Write-Warning "Failed to deploy directly to GamePath '$GamePath': $($_.Exception.Message)"
    }
}

Write-Host "Deployment completed successfully!"
Write-Host "Deployment folder: $(Resolve-Path $OutputPath)"
Write-Host "Files copied: $((Get-ChildItem $OutputPath -Recurse | Measure-Object).Count)"
Write-Host ""
Write-Host "Next steps:"
Write-Host "1. Copy the Deployment folder to your game directory"
Write-Host "2. Choose the correct mod from Deployment\\Mods\\net6 or Deployment\\Mods\\net35 and copy it to your game's Mods folder"
Write-Host "3. Run PhantomLink.exe from the game directory"
Write-Host "4. The tool will use the game's MelonLoader installation and connect to the mod"
