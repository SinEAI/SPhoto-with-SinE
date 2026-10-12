// Metal backend. Build with ARC (-fobjc-arc).
#import <Metal/Metal.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <string>
#include <vector>

#include "../grading.h"
#include "../params.h"
#include "../pipeline_source.h"
#include "../renderer.h"

namespace {

// The Metal prelude for pipeline.inc. The library source is kPrelude, the
// SpParams and SpFrame structs, pipeline.inc and kStages.
constexpr const char* kPrelude = R"(
#include <metal_stdlib>
using namespace metal;
#define vec2 float2
#define vec3 float3
#define SP_TEX_PARAMS texture2d<float> sp_tex, texture3d<float> sp_lut_tex, sampler sp_smp,
#define SP_TEX_ARGS sp_tex, sp_lut_tex, sp_smp,
static float3 sp_fetch(texture2d<float> tex, texture3d<float> lut, sampler smp, float2 uv) {
  return tex.sample(smp, uv).xyz;
}
static float3 sp_lut(texture2d<float> tex, texture3d<float> lut, sampler smp, float3 c) {
  float n = float(lut.get_width());
  return lut.sample(smp, c * ((n - 1.0) / n) + 0.5 / n).xyz;
}
)";

constexpr const char* kStages = R"(
struct VertexOut {
  float4 position [[position]];
  float2 uv;
};

// rect: the target rectangle in clip space (x0, y0, x1, y1).
vertex VertexOut sp_vertex(uint vid [[vertex_id]],
                           constant float4& rect [[buffer(0)]]) {
  float2 p = float2(float(vid & 1), float(vid >> 1));
  VertexOut out;
  out.uv = float2(p.x, 1.0 - p.y);
  out.position = float4(mix(rect.xy, rect.zw, p), 0.0, 1.0);
  return out;
}

fragment float4 sp_fragment(VertexOut in [[stage_in]],
                            constant SpFrame& frame [[buffer(0)]],
                            constant SpParams& params [[buffer(1)]],
                            texture2d<float> image [[texture(0)]],
                            texture3d<float> lut [[texture(1)]],
                            sampler smp [[sampler(0)]]) {
  SpParams p = params;
  SpFrame f = frame;
  float3 c = sp_process(image, lut, smp, p, f, in.uv);
  return float4(c, image.sample(smp, sp_source_uv(f, in.uv)).w);
}
)";

// Converts to IEEE half precision, rounding to nearest even.
uint16_t to_half(float value) {
  uint32_t x;
  std::memcpy(&x, &value, 4);
  const uint32_t sign = (x >> 16) & 0x8000u;
  const int exponent = static_cast<int>((x >> 23) & 0xffu) - 127 + 15;
  uint32_t mantissa = x & 0x7fffffu;
  if (exponent <= 0) {  // subnormal or zero: values this small read as 0
    return static_cast<uint16_t>(sign);
  }
  if (exponent >= 31) return static_cast<uint16_t>(sign | 0x7c00u);
  uint32_t half = sign | (static_cast<uint32_t>(exponent) << 10) | (mantissa >> 13);
  const uint32_t rest = mantissa & 0x1fffu;
  if (rest > 0x1000u || (rest == 0x1000u && (half & 1u))) ++half;
  return static_cast<uint16_t>(half);
}

class MetalRenderer final : public sp_renderer {
 public:
  bool init(id<MTLDevice> device) {
    device_ = device;
    queue_ = [device newCommandQueue];
    MTLCompileOptions* options = [MTLCompileOptions new];
#pragma clang diagnostic push
#pragma clang diagnostic ignored "-Wdeprecated-declarations"
    options.fastMathEnabled = NO;  // keep pow() close to the CPU reference
#pragma clang diagnostic pop
    NSError* error = nil;
    const std::string source = std::string(kPrelude) + sp::kShaderParamsStruct +
                               sp::kShaderFrameStruct + sp::kPipelineSource + kStages;
    library_ = [device newLibraryWithSource:@(source.c_str())
                                    options:options
                                      error:&error];
    if (!library_) {
      NSLog(@"sphoto: Metal shader compile failed: %@", error);
      return false;
    }
    MTLSamplerDescriptor* sd = [MTLSamplerDescriptor new];
    sd.minFilter = MTLSamplerMinMagFilterLinear;
    sd.magFilter = MTLSamplerMinMagFilterLinear;
    sd.sAddressMode = MTLSamplerAddressModeClampToEdge;
    sd.tAddressMode = MTLSamplerAddressModeClampToEdge;
    sd.rAddressMode = MTLSamplerAddressModeClampToEdge;
    sampler_ = [device newSamplerStateWithDescriptor:sd];
    // Until there is a colour table, a 2x2x2 identity keeps the binding valid.
    const float identity[] = {0, 0, 0, 1, 0, 0, 0, 1, 0, 1, 1, 0,
                              0, 0, 1, 1, 0, 1, 0, 1, 1, 1, 1, 1};
    lut_texture_ = make_lut(identity, 2);
    return queue_ && sampler_ && lut_texture_;
  }

