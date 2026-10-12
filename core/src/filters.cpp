// The first built-in filters. Each look is a few simple, monotonic moves on
// sRGB-encoded colour (tone curve, fade, split toning, saturation), so they
// stay gentle at full strength and blend cleanly at partial strength.
#include "filters.h"

#include <algorithm>
#include <cstring>

namespace sp {
namespace {

struct Rgb {
  float r, g, b;
};

float luma(Rgb c) { return 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b; }

// An S-curve through 0, 0.5 and 1; monotonic for 0 <= k <= 1.
float s_curve(float x, float k) { return x + k * x * (1.0f - x) * (2.0f * x - 1.0f); }

Rgb curve(Rgb c, float k) { return {s_curve(c.r, k), s_curve(c.g, k), s_curve(c.b, k)}; }

Rgb saturate(Rgb c, float amount) {
  const float l = luma(c);
  return {l + (c.r - l) * (1 + amount), l + (c.g - l) * (1 + amount),
          l + (c.b - l) * (1 + amount)};
}

// Lifts black to `black` and keeps white.
Rgb fade(Rgb c, float black) {
  return {black + c.r * (1 - black), black + c.g * (1 - black), black + c.b * (1 - black)};
}

// Adds `shadow` in the darks and `light` in the brights.
Rgb split(Rgb c, Rgb shadow, Rgb light) {
  const float l = std::clamp(luma(c), 0.0f, 1.0f);
  const float d = (1 - l) * (1 - l), b = l * l;
  return {c.r + shadow.r * d + light.r * b, c.g + shadow.g * d + light.g * b,
          c.b + shadow.b * d + light.b * b};
}

Rgb mono(Rgb c) {
  // Weighted towards red and green, like panchromatic film.
  const float l = 0.30f * c.r + 0.59f * c.g + 0.11f * c.b;
  return {l, l, l};
}

Rgb warm(Rgb c) {
  c = split(c, {0.03f, 0.01f, -0.02f}, {0.04f, 0.015f, -0.05f});
  return saturate(curve(c, 0.25f), 0.1f);
}

Rgb film(Rgb c) {
  c = curve(c, 0.4f);
  c = split(c, {-0.01f, 0.02f, 0.015f}, {0.03f, 0.01f, -0.03f});
  return saturate(fade(c, 0.07f), -0.12f);
}

Rgb cool(Rgb c) {
  c = split(c, {-0.01f, 0.01f, 0.05f}, {-0.02f, 0.0f, 0.03f});
  return saturate(curve(c, 0.2f), -0.15f);
}

Rgb teal_orange(Rgb c) {
  c = split(c, {-0.03f, 0.03f, 0.05f}, {0.06f, 0.02f, -0.05f});
  return saturate(curve(c, 0.3f), 0.1f);
}

Rgb mono_contrast(Rgb c) { return curve(mono(c), 0.6f); }

Rgb mono_soft(Rgb c) { return fade(curve(mono(c), 0.2f), 0.1f); }

struct Filter {
  const char* id;
  Rgb (*look)(Rgb);
};

constexpr Filter kFilters[] = {
    {"warm", warm},   {"film", film},           {"cool", cool},
    {"teal_orange", teal_orange}, {"mono", mono_contrast}, {"mono_soft", mono_soft},
};
constexpr int kFilterCount = sizeof(kFilters) / sizeof(kFilters[0]);

const Filter* find(const char* id) {
  if (!id) return nullptr;
  for (const Filter& f : kFilters) {
    if (std::strcmp(f.id, id) == 0) return &f;
  }
  return nullptr;
}

}  // namespace

int filter_count() { return kFilterCount; }

const char* filter_id(int index) {
  return index >= 0 && index < kFilterCount ? kFilters[index].id : nullptr;
}

bool is_filter(const char* id) { return find(id) != nullptr; }

int filter_index(const char* id) {
  const Filter* filter = find(id);
  return filter ? static_cast<int>(filter - kFilters) : -1;
}

void apply_filter(int index, float rgb[3]) {
  const Rgb c = kFilters[index].look({rgb[0], rgb[1], rgb[2]});
  rgb[0] = std::clamp(c.r, 0.0f, 1.0f);
  rgb[1] = std::clamp(c.g, 0.0f, 1.0f);
  rgb[2] = std::clamp(c.b, 0.0f, 1.0f);
}

}  // namespace sp
