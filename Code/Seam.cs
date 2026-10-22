using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldboxGlobe
{
    // 东西无缝：星球世界的地图左右两边首尾相接，和球上一样。
    // - 连通：地块、区块、大块的邻居表在左右两边绕到对面。原版的寻路、地区、岛屿、蔓延、群系都是按邻居表走的，跟着就通了
    // - 移动：单位一步跨过接缝时，先把下一步的目标挪到同一侧走过去，走到后再整体挪回地图里（正好差一个图宽，看不出来）；
    //   投射物同理，朝向、出拳按近的那一侧
    // - 距离、方向、按坐标取地块：见 Wrapped
    // 平面视角的画面和操作照原版，地图还是一张有左右边的图；拉远成球时自然是连着的
    // 上下不相接：球上从北边走出去是绕过极点，不是从南边冒出来
    public static class Seam
    {
        // 当前世界左右相接
        public static bool On { get; private set; }

        // 热重载、或者这个功能之前就建好的星球世界：现在接上
        public static void Init()
        {
            if (World.world != null && World.world.tiles_map != null && MapBox.width > 0 && Planet.IsPlanet()) Link();
        }

        // 横坐标绕回地图里
        public static float Fold(float pX)
        {
            float w = MapBox.width;
            if (pX < 0f) return pX + w;
            if (pX >= w) return pX - w;
            return pX;
        }

        // pX 的几个像（差整数个图宽）里离 pTo 最近的那个
        public static float Near(float pX, float pTo)
        {
            float w = MapBox.width;
            float d = pX - pTo;
            if (d > w / 2f) return pX - w;
            if (d < -w / 2f) return pX + w;
            return pX;
        }

        // 把左右两列的地块、区块、大块的邻居重新算一遍，算到对面。可以重复调用
        public static void Link()
        {
            MapBox world = World.world;
            int w = MapBox.width, h = MapBox.height;
            WorldTile[,] tiles = world.tiles_map;
            if (tiles[0, 0].tile_left == null)
            {
                var near = new List<WorldTile>(4);
                var all = new List<WorldTile>(8);
                WorldTile Tile(int x, int y) => y < 0 || y >= h ? null : tiles[(x + w) % w, y];
                for (int y = 0; y < h; y++)
                    foreach (WorldTile t in new[] { tiles[0, y], tiles[w - 1, y] })
                    {
                        t.world_edge = false;
                        Around(t.x, t.y, Tile, (n, d, diagonal) => t.addNeighbour(n, d, near, all, diagonal));
                        t.neighbours = near.ToArray();
                        t.neighboursAll = all.ToArray();
                        near.Clear();
                        all.Clear();
                    }

                ZoneCalculator zones = world.zone_calculator;
                int zw = zones.zones_total_x, zh = zones.zones_total_y;
                var zoneNear = new List<TileZone>(4);
                var zoneAll = new List<TileZone>(8);
                TileZone Zone(int x, int y) => y < 0 || y >= zh ? null : zones.map[(x + zw) % zw, y];
                for (int y = 0; y < zh; y++)
                    foreach (TileZone z in new[] { zones.map[0, y], zones.map[zw - 1, y] })
                    {
                        z.world_edge = false;
                        Around(z.x, z.y, Zone, (n, d, diagonal) => z.addNeighbour(n, d, zoneNear, zoneAll, diagonal));
                        z.neighbours = zoneNear.ToArray();
                        z.neighbours_all = zoneAll.ToArray();
                        zoneNear.Clear();
                        zoneAll.Clear();
                    }

                MapChunkManager chunks = world.map_chunk_manager;
                int cw = chunks._get_amount_x, ch = chunks._amount_y;
                var chunkNear = new List<MapChunk>(4);
                var chunkAll = new List<MapChunk>(8);
                MapChunk Chunk(int x, int y) => y < 0 || y >= ch ? null : chunks._map[(x + cw) % cw, y];
                for (int y = 0; y < ch; y++)
                    foreach (MapChunk c in new[] { chunks._map[0, y], chunks._map[cw - 1, y] })
                    {
                        c.world_edge = false;
                        Around(c.x, c.y, Chunk, (n, d, diagonal) => c.addNeighbour(n, d, chunkNear, chunkAll, diagonal));
                        c.neighbours = chunkNear.ToArray();
                        c.neighbours_all = chunkAll.ToArray();
                        chunkNear.Clear();
                        chunkAll.Clear();
                    }
                // 斜角的连接要等两列的邻居都换好再算
                for (int y = 0; y < ch; y++)
                {
                    chunks._map[0, y].generateEdgeConnections();
                    chunks._map[cw - 1, y].generateEdgeConnections();
                }
                chunks.allDirty();
            }
            On = true;
        }

        // 照原版的顺序列出上下左右和四个斜角
        static void Around<T>(int pX, int pY, Func<int, int, T> pGet, Action<T, TileDirection, bool> pAdd)
        {
            pAdd(pGet(pX - 1, pY), TileDirection.Left, false);
            pAdd(pGet(pX + 1, pY), TileDirection.Right, false);
            pAdd(pGet(pX, pY - 1), TileDirection.Down, false);
            pAdd(pGet(pX, pY + 1), TileDirection.Up, false);
            pAdd(pGet(pX - 1, pY - 1), TileDirection.Null, true);
            pAdd(pGet(pX - 1, pY + 1), TileDirection.Null, true);
            pAdd(pGet(pX + 1, pY - 1), TileDirection.Null, true);
            pAdd(pGet(pX + 1, pY + 1), TileDirection.Null, true);
        }

        // 新建或读取世界前先断开，是星球再由生成或读档接上
        [HarmonyPatch(typeof(MapBox), nameof(MapBox.addClearWorld))]
        static class Patch_Clear
        {
            static void Prefix() => On = false;
        }

        // 读档：世界的标记读进来时，地块、区块、大块都已经建好，是星球就接上
        [HarmonyPatch(typeof(MapStats), nameof(MapStats.load))]
        static class Patch_Load
        {
            static void Postfix()
            {
                if (!Guard.On) return;
                try
                {
                    if (Planet.IsPlanet()) Link();
                }
                catch (Exception e) { Guard.Fail("无缝·读档", e); }
            }
        }

        // 单位走一步：先把自己挪回地图里，再把下一步的目标挪到离自己近的那一侧
        [HarmonyPatch(typeof(Actor), nameof(Actor.moveTo))]
        static class Patch_Step
        {
            static void Prefix(Actor __instance)
            {
                if (!On || !Guard.On) return;
                try
                {
                    Vector2 pos = __instance.current_position;
                    float x = Fold(pos.x);
                    if (x != pos.x) __instance.current_position = new Vector2(x, pos.y);
                }
                catch (Exception e) { Guard.Fail("无缝·移动", e); }
            }

            static void Postfix(Actor __instance)
            {
                if (!On || !Guard.On) return;
                try
                {
                    Vector2 next = __instance.next_step_position;
                    float x = Near(next.x, __instance.current_position.x);
                    if (x != next.x) __instance.next_step_position = new Vector2(x, next.y);
                }
                catch (Exception e) { Guard.Fail("无缝·移动", e); }
            }
        }

        // 转身看向、出拳的方向：目标在接缝对面时按近的那一侧
        [HarmonyPatch(typeof(Actor), nameof(Actor.lookTowardsPosition))]
        static class Patch_Look
        {
            static void Prefix(Actor __instance, ref Vector2 pDirection)
            {
                if (!On || !Guard.On) return;
                try { pDirection.x = Near(pDirection.x, __instance.current_position.x); }
                catch (Exception e) { Guard.Fail("无缝·朝向", e); }
            }
        }

        [HarmonyPatch(typeof(Actor), nameof(Actor.punchTargetAnimation))]
        static class Patch_Punch
        {
            static void Prefix(Actor __instance, ref Vector3 pDirection)
            {
                if (!On || !Guard.On) return;
                try { pDirection.x = Near(pDirection.x, __instance.current_position.x); }
                catch (Exception e) { Guard.Fail("无缝·朝向", e); }
            }
        }

        // 投射物：目标在接缝对面时朝近的那一侧飞
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.start))]
        static class Patch_Launch
        {
            static void Prefix(Vector3 pLaunchPosition, ref Vector3 pTargetPosition)
            {
                if (!On || !Guard.On) return;
                try { pTargetPosition.x = Near(pTargetPosition.x, pLaunchPosition.x); }
                catch (Exception e) { Guard.Fail("无缝·投射物", e); }
            }
        }

        // 飞出地图左右边的投射物连同起点、目标一起挪回来，落点和沿途击中都按地图里的地块算
        [HarmonyPatch(typeof(Projectile), nameof(Projectile.update))]
        static class Patch_Flight
        {
            static void Prefix(Projectile __instance)
            {
                if (!On || !Guard.On) return;
                try
                {
                    float x = __instance._current_position_3d.x;
                    float folded = Fold(x);
                    if (folded == x) return;
                    float shift = folded - x;
                    __instance._current_position_3d.x = folded;
                    __instance._vector_start.x += shift;
                    __instance._vector_target.x += shift;
                }
                catch (Exception e) { Guard.Fail("无缝·投射物", e); }
            }
        }
    }
}
