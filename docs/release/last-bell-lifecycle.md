# Last Bell 发布与依赖生命周期验收

验收日期：2026-11-17。消费者为独立的 Last Bell 游戏仓库，macOS / Godot 4.7.2 .NET / .NET SDK 8.0.204。

## 实际发布

- [v0.1.0-rc.1](https://github.com/pakholeung37/majo-novel/releases/tag/v0.1.0-rc.1)，提交 `ecbfc0a`，[标签 CI](https://github.com/pakholeung37/majo-novel/actions/runs/44012432154) 通过。
- [v0.1.0-rc.2](https://github.com/pakholeung37/majo-novel/releases/tag/v0.1.0-rc.2)，提交 `bf5e987`，[标签 CI](https://github.com/pakholeung37/majo-novel/actions/runs/44013577119) 通过。
- 两次均由标签工作流生成草稿，检查三个 ZIP、install.py、SHA256SUMS 后发布为公开 prerelease；没有替换已有资产或移动标签。

捆绑 ZIP SHA-256：

- rc.1：`0acaf356b40abda9e30ef6ed06bc8137a2e5ef3893d84c4f884ebcaca0819ed2`
- rc.2：`a4a0bc5f3e13ce83931759eaac1d8cbbcae54738061af8cb978cc02998a0cb0a`

## 干净安装

从 Last Bell 创建无 addon、无锁文件的临时副本，从公开 Release 在线安装 rc.1，然后构建、导入并跑完游戏开场。另从 rc.2 公开 URL 使用 `curl .../install.py | python3 - init ... --check` 创建全新工程，构建和 Direction 校验通过。安装过程未依赖系统仓库的本地 ZIP。

首轮公开验收发现 rc.1 在 macOS 使用软链接临时目录时，C# 资源路径映射会令受检升级失败；失败时没有切换游戏。rc.2 在 MSBuild/Godot 调用前统一物理路径后通过。rc.1 发布说明已记录限制，推荐使用 rc.2。

## 实际 Last Bell 升级与回退

| 步骤 | 当前 Majo | 结果 |
|---|---|---|
| upgrade-public-rc2 | 0.1.0-rc.2 | 通过 |
| check-active-rc2 | 0.1.0-rc.2 | 通过 |
| rollback-public-rc1 | 0.1.0-rc.1 | 通过 |
| check-active-rc1 | 0.1.0-rc.1 | 通过 |
| reupgrade-public-rc2 | 0.1.0-rc.2 | 通过 |
| check-final-rc2 | 0.1.0-rc.2 | 通过 |

实际使用的游戏命令：

```sh
python3 tools/dev.py upgrade --version v0.1.0-rc.2 --check
python3 tools/dev.py check
python3 tools/dev.py rollback --check
python3 tools/dev.py check
python3 tools/dev.py upgrade --version v0.1.0-rc.2 --check
python3 tools/dev.py check
```

回退后 `majo.lock.json` 与 rc.1 保存的原始字节完全一致。每一步都核对 Main.cs、Main.tscn、Yarn 脚本、manifest 和 project.godot 的 SHA-256，均保持不变。最终依赖锁指向公开 rc.2 发布资产。逐步日志及机器可读报告保存在游戏的 `artifacts/majo-release-cycle.json`，本地回退点在 `.majo/history`，不提交这些缓存。

## 故障注入

1. 从已发布 rc.2 ZIP 制作一个仅用于本地测试的候选包，移除消费者使用的 `CreateDefault` API。游戏编译返回 CS0117，候选被拒绝；实际 rc.1 addon、锁文件与游戏源码保持原样。
2. 保持候选代码正常，但令游戏回归 profile 要求一个不存在的成功标记。游戏命令退出 0 且打印开场成功，但 profile 仍正确拒绝候选；实际安装版本保持 rc.1。随后恢复原 profile，正常升级通过。

## 兼容边界

rc.1 与 rc.2 的运行时 API 0.1、Yarn 依赖和存档布局未改变。管理器记录 `compatibility.json`；跨 API/引擎依赖边界需要明确的受检迁移授权。回退恢复系统依赖及宿主配置，不回滚玩家存档。Last Bell 冒烟测试禁用全局持久化，使用独立验证 namespace；这次没有宣称完成历史玩家存档迁移测试。
