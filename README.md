# 腕间音乐

Windows 原生桌面音乐控制器，通过 SteamVR Overlay 把控制面板固定在手柄对应的手腕位置。默认采用白色卡片、浅灰背景与蓝色控件；可在「设置 → 外观」开启「深色模式」，使用保留的深色紫色界面。桌面端与 VR 端使用同一份播放状态。

当前版本为 **v3.0.0**。真实 Windows 接口与 OpenVR 接入代码已实现；具体音乐软件版本、控制器和 Win10 / Win11 的组合仍需要实机验收。

## 直接运行

从 3.0 开始，「设置 → 自动更新」默认开启。程序启动时及运行期间每六小时检查一次本仓库的最新正式发行版，后台下载对应的安装包或便携包并验证 SHA256。下次以普通用户启动时自动更新并重新打开。关闭开关可暂停下载及安装。2.x 用户需手动升级到 3.0 一次。

在 GitHub 仓库的 **Releases → v3.0.0 → Assets** 中选择 Windows x64 安装版或免安装版。两者均包含 .NET 运行环境。

| 版本 | 文件 | 使用方式 |
| --- | --- | --- |
| ZIP 免安装版 | `WristMusic-3.0.0-win-x64-portable.zip` | 解压整个文件夹，双击 `WristMusic.exe` 即可使用 |
| EXE 安装版 | `WristMusic-3.0.0-win-x64-setup.exe` | 打开中文安装向导，选择目录后安装，支持开始菜单、可选桌面快捷方式与卸载 |

安装版默认安装到当前用户的 `%LOCALAPPDATA%\Programs\XingMengYu\WristMusic`，不要求管理员权限；也可以选择其他可写目录。通过 Windows「应用和功能」或开始菜单中的卸载入口卸载。卸载会保留用户设置，并尝试清理属于该安装目录的 SteamVR 自动启动注册；其他目录的便携版注册会保留。

两种版本共享 `%LOCALAPPDATA%\WristMusic` 中的用户设置。如果从便携版改用安装版，已经启用的 SteamVR 自动启动路径可以在「设置 → 启动与运行」中更新。

3.0.0 的 ZIP 和安装版仅包含程序、运行环境、手柄绑定和必要的第三方许可；不附带「开始使用」「使用说明」、验证报告、驱动参考报告或 PDB 调试符号。开发文档和验证资料保留在源码工作区。安装版覆盖升级时会清理旧版附带的上述文件，用户设置继续保留。

在源码目录中也可以打开 `artifacts/WristMusic-win-x64/WristMusic.exe`。免安装版无需单独安装 .NET；请保留整个目录，尤其是同目录的 `openvr_api.dll` 与 `Input` 手柄绑定文件夹。发布目录中的 `SHA256SUMS.txt` 提供两个文件的 SHA256 摘要。

1. 在「应用管理」中检查路径，未识别时点击「选择程序」指定音乐客户端主 exe。
2. 打开客户端并播放歌曲。若客户端有「系统媒体控制 / SMTC」选项，请开启。
3. 在「正在播放」右上角选择播放器，或使用「自动识别音乐软件」。自动模式过滤浏览器，从已知或已配置的播放器中优先选择正在播放的会话。
4. 启动 SteamVR，连接头显与两只手柄，再以 SteamVR 模式启动 VRChat。
5. 选择左 / 右手腕，将面板朝向头显。用另一只手柄瞄准，出现光点后按扳机。双击 B 显示面板，再次双击隐藏；抬腕显示默认关闭。
6. 桌面与 VR 进度条均支持点击跳转和拖拽；VR 按住扳机拖动、松开后确认跳转。音量条支持按住扳机滑动。桌面进度条也支持方向键。

## 控制范围

