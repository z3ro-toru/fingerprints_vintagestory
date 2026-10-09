# Fingerprints mod

## Building

### Requirements

- Vintage Story **1.22+**
- .NET SDK **10.0** ([download](https://dotnet.microsoft.com/download))
- (optional) Visual Studio 2022/2026, Rider, or VS Code — for development

### Option 1: CakeBuild (recommended)

Does not require Visual Studio. Works from PowerShell, bash, and any terminal.

1. Install the .NET SDK 10.
2. Set the `VINTAGE_STORY` environment variable to the path of your installed game
   (the folder containing `VintagestoryAPI.dll` and the `Mods/` folder):

   **Windows (PowerShell, current session):**
   ```powershell
   $env:VINTAGE_STORY = "C:\Users\YourUsername\AppData\Roaming\Vintagestory"
   ```
   
   **Windows (permanent):**
   ```powershell
   [Environment]::SetEnvironmentVariable("VINTAGE_STORY", "C:\Users\YourUsername\AppData\Roaming\Vintagestory", "User")
   ```
   **Linux/macOS:**
   ```bash
   export VINTAGE_STORY="$HOME/.local/share/Vintagestory"
   ```
3. Build the mod from the repository root:

   **Windows:**

   ```powershell
   .\build.ps1
   ```

   **Linux/macOS:**

   ```bash
   ./build.sh
   ```

   To skip JSON validation (useful if you edit JSON5 assets):

   ``` powershell
   .\build.ps1 -skipJsonValidation true
   ```
4. The finished archive will appear in `Releases/fingerprints_<version>.zip`.
Copy it into `%appdata%/VintagestoryData/Mods/
(Windows)` or `~/.local/share/VintagestoryData/Mods/
(Linux/macOS)` and launch the game.

### Option 2: Visual Studio / Rider / VS Code
1. Open fingerprints_vintagestory.slnx (Visual Studio 2022+ required)
2. Make sure the VINTAGE_STORY environment variable is set.
3. Build the fingerprints_vintagestory project.
4. The output will be in src/bin/<Configuration>/Mods/mod/.

## License
MIT — see LICENSE.