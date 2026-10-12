// Recipe JSON reading and writing. A small hand-written reader rather than a
// JSON library: recipes are tiny, and parsing numbers by hand keeps the result
// independent of the process locale (strtod reads "0,5" under some locales).
#include <algorithm>
#include <cctype>
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>

#include "filters.h"
#include "grading.h"
#include "params.h"
#include "sphoto/sphoto.h"

namespace {

constexpr int kRecipeVersion = 1;
constexpr const char* kFormat = "sphoto-recipe";
constexpr int kMaxIgnoredDepth = 64;

class Reader {
 public:
  explicit Reader(const char* text) : p_(text) {}

  bool fail() const { return failed_; }

  void space() {
    while (*p_ == ' ' || *p_ == '\t' || *p_ == '\n' || *p_ == '\r') ++p_;
  }

  bool peek(char c) {
    space();
    return *p_ == c;
  }

  bool expect(char c) {
    space();
    if (*p_ != c) return error();
    ++p_;
    return true;
  }

  bool string(std::string* out) {
    if (!expect('"')) return false;
    out->clear();
    while (*p_ && *p_ != '"') {
      char c = *p_++;
      if (c == '\\') {
        c = *p_++;
        switch (c) {
          case '"': case '\\': case '/': break;
          case 'b': c = '\b'; break;
          case 'f': c = '\f'; break;
          case 'n': c = '\n'; break;
          case 'r': c = '\r'; break;
          case 't': c = '\t'; break;
          case 'u':  // Keys and values we read are ASCII; keep a placeholder.
            for (int i = 0; i < 4; ++i) {
              if (!std::isxdigit(static_cast<unsigned char>(*p_))) return error();
              ++p_;
            }
            c = '?';
            break;
          default: return error();
        }
      }
      out->push_back(c);
    }
    return expect('"');
  }

  bool number(double* out) {
    space();
    double sign = 1.0;
    if (*p_ == '-') {
      sign = -1.0;
      ++p_;
    }
    if (!std::isdigit(static_cast<unsigned char>(*p_))) return error();
    double value = 0.0;
    while (std::isdigit(static_cast<unsigned char>(*p_))) value = value * 10.0 + (*p_++ - '0');
    if (*p_ == '.') {
      ++p_;
      if (!std::isdigit(static_cast<unsigned char>(*p_))) return error();
      double scale = 0.1;
      while (std::isdigit(static_cast<unsigned char>(*p_))) {
        value += (*p_++ - '0') * scale;
        scale *= 0.1;
      }
    }
    if (*p_ == 'e' || *p_ == 'E') {
      ++p_;
      int exp_sign = 1;
      if (*p_ == '+' || *p_ == '-') exp_sign = *p_++ == '-' ? -1 : 1;
      if (!std::isdigit(static_cast<unsigned char>(*p_))) return error();
      int exponent = 0;
      while (std::isdigit(static_cast<unsigned char>(*p_))) {
        exponent = std::min(exponent * 10 + (*p_++ - '0'), 400);
      }
      value *= std::pow(10.0, exp_sign * exponent);
    }
    value *= sign;
    if (!std::isfinite(value)) return error();
    *out = value;
    return true;
  }

  // Skips any JSON value.
  bool skip(int depth = 0) {
    space();
    if (*p_ == '"') {
      std::string ignored;
      return string(&ignored);
    }
    if (*p_ == '{' || *p_ == '[') {
      if (depth >= kMaxIgnoredDepth) return error();
      const char close = *p_ == '{' ? '}' : ']';
      const bool object = *p_ == '{';
      ++p_;
      if (peek(close)) return expect(close);
      do {
        if (object) {
          std::string key;
          if (!string(&key) || !expect(':')) return false;
        }
        if (!skip(depth + 1)) return false;
      } while (comma());
      return expect(close);
    }
    for (const char* word : {"true", "false", "null"}) {
      const size_t n = std::strlen(word);
      if (std::strncmp(p_, word, n) == 0) {
        p_ += n;
        return true;
      }
    }
    double ignored;
    return number(&ignored);
  }

