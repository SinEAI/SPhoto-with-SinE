#include <algorithm>
#include <array>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <vector>

#include "../src/frame.h"
#include "../src/shading.h"
#include "sphoto/sphoto.h"
#include "test.h"

// Compile the actual shared kernel to exercise floating-point invariants
// before the output clamp / 8-bit quantization can conceal an overflow.
// Inside sp::sl, as in cpu_pipeline.cpp, so the kernel's exp2/pow find
// shading.h's versions first instead of clashing with <cmath>'s globals
// (an ambiguous call with libc++ on macOS).
namespace sp::sl::tone_test {
using SpParams = sp_params;
using SpFrame = sp::Frame;
vec3 sp_fetch(vec2) { return vec3(0.0f); }
vec3 sp_lut(vec3 c) { return c; }
#define SP_TEX_PARAMS
#define SP_TEX_ARGS
#define SP_PIPELINE_SOURCE(...) __VA_ARGS__
#include "../src/pipeline.inc"
#undef SP_PIPELINE_SOURCE
#undef SP_TEX_ARGS
#undef SP_TEX_PARAMS

float peak(vec3 c) { return std::max({c.x, c.y, c.z}); }
float error(vec3 a, vec3 b) {
  return std::max({std::abs(a.x - b.x), std::abs(a.y - b.y), std::abs(a.z - b.z)});
}
bool bounded(vec3 c) {
  return std::isfinite(c.x) && std::isfinite(c.y) && std::isfinite(c.z) &&
         std::min({c.x, c.y, c.z}) >= 0.0f && peak(c) <= 1.000003f;
}
float random01(uint32_t& state) {
  state = 1664525u * state + 1013904223u;
  return static_cast<float>(state >> 8) / 16777215.0f;
}
sp_params random_params(uint32_t& state) {
  sp_params p{};
  p.exposure = 8.0f * random01(state) - 4.0f;
  p.contrast = 2.0f * random01(state) - 1.0f;
  p.temperature = 2.0f * random01(state) - 1.0f;
  p.tint = 2.0f * random01(state) - 1.0f;
  p.highlights = 2.0f * random01(state) - 1.0f;
  p.shadows = 2.0f * random01(state) - 1.0f;
  return p;
}
}  // namespace sp::sl::tone_test

TEST(coloured_highlights_have_a_valid_ray_bound) {
  using namespace sp::sl;
  using namespace sp::sl::tone_test;
  sp_params p{};
  p.exposure = 1.0f;
  p.highlights = -1.0f;
  const vec3 white = sp_tone(vec3(1.0f), p);
  EXPECT(std::abs(white.x - 0.70710678f) < 0.000002f);
  EXPECT(sp_tone(vec3(1.0f, 0.0f, 0.0f), p).x > 1.2f);
  EXPECT(sp_tone(vec3(0.0f, 0.0f, 1.0f), p).z > 1.6f);
  for (vec3 source : {vec3(1.0f, 0.0f, 0.0f), vec3(0.0f, 0.0f, 1.0f),
                      vec3(1.0f, 0.0f, 1.0f), vec3(1.0f, 0.25f, 0.02f)}) {
    EXPECT(bounded(sp_tone_mapped(source, p)));
    const float expected = std::min(1.0f, peak(sp_tone(source, p)));
    EXPECT(std::abs(peak(sp_tone_mapped(source, p)) - expected) < 0.000003f);
  }
  EXPECT(error(sp_tone_mapped(vec3(1.0f), p), white) < 0.000002f);
}

TEST(highlight_rolloff_preserves_toned_channel_ratios) {
  using namespace sp::sl;
  using namespace sp::sl::tone_test;
  uint32_t seed = 0x57920431u;
  for (int i = 0; i < 20000; ++i) {
    vec3 source(random01(seed), random01(seed), random01(seed));
    const sp_params p = random_params(seed);
    const vec3 before = sp_tone(source, p);
    const vec3 after = sp_tone_mapped(source, p);
    EXPECT(bounded(after));
    const float scale = peak(before) > 0.0f ? peak(after) / peak(before) : 1.0f;
    EXPECT(error(after, before * scale) < 0.000003f);
  }
}

