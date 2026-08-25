#!/usr/bin/env python3
from pathlib import Path


def replace_once(path: str, old: str, new: str, label: str) -> None:
    p = Path(path)
    text = p.read_text()
    count = text.count(old)
    if count != 1:
        raise SystemExit(f"{label}: expected exactly one match in {path}, found {count}")
    p.write_text(text.replace(old, new, 1))


# Firefox 145 (Bug 1991812) removed four linked Intel HD 3000 safeguards.
# Port them to Firefox 154 rather than blindly reverse-applying the old patch,
# because current Gecko renamed OperatingSystem::OSX to OperatingSystem::MacOS.

replace_once(
    "gfx/gl/GLContext.h",
    '''  GalliumLlvmpipe,
  MicrosoftBasicRenderDriver,
''',
    '''  GalliumLlvmpipe,
  IntelHD3000,
  MicrosoftBasicRenderDriver,
''',
    "restore GLRenderer::IntelHD3000",
)

replace_once(
    "gfx/gl/GLContext.cpp",
    '''      "Gallium 0.4 on llvmpipe",
      "Microsoft Basic Render Driver",
''',
    '''      "Gallium 0.4 on llvmpipe",
      "Intel HD Graphics 3000 OpenGL Engine",
      "Microsoft Basic Render Driver",
''',
    "restore HD3000 renderer detection",
)

replace_once(
    "gfx/gl/GLContext.cpp",
    '''#ifdef XP_MACOSX
    // OSX supports EXT_texture_sRGB in Legacy contexts, but not in Core
''',
    '''#ifdef XP_MACOSX
    // Bug 1009642 / 1124996: Apple's Intel HD 3000 driver mishandles
    // updates to S3TC compressed textures. Firefox carried this guard
    // through 144; restore it for legacy/OCLP systems.
    if (Renderer() == GLRenderer::IntelHD3000) {
      MarkExtensionUnsupported(EXT_texture_compression_s3tc);
    }

    // OSX supports EXT_texture_sRGB in Legacy contexts, but not in Core
''',
    "restore HD3000 S3TC guard",
)

hd3000_fbo_workaround = r'''
// Firefox carried this workaround through 144 (bugs 1586627 / FB7379358).
// After GL read/copy operations, Apple's Intel HD 3000 OpenGL driver can
// poison the most-recently-read framebuffer. Move that state to a disposable
// 1x1 framebuffer before continuing.
static void WorkAroundAppleIntelHD3000GraphicsGLDriverBug(GLContext* aGL) {
#ifdef XP_MACOSX
  if (aGL->WorkAroundDriverBugs() &&
      aGL->Renderer() == GLRenderer::IntelHD3000) {
    ScopedTexture texForReading(aGL);
    {
      ScopedBindTexture autoBindTexForReading(aGL, texForReading);
      aGL->fTexImage2D(LOCAL_GL_TEXTURE_2D, 0, LOCAL_GL_RGBA, 1, 1, 0,
                       LOCAL_GL_RGBA, LOCAL_GL_UNSIGNED_BYTE, nullptr);
      aGL->fTexParameteri(LOCAL_GL_TEXTURE_2D, LOCAL_GL_TEXTURE_MIN_FILTER,
                          LOCAL_GL_LINEAR);
      aGL->fTexParameteri(LOCAL_GL_TEXTURE_2D, LOCAL_GL_TEXTURE_MAG_FILTER,
                          LOCAL_GL_LINEAR);
    }

    ScopedFramebufferForTexture autoFBForReading(aGL, texForReading);
    if (autoFBForReading.IsComplete()) {
      ScopedBindFramebuffer autoFB(aGL, autoFBForReading.FB());
      ScopedTexture texReadingDest(aGL);
      ScopedBindTexture autoBindTexReadingDest(aGL, texReadingDest);
      aGL->fCopyTexImage2D(LOCAL_GL_TEXTURE_2D, 0, LOCAL_GL_RGBA, 0, 0, 1, 1,
                           0);
    }
  }
#endif
}
'''

replace_once(
    "gfx/layers/opengl/CompositorOGL.cpp",
    '''  mGLContext->fGenFramebuffers(1, aFBO);
}

GLuint CompositorOGL::CreateTexture''',
    '''  mGLContext->fGenFramebuffers(1, aFBO);
}
''' + hd3000_fbo_workaround + '''
GLuint CompositorOGL::CreateTexture''',
    "restore HD3000 framebuffer workaround",
)

replace_once(
    "gfx/layers/opengl/CompositorOGL.cpp",
    '''      mGLContext->fCopyTexImage2D(mFBOTextureTarget, 0, LOCAL_GL_RGBA,
                                  clampedRect.X(), FlipY(clampedRect.YMost()),
                                  clampedRectWidth, clampedRectHeight, 0);
''',
    '''      mGLContext->fCopyTexImage2D(mFBOTextureTarget, 0, LOCAL_GL_RGBA,
                                  clampedRect.X(), FlipY(clampedRect.YMost()),
                                  clampedRectWidth, clampedRectHeight, 0);
      WorkAroundAppleIntelHD3000GraphicsGLDriverBug(mGLContext);
''',
    "restore workaround after fCopyTexImage2D",
)

replace_once(
    "gfx/layers/opengl/CompositorOGL.cpp",
    '''      mGLContext->fReadPixels(clampedRect.X(), clampedRect.Y(),
                              clampedRectWidth, clampedRectHeight,
                              LOCAL_GL_RGBA, LOCAL_GL_UNSIGNED_BYTE, buf.get());
      mGLContext->fTexImage2D''',
    '''      mGLContext->fReadPixels(clampedRect.X(), clampedRect.Y(),
                              clampedRectWidth, clampedRectHeight,
                              LOCAL_GL_RGBA, LOCAL_GL_UNSIGNED_BYTE, buf.get());
      WorkAroundAppleIntelHD3000GraphicsGLDriverBug(mGLContext);
      mGLContext->fTexImage2D''',
    "restore workaround after fReadPixels",
)

replace_once(
    "widget/cocoa/GfxInfo.mm",
    '''    IMPLEMENT_MAC_DRIVER_BLOCKLIST(
        OperatingSystem::MacOS, DeviceFamily::IntelWebRenderBlocked,
        nsIGfxInfo::FEATURE_WEBRENDER, nsIGfxInfo::FEATURE_BLOCKED_DEVICE,
        "FEATURE_FAILURE_INTEL_GEN5_OR_OLDER");
  }
''',
    '''    IMPLEMENT_MAC_DRIVER_BLOCKLIST(
        OperatingSystem::MacOS, DeviceFamily::IntelWebRenderBlocked,
        nsIGfxInfo::FEATURE_WEBRENDER, nsIGfxInfo::FEATURE_BLOCKED_DEVICE,
        "FEATURE_FAILURE_INTEL_GEN5_OR_OLDER");

    // Intel HD 3000 / Sandy Bridge: preserve Firefox <=144 behavior.
    // Do not enable WebRender on this legacy Apple OpenGL stack.
    IMPLEMENT_MAC_DRIVER_BLOCKLIST(
        OperatingSystem::MacOS, DeviceFamily::IntelSandyBridge,
        nsIGfxInfo::FEATURE_WEBRENDER, nsIGfxInfo::FEATURE_BLOCKED_DEVICE,
        "FEATURE_FAILURE_INTEL_MAC_HD3000_NO_WEBRENDER");
  }
''',
    "restore Sandy Bridge WebRender blocklist",
)

print("Intel HD 3000 compatibility safeguards restored for Firefox 154.0.")
