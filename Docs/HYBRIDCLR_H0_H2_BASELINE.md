# HybridCLR H0-H2 基线

## 固定版本

- Unity: `6000.3.19f1`
- HybridCLR: `8.14.1` (`a0e0b502c6c1b9ce2d0983181f4555e6149ae249`)
- Addressables: `2.9.1`
- URP: `17.3.0`
- Newtonsoft.Json: `3.2.2`
- Windows Player: `StandaloneWindows64` / `IL2CPP` / `x86_64`
- API Compatibility Level: 以 `ProjectSettings.asset` 的 `apiCompatibilityLevel: 6` 为准；
  构建报告同时记录 Unity API 返回的枚举名称。

## 可重复验证

- `HotUpdateBaselineBuilder.VerifyFromCommandLine` 比较
  `Docs/Baselines/builtin-tools.schema.json`，防止 asmdef 拆分改变内置工具契约。
- `HybridClrProjectSetup.InstallFromCommandLine` 完成并校验 HybridCLR Installer。
- `HybridClrProjectSetup.GenerateAndStageFromCommandLine` 执行官方 Generate/All，
  并把与本次 Development Player 匹配的 AOT metadata、Smoke DLL/PDB 放入
  `Assets/StreamingAssets/HotUpdate`。
- `HybridClrProjectSetup.BuildSmokePlayerFromCommandLine` 构建 Windows IL2CPP
  Development Player，并生成 `Docs/Baselines/h2-windows-il2cpp-build.json`。
- Player 使用 `-gameWithLlmHybridClrSmoke` 启动时会校验 DLL 加载、Schema、
  Json.NET 参数反序列化和工具执行，并输出 `H2_SMOKE_SUCCESS` 后退出。

## 2026-09-21 验证结果

- asmdef 拆分后 Unity C# 编译通过，9 个 AOT BuiltinTools 的规范化 Schema 与
  `Docs/Baselines/builtin-tools.schema.json` 完全一致。
- HybridCLR `Generate/All` 完成，AOT generic references、bridge、裁剪配置和
  Windows x64 本地产物均已生成并 staging。
- Development Windows IL2CPP Player 构建成功；结果见
  `Docs/Baselines/h2-windows-il2cpp-build.json`。
- Player 以 `-gameWithLlmHybridClrSmoke` 启动并以退出码 0 结束，输出
  `H2_SMOKE_SUCCESS`。这同时覆盖 DLL 加载、Schema 生成、Json.NET 参数
  反序列化、校验和执行。

本机 Visual Studio 18 安装中最新的 MSVC 14.50 目录缺少标准库头文件；验证时仅
为 Unity 构建进程设置完整 MSVC 14.44 的 `INCLUDE`/`LIB` 路径，未修改仓库的构建
语义。持续集成机应安装完整的 Visual Studio C++ Tools 工作负载。

PDB 只由 Development/QA 生成和分发；非 Development 构建的 staging 流程会删除
PDB。关闭 `AgentHostClient.enableHybridClrBootstrap` 后只注册并运行 AOT 内置工具。

## 已知工具链兼容补丁

Unity 6000.3.19f1 自带的 Collections 2.6.6 在 Windows Player 关闭安全检查时仍
调用已移除的 `NativeArray<T>.ReadOnly(void*, int)` 内部构造函数。项目嵌入同版本
包并只替换 `NativeList<T>` 的两个只读视图创建点；说明见
`Packages/com.unity.collections/GAMEWITHLLM_PATCH.md`。