TEST(neutral_rolloff_preserves_the_existing_curve) {
  using namespace sp::sl;
  using namespace sp::sl::tone_test;
  uint32_t seed = 0x4829107u;
  for (int i = 0; i < 300; ++i) {
    sp_params p = random_params(seed);
    // Neutral source with neutral WB is the existing neutral rendering.
    p.temperature = p.tint = 0.0f;
    const float w = sp_tone(vec3(1.0f), p).x;
    for (int n = 0; n <= 256; ++n) {
      const vec3 source(n / 256.0f);
      const float tone = sp_tone(source, p).x;
      const float expected = w > 1.0f ? sp_shoulder(tone, w) : tone;
      EXPECT(error(sp_tone_mapped(source, p), vec3(expected)) < 0.000003f);
    }
  }
}

TEST(tone_bounds_cover_parameter_corners_and_colour_rays) {
  using namespace sp::sl;
  using namespace sp::sl::tone_test;
  const vec3 rays[] = {
      {1, 0, 0}, {0, 1, 0}, {0, 0, 1}, {1, 1, 0}, {1, 0, 1}, {0, 1, 1}, {1, 1, 1},
      {1, 0.6f, 0.3f}, {1, 0.25f, 0.1f}, {0.5f, 1, 0.2f}, {0.3f, 0.6f, 1},
      {1, 0.9999f, 0.9998f}, {1, 0.0001f, 0.0002f}};
  for (int mask = 0; mask < 729; ++mask) {
    int m = mask;
    float v[6];
    for (float& f : v) { f = static_cast<float>(m % 3 - 1); m /= 3; }
    sp_params p{};
    p.exposure = 4.0f * v[0]; p.contrast = v[1];
    p.temperature = v[2]; p.tint = v[3]; p.highlights = v[4]; p.shadows = v[5];
    for (vec3 ray : rays) {
      float last = -1.0f;
      for (int n = 0; n <= 64; ++n) {
        const vec3 output = sp_tone_mapped(ray * (n / 64.0f), p);
        EXPECT(bounded(output));
        EXPECT(peak(output) + 0.000003f >= last);
        last = peak(output);
      }
    }
  }
}

TEST(shoulder_is_continuous_at_knee_and_activation) {
  using namespace sp::sl;
  using namespace sp::sl::tone_test;
  for (float w : {1.001f, 1.1f, 2.0f, 16.0f, 400.0f}) {
    EXPECT(std::abs(sp_shoulder(0.6f, w) - 0.6f) < 0.000001f);
    const float dx = 0.0001f;
    const float right_slope = (sp_shoulder(0.6f + dx, w) - 0.6f) / dx;
    EXPECT(std::abs(right_slope - 1.0f) < 0.001f);
    EXPECT(std::abs(sp_shoulder(w, w) - 1.0f) < 0.000001f);
  }
  const vec3 source(0.9f, 0.7f, 0.5f);
  sp_params p{};
  p.exposure = -0.00001f;
  const vec3 below = sp_tone_mapped(source, p);
  p.exposure = 0.00001f;
  EXPECT(error(below, sp_tone_mapped(source, p)) < 0.00003f);
  // Swapping the largest component across an R/G tie must not jump.
  p.exposure = 2.0f; p.temperature = 0.7f; p.highlights = -0.4f;
  EXPECT(error(sp_tone_mapped(vec3(0.8f, 0.800001f, 0.3f), p),
               sp_tone_mapped(vec3(0.800001f, 0.8f, 0.3f), p)) < 0.00001f);
}

TEST(coloured_highlight_ramps_do_not_end_in_a_clipped_plateau) {
  const std::array<std::array<int, 3>, 3> colours = {{{1, 0, 0}, {0, 0, 1}, {1, 0, 1}}};
  sp_params p{};
  p.exposure = 1.0f; p.highlights = -1.0f;
  for (const auto& colour : colours) {
    std::vector<uint8_t> source, output(256 * 4);
    for (int v = 0; v < 256; ++v) {
      for (int c : colour) source.push_back(static_cast<uint8_t>(c * v));
      source.push_back(255);
    }
    EXPECT(sp_process_cpu(&p, source.data(), output.data(), 256, 1) == SP_OK);
    const int ch = colour[0] ? 0 : 2;
    EXPECT(output[255 * 4 + ch] == 255);
    EXPECT(output[250 * 4 + ch] < 255);
    for (int v = 1; v < 256; ++v) EXPECT(output[v * 4 + ch] >= output[(v - 1) * 4 + ch]);
    // Floating-point tests above cover ray monotonicity before clamping; 8-bit
    // quantization legitimately repeats adjacent values near a shoulder.
  }
}

