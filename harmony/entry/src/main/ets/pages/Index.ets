import sphoto, { ParamInfo } from 'libsphoto.so';

/** Display names for the core's recipe keys. */
const TITLES: Record<string, string> = {
  'exposure': '曝光', 'contrast': '对比度', 'saturation': '饱和度',
  'temperature': '色温', 'tint': '色调', 'highlights': '高光',
  'shadows': '阴影', 'fade': '褪色', 'sharpen': '锐化',
  'clarity': '清晰度', 'vignette': '暗角', 'grain': '颗粒',
};

/** Display names for the core's built-in filters. */
const FILTER_TITLES: Record<string, string> = {
  'warm': '暖阳', 'film': '胶片', 'cool': '冷调',
  'teal_orange': '青橙', 'mono': '黑白', 'mono_soft': '柔和黑白',
};

/** '' (no filter) and then the core's built-in filters. */
const FILTERS: string[] = [''].concat(sphoto.filterIds());

/** The adjustments the core offers, read from the core itself. */
const ADJUSTMENTS: ParamInfo[] = sphoto.paramInfo();

@Entry
@Component
struct Index {
  private adjustments: ParamInfo[] = ADJUSTMENTS;
  @State values: number[] = ADJUSTMENTS.map((): number => 0);
  @State frameMs: number = 0;
  @State filter: string = '';
  @State strength: number = 1;
  @State quarterTurns: number = 0;
  @State flip: boolean = false;
  @State straighten: number = 0;

  private applyLook(): void {
    this.frameMs = sphoto.setLook(this.filter, this.strength, this.quarterTurns, this.flip, this.straighten);
  }

  @Builder
  slider(title: string, value: number, min: number, max: number, onChange: (v: number) => void) {
    Row({ space: 12 }) {
      Text(title).fontSize(14).width(56)
      Slider({ value: value, min: min, max: max, step: 0.01 })
        .layoutWeight(1)
        .onChange((v: number) => onChange(v))
      Text(value.toFixed(2)).fontSize(12).fontColor($r('sys.color.font_secondary')).width(44)
    }
    .width('100%')
  }

  private set(index: number, value: number): void {
    this.values = this.values.map((v: number, i: number): number => i === index ? value : v);
    this.frameMs = sphoto.setParams(this.values);
  }

  @Builder
  adjustment(index: number) {
    Row({ space: 12 }) {
      Text(TITLES[this.adjustments[index].key] ?? this.adjustments[index].key).fontSize(14).width(56)
      Slider({
        value: this.values[index],
        min: this.adjustments[index].min,
        max: this.adjustments[index].max,
        step: 0.01
      })
        .layoutWeight(1)
        .onChange((v: number) => this.set(index, v))
      Text(this.values[index].toFixed(2)).fontSize(12).fontColor($r('sys.color.font_secondary')).width(44)
    }
    .width('100%')
  }

  build() {
    Navigation() {
      Column() {
        XComponent({ id: 'preview', type: XComponentType.SURFACE, libraryname: 'sphoto' })
          .layoutWeight(1)
          .width('100%')
          .backgroundColor(Color.Black)

        Column({ space: 8 }) {
          Text(`GPU 帧耗时 ${this.frameMs.toFixed(1)} ms · 核心 ${sphoto.version()}`)
            .fontSize(12)
            .fontColor($r('sys.color.font_secondary'))
          Scroll() {
            Row({ space: 8 }) {
              ForEach(FILTERS, (id: string) => {
                Button(id === '' ? '原图' : (FILTER_TITLES[id] ?? id))
                  .buttonStyle(this.filter === id ? ButtonStyleMode.EMPHASIZED : ButtonStyleMode.NORMAL)
                  .controlSize(ControlSize.SMALL)
                  .onClick(() => {
                    this.filter = id;
                    this.applyLook();
                  })
              }, (id: string) => id)
            }
          }
          .scrollable(ScrollDirection.Horizontal)
          .scrollBar(BarState.Off)
          .width('100%')
          Scroll() {
            Column({ space: 8 }) {
              if (this.filter !== '') {
                this.slider('滤镜强度', this.strength, 0, 1, (v: number) => {
                  this.strength = v;
                  this.applyLook();
                })
              }
              this.slider('拉直', this.straighten, -45, 45, (v: number) => {
                this.straighten = v;
                this.applyLook();
              })
              ForEach(this.adjustments, (item: ParamInfo, index: number) => {
                this.adjustment(index)
              }, (item: ParamInfo) => item.key)
            }
          }
          .height(280)
        }
        .padding(16)
        .backgroundColor($r('sys.color.comp_background_primary'))
      }
      .height('100%')
    }
    .title('SPhoto')
    .titleMode(NavigationTitleMode.Mini)
    .hideBackButton(true)
    .menus([
      { value: '旋转', action: () => {
        this.quarterTurns = (this.quarterTurns + 1) % 4;
        this.applyLook();
      } },
      { value: '翻转', action: () => {
        this.flip = !this.flip;
        this.applyLook();
      } },
      { value: '还原', action: () => {
        this.values = this.adjustments.map((): number => 0);
        this.filter = '';
        this.strength = 1;
        this.quarterTurns = 0;
        this.flip = false;
        this.straighten = 0;
        sphoto.setParams(this.values);
        this.applyLook();
      } },
    ])
  }
}
