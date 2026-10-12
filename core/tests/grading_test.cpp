// Colour grading: curves, HSL and filters, all through the one colour table.
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <cstdlib>
#include <vector>

#include "sphoto/sphoto.h"
#include "test.h"

namespace {

std::vector<uint8_t> render(const sp_edit& e, const std::vector<uint8_t>& src, int w, int h) {
  std::vector<uint8_t> dst(src.size());
  EXPECT(sp_render_cpu(&e, src.data(), w, h, dst.data(), w, h) == SP_OK);
  return dst;
}

std::vector<uint8_t> test_photo(int w, int h) {
  std::vector<uint8_t> image(static_cast<size_t>(w) * h * 4);
  sp_make_test_image(image.data(), w, h);
  return image;
}

// Every 8-bit grey, black to white.
std::vector<uint8_t> grey_ramp() {
  std::vector<uint8_t> image;
  for (int v = 0; v < 256; ++v) {
    const uint8_t b = static_cast<uint8_t>(v);
    image.insert(image.end(), {b, b, b, 255});
  }
  return image;
}

sp_curve curve(std::initializer_list<std::pair<float, float>> points) {
  sp_curve c{};
  for (const auto& [x, y] : points) {
    c.x[c.count] = x;
    c.y[c.count] = y;
    ++c.count;
  }
  return c;
}

}  // namespace

TEST(identity_grading_leaves_the_photo_unchanged) {
  const auto src = test_photo(96, 64);
  sp_edit e{};
  EXPECT(render(e, src, 96, 64) == src);
  // Curves along the diagonal, a single point, and HSL at zero are all the
  // identity: no table, so not even rounding changes.
  e.curves[0] = curve({{0, 0}, {1, 1}});
  e.curves[1] = curve({{0, 0}, {0.25f, 0.25f}, {1, 1}});
  e.curves[2] = curve({{0.5f, 0.9f}});
  std::snprintf(e.filter, sizeof(e.filter), "film");  // at strength 0
  EXPECT(render(e, src, 96, 64) == src);
}

TEST(filter_alone_blends_as_before) {
  // A filter at strength s is the untouched photo mixed with the filter at
  // full strength, as when the strength was applied in the shader; only
  // the rounding of the two 8-bit references differs.
  const auto src = test_photo(96, 64);
  for (int i = 0; i < sp_filter_count(); ++i) {
    sp_edit e{};
    std::snprintf(e.filter, sizeof(e.filter), "%s", sp_filter_id(i));
    e.filter_strength = 1.0f;
    const auto full = render(e, src, 96, 64);
    e.filter_strength = 0.35f;
    const auto part = render(e, src, 96, 64);
    int worst = 0;
    for (size_t k = 0; k < src.size(); ++k) {
      const double want = src[k] + (full[k] - src[k]) * 0.35;
      worst = std::max(worst, static_cast<int>(std::ceil(std::abs(part[k] - want) - 1e-9)));
    }
    EXPECT(worst <= 1);
  }
}

TEST(curve_passes_through_its_points_and_is_monotonic) {
  // Points on exact 8-bit values: 51 is 0.2 and 153 is 0.6.
  sp_edit e{};
  e.curves[0] = curve({{0, 0.05f}, {0.2f, 0.1f}, {0.6f, 0.8f}, {1, 0.95f}});
  auto out = render(e, grey_ramp(), 256, 1);
  const auto at = [&](int v, int c = 0) { return out[v * 4 + c]; };
  EXPECT(std::abs(at(0) - 0.05 * 255) <= 1);
  EXPECT(std::abs(at(51) - 0.1 * 255) <= 1);
  EXPECT(std::abs(at(153) - 0.8 * 255) <= 1);
  EXPECT(std::abs(at(255) - 0.95 * 255) <= 1);
  for (int v = 1; v < 256; ++v) EXPECT(at(v) >= at(v - 1));
  for (int v = 0; v < 256; ++v) EXPECT(at(v, 0) == at(v, 1) && at(v, 1) == at(v, 2));

  // A steep step that a plain cubic spline would overshoot: monotonic
  // interpolation stays between the points.
  e.curves[0] = curve({{0, 0}, {0.4f, 0.1f}, {0.5f, 0.9f}, {1, 1}});
  out = render(e, grey_ramp(), 256, 1);
  for (int v = 1; v < 256; ++v) EXPECT(at(v) >= at(v - 1));
  for (int v = 0; v <= 94; ++v) EXPECT(at(v) <= 26);    // to 0.4: below 0.1
  for (int v = 128; v < 256; ++v) EXPECT(at(v) >= 229);  // from 0.5: above 0.9

  // Flat outside the first and last point (to one level: on Apple
  // silicon the table lookup rounds 0.3 either way across the flat part).
  e.curves[0] = curve({{0.2f, 0.3f}, {0.8f, 0.6f}});
  out = render(e, grey_ramp(), 256, 1);
  EXPECT(std::abs(at(0) - at(40)) <= 1 && std::abs(at(0) - 0.3 * 255) <= 1);
  EXPECT(std::abs(at(255) - at(220)) <= 1 && std::abs(at(255) - 0.6 * 255) <= 1);
}

