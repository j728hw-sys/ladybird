Minecraft On OLD GPU
====================

Portable launcher for Minecraft Java 26.3 on old GPUs / systems where modern OpenGL
does not work through the vendor driver.

Renderer modes
--------------
CPU mode (default, stable):
  Mesa llvmpipe renders OpenGL on the CPU exactly as before.

Native Intel HD 2000 (experimental checkbox):
  Restores the original Java OpenGL files and loads HD2000NativeAgent.jar.
  The agent patches Minecraft 26.3 RenderPearl at class-load time:
    - requests OpenGL 3.1 instead of 3.3 Core;
    - bypasses only RenderPearl's 3.3 version gate while keeping real capabilities;
    - recompiles SPIR-V to GLSL 1.40 instead of GLSL 3.30;
    - uses GL_ARB_instanced_arrays for glVertexAttribDivisor on Sandy Bridge.
  Mesa llvmpipe environment variables are NOT enabled in this mode.
  Rendering is intended to go through the original Intel HD Graphics 2000 driver.

The native option is intentionally separate. Turning it off returns to the existing
CPU/llvmpipe path; the CPU implementation has not been removed.

Common behavior
---------------
1. Forces Minecraft to a very small real render resolution (default 320x180).
2. Keeps Minecraft windowed internally.
3. Starts IntegerScaler 2.20 when fullscreen upscale is enabled and scales the
   existing low-resolution window without changing the Windows display mode.
4. Lets you choose source resolution and scaled-image target size.
5. CPU mode additionally lets you choose llvmpipe thread count.
6. Works with Legacy Launcher:
   %APPDATA%\.tlauncher\legacy\Minecraft\TL.exe

Files
-----
MinecraftOnOldGPU.exe
  Main graphical interface.

MinecraftOnOldGPU.CLI.exe
  Command-line version.

runtime\mesa\
  Mesa llvmpipe runtime used only by CPU mode.

runtime\native-intel\HD2000NativeAgent.jar
  Experimental Minecraft 26.3 RenderPearl compatibility agent for native
  Intel HD Graphics 2000 OpenGL 3.1 / GLSL 1.40.

runtime\integer-scaler\
  IntegerScaler 2.20 lightweight fullscreen scaler.

Recommended baseline
--------------------
Render: 320x180 or 640x360
Output: current monitor resolution
Fullscreen upscale: ON
Windows display mode: NEVER CHANGED
Native Intel checkbox: OFF for the known-working CPU mode; ON only to test HD 2000.

CLI examples
------------
MinecraftOnOldGPU.CLI.exe launch -s 320x180
MinecraftOnOldGPU.CLI.exe launch -s 320x180 -t 1280x720 --threads 4
MinecraftOnOldGPU.CLI.exe launch -s 640x360 --native-intel
MinecraftOnOldGPU.CLI.exe restore

Important
---------
Do NOT enable Minecraft's own fullscreen mode when using fullscreen upscale.
Minecraft must remain windowed at the low source resolution. IntegerScaler keeps
that real low-resolution window and presents the already-rendered image fullscreen.
The launcher never calls ChangeDisplaySettings for fullscreen upscale, so Windows
stays at the desktop mode and Minecraft is not switched to desktop-size rendering.

CPU mode backs up Java OpenGL files before replacing them with Mesa. Native Intel
mode restores those originals before launch, so it cannot accidentally keep using
the bundled llvmpipe opengl32.dll.

The native mode targets Minecraft Java 26.3 specifically. It is experimental: old
Intel Windows drivers can still expose driver bugs or unsupported shader behavior.
If it fails, uncheck the native option and the existing CPU mode remains available.

Use the "Restore OpenGL" button or:
  MinecraftOnOldGPU.CLI.exe restore
to restore backed-up Java OpenGL files manually.

System requirement
------------------
.NET 8 Desktop Runtime x64 must already be installed in Windows.
The GUI and CLI do NOT embed or bundle the .NET runtime.

Fullscreen controls
-------------------
Fullscreen upscale OFF:
  No scaler is left running. Minecraft stays as the normal low-resolution window.

Fullscreen upscale ON + Stretch image ON:
  IntegerScaler enlarges the low-resolution window to use the screen. For exact
  ratios such as 640x360 -> 1920x1080 this is exactly 3x.

Fullscreen upscale ON + Stretch image OFF:
  Fullscreen presentation remains enabled, but the image stays 1:1 centered.

The launcher also closes IntegerScaler/Magpie processes left by older builds before
every launch, so disabling fullscreen scaling is a real off state.
