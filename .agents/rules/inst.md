---
trigger: always_on
glob:
description:
---

使用简体中文进行交流。永远只进行极少量关键注释，最好不要注释。没有明确要求不得直接开始修改嗲嘛

## Guide
- 有任何问题立即停止并询问用户意见
- 写完功能后编写测试用例。
- plan时使用todo工具增加TODO，实现后标记完成
- 完成功能后简短记录在log.md中
- 禁止unsafe，必须使用Span Api，如果库没有span api，立即停止并告诉用户。meshopt、diligent所有api都有span版本。
- 禁止使用grep，我这是windows
- 由于正在使用git-bash，使用正斜杠/而不是反斜杠
## Goal
查询 @docs/goal.md

