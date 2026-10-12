// NAPI bridge between the ArkTS UI and the SPhoto core. The XComponent
// surface callbacks and the exported functions all run on the UI thread,
// which owns the EGL context.
#include <EGL/egl.h>
#include <GLES3/gl3.h>
#include <ace/xcomponent/native_interface_xcomponent.h>
#include <hilog/log.h>
#include <napi/native_api.h>

#include <chrono>
#include <string>
#include <vector>

#include "sphoto/sphoto.h"

#undef LOG_TAG
#define LOG_TAG "SPhoto"

namespace {

// A 12-megapixel frame, the size of a typical phone photo.
constexpr int kTestWidth = 4032;
constexpr int kTestHeight = 3024;

struct State {
  EGLDisplay display = EGL_NO_DISPLAY;
  EGLSurface surface = EGL_NO_SURFACE;
  EGLContext context = EGL_NO_CONTEXT;
  sp_renderer* renderer = nullptr;
  int width = 0;
  int height = 0;
  sp_params params{};
  std::string filter;  // empty for none
  float strength = 1.0f;
  sp_geometry geometry{};
};

State g_state;

void destroy_gl() {
  if (g_state.display == EGL_NO_DISPLAY) return;
  eglMakeCurrent(g_state.display, g_state.surface, g_state.surface,
                 g_state.context);
  sp_renderer_destroy(g_state.renderer);
  g_state.renderer = nullptr;
  eglMakeCurrent(g_state.display, EGL_NO_SURFACE, EGL_NO_SURFACE,
                 EGL_NO_CONTEXT);
  if (g_state.surface != EGL_NO_SURFACE) {
    eglDestroySurface(g_state.display, g_state.surface);
  }
  if (g_state.context != EGL_NO_CONTEXT) {
    eglDestroyContext(g_state.display, g_state.context);
  }
  g_state.surface = EGL_NO_SURFACE;
  g_state.context = EGL_NO_CONTEXT;
}

bool create_gl(void* window) {
  g_state.display = eglGetDisplay(EGL_DEFAULT_DISPLAY);
  if (g_state.display == EGL_NO_DISPLAY ||
      !eglInitialize(g_state.display, nullptr, nullptr)) {
    return false;
  }
  const EGLint config_attribs[] = {
      EGL_RENDERABLE_TYPE, EGL_OPENGL_ES3_BIT,
      EGL_SURFACE_TYPE, EGL_WINDOW_BIT,
      EGL_RED_SIZE, 8, EGL_GREEN_SIZE, 8, EGL_BLUE_SIZE, 8, EGL_ALPHA_SIZE, 8,
      EGL_NONE};
  EGLConfig config = nullptr;
  EGLint count = 0;
  if (!eglChooseConfig(g_state.display, config_attribs, &config, 1, &count) ||
      count == 0) {
    return false;
  }
  g_state.surface = eglCreateWindowSurface(
      g_state.display, config, reinterpret_cast<EGLNativeWindowType>(window),
      nullptr);
  const EGLint context_attribs[] = {EGL_CONTEXT_CLIENT_VERSION, 3, EGL_NONE};
  g_state.context = eglCreateContext(g_state.display, config, EGL_NO_CONTEXT,
                                     context_attribs);
  if (g_state.surface == EGL_NO_SURFACE ||
      g_state.context == EGL_NO_CONTEXT ||
      !eglMakeCurrent(g_state.display, g_state.surface, g_state.surface,
                      g_state.context)) {
    return false;
  }

  g_state.renderer = sp_gles_renderer_create();
  if (!g_state.renderer) return false;
  std::vector<uint8_t> image(static_cast<size_t>(kTestWidth) * kTestHeight * 4);
  sp_make_test_image(image.data(), kTestWidth, kTestHeight);
  return sp_renderer_set_image(g_state.renderer, image.data(), kTestWidth,
                               kTestHeight) == SP_OK;
}

// Draws one frame and returns the GPU time in milliseconds, or -1.
double draw_frame() {
  if (!g_state.renderer || g_state.width <= 0 || g_state.height <= 0) {
    return -1.0;
  }
  const auto start = std::chrono::steady_clock::now();
  eglMakeCurrent(g_state.display, g_state.surface, g_state.surface,
                 g_state.context);
  sp_renderer_set_params(g_state.renderer, &g_state.params);
  sp_renderer_set_filter(g_state.renderer, g_state.filter.c_str(),
                         g_state.strength);
  sp_renderer_set_geometry(g_state.renderer, &g_state.geometry);
  if (sp_gles_renderer_draw(g_state.renderer, g_state.width, g_state.height) !=
      SP_OK) {
    return -1.0;
  }
  // glFinish makes the number meaningful for M0; the app will not block here.
  glFinish();
  const std::chrono::duration<double, std::milli> elapsed =
      std::chrono::steady_clock::now() - start;
  eglSwapBuffers(g_state.display, g_state.surface);
  return elapsed.count();
}

void update_size(OH_NativeXComponent* component, void* window) {
  uint64_t width = 0, height = 0;
  if (OH_NativeXComponent_GetXComponentSize(component, window, &width,
                                            &height) ==
      OH_NATIVEXCOMPONENT_RESULT_SUCCESS) {
    g_state.width = static_cast<int>(width);
    g_state.height = static_cast<int>(height);
  }
}

void on_surface_created(OH_NativeXComponent* component, void* window) {
  update_size(component, window);
  if (!create_gl(window)) {
    OH_LOG_ERROR(LOG_APP, "SPhoto: failed to create the GLES renderer");
    destroy_gl();
    return;
  }
  draw_frame();
}

void on_surface_changed(OH_NativeXComponent* component, void* window) {
  update_size(component, window);
  draw_frame();
}

void on_surface_destroyed(OH_NativeXComponent*, void*) { destroy_gl(); }

void on_touch(OH_NativeXComponent*, void*) {}

OH_NativeXComponent_Callback g_callbacks = {
    on_surface_created, on_surface_changed, on_surface_destroyed, on_touch};

// setParams(values: number[]): one value per adjustment, in sp_params order.
napi_value set_params(napi_env env, napi_callback_info info) {
  size_t argc = 1;
  napi_value array = nullptr;
  napi_get_cb_info(env, info, &argc, &array, nullptr, nullptr);
  sp_params params{};
  uint32_t length = 0;
  if (argc == 1 && napi_get_array_length(env, array, &length) == napi_ok) {
    for (uint32_t i = 0; i < length && i < static_cast<uint32_t>(sp_param_count()); ++i) {
      napi_value item = nullptr;
      double value = 0.0;
      napi_get_element(env, array, i, &item);
      napi_get_value_double(env, item, &value);
      sp_params_set(&params, static_cast<int>(i), static_cast<float>(value));
    }
  }
  g_state.params = params;
  napi_value result;
  napi_create_double(env, draw_frame(), &result);
  return result;
}

// setLook(filter, strength, quarterTurns, flip, straighten): the filter id
// ('' for none), its strength and the geometry.
napi_value set_look(napi_env env, napi_callback_info info) {
  size_t argc = 5;
  napi_value args[5] = {};
  napi_get_cb_info(env, info, &argc, args, nullptr, nullptr);
  if (argc == 5) {
    char id[32] = {};
    size_t length = 0;
    napi_get_value_string_utf8(env, args[0], id, sizeof(id), &length);
    double strength = 1.0, straighten = 0.0;
    int32_t turns = 0;
    bool flip = false;
    napi_get_value_double(env, args[1], &strength);
    napi_get_value_int32(env, args[2], &turns);
    napi_get_value_bool(env, args[3], &flip);
    napi_get_value_double(env, args[4], &straighten);
    g_state.filter = id;
    g_state.strength = static_cast<float>(strength);
    g_state.geometry = sp_geometry{};
    g_state.geometry.quarter_turns = turns;
    g_state.geometry.flip_horizontal = flip ? 1 : 0;
    g_state.geometry.straighten = static_cast<float>(straighten);
  }
  napi_value result;
  napi_create_double(env, draw_frame(), &result);
  return result;
}

// filterIds(): the built-in filters' ids.
napi_value filter_ids(napi_env env, napi_callback_info) {
  const int n = sp_filter_count();
  napi_value list;
  napi_create_array_with_length(env, n, &list);
  for (int i = 0; i < n; ++i) {
    napi_value id;
    napi_create_string_utf8(env, sp_filter_id(i), NAPI_AUTO_LENGTH, &id);
    napi_set_element(env, list, static_cast<uint32_t>(i), id);
  }
  return list;
}

// paramInfo(): [{ key, min, max }] for each adjustment, in sp_params order.
napi_value param_info(napi_env env, napi_callback_info) {
  const int n = sp_param_count();
  napi_value list;
  napi_create_array_with_length(env, n, &list);
  for (int i = 0; i < n; ++i) {
    const char* name = nullptr;
    float lo = 0.0f, hi = 0.0f;
    sp_param_info(i, &name, &lo, &hi);
    napi_value item, key, min_value, max_value;
    napi_create_object(env, &item);
    napi_create_string_utf8(env, name, NAPI_AUTO_LENGTH, &key);
    napi_create_double(env, lo, &min_value);
    napi_create_double(env, hi, &max_value);
    napi_set_named_property(env, item, "key", key);
    napi_set_named_property(env, item, "min", min_value);
    napi_set_named_property(env, item, "max", max_value);
    napi_set_element(env, list, static_cast<uint32_t>(i), item);
  }
  return list;
}

napi_value version(napi_env env, napi_callback_info) {
  napi_value result;
  napi_create_string_utf8(env, sp_version(), NAPI_AUTO_LENGTH, &result);
  return result;
}

napi_value init(napi_env env, napi_value exports) {
  napi_property_descriptor desc[] = {
      {"setParams", nullptr, set_params, nullptr, nullptr, nullptr,
       napi_default, nullptr},
      {"setLook", nullptr, set_look, nullptr, nullptr, nullptr, napi_default,
       nullptr},
      {"filterIds", nullptr, filter_ids, nullptr, nullptr, nullptr,
       napi_default, nullptr},
      {"paramInfo", nullptr, param_info, nullptr, nullptr, nullptr,
       napi_default, nullptr},
      {"version", nullptr, version, nullptr, nullptr, nullptr, napi_default,
       nullptr},
  };
  napi_define_properties(env, exports, sizeof(desc) / sizeof(desc[0]), desc);

  // When this module is loaded for an XComponent (libraryname: 'sphoto'),
  // exports carries the native component: register the surface callbacks.
  napi_value component_obj = nullptr;
  if (napi_get_named_property(env, exports, OH_NATIVE_XCOMPONENT_OBJ,
                              &component_obj) == napi_ok) {
    OH_NativeXComponent* component = nullptr;
    if (napi_unwrap(env, component_obj,
                    reinterpret_cast<void**>(&component)) == napi_ok &&
        component) {
      OH_NativeXComponent_RegisterCallback(component, &g_callbacks);
    }
  }
  return exports;
}

napi_module g_module = {
    .nm_version = 1,
    .nm_flags = 0,
    .nm_filename = nullptr,
    .nm_register_func = init,
    .nm_modname = "sphoto",
    .nm_priv = nullptr,
    .reserved = {0},
};

}  // namespace

extern "C" __attribute__((constructor)) void register_sphoto_module(void) {
  napi_module_register(&g_module);
}
