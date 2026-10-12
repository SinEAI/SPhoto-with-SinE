#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <string>
#include <vector>

#include "sphoto/sphoto.h"
#include "test.h"

namespace {

constexpr int kW = 96;
constexpr int kH = 64;

std::vector<uint8_t> test_image() {
  std::vector<uint8_t> image(kW * kH * 4);
  sp_make_test_image(image.data(), kW, kH);
  return image;
}

std::vector<uint8_t> process(const sp_params& p, const std::vector<uint8_t>& src) {
  std::vector<uint8_t> dst(src.size());
  EXPECT(sp_process_cpu(&p, src.data(), dst.data(), kW, kH) == SP_OK);
  return dst;
}

long sum_rgb(const std::vector<uint8_t>& image) {
  long sum = 0;
  for (size_t i = 0; i < image.size(); i += 4) {
    sum += image[i] + image[i + 1] + image[i + 2];
  }
  return sum;
}

}  // namespace

TEST(default_params_leave_every_value_unchanged) {
  // Every possible 8-bit value must survive the sRGB -> linear -> sRGB trip.
  std::vector<uint8_t> src(256 * 4);
  for (int v = 0; v < 256; ++v) {
    src[v * 4 + 0] = src[v * 4 + 1] = src[v * 4 + 2] = static_cast<uint8_t>(v);
    src[v * 4 + 3] = 255;
  }
  std::vector<uint8_t> dst(src.size());
  sp_params p;
  sp_params_default(&p);
  EXPECT(sp_process_cpu(&p, src.data(), dst.data(), 256, 1) == SP_OK);
  EXPECT(dst == src);
}

TEST(exposure_brightens_and_darkens) {
  const auto src = test_image();
  sp_params p{};
  p.exposure = 1.0f;
  const long brighter = sum_rgb(process(p, src));
  p.exposure = -1.0f;
  const long darker = sum_rgb(process(p, src));
  EXPECT(brighter > sum_rgb(src));
  EXPECT(darker < sum_rgb(src));
}

TEST(one_stop_doubles_linear_mid_grey) {
  // sRGB 118 is about 18% linear grey; +1 stop is about 36%, sRGB 162.
  std::vector<uint8_t> src = {118, 118, 118, 255};
  std::vector<uint8_t> dst(4);
  sp_params p{};
  p.exposure = 1.0f;
  EXPECT(sp_process_cpu(&p, src.data(), dst.data(), 1, 1) == SP_OK);
  EXPECT(std::abs(dst[0] - 162) <= 1);
}

TEST(saturation_minus_one_is_grey) {
  sp_params p{};
  p.saturation = -1.0f;
  const auto dst = process(p, test_image());
  for (size_t i = 0; i < dst.size(); i += 4) {
    EXPECT(dst[i] == dst[i + 1] && dst[i + 1] == dst[i + 2]);
  }
}

TEST(contrast_keeps_mid_grey_and_spreads_the_rest) {
  std::vector<uint8_t> src = {118, 118, 118, 255, 60, 60, 60, 255,
                              200, 200, 200, 255};
  std::vector<uint8_t> dst(src.size());
  sp_params p{};
  p.contrast = 1.0f;
  EXPECT(sp_process_cpu(&p, src.data(), dst.data(), 3, 1) == SP_OK);
  EXPECT(std::abs(dst[0] - 118) <= 1);
  EXPECT(dst[4] < 60);
  EXPECT(dst[8] > 200);
}

TEST(alpha_is_preserved) {
  std::vector<uint8_t> src = {10, 20, 30, 77};
  std::vector<uint8_t> dst(4);
  sp_params p{};
  p.exposure = 2.0f;
  EXPECT(sp_process_cpu(&p, src.data(), dst.data(), 1, 1) == SP_OK);
  EXPECT(dst[3] == 77);
}

