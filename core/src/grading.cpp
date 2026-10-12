// Colour grading: tone curves, HSL and the filter, baked into one table.
#include "grading.h"

#include <algorithm>
#include <cmath>

#include "filters.h"

namespace sp {
namespace {

// v limited to 0 .. 1, and an HSL amount to -1 .. 1; NaN reads as 0.
float unit(float v) { return v > 0.0f ? (v < 1.0f ? v : 1.0f) : 0.0f; }
float amount(float v) {
  if (v > 0.0f) return v < 1.0f ? v : 1.0f;
  return v < 0.0f ? (v > -1.0f ? v : -1.0f) : 0.0f;
}

// A curve's usable points and the tangents of its monotone cubic.
struct Spline {
  int n = 0;
  float x[8], y[8], m[8];
};

// Fritsch-Carlson: tangents from the mean of the neighbouring secants (0 at
// a peak or a dip), then shortened where they would make a segment
// overshoot, so the curve is monotonic wherever its points are.
Spline make_spline(const sp_curve& curve) {
  Spline s;
  const int count = std::clamp(curve.count, 0, 8);
  for (int i = 0; i < count; ++i) {
    const float x = unit(curve.x[i]);
    if (s.n > 0 && !(x > s.x[s.n - 1])) continue;
    s.x[s.n] = x;
    s.y[s.n] = unit(curve.y[i]);
    ++s.n;
  }
  if (s.n < 2) return s;
  float d[7];
  for (int k = 0; k + 1 < s.n; ++k) {
    d[k] = (s.y[k + 1] - s.y[k]) / (s.x[k + 1] - s.x[k]);
  }
  s.m[0] = d[0];
  s.m[s.n - 1] = d[s.n - 2];
  for (int k = 1; k + 1 < s.n; ++k) {
    s.m[k] = d[k - 1] * d[k] > 0.0f ? 0.5f * (d[k - 1] + d[k]) : 0.0f;
  }
  for (int k = 0; k + 1 < s.n; ++k) {
    if (d[k] == 0.0f) {
      s.m[k] = s.m[k + 1] = 0.0f;
      continue;
    }
    const float a = s.m[k] / d[k], b = s.m[k + 1] / d[k];
    const float r = a * a + b * b;
    if (r > 9.0f) {
      const float t = 3.0f / std::sqrt(r);
      s.m[k] = t * a * d[k];
      s.m[k + 1] = t * b * d[k];
    }
  }
  return s;
}

// The cubic Hermite segment through the points around x; flat outside.
float eval(const Spline& s, float x) {
  if (x <= s.x[0]) return s.y[0];
  if (x >= s.x[s.n - 1]) return s.y[s.n - 1];
  int k = 0;
  while (x >= s.x[k + 1]) ++k;
  const float h = s.x[k + 1] - s.x[k];
  const float t = (x - s.x[k]) / h, t2 = t * t, t3 = t2 * t;
  return (2.0f * t3 - 3.0f * t2 + 1.0f) * s.y[k] + (t3 - 2.0f * t2 + t) * h * s.m[k] +
         (3.0f * t2 - 2.0f * t3) * s.y[k + 1] + (t3 - t2) * h * s.m[k + 1];
}

// HSL works in a hexcone model on encoded values: a colour is
// luma + chroma * (pure(hue) - luma(pure(hue))), where chroma is max - min
// and pure(hue) the fully saturated colour of that hue (max 1, min 0). That
// is exact for any colour, so each control moves one coordinate:
//   hue        hue + 30 degrees * amount
//   saturation chroma * (1 + amount)
//   luminance  luma + 0.3 * amount
// and the result is brought back into gamut by lowering chroma only, which
// keeps both hue and lightness. Every amount is the band mix scaled by a
// chroma weight that is 0 up to kGreyChroma: greys, near-greys whose hue is
// noise, and the table points next to the grey axis stay exactly as they
// are, so greys keep their values even between table points.
constexpr float kBandCentres[9] = {0.0f, 30.0f, 60.0f, 120.0f, 180.0f,
                                   240.0f, 270.0f, 300.0f, 360.0f};
constexpr float kGreyChroma = 0.04f;  // above one table step, 1 / 32
constexpr float kFullChroma = 0.25f;

float luma(const float c[3]) { return 0.2126f * c[0] + 0.7152f * c[1] + 0.0722f * c[2]; }

// The fully saturated colour of hue h, in degrees.
void pure(float h, float c[3]) {
  for (int i = 0; i < 3; ++i) {
    const float k = std::fmod(5.0f - 2.0f * i + h / 60.0f, 6.0f);
    c[i] = 1.0f - std::max(0.0f, std::min({k, 4.0f - k, 1.0f}));
  }
}

void apply_hsl(const sp_hsl& hsl, float c[3]) {
  const float hi = std::max({c[0], c[1], c[2]}), lo = std::min({c[0], c[1], c[2]});
  const float chroma = hi - lo;
  const float t =
      std::clamp((chroma - kGreyChroma) / (kFullChroma - kGreyChroma), 0.0f, 1.0f);
  const float weight = t * t * (3.0f - 2.0f * t);
  if (weight <= 0.0f) return;

  float h;
  if (hi == c[0]) {
    h = (c[1] - c[2]) / chroma;
    if (h < 0.0f) h += 6.0f;
  } else if (hi == c[1]) {
    h = (c[2] - c[0]) / chroma + 2.0f;
  } else {
    h = (c[0] - c[1]) / chroma + 4.0f;
  }
  h *= 60.0f;

  // The two neighbouring bands share the hue with a smoothstep between
  // their centres; the weights always sum to 1.
  int band = 0;
  while (band < 7 && h >= kBandCentres[band + 1]) ++band;
  const float u = std::clamp(
      (h - kBandCentres[band]) / (kBandCentres[band + 1] - kBandCentres[band]), 0.0f, 1.0f);
  const float s = u * u * (3.0f - 2.0f * u);
  const int next = (band + 1) % 8;
  const auto mix = [&](const float* amounts) {
    return weight * ((1.0f - s) * amount(amounts[band]) + s * amount(amounts[next]));
  };

  float p[3];
  pure(h + 30.0f * mix(hsl.hue), p);
  const float lp = luma(p);
  const float y = unit(luma(c) + 0.3f * mix(hsl.luminance));
  float k = chroma * (1.0f + mix(hsl.saturation));
  for (int i = 0; i < 3; ++i) {
    const float d = p[i] - lp;
    if (d > 0.0f) k = std::min(k, (1.0f - y) / d);
    if (d < 0.0f) k = std::min(k, y / -d);
  }
  for (int i = 0; i < 3; ++i) c[i] = unit(y + k * (p[i] - lp));
}

}  // namespace

bool curve_is_identity(const sp_curve& curve) {
  const Spline s = make_spline(curve);
  if (s.n < 2) return true;
  if (s.x[0] != 0.0f || s.x[s.n - 1] != 1.0f) return false;
  for (int i = 0; i < s.n; ++i) {
    if (s.y[i] != s.x[i]) return false;
  }
  return true;
}

bool hsl_is_identity(const sp_hsl& hsl) {
  for (int i = 0; i < 8; ++i) {
    if (amount(hsl.hue[i]) != 0.0f || amount(hsl.saturation[i]) != 0.0f ||
        amount(hsl.luminance[i]) != 0.0f) {
      return false;
    }
  }
  return true;
}

bool bake_grading(const char* filter, float strength, const sp_curve curves[4],
                  const sp_hsl& hsl, std::vector<float>* rgb) {
  rgb->clear();
  int look = -1;
  if (filter && filter[0]) {
    look = filter_index(filter);
    if (look < 0) return false;
  }
  strength = unit(strength);
  if (strength <= 0.0f) look = -1;
  const bool use_hsl = !hsl_is_identity(hsl);
  bool any = look >= 0 || use_hsl;
  Spline splines[4];
  bool use_curve[4];
  for (int i = 0; i < 4; ++i) {
    use_curve[i] = !curve_is_identity(curves[i]);
    if (use_curve[i]) splines[i] = make_spline(curves[i]);
    any = any || use_curve[i];
  }
  if (!any) return true;

  rgb->resize(static_cast<size_t>(kLutSize) * kLutSize * kLutSize * 3);
  float* out = rgb->data();
  float change = 0.0f;
  const float step = 1.0f / (kLutSize - 1);
  for (int b = 0; b < kLutSize; ++b) {
    for (int g = 0; g < kLutSize; ++g) {
      for (int r = 0; r < kLutSize; ++r) {
        const float at[3] = {r * step, g * step, b * step};
        float c[3] = {at[0], at[1], at[2]};
        if (use_hsl) apply_hsl(hsl, c);
        for (int k = 0; k < 3; ++k) {
          if (use_curve[0]) c[k] = eval(splines[0], c[k]);
          if (use_curve[k + 1]) c[k] = eval(splines[k + 1], c[k]);
        }
        if (look >= 0) {
          float f[3] = {c[0], c[1], c[2]};
          apply_filter(look, f);
          for (int k = 0; k < 3; ++k) {
            c[k] = strength >= 1.0f ? f[k] : c[k] + (f[k] - c[k]) * strength;
          }
        }
        for (int k = 0; k < 3; ++k) {
          *out++ = c[k];
          change = std::max(change, std::fabs(c[k] - at[k]));
        }
      }
    }
  }
  // Controls too small to move any table point leave no table, so the photo
  // renders exactly as untouched.
  if (change < 1e-6f) rgb->clear();
  return true;
}

}  // namespace sp
