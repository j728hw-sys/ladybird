Minecraft On OLD GPU
====================

Portable launcher for Minecraft Java 26.3 on old GPUs / systems where modern OpenGL
does not work through the vendor driver.

What it does
------------
1. Uses Mesa llvmpipe: OpenGL is rendered on the CPU.
2. Forces Minecraft to a very small real render resolution (default 320x180).
3. Keeps Minecraft windowed internally.
4. Starts Magpie and scales the finished low-resolution window to fullscreen.
5. Lets you choose source resolution, output/display resolution and llvmpipe thread count.
6. Works with Legacy Launcher:
   %APPDATA%\.tlauncher\legacy\Minecraft\TL.exe

Files
-----
MinecraftOnOldGPU.exe
  Main graphical interface.

MinecraftOnOldGPU.CLI.exe
  Command-line version.

runtime\mesa\
  Mesa llvmpipe runtime.

runtime\magpie\
  Magpie fullscreen scaler.

Recommended for i3-2100 / Intel HD 2000
----------------------------------------
Render: 320x180
Output: current monitor resolution
llvmpipe threads: 4
Fullscreen upscale: ON
Restore display mode after exit: ON

CLI examples
------------
MinecraftOnOldGPU.CLI.exe launch -s 320x180
MinecraftOnOldGPU.CLI.exe launch -s 320x180 -t 1280x720 --threads 4
MinecraftOnOldGPU.CLI.exe restore

Important
---------
Do NOT enable Minecraft's own fullscreen mode when using fullscreen upscale.
Minecraft must remain windowed at the low source resolution. Magpie performs the
fullscreen enlargement after the frame has already been rendered.

The launcher backs up Java OpenGL files before replacing them. Use the
"Restore OpenGL" button or:
  MinecraftOnOldGPU.CLI.exe restore
to restore the backed-up files.


System requirement
------------------
.NET 8 Desktop Runtime x64 must already be installed in Windows.
The GUI and CLI do NOT embed or bundle the .NET runtime.
