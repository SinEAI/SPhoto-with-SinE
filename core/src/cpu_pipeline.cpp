// CPU reference build of pipeline.inc. Slow but exact: the GPU backends are
// tested against it, and the command-line tool uses it for exports.
#include <algorithm>
#include <cstdint>
#include <thread>
#include <vector>

#include "frame.h"
#include "params.h"
#include "shading.h"
#include "sphoto/sphoto.h"

namespace sp::sl {
namespace {

using SpParams = sp_params;
using SpFrame = Frame;

// An RGBA8 source read the way a GPU sampler with linear filtering and
// clamp-to-edge addressing reads it, and the filter table, if any.
struct Source {
  const uint8_t* rgba;
  int width;
  int height;
  const float* lut;  // lut_size^3 RGB triples, red fastest
  int lut_size;
};

float texel(const Source& s, int x, int y, int channel) {
  x = std::clamp(x, 0, s.width - 1);
  y = std::clamp(y, 0, s.height - 1);
  return s.rgba[(static_cast<size_t>(y) * s.width + x) * 4 + channel] / 255.0f;
}

vec3 sp_fetch(const Source& s, vec2 uv) {
  const float fx = uv.x * s.width - 0.5f;
  const float fy = uv.y * s.height - 0.5f;
  const float x0 = std::floor(fx), y0 = std::floor(fy);
  const float tx = fx - x0, ty = fy - y0;
  const int ix = static_cast<int>(x0), iy = static_cast<int>(y0);
  float c[3];
  for (int ch = 0; ch < 3; ++ch) {
    const float top = texel(s, ix, iy, ch) * (1.0f - tx) + texel(s, ix + 1, iy, ch) * tx;
    const float bottom =
        texel(s, ix, iy + 1, ch) * (1.0f - tx) + texel(s, ix + 1, iy + 1, ch) * tx;
    c[ch] = top * (1.0f - ty) + bottom * ty;
  }
  return {c[0], c[1], c[2]};
}

// Trilinear lookup, as a GPU samples a 3D texture with texel centres at the
// table points.
vec3 sp_lut(const Source& s, vec3 c) {
  const int n = s.lut_size;
  float at[3] = {clamp(c.x, 0.0f, 1.0f) * (n - 1), clamp(c.y, 0.0f, 1.0f) * (n - 1),
                 clamp(c.z, 0.0f, 1.0f) * (n - 1)};
  int i0[3], i1[3];
  float t[3];
  for (int k = 0; k < 3; ++k) {
    i0[k] = std::min(static_cast<int>(at[k]), n - 1);
    i1[k] = std::min(i0[k] + 1, n - 1);
    t[k] = at[k] - i0[k];
  }
  float out[3] = {0, 0, 0};
  for (int corner = 0; corner < 8; ++corner) {
    const int r = corner & 1 ? i1[0] : i0[0];
    const int g = corner & 2 ? i1[1] : i0[1];
    const int b = corner & 4 ? i1[2] : i0[2];
    const float w = (corner & 1 ? t[0] : 1 - t[0]) * (corner & 2 ? t[1] : 1 - t[1]) *
                    (corner & 4 ? t[2] : 1 - t[2]);
    const float* v = s.lut + ((static_cast<size_t>(b) * n + g) * n + r) * 3;
    for (int k = 0; k < 3; ++k) out[k] += w * v[k];
  }
  return {out[0], out[1], out[2]};
}

#define SP_TEX_PARAMS const Source &sp_source,
#define SP_TEX_ARGS sp_source,
#define SP_PIPELINE_SOURCE(...) __VA_ARGS__
#include "pipeline.inc"
#undef SP_PIPELINE_SOURCE
#undef SP_TEX_ARGS
#undef SP_TEX_PARAMS

uint8_t to_byte(float c) {
  return static_cast<uint8_t>(std::lround(std::clamp(c, 0.0f, 1.0f) * 255.0f));
}

}  // namespace
}  // namespace sp::sl

namespace sp {

void render_cpu(const sp_params& params, const Frame& frame, const float* lut,
                int lut_size, const uint8_t* src, int width, int height,
                uint8_t* dst, int out_width, int out_height) {
  const sl::Source source{src, width, height, lut, lut_size};
  Frame f = frame;
  if (!lut) f.lut_strength = 0.0f;

  auto rows = [&](int first, int last) {
    for (int y = first; y < last; ++y) {
      for (int x = 0; x < out_width; ++x) {
        const sl::vec2 uv((x + 0.5f) / out_width, (y + 0.5f) / out_height);
        const sl::vec3 e = sl::sp_process(source, params, f, uv);
        const sl::vec2 suv = sl::sp_source_uv(f, uv);
        // Alpha comes from the nearest source pixel's neighbourhood, as the
        // GPU's filtered read does; photos are opaque in practice.
        const int sx = std::clamp(static_cast<int>(suv.x * width), 0, width - 1);
        const int sy = std::clamp(static_cast<int>(suv.y * height), 0, height - 1);
        const size_t i = (static_cast<size_t>(y) * out_width + x) * 4;
        dst[i + 0] = sl::to_byte(e.x);
        dst[i + 1] = sl::to_byte(e.y);
        dst[i + 2] = sl::to_byte(e.z);
        dst[i + 3] = src[(static_cast<size_t>(sy) * width + sx) * 4 + 3];
      }
    }
  };

  const int threads = static_cast<int>(
      std::clamp(std::thread::hardware_concurrency(), 1u, 16u));
  if (threads == 1 || static_cast<long>(out_width) * out_height < 65536) {
    rows(0, out_height);
    return;
  }
  std::vector<std::thread> pool;
  const int chunk = (out_height + threads - 1) / threads;
  for (int t = 0; t < threads; ++t) {
    const int first = t * chunk, last = std::min(out_height, first + chunk);
    if (first < last) pool.emplace_back(rows, first, last);
  }
  for (auto& thread : pool) thread.join();
}

}  // namespace sp
