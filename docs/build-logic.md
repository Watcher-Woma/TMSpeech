# TMSpeech 项目构建逻辑分析

## 一、解决方案整体结构

解决方案包含 **7 个项目**，分为 3 层 + 插件层：

```
TMSpeech.sln
├── TMSpeech.Core                          (类库 - 核心层)
├── TMSpeech.GUI                           (类库 - 界面层，依赖 Core)
├── TMSpeech                               (WinExe - 入口，依赖 GUI)
└── Plugins/                               (解决方案文件夹)
    ├── TMSpeech.AudioSource.Windows       (类库 - 音频源插件)
    ├── TMSpeech.Recognizer.SherpaOnnx     (类库 - ONNX 识别器插件)
    ├── TMSpeech.Recognizer.SherpaNcnn     (类库 - NCNN 识别器插件)
    └── TMSpeech.Recognizer.Command        (类库 - 命令行识别器插件)
```

**依赖关系**：

```
TMSpeech (WinExe)
  └─→ TMSpeech.GUI
        └─→ TMSpeech.Core

插件（独立编译，运行时动态加载）：
  所有插件 ──引用──→ TMSpeech.Core (Private=false, ExcludeAssets=runtime)
```

---

## 二、全局构建配置

### `src/Directory.Build.props`

所有 `src/` 下的项目共享此配置：

```xml
<Project>
    <PropertyGroup>
        <Nullable>enable</Nullable>
        <AvaloniaVersion>11.0.9</AvaloniaVersion>
    </PropertyGroup>
    <ItemGroup>
        <PackageReference Include="GitVersion.MsBuild" Version="5.12.0" PrivateAssets="all"/>
    </ItemGroup>
</Project>
```

| 配置项 | 值 | 说明 |
|--------|-----|------|
| Nullable | enable | 全局启用可空引用类型 |
| AvaloniaVersion | 11.0.9 | 统一 Avalonia 版本号 |
| GitVersion.MsBuild | 5.12.0 | 语义化版本自动生成 |

### `GitVersion.yml`

```yaml
mode: ContinuousDelivery
tag-prefix: '[vV]?'
increment: Inherit
```

- 使用 **ContinuousDelivery** 模式，根据 Git 提交历史自动生成版本号
- 版本标签前缀支持 `v` 或 `V`（如 `v1.0.0`）
- 版本递增策略为 Inherit（继承分支版本）

---

## 三、各项目构建细节

### 1. TMSpeech.Core（核心类库）

| 配置项 | 值 |
|--------|-----|
| TargetFramework | `net6.0` |
| 输出类型 | 类库 |
| 项目引用 | 无（最底层） |

**NuGet 依赖**：

| 包名 | 版本 | 用途 |
|------|------|------|
| Downloader | 3.2.1 | 资源下载 |
| SharpCompress | 0.38.0 | 压缩包解压 |

### 2. TMSpeech.GUI（界面类库）

| 配置项 | 值 |
|--------|-----|
| TargetFramework | `net6.0` |
| 输出类型 | 类库 |
| 项目引用 | TMSpeech.Core |

**NuGet 依赖**：

| 包名 | 版本 | 用途 |
|------|------|------|
| Avalonia | 11.0.9 | UI 框架 |
| Avalonia.Controls.ColorPicker | 11.0.9 | 颜色选择器 |
| Avalonia.Themes.Fluent | 11.0.9 | Fluent 主题 |
| Avalonia.Fonts.Inter | 11.0.9 | Inter 字体 |
| Avalonia.ReactiveUI | 11.0.9 | MVVM 框架 |
| Avalonia.Diagnostics | 11.0.9 | 调试工具（仅 Debug） |
| Avalonia.Xaml.Behaviors | 11.0.10.9 | XAML 行为库 |
| MessageBox.Avalonia | 3.1.5.1 | 消息框 |
| ReactiveUI.Fody | 19.5.41 | 属性通知织入 |

