# 日常维护

仓库：<https://github.com/hanxiaodong0/MoonCalendar>

## 修改和提交

在项目目录打开 PowerShell，先同步远程提交：

```powershell
git pull --rebase
```

修改源码后，可在退出正在运行的日历后构建并检查：

```powershell
.\build.ps1
Start-Process .\dist\MoonCalendar.exe -ArgumentList '--self-test', "$PWD\verification\self-test.json" -Wait
Start-Process .\dist\MoonCalendar.exe -ArgumentList '--ui-self-test', "$PWD\verification\ui-self-test.json" -Wait
Get-Content .\verification\self-test.json -Encoding UTF8
Get-Content .\verification\ui-self-test.json -Encoding UTF8
```

日历正在运行时，可用 `.\build.ps1 -OutputDirectory "$PWD\verification\candidate"` 单独构建候选版本，再从该目录运行测试；正式替换 `dist/` 前先退出日历。

确认功能和测试结果后，查看并上传改动：

```powershell
git status
git diff
git add .
git commit -m "说明本次修改内容"
git push
```

提交说明可以使用中文，例如“修复切换月份后的日期显示”。每次提交尽量对应一个明确改动。

## 哪些文件会上传

- 上传源码、界面、图标、依赖库、节假日初始数据、构建脚本和文档。
- 保留第三方依赖许可证。`lib/lunar.dll` 是构建所需的固定版本依赖，需要纳入版本管理。
- `.gitignore` 已排除生成的 `dist/`、`bin/`、`obj/`、根目录 ZIP、临时依赖下载、运行日志和验证输出。
- 程序的个人设置和天气缓存保存在用户的本地应用数据目录，不在源码目录中。

## 更新依赖或节假日初始数据

需要 Python 及网络连接：

```powershell
python .\tools\fetch-dependencies.py
```

更新后先查看 `data/`、`lib/`、`dependencies.json` 的变化，再构建和检查，最后提交。

日历自身的联网缓存更新不直接修改源码仓库中的初始数据文件。

## 登录

首次上传使用 GitHub 官方 CLI 的浏览器授权。后续 `git push` 会通过本仓库配置的 GitHub 凭据助手认证。凭据、浏览器登录信息及本地 `.git/config` 不会上传到仓库。

换电脑时，克隆仓库并为那台电脑单独登录 GitHub 即可；不需要复制当前电脑的凭据。