TEST(bad_arguments_are_rejected) {
  sp_params p{};
  uint8_t px[4] = {};
  EXPECT(sp_process_cpu(nullptr, px, px, 1, 1) == SP_ERR_ARGUMENT);
  EXPECT(sp_process_cpu(&p, px, px, 0, 1) == SP_ERR_ARGUMENT);
  EXPECT(sp_make_test_image(nullptr, 1, 1) == SP_ERR_ARGUMENT);
}

namespace {

// One opaque pixel of grey v.
std::vector<uint8_t> grey(uint8_t v, int n = 1) {
  std::vector<uint8_t> image;
  for (int i = 0; i < n; ++i) image.insert(image.end(), {v, v, v, 255});
  return image;
}

std::vector<uint8_t> run(const sp_params& p, std::vector<uint8_t> src, int w, int h) {
  std::vector<uint8_t> dst(src.size());
  EXPECT(sp_process_cpu(&p, src.data(), dst.data(), w, h) == SP_OK);
  return dst;
}

}  // namespace

TEST(temperature_warms_and_cools_grey) {
  sp_params p{};
  p.temperature = 1.0f;
  auto warm = run(p, grey(128), 1, 1);
  EXPECT(warm[0] > warm[2]);
  p.temperature = -1.0f;
  auto cool = run(p, grey(128), 1, 1);
  EXPECT(cool[0] < cool[2]);
}

TEST(tint_moves_between_green_and_magenta) {
  sp_params p{};
  p.tint = 1.0f;
  auto magenta = run(p, grey(128), 1, 1);
  EXPECT(magenta[1] < magenta[0]);
  p.tint = -1.0f;
  auto green = run(p, grey(128), 1, 1);
  EXPECT(green[1] > green[0]);
}

TEST(shadows_lift_darks_more_than_lights) {
  sp_params p{};
  p.shadows = 1.0f;
  auto out = run(p, {40, 40, 40, 255, 220, 220, 220, 255}, 2, 1);
  EXPECT(out[0] - 40 > out[4] - 220);
  EXPECT(out[0] > 40);
}

TEST(negative_highlights_recover_brights) {
  sp_params p{};
  p.highlights = -1.0f;
  auto out = run(p, {40, 40, 40, 255, 230, 230, 230, 255}, 2, 1);
  EXPECT(out[4] < 230);
  EXPECT(std::abs(out[0] - 40) < 230 - out[4]);
}

TEST(fade_lifts_black_and_keeps_white) {
  sp_params p{};
  p.fade = 1.0f;
  auto out = run(p, {0, 0, 0, 255, 255, 255, 255, 255}, 2, 1);
  EXPECT(std::abs(out[0] - 38) <= 1);  // 15% of 255
  EXPECT(out[4] == 255);
}

TEST(vignette_darkens_corners_not_centre) {
  sp_params p{};
  p.vignette = 1.0f;
  const int w = 41, h = 41;
  auto out = run(p, grey(150, w * h), w, h);
  const auto at = [&](int x, int y) { return out[(y * w + x) * 4]; };
  EXPECT(at(20, 20) == 150);
  EXPECT(at(0, 0) < 80);
}

TEST(grain_is_deterministic_and_unbiased) {
  sp_params p{};
  p.grain = 1.0f;
  const int w = 64, h = 64;
  auto a = run(p, grey(128, w * h), w, h);
  auto b = run(p, grey(128, w * h), w, h);
  EXPECT(a == b);
  long sum = 0;
  int changed = 0;
  for (size_t i = 0; i < a.size(); i += 4) {
    sum += a[i] - 128;
    changed += a[i] != 128;
  }
  EXPECT(changed > w * h / 2);
  EXPECT(std::abs(sum) / (w * h) <= 1);
}

