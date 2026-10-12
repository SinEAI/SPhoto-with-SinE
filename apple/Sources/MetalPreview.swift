import MetalKit
import SwiftUI

/// Hosts an MTKView that the core draws into. Redraws only when the edit
/// parameters change and reports the GPU time of each frame.
struct MetalPreview {
    var params: EditParams
    var onFrameTime: (Double) -> Void

    func makeCoordinator() -> Coordinator { Coordinator(onFrameTime: onFrameTime) }

    fileprivate func makeView(_ coordinator: Coordinator) -> MTKView {
        let view = MTKView(frame: .zero, device: coordinator.device)
        view.colorPixelFormat = .bgra8Unorm
        view.enableSetNeedsDisplay = true
        view.isPaused = true
        view.delegate = coordinator
        return view
    }

    fileprivate func update(_ view: MTKView, _ coordinator: Coordinator) {
        coordinator.onFrameTime = onFrameTime
        coordinator.setParams(params)
        #if os(iOS)
        view.setNeedsDisplay()
        #else
        view.needsDisplay = true
        #endif
    }

    final class Coordinator: NSObject, MTKViewDelegate {
        let device = MTLCreateSystemDefaultDevice()
        private let queue: MTLCommandQueue?
        private var renderer: OpaquePointer?
        var onFrameTime: (Double) -> Void

        /// A 12-megapixel frame, the size of a typical phone photo.
        private static let testSize = (width: Int32(4032), height: Int32(3024))

        init(onFrameTime: @escaping (Double) -> Void) {
            self.onFrameTime = onFrameTime
            queue = device?.makeCommandQueue()
            super.init()
            guard let device else { return }
            renderer = sp_metal_renderer_create(Unmanaged.passUnretained(device).toOpaque())
            let (w, h) = Self.testSize
            var pixels = [UInt8](repeating: 0, count: Int(w) * Int(h) * 4)
            sp_make_test_image(&pixels, w, h)
            sp_renderer_set_image(renderer, pixels, w, h)
        }

        deinit {
            sp_renderer_destroy(renderer)
        }

        func setParams(_ p: EditParams) {
            var params = p.core
            sp_renderer_set_params(renderer, &params)
            sp_renderer_set_filter(renderer, p.filter, p.strength)
            var geometry = p.geometry
            sp_renderer_set_geometry(renderer, &geometry)
        }

        func mtkView(_ view: MTKView, drawableSizeWillChange size: CGSize) {}

        func draw(in view: MTKView) {
            guard let renderer, let queue,
                  let pass = view.currentRenderPassDescriptor,
                  let drawable = view.currentDrawable,
                  let commands = queue.makeCommandBuffer() else { return }
            let size = view.drawableSize
            sp_metal_renderer_encode(
                renderer,
                Unmanaged.passUnretained(pass).toOpaque(),
                Unmanaged.passUnretained(commands).toOpaque(),
                Int32(size.width), Int32(size.height))
            commands.present(drawable)
            commands.addCompletedHandler { [weak self] buffer in
                let ms = (buffer.gpuEndTime - buffer.gpuStartTime) * 1000
                DispatchQueue.main.async { self?.onFrameTime(ms) }
            }
            commands.commit()
        }
    }
}

#if os(iOS)
extension MetalPreview: UIViewRepresentable {
    func makeUIView(context: Context) -> MTKView { makeView(context.coordinator) }
    func updateUIView(_ view: MTKView, context: Context) { update(view, context.coordinator) }
}
#else
extension MetalPreview: NSViewRepresentable {
    func makeNSView(context: Context) -> MTKView { makeView(context.coordinator) }
    func updateNSView(_ view: MTKView, context: Context) { update(view, context.coordinator) }
}
#endif
