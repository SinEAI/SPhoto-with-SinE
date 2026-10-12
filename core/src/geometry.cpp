// Geometry: maps output texture coordinates to source texture coordinates
// with one homography, so cropping, turning, flipping, straightening and
// perspective all cost the same single lookup per pixel.
#include <algorithm>
#include <cmath>

#include "frame.h"
#include "params.h"

namespace sp {
namespace {

struct Mat3 {
  double m[9];

  static Mat3 identity() { return {{1, 0, 0, 0, 1, 0, 0, 0, 1}}; }

  Mat3 operator*(const Mat3& b) const {
    Mat3 r{};
    for (int i = 0; i < 3; ++i) {
      for (int j = 0; j < 3; ++j) {
        double sum = 0.0;
        for (int k = 0; k < 3; ++k) sum += m[i * 3 + k] * b.m[k * 3 + j];
        r.m[i * 3 + j] = sum;
      }
    }
    return r;
  }

  Mat3 inverse() const {
    const double* a = m;
    const double c00 = a[4] * a[8] - a[5] * a[7];
    const double c01 = a[5] * a[6] - a[3] * a[8];
    const double c02 = a[3] * a[7] - a[4] * a[6];
    const double det = a[0] * c00 + a[1] * c01 + a[2] * c02;
    const double k = 1.0 / det;
    return {{c00 * k, (a[2] * a[7] - a[1] * a[8]) * k, (a[1] * a[5] - a[2] * a[4]) * k,
             c01 * k, (a[0] * a[8] - a[2] * a[6]) * k, (a[2] * a[3] - a[0] * a[5]) * k,
             c02 * k, (a[1] * a[6] - a[0] * a[7]) * k, (a[0] * a[4] - a[1] * a[3]) * k}};
  }

  void apply(double x, double y, double* ox, double* oy) const {
    const double w = m[6] * x + m[7] * y + m[8];
    *ox = (m[0] * x + m[1] * y + m[2]) / w;
    *oy = (m[3] * x + m[4] * y + m[5]) / w;
  }
};

// Strength of full perspective correction: the near edge grows by up to
// this share of the half-size.
constexpr double kPerspective = 0.3;

}  // namespace

Geometry solve_geometry(const sp_geometry& g, int src_width, int src_height) {
  const double sw = src_width, sh = src_height;

  // Forward map F, in pixel coordinates centred on the source.
  Mat3 f = Mat3::identity();
  if (g.flip_horizontal) f = Mat3{{-1, 0, 0, 0, 1, 0, 0, 0, 1}} * f;
  const int turns = ((g.quarter_turns % 4) + 4) % 4;
  for (int i = 0; i < turns; ++i) {
    f = Mat3{{0, -1, 0, 1, 0, 0, 0, 0, 1}} * f;  // y points down: clockwise
  }
  const double tw = turns % 2 ? sh : sw;  // turned size
  const double th = turns % 2 ? sw : sh;
  const double half = std::max(tw, th) / 2.0;
  const double h = std::clamp(static_cast<double>(g.horizontal), -1.0, 1.0);
  const double v = std::clamp(static_cast<double>(g.vertical), -1.0, 1.0);
  if (h != 0.0 || v != 0.0) {
    f = Mat3{{1, 0, 0, 0, 1, 0, h * kPerspective / half, v * kPerspective / half, 1}} * f;
  }
  const double angle =
      std::clamp(static_cast<double>(g.straighten), -45.0, 45.0) * 3.14159265358979323846 / 180.0;
  if (angle != 0.0) {
    const double c = std::cos(angle), s = std::sin(angle);
    f = Mat3{{c, -s, 0, s, c, 0, 0, 0, 1}} * f;
  }

  // The frame: the largest centred rectangle of the turned aspect ratio
  // inside the transformed source.
  double quad[4][2];
  const double corners[4][2] = {{-sw / 2, -sh / 2}, {sw / 2, -sh / 2},
                                {sw / 2, sh / 2}, {-sw / 2, sh / 2}};
  for (int i = 0; i < 4; ++i) f.apply(corners[i][0], corners[i][1], &quad[i][0], &quad[i][1]);
  double area = 0.0;
  for (int i = 0; i < 4; ++i) {
    const int j = (i + 1) % 4;
    area += quad[i][0] * quad[j][1] - quad[j][0] * quad[i][1];
  }
  const double orientation = area < 0 ? -1.0 : 1.0;
  const auto inside = [&](double scale) {
    const double rx = scale * tw / 2, ry = scale * th / 2;
    const double rect[4][2] = {{-rx, -ry}, {rx, -ry}, {rx, ry}, {-rx, ry}};
    for (const auto& p : rect) {
      for (int i = 0; i < 4; ++i) {
        const int j = (i + 1) % 4;
        const double cross = (quad[j][0] - quad[i][0]) * (p[1] - quad[i][1]) -
                             (quad[j][1] - quad[i][1]) * (p[0] - quad[i][0]);
        if (cross * orientation < -1e-6 * half * half) return false;
      }
    }
    return true;
  };
  double scale = 1.0;
  if (!inside(1.0)) {
    double lo = 0.0, hi = 1.0;
    for (int i = 0; i < 60; ++i) {
      const double mid = (lo + hi) / 2;
      (inside(mid) ? lo : hi) = mid;
    }
    scale = lo;
  }
  const double fw = scale * tw, fh = scale * th;

  // The crop, in frame units.
  double cw = g.crop_width > 0 ? std::min(1.0, static_cast<double>(g.crop_width)) : 1.0;
  double ch = g.crop_height > 0 ? std::min(1.0, static_cast<double>(g.crop_height)) : 1.0;
  const double cx = std::clamp(static_cast<double>(g.crop_x), 0.0, 1.0 - cw);
  const double cy = std::clamp(static_cast<double>(g.crop_y), 0.0, 1.0 - ch);

  // Output uv -> frame (centred pixels) -> source (centred) -> source uv.
  const Mat3 out_to_frame{{cw * fw, 0, (cx - 0.5) * fw, 0, ch * fh, (cy - 0.5) * fh, 0, 0, 1}};
  const Mat3 to_uv{{1 / sw, 0, 0.5, 0, 1 / sh, 0.5, 0, 0, 1}};
  const Mat3 m = to_uv * (f.inverse() * out_to_frame);

  Geometry result{};
  std::copy(m.m, m.m + 9, result.to_source);
  result.width = cw * fw;
  result.height = ch * fh;
  result.out_width = std::max(1, static_cast<int>(std::lround(result.width)));
  result.out_height = std::max(1, static_cast<int>(std::lround(result.height)));
  return result;
}

Frame make_frame(const Geometry& geometry, int src_width, int src_height,
                 int out_width, int out_height, float lut_strength) {
  Frame f{};
  float* h = &f.h00;
  for (int i = 0; i < 9; ++i) h[i] = static_cast<float>(geometry.to_source[i]);
  // Source pixels per output pixel; the same both ways up to rounding.
  const double scale = geometry.width / out_width;
  f.px_x = static_cast<float>(scale / src_width);
  f.px_y = static_cast<float>(scale / src_height);
  f.src_w = static_cast<float>(src_width);
  f.src_h = static_cast<float>(src_height);
  f.aspect = static_cast<float>(geometry.width / geometry.height);
  f.radius = static_cast<float>(clarity_radius(out_width, out_height));
  f.lut_strength = lut_strength;
  return f;
}

}  // namespace sp
