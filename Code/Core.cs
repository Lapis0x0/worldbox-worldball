using System;
using System.Diagnostics;
using System.IO;
using HarmonyLib;
using NeoModLoader.utils;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace WorldboxGlobe
{
    // 模组的全部功能都从这里启动和停止。热重载时旧版本 Stop，新编译的版本 Start，所以 Start 必须能重复执行
    public static class Core
    {
        const string HarmonyId = "lapis0x0.worldball";

        static Harmony _harmony;

        public static void Start(string pFolder)
        {
            LoadIcons(pFolder);
            _harmony = new Harmony(HarmonyId);
            _harmony.PatchAll(typeof(Core).Assembly);
            Wrapped.Patch(_harmony);
            Planet.Init();
            Globe.Init();
            Seam.Init();
        }

        public static void Stop()
        {
            _harmony?.UnpatchSelf();
            _harmony = null;
            Globe.Stop();
        }

        // NML 只在游戏启动时加载 GameResources/ 下的图片；这里再注册一遍，热重载时改过的图标也能换上。
        // 原版 SpriteTextureLoader 按路径缓存，要把旧的清掉
        static void LoadIcons(string pFolder)
        {
            string root = Path.Combine(pFolder, "GameResources");
            if (!Directory.Exists(root)) return;
            foreach (string file in Directory.GetFiles(root, "*.png", SearchOption.AllDirectories))
            {
                Sprite[] sprites = SpriteLoadUtils.LoadSprites(file);
                if (sprites.Length == 0) continue;
                string path = file.Substring(root.Length + 1).Replace('\\', '/');
                path = path.Substring(0, path.Length - ".png".Length);
                ResourcesPatch.PatchResource(path, sprites[0]);
                SpriteTextureLoader._cached_sprites.Remove(path);
            }
        }
    }

    // 模组的代码不能把异常抛进原版：补丁先问 Guard.On，逻辑包在 try 里，出错交给 Guard.Fail 并照原版。
    // 30 秒内出错 5 次就停用，本次运行全部照原版
    public static class Guard
    {
        const int TripCount = 5;
        const double TripWindowSeconds = 30;

        static readonly Stopwatch _clock = Stopwatch.StartNew();
        static double _windowStart;
        static int _count;

        public static bool On { get; private set; } = true;

        public static void Fail(string pWhere, Exception pError)
        {
            Debug.LogError($"[星球] {pWhere} 出错：{pError}");
            if (!On) return;
            double now = _clock.Elapsed.TotalSeconds;
            if (now - _windowStart > TripWindowSeconds)
            {
                _windowStart = now;
                _count = 0;
            }
            if (++_count < TripCount) return;
            On = false;
            Debug.LogError($"[星球] {TripWindowSeconds} 秒内出错 {TripCount} 次，已停用");
        }
    }
}
