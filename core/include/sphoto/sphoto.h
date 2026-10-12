/*
 * SPhoto editing core: public C API.
 *
 * Every platform shell (Swift, Kotlin/JNI, C#, ArkTS/NAPI) talks to the core
 * through this header only. Images cross the boundary as tightly described
 * RGBA8 buffers in sRGB; all colour math happens in linear light inside.
 */
#ifndef SPHOTO_H
#define SPHOTO_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

#if defined(_WIN32)
#  if defined(SPHOTO_BUILD_SHARED)
#    define SP_API __declspec(dllexport)
#  else
#    define SP_API
#  endif
#else
#  define SP_API __attribute__((visibility("default")))
#endif

enum {
  SP_OK = 0,
  SP_ERR_ARGUMENT = -1,
  SP_ERR_GPU = -2,
  SP_ERR_UNSUPPORTED = -3, /* e.g. a recipe from a newer version */
  SP_ERR_FORMAT = -4,      /* malformed recipe */
};

/* Edit parameters. Every field is 0 when the photo is untouched. Ranges are
 * listed here and returned by sp_param_info; values outside are clamped by
 * the recipe reader, not by the renderers. */
typedef struct sp_params {
  float exposure;    /* stops, -4 .. +4 */
  float contrast;    /* -1 .. +1 */
  float saturation;  /* -1 .. +1, -1 is greyscale */
  float temperature; /* -1 .. +1, + is warmer */
  float tint;        /* -1 .. +1, + is more magenta */
  float highlights;  /* -1 .. +1 */
  float shadows;     /* -1 .. +1 */
  float fade;        /*  0 .. +1, lifts the blacks */
  float sharpen;     /*  0 .. +1 */
  float clarity;     /* -1 .. +1, local midtone contrast */
  float vignette;    /* -1 .. +1, + darkens the corners */
  float grain;       /*  0 .. +1 */
} sp_params;

/* Geometry, applied before any adjustment. Zero-initialised means untouched.
 * The frame is the source after flipping, turning, perspective and
 * straightening, trimmed to the largest centred rectangle of the turned
 * source's aspect ratio that has no empty corners; the crop is a rectangle
 * in that frame, in 0 .. 1 units. */
typedef struct sp_geometry {
  int quarter_turns;   /* clockwise 90 degree turns, 0 .. 3 */
  int flip_horizontal; /* 1 mirrors left-right, before turning */
  float vertical;      /* perspective, -1 .. +1 */
  float horizontal;    /* perspective, -1 .. +1 */
  float straighten;    /* degrees, -45 .. +45, + turns clockwise */
  float crop_x, crop_y;          /* top-left of the crop in the frame */
  float crop_width, crop_height; /* 0 means the whole frame */
} sp_geometry;

/* A tone curve on sRGB-encoded values: a smooth monotone (Fritsch-Carlson)
 * interpolation through count points, flat before the first and after the
 * last. Points are in 0 .. 1 with x ascending; points whose x does not
 * increase are ignored. A count below 2 means the identity. */
typedef struct sp_curve {
  int count; /* 0 .. 8 */
  float x[8];
  float y[8];
} sp_curve;

/* Hue, saturation and luminance by colour band, each -1 .. +1. The bands
 * are red, orange, yellow, green, aqua, blue, purple and magenta, centred
 * on hues 0, 30, 60, 120, 180, 240, 270 and 300 degrees; a colour between
 * two centres takes a smooth mix of both. Greys never change. */
typedef struct sp_hsl {
  float hue[8];        /* +-1 turns the hue by up to 30 degrees */
  float saturation[8]; /* -1 removes the band's colour, +1 doubles it */
  float luminance[8];  /* +-1 moves the lightness by up to 0.3 */
} sp_hsl;

/* A whole edit: what a recipe holds. Zero-initialised means untouched. */
typedef struct sp_edit {
  sp_params adjust;
  char filter[32];       /* built-in filter id, "" for none */
  float filter_strength; /* 0 .. 1 */
  sp_geometry geometry;
  /* Colour grading on encoded values, after the linear-light adjustments:
   * HSL, then the curves, then the filter. */
  sp_curve curves[4]; /* rgb (all channels), red, green, blue */
  sp_hsl hsl;
} sp_edit;

SP_API const char* sp_version(void);
SP_API void sp_params_default(sp_params* params);

/* Generic access to the fields of sp_params, in declaration order, so shells
 * can build their adjustment lists from the core. name is the recipe key
 * ("exposure"); shells supply their own localised labels. */
SP_API int sp_param_count(void);
SP_API int sp_param_info(int index, const char** name, float* min_value,
                         float* max_value);
SP_API float sp_params_get(const sp_params* params, int index);
SP_API void sp_params_set(sp_params* params, int index, float value);

/* Built-in filters (3D LUT looks), by id: "warm", "film" and so on. Shells
 * supply their own localised names. */
SP_API int sp_filter_count(void);
SP_API const char* sp_filter_id(int index);

/* Size of the edited frame for a source of src_width x src_height. */
SP_API int sp_geometry_output_size(const sp_geometry* geometry, int src_width,
                                   int src_height, int* out_width,
                                   int* out_height);