### 3. TMSpeech（主程序入口）

| 配置项 | 值 |
|--------|-----|
| TargetFramework | `net6.0`（Windows: `net6.0-windows10.0.17763.0`） |
| 输出类型 | `WinExe` |
| SelfContained | `true`（自包含部署） |
| 项目引用 | TMSpeech.GUI |
| 应用图标 | `Resources\tmspeech-circle.ico` |

**NuGet 依赖**：

| 包名 | 版本 | 用途 |
|------|------|------|
| Avalonia.Desktop | 11.0.9 | 桌面平台支持 |
| DesktopNotifications.Avalonia | 1.3.1 | 桌面通知 |
| nulastudio.NetBeauty | 2.1.4.3 | 发布包结构优化 |

### 4. 插件项目（共同特征）

所有插件项目共享以下关键配置：

```xml
<EnableDynamicLoading>true</EnableDynamicLoading>

<ProjectReference Include="..\..\TMSpeech.Core\TMSpeech.Core.csproj">
    <Private>false</Private>
    <ExcludeAssets>runtime</ExcludeAssets>
</ProjectReference>

<None Update="tmmodule.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</None>
```

| 配置项 | 值 | 说明 |
|--------|-----|------|
| EnableDynamicLoading | true | 启用动态加载支持，避免锁定 DLL |
| Private | false | 阻止 Core.dll 被复制到插件输出目录 |
| ExcludeAssets | runtime | 排除运行时资产，避免与主程序 Core 版本冲突 |
| tmmodule.json | PreserveNewest | 插件元数据文件始终复制到输出 |

**各插件特有依赖**：

| 插件 | NuGet 依赖 | 版本 |
|------|-----------|------|
| AudioSource.Windows | NAudio.Wasapi | 2.2.1 |
| Recognizer.SherpaOnnx | org.k2fsa.sherpa.onnx | 1.12.19 |
| Recognizer.SherpaNcnn | org.k2fsa.sherpa.ncnn | 2.1.13 |
| Recognizer.Command | 无额外依赖 | — |

---

## 四、核心构建逻辑：插件自动编译

### BuildPlugins Target（构建后触发）

```xml
<Target Name="BuildPlugins" AfterTargets="Build" Condition="'$(BuildPlugins)'=='true'">
```

**执行流程**：

1. 遍历 `../Plugins/*/*.csproj`，收集所有插件项目
2. 依次调用 MSBuild 编译每个插件
3. 将每个插件输出到主程序目录下的 `plugins\{PluginName}\`
4. 删除各插件输出目录中重复的 `TMSpeech.Core.*` 文件

```xml
<ItemGroup>
    <PluginProjects Include="..\Plugins\*\*.csproj" OutPutDir="%(Filename)" />
</ItemGroup>

<MSBuild Projects="@(PluginProjects)" Targets="Build"
         Properties="OutDir=$(PluginsOutputDirAbs)\%(OutPutDir)" />

<ItemGroup>
    <FilesToDelete Include="$(PluginsOutputDirAbs)\TMSpeech.Core.*" />
