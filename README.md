# BUCK 计算器

中文 Windows 桌面工具，用于降压电源的稳态参数初选与候选器件复核。填写输入电压范围、输出规格、纹波预算和元件容差，即可查看电感、电容、电流应力及工作模式。

![BUCK 计算器界面](docs/screenshot.png)

## 下载与运行

从 GitHub Release 下载 Windows x64 安装包，双击 MSI 完成安装，支持 Windows 10/11 64 位。安装后可从桌面或开始菜单启动程序。[下载最新版本](https://github.com/Epiphany-Lucky/buck-calculator/releases/latest)

| 附件 | 用途 |
|---|---|
| `BuckCalculator-v1.1.0-win-x64.msi` | 中文 Windows 安装程序 |
| `SHA256SUMS.txt` | MSI 安装包的 SHA-256 校验值 |

程序为当前 Windows 用户安装，默认位置为 `%LocalAppData%\Programs\BuckCalculator`。安装过程创建桌面和开始菜单快捷方式，Windows“设置 → 应用 → 已安装的应用”中提供卸载入口。升级沿用原安装目录。

在 PowerShell 中执行以下命令可查看本地下载文件的校验值，并与 `SHA256SUMS.txt` 比较。

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '.\BuckCalculator-v1.1.0-win-x64.msi'
```

## 软件内更新

程序在启动时及运行期间每 6 小时查询一次 GitHub 最新稳定版 Release。发现新版本后显示更新提示，也可以通过界面中的“检查更新”手动查询。

选择下载安装后，程序下载对应的 Windows x64 MSI，并用 Release 提供的 SHA-256 校验下载内容。下载和校验完成后，更新助手等待当前程序退出，运行 Windows Installer 完成升级，随后从安装目录重新启动应用。更新检查采用客户端定期轮询，联网状态和 GitHub API 可用性会影响检查时间。

## 主要功能

- 按输入电压上下限计算占空比范围。
- 根据目标电感纹波与负容差计算标称电感需求，自动选择标准值，支持填写候选电感。
- 计算最坏工况电感纹波、峰值、谷值、RMS 电流及上下管 RMS 电流。
- 纳入输出电容有效容量折减和 ESR，计算电容需求及保守纹波估算，支持填写候选电容。
- 计算输入电容的一阶容量需求和纹波 RMS 电流，并检查输入区间内的最坏占空比。
- 检查额定负载与指定工作负载的 CCM 适用性，显示满载最坏工况电感电流波形。
- 导出包含输入参数、计算结果和假设说明的 Markdown 或 CSV 报告。

## 输入参数

| 参数 | 单位 | 含义 |
|---|---|---|
| 最低／最高输入电压 | V | 实际输入电压范围，最低输入必须高于输出 |
| 输出电压 | V | 期望稳态输出电压 |
| 最大输出电流 | A | 元件初选所用额定负载 |
| 工作负载电流 | A | 用于检查指定负载下的导通模式 |
| 开关频率 | kHz | 固定开关频率 |
| 电感纹波目标 | % | 电感电流峰峰值占额定输出电流的比例 |
| 输出纹波预算 | mVpp | 稳态输出纹波允许峰峰值 |
| 电感负容差 | % | 标称电感到最小电感的折减比例 |
| 电容容量折减 | % | 标称输出电容到有效容量的折减比例 |
| 输出电容 ESR | mΩ | 输出电容组合的等效串联电阻 |
| 候选电感 | µH | 填 `0` 时自动选择，正数用于复核 |
| 候选输出电容 | µF | 填 `0` 时自动选择，正数用于复核 |
| 输入纹波预算 | mVpp | 输入电容容量估算使用的允许纹波 |

容量折减应结合器件的直流偏压、温度、容差与老化数据填写。电感负容差应结合实际电流偏置下的最小有效电感填写。

## 计算模型

程序采用理想、固定频率、连续导通模式的 BUCK 稳态模型，电感电流为三角波。所有下列公式使用 SI 单位。界面中的 kHz、µH、µF、mΩ 和 mV 会在计算时转换。

$$
D=\frac{V_o}{V_{in}},\qquad L_{min}=L_{nom}(1-t_L)
$$

$$
\Delta I_L=\frac{V_o(1-D)}{L_{min}f_s},\qquad I_{pk}=I_o+\frac{\Delta I_L}{2}
$$

$$
I_{valley}=I_o-\frac{\Delta I_L}{2},\qquad I_{L,rms}=\sqrt{I_o^2+\frac{\Delta I_L^2}{12}}
$$

$$
C_{eff}=C_{nom}(1-t_C),\qquad \Delta V_{o,estimate}=\frac{\Delta I_L}{8f_sC_{eff}}+\Delta I_L\cdot ESR
$$

其中输出纹波采用容量项与 ESR 项之和作为保守工程估算。电感纹波最坏工况取最高输入与最小电感，CCM 临界电流为纹波峰峰值的一半。电感与输出电容公式参照厂商 BUCK 功率级设计资料。[ADI 功率级设计说明](https://www.analog.com/en/resources/app-notes/2022/11/22/06/53/buck-power-stage-design-equations.html)

$$
C_{in,eff}\geq\frac{I_oD(1-D)}{f_s\Delta V_{in}},\qquad I_{Cin,rms}=\sqrt{I_o^2D(1-D)+\frac{D\Delta I_L^2}{12}}
$$

输入电容容量公式采用矩形电流近似，容量需求在 D=0.5 或区间端点处达到最大。输入 RMS 电流额外保留电感三角纹波项，最大值在完整输入范围内求取。上下管 RMS 电流分别按导通占空比加权。输入电容实际选型还需核对有效容量、ESR、纹波电流额定值和输入源阻抗。[TI 输入与输出电容选型说明](https://www.ti.com/lit/an/slta055/slta055.pdf)

当指定负载低于 CCM 临界电流时，界面提示 DCM，并说明所显示额定工况计算的适用条件。实际轻载波形取决于控制器的二极管仿真、PFM 或强制连续导通策略。器件压降、寄生参数、磁芯损耗、热设计、环路稳定性和负载瞬态需要结合具体器件进一步核验。

## 默认算例

默认输入范围为 9–16 V，输出为 5 V / 2 A，频率为 200 kHz，电感纹波目标为 30%，电感负容差为 20%，输出纹波预算为 20 mVpp，容量折减为 20%，ESR 为 5 mΩ。

选择候选电感 33 µH 时，最小电感为 26.4 µH，最高输入下电感纹波约为 0.6510 App，峰值约为 2.3255 A，RMS 约为 2.0088 A。自动电感选择会将负容差纳入目标复核，因此其选值可高于 33 µH。

保持全部默认参数并自动选值时，选择 39 µH 电感与 27 µF 输出电容，预计输出纹波约为 18.6943 mVpp。

## 从源码构建

开发环境使用 Windows 和 .NET 10 SDK。项目使用 WPF 及 .NET 标准库。

```powershell
git clone https://github.com/Epiphany-Lucky/buck-calculator.git
Set-Location -LiteralPath '.\buck-calculator'
dotnet run --project '.\src\BuckCalculator\BuckCalculator.csproj'
```

执行计算、更新和界面验证，并构建安装包。

```powershell
.\scripts\build.ps1 -Version 1.1.0
```

构建脚本先运行计算、报告与更新检查，再将 Windows x64 自包含应用封装进 WiX 6.0.2 MSI。程序运行所需的 .NET 运行时包含在应用中。输出位于 `artifacts`，发布附件为 MSI 和 SHA-256 文件，界面验证结果位于 `artifacts/smoke-test`。[Microsoft 自包含与单文件部署说明](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview)

加入 `-VerifyInstall` 可进一步执行隔离安装验证。脚本生成内部使用的 1.0.9 测试版本，在 `artifacts/installer-test/app` 安装后调用软件更新助手升级到目标 MSI，检查原安装目录保留、旧产品注销、自动重启、降级阻止和卸载清理。检测到现有用户安装或快捷方式时会记录跳过结果。

```powershell
.\scripts\build.ps1 -Version 1.1.0 -VerifyInstall
```

也可以单独执行计算验证。

```powershell
dotnet run --project '.\tests\BuckCalculator.Checks.csproj' --configuration Release
dotnet run --project '.\tests\BuckCalculator.UpdateChecks.csproj' --configuration Release
```

## GitHub 自动发布

推送到 `main` 会运行构建验证并保留可下载的 Actions 安装包。推送 `v*` 标签会在验证成功后创建 GitHub Release，并上传 MSI 和校验文件。已存在的同名 Release 会更新安装附件。

```powershell
git tag v1.1.0
git push origin v1.1.0
```

## 源码使用

仓库提供计算实现与构建流程，方便检查结果。源码授权以仓库后续明确提供的许可证为准。
