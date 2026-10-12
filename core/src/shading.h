// Just enough of GLSL in C++ to compile pipeline.inc for the CPU reference:
// vec2 / vec3, the operators and the built-in functions the pipeline uses,
// with GLSL semantics.
#pragma once

#include <algorithm>
#include <cmath>

namespace sp::sl {

using uint = unsigned int;

struct vec2 {
  float x, y;
  vec2() : x(0.0f), y(0.0f) {}
  explicit vec2(float s) : x(s), y(s) {}
  vec2(float x_, float y_) : x(x_), y(y_) {}
};

struct vec3 {
  float x, y, z;
  vec3() : x(0.0f), y(0.0f), z(0.0f) {}
  explicit vec3(float s) : x(s), y(s), z(s) {}
  vec3(float x_, float y_, float z_) : x(x_), y(y_), z(z_) {}
};

inline vec2 operator+(vec2 a, vec2 b) { return {a.x + b.x, a.y + b.y}; }
inline vec2 operator-(vec2 a, vec2 b) { return {a.x - b.x, a.y - b.y}; }
inline vec2 operator*(vec2 a, vec2 b) { return {a.x * b.x, a.y * b.y}; }
inline vec2 operator*(vec2 a, float s) { return {a.x * s, a.y * s}; }
inline vec2 operator*(float s, vec2 a) { return a * s; }

inline vec3 operator+(vec3 a, vec3 b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }
inline vec3 operator-(vec3 a, vec3 b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }
inline vec3 operator*(vec3 a, vec3 b) { return {a.x * b.x, a.y * b.y, a.z * b.z}; }
inline vec3 operator*(vec3 a, float s) { return {a.x * s, a.y * s, a.z * s}; }
inline vec3 operator*(float s, vec3 a) { return a * s; }
inline vec3 operator/(vec3 a, float s) { return {a.x / s, a.y / s, a.z / s}; }

inline float pow(float a, float b) { return std::pow(a, b); }
inline vec3 pow(vec3 a, vec3 b) {
  return {std::pow(a.x, b.x), std::pow(a.y, b.y), std::pow(a.z, b.z)};
}
inline float exp2(float a) { return std::exp2(a); }
inline float floor(float a) { return std::floor(a); }
inline float clamp(float a, float lo, float hi) { return std::min(std::max(a, lo), hi); }
inline vec3 clamp(vec3 a, vec3 lo, vec3 hi) {
  return {clamp(a.x, lo.x, hi.x), clamp(a.y, lo.y, hi.y), clamp(a.z, lo.z, hi.z)};
}
inline float max(float a, float b) { return std::max(a, b); }
inline vec3 max(vec3 a, vec3 b) {
  return {std::max(a.x, b.x), std::max(a.y, b.y), std::max(a.z, b.z)};
}
inline float dot(vec3 a, vec3 b) { return a.x * b.x + a.y * b.y + a.z * b.z; }
inline float length(vec2 a) { return std::sqrt(a.x * a.x + a.y * a.y); }
inline float smoothstep(float e0, float e1, float x) {
  const float t = clamp((x - e0) / (e1 - e0), 0.0f, 1.0f);
  return t * t * (3.0f - 2.0f * t);
}

}  // namespace sp::sl