TEST(sharpen_steepens_an_edge) {
  // A vertical edge: 4 columns of 80 then 4 of 170.
  const int w = 8, h = 3;
  std::vector<uint8_t> src;
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      const uint8_t v = x < 4 ? 80 : 170;
      src.insert(src.end(), {v, v, v, 255});
    }
  }
  sp_params p{};
  p.sharpen = 1.0f;
  auto out = run(p, src, w, h);
  const auto at = [&](int x) { return out[(1 * w + x) * 4]; };
  EXPECT(at(3) < 80);   // dark side of the edge gets darker
  EXPECT(at(4) > 170);  // bright side gets brighter
  EXPECT(at(0) == 80);  // flat areas are untouched
}

TEST(flat_image_ignores_sharpen_and_clarity) {
  sp_params p{};
  p.sharpen = 1.0f;
  p.clarity = 1.0f;
  EXPECT(run(p, grey(100, 64), 8, 8) == grey(100, 64));
}

TEST(params_are_reachable_by_index) {
  EXPECT(sp_param_count() == 12);
  const char* name = nullptr;
  float lo = 0, hi = 0;
  EXPECT(sp_param_info(0, &name, &lo, &hi) == SP_OK);
  EXPECT(std::string(name) == "exposure" && lo == -4.0f && hi == 4.0f);
  EXPECT(sp_param_info(sp_param_count(), &name, &lo, &hi) == SP_ERR_ARGUMENT);
  sp_params p{};
  sp_params_set(&p, 11, 0.5f);
  EXPECT(p.grain == 0.5f && sp_params_get(&p, 11) == 0.5f);
}

namespace {

// A w x h image where every pixel is unique: r = x, g = y.
std::vector<uint8_t> coordinates(int w, int h) {
  std::vector<uint8_t> image;
  for (int y = 0; y < h; ++y) {
    for (int x = 0; x < w; ++x) {
      image.insert(image.end(), {static_cast<uint8_t>(x * 10), static_cast<uint8_t>(y * 10), 0, 255});
    }
  }
  return image;
}

std::vector<uint8_t> render(const sp_edit& e, const std::vector<uint8_t>& src, int w, int h,
                            int* ow, int* oh) {
  EXPECT(sp_geometry_output_size(&e.geometry, w, h, ow, oh) == SP_OK);
  std::vector<uint8_t> dst(static_cast<size_t>(*ow) * *oh * 4);
  EXPECT(sp_render_cpu(&e, src.data(), w, h, dst.data(), *ow, *oh) == SP_OK);
  return dst;
}

}  // namespace

TEST(quarter_turn_rotates_clockwise) {
  const int w = 6, h = 4;
  sp_edit e{};
  e.geometry.quarter_turns = 1;
  int ow, oh;
  auto out = render(e, coordinates(w, h), w, h, &ow, &oh);
  EXPECT(ow == h && oh == w);
  // Clockwise: the output's top-left is the source's bottom-left.
  EXPECT(out[0] == 0 && out[1] == (h - 1) * 10);
  // And the output's top-right is the source's top-left.
  const size_t tr = static_cast<size_t>(ow - 1) * 4;
  EXPECT(out[tr] == 0 && out[tr + 1] == 0);
}

TEST(flip_mirrors_left_right) {
  const int w = 5, h = 3;
  sp_edit e{};
  e.geometry.flip_horizontal = 1;
  int ow, oh;
  auto out = render(e, coordinates(w, h), w, h, &ow, &oh);
  EXPECT(ow == w && oh == h);
  EXPECT(out[0] == (w - 1) * 10 && out[1] == 0);
}

TEST(crop_takes_the_pixels_inside) {
  const int w = 10, h = 10;
  sp_edit e{};
  e.geometry.crop_x = 0.2f;
  e.geometry.crop_y = 0.5f;
  e.geometry.crop_width = 0.3f;
  e.geometry.crop_height = 0.4f;
  int ow, oh;
  auto out = render(e, coordinates(w, h), w, h, &ow, &oh);
  EXPECT(ow == 3 && oh == 4);
  EXPECT(out[0] == 20 && out[1] == 50);
}

