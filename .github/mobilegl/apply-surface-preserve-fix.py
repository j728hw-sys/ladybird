from pathlib import Path

root = Path("MobileGL")

# 1) Keep the single DirectGLES backend context alive when the frontend switches
#    between WGL/EGL window surfaces. Only the native EGLSurface is replaced.
direct = root / "MobileGL/MG_Backend/DirectGLES/DirectGLES.cpp"
s = direct.read_text(encoding="utf-8")

old = r'''    Bool InitWindowSurface(NativeWindowType window) {
        if (!window) return false;

        if (!InitDisplayAndContext(EGL_WINDOW_BIT, window)) return false;

        g_Surface = g_EGLFuncs.eglCreateWindowSurface(g_Display, g_Config, window, nullptr);
        if (g_Surface == EGL_NO_SURFACE) return false;

        if (!MakeCurrent()) return false;

        ApplyRequestedSwapInterval();
        PublishDefaultFramebufferDepthStencilFormat();

        MGLOG_D("EGL context created successfully: display=%p, surface=%p, context=%p. window=%p", g_Display, g_Surface,
                g_Context, window);
        return true;
    }
'''

new = r'''    Bool InitWindowSurface(NativeWindowType window) {
        if (!window) return false;

        // WGL can move one live HGLRC between HWND/HDC pairs. The frontend GL
        // object namespace and all GPU-written contents must survive that move.
        // DirectGLES has one native backend context, so replacing a window surface
        // must NOT tear that context down: doing so destroys textures/FBOs/programs
        // while the frontend still considers them alive.
        if (g_Display != EGL_NO_DISPLAY && g_Context != EGL_NO_CONTEXT && g_Config != nullptr) {
            EGLSurface replacement =
                g_EGLFuncs.eglCreateWindowSurface(g_Display, g_Config, window, nullptr);
            if (replacement == EGL_NO_SURFACE) {
                MGLOG_E_ONCE("DirectGLES: failed to create replacement window surface while preserving context");
                return false;
            }

            const EGLSurface previous = g_Surface;
            g_Surface = replacement;
            if (!MakeCurrent()) {
                g_Surface = previous;
                if (g_EGLFuncs.eglDestroySurface) {
                    g_EGLFuncs.eglDestroySurface(g_Display, replacement);
                }
                MGLOG_E_ONCE("DirectGLES: failed to make replacement window surface current");
                return false;
            }

            ApplyRequestedSwapInterval();
            PublishDefaultFramebufferDepthStencilFormat();

            // The old native surface is no longer current after MakeCurrent above.
            // Destroy only the surface; the ES context and all of its driver objects
            // intentionally remain alive.
            if (previous != EGL_NO_SURFACE && previous != replacement && g_EGLFuncs.eglDestroySurface) {
                g_EGLFuncs.eglDestroySurface(g_Display, previous);
            }

            MGLOG_I("DirectGLES: preserved backend context while switching window surface: "
                    "display=%p surface=%p context=%p window=%p",
                    g_Display, g_Surface, g_Context, window);
            return true;
        }

        if (!InitDisplayAndContext(EGL_WINDOW_BIT, window)) return false;

        g_Surface = g_EGLFuncs.eglCreateWindowSurface(g_Display, g_Config, window, nullptr);
        if (g_Surface == EGL_NO_SURFACE) return false;

        if (!MakeCurrent()) return false;

        ApplyRequestedSwapInterval();
        PublishDefaultFramebufferDepthStencilFormat();

        MGLOG_D("EGL context created successfully: display=%p, surface=%p, context=%p. window=%p", g_Display, g_Surface,
                g_Context, window);
        return true;
    }
'''
if old not in s:
    raise SystemExit("InitWindowSurface source block not found; refusing partial patch")
s = s.replace(old, new, 1)
direct.write_text(s, encoding="utf-8")

# 2) The backend surface registry may activate another frontend EGLSurface. Do
#    not destroy/reset DirectGLES before the virtual InitWindowSurface call; the
#    code above performs a non-destructive native-surface switch.
bo = root / "MobileGL/MG_Backend/DirectGLES/BackendObject_DirectGLES.cpp"
s = bo.read_text(encoding="utf-8")
old = r'''        if (m_eglSurfaceInitialized) {
            DestroyEGLContext();
            ResetEGLRuntimeState();
        }

        return BackendObject::CreateEGLWindowSurface(surface, handle);
'''
new = r'''        // Switching WGL/EGL window surfaces must preserve the one native
        // DirectGLES context. BackendObject::ActivateEGLSurface will call
        // InitWindowSurface(), which replaces only the native EGLSurface.
        return BackendObject::CreateEGLWindowSurface(surface, handle);
'''
if old not in s:
    raise SystemExit("CreateEGLWindowSurface destructive block not found; refusing partial patch")
s = s.replace(old, new, 1)
bo.write_text(s, encoding="utf-8")

# Hard fail unless both sides are definitely in the patched tree.
d = direct.read_text(encoding="utf-8")
b = bo.read_text(encoding="utf-8")
marker = "DirectGLES: preserved backend context while switching window surface"
if marker not in d:
    raise SystemExit("surface-preserve marker missing from DirectGLES.cpp")
if "DestroyEGLContext();\n            ResetEGLRuntimeState();" in b[b.find("BackendObject_DirectGLES::CreateEGLWindowSurface"):b.find("BackendObject_DirectGLES::CreateEGLPbufferSurface")]:
    raise SystemExit("destructive window-surface path is still present")

print("SURFACE PRESERVE PATCH APPLIED")
