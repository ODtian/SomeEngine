# Job System 集成契约

SomeEngine 的 Job 实现来自外部仓库 `F:/SomeJob`，本仓库不再在 `src/SomeEngine.Core/Jobs` 下保留调度器、句柄池、队列或执行分发实现。

## 项目引用

- `SomeEngine.slnx` 将 `../SomeJob/src/SomeJob/SomeJob.csproj` 和 `../SomeJob/src/SomeJob.Dots/SomeJob.Dots.csproj` 放在 `/external/SomeJob/` 下。
- `src/SomeEngine.Core/SomeEngine.Core.csproj` 直接引用 `..\..\..\SomeJob\src\SomeJob\SomeJob.csproj`。
- 需要编写或测试 Job 代码时，使用 `SomeJob` 命名空间。

## 运行时 API

- `IJob`：单任务结构体接口。
- `IJobParallelFor`：按索引并行执行的结构体接口。
- `JobSystem.Schedule(...)`：调度单个 Job。
- `JobSystem.ScheduleParallel(...)`：调度并行 Job。
- `JobSystem.CombineDependencies(...)`：合并依赖句柄。
- `JobHandle.Complete()`：等待句柄完成；不再使用本地 `Return` 或 `Wait` 入口。

## SomeEngine 约束

- Core 层只持有 `SomeJob.JobHandle`，例如 `SystemContext.GlobalDependency`。
- 引擎代码不能重新引入 `SomeEngine.Core.Jobs` 命名空间或本地 Job 调度实现。
- 需要 Dots 风格 chunk/for 适配时，引用 `SomeJob.Dots` 暴露的 `SomeJob` API。
