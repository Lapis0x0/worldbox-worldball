using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace WorldboxGlobe
{
    // 「星球」模板：新建世界时多一个模板，生成的地图可以无缝卷成一个球。
    // - 宽高 2:1，横向对应经度 360°，纵向对应纬度 180°，卷成球时横竖比例一致
    // - 地图左右两边首尾相接（见 Seam），大陆可以跨过接缝
    // - 两极是冰原（原版的冰面和永冻土，季节变化也不会化）。冰原的边界和浮冰按多层噪声起伏，
    //   高纬度原有的陆地留下一部分，铺成雪原，山照原版冻上
    // 地形直接在球面上生成：每一格先换算成球面上的点，在那里取噪声决定高度，所以卷回球上时大陆形状自然，
    // 平面地图上高纬度的陆地会被横向拉宽（和现实的世界地图一样）。之后的群系、植被照原版。
    // 最后在存档里记一个标记，地球仪只对这样的世界生效
    public static class Planet
    {
        public const string Id = "globe_planet";
        const string Flag = "globe_planet";
        const string Icon = "ui/globe_template_planet";

        // 纬度（以 90° 为 1）：LandFade 起陆地逐渐压低，到两极高度只剩 PoleKeep，陆地少一些
        const float LandFade = 0.55f;
        const float PoleKeep = 0.8f;
        // 冰原：纬度加上噪声（以 0.5 为中）× IceRough 超过 IceEdge 的地方结冰；PoleCap 以上全是冰
        const float IceEdge = 0.8f;
        const float IceRough = 0.35f;
        const float PoleCap = 0.94f;
        const float IceScale = 48f;     // 冰原边界噪声最粗一层的尺度（格）
        const int IceOctaves = 5;
        // 地形：噪声最粗一层的尺度（格）、层数，扭曲取样点的幅度（以最粗一层的尺度为 1），陆地占多少
        const float LandScale = 160f;
        const int LandOctaves = 6;
        const float Warp = 0.6f;
        const float LandShare = 0.33f;
        const int SeaLevel = 98;        // 原版沙滩的最低高度，以下是海
        const int Upland = 170;         // 没有山的陆地最高到这里（原版 128 起是高地，199 起是丘陵）
        // 山脉：山脊噪声的尺度（格）、山脊有多尖、山能比高地再高多少；
        // 另一层低频噪声决定哪里有山脉，RangeFrom 到 RangeTo 之间逐渐出现；离海岸 InlandReach（陆地高度的比例）以内山会压低
        const float RidgeScale = 90f;
        const float RidgeSharp = 3f;
        const float RidgeFloor = 0.3f;
        const int RidgeHeight = 110;
        const float RangeScale = 220f;
        const float RangeFrom = 0.45f;
        const float RangeTo = 0.62f;
        const float InlandReach = 0.25f;
        const int IceHeight = 80;       // 浅海的高度，冰面铺在浅海上

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
            template.path_icon = Icon;
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
            template.values.random_shapes_amount = 0;   // 原版随机形状是在平面上画的，卷到球上高纬度会变形
            template.allow_edit_random_shapes = false;
            template.perlin_replace = continent.perlin_replace;
            foreach (ButtonClickMaptemplate button in Resources.FindObjectsOfTypeAll<ButtonClickMaptemplate>())
            {
                AddButton(button);
                // 已经建好的按钮在 Awake 里设过预览图，热重载换了图标要重新设
                if (button.name == Id) button.transform.Find("preview_icon").GetComponent<UnityEngine.UI.Image>().sprite = SpriteTextureLoader.getSprite(Icon);
            }
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
                    try
                    {
                        Seam.Link();
                        Shape();
                    }
                    catch (Exception e) { Guard.Fail("星球·地形", e); }
                }, "Planet Shape");
            }
        }

        // 原版按平面上的柏林噪声把一部分地块换成别的地形（大陆模板用来铺不同的土壤），左右两边对不上。
        // 星球改在球面上取噪声，尺度和原版相当
        [HarmonyPatch(typeof(GeneratorTool), nameof(GeneratorTool.ApplyPerlinReplace))]
        static class Patch_PerlinReplace
        {
            static int _salt;

            static bool Prefix(PerlinReplaceContainer pContainer)
            {
                if (MapGenerator.template?.id != Id || !Guard.On) return true;
                try
                {
                    float scale = MapBox.width / Mathf.Max(0.01f, pContainer.scale * Mathf.Max(1, MapBox.width / Mathf.Max(1, MapBox.height)));
                    int salt = 40 + _salt++ % 50;
                    foreach (WorldTile tile in World.world.tiles_list)
                    {
                        int value = (int)(Fbm(OnSphere(tile), scale, 2, salt) * 255f);
                        foreach (PerlinReplaceOption option in pContainer.options)
                            if (value > option.replace_height_value && tile.main_type.IsType(option.from))
                                tile.setTileType(option.to);
                    }
                    return false;
                }
                catch (Exception e) { Guard.Fail("星球·地形", e); return true; }
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

        static int _seed;

        // 这一格在冰原里：越靠近两极越容易结冰，边界由多层噪声决定，所以有大的起伏也有碎冰
        static bool Icy(WorldTile pTile)
        {
            float lat = Mathf.Abs(Latitude(pTile));
            if (lat > PoleCap) return true;
            if (lat + 0.5f * IceRough < IceEdge) return false;
            return lat + (Fbm(OnSphere(pTile), IceScale, IceOctaves, 7) - 0.5f) * IceRough > IceEdge;
        }

        // 地块在球面上的位置：横向是经度、纵向是纬度，球的半径是地图宽 / 2π（格）
        static Vector3 OnSphere(WorldTile pTile)
        {
            float lon = 2f * Mathf.PI * (pTile.x + 0.5f) / MapBox.width;
            float lat = Latitude(pTile) * Mathf.PI / 2f;
            float radius = MapBox.width / (2f * Mathf.PI);
            return new Vector3(Mathf.Cos(lat) * Mathf.Cos(lon), Mathf.Cos(lat) * Mathf.Sin(lon), Mathf.Sin(lat)) * radius;
        }

        // 球面上的高度噪声：取样点先按另外三层噪声扭一扭，海岸线不那么圆
        static float Land(Vector3 pPoint)
        {
            float reach = Warp * LandScale;
            var warped = new Vector3(
                pPoint.x + (Fbm(pPoint, LandScale, 3, 11) - 0.5f) * 2f * reach,
                pPoint.y + (Fbm(pPoint, LandScale, 3, 12) - 0.5f) * 2f * reach,
                pPoint.z + (Fbm(pPoint, LandScale, 3, 13) - 0.5f) * 2f * reach);
            return Fbm(warped, LandScale, LandOctaves, 0);
        }

        static void Shape()
        {
            _seed = UnityEngine.Random.Range(0, int.MaxValue);
            WorldTile[] tiles = World.world.tiles_list;
            var noise = new float[tiles.Length];
            for (int i = 0; i < tiles.Length; i++) noise[i] = Land(OnSphere(tiles[i]));

            // 按分位数定海平面：噪声最高的 LandShare 是陆地，海和陆地各自按排名铺满原版的高度范围
            var sorted = (float[])noise.Clone();
            Array.Sort(sorted);
            float low = sorted[0], high = sorted[sorted.Length - 1];
            float sea = sorted[Mathf.Clamp(Mathf.RoundToInt(sorted.Length * (1f - LandShare)), 0, sorted.Length - 1)];

            for (int i = 0; i < tiles.Length; i++)
            {
                WorldTile tile = tiles[i];
                float n = noise[i];
                float height;
                if (n < sea) height = (SeaLevel - 1) * (n - low) / Mathf.Max(1e-6f, sea - low);
                else
                {
                    // 陆地：大陆噪声只给平原到高地的起伏，山另由山脊噪声叠上去
                    float inland = (n - sea) / Mathf.Max(1e-6f, high - sea);
                    height = SeaLevel + (Upland - SeaLevel) * inland + Mountain(OnSphere(tile), inland);
                }
                float lat = Mathf.Abs(Latitude(tile));
                float keep = Mathf.Lerp(1f, PoleKeep, Smooth(LandFade, 1f, lat));
                int h = Mathf.RoundToInt(height * keep);
                // 冰原上的海抬到浅海，之后铺上冰面；陆地留着，之后铺成雪原
                if (Icy(tile)) h = Mathf.Max(h, IceHeight);
                tile.Height = Mathf.Clamp(h, 0, 255);
            }
        }

        // 山脊噪声：噪声值越接近中线越高，取高次方后只剩细长的山脊；每层叠加，有主脊也有支脉
        static float Mountain(Vector3 pPoint, float pInland)
        {
            float range = Smooth(RangeFrom, RangeTo, Fbm(pPoint, RangeScale, 2, 21));
            if (range <= 0f) return 0f;
            float sum = 0f, amp = 1f, total = 0f, freq = 1f / RidgeScale;
            for (int o = 0; o < 4; o++)
            {
                float v = Value(pPoint.x * freq, pPoint.y * freq, pPoint.z * freq, 30 * 16 + o);
                sum += amp * Mathf.Pow(1f - Mathf.Abs(2f * v - 1f), RidgeSharp);
                total += amp;
                amp *= 0.5f;
                freq *= 2f;
            }
            // 山脊噪声处处都有一点值，低于 RidgeFloor 的部分不算山，否则山脉一带全是丘陵
            return RidgeHeight * Smooth(RidgeFloor, 1f, sum / total) * range * Smooth(0f, InlandReach, pInland);
        }

        // 多层值噪声，0 到 1：从尺度 pScale（格）开始，每层尺度减半、幅度减半
        static float Fbm(Vector3 pPoint, float pScale, int pOctaves, int pSalt)
        {
            float sum = 0f, amp = 1f, total = 0f, freq = 1f / pScale;
            for (int o = 0; o < pOctaves; o++)
            {
                sum += amp * Value(pPoint.x * freq, pPoint.y * freq, pPoint.z * freq, pSalt * 16 + o);
                total += amp;
                amp *= 0.5f;
                freq *= 2f;
            }
            return sum / total;
        }

        // 三维值噪声：整数格点上取哈希出的随机值，格点之间平滑插值
        static float Value(float pX, float pY, float pZ, int pOctave)
        {
            int x0 = Mathf.FloorToInt(pX), y0 = Mathf.FloorToInt(pY), z0 = Mathf.FloorToInt(pZ);
            float fx = Fade(pX - x0), fy = Fade(pY - y0), fz = Fade(pZ - z0);
            float Lerp(float a, float b, float t) => a + (b - a) * t;
            float Corner(int dx, int dy, int dz) => Hash(x0 + dx, y0 + dy, z0 + dz, pOctave);
            return Lerp(
                Lerp(Lerp(Corner(0, 0, 0), Corner(1, 0, 0), fx), Lerp(Corner(0, 1, 0), Corner(1, 1, 0), fx), fy),
                Lerp(Lerp(Corner(0, 0, 1), Corner(1, 0, 1), fx), Lerp(Corner(0, 1, 1), Corner(1, 1, 1), fx), fy),
                fz);
        }

        static float Fade(float t) => t * t * t * (t * (t * 6f - 15f) + 10f);

        static float Hash(int pX, int pY, int pZ, int pOctave)
        {
            unchecked
            {
                uint h = (uint)_seed ^ (uint)pOctave * 0x27d4eb2du;
                h ^= (uint)pX * 0x8da6b343u;
                h ^= (uint)pY * 0xd8163841u;
                h ^= (uint)pZ * 0xcb1ab31fu;
                h ^= h >> 15;
                h *= 0x2c1b3c6du;
                h ^= h >> 12;
                h *= 0x297a2d39u;
                h ^= h >> 15;
                return (h & 0xffffff) / (float)0xffffff;
            }
        }

        // 铺原版不会化的冰雪地形：水面是冰，陆地是永冻土；原版 freeze() 只是临时冻住，转暖就化
        static void Freeze()
        {
            foreach (WorldTile tile in World.world.tiles_list)
            {
                if (!Icy(tile)) continue;
                TopTileType top = Frozen(tile.main_type?.id);
                if (top != null) MapAction.terraformTop(tile, top, true);
                else tile.freeze();
            }
        }

        static TopTileType Frozen(string pMain)
        {
            switch (pMain)
            {
                case "shallow_waters": return TopTileLibrary.ice;
                case "sand": return TopTileLibrary.snow_sand;
                case "soil_low": return TopTileLibrary.permafrost_low;
                case "soil_high": return TopTileLibrary.permafrost_high;
                default: return null;
            }
        }
    }
}