  bool boolean(bool* out) {
    space();
    if (std::strncmp(p_, "true", 4) == 0) {
      p_ += 4;
      *out = true;
      return true;
    }
    if (std::strncmp(p_, "false", 5) == 0) {
      p_ += 5;
      *out = false;
      return true;
    }
    return error();
  }

  bool comma() {
    if (!peek(',')) return false;
    ++p_;
    return true;
  }

  bool at_end() {
    space();
    return *p_ == '\0';
  }

 private:
  bool error() {
    failed_ = true;
    return false;
  }

  const char* p_;
  bool failed_ = false;
};

int find_param(const std::string& name) {
  for (int i = 0; i < sp::kParamCount; ++i) {
    if (name == sp::kParams[i].name) return i;
  }
  return -1;
}


float clamp_to(double v, double lo, double hi) {
  return static_cast<float>(v < lo ? lo : v > hi ? hi : v);
}

// Reads an object's members, calling member(key) for each; member reads the
// value and returns false on a format error.
template <typename F>
bool read_object(Reader& in, F member) {
  if (!in.expect('{')) return false;
  if (!in.peek('}')) {
    do {
      std::string key;
      if (!in.string(&key) || !in.expect(':')) return false;
      if (!member(key)) return false;
    } while (in.comma());
  }
  return in.expect('}');
}

bool read_adjust(Reader& in, sp_params* params) {
  return read_object(in, [&](const std::string& name) {
    const int index = find_param(name);
    if (index < 0) return in.skip();
    double value;
    if (!in.number(&value)) return false;
    const sp::ParamInfo& info = sp::kParams[index];
    sp::param_data(*params)[index] = clamp_to(value, info.min, info.max);
    return true;
  });
}

bool read_filter(Reader& in, sp_edit* edit) {
  return read_object(in, [&](const std::string& key) {
    if (key == "id") {
      std::string id;
      if (!in.string(&id)) return false;
      // An unknown filter (from a newer build) reads as no filter.
      std::memset(edit->filter, 0, sizeof(edit->filter));
      if (id.size() < sizeof(edit->filter) && sp::is_filter(id.c_str())) {
        std::memcpy(edit->filter, id.data(), id.size());
      }
      return true;
    }
    if (key == "strength") {
      double v;
      if (!in.number(&v)) return false;
      edit->filter_strength = clamp_to(v, 0.0, 1.0);
      return true;
    }
    return in.skip();
  });
}

bool read_geometry(Reader& in, sp_geometry* g) {
  return read_object(in, [&](const std::string& key) {
    double v;
    if (key == "quarter_turns") {
      if (!in.number(&v)) return false;
      g->quarter_turns = ((static_cast<int>(std::lround(std::fmod(v, 4.0))) % 4) + 4) % 4;
    } else if (key == "flip_horizontal") {
      bool flip;
      if (!in.boolean(&flip)) return false;
      g->flip_horizontal = flip ? 1 : 0;
    } else if (key == "straighten") {
      if (!in.number(&v)) return false;
      g->straighten = clamp_to(v, -45.0, 45.0);
    } else if (key == "vertical") {
      if (!in.number(&v)) return false;
      g->vertical = clamp_to(v, -1.0, 1.0);
    } else if (key == "horizontal") {
      if (!in.number(&v)) return false;
      g->horizontal = clamp_to(v, -1.0, 1.0);
    } else if (key == "crop") {
      double c[4];
      if (!in.expect('[')) return false;
      for (int i = 0; i < 4; ++i) {
        if ((i > 0 && !in.expect(',')) || !in.number(&c[i])) return false;
      }
      if (!in.expect(']')) return false;
      g->crop_width = clamp_to(c[2], 0.0, 1.0);
      g->crop_height = clamp_to(c[3], 0.0, 1.0);
      g->crop_x = clamp_to(c[0], 0.0, 1.0);
      g->crop_y = clamp_to(c[1], 0.0, 1.0);
    } else {
      return in.skip();
    }
    return true;
  });
}

// The keys of sp_edit.curves, and of the HSL controls.
constexpr const char* kCurveKeys[4] = {"rgb", "red", "green", "blue"};
constexpr const char* kHslKeys[3] = {"hue", "saturation", "luminance"};

float* hsl_bands(sp_hsl& hsl, int i) {
  return i == 0 ? hsl.hue : i == 1 ? hsl.saturation : hsl.luminance;
}
const float* hsl_bands(const sp_hsl& hsl, int i) {
  return i == 0 ? hsl.hue : i == 1 ? hsl.saturation : hsl.luminance;
}

// Reads a JSON array, calling element() for each item.
template <typename F>
bool read_array(Reader& in, F element) {
  if (!in.expect('[')) return false;
  if (!in.peek(']')) {
    do {
      if (!element()) return false;
    } while (in.comma());
  }
  return in.expect(']');
}

// A curve is an array of [x, y] points; points past the eighth are dropped.
bool read_curve(Reader& in, sp_curve* curve) {
  *curve = sp_curve{};
  return read_array(in, [&] {
    double x, y;
    if (!in.expect('[') || !in.number(&x) || !in.expect(',') || !in.number(&y) ||
        !in.expect(']')) {
      return false;
    }
    if (curve->count < 8) {
      curve->x[curve->count] = clamp_to(x, 0.0, 1.0);
      curve->y[curve->count] = clamp_to(y, 0.0, 1.0);
      ++curve->count;
    }
    return true;
  });
}

bool read_curves(Reader& in, sp_curve curves[4]) {
  return read_object(in, [&](const std::string& key) {
    for (int i = 0; i < 4; ++i) {
      if (key == kCurveKeys[i]) return read_curve(in, &curves[i]);
    }
    return in.skip();
  });
}

// Each HSL control is an array of 8 amounts, one per band.
bool read_hsl(Reader& in, sp_hsl* hsl) {
  return read_object(in, [&](const std::string& key) {
    for (int i = 0; i < 3; ++i) {
      if (key != kHslKeys[i]) continue;
      float* bands = hsl_bands(*hsl, i);
      std::fill(bands, bands + 8, 0.0f);
      int n = 0;
      return read_array(in, [&] {
        double v;
        if (!in.number(&v)) return false;
        if (n < 8) bands[n] = clamp_to(v, -1.0, 1.0);
        ++n;
        return true;
      });
    }
    return in.skip();
  });
}

// Formats a value with up to four decimals and a "." whatever the locale.
std::string format_number(double value) {
  const long long scaled = std::llround(std::fabs(value) * 10000.0);
  if (scaled == 0) return "0";
  char number[48];
  std::snprintf(number, sizeof(number), "%s%lld.%04lld", value < 0 ? "-" : "",
                scaled / 10000, scaled % 10000);
  std::string text = number;
  while (text.back() == '0') text.pop_back();
  if (text.back() == '.') text.pop_back();
  return text;
}

bool is_default(const sp_geometry& g) {
  const bool full_crop = (g.crop_width <= 0 || g.crop_width >= 1) &&
                         (g.crop_height <= 0 || g.crop_height >= 1);
  return g.quarter_turns % 4 == 0 && !g.flip_horizontal && g.straighten == 0 &&
         g.vertical == 0 && g.horizontal == 0 && full_crop;
}

int copy_out(const std::string& out, char* buf, int capacity) {
  if (capacity > 0) {
    const size_t n = std::min(out.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(buf, out.data(), n);
    buf[n] = '\0';
  }
  return static_cast<int>(out.size());
}

}  // namespace

extern "C" {

int sp_recipe_read_edit(const char* json, sp_edit* edit) {
  if (!json || !edit) return SP_ERR_ARGUMENT;
  sp_edit result{};
  bool have_format = false;
  double version = 0.0;
  Reader in(json);
  const bool ok = read_object(in, [&](const std::string& key) {
    if (key == "format") {
      std::string format;
      if (!in.string(&format)) return false;
      have_format = format == kFormat;
      return true;
    }
    if (key == "version") return in.number(&version);
    if (key == "adjust") return read_adjust(in, &result.adjust);
    if (key == "filter") return read_filter(in, &result);
    if (key == "geometry") return read_geometry(in, &result.geometry);
    if (key == "curves") return read_curves(in, result.curves);
    if (key == "hsl") return read_hsl(in, &result.hsl);
    return in.skip();
  });
  if (!ok || !in.at_end() || !have_format) return SP_ERR_FORMAT;
  if (version != kRecipeVersion) {
    return version > kRecipeVersion ? SP_ERR_UNSUPPORTED : SP_ERR_FORMAT;
  }
  if (!result.filter[0]) result.filter_strength = 0.0f;
  *edit = result;
  return SP_OK;
}

int sp_recipe_read(const char* json, sp_params* params) {
  if (!json || !params) return SP_ERR_ARGUMENT;
  sp_edit edit;
  const int rc = sp_recipe_read_edit(json, &edit);
  if (rc == SP_OK) *params = edit.adjust;
  return rc;
}

int sp_recipe_write_edit(const sp_edit* edit, char* buf, int capacity) {
  if (!edit || capacity < 0 || (capacity > 0 && !buf)) return SP_ERR_ARGUMENT;
  std::string out = "{\n  \"format\": \"";
  out += kFormat;
  out += "\",\n  \"version\": " + std::to_string(kRecipeVersion);

  char id[sizeof(edit->filter) + 1] = {};
  std::memcpy(id, edit->filter, sizeof(edit->filter));
  if (id[0]) {
    out += ",\n  \"filter\": { \"id\": \"";
    out += id;
    out += "\", \"strength\": " + format_number(edit->filter_strength) + " }";
  }

  out += ",\n  \"adjust\": {";
  bool first = true;
  for (int i = 0; i < sp::kParamCount; ++i) {
    const std::string text = format_number(sp::param_data(edit->adjust)[i]);
    if (text == "0") continue;
    out += first ? "\n    \"" : ",\n    \"";
    out += sp::kParams[i].name;
    out += "\": " + text;
    first = false;
  }
  out += first ? "}" : "\n  }";

  const sp_geometry& g = edit->geometry;
  if (!is_default(g)) {
    const double cw = g.crop_width > 0 ? g.crop_width : 1.0;
    const double ch = g.crop_height > 0 ? g.crop_height : 1.0;
    out += ",\n  \"geometry\": {\n    \"quarter_turns\": " +
           std::to_string(((g.quarter_turns % 4) + 4) % 4) +
           ",\n    \"flip_horizontal\": " + (g.flip_horizontal ? "true" : "false") +
           ",\n    \"straighten\": " + format_number(g.straighten) +
           ",\n    \"vertical\": " + format_number(g.vertical) +
           ",\n    \"horizontal\": " + format_number(g.horizontal) +
           ",\n    \"crop\": [" + format_number(g.crop_x) + ", " +
           format_number(g.crop_y) + ", " + format_number(cw) + ", " +
           format_number(ch) + "]\n  }";
  }

  // Only the curves and HSL controls that change something.
  std::string section;
  for (int i = 0; i < 4; ++i) {
    const sp_curve& c = edit->curves[i];
    if (sp::curve_is_identity(c)) continue;
    section += section.empty() ? "\n    \"" : ",\n    \"";
    section += kCurveKeys[i];
    section += "\": [";
    for (int k = 0; k < std::min(c.count, 8); ++k) {
      section += (k ? ", [" : "[") + format_number(c.x[k]) + ", " + format_number(c.y[k]) + "]";
    }
    section += "]";
  }
  if (!section.empty()) out += ",\n  \"curves\": {" + section + "\n  }";

  section.clear();
  for (int i = 0; i < 3; ++i) {
    const float* bands = hsl_bands(edit->hsl, i);
    std::string values;
    bool used = false;
    for (int k = 0; k < 8; ++k) {
      const std::string text = format_number(bands[k]);
      used = used || text != "0";
      values += (k ? ", " : "") + text;
    }
    if (!used) continue;
    section += section.empty() ? "\n    \"" : ",\n    \"";
    section += kHslKeys[i];
    section += "\": [" + values + "]";
  }
  if (!section.empty()) out += ",\n  \"hsl\": {" + section + "\n  }";
  out += "\n}\n";
  return copy_out(out, buf, capacity);
}

int sp_recipe_write(const sp_params* params, char* buf, int capacity) {
  if (!params || capacity < 0 || (capacity > 0 && !buf)) return SP_ERR_ARGUMENT;
  sp_edit edit{};
  edit.adjust = *params;
  return sp_recipe_write_edit(&edit, buf, capacity);
}

}  // extern "C"
