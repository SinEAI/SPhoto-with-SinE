import SwiftUI

/// One adjustment of the core, in sp_params order.
struct Adjustment: Identifiable {
    let id: Int
    let key: String
    let title: String
    let range: ClosedRange<Float>

    /// Display names for the core's recipe keys.
    private static let titles: [String: String] = [
        "exposure": "曝光", "contrast": "对比度", "saturation": "饱和度",
        "temperature": "色温", "tint": "色调", "highlights": "高光",
        "shadows": "阴影", "fade": "褪色", "sharpen": "锐化",
        "clarity": "清晰度", "vignette": "暗角", "grain": "颗粒",
    ]

    /// Every adjustment the core offers, read from the core itself.
    static let all: [Adjustment] = (0..<Int(sp_param_count())).map { i in
        var name: UnsafePointer<CChar>?
        var lo: Float = 0
        var hi: Float = 0
        sp_param_info(Int32(i), &name, &lo, &hi)
        let key = name.map { String(cString: $0) } ?? ""
        return Adjustment(id: i, key: key, title: titles[key] ?? key, range: lo...hi)
    }
}

/// A built-in filter of the core.
struct Filter: Identifiable {
    let id: String
    let title: String

    private static let titles: [String: String] = [
        "warm": "暖阳", "film": "胶片", "cool": "冷调",
        "teal_orange": "青橙", "mono": "黑白", "mono_soft": "柔和黑白",
    ]

    static let all: [Filter] = (0..<Int(sp_filter_count())).map { i in
        let id = String(cString: sp_filter_id(Int32(i)))
        return Filter(id: id, title: titles[id] ?? id)
    }
}

struct EditParams: Equatable {
    var values = [Float](repeating: 0, count: Adjustment.all.count)
    /// Filter id, "" for none.
    var filter = ""
    var strength: Float = 1
    var quarterTurns = 0
    var flip = false
    var straighten: Float = 0

    var geometry: sp_geometry {
        var g = sp_geometry()
        g.quarter_turns = Int32(quarterTurns)
        g.flip_horizontal = flip ? 1 : 0
        g.straighten = straighten
        return g
    }

    var core: sp_params {
        var params = sp_params()
        for (i, value) in values.enumerated() {
            sp_params_set(&params, Int32(i), value)
        }
        return params
    }
}

/// Editor: the core's Metal preview plus one slider per adjustment.
struct EditorView: View {
    @State private var params = EditParams()
    @State private var frameMs: Double = 0

    var body: some View {
        NavigationStack {
            VStack(spacing: 0) {
                MetalPreview(params: params) { frameMs = $0 }
                    .background(.black)
                    .ignoresSafeArea(edges: .horizontal)
                Form {
                    Section("滤镜") {
                        ScrollView(.horizontal, showsIndicators: false) {
                            HStack {
                                FilterButton(title: "原图", selected: params.filter.isEmpty) {
                                    params.filter = ""
                                }
                                ForEach(Filter.all) { filter in
                                    FilterButton(title: filter.title, selected: params.filter == filter.id) {
                                        params.filter = filter.id
                                    }
                                }
                            }
                        }
                        if !params.filter.isEmpty {
                            AdjustmentSlider(title: "强度", value: $params.strength, range: 0...1)
                        }
                    }
                    Section("几何") {
                        AdjustmentSlider(title: "拉直", value: $params.straighten, range: -45...45)
                    }
                    Section {
                        ForEach(Adjustment.all) { adjustment in
                            AdjustmentSlider(title: adjustment.title,
                                             value: $params.values[adjustment.id],
                                             range: adjustment.range)
                        }
                    } header: {
                        Text("调整")
                    } footer: {
                        Text(String(format: "GPU 帧耗时 %.1f ms · 核心 %@", frameMs, String(cString: sp_version())))
                    }
                }
                .formStyle(.grouped)
                .frame(maxHeight: 320)
            }
            .navigationTitle("SPhoto")
            #if os(iOS)
            .navigationBarTitleDisplayMode(.inline)
            #endif
            .toolbar {
                ToolbarItemGroup(placement: .primaryAction) {
                    Button("旋转", systemImage: "rotate.right") { params.quarterTurns = (params.quarterTurns + 1) % 4 }
                    Button("翻转", systemImage: "arrow.left.and.right.righttriangle.left.righttriangle.right") {
                        params.flip.toggle()
                    }
                    Button("还原") { params = EditParams() }
                        .disabled(params == EditParams())
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}

private struct FilterButton: View {
    let title: String
    let selected: Bool
    let action: () -> Void

    var body: some View {
        if selected {
            Button(title, action: action).buttonStyle(.borderedProminent)
        } else {
            Button(title, action: action).buttonStyle(.bordered)
        }
    }
}

private struct AdjustmentSlider: View {
    let title: String
    @Binding var value: Float
    let range: ClosedRange<Float>

    var body: some View {
        LabeledContent {
            Slider(value: $value, in: range)
        } label: {
            HStack {
                Text(title)
                Spacer()
                Text(value, format: .number.precision(.fractionLength(2)))
                    .monospacedDigit()
                    .foregroundStyle(.secondary)
            }
            .frame(width: 110)
        }
    }
}
