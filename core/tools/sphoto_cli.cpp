// sphoto-cli: applies a recipe to a photo and exports it, through the same
// core the apps use. Used for testing and batch work.
//
//   sphoto-cli [options] INPUT OUTPUT
//   sphoto-cli --list
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <fstream>
#include <sstream>
#include <string>
#include <vector>

#include "sphoto/sphoto.h"
#include "stb_image.h"
#include "stb_image_write.h"

namespace {

void usage() {
  std::fputs(
      "usage: sphoto-cli [options] INPUT OUTPUT\n"
      "       sphoto-cli --list\n"
      "\n"
      "Applies an edit to INPUT (JPEG, PNG, BMP, TGA, GIF; or test:WxH for\n"
      "the built-in test photo) and writes OUTPUT as .jpg or .png.\n"
      "\n"
      "  -r, --recipe FILE        apply the recipe in FILE\n"
      "  -s, --set NAME=VALUE     set one adjustment, after any recipe\n"
      "  -f, --filter ID          apply a built-in filter (see --list)\n"
      "      --strength S         filter strength 0-1 (default 1)\n"
      "      --rotate DEG         turn clockwise by 90, 180 or 270\n"
      "      --flip               mirror left-right\n"
      "      --straighten DEG     straighten by -45 to 45 degrees\n"
      "      --vertical V         vertical perspective, -1 to 1\n"
      "      --horizontal H       horizontal perspective, -1 to 1\n"
      "      --crop X,Y,W,H       crop, in 0-1 units of the straightened frame\n"
      "      --curve CH:X,Y/...   tone curve through 2-8 points in 0-1, for CH\n"
      "                           rgb, red, green or blue: rgb:0,0/0.3,0.25/1,1\n"
      "      --hsl C.BAND=V       HSL control C (hue, saturation, luminance) of\n"
      "                           a band (red, orange, yellow, green, aqua,\n"
      "                           blue, purple, magenta), -1 to 1: hue.red=0.2\n"
      "  -q, --quality N          JPEG quality 1-100 (default 92)\n"
      "      --save-recipe FILE   also write the final recipe to FILE\n"
      "      --gpu                render with OpenGL ES (EGL) instead of the CPU\n"
      "      --list               list the adjustments and filters\n",
      stderr);
}

bool read_file(const std::string& path, std::string* out) {
  std::ifstream in(path, std::ios::binary);
  if (!in) return false;
  std::ostringstream text;
  text << in.rdbuf();
  *out = text.str();
  return true;
}

bool ends_with(const std::string& s, const char* suffix) {
  const size_t n = std::strlen(suffix);
  if (s.size() < n) return false;
  for (size_t i = 0; i < n; ++i) {
    const char c = s[s.size() - n + i];
    if ((c >= 'A' && c <= 'Z' ? c + 32 : c) != suffix[i]) return false;
  }
  return true;
}

int find_param(const std::string& name) {
  for (int i = 0; i < sp_param_count(); ++i) {
    const char* candidate = nullptr;
    sp_param_info(i, &candidate, nullptr, nullptr);
    if (name == candidate) return i;
  }
  return -1;
}

void list_params() {
  std::printf("%-12s %6s %6s\n", "adjustment", "min", "max");
  for (int i = 0; i < sp_param_count(); ++i) {
    const char* name = nullptr;
    float lo = 0, hi = 0;
    sp_param_info(i, &name, &lo, &hi);
    std::printf("%-12s %6.1f %6.1f\n", name, lo, hi);
  }
  std::printf("\nfilters:");
  for (int i = 0; i < sp_filter_count(); ++i) std::printf(" %s", sp_filter_id(i));
  std::printf("\n");
}

bool parse_float(const char* text, float lo, float hi, float* out) {
  char* end = nullptr;
  const float v = std::strtof(text, &end);
  if (end == text || *end != '\0' || v < lo || v > hi) return false;
  *out = v;
  return true;
}

constexpr const char* kCurveNames[4] = {"rgb", "red", "green", "blue"};
constexpr const char* kBandNames[8] = {"red",  "orange", "yellow", "green",
                                       "aqua", "blue",   "purple", "magenta"};

// "rgb:0,0/0.3,0.25/1,1" into curves[index].
bool parse_curve(const std::string& text, int* index, sp_curve* curve) {
  const size_t colon = text.find(':');
  *index = -1;
  for (int i = 0; i < 4; ++i) {
    if (text.compare(0, colon, kCurveNames[i]) == 0) *index = i;
  }
  if (colon == std::string::npos || *index < 0) return false;
  *curve = sp_curve{};
  std::stringstream points(text.substr(colon + 1));
  std::string point;
  while (std::getline(points, point, '/')) {
    float x, y;
    char rest;
    if (curve->count == 8 ||
        std::sscanf(point.c_str(), "%f,%f%c", &x, &y, &rest) != 2 || x < 0 || x > 1 ||
        y < 0 || y > 1 || (curve->count > 0 && x <= curve->x[curve->count - 1])) {
      return false;
    }
    curve->x[curve->count] = x;
    curve->y[curve->count] = y;
    ++curve->count;
  }
  return curve->count >= 2;
}

// "saturation.blue=0.4" into hsl.
bool parse_hsl(const std::string& text, sp_hsl* hsl) {
  const size_t dot = text.find('.'), eq = text.find('=');
  if (dot == std::string::npos || eq == std::string::npos || eq < dot) return false;
  const std::string control = text.substr(0, dot), band = text.substr(dot + 1, eq - dot - 1);
  float* bands = control == "hue"          ? hsl->hue
                 : control == "saturation" ? hsl->saturation
                 : control == "luminance"  ? hsl->luminance
                                           : nullptr;
  float v = 0;
  if (!bands || !parse_float(text.c_str() + eq + 1, -1.0f, 1.0f, &v)) return false;
  for (int i = 0; i < 8; ++i) {
    if (band == kBandNames[i]) {
      bands[i] = v;
      return true;
    }
  }
  return false;
}

bool render_gpu(const sp_edit& edit, const std::vector<uint8_t>& src,
                std::vector<uint8_t>* dst, int w, int h, int ow, int oh) {
#if SPHOTO_CLI_GPU
  sp_egl_context* ctx = sp_egl_context_create();
  if (!ctx) {
    std::fputs("sphoto-cli: no OpenGL ES context available\n", stderr);
    return false;
  }
  sp_renderer* renderer = sp_gles_renderer_create();
  bool ok = renderer && sp_renderer_set_image(renderer, src.data(), w, h) == SP_OK;
  if (ok) {
    ok = sp_renderer_set_edit(renderer, &edit) == SP_OK &&
         sp_renderer_render_rgba(renderer, dst->data(), ow, oh) == SP_OK;
  }
  if (!ok) std::fputs("sphoto-cli: GPU render failed\n", stderr);
  if (renderer) sp_renderer_destroy(renderer);
  sp_egl_context_destroy(ctx);
  return ok;
#else
  (void)edit, (void)src, (void)dst, (void)w, (void)h, (void)ow, (void)oh;
  std::fputs("sphoto-cli: built without the GPU backend\n", stderr);
  return false;
#endif
}

}  // namespace

