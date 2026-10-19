在完成任务后，请将你在本次任务阅读代码所了解到的项目知识更新到project-info（或其他细节project-info）中以便后续ai agent使用。
在更新project-info时，应该注意只添加更general的内容，而非具体每次做了什么（除非本次编辑的东西就是很general的）
例如：
```
假设本次任务新增了自定义打开文件拓展名的功能，现在要更新某个具体的词条，原先没有"KeyValues.cs"的词条
good：`KeyValues.cs`: Contains constant strings for settings keys.
bad: `KeyValues.cs`: Contains constant strings for settings keys. The `CustomTextFileExtensions` key has been added here.
 （后半句是极其具体的内容，而且并不是什么泛用性内容，不应该写到project-info中）
```