| 功能 | 实现与边界 |
| --- | --- |
| 网易云音乐 / QQ音乐 / 酷狗音乐 / 汽水音乐 / Spotify | 预置识别、路径配置、启动与正常关闭；每个客户端的媒体控制能力由其版本和设置决定 |
| 其他播放器 | 在「其他播放器」配置额外客户端的 exe 后可识别对应媒体会话 |
| 播放 / 暂停、上一首 / 下一首 | 首选 Windows SMTC；无媒体会话时尝试向所选播放器窗口发送媒体命令 |
| 标题、歌手、封面、歌曲进度 | 首选客户端向 Windows 发布的信息；网易云缺少时间轴时尝试读取客户端实际显示的播放时间；无法读取时显示 `--:--`，不模拟计时 |
| 跳转进度 | 根据媒体会话的能力标志启用，客户端拒绝指令时显示失败状态；辅助接口仅读取时间，不提供跳转 |
| 音量 | **Windows 默认输出设备的系统主音量**，不是独立播放器音量；设备切换后会重新读取 |
| 打开软件 | 默认打开当前播放器。可在设置开启「打开上次使用的播放器」 |
| 关闭软件 | 正常窗口关闭请求；客户端若设置为关闭到托盘，可能不会退出。可在设置开启「关闭音乐软件时直接终止进程」 |
| 全局媒体按键兼容 | 默认关闭，可在设置开启。由 Windows 选择接收的应用，不能保证目标或执行成功，不支持元数据 / 进度 |

桌面两套主题和 VR 面板均只保留「上一首、播放 / 暂停、下一首」三个播放按钮。

预置客户端**并不意味着所有版本均支持完整进度和封面**。未开放的能力会禁用。缺少 SMTC 时，可以使用兼容命令尝试基本播放控制；界面会说明该模式无法确认执行结果。

网易云音乐 3.1.41.205564 的原生媒体会话实测能提供歌曲名和歌手，但系统时间轴的当前位置、总时长均为 0、更新时间为 1601 年默认值，不支持系统进度跳转。程序会尝试以只读 UI Automation 获取客户端实际时间文本；界面隐藏时间时辅助读取可能不可用，不会编造计时。

### 网易云后台进度与跳转（可选插件）

在「设置 → 网易云进度兼容（可选）」点击「安装进度插件」，会从作者 GitHub 下载 **BetterNCM 1.3.4 x64 + InfLink-rs 3.4.0**，并校验固定 SHA256。安装前需从托盘菜单完全退出网易云。安装会在网易云目录添加 `msimg32.dll`，插件保存到 `C:\betterncm\plugins\InfLink-rs.plugin`；受保护目录需要 Windows 管理员授权。自动入口限制为作者已测试范围内的网易云 3.1.20～3.1.41 x64，不覆盖已有其他版本加载器或同名插件。

安装后重新打开网易云、播放歌曲，并在腕间音乐选择「网易云音乐」或「自动跟随系统」。收到真实时间轴后自动显示当前位置和总时长，开放进度条跳转；无需切回桌面。如果原生媒体会话与插件会话同时存在，会优先选择有效且支持跳转的网易云会话，自动模式仍跟随 Windows 当前播放器，不会从 QQ 或浏览器跳到网易云。

插件是明确选择后才安装的第三方组件。正常启动腕间音乐、运行 ZIP 免安装版、安装腕间音乐 EXE 均不自动安装插件。新建 BetterNCM 配置只启用此进度方案，禁用内置插件商店并留空自动更新源；已有配置保持原样。InfLink-rs 的 Discord 功能默认关闭，不属于本功能。安装失败时回滚本次新增文件；组件存在其他版本或配置了自定义 BetterNCM 目录时，请使用「插件说明」按作者步骤安装。

移除插件：完全退出网易云后，删除 `C:\betterncm\plugins\InfLink-rs.plugin`。若 BetterNCM 仅用于此插件，还可移除网易云目录中本方案添加的 `msimg32.dll`。不要删除其他用途的加载器或插件。腕间音乐卸载时保留网易云插件，避免影响客户端已有配置。

