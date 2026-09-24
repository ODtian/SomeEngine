dotnet build/run 等会 restore 的不要使用沙盒
运行 python 时优先尝试使用 uv+venv，不要使用沙盒

0. 以端到端交付为标准对待每一个要求。
4. DRY：消除重复的业务规则、协议和状态知识；但不要把只是表面相似、变化原因不同的代码强行抽象到一起。
6. SRP / 关注点分离：业务逻辑、IO、解析、校验、状态更新和展示尽量保持边界清晰；拆分以变化原因和可测试性为准。

任何启发式算法必须报告后再做。
禁止分阶段计划，我只要一步到位的最终计划，禁止“第一阶段”、“先不做”、“一期”、“前期只做”等表述。
所有类与方法命名，必须经过 GitHub 搜索调研后再设计，通常情况禁止 XXPlan、XXRun、XXProgram 作为类名。

Profiler 只能作为外部 profiler 的插桩桥接器；不要恢复 managed/self profiler，不要在引擎内做 profile 存储、计时表、调用树、计数聚合、本地报告或 profile 输出文件。需要计数时走外部 profiler sink。
Tracy 工具路径必须先实际查找再记录。2026-06-16 使用明确的 `C:\Users\boqi` 路径复查：`C:\Users\boqi\Apps` 不存在；`tracy-capture.exe`、`tracy-csvexport.exe`、`tracy-profiler.exe` 位于 `~/App/windows-0.13.1`（Windows 实际路径：`C:\Users\boqi\App\windows-0.13.1`）。需要 capture/export/查看 Tracy 数据时先使用这里的 Tracy 工具，不要把路径写成 `~/Apps`，也不要临时构建或改造 `external/tracy` 工具链，除非用户明确要求。

性能目标必须靠跨场景成立的结构性优化达成。禁止通过调场景参数、benchmark 负载、pass 开关、功能降级、validation/static/placed/alias/细粒度 pass 关闭、warmup/window cutoff 调整等换个场景就失效的调参方式凑结果。
