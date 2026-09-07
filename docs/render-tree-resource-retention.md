# 角色树资源持有

Direction 仍在隐藏根节点下准备替换角色树，保持现有 Prepare / Commit / Rollback 隔离。每棵树的内置角色工厂使用独立的 `ResourceActorTextureLoader` scope，共享同一个 ResourceManager。

`ResourceRetentionScope` 对该树实际加载的贴图申请 pinned lease。同一 scope 的重复请求复用 lease；不同树拥有独立 lease，但 ResourceManager 按解析后的 provider、路径和类型共享缓存对象，缓存体积只统计一次。语言和 appearance override 在申请 lease 前沿用原有解析规则。

根节点退出场景树时释放该 scope。候选树失败或回滚会释放自己的资源持有，当前树不受影响；旧树在既有转场/事务清理流程中移除后释放；Runtime 重置和关闭通过 stage backend 清理当前树。预加载窗口释放不会释放渲染树的持有权。

`IResourceManager.IsPinned<T>` 用于查询资源是否已有持久持有者。Yarn 窗口对已 pinned 的贴图不再建立重复前瞻 lease，也不把它们计入 96 MiB 窗口预算；全局 ResourceManager 缓存统计仍包含这些资源，超出全局预算的压力不会被隐藏。首次使用、尚无角色树持有的大 atlas 仍可能超过前瞻预算，需要正常准备。

这次修复复用贴图资源，不复用角色节点，也没有增加内存预算。manifest 解析和角色节点构造仍遵循现有工厂流程。自定义纹理加载器及绕过内置工厂的插件需要自行管理其资源持有。

验证包括：不同 scope 共享对象且只计一次体积，候选持有者取消不影响另一持有者，最后一个 scope 释放后允许淘汰；Godot 使用 Ema atlas 连续替换角色树，并在中间显式 TrimCache，确认不新增 atlas cache miss，stage reset 后没有遗留 lease。

## Save restore lifecycle

Direction coordinator reset now invokes the registered rendering reset under its commit gate,
after transaction cleanup. This releases active stage actor trees (and their texture scopes)
and resets the render host before saved state is reconstructed. Cleaning up committed
transactions alone only releases replaced trees, not the currently published target.

Yarn startup with a checkpoint prepares the saved line's voice and the following window.
The matching node-start notification preserves that prepared cursor instead of replacing
it with the node-opening window. No Yarn source changes are required.

The headless preload regression repeats reset/restore three times and checks that old leases
reach zero before each restoration. Cache estimates remain conservative: an over-budget
warning can still occur when genuinely active textures plus other leased resources exceed
the global budget; releasing leases makes entries evictable and does not force immediate
Godot/process memory reclamation.
