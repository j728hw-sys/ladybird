// Regression test for MobileGL DirectGLES WGL surface migration.
// One HGLRC owns a GPU-written texture/FBO on window A, then the SAME HGLRC
// moves to window B. The texture contents must survive the surface switch.

#include <windows.h>
#include <cstdio>
#include <cstdlib>
#include <cmath>

typedef unsigned int GLenum;
typedef unsigned int GLuint;
typedef int GLint;
typedef int GLsizei;
typedef float GLfloat;
typedef unsigned int GLbitfield;
typedef unsigned char GLubyte;

#define GL_TEXTURE_2D 0x0DE1
#define GL_RGBA8 0x8058
#define GL_RGBA 0x1908
#define GL_UNSIGNED_BYTE 0x1401
#define GL_FRAMEBUFFER 0x8D40
#define GL_COLOR_ATTACHMENT0 0x8CE0
#define GL_FRAMEBUFFER_COMPLETE 0x8CD5
#define GL_COLOR_BUFFER_BIT 0x00004000
#define GL_NO_ERROR 0

static HMODULE ogl = nullptr;
typedef HGLRC (WINAPI *PFN_wglCreateContext)(HDC);
typedef BOOL  (WINAPI *PFN_wglDeleteContext)(HGLRC);
typedef BOOL  (WINAPI *PFN_wglMakeCurrent)(HDC,HGLRC);
typedef PROC  (WINAPI *PFN_wglGetProcAddress)(LPCSTR);
static PFN_wglCreateContext wglCreateContext_ = nullptr;
static PFN_wglDeleteContext wglDeleteContext_ = nullptr;
static PFN_wglMakeCurrent wglMakeCurrent_ = nullptr;
static PFN_wglGetProcAddress wglGetProcAddress_ = nullptr;

static void* GLProc(const char* n) {
    PROC p = wglGetProcAddress_ ? wglGetProcAddress_(n) : nullptr;
    if (!p) p = GetProcAddress(ogl, n);
    return reinterpret_cast<void*>(p);
}

typedef void (WINAPI *PFN_glGenTextures)(GLsizei,GLuint*);
typedef void (WINAPI *PFN_glBindTexture)(GLenum,GLuint);
typedef void (WINAPI *PFN_glTexImage2D)(GLenum,GLint,GLint,GLsizei,GLsizei,GLint,GLenum,GLenum,const void*);
typedef void (WINAPI *PFN_glGenFramebuffers)(GLsizei,GLuint*);
typedef void (WINAPI *PFN_glBindFramebuffer)(GLenum,GLuint);
typedef void (WINAPI *PFN_glFramebufferTexture2D)(GLenum,GLenum,GLenum,GLuint,GLint);
typedef GLenum (WINAPI *PFN_glCheckFramebufferStatus)(GLenum);
typedef void (WINAPI *PFN_glClearColor)(GLfloat,GLfloat,GLfloat,GLfloat);
typedef void (WINAPI *PFN_glClear)(GLbitfield);
typedef void (WINAPI *PFN_glReadPixels)(GLint,GLint,GLsizei,GLsizei,GLenum,GLenum,void*);
typedef void (WINAPI *PFN_glFinish)(void);
typedef GLenum (WINAPI *PFN_glGetError)(void);

static PFN_glGenTextures glGenTextures_;
static PFN_glBindTexture glBindTexture_;
static PFN_glTexImage2D glTexImage2D_;
static PFN_glGenFramebuffers glGenFramebuffers_;
static PFN_glBindFramebuffer glBindFramebuffer_;
static PFN_glFramebufferTexture2D glFramebufferTexture2D_;
static PFN_glCheckFramebufferStatus glCheckFramebufferStatus_;
static PFN_glClearColor glClearColor_;
static PFN_glClear glClear_;
static PFN_glReadPixels glReadPixels_;
static PFN_glFinish glFinish_;
static PFN_glGetError glGetError_;

#define FAIL(...) do { std::fprintf(stderr, "MIGRATION FAIL: " __VA_ARGS__); std::fprintf(stderr,"\n"); return 1; } while(0)

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    return DefWindowProcA(h,m,w,l);
}

