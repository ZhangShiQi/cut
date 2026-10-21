# Cut

Windows 截图工具，使用 C# / WPF 和 Win32。默认快捷键 **Ctrl + Alt + A**；截图、二维码和文字识别全程在本地处理，可另行配置 Azure 在线中英翻译和 AI 文字助手。当前版本 **0.6.0**。

## 运行

发布包支持 Windows 10/11 x64，包含 .NET 运行时、CPU OCR 引擎和模型。解压整个文件夹后运行 `Cut.exe`；本地截图、二维码和 OCR 无需安装 Python、配置接口密钥或下载模型。升级前从托盘退出旧版，不要只复制 EXE。

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

- 结果窗口可展开查看原图；原文和译文并排显示，均可编辑。OCR 默认保留识别行的换行。
- 选中文字后点击“复制选中”或按 Ctrl + C，复制编辑后的选中部分，窗口保持打开。
- “复制原文并完成”或原文框内 Ctrl + Enter 复制当前编辑内容，成功后结束截图；失败时保留修改供重试。
- “合并换行”作用于当前编辑内容，可 Ctrl + Z 撤销；不会自动合并换行或自动覆盖剪贴板。
- 结果窗口的 Esc / 返回选区保留原选区；识别期间 Esc 立即退出截图，迟到的结果不会再次弹窗。
- 模型首次使用时加载，后续复用。后台工作串行运行，支持取消推理。使用原始截图像素；大图按重叠区域分块，合并重复区域和横向跨块的文本行。
- 最多 1600 万像素、500 个文本区域；极长单行（缩放到 48 像素高后超过 4096 像素宽）会提示分段框选，避免静默截断。缺模型或文件损坏时提示重新解压完整发布包。
- 初版重点是横向印刷体截图。空格、复杂多列版面、低清晰度、手写、竖排、旋转文字可能需要手工修正；不承诺表格结构、公式或原版式还原。结果可编辑，请核对重要内容。

模型下载地址、固定版本与 SHA256 见 `Models/manifest.json`；大模型文件不提交 Git，由 `Tools/download-ocr-models.ps1` 重建，发布包内附带。输入图像仅在内存处理，不保存到磁盘或上传。测试仅生成合成样本和预览。

## 在线翻译设置与使用

本版接入 **Microsoft Azure Translator Text API v3**，支持自动检测源语言，以及简体中文和英文互译。未配置时，OCR、编辑和原文复制仍可使用；程序不会附带共享密钥或自动申请付费资源。

