#include <EGL/egl.h>
#include <EGL/eglext.h>

#include "sphoto/sphoto.h"

struct sp_egl_context {
  EGLDisplay display = EGL_NO_DISPLAY;
  EGLSurface surface = EGL_NO_SURFACE;
  EGLContext context = EGL_NO_CONTEXT;
};

extern "C" {

sp_egl_context* sp_egl_context_create(void) {
  auto* ctx = new sp_egl_context();
  ctx->display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
  if (ctx->display == EGL_NO_DISPLAY ||
      !eglInitialize(ctx->display, nullptr, nullptr) ||
      !eglBindAPI(EGL_OPENGL_ES_API)) {
    sp_egl_context_destroy(ctx);
    return nullptr;
  }

  const EGLint config_attribs[] = {
      EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
      EGL_SURFACE_TYPE, EGL_PBUFFER_BIT,
      EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
      EGL_NONE};
  EGLConfig config = nullptr;
  EGLint count = 0;
  if (!eglChooseConfig(ctx->display, config_attribs, &config, 1, &count) ||
      count == 0) {
    sp_egl_context_destroy(ctx);
    return nullptr;
  }

  // All rendering goes to framebuffer objects; the pbuffer only exists
  // because some drivers refuse to make a context current without a surface.
  const EGLint pbuffer_attribs[] = {EGL_WIDTH, 16, EGL_HEIGHT, 16, EGL_NONE};
  ctx->surface = eglCreatePbufferSurface(ctx->display, config, pbuffer_attribs);

  const EGLint context_attribs[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
  ctx->context =
      eglCreateContext(ctx->display, config, EGL_NO_CONTEXT, context_attribs);
  if (ctx->context == EGL_NO_CONTEXT || sp_egl_context_make_current(ctx) != SP_OK) {
    sp_egl_context_destroy(ctx);
    return nullptr;
  }
  return ctx;
}

int sp_egl_context_make_current(sp_egl_context* ctx) {
  if (!ctx) return SP_ERR_ARGUMENT;
  return eglMakeCurrent(ctx->display, ctx->surface, ctx->surface, ctx->context)
             ? SP_OK
             : SP_ERR_GPU;
}

void sp_egl_context_destroy(sp_egl_context* ctx) {
  if (!ctx) return;
  if (ctx->display != EGL_NO_DISPLAY) {
    eglMakeCurrent(ctx->display, EGL_NO_SURFACE, EGL_NO_SURFACE,
                   EGL_NO_CONTEXT);
    if (ctx->context != EGL_NO_CONTEXT) {
      eglDestroyContext(ctx->display, ctx->context);
    }
    if (ctx->surface != EGL_NO_SURFACE) {
      eglDestroySurface(ctx->display, ctx->surface);
    }
    eglTerminate(ctx->display);
  }
  delete ctx;
}

}  // extern "C"
