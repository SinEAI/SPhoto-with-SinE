# 公开仓库与提交身份

每次提交都必须脱敏。使用 GitHub 设置页面提供的私密提交邮箱（以 `users.noreply.github.com` 结尾），以 GitHub 用户名作为提交名称，不使用个人邮箱。

```sh
git config --local user.name "<GitHub username>"
git config --local user.email "<GitHub noreply email>"
git config --local core.hooksPath .githooks
```

安装 Python 3 后，本地 `pre-commit` 检查完整 Git 暂存快照及实际提交身份；`commit-msg` 检查提交说明（包括共同作者）；`pre-push` 检查待推送提交的快照、作者、提交者和说明。不能使用 `--no-verify` 绕过检查。

```sh
python3 tools/check_public_repo.py --staged --identities
python3 tools/check_public_repo.py --worktree
python3 tools/check_public_repo.py --history HEAD
python3 -m unittest discover -s tools -p 'test_*.py'
```

GitHub Actions 中的 `Public repository checks` 对所有提交历史运行相同检查。该工作流需要先提交到远端才能运行；主分支的强制保护需要在 GitHub 仓库设置中把此检查设为 required check。本地 hooks 与普通 CI 不能阻止有权限的人绕过规则直接提交到 GitHub。

检查器拦截常见凭据、非公开邮箱、带用户名的绝对路径、包含凭据的 URL、常见敏感运行文件与图片元数据，也检查 ZIP/JAR 中的内容。输出不包含匹配值。历史扫描包含提交说明和 author/committer 身份，GitHub 官方生成的历史身份仅允许 `noreply@github.com` 与 `web-flow@github.com`。

检测器基于规则，不是内容脱敏的证明。提交前必须人工复核姓名、手机号、身份证号、真实照片、截图、地址和位置等上下文。发布产物必须另行检查；特别是 PDB、崩溃日志、图库数据库、用户配置、安装包中的嵌入文件和编译路径。第三方许可证署名保留，任何检测例外都必须精确到真实误报，不能排除整个第三方目录。

zlib 官方 LICENSE 的两条公开作者联系邮箱按许可要求保留，仅允许出现在 `third-party-notices/zlib.txt` 和定义此规则的检查器中。其他文件、提交作者、提交者及共同作者不适用此例外；许可证中的凭据仍然拦截。

若敏感内容已经公开，删除当前文件不足以消除历史记录或远端副本。先撤销或轮换凭据；需要重写历史时先取得明确授权。