1. 在 Azure 门户创建 Translator 资源，在“密钥和终结点”中取得密钥及区域。官方说明：[认证配置](https://learn.microsoft.com/azure/ai-services/translator/text-translation/reference/authentication)。
2. 从主窗口点击“翻译服务设置”，或在 OCR 结果窗口点击“翻译设置”。通常保留默认地址 `https://api.cognitive.microsofttranslator.com`。区域型 / 多服务资源填写对应区域代码（例如 `eastasia`）；全局单服务资源可留空。
3. 填写自己的密钥，点击“测试连接”。测试仅发送固定示例 `Hello`，成功后再点击“保存”；测试不会自动保存配置。清空密钥后保存可停用翻译。
4. OCR 后选择目标语言，点击“翻译全部”，或先在原文中选中文字，再点击“翻译选中”。翻译使用当前编辑后的内容。
5. 译文可继续校对，使用“复制选中译文”或“复制译文并完成”。Ctrl + C 复制当前编辑框选中文字；Ctrl + Enter 复制当前编辑框全部内容并完成截图。

也支持 `api-apc` / `api-eur` / `api-nam.cognitive.microsofttranslator.com` 区域端点，以及 `https://资源名.cognitiveservices.azure.com` 自定义资源域名；自定义域名自动使用 `/translator/text/v3.0/translate` 路径。当前仅支持这些 Azure 官方 HTTPS 地址，不支持任意代理、其他厂商或中国区独立端点。

- 仅在明确点击翻译时上传当前待翻译的文字，不上传截图。在线翻译依赖网络和有效服务资源，费用/额度由自己的 Azure 套餐决定；打开 OCR 窗口不会发送请求。
- 单次上限 5000 个 UTF-16 字符，超长内容请选中部分分段翻译。默认 30 秒超时。错误提示区分密钥/区域错误、权限问题、限流/配额、网络与服务故障，不自动重试可能计费的请求。
- 原文或语言发生变化时标记“译文待更新”；正在请求时会取消，保留已有译文。手动编辑译文、关闭窗口或取消后，迟到的响应也不会覆盖当前内容。
- 翻译期间 Esc 先取消翻译；再次 Esc 返回截图选区。失败或取消后可重试，原文和已有译文不清空。
- 密钥通过 Windows DPAPI CurrentUser 加密保存到 `%LOCALAPPDATA%\Cut\translation.json`，与截图快捷键配置独立。不要复制该文件到其他用户或电脑；迁移后需重新填写密钥。程序不记录翻译原文、译文或密钥到日志。
- “翻译服务设置”打开时暂停快捷键启动截图，避免把正在配置的窗口截入画面。

本次交付为可配置版本：用户尚无 Azure Translator 资源，**没有进行真实密钥的在线翻译实测**。本地测试使用模拟 HTTP 响应验证请求、解析、错误处理和完整界面流程，不代表服务端翻译质量或账户连通性验证。取得资源后请先用设置页的“测试连接”验证自己的配置。

## AI 助手

截图后按 **Ctrl + T** 提取文字，再切换右侧的 **AI 助手** 页签。第一版基于 OCR 文字理解，支持读懂截图文字、解释报错、总结重点、润色和自定义提问；不具备图片视觉分析、图表/布局理解或自动执行命令的功能。

1. 从主窗口点击“AI 模型设置”，或在 AI 页签点击“模型设置”。
2. 填写兼容 Chat Completions 的**完整接口地址**（包括服务商要求的路径前缀和 `/chat/completions`）、模型 ID 和自己的 API Key。程序不附带模型账户或密钥，不自动选择模型。仅支持 Bearer Key 鉴权；不支持 Azure OpenAI 的 api-key/query-version 接口或仅支持 Responses 的接口。
3. 远程服务使用 HTTPS；明确配置为本机回环地址时允许 HTTP 和空密钥，例如 `http://localhost:1234/v1/chat/completions`。本地模型服务需自行安装运行。
4. “测试连接”使用当前填写但尚未保存的配置，只发送固定 Hello 示例与“请只回复 OK”任务；不发送截图或 OCR 原文。成功后点击“保存”。
5. 选择任务及“全部原文 / 选中文字”，按需校对左侧原文。自定义模式填写问题后点击“生成回答”。选中范围为空时不会自动改为发送全文。
6. 回答逐步显示，可停止、重新生成。生成结束或停止后可以编辑；Ctrl + C /“复制选中”保留窗口，“复制回答并完成”或回答框内 Ctrl + Enter 完成截图。在自定义问题框内 Ctrl + Enter 生成回答。

- 仅点击生成时发送当前范围内的文字、任务和自定义问题；不上传截图，不自动提交，不附带历史回答。截图文字和模型回答只在内存中处理，程序不把它们或密钥写入日志。
- 原文、选中范围、任务或问题变化时，取消旧请求并提示回答待更新；旧片段不会覆盖新结果。失败或停止时保留已有文字，部分回答提示可能不完整。原文保持独立，可随时校对。
- 单次输入最多 20,000 个 UTF-16 字符，问题最多 2,000 字符；回答接收上限 64,000 字符，总响应与事件也有限制。请求最长 120 秒，无自动重试、重定向跟随或工具执行。实际模型上下文、计费与输出限制以服务商为准。
- API Key 使用 DPAPI CurrentUser 加密存储在 `%LOCALAPPDATA%\Cut\ai.json`，与翻译、快捷键配置独立。更换电脑或 Windows 用户后重新填写；清空模型名称后保存可停用 AI。配置窗口打开时不会启动截图。
- AI 输出为可编辑纯文本，不自动打开链接、运行代码或访问文件。内容理解依赖 OCR 准确性，遇到识别错误请先校对原文再生成。
- 模型接口按官方 [Chat Completions 请求格式](https://developers.openai.com/api/reference/resources/chat/subresources/completions/methods/create) 和 [流式事件格式](https://developers.openai.com/api/reference/resources/chat/subresources/completions/streaming-events) 实现。服务商需要支持相应的 system/user 消息、文本输出与 SSE 流，亦接受兼容 JSON 回答；不保证所有标为“兼容”的服务或模型均支持。

本次未提供真实模型账户密钥，因此**未实测真实服务连通性、回答质量或计费**。自动测试使用模拟传输与合成文字，覆盖协议、加密存储、取消竞态及完整界面流程；配置实际服务后请先执行“测试连接”。

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
Start-Process .\bin\Release\net8.0-windows\win-x64\Cut.exe -ArgumentList '--translation-test' -Wait
Get-Content .\artifacts\translation-test.txt
Start-Process .\bin\Release\net8.0-windows\win-x64\Cut.exe -ArgumentList '--ai-test' -WindowStyle Hidden -Wait
Get-Content .\artifacts\ai-test.txt
```

`--self-test` 验证选区几何、跨屏像素、PNG 保存和快捷键冲突处理，不修改剪贴板。`--smoke-test` 短暂显示覆盖窗口，验证本机屏幕捕获、窗口位置并生成使用纯色测试背景的界面预览；不会把真实桌面保存到磁盘。

`--qr-test` 从项目根目录运行，读取 `Tests/Fixtures` 中由独立 ReportLab 编码器生成的固定样本，验证网址、中文、旋转、反色、小码、多码、去重、无二维码、取消、结果窗口动作和完整截图识别流程。窗口动作测试捕获复制内容与浏览器启动目标，不改变用户剪贴板或启动真实浏览器。该测试会短暂显示使用测试图片的窗口。

`--window-test` 验证窗口层叠命中、负坐标、屏幕边界裁剪、单击/拖拽阈值、选区调整和右键重选，并创建临时测试窗口检查真实 DWM 边界、窗口过滤及截图遮罩排除。包含显卡覆盖层回归测试，验证鼠标穿透、零透明度窗口被跳过，正常半透明窗口保留。仅保存合成图片预览，不保存真实桌面。

`--ocr-test` 使用真实离线模型识别 WPF 渲染的中英文、深色背景、小字号、标点、多行、宽图与跨块长句样本，记录忽略空白的字符错误率和冷/热耗时；检查空白图、取消、模型校验、阅读顺序、编辑/复制/撤销以及完整截图会话。复制操作通过注入回调验证，不替换用户的剪贴板；结果是固定合成样本成绩，不代表所有截图的准确率。

`--translation-test` 不访问网络、不使用真实密钥、不改动用户配置。使用模拟 HTTP 传输验证 Azure v3 协议、中英方向、Unicode/换行、仅发送文本、认证头、状态码和响应校验、超时/取消；使用真实 DPAPI 验证测试配置的加密存储。还验证原文译文编辑、选中翻译、复制命令、陈旧结果丢弃、配置窗口和连接测试流程，并保存合成界面预览。

`--ai-test` 使用模拟 HTTP/SSE、合成文字和真实 DPAPI，验证五种任务、完整/选中原文、分段 UTF-8、JSON 回答、服务错误、超时取消、迟到片段丢弃、编辑复制、配置窗口和完整截图会话；不访问外网、不覆盖用户剪贴板或用户配置。生成正常与紧凑布局的合成预览。

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
- `ITranslationService` / `AzureTranslationService`：可替换的翻译接口、Azure v3 请求与响应、超时取消和安全错误提示。
- `TranslationSettingsStore` / `TranslationSettingsWindow`：DPAPI 密钥加密、独立配置、连接测试和设置界面。
- `IAiService` / `CompatibleAiService`：任务提示、Chat Completions 文本请求、SSE/JSON 解析、取消、超时和响应大小限制。
- `AiAssistantPanel`：任务与范围选择、流式回答、陈旧结果防护、编辑复制。
- `AiSettingsStore` / `AiSettingsWindow`：独立模型配置、DPAPI 密钥加密、固定输入连接测试。
- `LinkService`：仅将 HTTP/HTTPS 网页交给系统默认浏览器。

## 手工验收

- 在微信、画图等程序粘贴截图，检查尺寸和画面。
- 保存对话框取消后仍可调整选区；保存失败后能重试。
- 双屏 100% / 150% 混合缩放、屏幕位于主屏左侧/上方、跨屏拖拽。
- 重复快捷键、连续截图、退出后重新启动，检查残留遮罩和快捷键释放。
- 框选实际网页上的二维码；在记事本粘贴识别文本，点击网页结果验证系统默认浏览器。

首版不含标注、滚动截图、贴图或自动更新。GDI 后端面向普通 SDR 桌面；HDR、受保护视频和独占全屏应用的捕获效果需要额外兼容性验证。Windows N/KN 精简媒体版本可能需要启用 Media Feature Pack 才能加载 OpenCV 的系统依赖。

发布包包含 ZXing.Net、PaddleOCR / RapidOCR 转换模型、ONNX Runtime、OpenCvSharp / OpenCV 及运行时。版本、来源和许可见 `THIRD-PARTY-NOTICES.txt`、`Models/manifest.json` 和 `Licenses`。
