#include <GLES3/gl3.h>

#include <algorithm>
#include <cstdio>
#include <initializer_list>
#include <string>

#include "../grading.h"
#include "../params.h"
#include "../pipeline_source.h"
#include "../renderer.h"
#include "shaders.h"

namespace {

GLuint compile(GLenum type, std::initializer_list<const char*> sources) {
  GLuint shader = glCreateShader(type);
  glShaderSource(shader, static_cast<GLsizei>(sources.size()), sources.begin(),
                 nullptr);
  glCompileShader(shader);
  GLint ok = GL_FALSE;
  glGetShaderiv(shader, GL_COMPILE_STATUS, &ok);
  if (!ok) {
    char log[1024];
    glGetShaderInfoLog(shader, sizeof(log), nullptr, log);
    std::fprintf(stderr, "sphoto: shader compile failed: %s\n", log);
    glDeleteShader(shader);
    return 0;
  }
  return shader;
}

class GlesRenderer final : public sp_renderer {
 public:
  bool init() {
    GLuint vs = compile(GL_VERTEX_SHADER, {sp::gles::kVertexShader});
    GLuint fs = compile(GL_FRAGMENT_SHADER,
                        {sp::gles::kFragmentPrelude, sp::kShaderParamsStruct,
                         sp::kShaderFrameStruct, sp::kPipelineSource,
                         sp::gles::kFragmentMain});
    if (!vs || !fs) return false;
    program_ = glCreateProgram();
    glAttachShader(program_, vs);
    glAttachShader(program_, fs);
    glLinkProgram(program_);
    glDeleteShader(vs);
    glDeleteShader(fs);
    GLint ok = GL_FALSE;
    glGetProgramiv(program_, GL_LINK_STATUS, &ok);
    if (!ok) return false;
    u_rect_ = glGetUniformLocation(program_, "u_rect");
    u_flip_y_ = glGetUniformLocation(program_, "u_flip_y");
    u_image_ = glGetUniformLocation(program_, "u_image");
    u_lut_ = glGetUniformLocation(program_, "u_lut");
    for (int i = 0; i < sp::kParamCount; ++i) {
      const std::string name = std::string("u_p.") + sp::kParams[i].name;
      u_params_[i] = glGetUniformLocation(program_, name.c_str());
    }
    for (int i = 0; i < sp::kFrameFieldCount; ++i) {
      const std::string name = std::string("u_f.") + sp::kFrameFieldNames[i];
      u_frame_[i] = glGetUniformLocation(program_, name.c_str());
    }
    glGenVertexArrays(1, &vao_);
    // Until there is a colour table, a 2x2x2 identity keeps the sampler valid.
    const float identity[] = {0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0,
                              0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1};
    upload_lut(identity, 2);
    return true;
  }

  ~GlesRenderer() override {
    glDeleteProgram(program_);
    glDeleteVertexArrays(1, &vao_);
    glDeleteTextures(1, &image_);
    glDeleteTextures(1, &lut_);
    glDeleteTextures(1, &target_);
    glDeleteFramebuffers(1, &fbo_);
  }

