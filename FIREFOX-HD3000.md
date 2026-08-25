# Firefox 154.0 + Intel HD Graphics 3000 compatibility restoration

This branch is intentionally isolated from the Ladybird work on `master`.

## Source pinned for reproducibility

- Firefox stable: **154.0**
- Mozilla tag: `FIREFOX_154_0_RELEASE`
- Source commit: `032a9fc1ac0cc3209f7c142744ba2e40847c8086`
- Regression/removal commit: `a6944a13821727f23cdfecc98271d7f723bb6994`
  (`Bug 1991812 - Remove old macOS HD3000 workarounds`)

Firefox 145 removed 80 lines of Intel HD 3000-specific handling from four files.
That was reasonable for officially supported Macs because Apple stopped officially
supporting HD 3000 before Mozilla's current macOS floor, but it regressed
OCLP/legacy-patched systems where the Apple Intel HD 3000 OpenGL stack is restored.

## What is restored

`scripts/restore_hd3000.py` ports the old safeguards to Firefox 154 APIs:

1. Restores recognition of `Intel HD Graphics 3000 OpenGL Engine`.
2. Restores `GLRenderer::IntelHD3000`.
3. Restores the S3TC guard for the buggy Apple HD 3000 OpenGL implementation.
4. Restores the framebuffer read/copy workaround used after `fCopyTexImage2D`
   and `fReadPixels`.
5. Restores the Sandy Bridge WebRender blocklist. This is deliberate: the
   working Firefox <=144 configuration did **not** force WebRender on HD 3000;
   it kept the legacy OpenGL compositor path and applied the driver workaround.

The Firefox 154 source has renamed `OperatingSystem::OSX` to
`OperatingSystem::MacOS`, so this is a source-level port of the old behavior,
not a blind reverse-apply of the Firefox 145 patch.

## Build

The only workflow in this branch is
`.github/workflows/firefox-hd3000-build.yml`.

It builds on an Intel GitHub macOS runner, targets macOS 10.15+ (therefore
Big Sur 11), disables the built-in updater so an unpatched stock Firefox cannot
silently replace this custom build, packages a DMG, and uploads it as an
Actions artifact.

This patch does not add Metal support to Intel HD 3000. It restores Mozilla's
former defensive handling of Apple's legacy HD 3000 OpenGL driver.