/* Recipes: the saved form of an edit, a versioned JSON document.
 *   { "format": "sphoto-recipe", "version": 1,
 *     "adjust": { "exposure": 0.5, "contrast": 0.2 } }
 * Missing adjustments are 0; unknown keys are ignored so older builds read
 * newer recipes of the same version. */
/* Readers reject non-finite numbers and unknown values nested deeper than
 * 64 containers with SP_ERR_FORMAT; outputs are unchanged on failure. */
SP_API int sp_recipe_read(const char* json, sp_params* params);
/* The same for a whole edit, with "filter", "geometry", "curves" and "hsl"
 * sections (curves and bands left out are untouched):
 *   "filter": { "id": "film", "strength": 0.8 },
 *   "geometry": { "quarter_turns": 1, "flip_horizontal": false,
 *                 "straighten": 2.5, "vertical": 0, "horizontal": 0,
 *                 "crop": [0.1, 0, 0.8, 1] },
 *   "curves": { "rgb": [[0, 0], [0.3, 0.25], [1, 1]], "blue": [...] },
 *   "hsl": { "hue": [0.2, 0, 0, 0, 0, 0, 0, 0], "saturation": [...] } */
SP_API int sp_recipe_read_edit(const char* json, sp_edit* edit);

/* Writes the recipe for params into buf (always NUL-terminated when
 * capacity > 0) and returns the length it needs, excluding the NUL, like
 * snprintf. Only non-zero adjustments are written. */
SP_API int sp_recipe_write(const sp_params* params, char* buf, int capacity);
SP_API int sp_recipe_write_edit(const sp_edit* edit, char* buf, int capacity);

/* Fills dst (width * height * 4 bytes) with a synthetic test photo:
 * a hue sweep with a brightness ramp and a grey step wedge. */
SP_API int sp_make_test_image(uint8_t* dst, int width, int height);

/* CPU reference implementation. Every GPU backend must match it.
 * sp_process_cpu applies adjustments only, at the source size.
 * sp_render_cpu applies a whole edit and writes out_width x out_height
 * pixels (sp_geometry_output_size gives the full-resolution size). */
SP_API int sp_process_cpu(const sp_params* params, const uint8_t* src,
                          uint8_t* dst, int width, int height);
SP_API int sp_render_cpu(const sp_edit* edit, const uint8_t* src, int width,
                         int height, uint8_t* dst, int out_width,
                         int out_height);

/* ---- GPU renderer ------------------------------------------------------ */

typedef struct sp_renderer sp_renderer;

SP_API int sp_renderer_set_image(sp_renderer* r, const uint8_t* rgba,
                                 int width, int height);
SP_API void sp_renderer_set_params(sp_renderer* r, const sp_params* params);
/* id NULL or "" removes the filter. */
SP_API int sp_renderer_set_filter(sp_renderer* r, const char* id,
                                  float strength);
SP_API void sp_renderer_set_geometry(sp_renderer* r,
                                     const sp_geometry* geometry);
/* The filter, curves and HSL are baked into one colour table; setting an
 * edit rebakes it only when one of them (or the filter strength) changed. */
SP_API int sp_renderer_set_edit(sp_renderer* r, const sp_edit* edit);
/* Full-resolution size of the edited frame. */
SP_API int sp_renderer_output_size(sp_renderer* r, int* out_width,
                                   int* out_height);

/* Renders the edited frame offscreen at out_width x out_height and copies it
 * into dst (out_width * out_height * 4 bytes). */
SP_API int sp_renderer_render_rgba(sp_renderer* r, uint8_t* dst,
                                   int out_width, int out_height);
SP_API void sp_renderer_destroy(sp_renderer* r);

/* OpenGL ES 3.0 backend (Android, HarmonyOS, Windows via ANGLE, Linux).
 * Create, draw and destroy with the caller's GL context current. */
SP_API sp_renderer* sp_gles_renderer_create(void);

/* Draws the edited frame aspect-fitted into the currently bound framebuffer
 * of size viewport_width x viewport_height. */
SP_API int sp_gles_renderer_draw(sp_renderer* r, int viewport_width,
                                 int viewport_height);

/* Offscreen EGL context for hosts without a window surface (tests, and the
 * Windows readback path through ANGLE). Makes itself current on creation. */
typedef struct sp_egl_context sp_egl_context;
SP_API sp_egl_context* sp_egl_context_create(void);
SP_API int sp_egl_context_make_current(sp_egl_context* ctx);
SP_API void sp_egl_context_destroy(sp_egl_context* ctx);

/* Metal backend (iOS, macOS). Objects are passed as unretained pointers
 * (id<MTLDevice>, MTLRenderPassDescriptor*, id<MTLCommandBuffer>). */
SP_API sp_renderer* sp_metal_renderer_create(void* mtl_device);
SP_API int sp_metal_renderer_encode(sp_renderer* r, void* render_pass,
                                    void* command_buffer, int viewport_width,
                                    int viewport_height);

#ifdef __cplusplus
}
#endif

#endif /* SPHOTO_H */
