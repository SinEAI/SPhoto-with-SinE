// Every GPU backend must match the CPU reference. Runs whichever backends
// this platform builds; skips when no GPU context can be created.
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "sphoto/sphoto.h"
#include "test.h"

namespace {

constexpr int kW = 160;
constexpr int kH = 120;
// GPUs evaluate pow() with less precision than the CPU. The error is tiny in
// linear light but the sRGB curve is steep near black, so a few very dark
// channels can differ by several 8-bit levels. Almost every value must still
// match to within 2 levels.
constexpr int kTypicalTolerance = 2;
constexpr int kWorstTolerance = 8;
constexpr double kMaxShareOverTypical = 0.01;
constexpr int kGeometryWorstTolerance = 12;

// One case per adjustment at a strong setting, plus mixes. Fields follow
// sp_params order: exposure, contrast, saturation, temperature, tint,
// highlights, shadows, fade, sharpen, clarity, vignette, grain.
const sp_params kCases[] = {
    {},
    {1.5f},
    {1.0f, 0, 0, 0, 0, -1.0f},
    {1.0f, 0.6f, 0, 0.7f, 0.2f, -0.4f},
    {4.0f, 1.0f, 0, -1.0f, 1.0f, -1.0f, 1.0f},
    {-2.0f},
    {0, 0.8f},
    {0, -0.6f},
    {0, 0, -1.0f},
    {0, 0, 0.7f},
    {1.0f, 0.6f, 1.0f, 0.7f, -0.4f, -1.0f, 0.8f},
    {0, 0, 0, 0.8f},
    {0, 0, 0, -0.8f, 0.6f},
    {0, 0, 0, 0, 0, -0.9f},
    {0, 0, 0, 0, 0, 0, 0.9f},
    {0, 0, 0, 0, 0, 0, 0, 0.7f},
    {0, 0, 0, 0, 0, 0, 0, 0, 1.0f},
    {0, 0, 0, 0, 0, 0, 0, 0, 0, 1.0f},
    {0, 0, 0, 0, 0, 0, 0, 0, 0, -1.0f},
    {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0.8f},
    {0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1.0f},
    /* Boundary pressure: ray shoulder + gamut-limited saturation. */
    {1.0f, 0, 1.0f, 0, 0, -1.0f},
    {1.0f, 0.2f, 0.8f, 0.4f, 0, -0.8f, 0.2f},
    {0.7f, 0.4f, 0.5f, 0.3f, -0.2f, -0.4f, 0.5f, 0.2f, 0.5f, 0.4f, 0.3f, 0.3f},
    {-0.5f, -0.3f, -0.4f, -0.3f, 0.2f, 0.3f, -0.3f, 0.0f, 0.2f, -0.5f, -0.4f, 0.0f},
};

std::string describe(const sp_params& p) {
  std::string text;
  for (int i = 0; i < sp_param_count(); ++i) {
    const float v = sp_params_get(&p, i);
    if (v == 0.0f) continue;
    const char* name = nullptr;
    sp_param_info(i, &name, nullptr, nullptr);
    char item[48];
    std::snprintf(item, sizeof(item), "%s%s=%.1f", text.empty() ? "" : " ", name, v);
    text += item;
  }
  return text.empty() ? "untouched" : text;
}

void compare_with_cpu(sp_renderer* renderer, const char* backend) {
  std::vector<uint8_t> src(kW * kH * 4);
  sp_make_test_image(src.data(), kW, kH);
  EXPECT(sp_renderer_set_image(renderer, src.data(), kW, kH) == SP_OK);

  std::vector<uint8_t> cpu(src.size()), gpu(src.size());
  for (const sp_params& p : kCases) {
    sp_process_cpu(&p, src.data(), cpu.data(), kW, kH);
    sp_renderer_set_params(renderer, &p);
    EXPECT(sp_renderer_render_rgba(renderer, gpu.data(), kW, kH) == SP_OK);
    int worst = 0;
    size_t over_typical = 0;
    for (size_t i = 0; i < cpu.size(); ++i) {
      const int diff = std::abs(cpu[i] - gpu[i]);
      worst = std::max(worst, diff);
      if (diff > kTypicalTolerance) ++over_typical;
    }
    const double share = static_cast<double>(over_typical) / cpu.size();
    std::printf("  %s %s: max diff %d, %.3f%% over %d\n", backend,
                describe(p).c_str(), worst, share * 100.0, kTypicalTolerance);
    EXPECT(worst <= kWorstTolerance);
    EXPECT(share <= kMaxShareOverTypical);
  }
}

// Whole edits: every filter, geometry, curves and HSL. Grain is left out of the geometry
// cases: it is keyed to the source pixel, and a sample that lands on a pixel
// boundary may round to different neighbours on the GPU and the CPU.
std::vector<sp_edit> edit_cases() {
  std::vector<sp_edit> cases;
  for (int i = 0; i < sp_filter_count(); ++i) {
    sp_edit e{};
    std::snprintf(e.filter, sizeof(e.filter), "%s", sp_filter_id(i));
    e.filter_strength = i % 2 ? 1.0f : 0.6f;
    cases.push_back(e);
  }
  sp_edit e{};
  e.geometry.quarter_turns = 1;
  cases.push_back(e);
  e = {};
  e.geometry.quarter_turns = 3;
  e.geometry.flip_horizontal = 1;
  cases.push_back(e);
  e = {};
  e.geometry.crop_x = 0.25f;
  e.geometry.crop_y = 0.1f;
  e.geometry.crop_width = 0.5f;
  e.geometry.crop_height = 0.6f;
  cases.push_back(e);
  e = {};
  e.geometry.straighten = 8.0f;
  cases.push_back(e);
  e = {};
  e.geometry.vertical = 0.6f;
  e.geometry.horizontal = -0.4f;
  cases.push_back(e);
  e = {};
  std::snprintf(e.filter, sizeof(e.filter), "film");
  e.filter_strength = 0.8f;
  e.adjust.exposure = 0.3f;
  e.adjust.clarity = 0.5f;
  e.adjust.vignette = 0.4f;
  e.geometry.quarter_turns = 1;
  e.geometry.straighten = -5.0f;
  e.geometry.crop_x = 0.1f;
  e.geometry.crop_width = 0.8f;
  cases.push_back(e);
  // Curves and HSL, alone and with a filter and roll-off.
  e = {};
  e.curves[0] = {3, {0.0f, 0.3f, 1.0f}, {0.0f, 0.22f, 1.0f}};
  e.curves[3] = {3, {0.0f, 0.5f, 1.0f}, {0.08f, 0.55f, 0.92f}};
  e.hsl.hue[0] = 0.6f;
  e.hsl.saturation[5] = 0.8f;
  e.hsl.luminance[3] = -0.7f;
  e.hsl.saturation[2] = -1.0f;
  cases.push_back(e);
  std::snprintf(e.filter, sizeof(e.filter), "teal_orange");
  e.filter_strength = 0.7f;
  e.adjust.exposure = 0.8f;
  e.adjust.contrast = 0.3f;
  cases.push_back(e);
  return cases;
}

std::string describe(const sp_edit& e) {
  std::string text = describe(e.adjust);
  if (text == "untouched") text.clear();
  char item[160];
  if (e.filter[0]) {
    std::snprintf(item, sizeof(item), " filter=%s@%.1f", e.filter, e.filter_strength);
    text += item;
  }
  for (int i = 0; i < 4; ++i) {
    if (e.curves[i].count < 2) continue;
    std::snprintf(item, sizeof(item), " curve%d=%d points", i, e.curves[i].count);
    text += item;
  }
  for (int b = 0; b < 8; ++b) {
    if (e.hsl.hue[b] == 0 && e.hsl.saturation[b] == 0 && e.hsl.luminance[b] == 0) continue;
    std::snprintf(item, sizeof(item), " hsl%d=%.1f,%.1f,%.1f", b, e.hsl.hue[b],
                  e.hsl.saturation[b], e.hsl.luminance[b]);
    text += item;
  }
  const sp_geometry& g = e.geometry;
  std::snprintf(item, sizeof(item),
                " turns=%d flip=%d straighten=%.0f persp=%.1f,%.1f crop=%.2f,%.2f,%.2f,%.2f",
                g.quarter_turns, g.flip_horizontal, g.straighten, g.vertical,
                g.horizontal, g.crop_x, g.crop_y, g.crop_width, g.crop_height);
  return text + item;
}

// Geometry resamples between pixels, where GPUs interpolate with a few bits
// of sub-pixel precision; edges in the test photo then differ a little more.
void compare_edits_with_cpu(sp_renderer* renderer, const char* backend) {
  std::vector<uint8_t> src(kW * kH * 4);
  sp_make_test_image(src.data(), kW, kH);
  EXPECT(sp_renderer_set_image(renderer, src.data(), kW, kH) == SP_OK);
  for (const sp_edit& e : edit_cases()) {
    int w = 0, h = 0;
    EXPECT(sp_geometry_output_size(&e.geometry, kW, kH, &w, &h) == SP_OK);
    std::vector<uint8_t> cpu(static_cast<size_t>(w) * h * 4), gpu(cpu.size());
    EXPECT(sp_render_cpu(&e, src.data(), kW, kH, cpu.data(), w, h) == SP_OK);
    EXPECT(sp_renderer_set_edit(renderer, &e) == SP_OK);
    int rw = 0, rh = 0;
    EXPECT(sp_renderer_output_size(renderer, &rw, &rh) == SP_OK);
    EXPECT(rw == w && rh == h);
    EXPECT(sp_renderer_render_rgba(renderer, gpu.data(), w, h) == SP_OK);
    int worst = 0;
    size_t over_typical = 0;
    for (size_t i = 0; i < cpu.size(); ++i) {
      const int diff = std::abs(cpu[i] - gpu[i]);
      worst = std::max(worst, diff);
      if (diff > kTypicalTolerance) ++over_typical;
    }
    const double share = static_cast<double>(over_typical) / cpu.size();
    std::printf("  %s %dx%d%s: max diff %d, %.3f%% over %d\n", backend, w, h,
                describe(e).c_str(), worst, share * 100.0, kTypicalTolerance);
    EXPECT(worst <= kGeometryWorstTolerance);
    EXPECT(share <= kMaxShareOverTypical);
  }

  // The filter strength is baked into the colour table, so changing only
  // the strength must still reach the GPU.
  sp_edit e{};
  std::snprintf(e.filter, sizeof(e.filter), "film");
  e.filter_strength = 1.0f;
  EXPECT(sp_renderer_set_edit(renderer, &e) == SP_OK);
  EXPECT(sp_renderer_set_filter(renderer, "film", 0.3f) == SP_OK);
  e.filter_strength = 0.3f;
  std::vector<uint8_t> cpu(src.size()), gpu(src.size());
  EXPECT(sp_render_cpu(&e, src.data(), kW, kH, cpu.data(), kW, kH) == SP_OK);
  EXPECT(sp_renderer_render_rgba(renderer, gpu.data(), kW, kH) == SP_OK);
  int worst = 0;
  for (size_t i = 0; i < cpu.size(); ++i) worst = std::max(worst, std::abs(cpu[i] - gpu[i]));
  std::printf("  %s film@1 then strength 0.3: max diff %d\n", backend, worst);
  EXPECT(worst <= kWorstTolerance);

  sp_edit none{};
  sp_renderer_set_edit(renderer, &none);
}

// Low-precision source reads are magnified when saturation nearly cancels
// a dark channel at the RGB boundary. Do not hide this behind the broader
// existing pow() tolerance. These cyan/blue values reproduce the GLES case
// that differed by 7 levels before the image sampler was explicitly highp.
void compare_colour_boundaries(sp_renderer* renderer, const char* backend) {
  constexpr int w = 64, h = 32;
  const uint8_t colours[][3] = {
      {129, 246, 255}, {85, 249, 255}, {100, 191, 255}, {75, 188, 255},
      {255, 128, 32}, {255, 254, 253}, {254, 255, 253}, {0, 0, 0}};
  std::vector<uint8_t> src(w * h * 4), cpu(src.size()), gpu(src.size());
  for (int y = 0; y < h; ++y) for (int x = 0; x < w; ++x) {
    const int i = (y * w + x) * 4;
    const auto& c = colours[x / 8];
    for (int ch = 0; ch < 3; ++ch) src[i + ch] = c[ch];
    src[i + 3] = 255;
  }
  EXPECT(sp_renderer_set_image(renderer, src.data(), w, h) == SP_OK);
  sp_edit edit{};
  edit.adjust.exposure = 1.0f;
  edit.adjust.contrast = 0.6f;
  edit.adjust.saturation = 1.0f;
  edit.adjust.temperature = 0.7f;
  edit.adjust.tint = -0.4f;
  edit.adjust.highlights = -1.0f;
  edit.adjust.shadows = 0.8f;
  EXPECT(sp_render_cpu(&edit, src.data(), w, h, cpu.data(), w, h) == SP_OK);
  EXPECT(sp_renderer_set_edit(renderer, &edit) == SP_OK);
  EXPECT(sp_renderer_render_rgba(renderer, gpu.data(), w, h) == SP_OK);
  int worst = 0;
  for (size_t i = 0; i < cpu.size(); ++i) worst = std::max(worst, std::abs(cpu[i] - gpu[i]));
  std::printf("  %s colour-boundary patches: max diff %d\n", backend, worst);
  EXPECT(worst <= 2);
}

}  // namespace

#if SPHOTO_TEST_GLES
TEST(gles_matches_cpu_reference) {
  sp_egl_context* ctx = sp_egl_context_create();
  if (!ctx) throw sptest::Skip{"no EGL / OpenGL ES 3 context available"};
  sp_renderer* renderer = sp_gles_renderer_create();
  EXPECT(renderer != nullptr);
  if (renderer) {
    compare_with_cpu(renderer, "gles");
    compare_edits_with_cpu(renderer, "gles");
    compare_colour_boundaries(renderer, "gles");
    sp_renderer_destroy(renderer);
  }
  sp_egl_context_destroy(ctx);
}
#endif

#if SPHOTO_TEST_METAL
TEST(metal_matches_cpu_reference) {
  sp_renderer* renderer = sp_metal_renderer_create(nullptr);
  if (!renderer) throw sptest::Skip{"no Metal device available"};
  compare_with_cpu(renderer, "metal");
  compare_edits_with_cpu(renderer, "metal");
  compare_colour_boundaries(renderer, "metal");
  sp_renderer_destroy(renderer);
}
#endif