static HWND MakeWindow(const char* title) {
    static bool reg = false;
    if (!reg) {
        WNDCLASSA wc{};
        wc.style = CS_OWNDC;
        wc.lpfnWndProc = WndProc;
        wc.hInstance = GetModuleHandleA(nullptr);
        wc.lpszClassName = "MGSurfaceMigration";
        if (!RegisterClassA(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) return nullptr;
        reg = true;
    }
    return CreateWindowExA(0,"MGSurfaceMigration",title,WS_OVERLAPPEDWINDOW,
                           CW_USEDEFAULT,CW_USEDEFAULT,320,240,nullptr,nullptr,
                           GetModuleHandleA(nullptr),nullptr);
}

static bool SetupPixelFormat(HDC dc) {
    PIXELFORMATDESCRIPTOR pfd{};
    pfd.nSize = sizeof(pfd);
    pfd.nVersion = 1;
    pfd.dwFlags = PFD_DRAW_TO_WINDOW | PFD_SUPPORT_OPENGL | PFD_DOUBLEBUFFER;
    pfd.iPixelType = PFD_TYPE_RGBA;
    pfd.cColorBits = 32;
    pfd.cDepthBits = 24;
    pfd.cStencilBits = 8;
    int pf = ChoosePixelFormat(dc,&pfd);
    if (pf <= 0) return false;
    PIXELFORMATDESCRIPTOR actual{};
    if (!DescribePixelFormat(dc,pf,sizeof(actual),&actual)) return false;
    return SetPixelFormat(dc,pf,&actual) == TRUE;
}

static bool ColorNear(const GLubyte p[4], int r, int g, int b) {
    return std::abs((int)p[0]-r) <= 6 &&
           std::abs((int)p[1]-g) <= 6 &&
           std::abs((int)p[2]-b) <= 6 &&
           p[3] >= 245;
}

int main() {
    ogl = LoadLibraryA("opengl32.dll");
    if (!ogl) FAIL("LoadLibrary opengl32.dll failed");
    wglCreateContext_ = (PFN_wglCreateContext)GetProcAddress(ogl,"wglCreateContext");
    wglDeleteContext_ = (PFN_wglDeleteContext)GetProcAddress(ogl,"wglDeleteContext");
    wglMakeCurrent_ = (PFN_wglMakeCurrent)GetProcAddress(ogl,"wglMakeCurrent");
    wglGetProcAddress_ = (PFN_wglGetProcAddress)GetProcAddress(ogl,"wglGetProcAddress");
    if (!wglCreateContext_ || !wglDeleteContext_ || !wglMakeCurrent_ || !wglGetProcAddress_)
        FAIL("missing WGL exports");

    HWND a = MakeWindow("surface-A");
    HWND b = MakeWindow("surface-B");
    if (!a || !b) FAIL("window creation failed");
    HDC dca = GetDC(a);
    HDC dcb = GetDC(b);
    if (!SetupPixelFormat(dca) || !SetupPixelFormat(dcb)) FAIL("SetPixelFormat failed");

    HGLRC ctx = wglCreateContext_(dca);
    if (!ctx) FAIL("wglCreateContext failed");
    if (!wglMakeCurrent_(dca,ctx)) FAIL("wglMakeCurrent(A) failed");

#define LOAD(name) name##_ = (PFN_##name)GLProc(#name); if (!name##_) FAIL("missing " #name)
    LOAD(glGenTextures);
    LOAD(glBindTexture);
    LOAD(glTexImage2D);
    LOAD(glGenFramebuffers);
    LOAD(glBindFramebuffer);
    LOAD(glFramebufferTexture2D);
    LOAD(glCheckFramebufferStatus);
    LOAD(glClearColor);
    LOAD(glClear);
    LOAD(glReadPixels);
    LOAD(glFinish);
    LOAD(glGetError);
#undef LOAD

    GLuint tex = 0, fbo = 0;
    glGenTextures_(1,&tex);
    glBindTexture_(GL_TEXTURE_2D,tex);
    glTexImage2D_(GL_TEXTURE_2D,0,GL_RGBA8,8,8,0,GL_RGBA,GL_UNSIGNED_BYTE,nullptr);

    glGenFramebuffers_(1,&fbo);
    glBindFramebuffer_(GL_FRAMEBUFFER,fbo);
    glFramebufferTexture2D_(GL_FRAMEBUFFER,GL_COLOR_ATTACHMENT0,GL_TEXTURE_2D,tex,0);
    if (glCheckFramebufferStatus_(GL_FRAMEBUFFER) != GL_FRAMEBUFFER_COMPLETE)
        FAIL("FBO incomplete on window A");

    // Unique GPU-only content. The frontend CPU texture shadow starts undefined;
    // preserving this value therefore proves the native ES context survived.
    glClearColor_(0.2f,0.7f,0.3f,1.0f);
    glClear_(GL_COLOR_BUFFER_BIT);
    glFinish_();

    GLubyte before[4]{};
    glReadPixels_(3,3,1,1,GL_RGBA,GL_UNSIGNED_BYTE,before);
    std::printf("before migration: %u,%u,%u,%u tex=%u fbo=%u\n",
                before[0],before[1],before[2],before[3],tex,fbo);
    if (!ColorNear(before,51,179,77)) FAIL("unexpected pre-migration pixel");

    // This is the Minecraft-relevant operation: same logical WGL context, new HDC/HWND.
    if (!wglMakeCurrent_(dcb,ctx)) FAIL("wglMakeCurrent(B, same HGLRC) failed");

    glBindFramebuffer_(GL_FRAMEBUFFER,fbo);
    GLenum status = glCheckFramebufferStatus_(GL_FRAMEBUFFER);
    if (status != GL_FRAMEBUFFER_COMPLETE)
        FAIL("FBO lost after surface migration: status=0x%x",status);

    glFinish_();
    GLubyte after[4]{};
    glReadPixels_(3,3,1,1,GL_RGBA,GL_UNSIGNED_BYTE,after);
    GLenum err = glGetError_();
    std::printf("after migration:  %u,%u,%u,%u glGetError=0x%x\n",
                after[0],after[1],after[2],after[3],err);
    if (err != GL_NO_ERROR) FAIL("GL error after migration: 0x%x",err);
    if (!ColorNear(after,51,179,77))
        FAIL("GPU texture contents were lost across same-HGLRC window switch");

    // Also prove the context can present on the new window.
    glBindFramebuffer_(GL_FRAMEBUFFER,0);
    glClearColor_(0.1f,0.2f,0.4f,1.0f);
    glClear_(GL_COLOR_BUFFER_BIT);
    if (!SwapBuffers(dcb)) FAIL("SwapBuffers on window B failed");

    wglMakeCurrent_(nullptr,nullptr);
    wglDeleteContext_(ctx);
    ReleaseDC(a,dca); ReleaseDC(b,dcb);
    DestroyWindow(a); DestroyWindow(b);
    std::printf("MIGRATION PASS\n");
    return 0;
}
