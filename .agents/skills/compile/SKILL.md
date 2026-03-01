---
name: compile
description: How to compile the project or third-party libraries
---

#Diligent Core
In `external` dir
```bash
uv run ./DiligentCore/BuildTools/.NET/dotnet-build-package.py -c Debug -d ./DiligentCore
```
如果需要更新c#绑定，也要编译一次。
如果编译后需要编译c#项目，先运行`rm C:\Users\tianl\.nuget\packages\diligentgraphics.diligentengine.core\`清空缓存

c#绑定源码在external\DiligentCore\build\.NET\Graphics\GraphicsEngine.NET里面自己找