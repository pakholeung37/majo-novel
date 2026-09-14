# Manosaba 角色图集转 Live2D PSD

此脚本读取 `demos/manosaba/assets/characters/<角色>/layered_character.json` 和对应 PNG 图集，按 manifest 的画布、裁切区域、pivot、坐标、缩放与 `sortingOrder` 从下到上还原为分层 PSD。目录层级会转成 PSD 文件夹；所有备选部件都会保留，默认外观以外的图层会隐藏。默认表情由 `defaultAppearance` 和 `compositionMap` 解析，而非只看单层的 `enabled` 字段。

## 安装与运行

```sh
python3 -m venv .venv
.venv/bin/python -m pip install -r tools/manosaba-to-live2d-requirements.txt
.venv/bin/python tools/manosaba_to_live2d_psd.py Hanna
.venv/bin/python tools/manosaba_to_live2d_psd.py
```

不传角色名时导出所有带 `layered_character.json` 的角色。默认输出在 `artifacts/live2d-psd/`；可用 `--output-dir` 指定位置，或用 `--input-dir` 指向另一份同格式角色目录。`Warden` 只有单张 PNG，没有分层 manifest，因此不会出现在批量导出中。

导出的 PSD 使用 RGB、每通道 8 位、sRGB ICC 配置、独立透明像素图层，并避免 PSD 图层蒙版。原素材中的 `#Mask_Ref...` / `#Masked_Ref...` 模板缓冲区关系会按默认外观烘焙到读取层的 alpha 中。隐藏的备选层仍保留，但切换备选姿势或表情后，原游戏的动态模板缓冲区效果可能需要在 Cubism 中手动修整。软光等混合模式也应在 Cubism 导入后检查。

将 PSD 拖入 Cubism Editor 建模工作区，选择“从 PSD 文件创建新模型”。这里能验证 PSD 的结构和像素，但没有自动化 Cubism Editor 导入测试。

参考：[Live2D PSD 创建注意事项](https://docs.live2d.com/en/cubism-editor-manual/precautions-for-psd-data/)、[Live2D 导入 PSD](https://docs.live2d.com/en/cubism-editor-manual/psd-import/)。