TEST(channel_curves_act_on_their_own_channel) {
  sp_edit e{};
  e.curves[1] = curve({{0, 0}, {1, 0.5f}});  // red at half
  auto out = render(e, grey_ramp(), 256, 1);
  for (int v = 0; v < 256; ++v) {
    EXPECT(std::abs(out[v * 4] - v * 0.5) <= 1);
    EXPECT(out[v * 4 + 1] == v && out[v * 4 + 2] == v);
  }
  // The rgb curve comes first, then the channel's own.
  e.curves[0] = curve({{0, 1}, {1, 0}});  // invert
  out = render(e, grey_ramp(), 256, 1);
  EXPECT(std::abs(out[0] - 127.5) <= 1 && out[1] == 255);
  EXPECT(out[255 * 4] == 0 && out[255 * 4 + 1] == 0);
}

TEST(hsl_leaves_greys_alone) {
  sp_edit e{};
  for (int b = 0; b < 8; ++b) {
    e.hsl.hue[b] = b % 2 ? 1.0f : -0.7f;
    e.hsl.saturation[b] = b % 3 ? 1.0f : -1.0f;
    e.hsl.luminance[b] = b % 2 ? -1.0f : 1.0f;
  }
  EXPECT(render(e, grey_ramp(), 256, 1) == grey_ramp());
}

TEST(hsl_moves_the_target_band_most) {
  // Red, orange, yellow, green, aqua, blue, purple and magenta at their
  // band centres.
  const std::vector<uint8_t> src = {
      220, 40, 40, 255, 220, 130, 40, 255, 220, 220, 40, 255, 40, 220, 40, 255,
      40, 220, 220, 255, 40, 40, 220, 255, 130, 40, 220, 255, 220, 40, 220, 255};
  const auto change = [&](const std::vector<uint8_t>& out, int i) {
    int sum = 0;
    for (int c = 0; c < 3; ++c) sum += std::abs(out[i * 4 + c] - src[i * 4 + c]);
    return sum;
  };
  for (int band = 0; band < 8; ++band) {
    for (int control = 0; control < 3; ++control) {
      sp_edit e{};
      float* amounts = control == 0   ? e.hsl.hue
                       : control == 1 ? e.hsl.saturation
                                      : e.hsl.luminance;
      amounts[band] = control == 1 ? -1.0f : 1.0f;
      const auto out = render(e, src, 8, 1);
      EXPECT(change(out, band) > 30);
      // A colour at a band centre takes no part in its neighbours; only the
      // table's interpolation between nearby hues lets a trace through.
      for (int other = 0; other < 8; ++other) {
        if (other != band) EXPECT(change(out, other) <= 3);
      }
    }
  }

  // Each control does what it says on red.
  const std::vector<uint8_t> red = {220, 40, 40, 255};
  sp_edit e{};
  e.hsl.hue[0] = 1.0f;  // towards orange
  auto out = render(e, red, 1, 1);
  EXPECT(out[1] > out[2] + 40);
  e = {};
  e.hsl.saturation[0] = -1.0f;  // to grey
  out = render(e, red, 1, 1);
  EXPECT(std::abs(out[0] - out[1]) <= 1 && std::abs(out[1] - out[2]) <= 1);
  e = {};
  e.hsl.luminance[0] = 1.0f;  // lighter, still red
  out = render(e, red, 1, 1);
  EXPECT(out[1] > 40 && out[0] > out[1] + 50 && std::abs(out[1] - out[2]) <= 2);
}