- [InfLink-rs 官方说明与兼容范围](https://github.com/apoint123/inflink-rs)
- [BetterNCM 1.3.4 官方发布](https://github.com/std-microblock/chromatic/releases/tag/1.3.4)
- 加载器 SHA256：`A7C77AF418D7940E63FAA58EA036FBA1F4BAAD497947109EA52ED78C8E86608F`
- InfLink-rs SHA256：`7D0B7B863B072A99B88EB614C788099315E690920889AACA22685D3663661468`（与 GitHub 发布摘要一致）。

进度计算也已处理缺少 EndTime 时改用 MaxSeekTime、默认或未来更新时间、暂停与倍速；缺少总时长时仍保留有效的当前位置。进度条数值与显示时间分离，防止界面控件把真实时间强制写回 0。

## 手腕面板

- 基于独立 OpenVR Overlay 进程，不需要修改 VRChat 文件。
- 面板跟随真实 SteamVR 手柄，而非 VRChat 虚拟角色骨骼。默认宽约 36 cm，尺寸可调为 50%～160%。
- 左右手切换、不透明度、抬腕显示、水平 / 高度 / 前后偏移与倾角可调整并自动保存。
- 抬腕显示默认关闭。开启后按面板朝向、与头显的距离和高度判定抬腕，采用不同的显示 / 隐藏阈值，以及稳定 120 ms 显示、稳定 450 ms 隐藏的缓冲，避免边界附近反复显隐。追踪短暂失效最多保留 250 ms；持续丢失后隐藏并取消未完成操作。
- 原生 Show / Hide 仅在显隐状态改变时调用，位置、大小和透明度仅在设置或佩戴手柄改变时提交；首次显示前先上传完整画面，减少重复提交与空纹理造成的闪烁。
- 「双击 B 显示 / 隐藏面板」默认开启。两次独立按下需间隔 90～450 ms；长按、断线重连和两只手各按一次不会合并成双击。面板显示时双击隐藏，隐藏时双击打开并返回音乐页；右上角「－」也可隐藏。抬腕模式中已自动隐藏的面板会直接打开，不会被误判为仍在显示。双击打开后暂不受抬腕角度影响，更改抬腕开关或重新连接后恢复自动判定。两路输入对同一次双击的重复报告仅短暂去重，连续两组双击可正常开关。
- 双击开关位于桌面「设置 → VR 显示与手柄」和 VR「设置 → 常规设置」。Touch 和 PICO Connect 的 `pico_controller` 类型预置右手 B，Index 支持任意一只手的 B；Vive 没有实体 B，默认不绑定系统 / 菜单键。连接时读取实际 SteamVR 手柄类型和驱动输入配置；未预置的类型明确提供 `/input/b` 点击源时，自动生成右手 B 和扳机默认绑定，不靠猜测驱动名称。SteamVR 中已有的用户自定义绑定仍优先，实机兼容性待验证。
- 另一只手柄沿本地 -Z 方向发射逻辑射线；面板中的光点是命中反馈。当前版本没有额外绘制贯穿场景的长激光线。
- SteamVR Input 使用独立的临时应用身份、明确的动作清单路径和相同应用身份的默认绑定，与「跟随 SteamVR 启动」的持久注册分开。B 动作未激活时，也尝试直接读取手柄模型中明确命名为 B 的组件，并合并该 B 组件实际报告的旧版按键掩码，避免兼容模式下组件位置有效、按下路径无数据时漏键；不把菜单 / 系统键猜成 B。两个输入通道的同一次双击只打开一次。扳机动作不可用时回退到传统 OpenVR 手柄状态。不同手柄的输入与追踪偏移需要实机确认。
- VR 画面为自适应高度的 WPF 离屏纹理：有有效进度时为 820×500；未读取到位置与有效时长时，隐藏整块进度 / 时间区域，把上一首、播放 / 暂停、下一首移至歌曲信息下方，并缩短至 820×405，减少约 19% 面板高度。重新获得进度后自动恢复。有效但不支持跳转的进度仍保留显示。
- 进入设置时恢复 820×500，返回播放后根据进度能力恢复相应尺寸。SteamVR 上传纹理与射线命中区域同步使用实际高度，缩短后的空白区域不保留点击范围。WPF 背景空闲时最高每秒重绘 8 次，操作期间临时提高到约 30 次，先处理输入再绘制状态。完整背景缓存在内存中，与光标合成后上传 D3D11 中转纹理，再以 GPU 整帧复制更新持续保留的共享纹理，经 `SetOverlayTexture` 提交。相同大小的连续刷新不会重建纹理，仅尺寸改变或断开连接时释放资源。
- 蓝色 / 紫色圆圈光标现在直接合成到面板的同一张纹理中，取消额外的原生光标 Overlay，解决独立层不显示时整块面板没有任何光标的回归问题。位置更新目标保持 90 Hz；每次只恢复上一个光标区域、绘制新的 64×64 圆圈，并局部更新 GPU 纹理，不等待 WPF 背景重绘。采用带透明度的亚像素插值，减少细微移动时的台阶感。光标位置直接使用按钮点击的命中点；离开面板或追踪失效时恢复背景，避免残影。扳机状态暂不可用时仍可显示有效的瞄准位置。实际更新频率受 Windows 调度、机器负载和串流帧率影响，头显内效果待验收。
- 已移除连续刷新时的 `SetOverlayRaw` 调用。旧路径可能与 OpenVR 已报告的「更新原始图像时旧画面短暂消失」有关，本次改用 GPU 纹理；具体设备上的闪烁是否消除仍需验收。[Valve 接口说明建议优先使用纹理提交](https://github.com/ValveSoftware/openvr/wiki/IVROverlay%3A%3ASetOverlayRaw)，相关问题见 [OpenVR #772](https://github.com/ValveSoftware/openvr/issues/772)。
- 未安装 SteamVR、头显 / 手柄丢失或运行时退出时显示真实状态；运行时连接每 8 秒重试，也可手动重新连接。
- 面板右上角齿轮可打开 VR 内设置，支持佩戴位置、大小、不透明度、偏移、倾角、抬腕显示、深色模式、启动选项和测试功能；「关于」页显示与桌面相同的版本、作者、交流群和免费说明。设置与桌面同步，自动保存。点击「返回播放」回到原音乐面板。
- VR 进度拖动期间只预览位置，松开扳机确认一次跳转；丢失追踪或面板隐藏时取消未完成的拖动。

需要 **PC 上的 SteamVR 运行环境**。独立运行的 Quest 版 VRChat，以及未经过 SteamVR 的运行方式，不在此版本支持范围。

Pico 4 + PICO Connect 的绑定已按 [PICO 官网](https://www.picoxr.com/cn/software/pico-link) 10.6.6 Windows 安装包内的驱动配置核对：`pico_phoenix_controller_profile.json` 声明 `pico_controller` 和右手 `/input/b`；右手模型的 `button_b` 组件使用 `/input/b/click`、旧版按键编号 1。程序内置这一类型的默认绑定，因此手柄在程序启动后才连接也能匹配；实体 B 组件读取继续作为兼容路径。这里只核对了官方文件并运行软件测试，未完成 Pico 4 实机按键或画面验收。来源与摘要记录见 `artifacts/validation/pico-connect-driver-reference.json`，官方驱动文件不随腕间音乐发行包分发。

## 平台与设置

技术目标：**Windows 10 1809（17763）及以上 / Windows 11，x64**。使用 .NET 10 WPF 与 Windows 媒体控制 API。当前测试不代表已经覆盖两个系统的全部版本；最终应在目标 Windows 构建和头显上验收。

设置位于 `%LOCALAPPDATA%\WristMusic\settings.json`，修改后自动保存。读取损坏文件时恢复默认设置，并显示诊断。异常日志写入系统临时目录 `WristMusic-error.log`。

VR 诊断日志为 `%LOCALAPPDATA%\WristMusic\Logs\steamvr.log`，记录 GPU 路径、连接错误、手柄型号与 B 组件、动作绑定状态、B 按下 / 松开、双击识别和面板显隐变化。单文件超过 1 MB 时保留一个旧文件再轮换，不记录媒体内容。桌面「手腕面板」页会显示当前 B 键通道是否可用。更新时先退出旧版，再完整解压 ZIP 或运行安装程序；旧进程继续运行时仍会使用旧代码。

升级旧版设置时，一次性关闭原有的抬腕显示默认值，其他佩戴与外观设置保留。升级后手动开启抬腕显示或关闭双击 B 的选择会正常保存，不会在下次启动时重置。

「深色模式」切换后立即生效，并自动保存；重新启动会恢复上次选择。旧版设置没有主题选项时默认使用白色界面。两套主题均使用自定义滚动条，手腕面板也会同步切换配色。白色版播放页采用独立的播放卡片与播放器开关区域，开启深色模式后保留原来的播放页布局。

「设置 → 启动 SteamVR → 启动腕间音乐时启动 SteamVR」**默认关闭**，与下方的「跟随 SteamVR 启动」互相独立。关闭时，打开腕间音乐会等待 SteamVR，自行启动 SteamVR 后再自动连接；原有设置升级后也保持关闭。开启后，下次打开腕间音乐时允许尝试启动一次 SteamVR；SteamVR 后续退出时不会反复拉起，手动「重新连接」可再次尝试。

默认连接使用 OpenVR 的 `VRApplication_Background` 类型，即使运行时在检查后退出，也不会通过初始化重新启动它。自动启动状态读取同样先确认 SteamVR 已运行；未运行时仅显示等待提示，不启动设置进程。[Valve 的 API 文档说明了 Background 初始化不启动 SteamVR 的行为。](https://github.com/ValveSoftware/openvr/wiki/API-Documentation)

「设置 → 测试功能」**默认关闭**。「显示当前累计听歌时长」也默认关闭；同时开启两项后，在原有桌面 / VR 音乐面板底部显示「累计听歌 HH:MM:SS」。统计所选媒体会话在该功能开启后的播放时间，暂停、媒体会话断开、功能关闭和休眠间隔不累计；切歌 / 跳转不把歌曲位置差额加入时长。累计值随设置保存，播放期间约每 30 秒保存一次，正常关闭程序也会保存。关闭测试功能隐藏显示并停止统计，保留已累计时长。

在「设置 → 启动与运行」开启 **跟随 SteamVR 启动**，下次启动 SteamVR 时会自动运行腕间音乐，桌面窗口最小化到任务栏。默认关闭；开关会向 SteamVR 注册应用并设置自动启动标志，回读确认成功后才保存。关闭开关会取消后续自动启动，当前程序继续运行。

读取、修改跟随启动设置需先运行 SteamVR；这些设置操作本身不会启动 SteamVR。

此功能通过 OpenVR 的应用管理接口实现，应用以 dashboard overlay 身份注册。配置过程在独立 Utility 进程中执行，不占用或重建正在运行的手腕 Overlay 连接；Utility 接口不需要加载头显驱动。[Valve 的接口定义说明了这两类 API 的用途及自动启动要求。](https://github.com/ValveSoftware/openvr/blob/master/headers/openvr.h)

未安装 SteamVR、注册失败或设置无法确认时会显示原因，并保留上次确认的开关状态。点击「重新检测」可以从 SteamVR 重新读取状态，也会尊重在 SteamVR 内修改的自动启动选项。便携版移动目录后，手动运行新目录里的程序，再点击出现的「更新启动路径」。注册清单保存在 `%LOCALAPPDATA%\WristMusic\SteamVR\WristMusic.vrmanifest`。程序使用单实例保护，避免手动启动与 SteamVR 自动启动产生重复窗口。

全局快捷键可在程序内关闭：

| 快捷键 | 操作 |
| --- | --- |
| Ctrl + Alt + Space | 播放 / 暂停 |
| Ctrl + Alt + ← | 上一首 |
| Ctrl + Alt + → | 下一首 |
| Ctrl + Alt + O | 启用 / 关闭手腕面板 |

快捷键占用时会在状态栏提示。程序退出时注销快捷键并销毁 Overlay。

## 开发与构建

需要 .NET 10 SDK。可使用已有 SDK，或运行 `scripts/bootstrap.ps1` 把微软官方 SDK 下载到项目 `.tools`，不安装到系统目录。

```powershell
./scripts/bootstrap.ps1
./scripts/build.ps1 -Test -Portable
./scripts/run.ps1
```

构建两个发行版本需要 [Inno Setup](https://jrsoftware.org/)（本项目使用 7.1.1）。

```powershell
# 安装 Inno Setup 并将 ISCC.exe 所在目录加入 PATH
./scripts/build.ps1 -Test -Portable -Installer
```

也可将编译器放在项目 `.tools/inno` 内。已有发布目录时，可以单独运行 `scripts/build-installer.ps1` 重建 ZIP 与 EXE。安装向导脚本位于 `installer/WristMusic.iss`，采用稳定的产品 ID 支持覆盖安装。`scripts/test-installer.ps1` 可在项目内的独立临时目录验证实际安装、开始菜单快捷方式、安装文件完整性、已安装程序运行、覆盖修复与卸载；测试会临时创建当前用户的安装记录和测试快捷方式，并在结束时卸载清理。

`-Portable` 输出自包含的 Windows x64 目录与 zip；首次构建会从官方 NuGet 源获取 Windows SDK 引用和运行时。OpenVR 的 C# 绑定与 x64 原生 DLL 已随源码固定版本保存，来源与授权见 `THIRD-PARTY-NOTICES.md`。

`--self-test` 会检查模拟播放流程、设置读写恢复、主题即时切换与保存、自动启动的注册 / 取消 / 失败恢复逻辑、单实例保护、进度边界、抬腕 / 射线几何和离屏按钮命中，并在 `artifacts/validation` 生成白色 / 深色两套页面、等待状态和 VR 面板 PNG（深色文件名带 `-dark`）。自动启动的修改操作使用模拟 SteamVR 后端测试，不会改变本机的自动启动配置。`--live-check` 追加只读 Windows 媒体 / 音量接口与 OpenVR DLL 探测，不会发送播放、音量或关闭客户端的指令。

后台冻结回归：`--self-test --background-soak` 将桌面消息滞留测试延长至 120 秒。在独立 STA 线程保留一条原生消息，确认旧的 Input 优先级定时器停止、新调度持续回调，清除消息后旧定时器恢复；不模拟鼠标点击或唤醒用户窗口。测试还覆盖回调合并、停止 / 重启 / 退出和离屏命令可用状态。这是后台调度机制的复现，不替代 Pico 4 + PICO Connect 的长时间实机验收。

VR、进度时钟和媒体轮询使用 `BackgroundUiTimer`，由线程池定时器主动唤醒原 UI 线程的 Loaded 优先级回调，每个定时器最多一项待处理工作。WPF 控件与 OpenVR 调用仍在原线程访问。命令可用状态显式通知，不依赖桌面焦点或鼠标触发重查询。连续三秒未完成的回调会将等待阶段 / 渲染阶段写入 `%LOCALAPPDATA%\WristMusic\Logs\steamvr.log`，恢复时补记一次；不会通过抢焦点或模拟点击恢复。诊断不能强行中断卡住的驱动调用。

主要源码：

```text
src/WristMusic/
  MainWindow.xaml                 桌面外壳与导航
  MainViewModel.cs                共享状态、指令、能力判断
  Views/                         播放 / 应用 / 手腕 / 快捷键 / 设置及 VR 面板
  Services/MediaService.cs        Windows SMTC 会话与曲目信息
  Services/PlayerService.cs       播放器发现、启动、关闭、兼容命令
  Services/VolumeService.cs       Windows Core Audio 主音量
  Services/OverlayService.cs      OpenVR 生命周期、追踪、抬腕、射线、纹理
  Services/BackgroundUiTimer.cs   不依赖桌面输入的后台刷新与停顿诊断
  Services/ThemeService.cs        即时主题切换
  Services/SteamVrAutoStartService.cs  SteamVR 原生自动启动注册、确认及独立设置进程
  Services/SingleInstanceGate.cs  防止手动与自动启动产生重复窗口
  Themes/Light.xaml              默认白色主题
  Themes/Dark.xaml               保留的深色紫色主题
  Themes/Scrolling.xaml          两套主题共用的自定义滚动条
  Diagnostics/SmokeTests.cs       自动检查与页面渲染
```

## 验收状态

本次开发环境已完成：Release 编译、自动流程检查、桌面五页与 VR 面板渲染、Windows SMTC 和 Core Audio 的只读探测，以及 OpenVR DLL 加载。详细结果见 `artifacts/validation/results.json`。

已在网易云 3.1.41.205564 上安装 BetterNCM 1.3.4 x64 与 InfLink-rs 3.4.0，实测取得非零真实进度、总时长和跳转能力；播放时递增、暂停后冻结、跳转目标与恢复原歌曲位置及暂停状态均已验证通过。插件运行于网易云进程，腕间音乐仍通过系统 SMTC 接口控制，后台无需前台窗口。结果摘要见 `artifacts/validation/netease-bridge-results.json`。

## 接口依据

- [Microsoft：Windows 系统媒体会话管理](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssessionmanager)
- [Microsoft：调整媒体播放位置，参数单位为 ticks](https://learn.microsoft.com/en-us/uwp/api/windows.media.control.globalsystemmediatransportcontrolssession.trychangeplaybackpositionasync)
- [Valve：OpenVR 官方仓库](https://github.com/ValveSoftware/openvr)
- [Valve：相对追踪设备固定 Overlay](https://github.com/ValveSoftware/openvr/wiki/IVROverlay%3A%3ASetOverlayTransformTrackedDeviceRelative)
- [Microsoft：Windows 上的 .NET 安装与平台要求](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [WPF 官方 Dispatcher 源码：原生输入门控与优先级调度](https://github.com/dotnet/wpf/blob/main/src/Microsoft.DotNet.Wpf/src/WindowsBase/System/Windows/Threading/Dispatcher.cs)

## 开源许可

Copyright © 2026 星梦屿工作室。腕间音乐的原创代码与资源采用 [GNU GPL v3.0（GPL-3.0-only）](LICENSE)。第三方组件的来源和许可见 [第三方声明](THIRD-PARTY-NOTICES.md)。
