# Cut

Windows 截图工具，使用 C# / WPF 和 Win32。默认快捷键 **Ctrl + Alt + A**；截图和二维码识别全程在本地处理。当前版本 **0.3.1**。

## 运行

需要 Windows 和 .NET 8 SDK（开发）或 .NET 8 Desktop Runtime（运行构建产物）。

```powershell
dotnet run --project .\Cut.csproj
```

首次运行显示设置窗口。关闭设置窗口后程序继续在系统托盘运行；双击托盘图标打开设置，通过托盘菜单退出。快捷键被占用时可在设置中修改，或点击“开始截图”。配置保存在 `%LOCALAPPDATA%\Cut\settings.json`。

## 操作

| 操作 | 行为 |
| --- | --- |
| Ctrl + Alt + A | 冻结桌面，进入框选 |
| 移动鼠标 / 单击 | 自动预选鼠标下的窗口 / 锁定为选区 |
| 左键拖拽 | 创建选区 |
| 拖动选区内部 / 边框 | 移动 / 调整选区 |
| Enter / Ctrl + C / 双击选区 | 复制图片并退出截图 |
| Ctrl + S / 保存按钮 | 保存 PNG，成功后退出截图 |
| Ctrl + Q / 识别二维码按钮 | 识别选区中的单个或多个二维码 |
| 右键 | 清除已有选区，无选区则退出 |
| Esc | 取消截图 |

进入截图模式后，鼠标经过窗口会显示与窗口可见边框一致的预选框；单击选定后可以移动、缩放、复制、保存或识别二维码。按住拖动始终可以自由框选。右键清除已选区域后恢复自动预选；预选期间右键退出。Enter / Ctrl + C、Ctrl + S、Ctrl + Q 也可以直接对当前预选区域操作。

窗口位置和层叠次序在抓取屏幕前记录，预选框与冻结画面对应；忽略 Cut 自身、隐藏、最小化及其他虚拟桌面上的窗口。优先使用 DWM 可见边框，排除不可见的调整大小边缘。鼠标下没有应用窗口时预选所在显示器。首版识别顶层窗口，不识别网页内部元素；被其他窗口遮住的内容不会被补全，导出的仍是屏幕上实际可见的画面。

0.3.1 修复显卡/录屏软件的透明覆盖层被误当成全屏窗口的问题：忽略鼠标穿透的分层窗口以及全局透明度为零的窗口，正常可交互的半透明窗口仍可预选。

支持多显示器、负坐标和跨屏框选；不同缩放比例的显示器按物理像素裁剪，不重采样。显示器之间没有实际屏幕的区域输出为黑色。显示器布局变化时取消当前截图。

## 二维码识别

框选完整二维码（包括周围空白）后，点击“识别二维码”或按 **Ctrl + Q**。

- 多个结果以列表展示，相同内容合并；选中一项查看完整文本。
- “复制内容”复制选中项的原始文本，成功后退出截图。
- HTTP/HTTPS 网页显示“打开链接”，点击后通过系统默认浏览器打开，并退出截图。其他内容只作为文本显示。
- 关闭结果窗口、按 Esc 或点击“返回选区”回到原选区。
- 未找到二维码或识别出错时保留选区；可以调整范围重试。
- 识别在后台进行，识别期间 Esc 可立即退出截图；迟到的结果不会弹窗。
- 原图和反色图均会扫描，支持同一选区中混合黑白极性的二维码。较小图片未识别到时尝试有限放大。超过 3200 万像素的选区会提示缩小。
- 不上传图片，不自动打开链接，不自动覆盖剪贴板。无法保证识别缺角、模糊或像素不足的二维码。

## 构建和验证

```powershell
dotnet build .\Cut.csproj -c Release
Start-Process .\bin\Release\net8.0-windows\Cut.exe -ArgumentList '--self-test' -Wait
Get-Content .\artifacts\self-test.txt
Start-Process .\bin\Release\net8.0-windows\Cut.exe -ArgumentList '--smoke-test' -Wait
Get-Content .\artifacts\smoke-test.txt
Start-Process .\bin\Release\net8.0-windows\Cut.exe -ArgumentList '--qr-test' -Wait
Get-Content .\artifacts\qr-test.txt
Start-Process .\bin\Release\net8.0-windows\Cut.exe -ArgumentList '--window-test' -Wait
Get-Content .\artifacts\window-test.txt
```

`--self-test` 验证选区几何、跨屏像素、PNG 保存和快捷键冲突处理，不修改剪贴板。`--smoke-test` 短暂显示覆盖窗口，验证本机屏幕捕获、窗口位置并生成使用纯色测试背景的界面预览；不会把真实桌面保存到磁盘。

`--qr-test` 从项目根目录运行，读取 `Tests/Fixtures` 中由独立 ReportLab 编码器生成的固定样本，验证网址、中文、旋转、反色、小码、多码、去重、无二维码、取消、结果窗口动作和完整截图识别流程。窗口动作测试捕获复制内容与浏览器启动目标，不改变用户剪贴板或启动真实浏览器。该测试会短暂显示使用测试图片的窗口。

`--window-test` 验证窗口层叠命中、负坐标、屏幕边界裁剪、单击/拖拽阈值、选区调整和右键重选，并创建临时测试窗口检查真实 DWM 边界、窗口过滤及截图遮罩排除。包含显卡覆盖层回归测试，验证鼠标穿透、零透明度窗口被跳过，正常半透明窗口保留。仅保存合成图片预览，不保存真实桌面。

发布依赖本机运行时的版本：

```powershell
dotnet publish .\Cut.csproj -c Release -o .\artifacts\publish
```

## 实现

程序、窗口和托盘使用统一的截图取景框图标。SVG 源文件和多尺寸 ICO 位于 `Assets`；托盘采用适合小尺寸的加粗版本。ICO 已嵌入程序，运行时不依赖外部图标文件。重新生成图标的方法见 `Assets/README.md`。

- `CaptureService`：GDI 屏幕快照及按物理像素裁剪、拼接。
- `SelectionController`：独立的框选、移动、调整大小逻辑。
- `CaptureSession`：多屏共享状态、复制和保存流程。
- `OverlayWindow`：每个显示器一个 WPF 覆盖窗口，渲染冻结画面、遮罩和工具条。
- `HotkeyService`：Win32 全局快捷键，替换失败时保留原注册。
- `ExportService`：剪贴板 Bitmap + PNG 格式、短暂重试、PNG 文件写入。
- `QrCodeService`：ZXing.Net 本地解码、串行后台工作、取消与有限预处理。
- `QrResultWindow`：结果列表、原始文本、复制与打开链接。
- `LinkService`：仅将 HTTP/HTTPS 网页交给系统默认浏览器。

## 手工验收

- 在微信、画图等程序粘贴截图，检查尺寸和画面。
- 保存对话框取消后仍可调整选区；保存失败后能重试。
- 双屏 100% / 150% 混合缩放、屏幕位于主屏左侧/上方、跨屏拖拽。
- 重复快捷键、连续截图、退出后重新启动，检查残留遮罩和快捷键释放。
- 框选实际网页上的二维码；在记事本粘贴识别文本，点击网页结果验证系统默认浏览器。

首版不含标注、OCR、滚动截图、贴图或自动更新。GDI 后端面向普通 SDR 桌面；HDR、受保护视频和独占全屏应用的捕获效果需要额外兼容性验证。

发布包包含 ZXing.Net 0.16.11（Apache-2.0），许可见 `THIRD-PARTY-NOTICES.txt` 和 `Licenses`。
