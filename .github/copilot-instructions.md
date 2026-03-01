# GitHub Copilot Instructions

使用简体中文进行交流。永远只进行极少量关键注释，最好不要注释。如果你在任何计划模式下，禁止修改代码。

## Guide
- 有任何问题立即停止并询问用户意见
- 写完功能后编写测试用例。
- plan时使用todo工具增加TODO，实现后标记完成
- 完成功能后简短记录在log.md中
- 禁止unsafe，必须使用Span Api，如果库没有span api，立即停止并告诉用户。meshopt、diligent所有api都有span版本。
- 目前测试项目应使用Runtime项目
- shell环境为bash
## Goal
查询 docs\goal.md