TEST(straighten_trims_the_empty_corners) {
  sp_geometry g{};
  g.straighten = 10.0f;
  int ow = 0, oh = 0;
  EXPECT(sp_geometry_output_size(&g, 400, 300, &ow, &oh) == SP_OK);
  EXPECT(ow < 400 && oh < 300);
  EXPECT(std::abs(ow * 3 - oh * 4) <= 4);  // keeps 4:3
  // The frame's corners touch the source's edges, so only the outermost
  // output rows may pick up some of a black first and last source row.
  std::vector<uint8_t> src(400 * 300 * 4, 255);
  sp_edit e{};
  e.geometry = g;
  std::vector<uint8_t> dst(static_cast<size_t>(ow) * oh * 4);
  for (int x = 0; x < 400; ++x) {
    for (int c = 0; c < 3; ++c) src[x * 4 + c] = src[(299 * 400 + x) * 4 + c] = 0;
  }
  EXPECT(sp_render_cpu(&e, src.data(), 400, 300, dst.data(), ow, oh) == SP_OK);
  int darkest = 255;
  for (int y = 1; y < oh - 1; ++y) {
    for (int x = 0; x < ow; ++x) darkest = std::min<int>(darkest, dst[(y * ow + x) * 4]);
  }
  EXPECT(darkest == 255);
}

TEST(mono_filter_at_full_strength_is_grey) {
  sp_edit e{};
  std::snprintf(e.filter, sizeof(e.filter), "mono");
  e.filter_strength = 1.0f;
  int ow, oh;
  auto out = render(e, test_image(), kW, kH, &ow, &oh);
  int worst = 0;
  for (size_t i = 0; i < out.size(); i += 4) {
    worst = std::max({worst, std::abs(out[i] - out[i + 1]), std::abs(out[i + 1] - out[i + 2])});
  }
  EXPECT(worst <= 1);
}

TEST(filter_strength_zero_changes_nothing) {
  sp_edit e{};
  std::snprintf(e.filter, sizeof(e.filter), "film");
  int ow, oh;
  EXPECT(render(e, test_image(), kW, kH, &ow, &oh) == test_image());
}

TEST(unknown_filter_is_rejected) {
  sp_edit e{};
  std::snprintf(e.filter, sizeof(e.filter), "no-such-look");
  e.filter_strength = 1.0f;
  auto src = test_image();
  std::vector<uint8_t> dst(src.size());
  EXPECT(sp_render_cpu(&e, src.data(), kW, kH, dst.data(), kW, kH) == SP_ERR_ARGUMENT);
}

namespace {

// An encoded value (0 .. 1) in linear light, and back, in double precision.
double to_linear(double v) {
  return v <= 0.04045 ? v / 12.92 : std::pow((v + 0.055) / 1.055, 2.4);
}
double to_srgb(double v) {
  return v <= 0.0031308 ? v * 12.92 : 1.055 * std::pow(v, 1.0 / 2.4) - 0.055;
}

}  // namespace

TEST(brightened_highlights_roll_off_instead_of_clipping) {
  // A white-to-black ramp in steps of 5. One stop up used to clip every
  // value above sRGB 187 to 255.
  std::vector<uint8_t> src;
  for (int v = 255; v >= 0; v -= 5) {
    const uint8_t b = static_cast<uint8_t>(v);
    src.insert(src.end(), {b, b, b, 255});
  }
  const int n = static_cast<int>(src.size() / 4);
  sp_params p{};
  p.exposure = 1.0f;
  auto out = run(p, src, n, 1);
  EXPECT(out[0] == 255);  // white still reaches white
  EXPECT(out[4] < 255);   // and nothing else does
  for (int i = 1; i < n; ++i) EXPECT(out[i * 4] < out[(i - 1) * 4]);
  EXPECT(out[(n - 1) * 4] == 0);
}

