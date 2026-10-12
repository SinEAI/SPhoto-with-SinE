#include <algorithm>
#include <cmath>
#include <cstdint>

#include <cstring>
#include <vector>

#include "filters.h"
#include "frame.h"
#include "grading.h"
#include "params.h"
#include "renderer.h"
#include "sphoto/sphoto.h"

#ifndef SPHOTO_VERSION
#define SPHOTO_VERSION "0.1.0"
#endif

namespace {

uint8_t to_byte(float c) {
  return static_cast<uint8_t>(std::lround(std::clamp(c, 0.0f, 1.0f) * 255.0f));
}

// HSV with s = 1, v = 1 to RGB.
void hue_to_rgb(float h, float rgb[3]) {
  for (int i = 0; i < 3; ++i) {
    const float k = std::fmod(5.0f - 2.0f * i + h * 6.0f, 6.0f);
    rgb[i] = 1.0f - std::max(0.0f, std::min({k, 4.0f - k, 1.0f}));
  }
}

}  // namespace

namespace sp {
void render_cpu(const sp_params& params, const Frame& frame, const float* lut,
                int lut_size, const uint8_t* src, int width, int height,
                uint8_t* dst, int out_width, int out_height);
}

extern "C" {

const char* sp_version(void) { return SPHOTO_VERSION; }

void sp_params_default(sp_params* params) {
  if (params) *params = sp_params{};
}

int sp_make_test_image(uint8_t* dst, int width, int height) {
  if (!dst || width <= 0 || height <= 0) return SP_ERR_ARGUMENT;
  const int wedge_top = height - height / 8;
  for (int y = 0; y < height; ++y) {
    for (int x = 0; x < width; ++x) {
      float rgb[3];
      if (y >= wedge_top) {
        // 11-step grey wedge, evenly spaced in sRGB.
        const float g = std::floor(11.0f * x / width) / 10.0f;
        rgb[0] = rgb[1] = rgb[2] = g;
      } else {
        hue_to_rgb(static_cast<float>(x) / width, rgb);
        // Top half fades to white, bottom half to black.
        const float t = static_cast<float>(y) / wedge_top;
        for (float& c : rgb) {
          c = t < 0.5f ? c + (1.0f - c) * (1.0f - 2.0f * t)
                       : c * (2.0f - 2.0f * t);
        }
      }
      uint8_t* px = dst + (static_cast<size_t>(y) * width + x) * 4;
      px[0] = to_byte(rgb[0]);
      px[1] = to_byte(rgb[1]);
      px[2] = to_byte(rgb[2]);
      px[3] = 255;
    }
  }
  return SP_OK;
}

int sp_param_count(void) { return sp::kParamCount; }

int sp_param_info(int index, const char** name, float* min_value,
                  float* max_value) {
  if (index < 0 || index >= sp::kParamCount) return SP_ERR_ARGUMENT;
  if (name) *name = sp::kParams[index].name;
  if (min_value) *min_value = sp::kParams[index].min;
  if (max_value) *max_value = sp::kParams[index].max;
  return SP_OK;
}

float sp_params_get(const sp_params* params, int index) {
  if (!params || index < 0 || index >= sp::kParamCount) return 0.0f;
  return sp::param_data(*params)[index];
}

void sp_params_set(sp_params* params, int index, float value) {
  if (!params || index < 0 || index >= sp::kParamCount) return;
  sp::param_data(*params)[index] = value;
}

int sp_process_cpu(const sp_params* params, const uint8_t* src, uint8_t* dst,
                   int width, int height) {
  if (!params || !src || !dst || width <= 0 || height <= 0) {
    return SP_ERR_ARGUMENT;
  }
  sp_edit edit{};
  edit.adjust = *params;
  return sp_render_cpu(&edit, src, width, height, dst, width, height);
}

int sp_render_cpu(const sp_edit* edit, const uint8_t* src, int width,
                  int height, uint8_t* dst, int out_width, int out_height) {
  if (!edit || !src || !dst || width <= 0 || height <= 0 || out_width <= 0 ||
      out_height <= 0) {
    return SP_ERR_ARGUMENT;
  }
  char id[sizeof(edit->filter) + 1] = {};
  std::memcpy(id, edit->filter, sizeof(edit->filter));
  std::vector<float> lut;
  if (!sp::bake_grading(id, edit->filter_strength, edit->curves, edit->hsl, &lut)) {
    return SP_ERR_ARGUMENT;
  }
  const sp::Geometry g = sp::solve_geometry(edit->geometry, width, height);
  const sp::Frame frame = sp::make_frame(g, width, height, out_width, out_height,
                                         lut.empty() ? 0.0f : 1.0f);
  // The pipeline reads neighbours, so it cannot write over its own source.
  std::vector<uint8_t> copy;
  if (src == dst ||
      (dst < src + static_cast<size_t>(width) * height * 4 &&
       src < dst + static_cast<size_t>(out_width) * out_height * 4)) {
    copy.assign(src, src + static_cast<size_t>(width) * height * 4);
    src = copy.data();
  }
  sp::render_cpu(edit->adjust, frame, lut.empty() ? nullptr : lut.data(),
                 sp::kLutSize, src, width, height, dst, out_width, out_height);
  return SP_OK;
}

int sp_filter_count(void) { return sp::filter_count(); }

const char* sp_filter_id(int index) { return sp::filter_id(index); }

int sp_geometry_output_size(const sp_geometry* geometry, int src_width,
                            int src_height, int* out_width, int* out_height) {
  if (!geometry || src_width <= 0 || src_height <= 0) return SP_ERR_ARGUMENT;
  const sp::Geometry g = sp::solve_geometry(*geometry, src_width, src_height);
  if (out_width) *out_width = g.out_width;
  if (out_height) *out_height = g.out_height;
  return SP_OK;
}

int sp_renderer_set_image(sp_renderer* r, const uint8_t* rgba, int width,
                          int height) {
  if (!r || !rgba || width <= 0 || height <= 0) return SP_ERR_ARGUMENT;
  const int rc = r->set_image(rgba, width, height);
  if (rc == SP_OK) {
    r->image_width = width;
    r->image_height = height;
  }
  return rc;
}

int sp_renderer_set_filter(sp_renderer* r, const char* id, float strength) {
  if (!r) return SP_ERR_ARGUMENT;
  const std::string next = id ? id : "";
  if (!next.empty() && !sp::is_filter(next.c_str())) return SP_ERR_ARGUMENT;
  strength = strength < 0.0f ? 0.0f : strength > 1.0f ? 1.0f : strength;
  // The strength is baked into the table too.
  if (next != r->filter || strength != r->filter_strength) {
    r->filter = next;
    r->filter_strength = strength;
    r->bake();
  }
  return SP_OK;
}

void sp_renderer_set_geometry(sp_renderer* r, const sp_geometry* geometry) {
  if (r && geometry) r->geometry = *geometry;
}

int sp_renderer_set_edit(sp_renderer* r, const sp_edit* edit) {
  if (!r || !edit) return SP_ERR_ARGUMENT;
  char id[sizeof(edit->filter) + 1] = {};
  std::memcpy(id, edit->filter, sizeof(edit->filter));
  if (id[0] && !sp::is_filter(id)) return SP_ERR_ARGUMENT;
  const float s = edit->filter_strength;
  const float strength = s < 0.0f ? 0.0f : s > 1.0f ? 1.0f : s;
  // Baking takes a moment, so only when the grading actually changed.
  if (r->filter != id || r->filter_strength != strength ||
      std::memcmp(r->curves, edit->curves, sizeof(r->curves)) != 0 ||
      std::memcmp(&r->hsl, &edit->hsl, sizeof(r->hsl)) != 0) {
    r->filter = id;
    r->filter_strength = strength;
    std::memcpy(r->curves, edit->curves, sizeof(r->curves));
    r->hsl = edit->hsl;
    r->bake();
  }
  r->params = edit->adjust;
  r->geometry = edit->geometry;
  return SP_OK;
}

int sp_renderer_output_size(sp_renderer* r, int* out_width, int* out_height) {
  if (!r || r->image_width <= 0) return SP_ERR_ARGUMENT;
  return sp_geometry_output_size(&r->geometry, r->image_width, r->image_height,
                                 out_width, out_height);
}

void sp_renderer_set_params(sp_renderer* r, const sp_params* params) {
  if (r && params) r->params = *params;
}

int sp_renderer_render_rgba(sp_renderer* r, uint8_t* dst, int out_width,
                            int out_height) {
  if (!r || !dst || out_width <= 0 || out_height <= 0) return SP_ERR_ARGUMENT;
  return r->render_rgba(dst, out_width, out_height);
}

void sp_renderer_destroy(sp_renderer* r) { delete r; }

}  // extern "C"
