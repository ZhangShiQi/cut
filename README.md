# Cut

Windows 截图工具，使用 C# / WPF 和 Win32。默认快捷键 **Ctrl + Alt + A**；截图、二维码和文字识别全程在本地处理。当前版本 **0.4.0**。

## 运行

发布包支持 Windows 10/11 x64，包含 .NET 运行时、CPU OCR 引擎和模型。解压整个文件夹后运行 `Cut.exe`，无需安装 Python、配置接口密钥或下载模型。升级前从托盘退出旧版，不要只复制 EXE。

源码开发需要 .NET 8 或更高版本 SDK；普通开发构建需要 .NET 8 Desktop Runtime。首次构建先下载并校验模型：

```powershell
& .\Tools\download-ocr-models.ps1
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
| Ctrl + T / 识别文字按钮 | 离线识别中英文混排文字，编辑并复制 |
| 右键 | 清除已有选区，无选区则退出 |
| Esc | 取消截图 |

进入截图模式后，鼠标经过窗口会显示与窗口可见边框一致的预选框；单击选定后可以移动、缩放、复制、保存或识别内容。按住拖动始终可以自由框选。右键清除已选区域后恢复自动预选；预选期间右键退出。Enter / Ctrl + C、Ctrl + S、Ctrl + Q、Ctrl + T 也可以直接对当前预选区域操作。

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

## 文字识别（OCR）

框选文字后点击“识别文字”或按 **Ctrl + T**。支持中文、英文、数字、常用标点及混排；使用 PP-OCRv5 mobile 检测/识别模型与 ONNX Runtime CPU，无网络请求。

- 结果窗口左侧显示原图（可滚动查看），右侧可以直接编辑；默认保留识别行的换行。
- 选中文字后点击“复制选中”或按 Ctrl + C，复制编辑后的选中部分，窗口保持打开。
- “复制全部并完成”或 Ctrl + Enter 复制当前编辑内容，成功后结束截图；失败时保留修改供重试。
- “合并换行”作用于当前编辑内容，可 Ctrl + Z 撤销；不会自动合并换行或自动覆盖剪贴板。
- 结果窗口的 Esc / 返回选区保留原选区；识别期间 Esc 立即退出截图，迟到的结果不会再次弹窗。
- 模型首次使用时加载，后续复用。后台工作串行运行，支持取消推理。使用原始截图像素；大图按重叠区域分块，合并重复区域和横向跨块的文本行。
- 最多 1600 万像素、500 个文本区域；极长单行（缩放到 48 像素高后超过 4096 像素宽）会提示分段框选，避免静默截断。缺模型或文件损坏时提示重新解压完整发布包。
- 初版重点是横向印刷体截图。空格、复杂多列版面、低清晰度、手写、竖排、旋转文字可能需要手工修正；不承诺表格结构、公式或原版式还原。结果可编辑，请核对重要内容。

模型下载地址、固定版本与 SHA256 见 `Models/manifest.json`；大模型文件不提交 Git，由 `Tools/download-ocr-models.ps1` 重建，发布包内附带。输入图像仅在内存处理，不保存到磁盘或上传。测试仅生成合成样本和预览。

## 构建和验证

```powershell
dotnet build .\Cut.csproj -c Release
Start-Process .\bin\Release\net8.0-windows\win-x64\Cut.exe -ArgumentList '--self-test' -Wait
Get-Content .\artifacts\self-test.txt
Start-Process .\bin\Release\net8.0-windows\win-x64\Cut.exe -ArgumentList '--smoke-test' -Wait
Get-Content .\artifacts\smoke-test.txt
Start-Process .\bin\Release\net8.0-windows\win-x64\Cut.exe -ArgumentList '--qr-test' -Wait
Get-Content .\artifacts\qr-test.txt
Start-Process .\bin\Release\net8.0-windows\win-x64\Cut.exe -ArgumentList '--window-test' -Wait
Get-Content .\artifacts\window-test.txt
Start-Process .\bin\Release\net8.0-windows\win-x64\Cut.exe -ArgumentList '--ocr-test' -Wait
Get-Content .\artifacts\ocr-test.txt
```

`--self-test` 验证选区几何、跨屏像素、PNG 保存和快捷键冲突处理，不修改剪贴板。`--smoke-test` 短暂显示覆盖窗口，验证本机屏幕捕获、窗口位置并生成使用纯色测试背景的界面预览；不会把真实桌面保存到磁盘。

`--qr-test` 从项目根目录运行，读取 `Tests/Fixtures` 中由独立 ReportLab 编码器生成的固定样本，验证网址、中文、旋转、反色、小码、多码、去重、无二维码、取消、结果窗口动作和完整截图识别流程。窗口动作测试捕获复制内容与浏览器启动目标，不改变用户剪贴板或启动真实浏览器。该测试会短暂显示使用测试图片的窗口。

`--window-test` 验证窗口层叠命中、负坐标、屏幕边界裁剪、单击/拖拽阈值、选区调整和右键重选，并创建临时测试窗口检查真实 DWM 边界、窗口过滤及截图遮罩排除。包含显卡覆盖层回归测试，验证鼠标穿透、零透明度窗口被跳过，正常半透明窗口保留。仅保存合成图片预览，不保存真实桌面。

`--ocr-test` 使用真实离线模型识别 WPF 渲染的中英文、深色背景、小字号、标点、多行、宽图与跨块长句样本，记录忽略空白的字符错误率和冷/热耗时；检查空白图、取消、模型校验、阅读顺序、编辑/复制/撤销以及完整截图会话。复制操作通过注入回调验证，不替换用户的剪贴板；结果是固定合成样本成绩，不代表所有截图的准确率。

发布包含运行时的便携版（发布脚本也复制本机 Visual Studio REDIST 中的 C++ 运行库）：

```powershell
& .\Tools\publish.ps1
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
- `OcrService` / `IOcrEngine`：本地模型校验、串行后台推理、检测分块、区域合并、CTC 解码和阅读顺序。
- `OcrResultWindow`：原图预览、文本编辑、部分/全部复制、合并换行和撤销。
- `LinkService`：仅将 HTTP/HTTPS 网页交给系统默认浏览器。

## 手工验收

- 在微信、画图等程序粘贴截图，检查尺寸和画面。
- 保存对话框取消后仍可调整选区；保存失败后能重试。
- 双屏 100% / 150% 混合缩放、屏幕位于主屏左侧/上方、跨屏拖拽。
- 重复快捷键、连续截图、退出后重新启动，检查残留遮罩和快捷键释放。
- 框选实际网页上的二维码；在记事本粘贴识别文本，点击网页结果验证系统默认浏览器。

首版不含标注、滚动截图、贴图或自动更新。GDI 后端面向普通 SDR 桌面；HDR、受保护视频和独占全屏应用的捕获效果需要额外兼容性验证。Windows N/KN 精简媒体版本可能需要启用 Media Feature Pack 才能加载 OpenCV 的系统依赖。

发布包包含 ZXing.Net、PaddleOCR / RapidOCR 转换模型、ONNX Runtime、OpenCvSharp / OpenCV 及运行时。版本、来源和许可见 `THIRD-PARTY-NOTICES.txt`、`Models/manifest.json` 和 `Licenses`。
