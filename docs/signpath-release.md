# 学习桌面 Windows 构建与 SignPath 签名

本流程构建学习桌面的 Windows x64 ZIP，通过 GitHub Actions 提供可下载的产物。它沿用 `scripts/build-windows-package.ps1`，包版本取自 `desktop/package.json`。SignPath 的签名目标是产品自己的运行入口 `Xdc.NativeDesktop.exe`。随包分发的 Electron 使用锁定版本的原始上游文件。

## 构建未签名包

在默认分支包含此工作流后，打开 GitHub **Actions → Windows release artifacts → Run workflow**。`sign` 默认关闭，可在配置 SignPath 之前运行。GitHub 托管的 Windows 运行器从所选提交安装依赖、运行测试并构建。运行成功后，从该次运行的 **Artifacts** 下载 `unsigned-windows-bundle`，内含 Windows ZIP 与配套的 `.sha256`。每次运行的产物都可按源提交追溯，GitHub Releases 页面由维护者另行管理。

## 申请与配置 SignPath

使用 SignPath Foundation 正式签名需要其接受该开源项目。申请所依据的源码应采用 OSI 认可的开源许可证，并提供与申请签名格式一致的现有可下载发行。维护者应确认发布许可和代码权属，保留 D32gang 上游以及 PeiPei233 参考实现和 WPS 适配器的署名及适用许可。现有 README 和 npm 元数据记录了 MIT 来源，公开发布前仍需核对适用于整个仓库和分发包的许可材料。 [基金会条款](https://signpath.org/terms)

向基金会提交的申请应包含项目地址、下载页、许可和公开的代码签名政策。由维护者确认审查角色、隐私政策以及 GitHub 和 SignPath 的双重验证。每次正式签名由授权审批人在 SignPath 人工批准。 [申请入口](https://signpath.org/apply)

收到批准后，在 SignPath 为该仓库配置项目和正式签名策略。可信构建系统选 GitHub.com，来源绑定实际仓库地址及允许签署的发布分支。开启来源校验和人工审批，授予 CI 用户提交此策略的权限。签名使用基金会为该项目提供的正式证书。 [SignPath 项目配置](https://docs.signpath.io/projects) [GitHub 集成](https://docs.signpath.io/trusted-build-systems/github)

在 GitHub 仓库的 **Settings → Secrets and variables → Actions**，填写下列已批准项目的实际值。

| 类型 | 名称 | 值 |
| --- | --- | --- |
| Variable | `SIGNPATH_ORGANIZATION_ID` | SignPath 组织 ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | 学习桌面 SignPath 项目的 slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | 正式签名策略的 slug |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | 下文产物配置的 slug |
| Secret | `SIGNPATH_API_TOKEN` | 有该策略提交权限的 CI API 令牌 |

仓库变量和 Secret 需要由有权限的维护者在 GitHub 设置页配置。令牌填入 Secret，审批人在 SignPath 登录并完成审批。

### SignPath 产物配置

工作流使用 GitHub 上传动作创建 ZIP `unsigned-windows-apphost`，其根目录直接包含 `Xdc.NativeDesktop.exe`。在 SignPath 的 Artifact configuration 中使用以下配置。`productName` 和 `productVersion` 区分大小写，工作流从该 EXE 的 Windows 版本资源读取值。`ProductVersion` 可能带源提交后缀，应使用读取到的完整值。 [SignPath 参数和 XML 语法](https://docs.signpath.io/artifact-configuration/syntax#user-defined-parameters) [PE 元数据检查](https://docs.signpath.io/artifact-configuration/reference#file-metadata-restrictions)

```xml
<artifact-configuration xmlns="http://signpath.io/artifact-configuration/v1">
  <parameters>
    <parameter name="productName" required="true" />
    <parameter name="productVersion" required="true" />
  </parameters>
  <zip-file>
    <pe-file path="Xdc.NativeDesktop.exe"
             product-name="${productName}"
             product-version="${productVersion}">
      <authenticode-sign />
    </pe-file>
  </zip-file>
</artifact-configuration>
```

## 构建并验收签名包

1. 从获准签署的版本手动运行 **Windows release artifacts**，打开 `sign`。该模式要求上述配置齐全。随后在 SignPath 打开此次签名请求，核对源提交、文件名和产品元数据，再由授权审批人批准。工作流等待审批和签名的上限为 60 分钟。
2. 工作流下载并验证签名返回的 EXE，替换原构建包里的宿主，再生成最终 ZIP 与 SHA-256 文件。成功后下载 **`signed-windows-bundle`**。产物内的 `release.json` 记录源提交和宿主签名，外层 GitHub artifact 是下载容器，先从中取出应用 ZIP 与校验文件。
3. 确认包的实际 SHA-256 和配套文件一致，完整解压应用 ZIP，用 Windows SDK SignTool 验证提取出的 EXE。确认智能应用控制处于 `On`，使用单独的 `--profile` 目录启动。隔离配置供验收使用。

```powershell
Get-FileHash -Algorithm SHA256 -LiteralPath '.\Xdc-Learning-Desktop-0.1.0-win-x64.zip'
Get-Content -LiteralPath '.\Xdc-Learning-Desktop-0.1.0-win-x64.zip.sha256'
Get-MpComputerStatus | Select-Object SmartAppControlState

# 在应用 ZIP 解压后的目录中运行
$app = '.\desktop\native-bin\Xdc.NativeDesktop.exe'
signtool.exe verify /pa /all /tw /v $app
Get-AuthenticodeSignature -LiteralPath $app | Select-Object Status, SignerCertificate, TimeStamperCertificate
```

确认签名有效、存在时间戳且发布者符合已批准的 SignPath 配置后，在同一终端继续。

```powershell
$testProfile = Join-Path ([IO.Path]::GetTempPath()) ('xdc-signpath-test-' + [guid]::NewGuid().ToString('N'))
& $app --profile $testProfile
```

检查动态背景变化能否实时透过模糊层，在布局中调整圆角，并检查四个角的透明区域。还需验证业务进程启动、登录、课程读取和课件下载，当前随包 Electron 缺少有效的 Authenticode 签名。隔离配置不会登记登录自启动，需另在测试设备的正式安装场景核对。完成这些验收后，再选定这一份签名包发布。 [Microsoft 智能应用控制及事件日志](https://learn.microsoft.com/windows/apps/develop/smart-app-control/test-your-app-with-smart-app-control#checking-event-logs)