</ItemGroup>
<Delete Files="@(FilesToDelete)"/>
```

### PublishPlugins Target（发布时触发）

```xml
<Target Name="PublishPlugins" AfterTargets="PrepareForPublish" Condition="'$(BuildPlugins)'=='true'">
```

- 使用 `xcopy` 将构建输出的 `plugins\` 目录整体复制到发布目录

```xml
<Exec Command="xcopy "$(BuildPluginDir)" "$(PublishPluginDir)" /E /Y /I /Q" />
```

### 构建开关

| 属性 | 默认值 | 说明 |
|------|--------|------|
| BuildPlugins | true | 控制是否编译插件 |
| PluginsOutputDir | `plugins\` | 插件输出子目录 |

---

## 五、NetBeauty 发布优化配置

主程序使用 [NetBeauty2](https://github.com/nulastudio/NetBeauty2) 优化发布包结构：

| 配置项 | 值 | 说明 |
|--------|-----|------|
| DisableBeauty | False | 启用 Beauty 优化 |
| BeautyOnPublishOnly | True | 仅在发布时执行 |
| BeautyLibsDir | `./lib` | 依赖 DLL 移入此子目录 |
| BeautySharedRuntimeMode | False | 非共享运行时模式 |
| BeautyHiddens | `hostfxr;hostpolicy;*.deps.json;*.runtimeconfig*.json` | 隐藏用户不需要的文件 |
| BeautyEnableDebugging | True | 允许第三方调试器（如 dnSpy） |
| BeautyUsePatch | True | 使用补丁减少文件数（SCD 模式） |
| SelfContained | true | 自包含部署，携带 .NET 运行时 |

---

## 六、完整构建流程

```
1. 目录级配置加载
   └── Directory.Build.props → 全局 Nullable, AvaloniaVersion, GitVersion

2. 核心层编译
   └── TMSpeech.Core → 输出 Core.dll

3. 界面层编译
   └── TMSpeech.GUI → 引用 Core → 输出 GUI.dll

4. 主程序编译
   └── TMSpeech → 引用 GUI → 输出 TMSpeech.exe
       │
       ├── AfterTargets=Build
       │   └── BuildPlugins Target:
       │       ├── 编译 AudioSource.Windows    → plugins/TMSpeech.AudioSource.Windows/
       │       ├── 编译 Recognizer.SherpaOnnx  → plugins/TMSpeech.Recognizer.SherpaOnnx/
       │       ├── 编译 Recognizer.SherpaNcnn  → plugins/TMSpeech.Recognizer.SherpaNcnn/
       │       ├── 编译 Recognizer.Command     → plugins/TMSpeech.Recognizer.Command/
       │       └── 删除各插件目录中的 TMSpeech.Core.*（避免重复）
       │
       └── AfterTargets=PrepareForPublish（dotnet publish 时）
           └── PublishPlugins Target:
               └── xcopy plugins/ → 发布目录/plugins/

5. 发布优化（dotnet publish）
   └── NetBeauty 重组 DLL 结构 → 依赖移入 lib/ 子目录
```

---

## 七、关键设计要点

### 1. 插件隔离机制

插件通过 `Private=false` + `ExcludeAssets=runtime` 不将 `TMSpeech.Core.dll` 打入输出，运行时由主程序提供统一的 Core 实现，避免版本冲突。

### 2. 运行时动态加载

`PluginManager` 使用自定义 `PluginLoadContext`（继承 `AssemblyLoadContext`）从 `plugins/{name}/` 目录加载插件程序集，实现插件热插拔与卸载隔离。

### 3. GitVersion 集成

通过 `GitVersion.MsBuild` 在编译时根据 Git 提交历史自动生成版本号（ContinuousDelivery 模式），版本信息可在 `ConfigWindow` 中通过 `GitVersionInformation` 类访问。

### 4. NetBeauty 发布优化

将大量依赖 DLL 移入 `lib/` 子目录，保持发布根目录整洁，减少文件数量，提升用户体验。

### 5. 条件编译（Windows 平台）

主程序在 Windows 下自动切换到 `net6.0-windows10.0.17763.0` TFM，以支持 Win32 API 调用（如窗口穿透 `SetCaptionLock`、无边框窗口拖拽等）。

---

## 八、Release 流程

1. 测试各种功能，确保功能完整、Bug 尽量修复
2. 使用 Visual Studio 的 Release 配置，选择打包 .NET 运行时（SelfContained）
3. 增加模型文件夹、`default_config.json` 文件
4. 打包压缩包后，在其他机器测试
5. 创建 Release，上传压缩包到 GitHub Release 和 Gitee（压缩包分卷，规避单文件大小限制）
