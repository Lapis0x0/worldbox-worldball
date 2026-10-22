using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace WorldboxGlobe
{
    // 地球仪视图：星球世界里，相机拉过原版的最远还能接着拉，地图逐渐卷成一个球；再往外是完整的球，拖动是转球。
    // 原版地图的各层（地形、国界、远景下的单位……）都是和地图一样大的一张贴图（MapLayer），
    // 这里用自己的网格贴上同样的贴图，用一台单独的相机画在原版画面之上。
    //
    // 投影：星球世界的地图横向是经度、纵向是纬度，直接按经纬度卷回球上。弯曲程度跟着缩放走：
    // 球的半径从无穷大缩到地图宽 / 2π，刚开始弯时网格和平面地图重合，换过去看不出来。
    // 球的朝向就是原版相机的位置：转球改的是相机位置，横向转一圈回到原处，拉近后落在转到的地方
    public static class Globe
    {
        const float BendStart = 1f;     // 从原版最远的几倍开始弯
        const float BendEnd = 2f;       // 到几倍弯成完整的球
        const float ZoomMax = 2.6f;     // 最远能拉到原版最远的几倍
        const float Fill = 0.6f;        // 刚弯成球时，球的直径占屏幕高的比例
        const float Shade = 0.45f;      // 球边缘比正面暗多少
        const int Cols = 192;
        const int Rows = 96;
        const int Segments = 96;
        const float Halo = 0.07f;       // 大气的厚度，以球的半径为 1
        static readonly Color Atmosphere = new Color(0.45f, 0.7f, 1f, 0.45f);
        const int StarSize = 512;       // 星空贴图的边长，一个像素对应屏幕上一个像素
        const float StarDrift = 0.15f;  // 横向转一圈，星空移动贴图的几分之一
        const int Layer = 31;           // 只有地球仪相机画这一层
        const string RootName = "globe_view";
        static readonly Color Space = new Color(0.02f, 0.03f, 0.06f);

        static GlobeView _view;

        // 地图在弯（或已经是球）：原版的点击、名牌、拖动交给地球仪
        public static bool Active => _view != null && _view.Bend > 0f;

        public static void Init()
        {
            // 热重载后旧版本建的对象还在，拆掉重建
            GameObject old = GameObject.Find(RootName);
            if (old != null) UnityEngine.Object.Destroy(old);
            var root = new GameObject(RootName);
            UnityEngine.Object.DontDestroyOnLoad(root);
            _view = root.AddComponent<GlobeView>();
        }

        public static void Stop()
        {
            if (_view != null) UnityEngine.Object.Destroy(_view.gameObject);
            _view = null;
        }

        // 原版最远：地图长边的 1.1 倍（MoveCamera.resetZoom）
        static float VanillaMax() => (int)(Mathf.Max(MapBox.width, MapBox.height) * 1.1f);

        public class GlobeView : MonoBehaviour
        {
            public float Bend;      // 0 平面，1 完整的球

            Camera _camera;
            Mesh _mesh;
            Vector3[] _verts;
            Color32[] _colors;
            readonly List<int> _tris = new List<int>();
            readonly List<(MapLayer layer, MeshRenderer renderer)> _layers = new List<(MapLayer, MeshRenderer)>();
            WorldLayer _terrain;
            MeshRenderer _halo;     // 大气：球外一圈淡淡的光
            Mesh _haloMesh;
            Vector3[] _haloVerts;
            Color32[] _haloColors;
            MeshRenderer _stars;    // 星空，贴在相机前面，随转球微微移动
            Mesh _starMesh;
            readonly List<(SpriteRenderer from, SpriteRenderer to)> _eras = new List<(SpriteRenderer, SpriteRenderer)>();
            Rect _map;              // 地图在世界坐标里的范围，取地形层的边界
            float _size;            // 地球仪相机的视野半高
            bool _dragging;
            Vector3 _lastMouse;

            void LateUpdate()
            {
                if (!Guard.On) { Hide(); return; }
                try { Step(); }
                catch (Exception e) { Hide(); Guard.Fail("地球仪", e); }
            }

            void OnDestroy()
            {
                foreach (var (_, renderer) in _layers)
                    if (renderer != null) Destroy(renderer.gameObject);
            }

            void Step()
            {
                MoveCamera move = MoveCamera.instance;
                if (World.world == null || move == null || MapBox.width <= 0 || !Planet.IsPlanet()) { Bend = 0f; Hide(); return; }
                float vanilla = VanillaMax();
                // 原版换窗口大小、换地图时会把上限改回去
                if (move.orthographic_size_max < vanilla * ZoomMax) move.orthographic_size_max = vanilla * ZoomMax;

                float zoom = move.main_camera.orthographicSize;
                float t = Mathf.Clamp01((zoom - vanilla * BendStart) / (vanilla * (BendEnd - BendStart)));
                Bend = t * t * (3f - 2f * t);
                if (Bend <= 0.001f) { Bend = 0f; Hide(); return; }

                if (_camera == null) Build();
                SyncLayers();
                if (_terrain == null) { Hide(); return; }

                // 球的半径：地图宽正好绕一圈时是 W / 2π，之前按 Bend 反比放大，Bend → 0 时趋于平面
                double edge = _map.width / 2.0;
                double radius = _map.width / (2.0 * Math.PI * Bend);

                // 视野：地图卷起来后横向变窄（球的直径只有地图宽的 1/π），得放大才看得清。放大倍数按「地图在屏幕上占多大」定：
                // 刚开始弯时和平面一样，弯成球时球的直径占屏幕高的 Fill，之后跟着缩放等比缩小
                double angle = edge / radius;
                double extent = angle < Math.PI / 2 ? radius * Math.Sin(angle) : radius;
                double boost = Mathf.Lerp(1f, (float)(Fill * vanilla * BendEnd / edge), Bend);
                _size = (float)(zoom * (extent / edge) / boost);

                Vector3 center = move.transform.position;
                if (!_camera.enabled) Tooltip.hideTooltip(null, true, string.Empty);
                _camera.backgroundColor = Color.Lerp(move.main_camera.backgroundColor, Space, Mathf.Clamp01(Bend * 3f));
                _camera.enabled = true;
                _camera.orthographicSize = _size;
                _camera.transform.position = new Vector3(center.x, center.y, -10f);
                _camera.farClipPlane = (float)(radius * 2.0 + 20.0);
                Shape(center.x, center.y, radius);
                UpdateStars(center);
                UpdateEras(zoom);
            }

            void Build()
            {
                var go = new GameObject("globe_camera");
                go.transform.SetParent(transform, false);
                _camera = go.AddComponent<Camera>();
                Camera main = MoveCamera.instance.main_camera;
                _camera.orthographic = true;
                _camera.clearFlags = CameraClearFlags.SolidColor;
                _camera.cullingMask = 1 << Layer;
                _camera.depth = main.depth + 0.5f;
                _camera.nearClipPlane = 0.1f;
                _camera.allowHDR = false;
                _camera.allowMSAA = false;
                foreach (Camera c in Camera.allCameras)
                    if (c != _camera) c.cullingMask &= ~(1 << Layer);

                _mesh = new Mesh { name = "globe_map" };
                _mesh.MarkDynamic();
                _verts = new Vector3[(Cols + 1) * (Rows + 1)];
                _colors = new Color32[_verts.Length];
                var uv = new Vector2[_verts.Length];
                for (int i = 0; i <= Rows; i++)
                for (int j = 0; j <= Cols; j++)
                    uv[i * (Cols + 1) + j] = new Vector2((float)j / Cols, (float)i / Rows);
                _mesh.vertices = _verts;
                _mesh.uv = uv;
                _mesh.colors32 = _colors;

                _haloMesh = new Mesh { name = "globe_halo" };
                _haloMesh.MarkDynamic();
                _haloVerts = new Vector3[Segments * 2];
                _haloColors = new Color32[Segments * 2];
                var haloTris = new int[Segments * 6];
                for (int k = 0; k < Segments; k++)
                {
                    int i0 = k * 2, o0 = k * 2 + 1, i1 = (k + 1) % Segments * 2, o1 = i1 + 1;
                    haloTris[k * 6] = i0; haloTris[k * 6 + 1] = i1; haloTris[k * 6 + 2] = o0;
                    haloTris[k * 6 + 3] = o0; haloTris[k * 6 + 4] = i1; haloTris[k * 6 + 5] = o1;
                }
                _haloMesh.vertices = _haloVerts;
                _haloMesh.colors32 = _haloColors;
                _haloMesh.uv = new Vector2[Segments * 2];
                _haloMesh.triangles = haloTris;

                _starMesh = new Mesh { name = "globe_stars" };
                _starMesh.MarkDynamic();
                _starMesh.vertices = new Vector3[4];
                _starMesh.uv = new Vector2[4];
                _starMesh.colors32 = new Color32[4];
                _starMesh.triangles = new[] { 0, 2, 1, 1, 2, 3 };
            }

            // 每个可见的地图层配一个渲染器，贴图、颜色、排序跟着原版那一层走
            void SyncLayers()
            {
                if (_layers.Count == 0 || _layers.Exists(l => l.layer == null))
                {
                    foreach (var (_, renderer) in _layers)
                        if (renderer != null) Destroy(renderer.gameObject);
                    if (_halo != null) Destroy(_halo.gameObject);
                    if (_stars != null) Destroy(_stars.gameObject);
                    foreach (var (_, to) in _eras)
                        if (to != null) Destroy(to.gameObject);
                    _eras.Clear();
                    _layers.Clear();
                    _terrain = null;
                    foreach (MapLayer layer in FindObjectsOfType<MapLayer>())
                    {
                        if (layer.sprRnd == null) continue;
                        _layers.Add((layer, NewRenderer("globe_" + layer.name, _mesh, layer.sprRnd.sharedMaterial)));
                        if (_terrain == null && layer is WorldLayer world) _terrain = world;
                    }
                    if (_terrain != null)
                    {
                        _halo = NewRenderer("globe_halo", _haloMesh, _terrain.sprRnd.sharedMaterial);
                        _halo.sharedMaterial.mainTexture = Texture2D.whiteTexture;
                        _halo.sharedMaterial.color = Color.white;
                        _stars = NewRenderer("globe_stars", _starMesh, _terrain.sprRnd.sharedMaterial);
                        _stars.transform.SetParent(_camera.transform, false);
                        _stars.sharedMaterial.mainTexture = StarTexture();
                        _stars.sharedMaterial.color = Color.white;
                    }
                    // 时代滤镜：原版贴在主相机前的几张全屏图，照样贴一份在地球仪相机前
                    if (WorldAgeEffects.instance != null)
                    {
                        foreach (SpriteRenderer from in WorldAgeEffects.instance.dict_effects.Values)
                        {
                            var go = new GameObject("globe_era_" + from.name) { layer = Layer };
                            go.transform.SetParent(_camera.transform, false);
                            SpriteRenderer to = go.AddComponent<SpriteRenderer>();
                            to.sharedMaterial = from.sharedMaterial;
                            _eras.Add((from, to));
                        }
                    }
                }
                foreach (var (layer, renderer) in _layers)
                {
                    SpriteRenderer sprite = layer.sprRnd;
                    bool on = layer.gameObject.activeInHierarchy && sprite.enabled && layer.texture != null;
                    renderer.enabled = on;
                    if (!on) continue;
                    Material material = renderer.sharedMaterial;
                    if (material.mainTexture != layer.texture) material.mainTexture = layer.texture;
                    // 原版夜里（黑暗纪元）会改材质的颜色，跟着它走
                    if (material.HasProperty("_Color")) material.color = Tint(sprite.sharedMaterial) * sprite.color;
                    renderer.sortingLayerID = sprite.sortingLayerID;
                    renderer.sortingOrder = sprite.sortingOrder;
                }
                if (_terrain == null) return;
                Bounds b = _terrain.sprRnd.bounds;
                _map = new Rect(b.min.x, b.min.y, b.size.x, b.size.y);
                _halo.enabled = true;
                _halo.sortingLayerID = _terrain.sprRnd.sortingLayerID;
                _halo.sortingOrder = _terrain.sprRnd.sortingOrder - 1;
                _stars.enabled = true;
                _stars.sortingLayerID = _terrain.sprRnd.sortingLayerID;
                _stars.sortingOrder = _terrain.sprRnd.sortingOrder - 2;
            }

            static Color Tint(Material pMaterial) =>
                pMaterial != null && pMaterial.HasProperty("_Color") ? pMaterial.color : Color.white;

            MeshRenderer NewRenderer(string pName, Mesh pMesh, Material pMaterial)
            {
                var go = new GameObject(pName) { layer = Layer };
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = pMesh;
                MeshRenderer renderer = go.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = new Material(pMaterial);
                return renderer;
            }

            // 横向按经度、纵向按纬度把网格卷到半径 R 的球上，再绕横轴转，让相机正对的那一点朝向屏幕
            void Shape(float pCx, float pCy, double pRadius)
            {
                double w = _map.width, h = _map.height;
                double midY = _map.y + h / 2.0;
                double tilt = (pCy - midY) / pRadius;
                double cosT = Math.Cos(tilt), sinT = Math.Sin(tilt);

                for (int i = 0; i <= Rows; i++)
                {
                    double y = _map.y + h * i / Rows;
                    double lat = (y - midY) / pRadius;
                    double cosLat = Math.Cos(lat), sinLat = Math.Sin(lat);
                    for (int j = 0; j <= Cols; j++)
                    {
                        double x = _map.x + w * j / Cols;
                        double lon = (x - pCx) / pRadius;
                        double px = pRadius * cosLat * Math.Sin(lon);
                        double py = pRadius * sinLat;
                        double pz = -pRadius * cosLat * Math.Cos(lon);
                        double ry = py * cosT + pz * sinT;
                        double rz = -py * sinT + pz * cosT;
                        int n = i * (Cols + 1) + j;
                        // z 越小越靠近相机：正面在 0 附近，背面最远到 2R
                        _verts[n] = new Vector3((float)(pCx + px), (float)(pCy + ry), (float)(rz + pRadius));
                        _colors[n] = Lit(Color.white, -rz / pRadius);
                    }
                }

                // 球背面的格子不画（材质不写深度，背面会透过来）
                _tris.Clear();
                for (int i = 0; i < Rows; i++)
                for (int j = 0; j < Cols; j++)
                {
                    int a = i * (Cols + 1) + j, b = a + 1, c = a + Cols + 1, d = c + 1;
                    float z = _verts[a].z + _verts[b].z + _verts[c].z + _verts[d].z;
                    if (z * 0.25f > pRadius) continue;
                    _tris.Add(a); _tris.Add(c); _tris.Add(b);
                    _tris.Add(b); _tris.Add(c); _tris.Add(d);
                }
                _mesh.vertices = _verts;
                _mesh.colors32 = _colors;
                _mesh.SetTriangles(_tris, 0, false);
                var bounds = new Bounds(new Vector3(pCx, pCy, (float)pRadius), Vector3.one * (float)(pRadius * 4 + w + h));
                _mesh.bounds = bounds;

                // 大气：从球的边缘往外淡出，弯成球以后才有
                for (int k = 0; k < Segments; k++)
                {
                    double a = 2 * Math.PI * k / Segments;
                    double ca = Math.Cos(a), sa = Math.Sin(a);
                    _haloVerts[k * 2] = new Vector3((float)(pCx + pRadius * 0.98 * ca), (float)(pCy + pRadius * 0.98 * sa), (float)pRadius);
                    _haloVerts[k * 2 + 1] = new Vector3((float)(pCx + pRadius * (1 + Halo) * ca), (float)(pCy + pRadius * (1 + Halo) * sa), (float)pRadius);
                }
                Color inner = Atmosphere * Tint(_terrain.sprRnd.sharedMaterial);
                inner.a = Atmosphere.a * Bend * Bend;
                for (int k = 0; k < Segments; k++)
                {
                    _haloColors[k * 2] = inner;
                    _haloColors[k * 2 + 1] = new Color(inner.r, inner.g, inner.b, 0f);
                }
                _haloMesh.vertices = _haloVerts;
                _haloMesh.colors32 = _haloColors;
                _haloMesh.bounds = bounds;
            }

            // 星空铺满地球仪相机的视野，每个星点在屏幕上大小固定；转球时跟着相机位置微微移动
            void UpdateStars(Vector3 pCenter)
            {
                float h = _size * 2f, w = h * _camera.aspect;
                float pw = _camera.pixelWidth / (float)StarSize, ph = _camera.pixelHeight / (float)StarSize;
                float ox = pCenter.x / Mathf.Max(1f, _map.width) * StarDrift, oy = pCenter.y / Mathf.Max(1f, _map.height) * StarDrift;
                _starMesh.vertices = new[]
                {
                    new Vector3(-w / 2, -h / 2, 5f), new Vector3(w / 2, -h / 2, 5f),
                    new Vector3(-w / 2, h / 2, 5f), new Vector3(w / 2, h / 2, 5f)
                };
                _starMesh.uv = new[] { new Vector2(ox, oy), new Vector2(ox + pw, oy), new Vector2(ox, oy + ph), new Vector2(ox + pw, oy + ph) };
                var c = new Color32(255, 255, 255, (byte)(255 * Mathf.Clamp01(Bend * 2f - 0.5f)));
                _starMesh.colors32 = new[] { c, c, c, c };
                _starMesh.bounds = new Bounds(Vector3.zero, new Vector3(w * 2, h * 2, 20f));
            }

            // 时代滤镜：大小按两台相机的视野换算，颜色和开关照原版
            void UpdateEras(float pZoom)
            {
                Transform main = MoveCamera.instance.main_camera.transform;
                float k = _size / Mathf.Max(0.01f, pZoom);
                foreach (var (from, to) in _eras)
                {
                    if (from == null || to == null) continue;
                    bool on = from.enabled && from.gameObject.activeInHierarchy && from.sprite != null;
                    to.enabled = on;
                    if (!on) continue;
                    to.sprite = from.sprite;
                    to.color = from.color;
                    to.sortingLayerID = from.sortingLayerID;
                    to.sortingOrder = from.sortingOrder;
                    Vector3 offset = from.transform.position - main.position;
                    to.transform.localPosition = new Vector3(offset.x * k, offset.y * k, 1f);
                    to.transform.localRotation = from.transform.rotation;
                    to.transform.localScale = from.transform.lossyScale * k;
                }
            }

            // 随机星点：大多是暗淡的白点，少数亮星带一点蓝或黄
            static Texture2D StarTexture()
            {
                var texture = new Texture2D(StarSize, StarSize, TextureFormat.RGBA32, false)
                {
                    filterMode = FilterMode.Point,
                    wrapMode = TextureWrapMode.Repeat
                };
                var pixels = new Color32[StarSize * StarSize];
                var random = new System.Random(20261022);
                for (int n = 0; n < StarSize * StarSize / 220; n++)
                {
                    int x = random.Next(StarSize), y = random.Next(StarSize);
                    double roll = random.NextDouble();
                    byte bright = (byte)(60 + 195 * Math.Pow(random.NextDouble(), 3));
                    Color32 c = roll < 0.08 ? new Color32((byte)(bright * 0.75), (byte)(bright * 0.85), bright, 255)
                        : roll < 0.14 ? new Color32(bright, (byte)(bright * 0.9), (byte)(bright * 0.7), 255)
                        : new Color32(bright, bright, bright, 255);
                    pixels[y * StarSize + x] = c;
                    // 少数亮星画成小十字
                    if (bright > 200 && x > 0 && y > 0 && x < StarSize - 1 && y < StarSize - 1)
                    {
                        var dim = new Color32((byte)(c.r / 3), (byte)(c.g / 3), (byte)(c.b / 3), 255);
                        pixels[y * StarSize + x - 1] = dim;
                        pixels[y * StarSize + x + 1] = dim;
                        pixels[(y - 1) * StarSize + x] = dim;
                        pixels[(y + 1) * StarSize + x] = dim;
                    }
                }
                texture.SetPixels32(pixels);
                texture.Apply();
                return texture;
            }

            // 朝向相机的程度 pFacing（正面 1，边缘 0）越小越暗；还没弯起来时不加明暗
            Color32 Lit(Color pColor, double pFacing)
            {
                float f = Mathf.Clamp01((float)pFacing);
                float light = 1f - Shade * Bend * (1f - Mathf.Sqrt(f));
                return new Color(pColor.r * light, pColor.g * light, pColor.b * light, pColor.a);
            }

            void Hide()
            {
                _dragging = false;
                if (_camera != null && _camera.enabled) _camera.enabled = false;
                foreach (var (_, renderer) in _layers)
                    if (renderer != null && renderer.enabled) renderer.enabled = false;
                if (_halo != null && _halo.enabled) _halo.enabled = false;
                if (_stars != null && _stars.enabled) _stars.enabled = false;
                foreach (var (_, to) in _eras)
                    if (to != null && to.enabled) to.enabled = false;
            }

            // 拖动转球：鼠标在球的正面移动多少，球面就跟着转多少。横向首尾相接，纵向到两极为止
            public void Drag(MoveCamera pMove)
            {
                pMove._move_velocity = Vector2.zero;
                bool held = Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
                if (!held) { _dragging = false; return; }
                if (!_dragging)
                {
                    if (World.world.isOverUI()) return;
                    _dragging = true;
                    _lastMouse = Input.mousePosition;
                    return;
                }
                Vector3 delta = Input.mousePosition - _lastMouse;
                _lastMouse = Input.mousePosition;
                float unit = _size * 2f / Mathf.Max(1, _camera.pixelHeight);
                Vector3 pos = pMove.transform.position;
                float x = _map.x + Mathf.Repeat(pos.x - delta.x * unit - _map.x, _map.width);
                float y = Mathf.Clamp(pos.y - delta.y * unit, _map.yMin, _map.yMax);
                pMove.transform.position = new Vector3(x, y, pos.z);
            }
        }

        // 地球仪显示时，拖动交给地球仪转球
        [HarmonyPatch(typeof(MoveCamera), "updateMouseCameraDrag")]
        static class Patch_Drag
        {
            static bool Prefix(MoveCamera __instance)
            {
                if (!Active || !Guard.On) return true;
                try { _view.Drag(__instance); return false; }
                catch (Exception e) { Guard.Fail("地球仪·拖动", e); return true; }
            }
        }

        // 地球仪显示时只响应快捷键（滚轮缩放在里面），不放神力、不选单位：鼠标下面对应的不是平面地图上的位置
        [HarmonyPatch(typeof(PlayerControl), nameof(PlayerControl.updateControls))]
        static class Patch_Controls
        {
            static bool Prefix()
            {
                if (!Active || !Guard.On) return true;
                try
                {
                    AssetManager.hotkey_library.checkHotKeyActions();
                    return false;
                }
                catch (Exception e) { Guard.Fail("地球仪·操作", e); return true; }
            }
        }

        // 地球仪显示时不弹鼠标下面的单位、国家提示，同样是按平面地图的位置找的
        [HarmonyPatch(typeof(CursorTooltipHelper), nameof(CursorTooltipHelper.update))]
        static class Patch_CursorTooltip
        {
            static bool Prefix() => !Active;
        }

        // 地球仪显示时藏起地图上的名牌，名牌按平面地图的位置画
        [HarmonyPatch(typeof(NameplateManager), nameof(NameplateManager.update))]
        static class Patch_Nameplates
        {
            static bool Prefix(NameplateManager __instance)
            {
                if (!Active || !Guard.On) return true;
                try
                {
                    if (__instance.gameObject.activeSelf) __instance.gameObject.SetActive(false);
                    return false;
                }
                catch (Exception e) { Guard.Fail("地球仪·名牌", e); return true; }
            }
        }
    }
}
