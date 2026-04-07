# BATCH-05 Questions

当前 kickoff 无阻塞问题。

如果执行过程中需要确认，请只记录真正会影响 MVP 边界的问题，例如：

1. `ShaderAsset` 最小 metadata 是否允许先只支持 compatibility tags + binding layout signature？
2. `MaterialRegistry` 的自动推导 MVP 是否明确要求保留 `TagStore<MaterialPass>` 路线？
3. 哪些未来 ECS-tag / MaterialStore 方案必须显式排除在 BATCH-06 / BATCH-07 之外？
