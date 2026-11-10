# ProbablyCoop · 双人合伙店

《深空当铺》（Probably Stolen）的双人合作 Mod。玩家共同经营同一家店，使用原版操作界面，各自拥有角色状态与随身物品。这个仓库也供内容 Mod 作者定位联机兼容问题和提交适配。

当前已发布的版本号为 `0.1.0`。这里的源码可能包含尚未发布到工坊的修复。当前原生接口基线为 Windows Steam 版 0.46D / Rev13，Steam build 26525552。两端需要相同的游戏与 ProbablyCoop 构建，测试时也请对齐内容 Mod 版本及加载顺序。

## 从源码构建

安装 .NET 9 SDK 和游戏。设定环境变量 `PSGameDir` 指向游戏安装目录，或用 `GameManaged` 指向游戏的 Managed 目录。构建产物写入 `torelease/ProbablyCoop/`。游戏程序集从本机引用。

```powershell
$env:PSGameDir = Read-Host 'Probably Stolen installation directory'
dotnet build ProbablyCoop.csproj -c Release
dotnet run --project Tests/ProbablyCoop.Tests.csproj -c Release
dotnet run --project Tests/NativeActors/ProbablyCoop.NativeActors.Tests.csproj -c Release -- ./torelease/ProbablyCoop/ProbablyCoop.dll
```

将产物目录内的文件一起放入游戏的 `Mods/probably_coop/`。在原版模组菜单启用、确认权限并重启。测试请使用独立合作档或隔离游戏配置。协议离线测试与实机双端测试分别记录。

## 源码导航

| 目录 | 内容 |
| --- | --- |
| `Runtime/` | 原生 Mod 入口、会话驱动和传输接口 |
| `Core/` | 握手、消息、状态、去重与操作依赖 |
| `Steam/` | Steam 房间、好友加入和消息传输 |
| `Game/` | 原版适配、角色、场景、共享业务和客机交互 |
| `Game/Architecture/` | 原版物品、窗口和布局的声明式投影 |
| `UI/`、`Localization/` | 房间界面、指针与本地化 |
| `Tests/` | 可独立构建的协议与原生接口测试 |

内容 Mod 作者请从 [联机适配指南](docs/compatibility.md) 开始。开发分支与版本规则见 [贡献指南](CONTRIBUTING.md)。
