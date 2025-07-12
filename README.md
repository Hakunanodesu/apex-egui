# Assisted Pursuit with Effortless eXecution

## 项目概述

A.P.E.X.，又称 Assisted Pursuit with Effortless eXecution，一个基于深度学习的实时目标检测系统，使用 Rust 语言开发，集成了 YOLO 目标检测模型、ONNXRUNTIME DirectML 加速和手柄控制映射功能。该项目能够通过屏幕截图进行目标检测和追踪，并将手柄输入映射到虚拟 Xbox 360 手柄，实现自动化控制。

## 主要特性

- 🎯 **实时目标检测**：基于 YOLO 模型的高效目标识别
- 🎮 **手柄映射**：DualSense 手柄的映射，原始输入将被拦截
- 📸 **屏幕捕获**：实时屏幕截图和目标追踪
- ⚡ **GPU 加速**：使用 DirectML 进行 GPU 加速推理
- 🔧 **易于配置**：初始化和配置工具
- 🎯 **目标追踪**：智能目标跟踪算法
- 🦀 **Rust 开发**：高性能、内存安全的系统级编程语言
- 🖥️ **现代 UI**：基于 egui 的现代化用户界面

## 技术栈

- **语言**: Rust 2024 Edition
- **UI 框架**: egui + eframe
- **游戏手柄**: SDL2 + hidapi
- **虚拟手柄**: ViGEmBus
- **屏幕捕获**: Windows Capture API
- **AI 推理**: ONNX Runtime with DirectML
- **图像处理**: image crate
- **HTTP 请求**: reqwest
- **序列化**: serde + serde_json

## 系统要求

- Windows 10/11 操作系统
- 支持 DirectX 12 的 GPU
- DualSense/Xbox 手柄
- Rust 工具链 (用于编译)

## 项目结构

```
src/
├── main.rs              # 主程序入口
├── modules/             # 核心功能模块
│   ├── bg_con_mapping.rs    # 手柄映射
│   ├── bg_con_reading.rs    # 手柄读取
│   ├── bg_onnx_dml_od.rs    # ONNX 推理
│   ├── bg_screen_cap.rs     # 屏幕捕获
│   └── hidhide.rs           # HidHide 集成
├── utils/               # 工具函数
│   ├── tools.rs             # 通用工具
│   ├── ui.rs               # UI 相关
│   └── bg_dl_instl.rs      # 下载安装
└── fonts/               # 字体文件
```

## 配置说明

项目使用 `config.json` 进行配置，包含以下主要设置：

- 屏幕捕获设置

## 使用方法

1. **启动程序**: 运行可执行文件
2. **配置设置**: 在 UI 界面中调整相关参数
3. **开始检测**: 点击开始按钮启动目标检测
4. **手柄控制**: 连接 DualSense 手柄进行控制

## 致谢

- [ViGEmBus](https://github.com/nefarius/ViGEmBus) - Windows kernel-mode driver emulating well-known USB game controllers
- [HidHide](https://github.com/nefarius/HidHide) - Gaming Input Peripherals Device Firewall for Windows
- [egui](https://github.com/emilk/egui) - Simple, fast, and highly portable immediate mode GUI library
- [ONNX Runtime](https://github.com/microsoft/onnxruntime) - Cross-platform, high performance ML inferencing and training accelerator

## ⚠️ **重要提示**

- 本项目仅供学习和研究目的使用
- 请勿用于游戏辅助或其他违规用途
- 使用前请确保遵守相关法律法规和平台规则

## 技术支持

如遇问题，请：

1. 检查系统要求和依赖项
2. 查看项目 [Issues](https://github.com/Hakunanodesu/Assisted-Pursuit-with-Effortless-eXecution/issues) 页面
3. 提交详细的问题报告，包含：
   - 操作系统版本
   - Rust 版本 (`rustc --version`)
   - 错误信息和日志
   - 复现步骤

## 许可证

本项目采用 MIT 许可证 - 详见 [LICENSE.txt](LICENSE.txt) 文件。
