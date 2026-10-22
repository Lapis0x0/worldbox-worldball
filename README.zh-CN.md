# 世界球 WorldBall

[English](README.md) | 中文

<img src="GameResources/ui/globe_template_planet.png" alt="世界球" width="100">

WorldBox 的世界是一个盒子，这个模组把它变成一个球：新建世界时多一个「星球」模板，地图东西首尾相接，拉远镜头就是一整颗可以转动的星球。

- 依赖：[NeoModLoader](https://github.com/WorldBoxOpenMods/ModLoader)

## 主要内容

- 「星球」模板：地图宽高 2:1，横向是经度、纵向是纬度。地形直接在球面上生成，大陆卷到球上形状自然；山脉成脉，两极是终年不化的冰原
- 东西无缝：地图左右两边连在一起。居民可以直接走过接缝，城市和国家可以横跨接缝扩张；找敌人、找猎物、判断攻击范围也按接缝两边的实际距离算
- 地球仪视角：镜头拉过原版的最远距离后，地图逐渐卷成一个球。按住右键或中键拖动转球，手机上用手指拖
- 原版画面完整上球：地形、国界、建筑、战争线、城市灯光、时代滤镜都画在球上；军队、国王、战斗等标记立在球面上，名牌跟着球转，转到背面自动隐藏
- 球上照常操作：放神力、点选单位、查看提示都和平面地图一样
- 平面视角和原版完全一样

只有用「星球」模板新建的世界才有这些变化，其他世界不受影响。

## 手动安装

1. 先安装 [NeoModLoader](https://github.com/WorldBoxOpenMods/ModLoader)
2. 下载本仓库（右上角「Code」→「Download ZIP」）
3. 解压到游戏目录下的 `Mods` 文件夹，把解压出的文件夹改名为 `WorldBall`，应该是 `Mods/WorldBall/mod.json`。Steam 版的游戏目录可以在库里右键 WorldBox →「管理」→「浏览本地文件」找到
4. 启动游戏，NML 会自动编译加载
