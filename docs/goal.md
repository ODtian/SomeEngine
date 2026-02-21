
## Final Goal
本项目实现一个全c# ECS游戏引擎。
- ECS游戏框架，所有系统建立在ECS基础上
  - 使用 https://github.com/friflo/Friflo.Engine.ECS
  - 参考https://github.com/turanszkij/WickedEngine 实现一个高性能job system
  - 基于https://github.com/Dreaming381/Latios-Framework/tree/master/Transforms实现QVVS的坐标系统
- GPU rendering pipeline管线
    - 要求：
      1. rhi使用https://github.com/DiligentGraphics/DiligentCore，并实现高性能render graph
      2. cluster based，利用 https://github.com/zeux/meshoptimizer并参考里面的demo/nanite.cpp
      3. 实现2阶段hiz剔除，无缝lod，参考bevy的实现
      4. 软光栅
      5. 实现可编程光栅化（对不同类型光栅化进行binning），蒙皮网格渲染
      6. 实现类nanite tess的动态细分技术
      7. 在一些优化下，尝试实现一些rop，例如stencil操作。
      8. 支持类似nanite voxel实现聚合体的渲染
- 光照系统
    - 实现compute based shading。对vis buffer上不同着色模型进行binning。
    - 实现pbr光照模型。并支持自定义着色模型
    - 使用slang着色器语言
    - 实现virtual shadow map
    - 实现类似ue5 megalight的光追DI技术
    - 实现各种后处理或反走样pass（参考已有引擎）
- 物理系统
  - 基于AVBD算法， https://graphics.cs.utah.edu/research/projects/avbd/
  - 在ECS框架中实现，参考https://github.com/Dreaming381/Latios-Framework/tree/master/PsyshockPhysics的框架
  - 计算出接触流形后，解算阶段可使用GPU加速
- 动画系统
  - 基于ECS和Job的高性能系统，参考https://github.com/Dreaming381/Latios-Framework/tree/master/Kinemation
  - 实现3A级动画压缩、动画混合
  - 实现linear和四元数蒙皮
  - 实现UE的物理动画（physics control）、IK、rigging系统
- 粒子系统
  - 暂时不需要
- 资产管线
  - 实现基于c#、ECS、高性能资产管线
  - 能够很好满足GPU rendering pipeline的流式加载需求。
- UI与编辑器
  - 前期使用IMGUI简易Debug
  - 后期使用avalonia共享纹理的方式构造游戏内UI。
  - 编辑器使用avalonia作为宿主。
- 其他系统
  - 参考 https://github.com/Dreaming381/Latios-Framework