  int set_image(const uint8_t* rgba, int width, int height) override {
    MTLTextureDescriptor* td = [MTLTextureDescriptor
        texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm
                                     width:width
                                    height:height
                                 mipmapped:NO];
    td.usage = MTLTextureUsageShaderRead;
    image_ = [device_ newTextureWithDescriptor:td];
    if (!image_) return SP_ERR_GPU;
    [image_ replaceRegion:MTLRegionMake2D(0, 0, width, height)
              mipmapLevel:0
                withBytes:rgba
              bytesPerRow:static_cast<NSUInteger>(width) * 4];
    return SP_OK;
  }

  int encode(MTLRenderPassDescriptor* pass, id<MTLCommandBuffer> commands,
             int viewport_width, int viewport_height) {
    if (!image_) return SP_ERR_ARGUMENT;
    id<MTLTexture> target = pass.colorAttachments[0].texture;
    id<MTLRenderPipelineState> pipeline = pipeline_for(target.pixelFormat);
    if (!pipeline) return SP_ERR_GPU;

    // Aspect-fit the edited frame into the viewport.
    const sp::Geometry g = sp::solve_geometry(geometry, image_width, image_height);
    const float image_aspect = static_cast<float>(g.width / g.height);
    const float view_aspect =
        static_cast<float>(viewport_width) / static_cast<float>(viewport_height);
    float sx = 1.0f, sy = 1.0f;
    if (image_aspect > view_aspect) {
      sy = view_aspect / image_aspect;
    } else {
      sx = image_aspect / view_aspect;
    }

    pass.colorAttachments[0].loadAction = MTLLoadActionClear;
    pass.colorAttachments[0].clearColor = MTLClearColorMake(0, 0, 0, 1);
    id<MTLRenderCommandEncoder> encoder =
        [commands renderCommandEncoderWithDescriptor:pass];
    draw(encoder, pipeline, -sx, -sy, sx, sy,
         std::max(1, static_cast<int>(sx * viewport_width + 0.5f)),
         std::max(1, static_cast<int>(sy * viewport_height + 0.5f)));
    [encoder endEncoding];
    return SP_OK;
  }

  int render_rgba(uint8_t* dst, int out_width, int out_height) override {
    if (!image_) return SP_ERR_ARGUMENT;
    MTLTextureDescriptor* td = [MTLTextureDescriptor
        texture2DDescriptorWithPixelFormat:MTLPixelFormatRGBA8Unorm
                                     width:out_width
                                    height:out_height
                                 mipmapped:NO];
    td.usage = MTLTextureUsageRenderTarget;
    td.storageMode = MTLStorageModePrivate;
    id<MTLTexture> target = [device_ newTextureWithDescriptor:td];
    const NSUInteger row_bytes = static_cast<NSUInteger>(out_width) * 4;
    id<MTLBuffer> readback =
        [device_ newBufferWithLength:row_bytes * out_height
                             options:MTLResourceStorageModeShared];
    id<MTLRenderPipelineState> pipeline =
        pipeline_for(MTLPixelFormatRGBA8Unorm);
    if (!target || !readback || !pipeline) return SP_ERR_GPU;

    MTLRenderPassDescriptor* pass = [MTLRenderPassDescriptor new];
    pass.colorAttachments[0].texture = target;
    pass.colorAttachments[0].loadAction = MTLLoadActionDontCare;
    pass.colorAttachments[0].storeAction = MTLStoreActionStore;

    id<MTLCommandBuffer> commands = [queue_ commandBuffer];
    id<MTLRenderCommandEncoder> encoder =
        [commands renderCommandEncoderWithDescriptor:pass];
    draw(encoder, pipeline, -1.0f, -1.0f, 1.0f, 1.0f, out_width, out_height);
    [encoder endEncoding];
    id<MTLBlitCommandEncoder> blit = [commands blitCommandEncoder];
    [blit copyFromTexture:target
                     sourceSlice:0
                     sourceLevel:0
                    sourceOrigin:MTLOriginMake(0, 0, 0)
                      sourceSize:MTLSizeMake(out_width, out_height, 1)
                        toBuffer:readback
               destinationOffset:0
          destinationBytesPerRow:row_bytes
        destinationBytesPerImage:row_bytes * out_height];
    [blit endEncoding];
    [commands commit];
    [commands waitUntilCompleted];
    if (commands.status != MTLCommandBufferStatusCompleted) return SP_ERR_GPU;
    std::memcpy(dst, readback.contents, row_bytes * out_height);
    return SP_OK;
  }