TEST(no_roll_off_while_white_stays_in_range) {
  // Darkening never brings the shoulder in: every grey comes out as the
  // plain linear-light result, except where that is a rounding tie.
  std::vector<uint8_t> src;
  for (int v = 0; v < 256; ++v) {
    const uint8_t b = static_cast<uint8_t>(v);
    src.insert(src.end(), {b, b, b, 255});
  }
  for (float stops : {-1.0f, -0.5f, -0.1f}) {
    sp_params p{};
    p.exposure = stops;
    auto out = run(p, src, 256, 1);
    for (int v = 0; v < 256; ++v) {
      const double want = 255.0 * to_srgb(to_linear(v / 255.0) * std::exp2(stops));
      const double tie = std::abs(want - std::floor(want) - 0.5);
      EXPECT(out[v * 4] == std::lround(want) || tie < 1e-3);
    }
  }
}

TEST(grain_vignette_clarity_zero_are_identity_ops) {
  const auto src = test_image();
  sp_params tone{};
  tone.exposure = 0.4f;
  tone.contrast = 0.25f;
  tone.highlights = -0.3f;
  auto base = process(tone, src);

  tone.grain = 0.0f;
  tone.vignette = 0.0f;
  tone.clarity = 0.0f;
  EXPECT(process(tone, src) == base);

  // Flat patch with grain=0 stays flat (no RGB snow).
  sp_params g0{};
  g0.exposure = 0.5f;
  g0.grain = 0.0f;
  auto flat = run(g0, grey(90, 64), 8, 8);
  for (size_t i = 0; i < flat.size(); i += 4) {
    EXPECT(flat[i] == flat[0] && flat[i + 1] == flat[1] && flat[i + 2] == flat[2]);
  }

  // vignette=0 leaves corners equal to centre on a flat field.
  sp_params v0{};
  v0.vignette = 0.0f;
  const int w = 41, h = 41;
  auto vig = run(v0, grey(150, w * h), w, h);
  EXPECT(vig[0] == 150);
  EXPECT(vig[(20 * w + 20) * 4] == 150);

  // clarity=0 on an edge matches sharpen-only when sharpen is also 0 (flat
  // response); with an edge and clarity=0, equals no-clarity baseline.
  sp_params c0{};
  c0.clarity = 0.0f;
  const int ew = 8, eh = 3;
  std::vector<uint8_t> edge;
  for (int y = 0; y < eh; ++y) {
    for (int x = 0; x < ew; ++x) {
      const uint8_t v = x < 4 ? 80 : 170;
      edge.insert(edge.end(), {v, v, v, 255});
    }
  }
  sp_params none{};
  EXPECT(run(c0, edge, ew, eh) == run(none, edge, ew, eh));
}

TEST(grain_weights_up_with_encoded_luma) {
  // Same grain amount: a bright flat field should move more on average than
  // a near-black field (deep blacks are not noisy RGB snow).
  sp_params p{};
  p.grain = 1.0f;
  const int w = 64, h = 64;
  auto dark = run(p, grey(8, w * h), w, h);
  auto bright = run(p, grey(200, w * h), w, h);
  long dark_dev = 0, bright_dev = 0;
  for (size_t i = 0; i < dark.size(); i += 4) {
    dark_dev += std::abs(dark[i] - 8);
    bright_dev += std::abs(bright[i] - 200);
  }
  EXPECT(bright_dev > dark_dev);
}

TEST(vignette_moderate_is_gentler_than_old_hard_corner) {
  // At vignette=0.5 on grey 150, the corner should darken, but not as hard as
  // the previous smoothstep(0.3,1) full-strength corner (~0.25x). A softer
  // falloff still reaches a similar corner at vignette=1.
  sp_params p{};
  p.vignette = 0.5f;
  const int w = 41, h = 41;
  auto out = run(p, grey(150, w * h), w, h);
  const int corner = out[0];
  const int centre = out[(20 * w + 20) * 4];
  EXPECT(centre == 150);
  EXPECT(corner < 150);
  EXPECT(corner > 70);  // not crushed at moderate amount
  p.vignette = 1.0f;
  auto full = run(p, grey(150, w * h), w, h);
  EXPECT(full[0] < 80);
}
