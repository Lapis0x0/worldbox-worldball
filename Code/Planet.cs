using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WorldboxGlobe
{
    // 「星球」模板：新建世界时多一个模板，生成的地图可以无缝卷成一个球。
    // - 宽高 2:1，横向对应经度 360°，纵向对应纬度 180°，卷成球时横竖比例一致
    // - 地图左右两边（经度 180° 处）是一条贯通南北的大洋，卷起来接缝在海里
    // - 两极冻成冰原（原版的冰雪地形），陆地集中在中低纬度
    // 地形先照大陆模板的噪声生成，再按经纬度调整高度；最后在存档里记一个标记，地球仪只对这样的世界生效
    public static class Planet
    {
        public const string Id = "globe_planet";
        const string Flag = "globe_planet";

        // 接缝大洋：离接缝不到半个地图宽的 SeamOcean 是海，到 SeamLand 恢复原来的地形
        const float SeamOcean = 0.07f;
        const float SeamLand = 0.22f;
        // 纬度（以 90° 为 1）：LandFade 起陆地逐渐沉入海，到 LandEnd 全是海
        const float LandFade = 0.55f;
        const float LandEnd = 0.75f;
        // 冰原的边界在 IceEdge 上下起伏 IceWobble
        const float IceEdge = 0.8f;
        const float IceWobble = 0.05f;

        static bool _sizing;

        public static void Init()
        {
            MapGenTemplate template = AssetManager.map_gen_templates.get(Id);
            if (template == null)
            {
                template = new MapGenTemplate { id = Id };
                AssetManager.map_gen_templates.add(template);
            }
            MapGenTemplate continent = AssetManager.map_gen_templates.get("continent");
            template.path_icon = continent.path_icon;
            template.freeze_mountains = true;
            template.show_reset_button = false;
            // 圆形、方形边缘和中心的环、湖都是按正方形地图设计的，由这里的经纬度规则代替
            template.allow_edit_round_edges = false;
            template.allow_edit_square_edges = false;
            template.allow_edit_ring_effect = false;
            template.allow_edit_center_lake = false;
            template.allow_edit_center_land = false;
            foreach (FieldInfo field in typeof(MapGenValues).GetFields(BindingFlags.Public | BindingFlags.Instance))
                field.SetValue(template.values, field.GetValue(continent.values));
            template.values.gradient_round_edges = false;
            template.values.square_edges = false;
            template.values.ring_effect = false;
            template.values.add_center_lake = false;
            template.values.add_center_gradient_land = false;
            template.perlin_replace = continent.perlin_replace;
            foreach (ButtonClickMaptemplate button in Resources.FindObjectsOfTypeAll<ButtonClickMaptemplate>())
                AddButton(button);
        }

        public static bool IsPlanet()
        {
            if (World.world?.map_stats?.custom_data == null) return false;
            World.world.map_stats.custom_data.get(Flag, out bool planet);
            return planet;
        }

        // 新建世界的模板按钮是界面里预先摆好的，按名字找模板。复制一个「大陆」按钮改名，放在它后面
        static void AddButton(ButtonClickMaptemplate pButton)
        {
            if (pButton == null || pButton.name != "continent") return;
            Transform parent = pButton.transform.parent;
            if (parent == null || parent.Find(Id) != null) return;
            // 先复制到一个停用的对象下面，改好名字再挂过去，按钮的 Awake 才会按新名字找到模板
            var holder = new GameObject("globe_holder");
            holder.SetActive(false);
            GameObject copy = UnityEngine.Object.Instantiate(pButton.gameObject, holder.transform);
            copy.name = Id;
            copy.transform.SetParent(parent, false);
            copy.transform.SetSiblingIndex(pButton.transform.GetSiblingIndex() + 1);
            UnityEngine.Object.Destroy(holder);
        }

        [HarmonyPatch(typeof(ButtonClickMaptemplate), "Awake")]
        static class Patch_Button
        {
            static void Postfix(ButtonClickMaptemplate __instance)
            {
                if (!Guard.On) return;
                try { AddButton(__instance); }
                catch (Exception e) { Guard.Fail("星球·模板按钮", e); }
            }
        }

        // 用星球模板新建世界时，地图改成 2:1，面积和所选尺寸相近
        [HarmonyPatch(typeof(MapBox), nameof(MapBox.generateNewMap))]
        static class Patch_Generate
        {
            static void Prefix() => _sizing = Config.current_map_template == Id;
        }

        [HarmonyPatch(typeof(MapBox), nameof(MapBox.setMapSize))]
        static class Patch_Size
        {
            static void Prefix(ref int pWidth, ref int pHeight)
            {
                if (!_sizing) return;
                _sizing = false;
                if (!Guard.On) return;
                try
                {
                    int height = Mathf.Max(2, Mathf.RoundToInt(pHeight / Mathf.Sqrt(2f)));
                    pWidth = height * 2;
                    pHeight = height;
                }
                catch (Exception e) { Guard.Fail("星球·地图尺寸", e); }
            }
        }

        // 噪声生成完、换算成地块之前，按经纬度调整高度
        [HarmonyPatch(typeof(MapGenerator), "schedulePerlinNoiseMap")]
        static class Patch_Shape
        {
            static void Postfix()
            {
                if (MapGenerator.template?.id != Id) return;
                SmoothLoader.add(delegate
                {
                    if (!Guard.On) return;
                    try { Shape(); }
                    catch (Exception e) { Guard.Fail("星球·地形", e); }
                }, "Planet Shape");
            }
        }

        // 整个世界生成完：冻上两极，记下这是一颗星球
        [HarmonyPatch(typeof(MapGenerator), nameof(MapGenerator.prepare))]
        static class Patch_Finish
        {
            static void Postfix()
            {
                if (MapGenerator.template?.id != Id) return;
                SmoothLoader.add(delegate
                {
                    if (!Guard.On) return;
                    try
                    {
                        Freeze();
                        World.world.map_stats.custom_data.set(Flag, true);
                    }
                    catch (Exception e) { Guard.Fail("星球·冰原", e); }
                }, "Planet Poles");
            }
        }

        static float Smooth(float pFrom, float pTo, float pValue)
        {
            float t = Mathf.Clamp01((pValue - pFrom) / (pTo - pFrom));
            return t * t * (3f - 2f * t);
        }

        // 纬度，以 90° 为 1，南负北正
        static float Latitude(WorldTile pTile) => (pTile.y + 0.5f) / MapBox.height * 2f - 1f;

        // 离接缝多远，以半个地图宽为 1
        static float FromSeam(WorldTile pTile)
        {
            float x = pTile.x + 0.5f;
            return Mathf.Min(x, MapBox.width - x) / (MapBox.width / 2f);
        }

        // 冰原的边界：沿经度起伏，取样点绕成一圈，地图左右两边接得上
        static float IceLine(WorldTile pTile, float pSeedX, float pSeedY)
        {
            float angle = 2f * Mathf.PI * pTile.x / MapBox.width;
            float noise = Mathf.PerlinNoise(pSeedX + Mathf.Cos(angle) * 3f, pSeedY + Mathf.Sin(angle) * 3f);
            return IceEdge + (noise - 0.5f) * 2f * IceWobble;
        }

        static float _seedX, _seedY;

        static void Shape()
        {
            _seedX = UnityEngine.Random.Range(0f, 1000f);
            _seedY = UnityEngine.Random.Range(0f, 1000f);
            foreach (WorldTile tile in World.world.tiles_list)
            {
                float lat = Mathf.Abs(Latitude(tile));
                float keep = Smooth(SeamOcean, SeamLand, FromSeam(tile)) * (1f - Smooth(LandFade, LandEnd, lat));
                int height = Mathf.RoundToInt(tile.Height * keep);
                // 冰原：抬到浅海到低地之间，之后冻成冰面和雪地
                if (lat > IceLine(tile, _seedX, _seedY)) height = Mathf.Max(height, 75 + (int)(Mathf.PerlinNoise(tile.x * 0.08f, tile.y * 0.08f) * 50f));
                tile.Height = height;
            }
        }

        static void Freeze()
        {
            foreach (WorldTile tile in World.world.tiles_list)
                if (Mathf.Abs(Latitude(tile)) > IceLine(tile, _seedX, _seedY)) tile.freeze();
        }
    }
}