 private:
  void draw(id<MTLRenderCommandEncoder> encoder,
            id<MTLRenderPipelineState> pipeline, float x0, float y0, float x1,
            float y1, int out_width, int out_height) {
    // out_width x out_height is the size in pixels of the drawn frame.
    if (lut_changed) {
      if (!lut.empty()) lut_texture_ = make_lut(lut.data(), sp::kLutSize);
      lut_changed = false;
    }
    const float rect[4] = {x0, y0, x1, y1};
    const sp::Frame frame = frame_for(out_width, out_height);
    [encoder setRenderPipelineState:pipeline];
    [encoder setVertexBytes:rect length:sizeof(rect) atIndex:0];
    [encoder setFragmentBytes:&frame length:sizeof(frame) atIndex:0];
    [encoder setFragmentBytes:&params length:sizeof(params) atIndex:1];
    [encoder setFragmentTexture:image_ atIndex:0];
    [encoder setFragmentTexture:lut_texture_ atIndex:1];
    [encoder setFragmentSamplerState:sampler_ atIndex:0];
    [encoder drawPrimitives:MTLPrimitiveTypeTriangleStrip
                vertexStart:0
                vertexCount:4];
  }

  // An n^3 RGB table, red fastest, as a filtered half-float 3D texture.
  id<MTLTexture> make_lut(const float* rgb, int n) {
    MTLTextureDescriptor* td = [MTLTextureDescriptor new];
    td.textureType = MTLTextureType3D;
    td.pixelFormat = MTLPixelFormatRGBA16Float;
    td.width = n;
    td.height = n;
    td.depth = n;
    td.usage = MTLTextureUsageShaderRead;
    id<MTLTexture> texture = [device_ newTextureWithDescriptor:td];
    if (!texture) return nil;
    std::vector<uint16_t> texels(static_cast<size_t>(n) * n * n * 4);
    for (size_t i = 0; i < static_cast<size_t>(n) * n * n; ++i) {
      texels[i * 4 + 0] = to_half(rgb[i * 3 + 0]);
      texels[i * 4 + 1] = to_half(rgb[i * 3 + 1]);
      texels[i * 4 + 2] = to_half(rgb[i * 3 + 2]);
      texels[i * 4 + 3] = to_half(1.0f);
    }
    const NSUInteger row = static_cast<NSUInteger>(n) * 8;
    [texture replaceRegion:MTLRegionMake3D(0, 0, 0, n, n, n)
               mipmapLevel:0
                     slice:0
                 withBytes:texels.data()
               bytesPerRow:row
             bytesPerImage:row * n];
    return texture;
  }

  id<MTLRenderPipelineState> pipeline_for(MTLPixelFormat format) {
    if (pipeline_ && pipeline_format_ == format) return pipeline_;
    MTLRenderPipelineDescriptor* pd = [MTLRenderPipelineDescriptor new];
    pd.vertexFunction = [library_ newFunctionWithName:@"sp_vertex"];
    pd.fragmentFunction = [library_ newFunctionWithName:@"sp_fragment"];
    pd.colorAttachments[0].pixelFormat = format;
    NSError* error = nil;
    pipeline_ = [device_ newRenderPipelineStateWithDescriptor:pd error:&error];
    if (!pipeline_) NSLog(@"sphoto: Metal pipeline failed: %@", error);
    pipeline_format_ = format;
    return pipeline_;
  }

  id<MTLDevice> device_ = nil;
  id<MTLCommandQueue> queue_ = nil;
  id<MTLLibrary> library_ = nil;
  id<MTLSamplerState> sampler_ = nil;
  id<MTLTexture> image_ = nil;
  id<MTLTexture> lut_texture_ = nil;
  id<MTLRenderPipelineState> pipeline_ = nil;
  MTLPixelFormat pipeline_format_ = MTLPixelFormatInvalid;
};

}  // namespace

extern "C" {

sp_renderer* sp_metal_renderer_create(void* mtl_device) {
  id<MTLDevice> device = (__bridge id<MTLDevice>)mtl_device;
  if (!device) device = MTLCreateSystemDefaultDevice();
  if (!device) return nullptr;
  auto* renderer = new MetalRenderer();
  if (!renderer->init(device)) {
    delete renderer;
    return nullptr;
  }
  return renderer;
}

int sp_metal_renderer_encode(sp_renderer* r, void* render_pass,
                             void* command_buffer, int viewport_width,
                             int viewport_height) {
  if (!r || !render_pass || !command_buffer || viewport_width <= 0 ||
      viewport_height <= 0) {
    return SP_ERR_ARGUMENT;
  }
  return static_cast<MetalRenderer*>(r)->encode(
      (__bridge MTLRenderPassDescriptor*)render_pass,
      (__bridge id<MTLCommandBuffer>)command_buffer, viewport_width,
      viewport_height);
}

}  // extern "C"
