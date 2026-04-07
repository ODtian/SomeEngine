---
trigger: always_on
glob:
description:
---

## Guide
- 有任何问题立即停止并询问用户意见
- 写完功能后编写测试用例。
- 发现 pre-existing 测试失败也必须修复，不得忽略。
- 禁止unsafe，必须使用Span Api，如果库没有span api，立即停止并告诉用户。meshopt、diligent所有api都有span版本。
- diligent api 在 F:/SomeEngine/external/DiligentCore/build/.NET/Graphics/GraphicsEngine.NET/obj/SharpGen-W9JYXH64En1K8nL2gz8snfwmGob3p4aeE5tPfFVBr68\SharpGen.Bindings.g.cs