# SPhoto

一个轻量的图库、看图与修图应用。各端界面使用平台原生框架，编辑能力集中在一套共享的 C++ 内核里。目前 Windows 版功能最完整，macOS、iOS、安卓、鸿蒙是验证内核用的简易编辑界面。

## 目录

| 目录 | 内容 |
| --- | --- |
| `core/` | C++17 编辑内核：C 接口、编辑管线（CPU 实现 + OpenGL ES 3 与 Metal 两个 GPU 后端）、配方读写、`sphoto-cli` 命令行工具、测试 |
| `windows/` | WinUI 3 应用（图库、看图、修图），经 P/Invoke 调用内核，GPU 走 ANGLE；`SPhoto.Library` 是不依赖 Windows 的图库层 |
| `apple/` | SwiftUI 应用（iOS、iPadOS、macOS），用 XcodeGen 生成工程 |
| `android/` | Jetpack Compose + Material 3 应用，经 JNI 调用内核 |
| `harmony/` | ArkUI 应用，经 Node-API 与 XComponent 调用内核 |

## 功能（Windows）

- **图库**：显示所选文件夹里的照片，按拍摄月份分组；内容相同的文件算作一张，连拍和相似照片自动叠放，修图保存的版本收在原图下面。
- **标签与页面**：标签只记在本地索引里，不写进照片文件；页面是几个标签的组合。
- **看图**：方向键切换、滚轮缩放、双击在适应窗口和 100% 之间切换，按 EXIF 方向自动摆正。
- **修图**：12 项基础调节、6 款滤镜（33³ 3D LUT，强度可调）、曲线、HSL、裁剪、旋转、翻转、拉直和透视；撤销重做；结果另存为新文件，原图不改动。

## 内核

- 同一份管线源码 `core/src/pipeline.inc` 既编译成 CPU 实现，也作为 OpenGL ES 和 Metal 的着色器文本，测试会逐项比较 GPU 与 CPU 的输出。
- 调节、滤镜和几何可以存成带版本号的 JSON 配方（`sp_recipe_read_edit` / `sp_recipe_write_edit`）。
- `sphoto-cli` 对图片套用配方并导出 JPEG 或 PNG，`--list` 列出全部调节和滤镜：

```sh
./build/sphoto-cli -f film --strength 0.8 --rotate 90 --crop 0,0.1,1,0.8 \
  -s exposure=0.5 --save-recipe r.json photo.jpg out.jpg
```

## 构建

**内核与测试**（Linux 需要 `libegl-dev libgles-dev` 和 Mesa）：

```sh
cmake -S core -B build -G Ninja
cmake --build build
EGL_PLATFORM=surfaceless ./build/sphoto_tests   # Linux 无显示器时
```

**Windows**：需要 Visual Studio 2022（含 C++ 与 .NET 桌面开发）和 vcpkg。

```powershell
$env:VCPKG_ROOT = "<vcpkg 所在目录>"
./windows/build-native.ps1 -Platform x64
dotnet build windows/SPhoto.Windows -c Release -p:Platform=x64
```

图库层是纯 .NET 8，任何系统上都能测试：`dotnet test windows/SPhoto.Library.Tests`。

Windows x64 发布通过 `.github/workflows/windows-release.yml` 完成。推送版本标签后，Windows runner 构建并测试内核、图库、便携程序和界面，再验证安装、卸载与产物脱敏。测试通过才上传安装包、便携 ZIP、文件清单和 SHA-256 校验文件到预览版 Release。原生依赖固定在 `windows/vcpkg.json` 指定的 vcpkg baseline，发布包包含运行依赖和第三方许可说明，不包含调试符号或用户数据。

每次提交、推送和发布都必须脱敏；提交身份使用 GitHub noreply 邮箱。检查与本地 hooks 设置见 `CONTRIBUTING.md`。

**iOS / macOS**：安装 XcodeGen 后执行 `cd apple && xcodegen generate`，用 Xcode 打开 `SPhoto.xcodeproj`。

**安卓**：用 Android Studio 打开 `android/`，或执行 `cd android && ./gradlew installDebug`。

**鸿蒙**：用 DevEco Studio 打开 `harmony/`，配置签名后在真机上运行。

## 第三方代码

`core/third_party/stb/` 是 [stb](https://github.com/nothings/stb) 的 `stb_image.h` 与 `stb_image_write.h`（公有领域 / MIT 双许可），只用于 `sphoto-cli` 读写图片。
