# Job System 设计需求

## 核心目标
实现一个高性能、零 GC (Zero-Alloc)、类型安全的 Job System，支持细粒度任务调度和依赖管理。

## 架构设计

### 1. 强类型数据存储 (Zero-Alloc)
- **拒绝装箱**：Job 数据必须是 `struct`，并直接存储在预分配的强类型数组/缓冲区中。
- **数据与调度分离**：
  - `JobDataStore<T>`：泛型静态类，维护特定类型 Job 的数据池 (RingBuffer/Array)。
  - **优势**：编译器生成独立代码路径，无虚函数调用开销，无装箱。

### 2. 统一调度队列 (Job Queue)
- **Job ID**：系统内部传递轻量级 `struct JobId { ushort TypeId; int Index; }`。
- **全局队列**：Worker 线程从统一的 Global Queue 中抢占 `JobId`。
- **执行分发**：Worker 根据 `TypeId` 查找对应的静态执行委托 `Execute(int index)`，进而调用 `JobDataStore<T>.Execute(index)`。

### 3. 依赖管理 (Dependency System)
- **JobHandle (Struct)**：
  - 仅包含 `Version` 和 `Index`，指向全局原子计数器池。
  - **必须是 struct**，避免 GC。
- **依赖机制**：
  - 支持 `Schedule(Job, JobHandle dependency)`。
  - 采用 **Dependency Counter** 机制：Job B 依赖 Job A -> Job B 的 ReadyCounter = 1。
  - Job A 完成时，递减 B 的 Counter。Counter 为 0 时，B 被推入 Ready Queue。

### 4. 递归调度 (Recursion)
- **线程安全**：`Schedule` 方法必须支持多线程并发调用。
- **Job 内创建 Job**：Worker 在执行 Job 时，可以分配并 Schedule 新的子 Job。
- **动态扩容**：数据存储和队列需要能够应对大量递归产生的任务。

### 5. 接口定义
- `IJob`：用户定义的 Job 结构体必须实现的接口。
- `Schedule<T>(T job, JobHandle dependency)`：调度入口。

## 待实现清单
- [ ] `IJob` 接口
- [ ] `JobHandle` (Struct, Atomic Counter Pool)
- [ ] `JobDataStore<T>` (Thread-safe storage)
- [ ] `GlobalJobQueue` (RingBuffer/ConcurrentQueue of JobId)
- [ ] `JobSystem` (Worker Threads, Executor Registry)
