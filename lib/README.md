# 第三方类库与运行库依赖

本目录已直接随仓库提交（未使用 Git LFS），拉取代码后无需额外配置即可编译，构建时会自动拷贝至输出目录的 `libs\` 子目录：

- **HALCON 19.11.0.0（x64）**：`halcondotnet.dll`、`halcon.dll`（输出至 `libs\halcon\`）。注意目标机运行 HALCON 算子时仍需具备有效的 HALCON 许可证。
- **大华相机 SDK（x64）**：`Dahua\x64\` 下的托管接口及 `Runtime\` 原生运行库（输出至 `libs\dahua\`）。
- **海康相机托管依赖（x64）**：`Hikvision\` 下的托管 DLL（输出至 `libs\hikvision\`；连接海康实体相机时目标机需安装匹配版本的海康 MVS Runtime 与驱动）。

