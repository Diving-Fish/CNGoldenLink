# CNGoldenLink

CN 金榜的 Celeste 联动 Mod，版本 0.1。

在 Mod 菜单开启连接后，通过浏览器登录 CN 金榜并授权，默认每 5 秒同步当前状态、CCT 统计和地图死亡数据。同步间隔可在菜单调整，服务地址可通过设置文件的 `ServiceBaseUrl` 修改。

无金完整通关最少死亡始终在本地记录；连接开关只控制上传，重新连接后补传已保存的纪录。当前支持 Windows，CCT 适配版本为 2.10.2。

## 构建

需要 .NET 8 SDK、安装 Everest 的 Celeste，以及 CCT 2.10.2。先启动一次游戏，生成 CCT 程序集缓存。

默认将项目放在 Celeste 游戏目录下，在项目目录执行：

```powershell
dotnet build -c Release
./scripts/package.ps1
```

其他目录通过参数指定游戏路径：

```powershell
./scripts/package.ps1 -CelestePath "E:/SteamLibrary/steamapps/common/Celeste"
```

生成的 `artifacts/CNGoldenLink-0.1.0.zip` 放入游戏 `Mods` 目录，移走旧版本后启动游戏。安装包不包含游戏或 CCT 程序集。

运行测试：

```powershell
dotnet run --project tests/Diagnostics.Tests.csproj -c Release
dotnet run --project sync-tests/Sync.Tests.csproj -c Release
```