TEST(identity_tone_preserves_saturated_and_skin_colours) {
  using namespace sp::sl;
  using namespace sp::sl::tone_test;
  const sp_params p{};
  for (int r = 0; r <= 16; ++r) for (int g = 0; g <= 16; ++g) for (int b = 0; b <= 16; ++b) {
    const vec3 source(r / 16.0f, g / 16.0f, b / 16.0f);
    EXPECT(error(sp_tone_mapped(source, p), source) < 0.000001f);
  }
}

TEST(saturation_limits_chroma_without_changing_luminance) {
  using namespace sp::sl;
  using namespace sp::sl::tone_test;
  uint32_t seed = 0x05100921u;
  for (int i = 0; i < 50000; ++i) {
    const vec3 c(random01(seed), random01(seed), random01(seed));
    const float y = sp_luma(c);
    const float amount = 2.0f * random01(seed) - 1.0f;
    const vec3 requested = vec3(y) + (c - vec3(y)) * (1.0f + amount);
    const vec3 result = sp_saturate(c, amount);
    EXPECT(bounded(result));
    EXPECT(std::abs(sp_luma(result) - y) < 0.000001f);
    if (bounded(requested)) EXPECT(error(requested, result) < 0.000004f);
    // A common chroma scale keeps all pairwise channel differences in ratio.
    const vec3 d = c - vec3(y), out_d = result - vec3(y);
    const float norm = std::max({std::abs(d.x), std::abs(d.y), std::abs(d.z)});
    if (norm > 0.0001f) {
      const float scale = std::max({std::abs(out_d.x), std::abs(out_d.y), std::abs(out_d.z)}) / norm;
      EXPECT(error(out_d, d * scale) < 0.000002f);
      EXPECT(scale <= 1.0f + amount + 0.00001f);
    }
  }
}

TEST(saturation_preserves_neutrals_and_existing_in_gamut_requests) {
  using namespace sp::sl;
  using namespace sp::sl::tone_test;
  for (int n = 0; n <= 256; ++n) {
    const float grey = n / 256.0f;
    for (float amount : {-1.0f, -0.5f, 0.0f, 0.5f, 1.0f}) {
      EXPECT(error(sp_saturate(vec3(grey), amount), vec3(grey)) < 0.000001f);
    }
  }
  for (vec3 c : {vec3(0.8f, 0.2f, 0.1f), vec3(0.3f, 0.6f, 0.7f), vec3(1, 0, 0),
                 vec3(0, 0, 1), vec3(1, 1, 0), vec3(0.7f, 0.45f, 0.3f)}) {
    const float y = sp_luma(c);
    EXPECT(error(sp_saturate(c, 0.0f), c) < 0.000001f);
    EXPECT(error(sp_saturate(c, -1.0f), vec3(y)) < 0.000001f);
    EXPECT(error(sp_saturate(c, -0.5f), vec3(y) + (c - vec3(y)) * 0.5f) < 0.000001f);
    float previous_chroma = 0.0f;
    for (int n = 0; n <= 100; ++n) {
      const vec3 out = sp_saturate(c, n / 100.0f);
      const float chroma = error(out, vec3(y));
      EXPECT(chroma + 0.000001f >= previous_chroma);
      previous_chroma = chroma;
    }
  }
}

TEST(saturation_clipping_regression_has_constant_y) {
  using namespace sp::sl;
  using namespace sp::sl::tone_test;
  const vec3 c(0.8f, 0.2f, 0.1f);
  const float y = sp_luma(c);
  const vec3 old = clamp(vec3(y) + (c - vec3(y)) * 2.0f, vec3(0.0f), vec3(1.0f));
  const vec3 fixed = sp_saturate(c, 1.0f);
  EXPECT(std::abs(sp_luma(old) - y) > 0.04f);
  EXPECT(std::abs(sp_luma(fixed) - y) < 0.000001f);
  EXPECT(std::abs(fixed.x - 1.0f) < 0.000001f);
  EXPECT(fixed.z > 0.0f);
}
