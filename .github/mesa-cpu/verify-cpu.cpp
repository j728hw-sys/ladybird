#include <windows.h>
#include <GL/gl.h>
#include <cstdio>
#include <cstring>
#include <cctype>

static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM w, LPARAM l) {
    return DefWindowProcA(h,m,w,l);
}

static bool contains_ci(const char* s,const char* n) {
    if(!s || !n) return false;
    size_t a=strlen(s), b=strlen(n);
    for(size_t i=0;i+b<=a;i++) {
        bool ok=true;
        for(size_t j=0;j<b;j++) {
            if(std::tolower((unsigned char)s[i+j])!=std::tolower((unsigned char)n[j])) { ok=false; break; }
        }
        if(ok) return true;
    }
    return false;
}

int main() {
    WNDCLASSA wc{};
    wc.style=CS_OWNDC;
    wc.lpfnWndProc=WndProc;
    wc.hInstance=GetModuleHandleA(nullptr);
    wc.lpszClassName="MesaCPUVerify";
    RegisterClassA(&wc);

    HWND hwnd=CreateWindowExA(0,wc.lpszClassName,"Mesa CPU verify",WS_OVERLAPPEDWINDOW,
                              0,0,640,360,nullptr,nullptr,wc.hInstance,nullptr);
    if(!hwnd) return 10;
    HDC dc=GetDC(hwnd);

    PIXELFORMATDESCRIPTOR pfd{};
    pfd.nSize=sizeof(pfd);
    pfd.nVersion=1;
    pfd.dwFlags=PFD_DRAW_TO_WINDOW|PFD_SUPPORT_OPENGL|PFD_DOUBLEBUFFER;
    pfd.iPixelType=PFD_TYPE_RGBA;
    pfd.cColorBits=24;
    pfd.cDepthBits=24;

    int pf=ChoosePixelFormat(dc,&pfd);
    if(pf<=0 || !SetPixelFormat(dc,pf,&pfd)) return 11;

    HGLRC rc=wglCreateContext(dc);
    if(!rc || !wglMakeCurrent(dc,rc)) return 12;

    const char* renderer=(const char*)glGetString(GL_RENDERER);
    const char* version=(const char*)glGetString(GL_VERSION);
    std::printf("GL_RENDERER=%s\nGL_VERSION=%s\n",renderer?renderer:"<null>",version?version:"<null>");

    bool cpu=contains_ci(renderer,"llvmpipe");

    wglMakeCurrent(nullptr,nullptr);
    wglDeleteContext(rc);
    ReleaseDC(hwnd,dc);
    DestroyWindow(hwnd);

    if(!cpu) {
        std::fprintf(stderr,"CPU RENDERER FAIL: renderer is not llvmpipe\n");
        return 20;
    }

    std::puts("CPU RENDERER PASS");
    return 0;
}
