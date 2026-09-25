# IPv4子网规划服务

C#12与ASP.NET Core8基础项目，目前仅有健康检查，尚未实现子网规划。

## 开发

```bash
dotnet restore --locked-mode
dotnet test
dotnet build
dotnet run --project src/SubnetPlanner.Api
curl http://127.0.0.1:<启动日志中的端口>/healthz
```

使用.NET SDK8.0.421。NuGet依赖版本保存在锁文件中，项目缓存为本目录.packages。tests/SubnetPlanner.Tests包含xUnit和ASP.NET Core集成测试依赖及一项健康检查测试。

默认仅监听本机，由系统分配空闲端口，实际地址见启动日志；也可通过--urls指定空闲端口。
