# EOpen

一个面向 Windows 的黑白像素启动面板。按 **Alt + 空格** 唤出，输入名称、拼音或简称，按回车打开应用或目录。

## 下载和运行

[下载 Windows 便携版](downloads/EOpen-Windows.zip?raw=true)，解压到固定目录，双击 `EOpen.exe`。程序使用 Windows 的 .NET Framework 和 WPF。在 Windows 11 上验证过。首次运行即显示面板，之后可从系统托盘打开或退出。

| 输入或操作 | 功能 |
| --- | --- |
| 应用名、拼音或首字母 | 搜索开始菜单和桌面上的应用，方向键选择，回车打开 |
| `wd`、`zm` | 打开文档或桌面目录 |
| `\q 问题` | 按回车用默认浏览器打开必应搜索 |
| `sj` | 打开微信官方文件传输助手网页版，用手机微信扫码后转发文件 |
| `lan` | 打开局域网接收窗口，在同一 Wi-Fi 下扫码发送文字、链接和文件 |
| **Alt + A** | 框选区域并复制截图，Esc 取消 |
| 拖动标题 | 移动启动面板 |

局域网传送使用 HTTP，请在可信网络使用。持有当次二维码或链接的人可以发送内容，单文件限 512 MB。文件保存到当前用户文档目录下的「EOpen 接收」。关闭接收窗口即停止接收。

## 设置

编辑程序旁边的 `folders.txt`，每行按 `简称|名称|目录或 exe 完整路径` 添加自己的入口，重新唤出面板后生效。默认启动快捷键为 `hotkey=Alt+Space`，改为单个英文字母（例如 `hotkey=J`）可使用 Ctrl + Alt + 对应字母。如果快捷键已被其他工具占用，可从托盘打开 EOpen。

如需登录后自动运行，把指向 `EOpen.exe` 的快捷方式放进 Windows 的 `shell:startup` 文件夹，在目标路径后加 ` --tray` 即可静默启动。删除该快捷方式可取消自启动。

## 从源码构建

在 Windows PowerShell 中进入仓库目录，执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

构建使用 Windows .NET Framework 的 C# 编译器，输出到仓库根目录的 `EOpen.exe`。随仓库提供的 QRCoder 1.6.0（net40）负责在本地生成二维码，其许可见 [QRCoder-LICENSE.txt](QRCoder-LICENSE.txt)。
