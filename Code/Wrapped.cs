using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace WorldboxGlobe
{
    // 东西无缝下按坐标做的事：原版按直线算两点隔多远、朝哪边，按坐标取地块、区块、大块时出了地图左右边就取不到，
    // 接缝对面的东西会被算成隔着整张地图，或者根本找不到（找附近的敌人是按大块往四周取的）。
    // 原版的这些函数都标了强制内联，函数体会被抄进调用它的方法里，直接改函数本身不生效。
    // 所以反过来改调用方：启动时扫一遍原版代码，找出调用这些函数的方法，把调用换成这里的同名函数：
    // - 距离、角度、两点间取点：先把第二个点挪到离第一个点近的那一侧（差整数个图宽），再交给原版去算
    // - 取地块、区块、大块：横坐标绕回地图里再取
    // 不是左右相接的世界，原样交给原版。
    // 只有横向差值超过半个图宽才会挪，按区块、大块编号算的小距离不受影响；屏幕坐标的几处另外排除
    public static class Wrapped
    {
        // 用屏幕坐标算距离的，不能按地图宽绕；ZoneCamera 靠相机角落取不到地块来判断越过了地图边
        static readonly HashSet<string> SkipTypes = new HashSet<string>
        {
            nameof(MoveCamera), nameof(NameplateManager), nameof(CursorSpeed), nameof(DebugToolLibrary), "BenchmarkDist", nameof(ZoneCamera)
        };
        static readonly HashSet<string> SkipMethods = new HashSet<string>
        {
            "PlayerControl.getDistanceBetweenOriginAndCurrentTouch", "PlayerControl.checkClickTouchInspectSelect"
        };

        static Dictionary<MethodInfo, MethodInfo> _swap;

        public static void Patch(Harmony pHarmony)
        {
            var clock = Stopwatch.StartNew();
            _swap = new Dictionary<MethodInfo, MethodInfo>();
            foreach (MethodInfo ours in typeof(Wrapped).GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                SwapAttribute swap = ours.GetCustomAttribute<SwapAttribute>();
                if (swap == null) continue;
                Type[] args = Array.ConvertAll(ours.GetParameters(), p => p.ParameterType);
                const BindingFlags any = BindingFlags.Public | BindingFlags.NonPublic;
                // 第一个参数是原版类型的，对应原版的实例方法
                MethodInfo vanilla = args.Length > 0 && args[0] == swap.Type
                    ? swap.Type.GetMethod(ours.Name, any | BindingFlags.Instance, null, args[1..], null)
                    : swap.Type.GetMethod(ours.Name, any | BindingFlags.Static, null, args, null);
                if (vanilla == null || vanilla.ReturnType != ours.ReturnType)
                {
                    Debug.LogError($"[星球] 找不到原版的 {swap.Type.Name}.{ours.Name}");
                    continue;
                }
                _swap[vanilla] = ours;
            }
            var tokens = new HashSet<int>();
            foreach (MethodInfo vanilla in _swap.Keys) tokens.Add(vanilla.MetadataToken);

            int patched = 0, failed = 0;
            foreach (MethodBase method in Callers(tokens))
            {
                try
                {
                    pHarmony.Patch(method, transpiler: new HarmonyMethod(typeof(Wrapped), nameof(Swap)));
                    patched++;
                }
                catch (Exception e)
                {
                    failed++;
                    Debug.LogWarning($"[星球] 无缝替换跳过 {method.DeclaringType?.Name}.{method.Name}：{e.Message}");
                }
            }
            Debug.Log($"[星球] 无缝替换：{patched} 个方法，{failed} 个跳过，用时 {clock.ElapsedMilliseconds} 毫秒");
        }

        // 原版里调用了这些函数的方法：直接在 IL 字节里找 call、callvirt 指令后面跟着它们的元数据标记，偶尔误认的也只是多打一次空补丁
        static IEnumerable<MethodBase> Callers(HashSet<int> pTokens)
        {
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            Type[] types;
            try { types = typeof(Toolbox).Assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = Array.FindAll(e.Types, t => t != null); }
            foreach (Type type in types)
            {
                if (type.ContainsGenericParameters || SkipTypes.Contains(type.Name)) continue;
                var methods = new List<MethodBase>(type.GetMethods(all));
                methods.AddRange(type.GetConstructors(all));
                foreach (MethodBase method in methods)
                {
                    if (method.IsAbstract || method.ContainsGenericParameters) continue;
                    if (method is MethodInfo info && _swap.ContainsKey(info)) continue;
                    if (SkipMethods.Contains(type.Name + "." + method.Name)) continue;
                    byte[] il;
                    try { il = method.GetMethodBody()?.GetILAsByteArray(); }
                    catch { continue; }
                    if (il != null && Calls(il, pTokens)) yield return method;
                }
            }
        }

        static bool Calls(byte[] pIl, HashSet<int> pTokens)
        {
            for (int i = 0; i + 4 < pIl.Length; i++)
            {
                if (pIl[i] != 0x28 && pIl[i] != 0x6F) continue;   // call、callvirt
                int token = pIl[i + 1] | pIl[i + 2] << 8 | pIl[i + 3] << 16 | pIl[i + 4] << 24;
                if (pTokens.Contains(token)) return true;
            }
            return false;
        }

        static IEnumerable<CodeInstruction> Swap(IEnumerable<CodeInstruction> pCode)
        {
            foreach (CodeInstruction code in pCode)
            {
                if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) && code.operand is MethodInfo method && _swap.TryGetValue(method, out MethodInfo ours))
                {
                    // 实例方法换成第一个参数是实例的静态方法，栈上的参数一样
                    code.opcode = OpCodes.Call;
                    code.operand = ours;
                }
                yield return code;
            }
        }

        // pX 挪到离 pTo 近的那一侧
        static float Near(float pX, float pTo) => Seam.On ? Seam.Near(pX, pTo) : pX;

        static int Near(int pX, int pTo)
        {
            if (!Seam.On) return pX;
            int w = MapBox.width, d = pX - pTo;
            if (d > w / 2) return pX - w;
            if (d < -w / 2) return pX + w;
            return pX;
        }

        // 横坐标绕回 0 到 pCount 之间
        static int Fold(int pX, int pCount)
        {
            pX %= pCount;
            return pX < 0 ? pX + pCount : pX;
        }

        // 替换原版 pType 上的同名函数
        [AttributeUsage(AttributeTargets.Method)]
        class SwapAttribute : Attribute
        {
            public readonly Type Type;
            public SwapAttribute(Type pType) => Type = pType;
        }

        [Swap(typeof(MapBox))] public static WorldTile GetTile(MapBox pWorld, int pX, int pY) => pWorld.GetTile(Seam.On ? Fold(pX, MapBox.width) : pX, pY);
        [Swap(typeof(MapChunkManager))] public static MapChunk get(MapChunkManager pChunks, int pX, int pY) => pChunks.get(Seam.On ? Fold(pX, pChunks._get_amount_x) : pX, pY);
        [Swap(typeof(ZoneCalculator))] public static TileZone getZone(ZoneCalculator pZones, int pX, int pY) => pZones.getZone(Seam.On ? Fold(pX, pZones.zones_total_x) : pX, pY);

        [Swap(typeof(Toolbox))] public static float DistVec3(Vector3 pT1, Vector3 pT2) { pT2.x = Near(pT2.x, pT1.x); return Toolbox.DistVec3(pT1, pT2); }
        [Swap(typeof(Toolbox))] public static float DistVec2(Vector2Int pT1, Vector2Int pT2) { pT2.x = Near(pT2.x, pT1.x); return Toolbox.DistVec2(pT1, pT2); }
        [Swap(typeof(Toolbox))] public static float DistVec2Float(Vector2 pT1, Vector2 pT2) { pT2.x = Near(pT2.x, pT1.x); return Toolbox.DistVec2Float(pT1, pT2); }
        [Swap(typeof(Toolbox))] public static float DistTile(WorldTile pT1, WorldTile pT2) => Toolbox.Dist(pT1.x, pT1.y, Near(pT2.x, pT1.x), pT2.y);
        [Swap(typeof(Toolbox))] public static float Dist(float x1, float y1, float x2, float y2) => Toolbox.Dist(x1, y1, Near(x2, x1), y2);
        [Swap(typeof(Toolbox))] public static float Dist(int x1, int y1, int x2, int y2) => Toolbox.Dist(x1, y1, Near(x2, x1), y2);
        [Swap(typeof(Toolbox))] public static float SquaredDist(float x1, float y1, float x2, float y2) => Toolbox.SquaredDist(x1, y1, Near(x2, x1), y2);
        [Swap(typeof(Toolbox))] public static int SquaredDist(int x1, int y1, int x2, int y2) => Toolbox.SquaredDist(x1, y1, Near(x2, x1), y2);
        [Swap(typeof(Toolbox))] public static int SquaredDistTile(WorldTile pT1, WorldTile pT2) => Toolbox.SquaredDist(pT1.x, pT1.y, Near(pT2.x, pT1.x), pT2.y);
        [Swap(typeof(Toolbox))] public static float SquaredDistVec2Float(Vector2 pT1, Vector2 pT2) { pT2.x = Near(pT2.x, pT1.x); return Toolbox.SquaredDistVec2Float(pT1, pT2); }
        [Swap(typeof(Toolbox))] public static int SquaredDistVec2(Vector2Int pT1, Vector2Int pT2) { pT2.x = Near(pT2.x, pT1.x); return Toolbox.SquaredDistVec2(pT1, pT2); }
        [Swap(typeof(Toolbox))] public static float SquaredDistVec3(Vector3 pT1, Vector3 pT2) { pT2.x = Near(pT2.x, pT1.x); return Toolbox.SquaredDistVec3(pT1, pT2); }
        [Swap(typeof(Toolbox))] public static float getAngle(float pX1, float pY1, float pX2, float pY2) => Toolbox.getAngle(pX1, pY1, Near(pX2, pX1), pY2);
        [Swap(typeof(Toolbox))] public static float getAngleDegrees(float pX1, float pY1, float pX2, float pY2) => Toolbox.getAngleDegrees(pX1, pY1, Near(pX2, pX1), pY2);
        [Swap(typeof(Toolbox))] public static float getAngleDegrees(Vector2 pVec1, Vector2 pVec2) { pVec2.x = Near(pVec2.x, pVec1.x); return Toolbox.getAngleDegrees(pVec1, pVec2); }
        [Swap(typeof(Toolbox))] public static Quaternion getEulerAngle(float pX1, float pY1, float pX2, float pY2) => Toolbox.getEulerAngle(pX1, pY1, Near(pX2, pX1), pY2);
        [Swap(typeof(Toolbox))] public static Quaternion getEulerAngle(Vector2 pVec1, Vector2 pVec2) { pVec2.x = Near(pVec2.x, pVec1.x); return Toolbox.getEulerAngle(pVec1, pVec2); }
        [Swap(typeof(Toolbox))] public static Vector3 getNewPoint(float pX1, float pY1, float pX2, float pY2, float pDist, bool pConvertNegative) => Toolbox.getNewPoint(pX1, pY1, Near(pX2, pX1), pY2, pDist, pConvertNegative);
        [Swap(typeof(Toolbox))] public static Vector2 getNewPointVec2(Vector2 pVec1, Vector2 pVec2, float pDist, bool pConvertNegative) { pVec2.x = Near(pVec2.x, pVec1.x); return Toolbox.getNewPointVec2(pVec1, pVec2, pDist, pConvertNegative); }
        // 这个重载自己也是强制内联的，调用它的地方抄走的是没替换过的原版，所以整个照原版重写一遍
        [Swap(typeof(Toolbox))]
        public static Actor getClosestActor(HashSet<Actor> pCollection, WorldTile pTile)
        {
            Actor result = null;
            int best = int.MaxValue;
            foreach (Actor actor in pCollection)
            {
                if (actor.isRekt()) continue;
                int dist = SquaredDistTile(pTile, actor.current_tile);
                if (dist < best)
                {
                    best = dist;
                    result = actor;
                }
            }
            return result;
        }

        [Swap(typeof(Toolbox))] public static Vector2 getNewPointVec2(float pX1, float pY1, float pX2, float pY2, float pDist, bool pConvertNegative) => Toolbox.getNewPointVec2(pX1, pY1, Near(pX2, pX1), pY2, pDist, pConvertNegative);
    }
}
