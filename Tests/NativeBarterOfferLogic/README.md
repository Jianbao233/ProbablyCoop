# 以物易物报价组逻辑测试

此独立可执行测试通过相对路径直接编译生产文件 `Game/NativeBarterOffer.cs`，使用明确的游戏、手持模型与 Harmony 属性 STUB。当前保留 **37 项**检查，覆盖换组、旧顾客/索引/数量/令牌/物品证明、缺失字段、报价组往返后的旧操作、柜台持物冲突、经营状态拒绝、客机请求和业务异常标记。

在仓库根目录运行：

```powershell
dotnet run --project Tests/NativeBarterOfferLogic/ProbablyCoop.NativeBarterOfferLogic.Tests.csproj -c Release
```

需要 .NET 9 SDK 和本地 `Newtonsoft.Json.dll`。默认通过工作区惯用的 `PSGameDir` 定位游戏目录；可使用 `-p:PSGameDir="其他游戏目录"` 或 `-p:GameManaged="其他 Managed 目录"` 覆盖。没有用户目录绝对路径，也不需要安装或启动游戏。

本目录的 `Directory.Build.props` 阻止继承主 Mod 的游戏程序集引用及打包配置。构建产物只写入此测试目录的 `bin/`、`obj/`，不构建主 Mod、不写入 `torelease/`。运行依赖的 JSON DLL 仅复制到被 Git 忽略的测试输出目录，不提交任何游戏 DLL。

预期结果：

```text
PASS barter selector production logic with STUB game model checks=37; NOT Unity/Harmony/gameplay validation
```

这些检查不验证 Unity 生命周期、真实 Harmony 安装、原版按钮/工厂、完整所有权图或联网。`TradeToken`、手持关系和请求发送由 STUB 提供，因此本测试也不能证明 `NativeWorld.TradeToken` 对旧成交请求的防重放接线；该路径由真实双端营业回归单独验收。STUB 的报价生成只模拟物品身份替换，不代表原版物品内容正确。