  int set_image(const uint8_t* rgba, int width, int height) override {
    if (!image_) glGenTextures(1, &image_);
    glBindTexture(GL_TEXTURE_2D, image_);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 1);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0, GL_RGBA,
                 GL_UNSIGNED_BYTE, rgba);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    return glGetError() == GL_NO_ERROR ? SP_OK : SP_ERR_GPU;
  }

  int draw(int viewport_width, int viewport_height) {
    if (!image_) return SP_ERR_ARGUMENT;
    // Aspect-fit the edited frame into the viewport.
    const sp::Geometry g = sp::solve_geometry(geometry, image_width, image_height);
    const float image_aspect = static_cast<float>(g.width / g.height);
    const float view_aspect =
        static_cast<float>(viewport_width) / viewport_height;
    float sx = 1.0f, sy = 1.0f;
    if (image_aspect > view_aspect) {
      sy = view_aspect / image_aspect;
    } else {
      sx = image_aspect / view_aspect;
    }
    glViewport(0, 0, viewport_width, viewport_height);
    glClearColor(0.0f, 0.0f, 0.0f, 1.0f);
    glClear(GL_COLOR_BUFFER_BIT);
    const int w = std::max(1, static_cast<int>(sx * viewport_width + 0.5f));
    const int h = std::max(1, static_cast<int>(sy * viewport_height + 0.5f));
    return draw_quad(-sx, -sy, sx, sy, false, w, h);
  }

  int render_rgba(uint8_t* dst, int out_width, int out_height) override {
    if (!image_) return SP_ERR_ARGUMENT;
    GLint previous_fbo = 0;
    glGetIntegerv(GL_FRAMEBUFFER_BINDING, &previous_fbo);
    if (!ensure_target(out_width, out_height)) return SP_ERR_GPU;
    glBindFramebuffer(GL_FRAMEBUFFER, fbo_);
    glViewport(0, 0, out_width, out_height);
    int result = draw_quad(-1.0f, -1.0f, 1.0f, 1.0f, true, out_width, out_height);
    if (result == SP_OK) {
      glPixelStorei(GL_PACK_ALIGNMENT, 1);
      glReadPixels(0, 0, out_width, out_height, GL_RGBA, GL_UNSIGNED_BYTE, dst);
      if (glGetError() != GL_NO_ERROR) result = SP_ERR_GPU;
    }
    glBindFramebuffer(GL_FRAMEBUFFER, static_cast<GLuint>(previous_fbo));
    return result;
  }

 private:
  // out_width x out_height is the size in pixels of the drawn frame.
  int draw_quad(float x0, float y0, float x1, float y1, bool flip_y,
                int out_width, int out_height) {
    if (lut_changed) {
      if (!lut.empty()) upload_lut(lut.data(), sp::kLutSize);
      lut_changed = false;
    }
    glUseProgram(program_);
    glBindVertexArray(vao_);
    glActiveTexture(GL_TEXTURE0);
    glBindTexture(GL_TEXTURE_2D, image_);
    glUniform1i(u_image_, 0);
    glActiveTexture(GL_TEXTURE1);
    glBindTexture(GL_TEXTURE_3D, lut_);
    glUniform1i(u_lut_, 1);
    glActiveTexture(GL_TEXTURE0);
    glUniform4f(u_rect_, x0, y0, x1, y1);
    glUniform1f(u_flip_y_, flip_y ? 1.0f : 0.0f);
    const sp::Frame frame = frame_for(out_width, out_height);
    const float* fields = &frame.h00;
    for (int i = 0; i < sp::kFrameFieldCount; ++i) glUniform1f(u_frame_[i], fields[i]);
    const float* values = sp::param_data(params);
    for (int i = 0; i < sp::kParamCount; ++i) {
      glUniform1f(u_params_[i], values[i]);
    }
    glDrawArrays(GL_TRIANGLE_STRIP, 0, 4);
    glBindVertexArray(0);
    return glGetError() == GL_NO_ERROR ? SP_OK : SP_ERR_GPU;
  }

  // Uploads an n^3 RGB table, red fastest, as a filtered 3D texture.
  void upload_lut(const float* rgb, int n) {
    if (!lut_) glGenTextures(1, &lut_);
    glActiveTexture(GL_TEXTURE1);
    glBindTexture(GL_TEXTURE_3D, lut_);
    glPixelStorei(GL_UNPACK_ALIGNMENT, 4);
    glTexImage3D(GL_TEXTURE_3D, 0, GL_RGB16F, n, n, n, 0, GL_RGB, GL_FLOAT, rgb);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_MIN_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_MAG_FILTER, GL_LINEAR);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_WRAP_S, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_WRAP_T, GL_CLAMP_TO_EDGE);
    glTexParameteri(GL_TEXTURE_3D, GL_TEXTURE_WRAP_R, GL_CLAMP_TO_EDGE);
    glActiveTexture(GL_TEXTURE0);
  }

  bool ensure_target(int width, int height) {
    if (target_ && width == target_width_ && height == target_height_) {
      return true;
    }
    if (!target_) glGenTextures(1, &target_);
    if (!fbo_) glGenFramebuffers(1, &fbo_);
    glBindTexture(GL_TEXTURE_2D, target_);
    glTexImage2D(GL_TEXTURE_2D, 0, GL_RGBA8, width, height, 0, GL_RGBA,
                 GL_UNSIGNED_BYTE, nullptr);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MIN_FILTER, GL_NEAREST);
    glTexParameteri(GL_TEXTURE_2D, GL_TEXTURE_MAG_FILTER, GL_NEAREST);
    glBindFramebuffer(GL_FRAMEBUFFER, fbo_);
    glFramebufferTexture2D(GL_FRAMEBUFFER, GL_COLOR_ATTACHMENT0, GL_TEXTURE_2D,
                           target_, 0);
    const bool complete =
        glCheckFramebufferStatus(GL_FRAMEBUFFER) == GL_FRAMEBUFFER_COMPLETE;
    target_width_ = width;
    target_height_ = height;
    return complete;
  }

  GLuint program_ = 0, vao_ = 0, image_ = 0, lut_ = 0, target_ = 0, fbo_ = 0;
  GLint u_rect_ = -1, u_flip_y_ = -1, u_image_ = -1, u_lut_ = -1;
  GLint u_params_[sp::kParamCount] = {};
  GLint u_frame_[sp::kFrameFieldCount] = {};
  int target_width_ = 0, target_height_ = 0;
};

}  // namespace

extern "C" {

sp_renderer* sp_gles_renderer_create(void) {
  auto* renderer = new GlesRenderer();
  if (!renderer->init()) {
    delete renderer;
    return nullptr;
  }
  return renderer;
}

int sp_gles_renderer_draw(sp_renderer* r, int viewport_width,
                          int viewport_height) {
  if (!r || viewport_width <= 0 || viewport_height <= 0) {
    return SP_ERR_ARGUMENT;
  }
  return static_cast<GlesRenderer*>(r)->draw(viewport_width, viewport_height);
}

}  // extern "C"
