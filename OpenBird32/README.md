# OpenBird ARMv7 build

This branch builds the real [`crosslife/OpenBird`](https://github.com/crosslife/OpenBird) Cocos2d-x project as an unsigned 32-bit iOS ARMv7 IPA.

## Pinned source

- Repository: `crosslife/OpenBird`
- Commit: `9e0198a1a2295f03fa1e8676e216e22c9c7d380b`
- License: MIT
- Original iOS target: `FlappyBird iOS`
- Rendering/game stack: Cocos2d-x, OpenGL ES, C++, Objective-C++, Lua and Chipmunk physics

The workflow clones only the pinned revision. It does not download a prebuilt IPA and does not replace the game with a simplified UIKit implementation.

## Output

The Actions artifact is named `OpenBird-iPhoneOS5.0-armv7` and contains:

- `OpenBird-iPhoneOS5.0-armv7.ipa`
- `SHA256SUMS.txt`
- build and Mach-O audit reports

The IPA is unsigned and intended for the SideInstaller/HLE ARMv7 runtime test.
