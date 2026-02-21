- qvvs设计中目前有一些padding可供后续拓展
- 在层级lod选择中，带加速结构的设计中，父lodbound存储在叶子节点上，表示叶子节点所表示的组的父lodbound与父error。而cluster只存储自身lodbound和selferror用于剔除。目前线性剔除的设计中，cluster同时存自己的lodbound和父lodbound与error这是个暂时的设计。
- [2026-02-18] [API Design: Slot-Based Binding](design/diligent_slot_based_binding.md): 深度调研了 DiligentCore 的重映射机制。设计了修改源码以支持基于位置（Binding/Set）绑定的方案，涵盖了显式签名、隐式变量描述以及运行时绑定 API 的全链条改造。
 