int main(int argc, char** argv) {
  sp_edit edit{};
  sp_params& params = edit.adjust;
  std::vector<std::string> sets, positional;
  std::string filter;
  float strength = 1.0f;
  bool have_strength = false;
  sp_geometry geometry{};
  bool have_geometry[8] = {};  // which of the geometry flags were given
  std::vector<std::string> curve_flags, hsl_flags;
  std::string recipe_path, save_recipe_path;
  int quality = 92;
  bool gpu = false;

  for (int i = 1; i < argc; ++i) {
    const std::string arg = argv[i];
    auto value = [&](const char* flag) -> const char* {
      if (i + 1 >= argc) {
        std::fprintf(stderr, "sphoto-cli: %s needs a value\n", flag);
        std::exit(2);
      }
      return argv[++i];
    };
    if (arg == "-h" || arg == "--help") {
      usage();
      return 0;
    } else if (arg == "--list") {
      list_params();
      return 0;
    } else if (arg == "-r" || arg == "--recipe") {
      recipe_path = value("--recipe");
    } else if (arg == "-s" || arg == "--set") {
      sets.push_back(value("--set"));
    } else if (arg == "-f" || arg == "--filter") {
      filter = value("--filter");
    } else if (arg == "--strength") {
      if (!parse_float(value("--strength"), 0.0f, 1.0f, &strength)) {
        std::fputs("sphoto-cli: --strength must be 0-1\n", stderr);
        return 2;
      }
      have_strength = true;
    } else if (arg == "--rotate") {
      const int deg = std::atoi(value("--rotate"));
      if (deg % 90 != 0) {
        std::fputs("sphoto-cli: --rotate takes 90, 180 or 270\n", stderr);
        return 2;
      }
      geometry.quarter_turns = ((deg / 90) % 4 + 4) % 4;
      have_geometry[0] = true;
    } else if (arg == "--flip") {
      geometry.flip_horizontal = 1;
      have_geometry[1] = true;
    } else if (arg == "--straighten" || arg == "--vertical" || arg == "--horizontal") {
      const bool straighten = arg == "--straighten";
      float v = 0;
      if (!parse_float(value(arg.c_str()), straighten ? -45.0f : -1.0f,
                       straighten ? 45.0f : 1.0f, &v)) {
        std::fprintf(stderr, "sphoto-cli: %s is out of range\n", arg.c_str());
        return 2;
      }
      if (straighten) {
        geometry.straighten = v;
        have_geometry[2] = true;
      } else if (arg == "--vertical") {
        geometry.vertical = v;
        have_geometry[3] = true;
      } else {
        geometry.horizontal = v;
        have_geometry[4] = true;
      }
    } else if (arg == "--crop") {
      float c[4];
      if (std::sscanf(value("--crop"), "%f,%f,%f,%f", &c[0], &c[1], &c[2], &c[3]) != 4 ||
          c[2] <= 0 || c[3] <= 0 || c[0] < 0 || c[1] < 0 || c[0] + c[2] > 1.0001f ||
          c[1] + c[3] > 1.0001f) {
        std::fputs("sphoto-cli: --crop is X,Y,W,H inside 0-1\n", stderr);
        return 2;
      }
      geometry.crop_x = c[0];
      geometry.crop_y = c[1];
      geometry.crop_width = c[2];
      geometry.crop_height = c[3];
      have_geometry[5] = true;
    } else if (arg == "--curve") {
      curve_flags.push_back(value("--curve"));
    } else if (arg == "--hsl") {
      hsl_flags.push_back(value("--hsl"));
    } else if (arg == "-q" || arg == "--quality") {
      quality = std::atoi(value("--quality"));
    } else if (arg == "--save-recipe") {
      save_recipe_path = value("--save-recipe");
    } else if (arg == "--gpu") {
      gpu = true;
    } else if (!arg.empty() && arg[0] == '-' && arg != "-") {
      std::fprintf(stderr, "sphoto-cli: unknown option %s\n", arg.c_str());
      usage();
      return 2;
    } else {
      positional.push_back(arg);
    }
  }
  if (positional.size() != 2) {
    usage();
    return 2;
  }
  const std::string& input = positional[0];
  const std::string& output = positional[1];
  if (quality < 1 || quality > 100) {
    std::fputs("sphoto-cli: --quality must be 1-100\n", stderr);
    return 2;
  }

  if (!recipe_path.empty()) {
    std::string json;
    if (!read_file(recipe_path, &json)) {
      std::fprintf(stderr, "sphoto-cli: cannot read %s\n", recipe_path.c_str());
      return 1;
    }
    const int rc = sp_recipe_read_edit(json.c_str(), &edit);
    if (rc != SP_OK) {
      std::fprintf(stderr, "sphoto-cli: %s: %s\n", recipe_path.c_str(),
                   rc == SP_ERR_UNSUPPORTED ? "made by a newer SPhoto"
                                            : "not a valid recipe");
      return 1;
    }
  }
  for (const std::string& set : sets) {
    const size_t eq = set.find('=');
    const int index = eq == std::string::npos ? -1 : find_param(set.substr(0, eq));
    if (index < 0) {
      std::fprintf(stderr, "sphoto-cli: --set %s: unknown adjustment (see --list)\n",
                   set.c_str());
      return 2;
    }
    const char* name = nullptr;
    float lo = 0, hi = 0;
    sp_param_info(index, &name, &lo, &hi);
    char* end = nullptr;
    const float v = std::strtof(set.c_str() + eq + 1, &end);
    if (end == set.c_str() + eq + 1 || *end != '\0' || v < lo || v > hi) {
      std::fprintf(stderr, "sphoto-cli: --set %s: value must be %.1f to %.1f\n",
                   set.c_str(), lo, hi);
      return 2;
    }
    sp_params_set(&params, index, v);
  }

  // Command-line flags override what the recipe says.
  if (!filter.empty()) {
    bool known = false;
    for (int i = 0; i < sp_filter_count(); ++i) known |= filter == sp_filter_id(i);
    if (!known || filter.size() >= sizeof(edit.filter)) {
      std::fprintf(stderr, "sphoto-cli: unknown filter %s (see --list)\n", filter.c_str());
      return 2;
    }
    std::snprintf(edit.filter, sizeof(edit.filter), "%s", filter.c_str());
    edit.filter_strength = strength;
  } else if (have_strength) {
    edit.filter_strength = strength;
  }
  sp_geometry& g = edit.geometry;
  if (have_geometry[0]) g.quarter_turns = geometry.quarter_turns;
  if (have_geometry[1]) g.flip_horizontal = geometry.flip_horizontal;
  if (have_geometry[2]) g.straighten = geometry.straighten;
  if (have_geometry[3]) g.vertical = geometry.vertical;
  if (have_geometry[4]) g.horizontal = geometry.horizontal;
  if (have_geometry[5]) {
    g.crop_x = geometry.crop_x;
    g.crop_y = geometry.crop_y;
    g.crop_width = geometry.crop_width;
    g.crop_height = geometry.crop_height;
  }
  for (const std::string& flag : curve_flags) {
    int index = 0;
    sp_curve curve;
    if (!parse_curve(flag, &index, &curve)) {
      std::fprintf(stderr,
                   "sphoto-cli: --curve %s: expected CH:X,Y/X,Y/... with CH rgb, red, green "
                   "or blue and 2-8 points in 0-1, x increasing\n",
                   flag.c_str());
      return 2;
    }
    edit.curves[index] = curve;
  }
  for (const std::string& flag : hsl_flags) {
    if (!parse_hsl(flag, &edit.hsl)) {
      std::fprintf(stderr,
                   "sphoto-cli: --hsl %s: expected hue, saturation or luminance, a band "
                   "and a value -1 to 1, as in saturation.blue=0.4\n",
                   flag.c_str());
      return 2;
    }
  }

  int w = 0, h = 0;
  std::vector<uint8_t> src;
  if (input.rfind("test:", 0) == 0) {
    if (std::sscanf(input.c_str() + 5, "%dx%d", &w, &h) != 2 || w <= 0 || h <= 0) {
      std::fputs("sphoto-cli: test input is test:WIDTHxHEIGHT\n", stderr);
      return 2;
    }
    src.resize(static_cast<size_t>(w) * h * 4);
    sp_make_test_image(src.data(), w, h);
  } else {
    int channels = 0;
    unsigned char* pixels = stbi_load(input.c_str(), &w, &h, &channels, 4);
    if (!pixels) {
      std::fprintf(stderr, "sphoto-cli: cannot read %s: %s\n", input.c_str(),
                   stbi_failure_reason());
      return 1;
    }
    src.assign(pixels, pixels + static_cast<size_t>(w) * h * 4);
    stbi_image_free(pixels);
  }

  int ow = 0, oh = 0;
  sp_geometry_output_size(&edit.geometry, w, h, &ow, &oh);
  std::vector<uint8_t> dst(static_cast<size_t>(ow) * oh * 4);
  if (gpu) {
    if (!render_gpu(edit, src, &dst, w, h, ow, oh)) return 1;
  } else if (sp_render_cpu(&edit, src.data(), w, h, dst.data(), ow, oh) != SP_OK) {
    std::fputs("sphoto-cli: processing failed\n", stderr);
    return 1;
  }

  int ok = 0;
  if (ends_with(output, ".png")) {
    ok = stbi_write_png(output.c_str(), ow, oh, 4, dst.data(), ow * 4);
  } else if (ends_with(output, ".jpg") || ends_with(output, ".jpeg")) {
    ok = stbi_write_jpg(output.c_str(), ow, oh, 4, dst.data(), quality);
  } else {
    std::fputs("sphoto-cli: OUTPUT must end in .jpg, .jpeg or .png\n", stderr);
    return 2;
  }
  if (!ok) {
    std::fprintf(stderr, "sphoto-cli: cannot write %s\n", output.c_str());
    return 1;
  }

  if (!save_recipe_path.empty()) {
    std::string text(sp_recipe_write_edit(&edit, nullptr, 0), '\0');
    sp_recipe_write_edit(&edit, text.data(), static_cast<int>(text.size()) + 1);
    std::ofstream out(save_recipe_path, std::ios::binary);
    out << text;
    if (!out) {
      std::fprintf(stderr, "sphoto-cli: cannot write %s\n", save_recipe_path.c_str());
      return 1;
    }
  }
  std::printf("%s: %dx%d -> %s: %dx%d\n", input.c_str(), w, h, output.c_str(), ow, oh);
  return 0;
}
